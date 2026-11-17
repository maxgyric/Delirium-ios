//! Garry's Mod (Source engine) maps for Delirium Sandbox: a .bsp from your own game read here and written out as a
//! .glb the sandbox loads like any other map (GlbScene), cached beside its other files. Nothing of the game is in
//! here; the map is yours.
//!
//! Stage 1: the map's shape (brushes, brush entities, displacements), each material's average colour, its texture
//! coordinates, where you start (Spawn). Water is drawn but not stood on ([nocollide] in its name).

pub mod bsp;

use bsp::{Map, V3};
use std::ffi::{CStr, c_char};
use std::sync::Mutex;

/// Source's inches to metres
pub const SCALE: f32 = 0.0254;

// Source (X forward, Y left, Z up, inches) to glTF (Y up, right-handed, metres)
fn gl(v: V3) -> V3 { [v[0] * SCALE, v[2] * SCALE, -v[1] * SCALE] }
fn gln(v: V3) -> V3 { [v[0], v[2], -v[1]] }

/// The map as a .glb: a mesh a material, its nodes under one root, a "Spawn" node where you start.
pub fn to_glb(map: &Map) -> Vec<u8> {
    let mut bin: Vec<u8> = vec![];
    let mut accessors = vec![];
    let mut views = vec![];
    let mut meshes = vec![];
    let mut materials = vec![];
    let mut nodes = vec![];
    let mut children = vec![];
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
        let alpha = if water { 0.6 } else if glass { 0.35 } else { 1.0 };
        let c = if water { [c[0] * 0.6, c[1] * 0.8, c[2].max(0.3)] } else { c };
        let mi = materials.len();
        materials.push(format!(
            r#"{{"name":{},"pbrMetallicRoughness":{{"baseColorFactor":[{},{},{},{alpha}],"metallicFactor":0,"roughnessFactor":0.9}}{}}}"#,
            json(&name), c[0].clamp(0.0, 1.0), c[1].clamp(0.0, 1.0), c[2].clamp(0.0, 1.0), if alpha < 1.0 { r#","alphaMode":"BLEND""# } else { "" }
        ));
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
        r#"{{"asset":{{"version":"2.0","generator":"Delirium Sandbox (Source map)"}},"scene":0,"scenes":[{{"nodes":[0]}}],"nodes":[{}],"meshes":[{}],"materials":[{}],"accessors":[{}],"bufferViews":[{}],"buffers":[{{"byteLength":{}}}]}}"#,
        nodes.join(","), meshes.join(","), materials.join(","), accessors.join(","), views.join(","), bin.len()
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

/// A .bsp file to a .glb file: a line about it (triangles, materials), or the reason it couldn't be.
pub fn convert(bsp_path: &str, glb_path: &str) -> Result<String, String> {
    let data = std::fs::read(bsp_path).map_err(|e| format!("{bsp_path}: {e}"))?;
    let map = Map::read(&data)?;
    let glb = to_glb(&map);
    std::fs::write(glb_path, &glb).map_err(|e| format!("{glb_path}: {e}"))?;
    let tris: usize = map.batches.values().map(|b| b.pos.len() / 3).sum();
    Ok(format!("v{} {} triangles, {} materials, {} faces of the 3D skybox and {} tool faces left out", map.version, tris, map.batches.len(), map.skipped.0, map.skipped.1))
}

static ERR: Mutex<String> = Mutex::new(String::new());

/// For C# (Models.cs): `bsp` (a .bsp) written to `glb` (a .glb). 0, or -1 (dbsp_error says why); `dbsp_error`
/// afterwards has the line about it either way.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dbsp_convert(bsp: *const c_char, glb: *const c_char) -> i32 {
    let text = |p: *const c_char| if p.is_null() { None } else { unsafe { CStr::from_ptr(p) }.to_str().ok().map(str::to_owned) };
    let (Some(b), Some(g)) = (text(bsp), text(glb)) else {
        *ERR.lock().unwrap() = "no path".into();
        return -1;
    };
    match std::panic::catch_unwind(|| convert(&b, &g)) {
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
