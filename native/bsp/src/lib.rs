//! Garry's Mod (Source engine) maps for Delirium Sandbox: a .bsp from your own game read here and written out as a
//! .glb the sandbox loads like any other map (GlbScene), cached beside its other files. Nothing of the game is in
//! here; the map is yours.
//!
//! The map's shape (brushes, brush entities, displacements), its textures (its pakfile's, your game's packs'), where
//! you start (Spawn). Water is drawn but not stood on ([nocollide] in its name).

pub mod bsp;
pub mod content;
pub mod vpk;
pub mod vtf;

use bsp::{Map, V3};
use std::ffi::{CStr, c_char};
use std::sync::Mutex;

/// Source's inches to metres
pub const SCALE: f32 = 0.0254;

// Source (X forward, Y left, Z up, inches) to glTF (Y up, right-handed, metres)
fn gl(v: V3) -> V3 { [v[0] * SCALE, v[2] * SCALE, -v[1] * SCALE] }
fn gln(v: V3) -> V3 { [v[0], v[2], -v[1]] }

/// The map as a .glb: a mesh a material, its nodes under one root, a "Spawn" node where you start.
/// A material's picture for the .glb: its PNG, and how it's drawn (0 opaque, 1 cut out, 2 see-through)
pub struct Tex {
    pub png: std::rc::Rc<Vec<u8>>, // shared by the materials with the same picture (a map's cubemap copies)
    pub mode: u8,
}

pub fn to_glb(map: &Map, tex: &std::collections::HashMap<usize, Tex>) -> Vec<u8> {
    let mut bin: Vec<u8> = vec![];
    let mut accessors = vec![];
    let mut views = vec![];
    let mut meshes = vec![];
    let mut materials = vec![];
    let mut nodes = vec![];
    let mut children = vec![];
    let mut images = vec![];
    let mut textures = vec![];
    let mut seen: Vec<(*const Vec<u8>, usize)> = vec![]; // a picture once, its texture's number
    let mut keys: Vec<&usize> = map.batches.keys().collect();
    keys.sort();
    let push = |bytes: &[u8], target: u32, bin: &mut Vec<u8>, views: &mut Vec<String>| -> usize {
        while bin.len() % 4 != 0 { bin.push(0); }
        let at = bin.len();
        bin.extend_from_slice(bytes);
        views.push(format!(r#"{{"buffer":0,"byteOffset":{at},"byteLength":{},"target":{target}}}"#, bytes.len()));
        views.len() - 1
    };
    for &k in keys {
        let b = &map.batches[&k];
        if b.pos.is_empty() { continue; }
        let m = &map.materials.get(k);
        let name = m.map(|m| m.name.clone()).unwrap_or_else(|| format!("material{k}"));
        let water = name.contains("water") && !name.contains("waterfall");
        let glass = name.contains("glass") || name.contains("window");
        let (w, h) = m.map(|m| (m.width as f32, m.height as f32)).unwrap_or((64.0, 64.0));
        let n = b.pos.len();
        let mut lo = [f32::MAX; 3];
        let mut hi = [f32::MIN; 3];
        let mut pbytes = Vec::with_capacity(n * 12);
        let mut nbytes = Vec::with_capacity(n * 12);
        let mut ubytes = Vec::with_capacity(n * 8);
        for i in 0..n {
            let p = gl(b.pos[i]);
            for a in 0..3 { lo[a] = lo[a].min(p[a]); hi[a] = hi[a].max(p[a]); }
            for v in p { pbytes.extend_from_slice(&v.to_le_bytes()); }
            for v in gln(b.nrm[i]) { nbytes.extend_from_slice(&v.to_le_bytes()); }
            for v in [b.uv[i][0] / w, b.uv[i][1] / h] { ubytes.extend_from_slice(&v.to_le_bytes()); }
        }
        let mut ibytes = Vec::with_capacity(n * 4);
        for i in 0..n as u32 { ibytes.extend_from_slice(&i.to_le_bytes()); }
        let vp = push(&pbytes, 34962, &mut bin, &mut views);
        let vn = push(&nbytes, 34962, &mut bin, &mut views);
        let vu = push(&ubytes, 34962, &mut bin, &mut views);
        let vi = push(&ibytes, 34963, &mut bin, &mut views);
        let a0 = accessors.len();
        accessors.push(format!(r#"{{"bufferView":{vp},"componentType":5126,"count":{n},"type":"VEC3","min":[{},{},{}],"max":[{},{},{}]}}"#, lo[0], lo[1], lo[2], hi[0], hi[1], hi[2]));
        accessors.push(format!(r#"{{"bufferView":{vn},"componentType":5126,"count":{n},"type":"VEC3"}}"#));
        accessors.push(format!(r#"{{"bufferView":{vu},"componentType":5126,"count":{n},"type":"VEC2"}}"#));
        accessors.push(format!(r#"{{"bufferView":{vi},"componentType":5125,"count":{n},"type":"SCALAR"}}"#));
        let c = m.map(|m| m.color).unwrap_or([0.5; 3]);
        let mi = materials.len();
        if let Some(t) = tex.get(&k) {
            // its own texture (from the map's pakfile or your game's packs)
            let key = std::rc::Rc::as_ptr(&t.png);
            let ti = match seen.iter().find(|s| s.0 == key) {
                Some(s) => s.1,
                None => {
                    let vimg = push(&t.png, 0, &mut bin, &mut views);
                    if let Some(v) = views.last_mut() { *v = v.replace(r#","target":0"#, ""); }
                    images.push(format!(r#"{{"bufferView":{vimg},"mimeType":"image/png"}}"#));
                    textures.push(format!(r#"{{"source":{}}}"#, images.len() - 1));
                    seen.push((key, textures.len() - 1));
                    textures.len() - 1
                }
            };
            let alpha = if water && t.mode == 0 { 0.6 } else { 1.0 };
            let mode = match (t.mode, alpha < 1.0) { (2, _) | (_, true) => r#","alphaMode":"BLEND""#, (1, _) => r#","alphaMode":"MASK","alphaCutoff":0.5"#, _ => "" };
            materials.push(format!(
                r#"{{"name":{},"pbrMetallicRoughness":{{"baseColorTexture":{{"index":{ti}}},"baseColorFactor":[1,1,1,{alpha}],"metallicFactor":0,"roughnessFactor":0.9}}{mode}}}"#,
                json(&name)
            ));
        } else {
            let alpha = if water { 0.6 } else if glass { 0.35 } else { 1.0 };
            let c = if water { [c[0] * 0.6, c[1] * 0.8, c[2].max(0.3)] } else { c };
            materials.push(format!(
                r#"{{"name":{},"pbrMetallicRoughness":{{"baseColorFactor":[{},{},{},{alpha}],"metallicFactor":0,"roughnessFactor":0.9}}{}}}"#,
                json(&name), c[0].clamp(0.0, 1.0), c[1].clamp(0.0, 1.0), c[2].clamp(0.0, 1.0), if alpha < 1.0 { r#","alphaMode":"BLEND""# } else { "" }
            ));
        }
        let mesh_name = if water { format!("{name} [nocollide]") } else { name.clone() };
        meshes.push(format!(r#"{{"name":{},"primitives":[{{"attributes":{{"POSITION":{a0},"NORMAL":{},"TEXCOORD_0":{}}},"indices":{},"material":{mi}}}]}}"#, json(&mesh_name), a0 + 1, a0 + 2, a0 + 3));
        nodes.push(format!(r#"{{"name":{},"mesh":{}}}"#, json(&mesh_name), meshes.len() - 1));
        children.push(nodes.len());
    }
    if let Some((at, yaw)) = map.spawn() {
        // a little up: the origin is the player's feet, sometimes a few units into the floor
        let p = gl([at[0], at[1], at[2] + 4.0]);
        let r = (yaw.to_radians()) * 0.5; // about up (glTF Y: Source's turn left is the same way)
        nodes.push(format!(r#"{{"name":"Spawn","translation":[{},{},{}],"rotation":[0,{},0,{}]}}"#, p[0], p[1], p[2], r.sin(), r.cos()));
        children.push(nodes.len());
    }
    let kids: Vec<String> = children.iter().map(|c| c.to_string()).collect();
    nodes.insert(0, format!(r#"{{"name":"bsp","children":[{}]}}"#, kids.join(",")));
    while bin.len() % 4 != 0 { bin.push(0); }
    let json_text = format!(
        r#"{{"asset":{{"version":"2.0","generator":"Delirium Sandbox (Source map)"}},"scene":0,"scenes":[{{"nodes":[0]}}],"nodes":[{}],"meshes":[{}],"materials":[{}],{}"accessors":[{}],"bufferViews":[{}],"buffers":[{{"byteLength":{}}}]}}"#,
        nodes.join(","), meshes.join(","), materials.join(","),
        if images.is_empty() { String::new() } else { format!(r#""images":[{}],"textures":[{}],"#, images.join(","), textures.join(",")) },
        accessors.join(","), views.join(","), bin.len()
    );
    let mut j = json_text.into_bytes();
    while j.len() % 4 != 0 { j.push(b' '); }
    let total = 12 + 8 + j.len() + 8 + bin.len();
    let mut out = Vec::with_capacity(total);
    out.extend_from_slice(b"glTF");
    out.extend_from_slice(&2u32.to_le_bytes());
    out.extend_from_slice(&(total as u32).to_le_bytes());
    out.extend_from_slice(&(j.len() as u32).to_le_bytes());
    out.extend_from_slice(b"JSON");
    out.extend_from_slice(&j);
    out.extend_from_slice(&(bin.len() as u32).to_le_bytes());
    out.extend_from_slice(b"BIN\0");
    out.extend_from_slice(&bin);
    out
}

fn json(s: &str) -> String {
    let mut o = String::from("\"");
    for c in s.chars() {
        match c {
            '"' => o.push_str("\\\""),
            '\\' => o.push_str("\\\\"),
            c if (c as u32) < 0x20 => o.push_str(&format!("\\u{:04x}", c as u32)),
            c => o.push(c),
        }
    }
    o.push('"');
    o
}

/// A .bsp file to a .glb file, its textures looked for in its pakfile and under `roots` (your game's packs and
/// folders): a line about it (triangles, materials, textures found), or the reason it couldn't be.
pub fn convert(bsp_path: &str, glb_path: &str, roots: &[std::path::PathBuf], max_side: usize) -> Result<String, String> {
    let data = std::fs::read(bsp_path).map_err(|e| format!("{bsp_path}: {e}"))?;
    let map = Map::read(&data)?;
    let pak = data.get(map.pak.0..map.pak.0 + map.pak.1).unwrap_or_default();
    let content = content::Content::new(pak, roots);
    let mut tex = std::collections::HashMap::new();
    let mut missing = vec![];
    // one decode a texture (materials share them)
    let mut done: std::collections::HashMap<String, Option<std::rc::Rc<Vec<u8>>>> = std::collections::HashMap::new();
    for &k in map.batches.keys() {
        let Some(m) = map.materials.get(k) else { continue };
        let vmt = content.material(&m.name);
        let base = vmt.as_ref().and_then(|v| v.base.clone()).unwrap_or_else(|| m.name.clone());
        let base = base.replace('\\', "/").to_ascii_lowercase();
        let pic = done.entry(base.clone()).or_insert_with(|| {
            content.find(&format!("materials/{}.vtf", base.trim_end_matches(".vtf"))).and_then(|v| vtf::decode(&v, max_side).ok()).map(|(w, h, rgba)| std::rc::Rc::new(content::png(w, h, &rgba)))
        });
        match pic {
            Some(png) => {
                let mode = match &vmt { Some(v) if v.translucent => 2, Some(v) if v.alphatest => 1, _ => 0 };
                tex.insert(k, Tex { png: png.clone(), mode });
            }
            None => missing.push(m.name.clone()),
        }
    }
    let glb = to_glb(&map, &tex);
    std::fs::write(glb_path, &glb).map_err(|e| format!("{glb_path}: {e}"))?;
    let tris: usize = map.batches.values().map(|b| b.pos.len() / 3).sum();
    missing.sort();
    Ok(format!(
        "v{} {} triangles, {} materials, {} textured ({} packs{}); {} faces of the 3D skybox and {} tool faces left out{}",
        map.version, tris, map.batches.len(), tex.len(), content.packs(),
        if content.missing_archives > 0 { format!(", {} of their archives missing", content.missing_archives) } else { String::new() },
        map.skipped.0, map.skipped.1,
        if missing.is_empty() { String::new() } else { format!("; no texture for {} (e.g. {})", missing.len(), missing.iter().take(3).cloned().collect::<Vec<_>>().join(", ")) }
    ))
}

static ERR: Mutex<String> = Mutex::new(String::new());

/// For C# (Models.cs): `bsp` (a .bsp) written to `glb` (a .glb), its textures from its pakfile and the packs and
/// folders under `roots` ('|' between them), no bigger than `max_side`. 0, or -1 (dbsp_error says why); `dbsp_error`
/// afterwards has the line about it either way.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dbsp_convert(bsp: *const c_char, glb: *const c_char, roots: *const c_char, max_side: i32) -> i32 {
    let text = |p: *const c_char| if p.is_null() { None } else { unsafe { CStr::from_ptr(p) }.to_str().ok().map(str::to_owned) };
    let (Some(b), Some(g)) = (text(bsp), text(glb)) else {
        *ERR.lock().unwrap() = "no path".into();
        return -1;
    };
    let roots: Vec<std::path::PathBuf> = text(roots).unwrap_or_default().split('|').filter(|s| !s.is_empty()).map(std::path::PathBuf::from).collect();
    match std::panic::catch_unwind(|| convert(&b, &g, &roots, max_side.clamp(16, 4096) as usize)) {
        Ok(Ok(s)) => { *ERR.lock().unwrap() = s; 0 }
        Ok(Err(e)) => { *ERR.lock().unwrap() = e; -1 }
        Err(_) => { *ERR.lock().unwrap() = "the map reader failed (a bug: tell Claude which map)".into(); -1 }
    }
}

/// The last line (`dbsp_convert`'s) into `out` (`cap` bytes, 0-terminated); its length.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dbsp_error(out: *mut u8, cap: i32) -> i32 {
    let e = ERR.lock().unwrap();
    let b = e.as_bytes();
    let n = b.len().min((cap.max(1) - 1) as usize);
    if !out.is_null() {
        unsafe {
            std::ptr::copy_nonoverlapping(b.as_ptr(), out, n);
            *out.add(n) = 0;
        }
    }
    n as i32
}
