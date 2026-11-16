//! GTA IV's sounds in Delirium Sandbox, from your own files: audio.rpf (config/sounds.dat15: what each sound is
//! made of; config/game.dat16: which sounds a weapon, a vehicle, a footstep uses) and xbox360/audio/sfx/*.rpf (the
//! banks of waves, XMA on the Xbox). A sound is handed out as a small tree (play these together / pick one of these /
//! a wave, each with its volume and pitch); a wave as 16-bit samples, decoded by FFmpeg's XMA2 decoder (linked in on
//! the iPhone: ci/xma.c).
//!
//! The metadata: every object is a type byte, a header (bits saying which of its optional fields follow: the first
//! one the volume in hundredths of a dB, the third the pitch in cents), then its own fields. Its children are
//! named by the hash (joaat) of their names, a wave by its own name's hash; rather than each type's layout, a
//! sound's u32 that is another object's name hash is a child of it, one that is a loaded wave's hash its wave.
//! Types: 12 a wave, 13 all its children at once, 14 one of them at random, 11 a stereo pair (both), the rest one
//! child carried through (wrappers, envelopes, a crossfade's near side, a sequence's first).
use delirium_data::{archive_key::ArchiveKey, audioconfig::Metadata, rpf, xex};
use std::collections::HashMap;
use std::path::Path;

pub mod ffi;

pub fn joaat(s: &str) -> u32 {
    let mut h: u32 = 0;
    for c in s.bytes() {
        h = h.wrapping_add(c.to_ascii_lowercase() as u32);
        h = h.wrapping_add(h << 10);
        h ^= h >> 6;
    }
    h = h.wrapping_add(h << 3);
    h ^= h >> 11;
    h.wrapping_add(h << 15)
}

fn be16(d: &[u8], at: usize) -> Option<u16> { d.get(at..at + 2).map(|b| u16::from_be_bytes([b[0], b[1]])) }
fn be32(d: &[u8], at: usize) -> Option<u32> { d.get(at..at + 4).map(|b| u32::from_be_bytes(b.try_into().unwrap())) }
fn be64(d: &[u8], at: usize) -> Option<u64> { d.get(at..at + 8).map(|b| u64::from_be_bytes(b.try_into().unwrap())) }

/// One wave in a bank.
#[derive(Clone, Copy, Debug)]
pub struct Wave {
    pub bank: usize,
    pub at: usize,       // its data's place in the bank
    pub size: usize,     // bytes (whole 2048-byte packets for XMA)
    pub samples: u32,
    pub loop_start: i32, // -1: plays once
    pub rate: u32,
    pub xma: bool,       // else 16-bit big-endian PCM
}

/// A bank (an .ivaud of the Xbox): a table of waves (hash, its info's place), the infos, the data.
fn read_bank(d: &[u8], bank: usize, into: &mut HashMap<u32, Wave>) -> Option<()> {
    let table = be64(d, 0)? as usize;
    let n = be32(d, 0x10)? as usize;
    let base = be32(d, 0x18)? as usize;
    let infos = table + n * 16;
    for k in 0..n {
        let e = table + 16 * k;
        let (off, hash, isz) = (be64(d, e)? as usize, be32(d, e + 8)?, be32(d, e + 12)? as usize);
        let i = infos + off;
        let r = d.get(i..i + isz)?;
        let w = Wave {
            bank,
            at: base + be64(r, 0)? as usize,
            size: be32(r, 0x0C)? as usize,
            samples: be32(r, 0x10)?,
            loop_start: be32(r, 0x14)? as i32,
            rate: be16(r, 0x18)? as u32,
            // XMA's info goes on (codec 56, packets, a seek table); a PCM wave's ends with one word
            xma: isz > 0x20 && be32(r, 0x24) == Some(56),
        };
        if w.at + w.size <= d.len() && w.rate > 0 {
            into.entry(hash).or_insert(w);
        }
    }
    Some(())
}

/// A sound as handed out: what to play.
#[derive(Clone, Debug)]
pub enum Node {
    Wave { wave: usize, vol: i16, pitch: i16 },
    All { kids: Vec<Node>, vol: i16, pitch: i16 },
    Any { kids: Vec<Node>, vol: i16, pitch: i16 },
}

impl Node {
    /// Flat for C#: kind (0 wave, 1 all, 2 any), volume (1/100 dB), pitch (cents), then the wave's number or the
    /// children's count and the children.
    pub fn flat(&self, out: &mut Vec<i32>) {
        match self {
            Node::Wave { wave, vol, pitch } => out.extend([0, *vol as i32, *pitch as i32, *wave as i32]),
            Node::All { kids, vol, pitch } | Node::Any { kids, vol, pitch } => {
                out.extend([if matches!(self, Node::All { .. }) { 1 } else { 2 }, *vol as i32, *pitch as i32, kids.len() as i32]);
                for k in kids { k.flat(out); }
            }
        }
    }
    fn tune(self, v: i16, p: i16) -> Node {
        let add = |a: i16, b: i16| a.saturating_add(b);
        match self {
            Node::Wave { wave, vol, pitch } => Node::Wave { wave, vol: add(vol, v), pitch: add(pitch, p) },
            Node::All { kids, vol, pitch } => Node::All { kids, vol: add(vol, v), pitch: add(pitch, p) },
            Node::Any { kids, vol, pitch } => Node::Any { kids, vol: add(vol, v), pitch: add(pitch, p) },
        }
    }
}

pub struct Sounds {
    sounds: Metadata,
    game: Metadata,
    by_hash: HashMap<u32, usize>,     // sound objects by their names' hashes
    game_by_name: HashMap<String, usize>,
    banks: Vec<Vec<u8>>,
    waves: HashMap<u32, Wave>,
    /// The waves handed out so far (a Node's `wave` is a place in here).
    pub used: Vec<Wave>,
    used_at: HashMap<u32, usize>,
}

const BANKS: &[&str] = &["resident", "streamed_vehicles", "general"];

impl Sounds {
    /// `dir`: the game's folder (default.xex, audio.rpf, xbox360/audio/sfx/).
    pub fn open(dir: &Path) -> Result<Sounds, String> {
        let audio = std::fs::read(dir.join("audio.rpf")).map_err(|e| format!("audio.rpf: {e} (copy it next to default.xex)"))?;
        let key = match delirium_anim::KEY.get() {
            Some(k) if rpf::read_toc(&audio, k).is_ok() => k.clone(),
            _ => {
                let p = xex::program(&dir.join("default.xex"))?;
                ArchiveKey::find_by_trial(&p, &audio[..0x810.min(audio.len())]).map(|(k, _)| k).ok_or("audio.rpf: no key in default.xex")?
            }
        };
        let toc = rpf::read_toc(&audio, &key)?;
        let get = |name: &str| -> Result<Vec<u8>, String> {
            let e = toc.iter().find(|e| e.path.eq_ignore_ascii_case(name)).ok_or(format!("{name} isn't in audio.rpf"))?;
            rpf::read(&audio, e)
        };
        let sounds = Metadata::read(&get("config/sounds.dat15")?).ok_or("sounds.dat15: not read")?;
        let game = Metadata::read(&get("config/game.dat16")?).ok_or("game.dat16: not read")?;
        let by_hash = sounds.objects.iter().enumerate().map(|(i, o)| (joaat(&o.name), i)).collect();
        let game_by_name = game.objects.iter().enumerate().map(|(i, o)| (o.name.to_ascii_uppercase(), i)).collect();
        let mut s = Sounds { sounds, game, by_hash, game_by_name, banks: vec![], waves: HashMap::new(), used: vec![], used_at: HashMap::new() };
        let sfx = dir.join("xbox360").join("audio").join("sfx");
        let mut found = 0;
        for name in BANKS {
            let Ok(d) = std::fs::read(sfx.join(format!("{name}.rpf"))) else { continue };
            found += 1;
            let toc = rpf::read_toc(&d, &key)?;
            for e in &toc {
                let b = rpf::read(&d, e)?;
                let at = s.banks.len();
                if read_bank(&b, at, &mut s.waves).is_some() { s.banks.push(b); }
            }
        }
        if found == 0 { return Err("xbox360/audio/sfx/resident.rpf: not there (copy it from the game)".into()); }
        Ok(s)
    }

    fn obj(&self, i: usize) -> (u8, &[u8]) {
        let b = self.sounds.bytes(&self.sounds.objects[i]);
        (b.first().copied().unwrap_or(0), b)
    }

    /// A sound by its name (as sounds.dat15 has it), or None when none of it is in the loaded banks.
    pub fn sound(&mut self, name: &str) -> Option<Node> {
        let i = *self.by_hash.get(&joaat(name))?;
        self.node(i, 0)
    }

    /// The sound a game object (game.dat16: a weapon "W_GLOCK", a bike "PCJ", "FOOTSTEPS_CONCRETE_CLEAN") names at
    /// byte `at` of its fields.
    pub fn game_sound(&mut self, object: &str, at: usize) -> Option<Node> {
        let o = &self.game.objects[*self.game_by_name.get(&object.to_ascii_uppercase())?];
        let h = be32(self.game.bytes(o), at)?;
        let i = *self.by_hash.get(&h)?;
        self.node(i, 0)
    }

    fn node(&mut self, i: usize, depth: usize) -> Option<Node> {
        if depth > 16 { return None; }
        let (t, b) = self.obj(i);
        let b = b.to_vec();
        // the header's optional fields: volume (bit 0), something of 2 bytes (bit 1), pitch (bit 2)
        let mask = be16(&b, 13).unwrap_or(0);
        let mut at = 15;
        let mut vol = 0i16;
        let mut pitch = 0i16;
        if mask & 1 != 0 { vol = be16(&b, at).unwrap_or(0) as i16; at += 2; }
        if mask & 2 != 0 { at += 2; }
        if mask & 4 != 0 { pitch = be16(&b, at).unwrap_or(0) as i16; }
        let me = joaat(&self.sounds.objects[i].name);
        // its children (other objects' names) and waves, in order
        let mut kids = vec![];
        let mut wave = None;
        let mut k = 13;
        while k + 4 <= b.len() {
            let v = be32(&b, k).unwrap();
            if t == 12 && wave.is_none() && self.waves.contains_key(&v) { wave = Some(v); k += 4; continue; }
            if t != 12 && v != me {
                if let Some(&c) = self.by_hash.get(&v) { kids.push(c); k += 4; continue; }
            }
            k += 1;
        }
        if t == 12 {
            let h = wave?;
            let w = self.waves[&h];
            let n = *self.used_at.entry(h).or_insert_with(|| { self.used.push(w); self.used.len() - 1 });
            return Some(Node::Wave { wave: n, vol, pitch });
        }
        let kids: Vec<Node> = match t {
            13 | 14 | 11 => kids.into_iter().filter_map(|c| self.node(c, depth + 1)).collect(),
            _ => kids.into_iter().find_map(|c| self.node(c, depth + 1)).into_iter().collect(),
        };
        if kids.is_empty() { return None; }
        Some(match t {
            14 => Node::Any { kids, vol, pitch },
            13 | 11 => Node::All { kids, vol, pitch },
            _ => kids.into_iter().next().unwrap().tune(vol, pitch),
        })
    }

    /// A handed-out wave's samples (mono 16-bit; a stereo one's first channel).
    pub fn pcm(&self, n: usize) -> Result<Vec<i16>, String> {
        let w = *self.used.get(n).ok_or("no such wave")?;
        let d = &self.banks[w.bank][w.at..w.at + w.size];
        if !w.xma {
            return Ok(d.chunks_exact(2).take(w.samples as usize).map(|c| i16::from_be_bytes([c[0], c[1]])).collect());
        }
        let mut out = vec![0i16; w.samples as usize + 4096];
        let got = unsafe { delirium_xma_decode(d.as_ptr(), d.len() as i32, 1, w.rate as i32, out.as_mut_ptr(), out.len() as i32) };
        if got < 0 { return Err(format!("XMA: decoder error {got}")); }
        out.truncate((got as usize).min(w.samples as usize));
        Ok(out)
    }
}

unsafe extern "C" {
    /// ci/xma.c: one XMA2 wave (whole 2048-byte packets) to 16-bit samples; the count, or < 0.
    fn delirium_xma_decode(data: *const u8, size: i32, channels: i32, rate: i32, out: *mut i16, max: i32) -> i32;
}
