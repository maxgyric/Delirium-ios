//! A Source engine model's (.mdl, v44-49) skeleton and animations: what Grand Theft Gmod's exporter makes out of
//! GTA V's clips (from your own GTA V: models/grandtheftgmod/anims/*.mdl, ValveBiped bones), read for Delirium
//! Sandbox's hero. Each animation's frames come out as every bone's local position and rotation (Source's model
//! space: X forward, Y left, Z up, inches).
//!
//! The layout (studio.h): studiohdr_t's bones (216 bytes each), animdescs (100), seqdescs (212). An animation is
//! a list per bone (mstudioanim_t: bone, flags, next) of a raw quaternion (48 or 64 bits) or three run-length
//! encoded euler tracks (mstudioanimvalue_t), and a raw (3 half floats) or encoded position; long ones in sections
//! of `sectionframes` frames (the last frame of those in a section of its own).
use bevy_math::{Quat, Vec3};
use std::collections::HashMap;

fn i32_at(d: &[u8], at: usize) -> Result<i32, String> {
    d.get(at..at + 4).map(|b| i32::from_le_bytes(b.try_into().unwrap())).ok_or_else(|| format!("mdl: read past the end at {at}"))
}
fn i16_at(d: &[u8], at: usize) -> Result<i16, String> {
    d.get(at..at + 2).map(|b| i16::from_le_bytes([b[0], b[1]])).ok_or_else(|| format!("mdl: read past the end at {at}"))
}
fn u16_at(d: &[u8], at: usize) -> Result<u16, String> { i16_at(d, at).map(|v| v as u16) }
fn f32_at(d: &[u8], at: usize) -> Result<f32, String> { i32_at(d, at).map(|v| f32::from_bits(v as u32)) }
fn v3_at(d: &[u8], at: usize) -> Result<Vec3, String> { Ok(Vec3::new(f32_at(d, at)?, f32_at(d, at + 4)?, f32_at(d, at + 8)?)) }
fn text_at(d: &[u8], at: usize) -> String {
    let s = d.get(at..).unwrap_or_default();
    String::from_utf8_lossy(s.split(|&c| c == 0).next().unwrap_or_default()).into_owned()
}

fn half(h: u16) -> f32 {
    let s = if h & 0x8000 != 0 { -1.0 } else { 1.0 };
    let e = ((h >> 10) & 0x1f) as i32;
    let m = (h & 0x3ff) as f32;
    s * match e {
        0 => m * 2f32.powi(-24),
        31 => if m == 0.0 { f32::INFINITY } else { f32::NAN },
        _ => (1.0 + m / 1024.0) * 2f32.powi(e - 15),
    }
}

/// Source's AngleQuaternion (a RadianEuler: x roll, y pitch, z yaw)
fn euler(a: Vec3) -> Quat {
    let (sr, cr) = (a.x * 0.5).sin_cos();
    let (sp, cp) = (a.y * 0.5).sin_cos();
    let (sy, cy) = (a.z * 0.5).sin_cos();
    Quat::from_xyzw(sr * cp * cy - cr * sp * sy, cr * sp * cy + sr * cp * sy, cr * cp * sy - sr * sp * cy, cr * cp * cy + sr * sp * sy)
}

#[derive(Clone, Debug)]
pub struct Bone {
    pub name: String,
    pub parent: Option<usize>,
    pub pos: Vec3,
    pub quat: Quat,
    rot: Vec3,
    posscale: Vec3,
    rotscale: Vec3,
}

#[derive(Clone, Debug)]
pub struct AnimDesc {
    pub name: String,
    pub fps: f32,
    pub flags: i32,
    pub frames: usize,
    at: usize,
    animindex: i32,
    sectionindex: i32,
    sectionframes: i32,
}

pub struct Mdl {
    data: Vec<u8>,
    pub bones: Vec<Bone>,
    pub anims: Vec<AnimDesc>,
    /// sequence name (lower case) -> its (first) animation
    pub seqs: HashMap<String, usize>,
}

const RAWPOS: u8 = 0x01;
const RAWROT: u8 = 0x02;
const ANIMPOS: u8 = 0x04;
const ANIMROT: u8 = 0x08;
const DELTA: u8 = 0x10;
const RAWROT2: u8 = 0x20;

impl Mdl {
    pub fn read(data: Vec<u8>) -> Result<Mdl, String> {
        let d = &data;
        if d.get(0..4) != Some(b"IDST") { return Err("not a Source model (.mdl)".into()); }
        let version = i32_at(d, 4)?;
        if !(44..=49).contains(&version) { return Err(format!("mdl version {version}: only 44-49 are read")); }
        let (nb, bi) = (i32_at(d, 156)? as usize, i32_at(d, 160)? as usize);
        let (na, ai) = (i32_at(d, 180)? as usize, i32_at(d, 184)? as usize);
        let (ns, si) = (i32_at(d, 188)? as usize, i32_at(d, 192)? as usize);
        let mut bones = Vec::with_capacity(nb);
        for b in 0..nb {
            let o = bi + b * 216;
            let parent = i32_at(d, o + 4)?;
            bones.push(Bone {
                name: text_at(d, o + i32_at(d, o)? as usize),
                parent: (parent >= 0).then_some(parent as usize),
                pos: v3_at(d, o + 32)?,
                quat: Quat::from_xyzw(f32_at(d, o + 44)?, f32_at(d, o + 48)?, f32_at(d, o + 52)?, f32_at(d, o + 56)?).normalize(),
                rot: v3_at(d, o + 60)?,
                posscale: v3_at(d, o + 72)?,
                rotscale: v3_at(d, o + 84)?,
            });
        }
        let mut anims = Vec::with_capacity(na);
        for a in 0..na {
            let o = ai + a * 100;
            anims.push(AnimDesc {
                name: text_at(d, o + i32_at(d, o + 4)? as usize),
                fps: f32_at(d, o + 8)?,
                flags: i32_at(d, o + 12)?,
                frames: i32_at(d, o + 16)?.max(1) as usize,
                at: o,
                animindex: i32_at(d, o + 56)?,
                sectionindex: i32_at(d, o + 80)?,
                sectionframes: i32_at(d, o + 84)?,
            });
        }
        let mut seqs = HashMap::new();
        for q in 0..ns {
            let o = si + q * 212;
            let name = text_at(d, o + i32_at(d, o + 4)? as usize).to_ascii_lowercase();
            let first = i16_at(d, o + i32_at(d, o + 60)? as usize)?;
            if first >= 0 && (first as usize) < anims.len() { seqs.insert(name, first as usize); }
        }
        Ok(Mdl { data, bones, anims, seqs })
    }

    /// Animation `a`'s frame `f`: every bone's local frame (its parent's space)
    pub fn frame(&self, a: usize, f: usize) -> Result<Vec<(Vec3, Quat)>, String> {
        let d = &self.data;
        let ad = &self.anims[a];
        let mut out: Vec<(Vec3, Quat)> = self.bones.iter().map(|b| (b.pos, b.quat)).collect();
        let mut frame = f.min(ad.frames - 1) as i32;
        let mut base = ad.at as i64 + ad.animindex as i64;
        if ad.sectionframes != 0 {
            let n = ad.frames as i32;
            let section = if n > ad.sectionframes && frame == n - 1 {
                frame = 0;
                n / ad.sectionframes + 1
            } else {
                let s = frame / ad.sectionframes;
                frame -= s * ad.sectionframes;
                s
            };
            let so = ad.at + ad.sectionindex as usize + section as usize * 8;
            base = ad.at as i64 + i32_at(d, so + 4)? as i64;
        }
        if ad.animindex == 0 && ad.sectionframes == 0 { return Ok(out); }
        let mut p = base as usize;
        for _ in 0..self.bones.len() + 1 {
            let bone = *d.get(p).ok_or("mdl: animation past the end")? as usize;
            let flags = d[p + 1];
            let next = i16_at(d, p + 2)?;
            if bone < self.bones.len() {
                let b = &self.bones[bone];
                let data = p + 4;
                let rot_size = if flags & RAWROT2 != 0 { 8 } else if flags & (RAWROT | ANIMROT) != 0 { 6 } else { 0 };
                // rotation
                let q = if flags & RAWROT2 != 0 {
                    let v = u64::from_le_bytes(d[data..data + 8].try_into().unwrap());
                    let c = |k: u32| ((v >> (21 * k)) & 0x1f_ffff) as f32;
                    let (x, y, z) = ((c(0) - 1048576.0) / 1048576.5, (c(1) - 1048576.0) / 1048576.5, (c(2) - 1048576.0) / 1048576.5);
                    let w = (1.0 - x * x - y * y - z * z).max(0.0).sqrt();
                    Some(Quat::from_xyzw(x, y, z, if v >> 63 != 0 { -w } else { w }))
                } else if flags & RAWROT != 0 {
                    let (a0, a1, a2) = (u16_at(d, data)?, u16_at(d, data + 2)?, u16_at(d, data + 4)?);
                    let x = (a0 as f32 - 32768.0) / 32768.0;
                    let y = (a1 as f32 - 32768.0) / 32768.0;
                    let z = ((a2 & 0x7fff) as f32 - 16384.0) / 16384.0;
                    let w = (1.0 - x * x - y * y - z * z).max(0.0).sqrt();
                    Some(Quat::from_xyzw(x, y, z, if a2 & 0x8000 != 0 { -w } else { w }))
                } else if flags & ANIMROT != 0 {
                    let mut e = Vec3::ZERO;
                    for k in 0..3 {
                        let off = i16_at(d, data + 2 * k)?;
                        let v = if off > 0 { self.value(data + off as usize, frame)? } else { 0.0 };
                        e[k] = v * b.rotscale[k] + if flags & DELTA == 0 { b.rot[k] } else { 0.0 };
                    }
                    Some(euler(e))
                } else {
                    None
                };
                // position
                let pd = data + rot_size;
                let pos = if flags & RAWPOS != 0 {
                    Some(Vec3::new(half(u16_at(d, pd)?), half(u16_at(d, pd + 2)?), half(u16_at(d, pd + 4)?)))
                } else if flags & ANIMPOS != 0 {
                    let mut v3 = Vec3::ZERO;
                    for k in 0..3 {
                        let off = i16_at(d, pd + 2 * k)?;
                        let v = if off > 0 { self.value(pd + off as usize, frame)? } else { 0.0 };
                        v3[k] = v * b.posscale[k] + if flags & DELTA == 0 { b.pos[k] } else { 0.0 };
                    }
                    Some(v3)
                } else {
                    None
                };
                if let Some(q) = q { out[bone].1 = q.normalize(); }
                if let Some(v) = pos { out[bone].0 = v; }
            }
            if next == 0 { break; }
            p = (p as i64 + next as i64) as usize;
        }
        Ok(out)
    }

    // ExtractAnimValue: runs of (valid, total) headers, `valid` values each, the last one held for the rest
    fn value(&self, mut at: usize, frame: i32) -> Result<f32, String> {
        let d = &self.data;
        let mut k = frame;
        loop {
            let (valid, total) = (*d.get(at).ok_or("mdl: value past the end")? as i32, *d.get(at + 1).ok_or("mdl: value past the end")? as i32);
            if total == 0 { return Ok(0.0); }
            if total > k {
                let i = if valid > k { k + 1 } else { valid };
                return Ok(i16_at(d, at + 2 * i as usize)? as f32);
            }
            k -= total;
            at += 2 * (valid as usize + 1);
        }
    }
}
