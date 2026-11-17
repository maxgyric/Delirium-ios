//! Garry's Mod (Source engine) maps for Delirium Sandbox: a .bsp from your own game read here and written out as a
//! .glb the sandbox loads like any other map (GlbScene), cached beside its other files. Nothing of the game is in
//! here; the map is yours.
//!
//! The map's shape (brushes, brush entities, displacements), its textures (its pakfile's, your game's packs'), where
//! you start (Spawn). Water is drawn but not stood on ([nocollide] in its name).

pub mod bsp;
pub mod content;
pub mod gma;
pub mod player;
pub mod sky;
pub mod studio;
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
    pub tint: [f32; 3], // the material's $color
    pub two_sided: bool, // $nocull
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
    // the baked light: one picture, every material's "lightmap" (extras) and second texture coordinates
    let mut lm_tex = None;
    if let Some(l) = &map.lightmap {
        let png = content::png(l.w, l.h, &l.rgba);
        while bin.len() % 4 != 0 { bin.push(0); }
        let at = bin.len();
        bin.extend_from_slice(&png);
        views.push(format!(r#"{{"buffer":0,"byteOffset":{at},"byteLength":{}}}"#, png.len()));
        images.push(format!(r#"{{"bufferView":{},"mimeType":"image/png","name":"lightmap"}}"#, views.len() - 1));
        textures.push(format!(r#"{{"source":{}}}"#, images.len() - 1));
        lm_tex = Some((textures.len() - 1, l.w as f32, l.h as f32));
    }
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
        let nocollide = k & NOCOLLIDE != 0;
        let key = k;
        let sky3d = k & SKY3D != 0;
        let k = k & !(NOCOLLIDE | SKY3D);
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
        let mut lbytes = Vec::with_capacity(n * 8);
        for i in 0..n {
            let p = gl(b.pos[i]);
            for a in 0..3 { lo[a] = lo[a].min(p[a]); hi[a] = hi[a].max(p[a]); }
            for v in p { pbytes.extend_from_slice(&v.to_le_bytes()); }
            for v in gln(b.nrm[i]) { nbytes.extend_from_slice(&v.to_le_bytes()); }
            for v in [b.uv[i][0] / w, b.uv[i][1] / h] { ubytes.extend_from_slice(&v.to_le_bytes()); }
            if let Some((_, lw, lh)) = lm_tex {
                let l = b.luv.get(i).copied().unwrap_or([1.0, 1.0]);
                for v in [l[0] / lw, l[1] / lh] { lbytes.extend_from_slice(&v.to_le_bytes()); }
            }
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
        let luv_acc = if lm_tex.is_some() {
            let vl = push(&lbytes, 34962, &mut bin, &mut views);
            accessors.push(format!(r#"{{"bufferView":{vl},"componentType":5126,"count":{n},"type":"VEC2"}}"#));
            format!(r#","TEXCOORD_1":{}"#, accessors.len() - 1)
        } else { String::new() };
        let lm_extra = match lm_tex { Some((t, _, _)) => format!(r#","extras":{{"lightmap":{t}}}"#), None => String::new() };
        let c = m.map(|m| m.color).unwrap_or([0.5; 3]);
        let mi = materials.len();
        // (its texture's under its own key: the 3D skybox's and water's with NOCOLLIDE in it)
        if let Some(t) = tex.get(&key).or_else(|| tex.get(&k)) {
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
                r#"{{"name":{},"pbrMetallicRoughness":{{"baseColorTexture":{{"index":{ti}}},"baseColorFactor":[{},{},{},{alpha}],"metallicFactor":0,"roughnessFactor":0.9}}{mode}{}{lm_extra}}}"#,
                json(&name), t.tint[0], t.tint[1], t.tint[2], if t.two_sided { r#","doubleSided":true"# } else { "" }
            ));
        } else {
            let alpha = if water { 0.6 } else if glass { 0.35 } else { 1.0 };
            let c = if water { [c[0] * 0.6, c[1] * 0.8, c[2].max(0.3)] } else { c };
            materials.push(format!(
                r#"{{"name":{},"pbrMetallicRoughness":{{"baseColorFactor":[{},{},{},{alpha}],"metallicFactor":0,"roughnessFactor":0.9}}{}{lm_extra}}}"#,
                json(&name), c[0].clamp(0.0, 1.0), c[1].clamp(0.0, 1.0), c[2].clamp(0.0, 1.0), if alpha < 1.0 { r#","alphaMode":"BLEND""# } else { "" }
            ));
        }
        let mesh_name = if sky3d { format!("{name} [sky3d] [nocollide]") } else if water || nocollide { format!("{name} [nocollide]") } else { name.clone() };
        meshes.push(format!(r#"{{"name":{},"primitives":[{{"attributes":{{"POSITION":{a0},"NORMAL":{},"TEXCOORD_0":{}{luv_acc}}},"indices":{},"material":{mi}}}]}}"#, json(&mesh_name), a0 + 1, a0 + 2, a0 + 3));
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

/// A batch key's flag: the triangles of models that aren't solid (drawn, not stood on)
pub const NOCOLLIDE: usize = 1 << 24;
/// (a batch key's flag: in the 3D skybox, drawn by a camera of its own behind the map, as Source does)
pub const SKY3D: usize = 1 << 25;

/// The map's models put in its batches (a material each of theirs, after the map's): how many placed, and which
/// models weren't found or read
fn add_props(map: &mut Map, content: &content::Content) -> (usize, Vec<String>) {
    use std::collections::HashMap;
    let props = std::mem::take(&mut map.props);
    let mut models: HashMap<String, Option<studio::Model>> = HashMap::new();
    let mut mats: HashMap<String, usize> = HashMap::new();
    let mut placed = 0;
    let mut missing = vec![];
    let find = |p: &str| content.find(p);
    let exists = |p: &str| content.find(p).is_some();
    for p in &props {
        let model = models.entry(p.model.clone()).or_insert_with(|| match studio::Model::read(&p.model, &find, &exists) {
            Ok(m) => Some(m),
            Err(_) => None,
        });
        let Some(model) = model.as_ref() else {
            if !missing.contains(&p.model) { missing.push(p.model.clone()); }
            continue;
        };
        let r = studio::angle_matrix(p.angles);
        // one in the 3D skybox: round the map, that much bigger, not stood on
        let sky = map.sky.filter(|(lo, hi, _, _)| (0..3).all(|a| p.origin[a] >= lo[a] - 1.0 && p.origin[a] <= hi[a] + 1.0));
        let (origin, scale, solid) = match sky {
            Some((_, _, o, k)) => ([(p.origin[0] - o[0]) * k, (p.origin[1] - o[1]) * k, (p.origin[2] - o[2]) * k], p.scale * k, false),
            None => (p.origin, p.scale, p.solid),
        };
        let flags = if sky.is_some() { NOCOLLIDE | SKY3D } else if solid { 0 } else { NOCOLLIDE };
        let skin = model.skins.get(p.skin).or(model.skins.first());
        for mesh in &model.meshes {
            let ti = skin.and_then(|s| s.get(mesh.material).copied()).unwrap_or(mesh.material);
            let Some(name) = model.materials.get(ti) else { continue };
            let mi = *mats.entry(name.clone()).or_insert_with(|| {
                map.materials.push(bsp::Material { name: name.clone(), color: [0.7; 3], width: 1, height: 1 });
                map.materials.len() - 1
            });
            let key = mi | flags;
            let luv = map.prop_luv;
            let b = map.batches.entry(key).or_default();
            for t in &mesh.tris {
                let v = [&model.vertices[t[0] as usize], &model.vertices[t[1] as usize], &model.vertices[t[2] as usize]];
                let pos: Vec<bsp::V3> = v.iter().map(|v| {
                    let q = studio::rotate(&r, [v.pos[0] * scale, v.pos[1] * scale, v.pos[2] * scale]);
                    [q[0] + origin[0], q[1] + origin[1], q[2] + origin[2]]
                }).collect();
                let nrm: Vec<bsp::V3> = v.iter().map(|v| studio::rotate(&r, v.nrm)).collect();
                // front faces counter-clockwise (glTF's), by the vertices' own normals
                let face = bsp::cross([pos[1][0] - pos[0][0], pos[1][1] - pos[0][1], pos[1][2] - pos[0][2]], [pos[2][0] - pos[0][0], pos[2][1] - pos[0][1], pos[2][2] - pos[0][2]]);
                let avg = [nrm[0][0] + nrm[1][0] + nrm[2][0], nrm[0][1] + nrm[1][1] + nrm[2][1], nrm[0][2] + nrm[1][2] + nrm[2][2]];
                let order = if bsp::dot(face, avg) < 0.0 { [0, 2, 1] } else { [0, 1, 2] };
                for &i in &order {
                    b.pos.push(pos[i]);
                    b.nrm.push(nrm[i]);
                    b.uv.push(v[i].uv);
                    b.luv.push(luv);
                }
            }
        }
        placed += 1;
    }
    (placed, missing)
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
    // a Workshop addon (.gma, maybe LZMA-packed): the map asked for ("addon.gma#maps/x.bsp") or its first, its own
    // files first for its textures
    let (bsp_path, pick) = match bsp_path.split_once('#') { Some((a, b)) => (a, Some(b.to_ascii_lowercase())), None => (bsp_path, None) };
    let (data, gma, from) = if bsp_path.to_ascii_lowercase().ends_with(".gma") {
        let g = gma::Gma::open(std::path::Path::new(bsp_path))?;
        let maps = g.maps();
        let chosen = pick.as_ref().and_then(|p| maps.iter().find(|m| *m == p || m.trim_start_matches("maps/").trim_end_matches(".bsp") == p.as_str())).or(maps.first());
        let Some(m) = chosen else {
            let models = g.order.iter().filter(|p| p.ends_with(".mdl")).count();
            return Err(if models > 0 { format!("\"{}\" has no map ({models} models: player models aren't read yet)", g.name) } else { format!("\"{}\" has no map in it", g.name) });
        };
        let d = g.read(m).unwrap_or_default();
        let from = format!("{} ({}{})", m.trim_start_matches("maps/"), g.name, if maps.len() > 1 { format!(", one of its {} maps", maps.len()) } else { String::new() });
        (d, Some(g), from)
    } else {
        (std::fs::read(bsp_path).map_err(|e| format!("{bsp_path}: {e}"))?, None, String::new())
    };
    let mut map = Map::read(&data)?;
    let pak = data.get(map.pak.0..map.pak.0 + map.pak.1).unwrap_or_default();
    let content = content::Content::new(pak, roots, gma);
    let (placed, unplaced) = add_props(&mut map, &content);
    let mut tex = std::collections::HashMap::new();
    let mut missing = vec![];
    // one decode a texture (materials share them)
    let mut done: std::collections::HashMap<String, Option<std::rc::Rc<Vec<u8>>>> = std::collections::HashMap::new();
    let mut water: Vec<(usize, [f32; 3])> = vec![];
    for &key in map.batches.keys() {
        let k = key & !(NOCOLLIDE | SKY3D);
        let Some(m) = map.materials.get(k) else { continue };
        let vmt = content.material(&m.name);
        // water: its colour under the surface (it has no picture of its own)
        if let Some(f) = vmt.as_ref().filter(|v| v.shader.contains("water")).and_then(|v| v.fog) { water.push((k, f)); }
        let base = vmt.as_ref().and_then(|v| v.base.clone()).unwrap_or_else(|| m.name.clone());
        let base = base.replace('\\', "/").to_ascii_lowercase();
        let pic = done.entry(base.clone()).or_insert_with(|| {
            content.find(&format!("materials/{}.vtf", base.trim_end_matches(".vtf"))).and_then(|v| vtf::decode(&v, max_side).ok()).map(|(w, h, rgba)| std::rc::Rc::new(content::png(w, h, &rgba)))
        });
        match pic {
            Some(png) => {
                let mode = match &vmt { Some(v) if v.translucent => 2, Some(v) if v.alphatest => 1, _ => 0 };
                let tint = vmt.as_ref().map(|v| v.tint).unwrap_or([1.0; 3]);
                let two_sided = vmt.as_ref().map(|v| v.nocull).unwrap_or(false);
                tex.insert(key, Tex { png: png.clone(), mode, tint, two_sided });
            }
            None => if key == k { missing.push(m.name.clone()) },
        }
    }
    for (k, c) in water { if let Some(m) = map.materials.get_mut(k) { m.color = c; } }
    let glb = to_glb(&map, &tex);
    std::fs::write(glb_path, &glb).map_err(|e| format!("{glb_path}: {e}"))?;
    // its sky, fog, sun: beside the .glb (".env": key=values; ".sky.png": the panorama)
    let mut env = sky::environment(&map.entities, map.exposure);
    let mut sky_name = String::new();
    if let Some((w, h, rgba, name)) = sky::panorama(&content, &map.entities, 2048) {
        let png_path = format!("{glb_path}.sky.png");
        if std::fs::write(&png_path, content::png(w, h, &rgba)).is_ok() {
            env.push_str(&format!("\nsky={png_path}"));
            sky_name = name;
        }
    }
    let _ = std::fs::write(format!("{glb_path}.env"), &env);
    let tris: usize = map.batches.values().map(|b| b.pos.len() / 3).sum();
    missing.sort();
    Ok(format!(
        "{}v{}, light x{:.1}, {} triangles, {} materials, {} textured ({} packs{}); {} models placed{}; {}{} faces of the 3D skybox drawn round it, {} tool faces left out{}",
        if from.is_empty() { String::new() } else { format!("{from}: ") }, map.version, map.exposure, tris, map.batches.len(), tex.len(), content.packs(),
        if content.missing_archives > 0 { format!(", {} of their archives missing", content.missing_archives) } else { String::new() },
        placed,
        if unplaced.is_empty() { String::new() } else { format!(" ({} models not found, e.g. {})", unplaced.len(), unplaced.iter().take(2).cloned().collect::<Vec<_>>().join(", ")) },
        if sky_name.is_empty() { String::new() } else { format!("sky {sky_name}; ") },
        map.skipped.0, map.skipped.1,
        if missing.is_empty() { String::new() } else { format!("; no texture for {} (e.g. {})", missing.len(), missing.iter().take(3).cloned().collect::<Vec<_>>().join(", ")) }
    ))
}

static ERR: Mutex<String> = Mutex::new(String::new());

/// For C# (the files list): an addon's (.gma) maps, '\n' between them ("maps/x.bsp"), into `out` (`cap` bytes,
/// 0-terminated): how many maps, or -1 (dbsp_error says why).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dgma_maps(gma: *const c_char, out: *mut u8, cap: i32) -> i32 {
    let p = if gma.is_null() { None } else { unsafe { CStr::from_ptr(gma) }.to_str().ok().map(str::to_owned) };
    let Some(p) = p else { return -1 };
    match std::panic::catch_unwind(|| gma::Gma::open(std::path::Path::new(&p)).map(|g| g.maps())) {
        Ok(Ok(maps)) => {
            let text = maps.join("\n");
            let b = text.as_bytes();
            let n = b.len().min((cap.max(1) - 1) as usize);
            if !out.is_null() {
                unsafe {
                    std::ptr::copy_nonoverlapping(b.as_ptr(), out, n);
                    *out.add(n) = 0;
                }
            }
            maps.len() as i32
        }
        Ok(Err(e)) => { *ERR.lock().unwrap() = e; -1 }
        Err(_) => -1,
    }
}

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

/// For C# (the files list): an addon's player models, "path|name" a line, into `out` (`cap` bytes, 0-terminated):
/// how many, or -1.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dgma_models(gma: *const c_char, out: *mut u8, cap: i32) -> i32 {
    let p = if gma.is_null() { None } else { unsafe { CStr::from_ptr(gma) }.to_str().ok().map(str::to_owned) };
    let Some(p) = p else { return -1 };
    match std::panic::catch_unwind(|| gma::Gma::open(std::path::Path::new(&p)).map(|g| player::models(&g))) {
        Ok(Ok(list)) => {
            let text = list.iter().map(|(p, n)| format!("{p}|{}", n.replace(['|', '\n'], " "))).collect::<Vec<_>>().join("\n");
            let b = text.as_bytes();
            let n = b.len().min((cap.max(1) - 1) as usize);
            if !out.is_null() {
                unsafe {
                    std::ptr::copy_nonoverlapping(b.as_ptr(), out, n);
                    *out.add(n) = 0;
                }
            }
            list.len() as i32
        }
        Ok(Err(e)) => { *ERR.lock().unwrap() = e; -1 }
        Err(_) => -1,
    }
}

/// For C# (Models.cs): a player model ("addon.gma#models/x.mdl") written to `glb` as a rigged .glb, its textures
/// from the addon and the packs under `roots`. 0, or -1 (dbsp_error says why); dbsp_error has the line either way.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dmdl_convert(spec: *const c_char, glb: *const c_char, roots: *const c_char, max_side: i32) -> i32 {
    let text = |p: *const c_char| if p.is_null() { None } else { unsafe { CStr::from_ptr(p) }.to_str().ok().map(str::to_owned) };
    let (Some(s), Some(g)) = (text(spec), text(glb)) else {
        *ERR.lock().unwrap() = "no path".into();
        return -1;
    };
    let roots: Vec<std::path::PathBuf> = text(roots).unwrap_or_default().split('|').filter(|s| !s.is_empty()).map(std::path::PathBuf::from).collect();
    match std::panic::catch_unwind(|| player::convert(&s, &g, &roots, max_side.clamp(16, 4096) as usize)) {
        Ok(Ok(s)) => { *ERR.lock().unwrap() = s; 0 }
        Ok(Err(e)) => { *ERR.lock().unwrap() = e; -1 }
        Err(_) => { *ERR.lock().unwrap() = "the model reader failed (a bug: tell Claude which model)".into(); -1 }
    }
}

/// For C# (the models browser): one file of an addon ("maps/thumb/gm_x.png") written to `out_path`. Its size, or -1.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dgma_file(gma: *const c_char, path: *const c_char, out_path: *const c_char) -> i32 {
    let text = |p: *const c_char| if p.is_null() { None } else { unsafe { CStr::from_ptr(p) }.to_str().ok().map(str::to_owned) };
    let (Some(g), Some(p), Some(o)) = (text(gma), text(path), text(out_path)) else { return -1 };
    match std::panic::catch_unwind(|| -> Option<usize> {
        let a = gma::Gma::open(std::path::Path::new(&g)).ok()?;
        let b = a.read(&p.to_ascii_lowercase())?;
        std::fs::write(&o, &b).ok()?;
        Some(b.len())
    }) {
        Ok(Some(n)) => n.min(i32::MAX as usize) as i32,
        _ => -1,
    }
}

/// For C# (the models browser, on a thread of its own): every map's own picture in an addon (maps/thumb/<map>.png)
/// written to `out_dir`/<map>.png, the addon opened once (a Workshop download is LZMA-packed: seconds to unpack).
/// How many, or -1.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dgma_thumbs(gma: *const c_char, out_dir: *const c_char) -> i32 {
    let text = |p: *const c_char| if p.is_null() { None } else { unsafe { CStr::from_ptr(p) }.to_str().ok().map(str::to_owned) };
    let (Some(g), Some(o)) = (text(gma), text(out_dir)) else { return -1 };
    match std::panic::catch_unwind(|| -> Option<i32> {
        let a = gma::Gma::open(std::path::Path::new(&g)).ok()?;
        let _ = std::fs::create_dir_all(&o);
        let mut n = 0;
        for m in a.maps() {
            let name = m.trim_start_matches("maps/").trim_end_matches(".bsp").to_string();
            for inside in [format!("maps/thumb/{name}.png"), format!("maps/{name}.png"), format!("maps/thumb/{name}.jpg")] {
                if let Some(b) = a.read(&inside) {
                    if std::fs::write(std::path::Path::new(&o).join(format!("{name}.png")), &b).is_ok() { n += 1; }
                    break;
                }
            }
        }
        Some(n)
    }) {
        Ok(Some(n)) => n,
        _ => -1,
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
