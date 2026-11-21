//! GTA IV's clips on a Counter-Strike player: the player's skeleton (player.xft) and his clips from anim.img, read
//! from your own game the way the Playroom's hero reads them (nothing of the game is in here), each pose turned onto
//! the CS model's bones. A C interface for the CS Delirium plugin (delirium_anim.dll), 32-bit like the game.
//!
//! The target pose comes out in CS model space with the character facing +X, Z up, the ground at the CS model's own
//! (its reference pose's) ground, in GoldSrc units; the host puts it where the player stands, turned his way.

use bevy_math::{Quat, Vec3};
pub mod mdl;
use delirium_data::{anim, archive_key::ArchiveKey, img, model, resource, rpf, skeleton};
use std::collections::HashMap;
use std::ffi::{CStr, c_char};
use std::io::{Read, Seek, SeekFrom};
use std::path::{Path, PathBuf};
use std::sync::Mutex;

type Frame = (Vec3, Quat);

struct Clip {
    anim: anim::Animation,
    // (Sandbox) a GTA V clip from Grand Theft Gmod's model: in place, its speed (m/s) from the addon's clip data
    speed: Option<f32>,
    v: Option<Meta>,
    map: Vec<Option<usize>>, // track -> bone
    // where the clip stands: its hips' middle over the cycle (flat) and its lowest foot, in GTA model space. GTA IV's
    // clips carry the hips on the root bone, some from the ground, some from the hips, some off to a side (the
    // crouches): the pose is put back in place on the ground by these.
    mid: Vec3,
    floor: f32,
    // the ground at the clip's end (its lowest foot there, the mover's travel on): for clips that move the body
    // over the ground with the mover (a get-up rises from lying by it)
    floor_end: f32,
}

struct Target {
    parents: Vec<Option<usize>>,
    local: Vec<Frame>,            // the CS reference pose, each bone in its parent's frame
    gta: Vec<Option<usize>>,      // CS bone -> GTA bone (the ones that move with the clip)
    corr: Vec<Quat>,              // CS bone's frame in its GTA bone's (both at rest, the CS one lined up)
    rfix: Quat,                   // CS model space -> facing +X
    r0: Quat,                     // GTA model space -> facing +X
    scale: f32,                   // GTA metres -> CS units
    pelvis: usize,                // CS pelvis
    gta_pelvis: usize,
    gta_floor: f32,
    cs_floor: f32,
}

struct Anim {
    // (Sandbox) GTA V's clips out of Grand Theft Gmod's model (Anim::open_mdl), not GTA IV's anim.img
    mdl: Option<mdl::Mdl>,
    // (Sandbox) the addon's other models (male_guns.mdl: the guns' aims and shots), the same skeleton
    extra: Vec<mdl::Mdl>,
    meta: HashMap<String, Meta>, // sequence -> its clip data (the addon's)
    pc: bool,
    table: Vec<img::Entry>,
    file: std::fs::File,
    parents: Vec<Option<usize>>,
    bind: Vec<Frame>,
    names: Vec<String>,
    ids: Vec<u16>,
    dicts: HashMap<String, Vec<anim::Animation>>,
    clips: Vec<Clip>,
    target: Option<Target>,
}

static STATE: Mutex<Option<Anim>> = Mutex::new(None);

// (Sandbox) The start's cache: "DAC1", the program's size and time, the key (32), the numbers (kv::export), the
// player's skeleton (name, parent, id, translation, rotation each bone).
fn cache_stamp(exe: &Path) -> Option<(u64, u64)> {
    let m = std::fs::metadata(exe).ok()?;
    let t = m.modified().ok()?.duration_since(std::time::UNIX_EPOCH).ok()?.as_secs();
    Some((m.len(), t))
}

fn write_cache(path: &Path, st: (u64, u64), key: &[u8; 32], bones: &[skeleton::Bone]) {
    let Some(kv) = delirium_data::kv::export() else { return };
    let mut v = b"DAC1".to_vec();
    v.extend_from_slice(&st.0.to_le_bytes());
    v.extend_from_slice(&st.1.to_le_bytes());
    v.extend_from_slice(key);
    v.extend_from_slice(&(kv.len() as u32).to_le_bytes());
    v.extend_from_slice(&kv);
    v.extend_from_slice(&(bones.len() as u32).to_le_bytes());
    for b in bones {
        v.extend_from_slice(&(b.name.len() as u16).to_le_bytes());
        v.extend_from_slice(b.name.as_bytes());
        v.extend_from_slice(&b.parent.map_or(-1i32, |p| p as i32).to_le_bytes());
        v.extend_from_slice(&b.id.to_le_bytes());
        for f in b.translation.iter().chain(b.rotation.iter()) { v.extend_from_slice(&f.to_le_bytes()); }
    }
    let _ = std::fs::write(path, v); // a read-only folder: no cache, no harm
}

fn read_cache(path: &Path, st: (u64, u64)) -> Option<(ArchiveKey, Vec<skeleton::Bone>)> {
    let b = std::fs::read(path).ok()?;
    let mut at = 0usize;
    let mut take = |n: usize| -> Option<&[u8]> { let s = b.get(at..at + n)?; at += n; Some(s) };
    if take(4)? != b"DAC1" { return None }
    let u64_ = |s: &[u8]| u64::from_le_bytes(s.try_into().unwrap());
    if u64_(take(8)?) != st.0 || u64_(take(8)?) != st.1 { return None }
    let key: [u8; 32] = take(32)?.try_into().ok()?;
    let n = u32::from_le_bytes(take(4)?.try_into().ok()?) as usize;
    let kv = take(n)?.to_vec();
    let count = u32::from_le_bytes(take(4)?.try_into().ok()?) as usize;
    let mut bones = Vec::with_capacity(count);
    for _ in 0..count {
        let len = u16::from_le_bytes(take(2)?.try_into().ok()?) as usize;
        let name = String::from_utf8(take(len)?.to_vec()).ok()?;
        let parent = i32::from_le_bytes(take(4)?.try_into().ok()?);
        let id = u16::from_le_bytes(take(2)?.try_into().ok()?);
        let mut f = [0f32; 7];
        for x in f.iter_mut() { *x = f32::from_le_bytes(take(4)?.try_into().ok()?); }
        bones.push(skeleton::Bone { name, parent: (parent >= 0).then_some(parent as usize), id, translation: [f[0], f[1], f[2]],
                                    rotation: [f[3], f[4], f[5], f[6]], flags: 0, rot_min: [0.0; 3], rot_max: [0.0; 3], block: [0.0; 4] });
    }
    if !delirium_data::kv::import(&kv) { return None }
    Some((ArchiveKey::from_bytes(&key), bones))
}
/// The archives' key found when the game was opened (the bikes reuse it: no second pass over default.xex).
pub static KEY: std::sync::OnceLock<ArchiveKey> = std::sync::OnceLock::new();
static ERR: Mutex<String> = Mutex::new(String::new());

fn fail(e: impl Into<String>) -> i32 {
    *ERR.lock().unwrap_or_else(|e| e.into_inner()) = e.into();
    -1
}

unsafe fn text<'a>(p: *const c_char) -> Option<&'a str> {
    if p.is_null() { None } else { unsafe { CStr::from_ptr(p) }.to_str().ok() }
}

unsafe fn put(s: &str, buf: *mut c_char, len: i32) -> i32 {
    if !buf.is_null() && len > 0 {
        let n = s.len().min(len as usize - 1);
        unsafe {
            std::ptr::copy_nonoverlapping(s.as_ptr(), buf as *mut u8, n);
            *buf.add(n) = 0;
        }
    }
    s.len() as i32
}

/// The bone's name as a key: lower case letters and digits, GTA's "Char" and Biped's "Bip01" off the front
/// ("Char_L_UpperArm", "Bip01 L UpperArm" -> "lupperarm").
fn key(name: &str) -> String {
    let k: String = name.chars().filter(|c| c.is_ascii_alphanumeric()).map(|c| c.to_ascii_lowercase()).collect();
    for p in ["valvebipedbip01", "char", "bip01", "skel"] {
        if let Some(r) = k.strip_prefix(p) {
            if !r.is_empty() {
                // (Sandbox) Grand Theft Gmod's ValveBiped skeleton of GTA V's: Spine..Spine2, Spine4 (the top), Neck1, Head1
                return match r { "spine4" => "spine3", "neck1" => "neck", "head1" => "head", _ => r }.to_string();
            }
        }
    }
    k
}

// The bones the clips move (the rest, fingers and helpers, keep the CS model's own animation), and which way each
// limb bone points (to the next one down the limb): how the two skeletons' rest poses are lined up.
const MOVING: [&str; 23] = [
    "pelvis", "spine", "spine1", "spine2", "spine3", "neck", "head", "lclavicle", "lupperarm", "lforearm", "lhand",
    "rclavicle", "rupperarm", "rforearm", "rhand", "lthigh", "lcalf", "lfoot", "ltoe0", "rthigh", "rcalf", "rfoot", "rtoe0",
];
const LIMB: [(&str, &str); 14] = [
    ("lclavicle", "lupperarm"), ("lupperarm", "lforearm"), ("lforearm", "lhand"), ("rclavicle", "rupperarm"),
    ("rupperarm", "rforearm"), ("rforearm", "rhand"), ("lthigh", "lcalf"), ("lcalf", "lfoot"), ("lfoot", "ltoe0"),
    ("rthigh", "rcalf"), ("rcalf", "rfoot"), ("rfoot", "rtoe0"), ("neck", "head"), ("spine3", "neck"),
];

fn plat(pc: bool, s: &str) -> String {
    if !pc {
        return s.into();
    }
    [("xbox360", "pc"), (".xrt", ".art"), (".xft", ".wft"), (".xad", ".wad"), (".xtd", ".wtd"), (".xdr", ".wdr")]
        .iter()
        .fold(s.into(), |s, (a, b)| s.replace(a, b))
}

fn img_read(pc: bool, table: &[img::Entry], f: &mut std::fs::File, name: &str) -> Result<resource::Resource, String> {
    let name = &plat(pc, name);
    let e = table.iter().find(|e| e.name.eq_ignore_ascii_case(name)).ok_or(format!("{name} not found"))?;
    let mut raw = vec![];
    f.seek(SeekFrom::Start(e.offset)).map_err(|e| e.to_string())?;
    f.take(e.used_blocks as u64 * delirium_data::kv!(722 int)).read_to_end(&mut raw).map_err(|e| e.to_string())?;
    resource::parse(&raw)
}

fn world(parents: &[Option<usize>], local: &[Frame]) -> Vec<Frame> {
    let mut out: Vec<Frame> = Vec::with_capacity(local.len());
    for (i, &(t, r)) in local.iter().enumerate() {
        out.push(match parents[i] {
            Some(p) => (out[p].0 + out[p].1 * t, (out[p].1 * r).normalize()),
            None => (t, r),
        });
    }
    out
}

/// Which way a skeleton at rest faces (flat, unit): its left (left thigh's from the right one's) crossed with up.
fn facing(w: &[Frame], keys: &[String]) -> Option<Vec3> {
    let at = |k: &str| keys.iter().position(|x| x == k).map(|i| w[i].0);
    let left = (at("lthigh")? - at("rthigh")?).with_z(0.0);
    let f = left.cross(Vec3::Z).normalize_or_zero();
    (f != Vec3::ZERO).then_some(f)
}

fn quat(v: &[f32]) -> Quat {
    Quat::from_xyzw(v[0], v[1], v[2], v[delirium_data::kv!(0 int)]).normalize()
}

impl Anim {
    fn open(dir: &Path) -> Result<Anim, String> {
        let pc = dir.join("GTAIV.exe").is_file();
        let exe = if pc { dir.join("GTAIV.exe") } else { dir.join("default.xex") };
        // (Sandbox) what the last start worked out of the program (its numbers, the archives' key, the player's
        // skeleton), kept beside the game: unpacking default.xex and trying keys through it took seconds a start
        let stamp = cache_stamp(&exe);
        let cache = dir.join("delirium-anim.cache");
        if let Some((key, bones)) = stamp.and_then(|st| read_cache(&cache, st)) {
            delirium_data::LE.store(pc, std::sync::atomic::Ordering::Relaxed);
            let _ = KEY.set(key.clone());
            return Self::finish(dir, pc, key, bones);
        }
        let prog = delirium_data::xex::program(&exe)?;
        if !delirium_data::kv::unlock(&prog) {
            return Err(format!("{}: not a version the numbers are read from (the Steam GTAIV.exe or the disc's default.xex)", exe.display()));
        }
        delirium_data::LE.store(pc, std::sync::atomic::Ordering::Relaxed);
        let rd = |p: &str| std::fs::read(dir.join(p)).map_err(|e| format!("{p}: {e}"));
        // the archives' key, out of the program, tried on an archive's head; the player's archive
        let found = std::cell::Cell::new(None::<[u8; 32]>);
        let find = |head: &[u8]| ArchiveKey::find_by_trial(&prog, &head[..0x810.min(head.len())]).map(|(k, b)| { found.set(Some(b)); k }).ok_or("no archive key in the game's program");
        let (peds, key) = if pc {
            let peds = rd("pc/models/cdimages/playerped.rpf")?;
            let key = find(&peds)?;
            (peds, key)
        } else {
            let platform = rd("xbox360.rpf")?;
            let key = find(&platform)?;
            let toc = rpf::read_toc(&platform, &key)?;
            let nested = toc.iter().find(|e| e.path.eq_ignore_ascii_case("models/cdimages/playerped.rpf")).ok_or("playerped.rpf not found")?;
            (rpf::read(&platform, nested)?, key)
        };
        let _ = KEY.set(key.clone());
        let key_bytes = found.get();
        let inner = rpf::read_toc(&peds, &key)?;
        let e = inner.iter().find(|e| e.path.eq_ignore_ascii_case(&plat(pc, "player.xft"))).ok_or("player.xft not in playerped.rpf")?;
        let frag = resource::parse(&rpf::read(&peds, e)?)?;
        let bones = skeleton::read(&frag, model::fragment_root(&frag)?)?;
        if let (Some(st), Some(kb)) = (stamp, key_bytes) {
            write_cache(&cache, st, &kb, &bones);
        }
        Self::finish(dir, pc, key, bones)
    }

    fn finish(dir: &Path, pc: bool, key: ArchiveKey, bones: Vec<skeleton::Bone>) -> Result<Anim, String> {
        let path = PathBuf::from(plat(pc, &dir.join("xbox360/anim/anim.img").to_string_lossy()));
        let mut file = std::fs::File::open(&path).map_err(|e| format!("anim.img: {e}"))?;
        let mut head = vec![];
        (&mut file).take(delirium_data::kv!(1 int) << delirium_data::kv!(127 int)).read_to_end(&mut head).map_err(|e| e.to_string())?;
        let table = img::read_table(&head, Some(&key))?;
        Ok(Anim {
            extra: vec![],
            mdl: None,
            meta: HashMap::new(),
            pc,
            table,
            file,
            parents: bones.iter().map(|b| b.parent).collect(),
            bind: bones.iter().map(|b| (Vec3::from(b.translation), Quat::from_array(b.rotation).normalize())).collect(),
            names: bones.iter().map(|b| b.name.clone()).collect(),
            ids: bones.iter().map(|b| b.id).collect(),
            dicts: HashMap::new(),
            clips: vec![],
            target: None,
        })
    }

    fn dict(&mut self, dict: &str) -> Result<&Vec<anim::Animation>, String> {
        let k = dict.to_ascii_lowercase();
        if !self.dicts.contains_key(&k) {
            let r = img_read(self.pc, &self.table, &mut self.file, &format!("{k}.xad"))?;
            let list = anim::read_dictionary(&r)?;
            self.dicts.insert(k.clone(), list);
        }
        Ok(&self.dicts[&k])
    }

    fn clip(&mut self, dict: &str, name: &str) -> Result<usize, String> {
        let mut speed = None;
        let mut vmeta = None;
        let a = if let Some(m) = &self.mdl {
            let seq = name.to_ascii_lowercase();
            speed = self.meta.get(&seq).map(|v| v.speed);
            vmeta = self.meta.get(&seq).cloned();
            if let Some(&ai) = m.seqs.get(&seq) {
                mdl_animation(m, ai)?
            } else {
                // another of the addon's models: its bones put onto the first one's by name
                let (x, ai) = self.extra.iter().find_map(|x| x.seqs.get(&seq).map(|&ai| (x, ai))).ok_or(format!("no sequence {name} in the models"))?;
                let mut a = mdl_animation(x, ai)?;
                a.tracks.retain_mut(|t| match m.bones.iter().position(|b| b.name.eq_ignore_ascii_case(&x.bones[t.bone as usize].name)) {
                    Some(b) => { t.bone = b as u16; true }
                    None => false,
                });
                a
            }
        } else {
            self.dict(dict)?.iter().find(|a| a.name.eq_ignore_ascii_case(name)).cloned().ok_or(format!("no {name} in {dict}"))?
        };
        let map = a.tracks.iter().map(|t| self.ids.iter().position(|&id| id == t.bone).filter(|&b| b != 0)).collect();
        self.clips.push(Clip { anim: a, map, speed: None, v: None, mid: Vec3::ZERO, floor: 0.0, floor_end: 0.0 });
        let c = self.clips.len() - 1;
        self.clips[c].speed = speed;
        self.clips[c].v = vmeta;
        let keys: Vec<String> = self.names.iter().map(|n| key(n)).collect();
        let pelvis = keys.iter().position(|k| k == "pelvis").unwrap_or(0);
        let feet: Vec<usize> = keys.iter().enumerate().filter(|(_, k)| ["lfoot", "rfoot", "ltoe0", "rtoe0"].contains(&k.as_str())).map(|(i, _)| i).collect();
        let (mut mid, mut floor) = (Vec3::ZERO, f32::MAX);
        const N: usize = 12;
        for k in 0..N {
            let w = world(&self.parents, &self.sample_rooted(c, k as f32 / (N - 1) as f32));
            mid += w[pelvis].0.with_z(0.0) / N as f32;
            floor = feet.iter().map(|&f| w[f].0.z).fold(floor, f32::min);
        }
        (self.clips[c].mid, self.clips[c].floor) = (mid, if floor == f32::MAX { 0.0 } else { floor });
        let w = world(&self.parents, &self.sample_rooted(c, 1.0));
        let end = feet.iter().map(|&f| w[f].0.z).fold(f32::MAX, f32::min);
        self.clips[c].floor_end = if end == f32::MAX { 0.0 } else { end + self.mover(c, 1.0).z };
        Ok(c)
    }

    // a rotation track's value: GTA IV's order (its w where the game's numbers say), or x y z w (GTA V's, Source's)
    fn q(&self, v: &[f32]) -> Quat {
        if self.mdl.is_some() { Quat::from_xyzw(v[0], v[1], v[2], v[3]).normalize() } else { quat(v) }
    }

    /// Clip `c` at `phase` (0..1): the GTA skeleton's local pose, its root where it stands at rest (in place).
    fn sample(&self, c: usize, phase: f32) -> Vec<Frame> {
        let Clip { anim: a, map, .. } = &self.clips[c];
        let mut out = self.bind.clone();
        let x = phase.clamp(0.0, 1.0) * (a.frames.max(1) - 1) as f32;
        let (i, f) = (x.floor() as usize, x.fract());
        for (t, bone) in a.tracks.iter().zip(map) {
            let Some(&b) = bone.as_ref() else { continue };
            let (p, q) = (t.sample(i), t.sample(i + 1));
            match t.kind {
                anim::BONE_ROTATION => out[b].1 = self.q(&p).slerp(self.q(&q), f),
                anim::BONE_TRANSLATION => out[b].0 = Vec3::from_slice(&p).lerp(Vec3::from_slice(&q), f),
                _ => {}
            }
        }
        out
    }

    /// As `sample`, with the root bone's own tracks (the hips' sway, bob and turn are on it in GTA IV's clips; a
    /// root that doesn't move keeps its rest frame)
    fn sample_rooted(&self, c: usize, phase: f32) -> Vec<Frame> {
        let mut out = self.sample(c, phase);
        let a = &self.clips[c].anim;
        let x = phase.clamp(0.0, 1.0) * (a.frames.max(1) - 1) as f32;
        let (i, f) = (x.floor() as usize, x.fract());
        let roots: Vec<&anim::Track> = a.tracks.iter().filter(|t| t.bone == 0).collect();
        let still = |t: &anim::Track| t.kind == anim::BONE_ROTATION && (0..a.frames).all(|k| { let v = t.sample(k); v[0].abs() < 1e-3 && v[1].abs() < 1e-3 && v[2].abs() < 1e-3 });
        if !roots.iter().any(|t| still(t)) {
            for t in roots {
                match t.kind {
                    anim::BONE_ROTATION => out[0].1 = self.q(&t.sample(i)).slerp(self.q(&t.sample(i + 1)), f),
                    anim::BONE_TRANSLATION => out[0].0 = Vec3::from_slice(&t.sample(i)).lerp(Vec3::from_slice(&t.sample(i + 1)), f),
                    _ => {}
                }
            }
        }
        out
    }

    /// The mover's travel at `phase` (GTA model space; zero for a clip without one)
    fn mover(&self, c: usize, phase: f32) -> Vec3 {
        let a = &self.clips[c].anim;
        let x = phase.clamp(0.0, 1.0) * (a.frames.max(1) - 1) as f32;
        let (i, f) = (x.floor() as usize, x.fract());
        a.tracks.iter().find(|t| t.bone == 0 && t.kind == anim::MOVER_TRANSLATION)
            .map_or(Vec3::ZERO, |t| Vec3::from_slice(&t.sample(i)).lerp(Vec3::from_slice(&t.sample(i + 1)), f))
    }

    /// Where in its cycle the clip has the left foot furthest ahead of the right along the way it goes (0..1; 0
    /// for a clip that stays put): walk, run and sprint (and each strafe's) cycles lined up by it step together
    fn sync(&self, c: usize) -> f32 {
        let a = &self.clips[c].anim;
        let mut way = self.travel(c).with_z(0.0);
        if self.clips[c].speed.is_some_and(|v| v > 0.2) && way.length() / a.duration.max(0.01) < 0.2 {
            // (Sandbox) in place (GTA V's, the addon's): the way its clip data has it go (a strafe's sideways), or
            // the way the body faces
            let keys: Vec<String> = self.names.iter().map(|n| key(n)).collect();
            let data = self.clips[c].v.as_ref().and_then(|m| m.curve.len().checked_sub(3).map(|k| Vec3::new(m.curve[k], m.curve[k + 1], 0.0)));
            way = match data {
                Some(d) if d.length() > 0.05 => d,
                _ => facing(&world(&self.parents, &self.bind), &keys).unwrap_or(Vec3::X).with_z(0.0),
            };
        } else if way.length() / a.duration.max(0.01) < 0.2 {
            return 0.0;
        }
        let way = way.normalize();
        let keys: Vec<String> = self.names.iter().map(|n| key(n)).collect();
        let (Some(l), Some(r)) = (keys.iter().position(|k| k == "lfoot"), keys.iter().position(|k| k == "rfoot")) else { return 0.0 };
        const N: usize = 64;
        let mut best = (f32::MIN, 0.0);
        for k in 0..N {
            let ph = k as f32 / N as f32;
            let w = world(&self.parents, &self.sample_rooted(c, ph));
            let ahead = (w[l].0 - w[r].0).dot(way);
            if ahead > best.0 {
                best = (ahead, ph);
            }
        }
        best.1
    }

    /// How far the clip carries him over its length (its root and mover tracks), GTA model space
    fn travel(&self, c: usize) -> Vec3 {
        let a = &self.clips[c].anim;
        let at = |phase: f32| {
            let x = phase * (a.frames.max(1) - 1) as f32;
            let (i, f) = (x.floor() as usize, x.fract());
            let mut p = Vec3::ZERO;
            for t in a.tracks.iter().filter(|t| t.bone == 0) {
                if t.kind == anim::BONE_TRANSLATION || t.kind == anim::MOVER_TRANSLATION {
                    p += Vec3::from_slice(&t.sample(i)).lerp(Vec3::from_slice(&t.sample(i + 1)), f);
                }
            }
            p
        };
        at(1.0) - at(0.0)
    }

    /// How far the clip carries him (its root and mover tracks), flat, m/s
    fn speed(&self, c: usize) -> f32 {
        if let Some(v) = self.clips[c].speed { return v; }
        let a = &self.clips[c].anim;
        let at = |phase: f32| {
            let x = phase * (a.frames.max(1) - 1) as f32;
            let (i, f) = (x.floor() as usize, x.fract());
            let mut p = Vec3::ZERO;
            for t in a.tracks.iter().filter(|t| t.bone == 0) {
                if t.kind == anim::BONE_TRANSLATION || t.kind == anim::MOVER_TRANSLATION {
                    p += Vec3::from_slice(&t.sample(i)).lerp(Vec3::from_slice(&t.sample(i + 1)), f);
                }
            }
            p
        };
        (at(1.0) - at(0.0)).with_z(0.0).length() / a.duration.max(0.01)
    }

    fn target(&mut self, names: &[String], parents: &[Option<usize>], local: Vec<Frame>) -> Result<String, String> {
        let gk: Vec<String> = self.names.iter().map(|n| key(n)).collect();
        let ck: Vec<String> = names.iter().map(|n| key(n)).collect();
        let gw = world(&self.parents, &self.bind);
        let cw = world(parents, &local);
        let gf = facing(&gw, &gk).ok_or("GTA's skeleton: no thighs")?;
        let cf = facing(&cw, &ck).ok_or("the CS skeleton: no thighs")?;
        let r0 = Quat::from_rotation_arc(gf, Vec3::X);
        let rfix = Quat::from_rotation_arc(cf, Vec3::X);
        let gta: Vec<Option<usize>> = ck.iter().map(|k| if MOVING.contains(&k.as_str()) { gk.iter().position(|g| g == k) } else { None }).collect();
        let pelvis = ck.iter().position(|k| k == "pelvis").ok_or("the CS skeleton: no pelvis")?;
        let gta_pelvis = gk.iter().position(|k| k == "pelvis").ok_or("GTA's skeleton: no pelvis")?;
        let floor = |w: &[Frame], k: &[String]| {
            k.iter().enumerate().filter(|(_, k)| ["lfoot", "rfoot", "ltoe0", "rtoe0"].contains(&k.as_str())).map(|(i, _)| w[i].0.z).fold(f32::MAX, f32::min)
        };
        let (gta_floor, cs_floor) = (floor(&gw, &gk), floor(&cw, &ck));
        let scale = (cw[pelvis].0.z - cs_floor) / (gw[gta_pelvis].0.z - gta_floor).max(1e-3);
        // Both at rest facing +X; each CS bone lined up with its GTA bone's way to its first moving child (both
        // skeletons' rest poses needn't be the same: a T, an A), then held in its GTA bone's frame from there.
        let gw: Vec<Frame> = gw.iter().map(|&(p, r)| (r0 * p, (r0 * r).normalize())).collect();
        let cw: Vec<Frame> = cw.iter().map(|&(p, r)| (rfix * p, (rfix * r).normalize())).collect();
        let mut corr = vec![Quat::IDENTITY; names.len()];
        let mut line = vec![Quat::IDENTITY; names.len()];
        for b in 0..names.len() {
            let Some(g) = gta[b] else { continue };
            // a limb bone: lined up with its GTA bone's way down the limb; the trunk as it is (both upright, facing
            // +X); a hand, a toe: as the bone above it
            let next = LIMB.iter().find(|(a, _)| *a == ck[b]).and_then(|(_, c)| ck.iter().position(|k| k == c)).filter(|&c| gta[c].is_some());
            line[b] = match next {
                Some(c) => {
                    let (dc, dg) = ((cw[c].0 - cw[b].0).normalize_or_zero(), (gw[gta[c].unwrap()].0 - gw[g].0).normalize_or_zero());
                    if dc == Vec3::ZERO || dg == Vec3::ZERO { Quat::IDENTITY } else { Quat::from_rotation_arc(dc, dg) }
                }
                None if ["lhand", "rhand", "ltoe0", "rtoe0"].contains(&ck[b].as_str()) => parents[b].map_or(Quat::IDENTITY, |p| line[p]),
                None => Quat::IDENTITY,
            };
            corr[b] = (gw[g].1.inverse() * line[b] * cw[b].1).normalize();
        }
        let moved: Vec<&str> = (0..names.len()).filter(|&b| gta[b].is_some()).map(|b| ck[b].as_str()).collect();
        let report = format!("{} of the CS model's {} bones move with GTA IV's clips ({}); scale {:.1} units/m", moved.len(), names.len(), moved.join(" "), scale);
        self.target = Some(Target { parents: parents.to_vec(), local, gta, corr, rfix, r0, scale, pelvis, gta_pelvis, gta_floor, cs_floor });
        Ok(report)
    }

    /// Clip `c` at `phase` on the CS skeleton: every bone's frame in CS model space facing +X (GoldSrc units, the
    /// ground at the CS model's ground); `moved[b]` 1 for a bone the clip moves.
    fn pose(&self, c: usize, phase: f32, out: &mut [f32], moved: &mut [i32]) -> Result<(), String> {
        self.pose_with(c, phase, out, moved, false)
    }

    /// As `pose`, the body carried by the clip's mover over the ground (not put back in place): its ground the
    /// ground under its end, its place relative to where the clip starts. For get-ups (lying -> standing).
    fn pose_with(&self, c: usize, phase: f32, out: &mut [f32], moved: &mut [i32], travel: bool) -> Result<(), String> {
        self.pose_mode(c, phase, out, moved, if travel { 1 } else { 0 })
    }

    /// mode 0: in place on its feet; 1: carried by its mover; 2: as the clip has it about its root (a vehicle's
    /// seat), its mover on top, nothing re-grounded
    fn pose_mode(&self, c: usize, phase: f32, out: &mut [f32], moved: &mut [i32], mode: i32) -> Result<(), String> {
        let travel = mode == 1;
        let t = self.target.as_ref().ok_or("no target skeleton (da_target)")?;
        let gl = self.sample_rooted(c, phase);
        let mut gw = world(&self.parents, &gl);
        let (mid, floor) = if mode == 2 {
            let m = self.mover(c, phase);
            for f in gw.iter_mut() {
                f.0 += m;
            }
            (Vec3::ZERO, 0.0)
        } else if mode == 3 {
            // (Sandbox) as the clip has it about the model's ground (GTA V's climbs: their rise is the clip data's)
            (Vec3::ZERO, self.target.as_ref().map_or(0.0, |t| t.gta_floor))
        } else if travel {
            let m = self.mover(c, phase);
            for f in gw.iter_mut() {
                f.0 += m;
            }
            (Vec3::ZERO, self.clips[c].floor_end)
        } else {
            (self.clips[c].mid, self.clips[c].floor)
        };
        let n = t.parents.len();
        let mut w: Vec<Frame> = Vec::with_capacity(n);
        for b in 0..n {
            let rest = match t.parents[b] {
                Some(p) => (w[p].0 + w[p].1 * t.local[b].0, (w[p].1 * t.local[b].1).normalize()),
                None => (t.rfix * t.local[b].0, (t.rfix * t.local[b].1).normalize()),
            };
            let f = match t.gta[b] {
                Some(g) => {
                    let rot = (t.r0 * gw[g].1 * t.corr[b]).normalize();
                    let pos = if b == t.pelvis {
                        let p = t.r0 * (gw[t.gta_pelvis].0 - mid.with_z(floor));
                        Vec3::new(p.x * t.scale, p.y * t.scale, p.z * t.scale + t.cs_floor)
                    } else {
                        rest.0
                    };
                    (pos, rot)
                }
                None => rest,
            };
            w.push(f);
        }
        for b in 0..n {
            let (p, r) = w[b];
            out[b * 7..b * 7 + 7].copy_from_slice(&[p.x, p.y, p.z, r.x, r.y, r.z, r.w]);
            moved[b] = t.gta[b].is_some() as i32;
        }
        Ok(())
    }
}

impl Anim {
    /// (Sandbox) GTA V's clips: Grand Theft Gmod's male_move.mdl (made by its exporter from your GTA V) and its clip
    /// data (lib_male_move*.lua: each clip's speed), found in `dir` or the addon's folders under it.
    fn open_mdl(dir: &Path) -> Result<Anim, String> {
        let find = |name: &str, under: &[&str]| -> Option<PathBuf> {
            let mut places = vec![dir.to_path_buf()];
            for u in under { places.push(dir.join(u)); places.push(dir.join("grandtheftgmod").join(u)); }
            places.into_iter().map(|p| p.join(name)).find(|p| p.is_file())
        };
        let path = find("male_move.mdl", &["models/grandtheftgmod/anims", "anims"]).ok_or("male_move.mdl isn't there (models/grandtheftgmod/anims of Grand Theft Gmod)")?;
        let m = mdl::Mdl::read(std::fs::read(&path).map_err(|e| format!("{}: {e}", path.display()))?)?;
        // the clip data: every lib_male_move*.lua beside the first one found
        let mut meta = HashMap::new();
        if let Some(first) = find("lib_male_move.lua", &["lua/grandtheftgmod/clips", "clips"]) {
            let folder = first.parent().unwrap().to_path_buf();
            if let Ok(rd) = std::fs::read_dir(&folder) {
                for e in rd.flatten() {
                    let n = e.file_name().to_string_lossy().to_ascii_lowercase();
                    if (n.starts_with("lib_male_move") || n.starts_with("lib_male_guns")) && n.ends_with(".lua") {
                        if let Ok(t) = std::fs::read_to_string(e.path()) { lua_meta(&t, &mut meta); }
                    }
                }
            }
        }
        // the guns' model beside it, when it's there (its aims, shots, reloads)
        let mut extra = vec![];
        if let Some(g) = path.parent().map(|d| d.join("male_guns.mdl")).filter(|g| g.is_file()) {
            match std::fs::read(&g).map_err(|e| e.to_string()).and_then(mdl::Mdl::read) {
                Ok(x) => extra.push(x),
                Err(e) => eprintln!("male_guns.mdl: {e}"),
            }
        }
        let file = std::fs::File::open(&path).map_err(|e| e.to_string())?;
        Ok(Anim {
            extra,
            parents: m.bones.iter().map(|b| b.parent).collect(),
            bind: m.bones.iter().map(|b| (b.pos, b.quat)).collect(),
            names: m.bones.iter().map(|b| b.name.clone()).collect(),
            ids: (0..m.bones.len() as u16).collect(),
            mdl: Some(m),
            meta,
            pc: false,
            table: vec![],
            file,
            dicts: HashMap::new(),
            clips: vec![],
            target: None,
        })
    }
}

/// (Sandbox) A Source model's animation as a GTA IV one: a rotation and a position track a bone, every frame
fn mdl_animation(m: &mdl::Mdl, ai: usize) -> Result<anim::Animation, String> {
    let ad = &m.anims[ai];
    let n = m.bones.len();
    let mut rot = vec![vec![vec![]; 4]; n];
    let mut pos = vec![vec![vec![]; 3]; n];
    for f in 0..ad.frames {
        let fr = m.frame(ai, f)?;
        for b in 0..n {
            let (p, q) = fr[b];
            for (k, v) in [q.x, q.y, q.z, q.w].into_iter().enumerate() { rot[b][k].push(v); }
            for (k, v) in [p.x, p.y, p.z].into_iter().enumerate() { pos[b][k].push(v); }
        }
    }
    let mut tracks = vec![];
    for b in 0..n {
        tracks.push(anim::Track { kind: anim::BONE_ROTATION, bone: b as u16, values: std::mem::take(&mut rot[b]) });
        tracks.push(anim::Track { kind: anim::BONE_TRANSLATION, bone: b as u16, values: std::mem::take(&mut pos[b]) });
    }
    Ok(anim::Animation { name: ad.name.trim_start_matches('@').to_string(), hash: 0, frames: ad.frames, duration: (ad.frames.max(2) - 1) as f32 / ad.fps.max(1.0), tracks, mover_rotation: None, events: vec![] })
}

/// (Sandbox) A GTA V clip's data (Grand Theft Gmod's lua): its speed (m/s), length, its root's way over the clip
/// (x forward, y left in metres, the turn left in degrees: evenly spaced samples), and its marks (phase pairs: 1 a
/// left heel down, 2 a right heel down, 3 the clip's blend-out point, the MoveEvent "blend_out_*")
#[derive(Clone, Default)]
struct Meta {
    speed: f32,
    dur: f32,
    curve: Vec<f32>,
    tags: Vec<f32>,
    // (climbs) its root's height over the clip (m), sampled as `curve`
    zcurve: Vec<f32>,
    // (melee) its strike: the first frame, the frames' count, then a frame each its ten strike points (the
    // hands, the elbows, the knees and the feet, as the addon numbers them), x forward, y left, z up (m) about his
    // root
    strike: Vec<f32>,
}

fn lua_meta(text: &str, into: &mut HashMap<String, Meta>) {
    let num_at = |rest: &str| -> Option<f32> {
        let rest = rest.trim_start();
        let end = rest.find(|c: char| !(c.is_ascii_digit() || c == '.' || c == '-' || c == 'e')).unwrap_or(rest.len());
        rest[..end].parse().ok()
    };
    let num = |line: &str, key: &str| -> Option<f32> { num_at(&line[line.find(key)? + key.len()..]) };
    let braces = |line: &str, key: &str| -> Option<String> {
        let i = line.find(key)? + key.len();
        let r = &line[i..];
        let (a, b) = (r.find('{')?, r.find('}')?);
        Some(r[a + 1..b].to_string())
    };
    let mut seq: Option<String> = None;
    for line in text.lines() {
        if let Some(i) = line.find("seq = \"") {
            let r = &line[i + 7..];
            seq = r.find('"').map(|e| r[..e].to_ascii_lowercase());
            if let Some(s) = &seq {
                into.insert(s.clone(), Meta { dur: num(line, "dur =").unwrap_or(0.0), ..Default::default() });
            }
            continue;
        }
        let Some(m) = seq.as_ref().and_then(|s| into.get_mut(s)) else { continue };
        if let Some(v) = num(line, "speed =") { m.speed = v * 0.0254; } // inches/s -> m/s
        if let Some(c) = braces(line, "zcurve =") {
            m.zcurve = c.split(',').filter_map(|x| x.trim().parse::<f32>().ok()).map(|v| v * 0.0254).collect();
        } else if let Some(c) = braces(line, "curve =") {
            let v: Vec<f32> = c.split(',').filter_map(|x| x.trim().parse().ok()).collect();
            m.curve = v.chunks_exact(3).flat_map(|t| [t[0] * 0.0254, t[1] * 0.0254, t[2]]).collect();
        }
        if let Some(i) = line.find("strike = {") {
            let r = &line[i..];
            if let (Some(f0), Some(fi)) = (num(r, "f0 ="), r.find("frames = {")) {
                let mut rest = &r[fi + 10..];
                let mut frames: Vec<Vec<f32>> = Vec::new();
                while let Some(a) = rest.find('{') {
                    // (the frames' list ends where a '}' comes before the next '{')
                    if let Some(close) = rest.find('}') { if close < a { break; } }
                    let Some(b) = rest[a..].find('}') else { break };
                    let v: Vec<f32> = rest[a + 1..a + b].split(',').filter_map(|x| x.trim().parse::<f32>().ok()).map(|x| x * 0.0254).collect();
                    if v.len() == 30 { frames.push(v); }
                    rest = &rest[a + b + 1..];
                }
                if !frames.is_empty() {
                    m.strike = vec![f0, frames.len() as f32];
                    for f in frames { m.strike.extend(f); }
                }
            }
        }
        if line.trim_start().starts_with("tags =") {
            let mut rest = line;
            while let Some(i) = rest.find("{ \"") {
                rest = &rest[i + 3..];
                let kind = &rest[..rest.find('"').unwrap_or(0)];
                let after = &rest[kind.len() + 1..];
                let Some(phase) = after.trim_start_matches(|c: char| c == ',' || c == ' ').split(',').next().and_then(|x| x.trim().parse::<f32>().ok()) else { continue };
                let close = after.find("} }").unwrap_or(after.len());
                let body = &after[..close];
                if kind == "Foot" && body.contains("[\"Heel\"] = true") {
                    m.tags.extend([if body.contains("[\"Right\"] = true") { 2.0 } else { 1.0 }, phase]);
                } else if kind == "MoveEvent" && body.contains("\"blend_out") {
                    m.tags.extend([3.0, phase]);
                }
            }
        }
    }
}

// (Sandbox) GTA V's clips (Grand Theft Gmod's model) beside GTA IV's: their numbers from V_BASE up, every clip
// call taking either
static STATE_V: Mutex<Option<Anim>> = Mutex::new(None);
const V_BASE: i32 = 1 << 20;
type TargetArgs = (Vec<String>, Vec<Option<usize>>, Vec<Frame>);
static LAST_TARGET: Mutex<Option<TargetArgs>> = Mutex::new(None);

fn with_clip<R>(c: i32, f: impl FnOnce(&mut Anim, usize) -> Result<R, String>) -> Result<R, String> {
    let (st, i) = if c >= V_BASE { (&STATE_V, c - V_BASE) } else { (&STATE, c) };
    let mut g = st.lock().unwrap_or_else(|e| e.into_inner());
    let a = g.as_mut().ok_or("not open")?;
    let i = usize::try_from(i).ok().filter(|&i| i < a.clips.len()).ok_or("no such clip")?;
    f(a, i)
}

/// (Sandbox) Opens GTA V's clips: Grand Theft Gmod's male_move.mdl and clip data in `dir` (or the addon's folders
/// under it), onto the skeleton da_target was given. 0, or -1.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dv_open(dir: *const c_char) -> i32 {
    let Some(d) = (unsafe { text(dir) }) else { return fail("dv_open: no folder") };
    match Anim::open_mdl(Path::new(d)) {
        Ok(mut a) => {
            if let Some((n, p, l)) = LAST_TARGET.lock().unwrap_or_else(|e| e.into_inner()).clone() {
                if let Err(e) = a.target(&n, &p, l) { return fail(format!("dv_open: {e}")); }
            }
            *STATE_V.lock().unwrap_or_else(|e| e.into_inner()) = Some(a);
            0
        }
        Err(e) => fail(format!("dv_open: {e}")),
    }
}

/// (Sandbox) GTA V's clip (a sequence of the model: "gtgl_male_move_walk"): its number (as da_clip's for the rest),
/// or -1.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dv_clip(name: *const c_char) -> i32 {
    let Some(n) = (unsafe { text(name) }) else { return fail("dv_clip: no name") };
    let mut g = STATE_V.lock().unwrap_or_else(|e| e.into_inner());
    let Some(a) = g.as_mut() else { return fail("dv_clip: call dv_open first") };
    match a.clip("", n) {
        Ok(i) => V_BASE + i as i32,
        Err(e) => fail(format!("dv_clip: {e}")),
    }
}

/// (Sandbox) A GTA V clip's data into `out` (`cap` floats): `what` 0 its root's way (x forward, y left m, turn
/// left degrees, a triple a sample, evenly over the clip), 1 its marks (kind, phase pairs: 1 left heel, 2 right
/// heel, 3 blend-out), 2 a climb's root height, 3 a blow's strike (first frame, frames, ten x/y/z points a frame,
/// metres about the root: x forward, y left, z up). How many floats; 0 for a clip without (GTA IV's), -1 on an error.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dv_clip_data(c: i32, what: i32, out: *mut f32, cap: i32) -> i32 {
    match with_clip(c, |a, i| Ok(a.clips[i].v.as_ref().map(|m| match what { 0 => m.curve.clone(), 1 => m.tags.clone(), 3 => m.strike.clone(), _ => m.zcurve.clone() }).unwrap_or_default())) {
        Ok(v) => {
            let n = v.len().min(cap.max(0) as usize);
            if !out.is_null() { unsafe { std::ptr::copy_nonoverlapping(v.as_ptr(), out, n) }; }
            n as i32
        }
        Err(e) => fail(format!("dv_clip_data: {e}")),
    }
}

fn with<R>(f: impl FnOnce(&mut Anim) -> Result<R, String>) -> Result<R, String> {
    let mut g = STATE.lock().unwrap_or_else(|e| e.into_inner());
    let a = g.as_mut().ok_or("call da_open first")?;
    f(a)
}

/// Opens your GTA IV: `game_dir` the folder with default.xex (and xbox360.rpf, xbox360\anim\anim.img) or GTAIV.exe
/// (and pc\...). 0, or -1 (da_error says why).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn da_open(game_dir: *const c_char) -> i32 {
    let Some(d) = (unsafe { text(game_dir) }) else { return fail("da_open: no folder") };
    match Anim::open(Path::new(d)) {
        Ok(a) => {
            *STATE.lock().unwrap_or_else(|e| e.into_inner()) = Some(a);
            0
        }
        Err(e) => fail(format!("da_open: {e}")),
    }
}

/// The last error into `buf`; its length.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn da_error(buf: *mut c_char, len: i32) -> i32 {
    let e = ERR.lock().unwrap_or_else(|e| e.into_inner()).clone();
    unsafe { put(&e, buf, len) }
}

/// The clip dictionaries in anim.img whose names hold `filter` (comma separated, without ".xad"); how many.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn da_dicts(filter: *const c_char, buf: *mut c_char, len: i32) -> i32 {
    let f = unsafe { text(filter) }.unwrap_or("").to_ascii_lowercase();
    match with(|a| {
        let l: Vec<String> = a.table.iter().map(|e| e.name.to_ascii_lowercase()).filter(|n| n.contains(&f))
            .map(|n| n.trim_end_matches(".xad").trim_end_matches(".wad").to_string()).collect();
        Ok(l)
    }) {
        Ok(l) => {
            unsafe { put(&l.join(","), buf, len) };
            l.len() as i32
        }
        Err(e) => fail(e),
    }
}

/// The clips in dictionary `dict` (comma separated); how many, or -1.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn da_dict_clips(dict: *const c_char, buf: *mut c_char, len: i32) -> i32 {
    let Some(d) = (unsafe { text(dict) }) else { return fail("da_dict_clips: no dictionary") };
    match with(|a| Ok(a.dict(d)?.iter().map(|c| c.name.clone()).collect::<Vec<_>>())) {
        Ok(l) => {
            unsafe { put(&l.join(","), buf, len) };
            l.len() as i32
        }
        Err(e) => fail(e),
    }
}

/// Loads clip `name` from dictionary `dict` ("move_player", "walk"): its number, or -1.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn da_clip(dict: *const c_char, name: *const c_char) -> i32 {
    let (Some(d), Some(n)) = (unsafe { text(dict) }, unsafe { text(name) }) else { return fail("da_clip: no name") };
    match with(|a| a.clip(d, n)) {
        Ok(i) => i as i32,
        Err(e) => fail(format!("da_clip: {e}")),
    }
}

/// Clip `c`'s length (s) and how fast it carries him (m/s, flat).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn da_clip_info(c: i32, duration: *mut f32, speed: *mut f32) -> i32 {
    match with_clip(c, |a, i| Ok((a.clips[i].anim.duration, a.speed(i)))) {
        Ok((d, s)) => {
            unsafe {
                if !duration.is_null() { *duration = d }
                if !speed.is_null() { *speed = s }
            }
            0
        }
        Err(e) => fail(format!("da_clip_info: {e}")),
    }
}

/// Clip `c`'s step phase (Anim::sync): the phase where its left foot is furthest ahead; negative on an error.
#[unsafe(no_mangle)]
pub extern "C" fn da_clip_sync(c: i32) -> f32 {
    match with_clip(c, |a, i| Ok(a.sync(i))) {
        Ok(v) => v,
        Err(e) => {
            fail(format!("da_clip_sync: {e}"));
            -1.0
        }
    }
}

/// The CS model's skeleton: `n` bones, their names, parents (-1 none) and reference pose (7 floats each, in the
/// parent's frame: position, rotation x y z w). The report (which bones move, the scale) into `buf`; how many move.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn da_target(n: i32, names: *const *const c_char, parents: *const i32, local: *const f32, buf: *mut c_char, len: i32) -> i32 {
    if n <= 0 || names.is_null() || parents.is_null() || local.is_null() {
        return fail("da_target: no skeleton");
    }
    let n = n as usize;
    let nm: Vec<String> = (0..n).map(|i| unsafe { text(*names.add(i)) }.unwrap_or("").to_string()).collect();
    let pa: Vec<Option<usize>> = (0..n).map(|i| usize::try_from(unsafe { *parents.add(i) }).ok().filter(|&p| p < i)).collect();
    let lo: Vec<Frame> = (0..n).map(|i| {
        let v = unsafe { std::slice::from_raw_parts(local.add(i * 7), 7) };
        (Vec3::new(v[0], v[1], v[2]), Quat::from_xyzw(v[3], v[4], v[5], v[6]).normalize())
    }).collect();
    *LAST_TARGET.lock().unwrap_or_else(|e| e.into_inner()) = Some((nm.clone(), pa.clone(), lo.clone()));
    if let Some(v) = STATE_V.lock().unwrap_or_else(|e| e.into_inner()).as_mut() { let _ = v.target(&nm, &pa, lo.clone()); }
    match with(|a| a.target(&nm, &pa, lo)) {
        Ok(r) => {
            unsafe { put(&r, buf, len) };
            STATE.lock().unwrap_or_else(|e| e.into_inner()).as_ref().and_then(|a| a.target.as_ref()).map_or(0, |t| t.gta.iter().filter(|g| g.is_some()).count() as i32)
        }
        Err(e) => fail(format!("da_target: {e}")),
    }
}

/// Clip `c` at `phase` (0..1) on the CS skeleton: `out` 7 floats a bone (CS model space facing +X, GoldSrc units,
/// the ground at the model's own), `moved` 1 for the bones the clip moves (the rest as at rest: the host keeps its
/// own animation's there).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn da_pose(c: i32, phase: f32, out: *mut f32, moved: *mut i32) -> i32 {
    if out.is_null() || moved.is_null() {
        return fail("da_pose: no output");
    }
    match with_clip(c, |a, i| {
        let n = a.target.as_ref().ok_or("no target skeleton")?.parents.len();
        let (o, m) = unsafe { (std::slice::from_raw_parts_mut(out, n * 7), std::slice::from_raw_parts_mut(moved, n)) };
        a.pose(i, phase, o, m)
    }) {
        Ok(()) => 0,
        Err(e) => fail(format!("da_pose: {e}")),
    }
}

/// As `da_pose`, with the clip's mover carrying the body over the ground (a get-up rising from lying): the ground
/// at the model's own (the clip's ground under its end), its place relative to where the clip starts.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn da_pose_root(c: i32, phase: f32, out: *mut f32, moved: *mut i32) -> i32 {
    if out.is_null() || moved.is_null() {
        return fail("da_pose_root: no output");
    }
    match with_clip(c, |a, i| {
        let n = a.target.as_ref().ok_or("no target skeleton")?.parents.len();
        let (o, m) = unsafe { (std::slice::from_raw_parts_mut(out, n * 7), std::slice::from_raw_parts_mut(moved, n)) };
        a.pose_with(i, phase, o, m, true)
    }) {
        Ok(()) => 0,
        Err(e) => fail(format!("da_pose_root: {e}")),
    }
}

/// As `da_pose`, the clip about its own root (in a vehicle: the seat) with its mover, not stood on its feet: the
/// pelvis where the clip has it from the root (GTA metres scaled to the target, plus the target's floor height).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn da_pose_seat(c: i32, phase: f32, out: *mut f32, moved: *mut i32) -> i32 {
    if out.is_null() || moved.is_null() {
        return fail("da_pose_seat: no output");
    }
    match with_clip(c, |a, i| {
        let n = a.target.as_ref().ok_or("no target skeleton")?.parents.len();
        let (o, m) = unsafe { (std::slice::from_raw_parts_mut(out, n * 7), std::slice::from_raw_parts_mut(moved, n)) };
        a.pose_mode(i, phase, o, m, 2)
    }) {
        Ok(()) => 0,
        Err(e) => fail(format!("da_pose_seat: {e}")),
    }
}

/// (Sandbox) As `da_pose`, the clip about the model's own ground, where the clip has it (not stood on its feet, not
/// put back over its middle): GTA V's climbs and vaults, whose rise and way along are the clip data's.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn da_pose_ground(c: i32, phase: f32, out: *mut f32, moved: *mut i32) -> i32 {
    if out.is_null() || moved.is_null() {
        return fail("da_pose_ground: no output");
    }
    match with_clip(c, |a, i| {
        let n = a.target.as_ref().ok_or("no target skeleton")?.parents.len();
        let (o, m) = unsafe { (std::slice::from_raw_parts_mut(out, n * 7), std::slice::from_raw_parts_mut(moved, n)) };
        a.pose_mode(i, phase, o, m, 3)
    }) {
        Ok(()) => 0,
        Err(e) => fail(format!("da_pose_ground: {e}")),
    }
}

/// GTA IV's player skeleton: how many bones, and each one's name (`buf`, comma separated).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn da_bones(buf: *mut c_char, len: i32) -> i32 {
    match with(|a| Ok(a.names.join(","))) {
        Ok(s) => {
            unsafe { put(&s, buf, len) };
            s.split(',').count() as i32
        }
        Err(e) => fail(e),
    }
}

/// For checking: clip `dict/name`'s root (bone 0) and pelvis tracks, a line each: kind, frames, first / middle / last.
#[doc(hidden)]
pub fn describe(dict: &str, name: &str) -> String {
    with(|a| {
        let c = a.clip(dict, name)?;
        let clip = &a.clips[c];
        let mut s = format!("{dict}/{name}: {} frames {:.2}s\n", clip.anim.frames, clip.anim.duration);
        let pelvis = a.names.iter().position(|n| n == "Char_Pelvis");
        for (t, m) in clip.anim.tracks.iter().zip(&clip.map) {
            if t.bone == 0 || m.is_some() && *m == pelvis {
                let f = clip.anim.frames;
                let fmt = |v: Vec<f32>| v.iter().map(|x| format!("{x:.3}")).collect::<Vec<_>>().join(" ");
                s += &format!("  bone {} kind {} : [{}] [{}] [{}]\n", t.bone, t.kind, fmt(t.sample(0)), fmt(t.sample(f / 2)), fmt(t.sample(f.saturating_sub(1))));
            }
        }
        if let Some(m) = &clip.anim.mover_rotation {
            s += &format!("  mover rotation: [{:?}] [{:?}]\n", m.sample(0), m.sample(clip.anim.frames - 1));
        }
        Ok(s)
    }).unwrap_or_else(|e| e)
}

/// For checking: the clip's hips facing (degrees, from +X) over its cycle with its root's tracks used, its mover's
/// way, the lowest foot.
#[doc(hidden)]
pub fn facing_report(dict: &str, name: &str) -> String {
    with(|a| {
        let c = a.clip(dict, name)?;
        let keys: Vec<String> = a.names.iter().map(|n| key(n)).collect();
        let mut s = format!("{dict}/{name} bind root t {:?} r {:?}\n  ", a.bind[0].0, a.bind[0].1);
        for k in 0..5 {
            let ph = k as f32 / 4.0;
            let l = a.sample_rooted(c, ph);
            let w = world(&a.parents, &l);
            let f = facing(&w, &keys).unwrap_or(Vec3::ZERO);
            let low = keys.iter().enumerate().filter(|(_, k)| ["lfoot", "rfoot", "ltoe0", "rtoe0"].contains(&k.as_str())).map(|(i, _)| w[i].0.z).fold(f32::MAX, f32::min);
            let pel = w[keys.iter().position(|k| k == "pelvis").unwrap()].0;
            s += &format!("ph {ph:.2}: face {:.0} low {:.2} pelvis ({:.2} {:.2} {:.2}) | ", f.y.atan2(f.x).to_degrees(), low, pel.x, pel.y, pel.z);
        }
        Ok(s + "\n")
    }).unwrap_or_else(|e| e)
}

#[cfg(test)]
mod cache_test {
    use super::*;
    #[test]
    fn round_trip() {
        assert!(delirium_data::kv::import(&vec![7u8; delirium_data::kv::export().map_or(1095 * 12, |v| v.len())]));
        let bones = vec![skeleton::Bone { name: "SKEL_ROOT".into(), parent: None, id: 0, translation: [1.0, 2.0, 3.0], rotation: [0.0, 0.0, 0.0, 1.0], flags: 0, rot_min: [0.0; 3], rot_max: [0.0; 3], block: [0.0; 4] },
                         skeleton::Bone { name: "SKEL_Pelvis".into(), parent: Some(0), id: 11816, translation: [0.0, 0.1, 0.9], rotation: [0.5, 0.5, 0.5, 0.5], flags: 0, rot_min: [0.0; 3], rot_max: [0.0; 3], block: [0.0; 4] }];
        let p = std::env::temp_dir().join("dac_test.cache");
        write_cache(&p, (123, 456), &[9u8; 32], &bones);
        let (_, b) = read_cache(&p, (123, 456)).expect("read back");
        assert_eq!(b.len(), 2);
        assert_eq!(b[1].name, "SKEL_Pelvis");
        assert_eq!(b[1].parent, Some(0));
        assert_eq!(b[1].id, 11816);
        assert_eq!(b[1].rotation, [0.5, 0.5, 0.5, 0.5]);
        assert!(read_cache(&p, (124, 456)).is_none());
        assert_eq!(delirium_data::kv::export().unwrap(), vec![7u8; 1095 * 12]);
    }
}

#[cfg(test)]
mod lua_test {
    // the shape of Grand Theft Gmod's clip data (a start clip of lib_male_move_p2.lua as written)
    const T: &str = r#"GTG.RegisterClips("lib_male_move", {
	["wstart_l_-180"] = { seq = "gtgl_male_move_wstart_l_m180", from = "move_m@generic.ycd", dur = 2, frames = 61, loop = false,
		move = { -102.4, 0, 179.7 }, speed = 51.2,
		curve = { 0,0,0, -0.4,0,28.4, -2.1,0,56.8, -5.2,0,85.2, -7.8,0,113.5, -11.5,0,141.9, -16,0,170.3, -20.5,0,179.8, -25.2,0,179.8, -30.1,0,179.9, -35.5,0,180, -41.5,0,180, -49.1,0,180, -56.5,0,180, -63,0,180, -69.2,0,180, -75.3,0,179.9, -81.8,0,179.9, -88.6,0,179.8, -95.5,0,179.8, -102.4,0,179.7 },
		tags = { { "Foot", 0, 0, { ["Heel"] = true, ["Right"] = true } }, { "MoveEvent", 0, 1, { ["MoveEvent"] = "0x7E1C8464" } }, { "MoveEvent", 0.032, 0.331, { ["MoveEvent"] = "0xC9AF24C2" } }, { "MoveEvent", 0.281, 0.316, { ["MoveEvent"] = "0x70ABE2B4" } }, { "Foot", 0.316, 0.316, { ["Heel"] = true, ["Right"] = false } }, { "MoveEvent", 0.354, 1, { ["MoveEvent"] = "0x47A803B7" } }, { "MoveEvent", 0.592, 0.627, { ["MoveEvent"] = "0x70ABE2B4" } }, { "Foot", 0.627, 0.627, { ["Heel"] = true, ["Right"] = true } }, { "MoveEvent", 0.651, 0.864, { ["MoveEvent"] = "0xC9AF24C2" } }, { "MoveEvent", 0.658, 1, { ["MoveEvent"] = "blend_out_start" } }, { "MoveEvent", 0.832, 0.867, { ["MoveEvent"] = "0x70ABE2B4" } }, { "Foot", 0.867, 0.867, { ["Heel"] = true, ["Right"] = false } } },
		lplant = { 0, 16, 50 }, rplant = { 0, 35 } },
"#;
    #[test]
    fn parse() {
        let mut m = std::collections::HashMap::new();
        super::lua_meta(T, &mut m);
        let c = &m["gtgl_male_move_wstart_l_m180"];
        assert!((c.speed - 51.2 * 0.0254).abs() < 1e-4 && c.dur == 2.0);
        assert_eq!(c.curve.len(), 63);
        assert!((c.curve[60] + 102.4 * 0.0254).abs() < 1e-4 && (c.curve[62] - 179.7).abs() < 1e-4);
        assert_eq!(c.tags, vec![2.0, 0.0, 1.0, 0.316, 2.0, 0.627, 3.0, 0.658, 1.0, 0.867]);
    }

    #[test]
    fn strike() {
        // (made-up numbers in the shape of a blow's strike data)
        let f: Vec<String> = (0..30).map(|i| format!("{}", i as f32)).collect();
        let fr = format!("{{ {} }}", f.join(","));
        let t = format!("\t[\"x\"] = {{ seq = \"gtgl_male_move_melee_x\", dur = 1, frames = 30, loop = false,\n\t\tstrike = {{ f0 = 7, frames = {{ {fr}, {fr} }} }},\n");
        let mut m = std::collections::HashMap::new();
        super::lua_meta(&t, &mut m);
        let s = &m["gtgl_male_move_melee_x"].strike;
        assert_eq!(s.len(), 2 + 60);
        assert_eq!((s[0], s[1]), (7.0, 2.0));
        assert!((s[2 + 29] - 29.0 * 0.0254).abs() < 1e-5 && (s[2 + 30 + 1] - 0.0254).abs() < 1e-5);
        // the real files, if they're here
        if let Ok(p) = std::env::var("GTG_MOVE_LUA") {
            let mut m = std::collections::HashMap::new();
            super::lua_meta(&std::fs::read_to_string(p).unwrap(), &mut m);
            let c = &m["gtgl_male_move_melee_short_0_punch"];
            println!("short_0_punch strike: f0 {} frames {} first {:?} curve {}", c.strike[0], c.strike[1], &c.strike[2..5], c.curve.len());
            let n = m.values().filter(|c| !c.strike.is_empty()).count();
            println!("{n} clips with a strike");
        }
    }
}
