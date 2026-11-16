//! GTA IV's clips on a Counter-Strike player: the player's skeleton (player.xft) and his clips from anim.img, read
//! from your own game the way the Playroom's hero reads them (nothing of the game is in here), each pose turned onto
//! the CS model's bones. A C interface for the CS Delirium plugin (delirium_anim.dll), 32-bit like the game.
//!
//! The target pose comes out in CS model space with the character facing +X, Z up, the ground at the CS model's own
//! (its reference pose's) ground, in GoldSrc units; the host puts it where the player stands, turned his way.

use bevy_math::{Quat, Vec3};
use delirium_data::{anim, archive_key::ArchiveKey, img, model, resource, rpf, skeleton};
use std::collections::HashMap;
use std::ffi::{CStr, c_char};
use std::io::{Read, Seek, SeekFrom};
use std::path::{Path, PathBuf};
use std::sync::Mutex;

type Frame = (Vec3, Quat);

struct Clip {
    anim: anim::Animation,
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
    for p in ["char", "bip01", "skel"] {
        if let Some(r) = k.strip_prefix(p) {
            if !r.is_empty() {
                return r.to_string();
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
        let prog = delirium_data::xex::program(&exe)?;
        if !delirium_data::kv::unlock(&prog) {
            return Err(format!("{}: not a version the numbers are read from (the Steam GTAIV.exe or the disc's default.xex)", exe.display()));
        }
        delirium_data::LE.store(pc, std::sync::atomic::Ordering::Relaxed);
        let rd = |p: &str| std::fs::read(dir.join(p)).map_err(|e| format!("{p}: {e}"));
        // the archives' key, out of the program, tried on an archive's head; the player's archive
        let find = |head: &[u8]| ArchiveKey::find_by_trial(&prog, &head[..0x810.min(head.len())]).map(|(k, _)| k).ok_or("no archive key in the game's program");
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
        let inner = rpf::read_toc(&peds, &key)?;
        let e = inner.iter().find(|e| e.path.eq_ignore_ascii_case(&plat(pc, "player.xft"))).ok_or("player.xft not in playerped.rpf")?;
        let frag = resource::parse(&rpf::read(&peds, e)?)?;
        let bones = skeleton::read(&frag, model::fragment_root(&frag)?)?;
        let path = PathBuf::from(plat(pc, &dir.join("xbox360/anim/anim.img").to_string_lossy()));
        let mut file = std::fs::File::open(&path).map_err(|e| format!("anim.img: {e}"))?;
        let mut head = vec![];
        (&mut file).take(delirium_data::kv!(1 int) << delirium_data::kv!(127 int)).read_to_end(&mut head).map_err(|e| e.to_string())?;
        let table = img::read_table(&head, Some(&key))?;
        Ok(Anim {
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
        let a = self.dict(dict)?.iter().find(|a| a.name.eq_ignore_ascii_case(name)).cloned().ok_or(format!("no {name} in {dict}"))?;
        let map = a.tracks.iter().map(|t| self.ids.iter().position(|&id| id == t.bone).filter(|&b| b != 0)).collect();
        self.clips.push(Clip { anim: a, map, mid: Vec3::ZERO, floor: 0.0, floor_end: 0.0 });
        let c = self.clips.len() - 1;
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
                anim::BONE_ROTATION => out[b].1 = quat(&p).slerp(quat(&q), f),
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
                    anim::BONE_ROTATION => out[0].1 = quat(&t.sample(i)).slerp(quat(&t.sample(i + 1)), f),
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
        let way = self.travel(c).with_z(0.0);
        if way.length() / a.duration.max(0.01) < 0.2 {
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
    match with(|a| {
        let i = usize::try_from(c).ok().filter(|&i| i < a.clips.len()).ok_or("no such clip")?;
        Ok((a.clips[i].anim.duration, a.speed(i)))
    }) {
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
    match with(|a| {
        let i = usize::try_from(c).ok().filter(|&i| i < a.clips.len()).ok_or("no such clip")?;
        Ok(a.sync(i))
    }) {
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
    match with(|a| {
        let i = usize::try_from(c).ok().filter(|&i| i < a.clips.len()).ok_or("no such clip")?;
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
    match with(|a| {
        let i = usize::try_from(c).ok().filter(|&i| i < a.clips.len()).ok_or("no such clip")?;
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
    match with(|a| {
        let i = usize::try_from(c).ok().filter(|&i| i < a.clips.len()).ok_or("no such clip")?;
        let n = a.target.as_ref().ok_or("no target skeleton")?.parents.len();
        let (o, m) = unsafe { (std::slice::from_raw_parts_mut(out, n * 7), std::slice::from_raw_parts_mut(moved, n)) };
        a.pose_mode(i, phase, o, m, 2)
    }) {
        Ok(()) => 0,
        Err(e) => fail(format!("da_pose_seat: {e}")),
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
