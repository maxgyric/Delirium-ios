//! A Source map's look beyond its surfaces: its 2D skybox (worldspawn's "skyname": six pictures, <name>rt/bk/lf/ft/up/dn)
//! made into one equirectangular panorama, and its fog, sun and ambient light (env_fog_controller,
//! light_environment), for the sandbox to set when it loads the map.
//!
//! The six faces laid out as Quake 2's st_to_vec (a face's picture coordinates, s and t in -1..1 with t up, and its
//! distance make a direction) but with lf and bk the other way round: rt +X, ft -Y, lf -X, bk +Y, up +Z, dn -Z in
//! Source's axes. That's what HL2's own sky_day01_01 says: going round, each face's right edge meets the next one's
//! left edge (rt, ft, lf, bk: 0.3-0.7 of 255 apart at the seams, 75-106 any other way) and each top meets the up
//! face's edge this layout gives it (about 1 apart, 9-18 the others).

use crate::bsp::V3;
use crate::content::Content;
use std::collections::HashMap;

const FACES: [(&str, [i32; 3]); 6] = [
    ("rt", [3, -1, 2]),
    ("bk", [1, 3, 2]),
    ("lf", [-3, 1, 2]),
    ("ft", [-1, -3, 2]),
    ("up", [-2, -1, 3]),
    ("dn", [2, -1, -3]),
];

struct Pic {
    w: usize,
    h: usize,
    rgba: Vec<u8>,
}

impl Pic {
    // bilinear, u and v in 0..1 (v down from the top), clamped to the edges
    fn at(&self, u: f32, v: f32) -> [f32; 3] {
        let x = (u * self.w as f32 - 0.5).clamp(0.0, (self.w - 1) as f32);
        let y = (v * self.h as f32 - 0.5).clamp(0.0, (self.h - 1) as f32);
        let (x0, y0) = (x as usize, y as usize);
        let (x1, y1) = ((x0 + 1).min(self.w - 1), (y0 + 1).min(self.h - 1));
        let (fx, fy) = (x - x0 as f32, y - y0 as f32);
        let p = |x: usize, y: usize, c: usize| self.rgba[(y * self.w + x) * 4 + c] as f32;
        let mut o = [0.0; 3];
        for c in 0..3 {
            let a = p(x0, y0, c) * (1.0 - fx) + p(x1, y0, c) * fx;
            let b = p(x0, y1, c) * (1.0 - fx) + p(x1, y1, c) * fx;
            o[c] = a * (1.0 - fy) + b * fy;
        }
        o
    }
}

/// The skybox as a panorama (`w` x `w/2`, RGBA, top row first): longitude from Source's +X turning toward +Y across,
/// latitude from straight up down. None when the map names none or its pictures aren't there.
pub fn panorama(content: &Content, entities: &[HashMap<String, String>], w: usize) -> Option<(usize, usize, Vec<u8>, String)> {
    let world = entities.iter().find(|e| e.get("classname").map(|c| c == "worldspawn").unwrap_or(false))?;
    let name = world.get("skyname")?.to_ascii_lowercase();
    let mut faces: Vec<Option<Pic>> = vec![];
    for (suf, _) in FACES {
        let mut pic = None;
        for n in [name.clone(), name.trim_end_matches("_hdr").to_string()] {
            let base = content.material(&format!("skybox/{n}{suf}")).and_then(|m| m.base).unwrap_or(format!("skybox/{n}{suf}"));
            let path = format!("materials/{}.vtf", base.replace('\\', "/").to_ascii_lowercase().trim_end_matches(".vtf"));
            if let Some(Ok((pw, ph, rgba))) = content.find(&path).map(|v| crate::vtf::decode(&v, 1024)) {
                pic = Some(Pic { w: pw, h: ph, rgba });
                break;
            }
        }
        faces.push(pic);
    }
    if faces.iter().take(5).all(|f| f.is_none()) { return None; }
    // below the horizon when the sides stop there (and for a placeholder bottom): the sides' last rows' colour
    let (mut sum, mut n) = ([0f32; 3], 0f32);
    for p in faces.iter().take(4).flatten() {
        let y = if p.h * 2 <= p.w { p.h - 1 } else { p.h / 2 };
        for x in 0..p.w {
            for c in 0..3 { sum[c] += p.rgba[(y * p.w + x) * 4 + c] as f32; }
            n += 1.0;
        }
    }
    let low = if n > 0.0 { [sum[0] / n, sum[1] / n, sum[2] / n] } else { [128.0; 3] };
    let h = w / 2;
    let mut out = vec![255u8; w * h * 4];
    // the bottom face is often left out (under the ground): the lowest row of the sides then
    for y in 0..h {
        let lat = std::f32::consts::FRAC_PI_2 - (y as f32 + 0.5) / h as f32 * std::f32::consts::PI;
        for x in 0..w {
            let lon = (x as f32 + 0.5) / w as f32 * std::f32::consts::TAU - std::f32::consts::PI;
            let d: V3 = [lat.cos() * lon.cos(), lat.cos() * lon.sin(), lat.sin()];
            let c = sample(&faces, d, low).unwrap_or(low);
            let o = (y * w + x) * 4;
            out[o] = c[0] as u8;
            out[o + 1] = c[1] as u8;
            out[o + 2] = c[2] as u8;
        }
    }
    Some((w, h, out, name))
}

// the face the direction goes through, and where on it
fn sample(faces: &[Option<Pic>], d: V3, low: [f32; 3]) -> Option<[f32; 3]> {
    let big = d.iter().map(|v| v.abs()).fold(0.0f32, f32::max);
    if big < 1e-6 { return None; }
    for (k, (_, a)) in FACES.iter().enumerate() {
        // its axis: |a| == 3 at j, the direction's biggest there and on its side
        let j = a.iter().position(|v| v.abs() == 3).unwrap();
        let dist = a[j].signum() as f32 * d[j];
        if dist <= 0.0 || (dist - big).abs() > 1e-6 { continue; }
        let pic = faces[k].as_ref()?;
        if k == 5 && pic.w < 16 { return Some(low); }
        let comp = |m: i32| -> f32 {
            let i = a.iter().position(|v| v.abs() == m).unwrap();
            a[i].signum() as f32 * d[i] / dist
        };
        let (s, t) = (comp(1), comp(2));
        let v = 1.0 - (t + 1.0) * 0.5;
        // a side half as tall as it's wide: just its upper half (the lower clamps to its last row)
        if k < 4 && pic.h * 2 <= pic.w && v > 0.5 {
            // just under the horizon the last row fades into the colour below
            let f = ((v - 0.5) * 12.0).min(1.0);
            let e = pic.at((s + 1.0) * 0.5, 1.0);
            return Some([e[0] + (low[0] - e[0]) * f, e[1] + (low[1] - e[1]) * f, e[2] + (low[2] - e[2]) * f]);
        }
        let v = if k < 4 && pic.h * 2 <= pic.w { v * 2.0 } else { v };
        return Some(pic.at((s + 1.0) * 0.5, v));
    }
    None
}

fn nums(s: &str) -> Vec<f32> { s.split_whitespace().filter_map(|x| x.parse().ok()).collect() }

/// The map's fog, sun and ambient light as "key=values" lines (Unity's axes: the sun's direction is where its light
/// goes; distances in metres)
pub fn environment(entities: &[HashMap<String, String>], exposure: f32) -> String {
    let mut lines = vec![];
    fn class<'a>(entities: &'a [HashMap<String, String>], c: &'a str) -> impl Iterator<Item = &'a HashMap<String, String>> + 'a {
        entities.iter().filter(move |e| e.get("classname").map(|x| x == c).unwrap_or(false))
    }
    if let Some(f) = class(entities, "env_fog_controller").find(|e| e.get("fogenable").map(|v| v == "1").unwrap_or(false)) {
        let c = f.get("fogcolor").map(|s| nums(s)).unwrap_or_default();
        if c.len() >= 3 {
            let start = f.get("fogstart").and_then(|v| v.parse::<f32>().ok()).unwrap_or(0.0).max(0.0) * crate::SCALE;
            let end = f.get("fogend").and_then(|v| v.parse::<f32>().ok()).unwrap_or(10000.0) * crate::SCALE;
            let max = f.get("fogmaxdensity").and_then(|v| v.parse::<f32>().ok()).unwrap_or(1.0).clamp(0.0, 1.0);
            lines.push(format!("fog={} {} {} {} {} {}", c[0] / 255.0, c[1] / 255.0, c[2] / 255.0, start, end.max(start + 1.0), max));
        }
    }
    if let Some(l) = class(entities, "light_environment").next() {
        let c = l.get("_light").map(|s| nums(s)).unwrap_or_default();
        let a = l.get("angles").map(|s| nums(s)).unwrap_or_default();
        // its "pitch" key goes down negative (-90: straight down); its angles' pitch (an angle's) down positive
        let pitch = l.get("pitch").and_then(|v| v.parse::<f32>().ok()).map(|p| -p).or(a.first().copied()).unwrap_or(45.0);
        let yaw = a.get(1).copied().unwrap_or(0.0);
        // Source's forward for (pitch, yaw): its light goes that way
        let (sp, cp) = pitch.to_radians().sin_cos();
        let (sy, cy) = yaw.to_radians().sin_cos();
        let f = [cp * cy, cp * sy, -sp];
        // to Unity: (-x, z, -y) of Source's (the .glb's x mirrored)
        let u = [-f[0], f[2], -f[1]];
        if c.len() >= 3 {
            let bright = c.get(3).copied().unwrap_or(200.0);
            lines.push(format!("sun={} {} {} {} {} {} {}", c[0] / 255.0, c[1] / 255.0, c[2] / 255.0, (bright / 350.0 * exposure).clamp(0.3, 1.4), u[0], u[1], u[2]));
        }
        let m = l.get("_ambient").map(|s| nums(s)).unwrap_or_default();
        if m.len() >= 3 {
            // (brought up with the baked light, and never so dark the people are black shapes)
            let k = (m.get(3).copied().unwrap_or(150.0) / 200.0 * exposure).clamp(0.45, 1.6);
            lines.push(format!("ambient={} {} {}", (m[0] / 255.0 * k).min(1.0), (m[1] / 255.0 * k).min(1.0), (m[2] / 255.0 * k).min(1.0)));
        }
    }
    lines.join("\n")
}
