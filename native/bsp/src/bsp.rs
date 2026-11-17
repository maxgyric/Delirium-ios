//! A Source engine map (.bsp, versions 19-21: Garry's Mod's, Counter-Strike: Source's, Half-Life 2's): its
//! visible surfaces (the world's brushes, brush entities, displacements) as triangles, each with its material, and
//! its entities (where you start, the 3D skybox's camera). Everything in Source's units (inches) and axes (X
//! forward, Y left, Z up).
//!
//! The layout (Valve's bspfile.h): a header of 64 lumps {offset, length, version, fourCC}; planes (20 bytes),
//! vertices (12), edges (2 x u16), surfedges (i32, negative: the edge backwards), faces (56), texinfo (72),
//! texdata (32) and its name table, models (48), displacement infos (176) and vertices (20), nodes (32), leaves (32,
//! or 56 in version 0 of the lump).

use std::collections::HashMap;

pub type V3 = [f32; 3];

fn sub(a: V3, b: V3) -> V3 { [a[0] - b[0], a[1] - b[1], a[2] - b[2]] }
fn add(a: V3, b: V3) -> V3 { [a[0] + b[0], a[1] + b[1], a[2] + b[2]] }
fn mul(a: V3, k: f32) -> V3 { [a[0] * k, a[1] * k, a[2] * k] }
pub fn dot(a: V3, b: V3) -> f32 { a[0] * b[0] + a[1] * b[1] + a[2] * b[2] }
pub fn cross(a: V3, b: V3) -> V3 { [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]] }
fn lerp(a: V3, b: V3, t: f32) -> V3 { add(a, mul(sub(b, a), t)) }

struct R<'a>(&'a [u8]);
impl R<'_> {
    fn i32(&self, at: usize) -> Result<i32, String> {
        self.0.get(at..at + 4).map(|b| i32::from_le_bytes(b.try_into().unwrap())).ok_or_else(|| format!("bsp: read past the end at {at}"))
    }
    fn u16(&self, at: usize) -> Result<u16, String> {
        self.0.get(at..at + 2).map(|b| u16::from_le_bytes([b[0], b[1]])).ok_or_else(|| format!("bsp: read past the end at {at}"))
    }
    fn i16(&self, at: usize) -> Result<i16, String> { self.u16(at).map(|v| v as i16) }
    fn f32(&self, at: usize) -> Result<f32, String> { self.i32(at).map(|v| f32::from_bits(v as u32)) }
    fn v3(&self, at: usize) -> Result<V3, String> { Ok([self.f32(at)?, self.f32(at + 4)?, self.f32(at + 8)?]) }
}

#[derive(Clone, Copy)]
struct Lump {
    ofs: usize,
    len: usize,
    version: i32,
}

/// One triangle soup per material (a texdata): positions, the face's normal at each, and texture coordinates in
/// texels (divide by the texture's size).
#[derive(Default)]
pub struct Batch {
    pub pos: Vec<V3>,
    pub nrm: Vec<V3>,
    pub uv: Vec<[f32; 2]>,
    /// where in the lightmap (Map::lightmap's pixels)
    pub luv: Vec<[f32; 2]>,
}

/// The map's baked light: every face's lightmap packed into one picture (RGBA, top row first; half its brightness,
/// the shader doubles it), and where a face without one looks (a white texel).
pub struct Lightmap {
    pub w: usize,
    pub h: usize,
    pub rgba: Vec<u8>,
}

// Shelves of faces' lightmaps, left to right, a row at a time (1 texel apart)
struct Packer {
    w: usize,
    h: usize,
    x: usize,
    y: usize,
    row: usize,
    px: Vec<u8>,
}

impl Packer {
    fn new(w: usize) -> Packer {
        // a white block first: where faces without lightmaps look (half brightness: the shader doubles it)
        let mut p = Packer { w, h: 0, x: 0, y: 0, row: 0, px: vec![] };
        p.place(2, 2).map(|(x, y)| p.fill(x, y, 2, 2, |_, _| [128, 128, 128]));
        p
    }
    fn place(&mut self, w: usize, h: usize) -> Option<(usize, usize)> {
        if w + 2 > self.w { return None; }
        if self.x + w + 2 > self.w {
            self.y += self.row;
            self.x = 0;
            self.row = 0;
        }
        if self.y + h + 2 > 4096 { return None; }
        let at = (self.x + 1, self.y + 1);
        self.x += w + 2;
        self.row = self.row.max(h + 2);
        let need = (self.y + self.row) * self.w * 4;
        if self.px.len() < need { self.px.resize(need, 255); }
        self.h = self.h.max(self.y + self.row);
        Some(at)
    }
    // a face's luxels in, its border repeated outward a texel (no dark seams when filtered)
    fn fill(&mut self, x0: usize, y0: usize, w: usize, h: usize, f: impl Fn(usize, usize) -> [u8; 3]) {
        for y in 0..h + 2 {
            for x in 0..w + 2 {
                let (sx, sy) = (x.saturating_sub(1).min(w - 1), y.saturating_sub(1).min(h - 1));
                let c = f(sx, sy);
                let (px, py) = (x0 + x - 1, y0 + y - 1);
                let o = (py * self.w + px) * 4;
                if o + 4 <= self.px.len() { self.px[o..o + 4].copy_from_slice(&[c[0], c[1], c[2], 255]); }
            }
        }
    }
}

// Source's luxel (RGB times 2^exponent, linear) to the lightmap's byte: brightened by the map's exposure, gamma,
// half (overbright x2)
fn luxel(r: u8, g: u8, b: u8, e: i8, exposure: f32) -> [u8; 3] {
    let k = 2f32.powi(e as i32) / 255.0 * exposure;
    let f = |c: u8| ((c as f32 * k).max(0.0).powf(1.0 / 2.2) * 0.5 * 255.0).round().clamp(0.0, 255.0) as u8;
    [f(r), f(g), f(b)]
}

pub struct Material {
    pub name: String,
    pub color: V3, // the texture's average colour (its reflectivity), linear
    pub width: i32,
    pub height: i32,
}

pub struct Map {
    pub lightmap: Option<Lightmap>,
    pub version: i32,
    pub materials: Vec<Material>,
    pub batches: HashMap<usize, Batch>,
    pub entities: Vec<HashMap<String, String>>,
    /// the same entities with every key in order, repeated ones too (outputs: "OnPressed" a line each)
    pub entities_kv: Vec<Vec<(String, String)>>,
    /// each brush model's box (its own space: a brush entity's origin moves it)
    pub model_bounds: Vec<(V3, V3)>,
    /// faces left out: (in the 3D skybox, tools / sky / triggers)
    pub skipped: (usize, usize),
    /// the 3D skybox: its box, its camera, its scale (its faces and models drawn round the map that much bigger)
    pub sky: Option<(V3, V3, V3, f32)>,
    /// how much the baked light was brightened (GMod's HDR exposure brings a dark map up: dusk isn't black)
    pub exposure: f32,
    /// where its pakfile (a zip of its own materials) is in the file
    pub pak: (usize, usize),
    /// its models placed in it (static props, prop_ entities)
    pub props: Vec<Prop>,
    /// where in the lightmap a model's light is (the map's average: they have no lightmap of their own)
    pub prop_luv: [f32; 2],
}

pub struct Prop {
    pub model: String,
    pub origin: V3,
    pub angles: V3,
    pub skin: usize,
    pub solid: bool,
    pub scale: f32,
    /// the entity it is when it can move or be told to (prop_door_rotating, a named prop_dynamic): its own meshes
    pub ent: Option<usize>,
}

// The game lump's static props ("sprp"): its model names, its leaves, then each prop (as long as its version says)
fn static_props(data: &[u8], game: &[u8]) -> Vec<Prop> {
    let mut out = vec![];
    let r = R(game);
    let Ok(n) = r.i32(0) else { return out };
    for k in 0..(n.max(0) as usize).min(64) {
        let e = 4 + k * 16;
        let Some(id) = game.get(e..e + 4) else { break };
        if id != b"prps" && id != b"sprp" { continue; }
        let (flags, version) = (r.u16(e + 4).unwrap_or(0), r.u16(e + 6).unwrap_or(0) as usize);
        let (ofs, len) = (r.i32(e + 8).unwrap_or(0) as usize, r.i32(e + 12).unwrap_or(0) as usize);
        let Some(raw) = data.get(ofs..ofs + len) else { continue };
        let body: std::borrow::Cow<[u8]> = if flags & 1 != 0 && raw.len() >= 17 && &raw[0..4] == b"LZMA" {
            let size = u32::from_le_bytes(raw[4..8].try_into().unwrap()) as u64;
            let mut alone = raw[12..17].to_vec();
            alone.extend_from_slice(&size.to_le_bytes());
            alone.extend_from_slice(&raw[17..]);
            match crate::gma::unlzma(&alone) { Ok(b) => std::borrow::Cow::Owned(b), Err(_) => continue }
        } else {
            std::borrow::Cow::Borrowed(raw)
        };
        let b = R(&body);
        let Ok(nd) = b.i32(0) else { continue };
        let names: Vec<String> = (0..nd.max(0) as usize).map(|i| {
            let o = 4 + i * 128;
            String::from_utf8_lossy(body.get(o..o + 128).unwrap_or_default().split(|&c| c == 0).next().unwrap_or_default()).replace('\\', "/").to_ascii_lowercase()
        }).collect();
        let mut at = 4 + names.len() * 128;
        let nl = b.i32(at).unwrap_or(0).max(0) as usize;
        at += 4 + nl * 2;
        let np = b.i32(at).unwrap_or(0).max(0) as usize;
        at += 4;
        if np == 0 { continue; }
        // each prop's size: its version's, or what's left shared out
        let stride = match version { 4 => 56, 5 => 60, 6 => 64, 7 | 8 => 68, 9 => 72, 10 => 76, 11 => 80, _ => 0 };
        let stride = if stride * np == body.len() - at || stride == 0 { if stride == 0 { (body.len() - at) / np } else { stride } } else { (body.len() - at) / np };
        for i in 0..np {
            let o = at + i * stride;
            let (Ok(origin), Ok(angles), Ok(t)) = (b.v3(o), b.v3(o + 12), b.u16(o + 24)) else { break };
            let solid = body.get(o + 30).copied().unwrap_or(6) != 0;
            let skin = b.i32(o + 32).unwrap_or(0).max(0) as usize;
            let scale = if version >= 11 && stride >= 80 { b.f32(o + stride - 4).unwrap_or(1.0) } else { 1.0 };
            if let Some(m) = names.get(t as usize) {
                out.push(Prop { model: m.clone(), origin, angles, skin, solid, scale: if scale > 0.01 && scale < 100.0 { scale } else { 1.0 }, ent: None });
            }
        }
    }
    out
}

const SURF_SKY2D: i32 = 0x2;
const SURF_SKY: i32 = 0x4;
const SURF_TRIGGER: i32 = 0x40;
const SURF_NODRAW: i32 = 0x80;
const SURF_HINT: i32 = 0x100;
const SURF_SKIP: i32 = 0x200;

/// The entities as parse_entities finds them (the same blocks, in the same order) with all their keys, in order
pub fn parse_entities_kv(text: &str) -> Vec<Vec<(String, String)>> {
    let mut out = vec![];
    let mut cur: Option<Vec<(String, String)>> = None;
    let mut key: Option<String> = None;
    let mut chars = text.chars().peekable();
    while let Some(c) = chars.next() {
        match c {
            '{' => cur = Some(vec![]),
            '}' => {
                if let Some(e) = cur.take() { out.push(e); }
                key = None;
            }
            '"' => {
                let mut s = String::new();
                for c in chars.by_ref() {
                    if c == '"' { break; }
                    s.push(c);
                }
                if let Some(e) = cur.as_mut() {
                    match key.take() {
                        None => key = Some(s),
                        Some(k) => e.push((k, s)),
                    }
                }
            }
            _ => {}
        }
    }
    out
}

/// Brush entities that move or are told to (their faces get meshes of their own, "[ent N]")
pub fn is_mover(class: &str) -> bool {
    matches!(class, "func_door" | "func_door_rotating" | "func_button" | "func_rot_button" | "momentary_rot_button" | "func_movelinear"
        | "func_tracktrain" | "func_train" | "func_rotating" | "func_brush" | "func_wall_toggle" | "func_platrot" | "func_water_analog"
        | "func_breakable" | "func_breakable_surf" | "func_physbox" | "func_physbox_multiplayer" | "func_lookdoor" | "func_illusionary")
}

pub fn parse_entities(text: &str) -> Vec<HashMap<String, String>> {
    let mut out = vec![];
    let mut cur: Option<HashMap<String, String>> = None;
    let mut key: Option<String> = None;
    let mut chars = text.chars().peekable();
    while let Some(c) = chars.next() {
        match c {
            '{' => cur = Some(HashMap::new()),
            '}' => {
                if let Some(e) = cur.take() { out.push(e); }
                key = None;
            }
            '"' => {
                let mut s = String::new();
                for c in chars.by_ref() {
                    if c == '"' { break; }
                    s.push(c);
                }
                if let Some(e) = cur.as_mut() {
                    match key.take() {
                        None => key = Some(s),
                        Some(k) => { e.entry(k.to_ascii_lowercase()).or_insert(s); }
                    }
                }
            }
            _ => {}
        }
    }
    out
}

pub fn vec3_of(s: &str) -> Option<V3> {
    let v: Vec<f32> = s.split_whitespace().filter_map(|x| x.parse().ok()).collect();
    (v.len() >= 3).then(|| [v[0], v[1], v[2]])
}

impl Map {
    pub fn read(data: &[u8]) -> Result<Map, String> {
        let r = R(data);
        if data.get(0..4) != Some(b"VBSP") {
            return Err(if data.get(0..4) == Some(b"PSBV") { "a console (big-endian) map: not read".into() } else { "not a Source map (.bsp)".into() });
        }
        let version = r.i32(4)?;
        if !(19..=21).contains(&version) {
            return Err(format!("map version {version}: only Source's 19-21 (Garry's Mod, CS:S, HL2) are read"));
        }
        let mut lumps = [Lump { ofs: 0, len: 0, version: 0 }; 64];
        for (i, l) in lumps.iter_mut().enumerate() {
            let at = 8 + i * 16;
            let (a, b, c) = (r.i32(at)?, r.i32(at + 4)?, r.i32(at + 8)?);
            // version 21's lumps (L4D2 style) have {version, offset, length}; Garry's Mod's are 20
            *l = if version == 21 && a >= 0 && a < 4 && (b as usize) >= 8 + 64 * 16 {
                Lump { version: a, ofs: b as usize, len: c as usize }
            } else {
                Lump { ofs: a as usize, len: b as usize, version: c }
            };
        }
        let lump = |i: usize| -> Result<std::borrow::Cow<[u8]>, String> {
            let l = lumps[i];
            let d = data.get(l.ofs..l.ofs + l.len).ok_or(format!("bsp: lump {i} past the end"))?;
            // a compressed lump: "LZMA", its size, the stream's, 5 property bytes, the stream (as an .lzma file)
            if d.len() >= 17 && &d[0..4] == b"LZMA" {
                let size = u32::from_le_bytes(d[4..8].try_into().unwrap()) as u64;
                let mut alone = d[12..17].to_vec();
                alone.extend_from_slice(&size.to_le_bytes());
                alone.extend_from_slice(&d[17..]);
                return crate::gma::unlzma(&alone).map(std::borrow::Cow::Owned).map_err(|e| format!("bsp: lump {i}: {e}"));
            }
            Ok(std::borrow::Cow::Borrowed(d))
        };
        let ent_text = String::from_utf8_lossy(&lump(0)?).into_owned();
        let entities = parse_entities(&ent_text);
        let entities_kv = parse_entities_kv(&ent_text);
        let planes = lump(1)?;
        let texdata = lump(2)?;
        let verts = lump(3)?;
        let nodes = lump(5)?;
        let texinfo = lump(6)?;
        let faces = lump(7)?;
        // the baked light (LDR, or HDR if that's all there is): 4 bytes a luxel
        // the baked light: LDR's, or (an HDR-only map) HDR's, whose faces' places in it are in the HDR faces lump
        let ldr = lump(8)?;
        let hdr = ldr.is_empty();
        let light = if hdr { lump(53)? } else { ldr };
        let faces_hdr = if hdr { lump(58)? } else { std::borrow::Cow::Borrowed(&[][..]) };
        let fl = if faces_hdr.len() == faces.len() { R(&faces_hdr) } else { R(&faces) };
        // the exposure: the light's average brought up to GMod's (its HDR eye adapts), never darkened
        let exposure = {
            let (mut sum, mut n) = (0f64, 0f64);
            for q in (0..light.len() / 4).step_by(5) {
                let k = 2f32.powi(light[q * 4 + 3] as i8 as i32) / 255.0;
                let y = 0.2126 * light[q * 4] as f32 + 0.7152 * light[q * 4 + 1] as f32 + 0.0722 * light[q * 4 + 2] as f32;
                sum += (y * k) as f64;
                n += 1.0;
            }
            if n > 0.0 && sum > 0.0 { (0.22 / (sum / n) as f32).clamp(1.0, 12.0) } else { 1.0 }
        };
        let leaves = lump(10)?;
        let edges = lump(12)?;
        let surfedges = lump(13)?;
        let models = lump(14)?;
        let dispinfo = lump(26)?;
        let dispverts = lump(33)?;
        let strdata = lump(43)?;
        let strtable = lump(44)?;
        let (p, tx, vx, nd, ti, fc, lf, ed, se, md, di, dv, sd, st) =
            (R(&planes), R(&texdata), R(&verts), R(&nodes), R(&texinfo), R(&faces), R(&leaves), R(&edges), R(&surfedges), R(&models), R(&dispinfo), R(&dispverts), R(&strdata), R(&strtable));

        // materials
        let mut materials = vec![];
        for k in 0..texdata.len() / 32 {
            let o = k * 32;
            let id = tx.i32(o + 12)? as usize;
            let so = st.i32(id * 4).unwrap_or(0).max(0) as usize;
            let name = strdata.get(so..).map(|s| String::from_utf8_lossy(s.split(|&c| c == 0).next().unwrap_or_default()).into_owned()).unwrap_or_default();
            materials.push(Material { name: name.replace('\\', "/").to_ascii_lowercase(), color: tx.v3(o)?, width: tx.i32(o + 16)?.max(1), height: tx.i32(o + 20)?.max(1) });
        }
        let _ = sd;

        // the 3D skybox (a small copy of the world around the sky_camera, in an area of its own): its box, to leave
        let leaf_size = if lumps[10].version == 0 { 56 } else { 32 };
        let sky_cam = entities.iter().find(|e| e.get("classname").map(|c| c == "sky_camera").unwrap_or(false)).and_then(|e| {
            Some((e.get("origin").and_then(|o| vec3_of(o))?, e.get("scale").and_then(|v| v.parse::<f32>().ok()).filter(|&v| v > 0.5).unwrap_or(16.0)))
        });
        let sky_box = entities.iter().find(|e| e.get("classname").map(|c| c == "sky_camera").unwrap_or(false)).and_then(|e| e.get("origin").and_then(|o| vec3_of(o))).and_then(|at| {
            // down the tree to the leaf it's in
            let mut n: i32 = md.i32(36).ok()?; // model 0's headnode
            let mut guard = 0;
            while n >= 0 && guard < 100000 {
                guard += 1;
                let o = n as usize * 32;
                let pl = nd.i32(o).ok()? as usize;
                let (normal, dist) = (p.v3(pl * 20).ok()?, p.f32(pl * 20 + 12).ok()?);
                n = if dot(normal, at) - dist >= 0.0 { nd.i32(o + 4).ok()? } else { nd.i32(o + 8).ok()? };
            }
            let leaf = (-1 - n) as usize;
            let area = (lf.i16(leaf * leaf_size + 6).ok()? as u16 & 0x1ff) as i32;
            let (mut lo, mut hi) = ([f32::MAX; 3], [f32::MIN; 3]);
            for k in 0..leaves.len() / leaf_size {
                let o = k * leaf_size;
                if (lf.i16(o + 6).ok()? as u16 & 0x1ff) as i32 != area || lf.i32(o).ok()? == 1 { continue; } // (solid leaves skipped)
                for a in 0..3 {
                    lo[a] = lo[a].min(lf.i16(o + 8 + a * 2).ok()? as f32);
                    hi[a] = hi[a].max(lf.i16(o + 14 + a * 2).ok()? as f32);
                }
            }
            (lo[0] <= hi[0]).then_some((lo, hi))
        });

        // which brush models to draw: the world (0) and the brush entities that aren't triggers or tools
        let mut draw: Vec<(usize, V3, usize)> = vec![(0, [0.0; 3], 0)];
        for (ei, e) in entities.iter().enumerate() {
            let Some(m) = e.get("model").and_then(|m| m.strip_prefix('*')).and_then(|m| m.parse::<usize>().ok()) else { continue };
            let class = e.get("classname").map(String::as_str).unwrap_or("");
            if class.starts_with("trigger_") || class.starts_with("func_areaportal") || class == "func_occluder" || class == "func_clip_vphysics" || class == "func_viscluster" || class == "func_precipitation" || class.starts_with("env_") {
                continue;
            }
            if e.get("rendermode").map(|v| v == "10").unwrap_or(false) { continue; } // "don't render"
            let origin = e.get("origin").and_then(|o| vec3_of(o)).unwrap_or([0.0; 3]);
            // one that moves or is told to: its own meshes ("[ent N]")
            draw.push((m, origin, if is_mover(class) { crate::ent_key(ei) } else { 0 }));
        }

        let mut batches: HashMap<usize, Batch> = HashMap::new();
        let mut pack = Packer::new(2048);
        let white = [1.0f32, 1.0];
        let mut skipped = (0, 0);
        let vertex = |i: usize| vx.v3(i * 12);
        // where each face's lightmap goes, planned first: tallest first, so a shelf's faces are about as tall as each
        // other (in the file's order a big map's wasted half its picture, and ran out of room)
        let mut planned: HashMap<usize, (usize, usize)> = HashMap::new();
        {
            let mut want = vec![];
            for &(m, _, _) in &draw {
                let mo = m * 48;
                let (first, count) = (md.i32(mo + 40)? as usize, md.i32(mo + 44)? as usize);
                for f in first..first + count {
                    let o = f * 56;
                    let tinfo = fc.i16(o + 10)?;
                    if tinfo < 0 { continue; }
                    let t = tinfo as usize * 72;
                    if ti.i32(t + 64)? & (SURF_SKY2D | SURF_SKY | SURF_TRIGGER | SURF_NODRAW | SURF_HINT | SURF_SKIP) != 0 { continue; }
                    let styles0 = fl.0.get(o + 16).copied().unwrap_or(255);
                    let lofs = fl.i32(o + 20)?;
                    let lsize = [fl.i32(o + 36)? as usize + 1, fl.i32(o + 40)? as usize + 1];
                    if lofs >= 0 && styles0 != 255 && lsize[0] <= 1024 && lsize[1] <= 1024 && (lofs as usize) + lsize[0] * lsize[1] * 4 <= light.len() {
                        want.push((f, lsize));
                    }
                }
            }
            want.sort_by(|a, b| b.1[1].cmp(&a.1[1]).then(b.1[0].cmp(&a.1[0])));
            for (f, lsize) in want {
                if let Some(at) = pack.place(lsize[0], lsize[1]) { planned.insert(f, at); }
            }
        }
        for (m, origin, ek) in draw {
            let mo = m * 48;
            let (first, count) = (md.i32(mo + 40)? as usize, md.i32(mo + 44)? as usize);
            for f in first..first + count {
                let o = f * 56;
                let plane = fc.u16(o)? as usize;
                let side = fc.0.get(o + 2).copied().unwrap_or(0);
                let (fe, ne) = (fc.i32(o + 4)? as usize, fc.i16(o + 8)? as usize);
                let tinfo = fc.i16(o + 10)?;
                let disp = fc.i16(o + 12)?;
                if tinfo < 0 { skipped.1 += 1; continue; }
                let t = tinfo as usize * 72;
                let flags = ti.i32(t + 64)?;
                let td = ti.i32(t + 68)?;
                if flags & (SURF_SKY2D | SURF_SKY | SURF_TRIGGER | SURF_NODRAW | SURF_HINT | SURF_SKIP) != 0 || td < 0 { skipped.1 += 1; continue; }
                let mat = td as usize;
                if let Some(mt) = materials.get(mat) {
                    let n = mt.name.as_str();
                    if n.starts_with("tools/") && !n.starts_with("tools/toolsblack") { skipped.1 += 1; continue; }
                }
                // its corners
                let mut poly = Vec::with_capacity(ne);
                for k in 0..ne {
                    let s = se.i32((fe + k) * 4)?;
                    let e = s.unsigned_abs() as usize * 4;
                    let vi = if s >= 0 { ed.u16(e)? } else { ed.u16(e + 2)? } as usize;
                    poly.push(add(vertex(vi)?, origin));
                }
                if poly.len() < 3 { continue; }
                let normal = p.v3(plane * 20)?;
                // (its plane already faces its way: planes come in pairs, each and its flip, and a face names its own;
                // "side" only says which of the pair that is. Turning it round again had turned a third of the map's
                // faces inside out: seen from behind, gone from the front)
                let _ = side;
                // in the 3D skybox: drawn round the map, as big as the sky camera makes it (not stood on)
                let mut in_sky = None;
                if let Some((lo, hi)) = sky_box {
                    let c = mul(poly.iter().fold([0.0; 3], |a, &v| add(a, v)), 1.0 / poly.len() as f32);
                    if (0..3).all(|a| c[a] >= lo[a] - 1.0 && c[a] <= hi[a] + 1.0) {
                        skipped.0 += 1;
                        match sky_cam { Some(cam) => in_sky = Some(cam), None => continue }
                    }
                }
                let (s, tv) = (ti.v3(t)?, ti.v3(t + 16)?);
                let (so, to) = (ti.f32(t + 12)?, ti.f32(t + 28)?);
                // its lightmap into the picture (style 0's: the first of a bumped face's four)
                let styles0 = fl.0.get(o + 16).copied().unwrap_or(255);
                let lofs = fl.i32(o + 20)?;
                let (lmin, lsize) = ([fl.i32(o + 28)? as f32, fl.i32(o + 32)? as f32], [fl.i32(o + 36)? as usize + 1, fl.i32(o + 40)? as usize + 1]);
                let mut rect = None;
                if lofs >= 0 && styles0 != 255 && lsize[0] <= 1024 && lsize[1] <= 1024 && (lofs as usize) + lsize[0] * lsize[1] * 4 <= light.len() {
                    if let Some(&(x, y)) = planned.get(&f) {
                        let l = &light[lofs as usize..];
                        pack.fill(x, y, lsize[0], lsize[1], |lx, ly| {
                            let q = (ly * lsize[0] + lx) * 4;
                            luxel(l[q], l[q + 1], l[q + 2], l[q + 3] as i8, exposure)
                        });
                        rect = Some((x as f32, y as f32));
                    }
                }
                let (ls, lt) = (ti.v3(t + 32)?, ti.v3(t + 48)?);
                let (lso, lto) = (ti.f32(t + 44)?, ti.f32(t + 60)?);
                let uv = |v: V3| {
                    let m = sub(v, origin);
                    let l = match rect {
                        Some((x, y)) => [
                            x + (dot(ls, m) + lso - lmin[0] + 0.5).clamp(0.5, lsize[0] as f32 - 0.5),
                            y + (dot(lt, m) + lto - lmin[1] + 0.5).clamp(0.5, lsize[1] as f32 - 0.5),
                        ],
                        None => white,
                    };
                    [dot(s, m) + so, dot(tv, m) + to, l[0], l[1]]
                };
                let b = batches.entry(if in_sky.is_some() { mat | crate::NOCOLLIDE | crate::SKY3D } else { mat | ek }).or_default();
                let place = |v: V3| match in_sky { Some((o, k)) => mul(sub(v, o), k), None => v };
                let mut tri = |a: V3, bb: V3, c: V3, ua: [f32; 4], ub: [f32; 4], uc: [f32; 4], fallback: V3| {
                    let (a, bb, c) = (place(a), place(bb), place(c));
                    // front faces counter-clockwise about the face's normal (glTF's way)
                    let mut n = cross(sub(bb, a), sub(c, a));
                    let len = dot(n, n).sqrt();
                    if len < 1e-6 { return; }
                    n = mul(n, 1.0 / len);
                    let (bb, c, ub, uc, n) = if dot(n, fallback) < 0.0 { (c, bb, uc, ub, mul(n, -1.0)) } else { (bb, c, ub, uc, n) };
                    b.pos.extend([a, bb, c]);
                    b.nrm.extend([n, n, n]);
                    b.uv.extend([[ua[0], ua[1]], [ub[0], ub[1]], [uc[0], uc[1]]]);
                    b.luv.extend([[ua[2], ua[3]], [ub[2], ub[3]], [uc[2], uc[3]]]);
                };
                if disp >= 0 && poly.len() == 4 {
                    // a displacement: a (2^power + 1)^2 grid over the face, each point moved by its own offset
                    let d = disp as usize * 176;
                    let start = di.v3(d)?;
                    let vstart = di.i32(d + 12)? as usize;
                    let power = di.i32(d + 20)?.clamp(1, 4) as u32;
                    let size = (1usize << power) + 1;
                    let first = (0..4).min_by(|&a, &b| {
                        let (da, db) = (sub(poly[a], add(start, origin)), sub(poly[b], add(start, origin)));
                        dot(da, da).partial_cmp(&dot(db, db)).unwrap()
                    }).unwrap();
                    let c: Vec<V3> = (0..4).map(|k| poly[(first + k) % 4]).collect();
                    let mut grid = Vec::with_capacity(size * size);
                    let mut flat = Vec::with_capacity(size * size);
                    for i in 0..size {
                        let fi = i as f32 / (size - 1) as f32;
                        let (a, bb) = (lerp(c[0], c[1], fi), lerp(c[3], c[2], fi));
                        for j in 0..size {
                            let base = lerp(a, bb, j as f32 / (size - 1) as f32);
                            let o = (vstart + i * size + j) * 20;
                            let (dvec, dist) = (dv.v3(o).unwrap_or([0.0; 3]), dv.f32(o + 12).unwrap_or(0.0));
                            grid.push(add(base, mul(dvec, dist)));
                            flat.push(base);
                        }
                    }
                    for i in 0..size - 1 {
                        for j in 0..size - 1 {
                            let (k0, k1, k2, k3) = (i * size + j, i * size + j + 1, (i + 1) * size + j, (i + 1) * size + j + 1);
                            tri(grid[k0], grid[k2], grid[k1], uv(flat[k0]), uv(flat[k2]), uv(flat[k1]), normal);
                            tri(grid[k1], grid[k2], grid[k3], uv(flat[k1]), uv(flat[k2]), uv(flat[k3]), normal);
                        }
                    }
                } else {
                    for k in 1..poly.len() - 1 {
                        tri(poly[0], poly[k], poly[k + 1], uv(poly[0]), uv(poly[k]), uv(poly[k + 1]), normal);
                    }
                }
            }
        }
        let pak = (lumps[40].ofs, lumps[40].len); // (a zip: its entries say how they're packed)
        // the models in it: static props, and prop_ entities with a model
        let mut props = lump(35).map(|g| static_props(data, &g)).unwrap_or_default();
        for (ei, e) in entities.iter().enumerate() {
            let class = e.get("classname").map(String::as_str).unwrap_or("");
            let Some(model) = e.get("model").filter(|m| m.to_ascii_lowercase().ends_with(".mdl")) else { continue };
            if !class.starts_with("prop_") { continue; }
            // a door, or a prop that's named or hangs from something (told to move, show, hide): its own meshes
            let ent = (class == "prop_door_rotating" || class == "prop_dynamic" && (e.contains_key("targetname") || e.contains_key("parentname"))).then_some(ei);
            props.push(Prop {
                model: model.replace('\\', "/").to_ascii_lowercase(),
                origin: e.get("origin").and_then(|o| vec3_of(o)).unwrap_or([0.0; 3]),
                angles: e.get("angles").and_then(|o| vec3_of(o)).unwrap_or([0.0; 3]),
                skin: e.get("skin").and_then(|s| s.parse().ok()).unwrap_or(0),
                solid: e.get("solid").map(|s| s != "0").unwrap_or(true),
                scale: e.get("modelscale").and_then(|s| s.parse().ok()).filter(|&v: &f32| v > 0.01).unwrap_or(1.0),
                ent,
            });
        }
        // the models' light: the map's average luxel, in a block of its own
        let mut prop_luv = [1.0f32, 1.0];
        if !light.is_empty() && light.len() >= 4 {
            let (mut sum, mut n) = ([0f64; 3], 0f64);
            for q in (0..light.len() / 4).step_by(7) {
                let c = luxel(light[q * 4], light[q * 4 + 1], light[q * 4 + 2], light[q * 4 + 3] as i8, exposure);
                for a in 0..3 { sum[a] += c[a] as f64; }
                n += 1.0;
            }
            if n > 0.0 {
                let avg = [(sum[0] / n) as u8, (sum[1] / n) as u8, (sum[2] / n) as u8];
                // a little brighter than the average (it counts the dark undersides too)
                let lift = |c: u8| ((c as f32 * 1.3).min(150.0)) as u8;
                if let Some((x, y)) = pack.place(2, 2) {
                    pack.fill(x, y, 2, 2, |_, _| [lift(avg[0]), lift(avg[1]), lift(avg[2])]);
                    prop_luv = [x as f32 + 1.0, y as f32 + 1.0];
                }
            }
        }
        let lightmap = (pack.h > 2 && !light.is_empty()).then(|| Lightmap { w: pack.w, h: pack.h, rgba: std::mem::take(&mut pack.px) });
        let sky = match (sky_box, sky_cam) { (Some((lo, hi)), Some((o, k))) => Some((lo, hi, o, k)), _ => None };
        let model_bounds = (0..md.0.len() / 48).map(|m| (md.v3(m * 48).unwrap_or([0.0; 3]), md.v3(m * 48 + 12).unwrap_or([0.0; 3]))).collect();
        Ok(Map { lightmap, version, materials, batches, entities, entities_kv, model_bounds, skipped, pak, props, prop_luv, sky, exposure })
    }

    /// Where a player starts (its origin, yaw in degrees), if the map says
    pub fn spawn(&self) -> Option<(V3, f32)> {
        for class in ["info_player_start", "info_player_deathmatch", "info_player_combine", "info_player_rebel", "info_player_counterterrorist", "info_player_terrorist", "gmod_player_start"] {
            for e in &self.entities {
                if e.get("classname").map(|c| c == class).unwrap_or(false) {
                    if let Some(o) = e.get("origin").and_then(|o| vec3_of(o)) {
                        let yaw = e.get("angles").and_then(|a| vec3_of(a)).map(|a| a[1]).unwrap_or(0.0);
                        return Some((o, yaw));
                    }
                }
            }
        }
        None
    }
}
