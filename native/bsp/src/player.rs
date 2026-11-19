//! Garry's Mod player models (a Workshop addon's .mdl with its .vvd and .vtx) as a rigged .glb for the sandbox: its
//! skeleton (ValveBiped's bones, or whatever it has: Rigs.cs names them), its mesh bound to it (each vertex's three
//! bone weights), its textures (the addon's own first, then your packs). The sandbox then treats it like any other
//! character file: Mannequin.Prepare turns it to face +Z, sizes it and lays its limbs along the mannequin's.
//!
//! Source's model space (x forward, y left, z up, inches) to glTF's (+Z the way he faces, +X his left, +Y up,
//! metres): (y, z, x) times 0.0254, a turn (no mirror). The bind pose is each bone's poseToBone inverted.

use crate::content::{png, Content};
use crate::studio;
use std::collections::HashMap;

type M3 = [[f32; 3]; 3];

// Source's axes to glTF's: rows his left, up, the way he faces (+X, +Y, +Z in glTF)
fn gl(c: &M3, v: [f32; 3]) -> [f32; 3] { mv(c, v) }
// a rotation in Source's axes as the same rotation in glTF's: C R C^T
fn gl_m(c: &M3, r: &M3) -> M3 { mul(&mul(c, r), &tr(c)) }

fn mul(a: &M3, b: &M3) -> M3 {
    let mut o = [[0f32; 3]; 3];
    for i in 0..3 { for j in 0..3 { o[i][j] = (0..3).map(|k| a[i][k] * b[k][j]).sum(); } }
    o
}
fn tr(a: &M3) -> M3 { let mut o = [[0f32; 3]; 3]; for i in 0..3 { for j in 0..3 { o[i][j] = a[j][i]; } } o }
fn mv(a: &M3, v: [f32; 3]) -> [f32; 3] { [0, 1, 2].map(|i| a[i][0] * v[0] + a[i][1] * v[1] + a[i][2] * v[2]) }

// the nearest rotation (a bone's matrix can carry a little scale or skew): Gram-Schmidt on its columns
fn orthonormal(m: &M3) -> M3 {
    let col = |j: usize| [m[0][j], m[1][j], m[2][j]];
    let norm = |v: [f32; 3]| { let l = (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]).sqrt().max(1e-9); [v[0] / l, v[1] / l, v[2] / l] };
    let x = norm(col(0));
    let y0 = col(1);
    let d = x[0] * y0[0] + x[1] * y0[1] + x[2] * y0[2];
    let y = norm([y0[0] - d * x[0], y0[1] - d * x[1], y0[2] - d * x[2]]);
    let z = [x[1] * y[2] - x[2] * y[1], x[2] * y[0] - x[0] * y[2], x[0] * y[1] - x[1] * y[0]];
    [[x[0], y[0], z[0]], [x[1], y[1], z[1]], [x[2], y[2], z[2]]]
}

fn quat(m: &M3) -> [f32; 4] {
    let t = m[0][0] + m[1][1] + m[2][2];
    let q = if t > 0.0 {
        let s = (t + 1.0).sqrt() * 2.0;
        [(m[2][1] - m[1][2]) / s, (m[0][2] - m[2][0]) / s, (m[1][0] - m[0][1]) / s, 0.25 * s]
    } else if m[0][0] > m[1][1] && m[0][0] > m[2][2] {
        let s = (1.0 + m[0][0] - m[1][1] - m[2][2]).sqrt() * 2.0;
        [0.25 * s, (m[0][1] + m[1][0]) / s, (m[0][2] + m[2][0]) / s, (m[2][1] - m[1][2]) / s]
    } else if m[1][1] > m[2][2] {
        let s = (1.0 + m[1][1] - m[0][0] - m[2][2]).sqrt() * 2.0;
        [(m[0][1] + m[1][0]) / s, 0.25 * s, (m[1][2] + m[2][1]) / s, (m[0][2] - m[2][0]) / s]
    } else {
        let s = (1.0 + m[2][2] - m[0][0] - m[1][1]).sqrt() * 2.0;
        [(m[0][2] + m[2][0]) / s, (m[1][2] + m[2][1]) / s, 0.25 * s, (m[1][0] - m[0][1]) / s]
    };
    let l = q.iter().map(|v| v * v).sum::<f32>().sqrt().max(1e-9);
    q.map(|v| v / l)
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

/// An addon's player models: (path, the name it gives it). Its Lua's player_manager.AddValidModel("Name", "path")
/// when it has any; else every model with a pelvis and a head that isn't a weapon or a pair of hands.
pub fn models(g: &crate::gma::Gma) -> Vec<(String, String)> {
    let mut out: Vec<(String, String)> = vec![];
    for p in g.order.iter().filter(|p| p.ends_with(".lua")) {
        let Some(b) = g.read(p) else { continue };
        let text = String::from_utf8_lossy(&b).into_owned();
        let mut rest = text.as_str();
        while let Some(at) = rest.find("AddValidModel") {
            rest = &rest[at + 13..];
            // its two strings
            let mut strs = vec![];
            let mut s = rest;
            for _ in 0..2 {
                let Some(q) = s.find(|c| c == '"' || c == '\'') else { break };
                let quote = s.as_bytes()[q] as char;
                let Some(e) = s[q + 1..].find(quote) else { break };
                strs.push(s[q + 1..q + 1 + e].to_string());
                s = &s[q + 1 + e + 1..];
            }
            if strs.len() == 2 {
                let path = strs[1].replace('\\', "/").to_ascii_lowercase();
                if g.files.contains_key(&path) && !out.iter().any(|(p, _)| *p == path) { out.push((path, strs[0].clone())); }
            }
        }
    }
    if !out.is_empty() { return out; }
    for p in g.order.iter().filter(|p| p.ends_with(".mdl") && p.starts_with("models/")) {
        let low = p.to_ascii_lowercase();
        if low.contains("/weapons/") || low.contains("arms") || low.contains("hands") || low.contains("/props") { continue; }
        let Some(m) = g.read(p) else { continue };
        let nb = m.get(156..160).map(|b| i32::from_le_bytes(b.try_into().unwrap())).unwrap_or(0).clamp(0, 256) as usize;
        let bi = m.get(160..164).map(|b| i32::from_le_bytes(b.try_into().unwrap())).unwrap_or(0).max(0) as usize;
        let names: Vec<String> = (0..nb).map(|b| {
            let o = bi + b * 216;
            let n = m.get(o..o + 4).map(|x| i32::from_le_bytes(x.try_into().unwrap())).unwrap_or(0).max(0) as usize;
            let s = m.get(o + n..).unwrap_or_default();
            String::from_utf8_lossy(s.split(|&c| c == 0).next().unwrap_or_default()).to_ascii_lowercase()
        }).collect();
        if names.iter().any(|n| n.contains("pelvis") || n.contains("hips")) && names.iter().any(|n| n.contains("head")) {
            let name = p.rsplit('/').next().unwrap_or(p).trim_end_matches(".mdl").to_string();
            out.push((p.clone(), name));
        }
    }
    out
}

/// "addon.gma#models/x.mdl" (or a .gma: its first player model) to a rigged .glb: a line about it, or why it can't be
pub fn convert(spec: &str, glb_path: &str, roots: &[std::path::PathBuf], max_side: usize) -> Result<String, String> {
    // a loose .mdl (an addon's folder as it is: ".../grandtheftgmod/models/weapons/x.mdl"): read from its folder
    let low = spec.replace('\\', "/");
    let loose = !spec.contains('#') && low.to_ascii_lowercase().ends_with(".mdl");
    let (mdl_path, shown, addon, content) = if loose {
        let at = low.to_ascii_lowercase().rfind("/models/").ok_or(format!("{spec}: not in a models folder"))?;
        let root = std::path::PathBuf::from(&low[..at]);
        let rel = low[at + 1..].to_ascii_lowercase();
        let shown = rel.rsplit('/').next().unwrap_or(&rel).trim_end_matches(".mdl").to_string();
        let addon = root.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
        let mut content = Content::new(&[], roots, None);
        content.add_loose(root);
        (rel, shown, addon, content)
    } else {
        let (gma_path, pick) = match spec.split_once('#') { Some((a, b)) => (a, Some(b.to_ascii_lowercase())), None => (spec, None) };
        let g = crate::gma::Gma::open(std::path::Path::new(gma_path))?;
        let list = models(&g);
        let (mdl_path, shown) = match pick {
            Some(p) => (p.clone(), list.iter().find(|(q, _)| *q == p).map(|(_, n)| n.clone()).unwrap_or_else(|| p.clone())),
            None => list.first().cloned().ok_or(format!("\"{}\" has no player model in it", g.name))?,
        };
        let addon = g.name.clone();
        (mdl_path, shown, addon, Content::new(&[], roots, Some(g)))
    };
    let find = |p: &str| content.find(p);
    let exists = |p: &str| content.find(p).is_some();
    let model = studio::Model::read(&mdl_path, &find, &exists)?;
    if model.bones.is_empty() { return Err(format!("{mdl_path}: no skeleton")); }
    let nb = model.bones.len();

    // the bind pose: each bone's place in the model (poseToBone inverted), Source's axes first
    let mut src_r = vec![[[0f32; 3]; 3]; nb];
    let mut src_t = vec![[0f32; 3]; nb];
    for b in 0..nb {
        let m = &model.pose_to_bone[b];
        let r: M3 = [[m[0][0], m[0][1], m[0][2]], [m[1][0], m[1][1], m[1][2]], [m[2][0], m[2][1], m[2][2]]];
        let rt = tr(&orthonormal(&r));
        src_t[b] = mv(&rt, [m[0][3], m[1][3], m[2][3]]).map(|v| -v);
        src_r[b] = rt;
    }
    // which way he faces: his left from his thighs (left minus right, level), up Source's +Z, forward left x up.
    // (Models are made facing +X, -Y, anything: Mannequin.Prepare only turns him round, so he has to face +-Z.)
    let side_of = |n: &str| -> Option<bool> {
        let n = n.to_ascii_lowercase();
        if !(n.contains("thigh") || n.contains("upleg") || n.contains("upperleg")) { return None; }
        if n.contains("_l_") || n.contains("l_thigh") || n.contains("left") || n.ends_with(".l") || n.ends_with("_l") { Some(true) }
        else if n.contains("_r_") || n.contains("r_thigh") || n.contains("right") || n.ends_with(".r") || n.ends_with("_r") { Some(false) }
        else { None }
    };
    let l = (0..nb).find(|&b| side_of(&model.bones[b]) == Some(true));
    let r = (0..nb).find(|&b| side_of(&model.bones[b]) == Some(false));
    let mut left = match (l, r) { (Some(l), Some(r)) => [src_t[l][0] - src_t[r][0], src_t[l][1] - src_t[r][1], 0.0], _ => [0.0, 1.0, 0.0] };
    let len = (left[0] * left[0] + left[1] * left[1]).sqrt();
    if len < 1e-4 { left = [0.0, 1.0, 0.0]; } else { left = [left[0] / len, left[1] / len, 0.0]; }
    let up = [0.0, 0.0, 1.0];
    let fwd = [left[1] * up[2] - left[2] * up[1], left[2] * up[0] - left[0] * up[2], left[0] * up[1] - left[1] * up[0]];
    let c: M3 = [left, up, fwd];
    let mut world_r = vec![[[0f32; 3]; 3]; nb];
    let mut world_t = vec![[0f32; 3]; nb];
    for b in 0..nb {
        world_r[b] = gl_m(&c, &src_r[b]);
        world_t[b] = gl(&c, src_t[b]).map(|v| v * crate::SCALE);
    }

    let mut bin: Vec<u8> = vec![];
    let mut views = vec![];
    let mut accessors = vec![];
    let push = |bytes: &[u8], target: Option<u32>, bin: &mut Vec<u8>, views: &mut Vec<String>| -> usize {
        while bin.len() % 4 != 0 { bin.push(0); }
        let at = bin.len();
        bin.extend_from_slice(bytes);
        views.push(match target {
            Some(t) => format!(r#"{{"buffer":0,"byteOffset":{at},"byteLength":{},"target":{t}}}"#, bytes.len()),
            None => format!(r#"{{"buffer":0,"byteOffset":{at},"byteLength":{}}}"#, bytes.len()),
        });
        views.len() - 1
    };

    // the vertices, all of them shared by the meshes
    let n = model.vertices.len();
    let (mut pb, mut nbb, mut ub, mut jb, mut wb) = (Vec::with_capacity(n * 12), Vec::with_capacity(n * 12), Vec::with_capacity(n * 8), Vec::with_capacity(n * 8), Vec::with_capacity(n * 16));
    let (mut lo, mut hi) = ([f32::MAX; 3], [f32::MIN; 3]);
    for v in &model.vertices {
        let p = gl(&c, v.pos).map(|x| x * crate::SCALE);
        for a in 0..3 { lo[a] = lo[a].min(p[a]); hi[a] = hi[a].max(p[a]); }
        for x in p { pb.extend_from_slice(&x.to_le_bytes()); }
        for x in gl(&c, v.nrm) { nbb.extend_from_slice(&x.to_le_bytes()); }
        for x in v.uv { ub.extend_from_slice(&x.to_le_bytes()); }
        let k = (v.nbones as usize).clamp(1, 3);
        let mut w = [0f32; 4];
        let mut j = [0u16; 4];
        for i in 0..k {
            let bone = v.bones[i] as usize;
            if bone < nb { j[i] = bone as u16; w[i] = v.weights[i].max(0.0); }
        }
        let sum: f32 = w.iter().sum();
        if sum > 1e-6 { for x in w.iter_mut() { *x /= sum; } } else { w = [1.0, 0.0, 0.0, 0.0]; j = [0; 4]; }
        for x in j { jb.extend_from_slice(&x.to_le_bytes()); }
        for x in w { wb.extend_from_slice(&x.to_le_bytes()); }
    }
    let vp = push(&pb, Some(34962), &mut bin, &mut views);
    accessors.push(format!(r#"{{"bufferView":{vp},"componentType":5126,"count":{n},"type":"VEC3","min":[{},{},{}],"max":[{},{},{}]}}"#, lo[0], lo[1], lo[2], hi[0], hi[1], hi[2]));
    let vn = push(&nbb, Some(34962), &mut bin, &mut views);
    accessors.push(format!(r#"{{"bufferView":{vn},"componentType":5126,"count":{n},"type":"VEC3"}}"#));
    let vu = push(&ub, Some(34962), &mut bin, &mut views);
    accessors.push(format!(r#"{{"bufferView":{vu},"componentType":5126,"count":{n},"type":"VEC2"}}"#));
    let vj = push(&jb, Some(34962), &mut bin, &mut views);
    accessors.push(format!(r#"{{"bufferView":{vj},"componentType":5123,"count":{n},"type":"VEC4"}}"#));
    let vw = push(&wb, Some(34962), &mut bin, &mut views);
    accessors.push(format!(r#"{{"bufferView":{vw},"componentType":5126,"count":{n},"type":"VEC4"}}"#));

    // a primitive a material (the default skin's), its triangles counter-clockwise about their vertices' normals
    let skin0 = model.skins.first().cloned().unwrap_or_default();
    let mut by_mat: Vec<(usize, Vec<u32>)> = vec![];
    for mesh in &model.meshes {
        let mi = skin0.get(mesh.material).copied().unwrap_or(mesh.material);
        let idx = match by_mat.iter().position(|(m, _)| *m == mi) { Some(i) => i, None => { by_mat.push((mi, vec![])); by_mat.len() - 1 } };
        for t in &mesh.tris {
            let v = [&model.vertices[t[0] as usize], &model.vertices[t[1] as usize], &model.vertices[t[2] as usize]];
            let e1 = [v[1].pos[0] - v[0].pos[0], v[1].pos[1] - v[0].pos[1], v[1].pos[2] - v[0].pos[2]];
            let e2 = [v[2].pos[0] - v[0].pos[0], v[2].pos[1] - v[0].pos[1], v[2].pos[2] - v[0].pos[2]];
            let face = [e1[1] * e2[2] - e1[2] * e2[1], e1[2] * e2[0] - e1[0] * e2[2], e1[0] * e2[1] - e1[1] * e2[0]];
            let avg = [0, 1, 2].map(|a| v[0].nrm[a] + v[1].nrm[a] + v[2].nrm[a]);
            let o = if face[0] * avg[0] + face[1] * avg[1] + face[2] * avg[2] < 0.0 { [0, 2, 1] } else { [0, 1, 2] };
            for i in o { by_mat[idx].1.push(t[i]); }
        }
    }
    let (mut images, mut textures, mut materials, mut prims) = (vec![], vec![], vec![], vec![]);
    let mut tex_of: HashMap<String, Option<usize>> = HashMap::new();
    let mut missing = vec![];
    for (mi, idx) in &by_mat {
        if idx.is_empty() { continue; }
        let mut ib = Vec::with_capacity(idx.len() * 4);
        for i in idx { ib.extend_from_slice(&i.to_le_bytes()); }
        let vi = push(&ib, Some(34963), &mut bin, &mut views);
        accessors.push(format!(r#"{{"bufferView":{vi},"componentType":5125,"count":{},"type":"SCALAR"}}"#, idx.len()));
        let ia = accessors.len() - 1;
        let name = model.materials.get(*mi).cloned().unwrap_or_else(|| format!("material{mi}"));
        let vmt = content.material(&name);
        let base = vmt.as_ref().and_then(|v| v.base.clone()).unwrap_or_else(|| name.clone()).replace('\\', "/").to_ascii_lowercase();
        let ti = match tex_of.get(&base) {
            Some(t) => *t,
            None => {
                let t = content.find(&format!("materials/{}.vtf", base.trim_end_matches(".vtf"))).and_then(|v| crate::vtf::decode(&v, max_side).ok()).map(|(w, h, rgba)| {
                    let iv = push(&png(w, h, &rgba), None, &mut bin, &mut views);
                    images.push(format!(r#"{{"bufferView":{iv},"mimeType":"image/png"}}"#));
                    textures.push(format!(r#"{{"source":{}}}"#, images.len() - 1));
                    textures.len() - 1
                });
                tex_of.insert(base.clone(), t);
                t
            }
        };
        if ti.is_none() { missing.push(name.clone()); }
        let (translucent, alphatest, nocull, tint) = vmt.as_ref().map(|v| (v.translucent, v.alphatest, v.nocull, v.tint)).unwrap_or((false, false, false, [1.0; 3]));
        let mode = if translucent { r#","alphaMode":"BLEND""# } else if alphatest { r#","alphaMode":"MASK","alphaCutoff":0.5"# } else { "" };
        let tex = ti.map(|t| format!(r#""baseColorTexture":{{"index":{t}}},"#)).unwrap_or_default();
        materials.push(format!(
            r#"{{"name":{},"pbrMetallicRoughness":{{{tex}"baseColorFactor":[{},{},{},1],"metallicFactor":0,"roughnessFactor":0.8}}{mode}{}}}"#,
            json(&name), tint[0], tint[1], tint[2], if nocull { r#","doubleSided":true"# } else { "" }
        ));
        prims.push(format!(r#"{{"attributes":{{"POSITION":0,"NORMAL":1,"TEXCOORD_0":2,"JOINTS_0":3,"WEIGHTS_0":4}},"indices":{ia},"material":{}}}"#, materials.len() - 1));
    }
    if prims.is_empty() { return Err(format!("{mdl_path}: no triangles")); }

    // the skeleton: node 0 the root, 1 the mesh, 2.. the bones (each in its parent's space)
    let mut nodes = vec![String::new(), format!(r#"{{"name":{},"mesh":0,"skin":0}}"#, json(&shown))];
    let mut ibm = Vec::with_capacity(nb * 64);
    let mut top = vec![1usize];
    for b in 0..nb {
        let p = model.parents[b];
        let (r, t) = if p >= 0 && (p as usize) < nb {
            let pr = tr(&world_r[p as usize]);
            let d = [0, 1, 2].map(|a| world_t[b][a] - world_t[p as usize][a]);
            (mul(&pr, &world_r[b]), mv(&pr, d))
        } else {
            top.push(2 + b);
            (world_r[b], world_t[b])
        };
        let q = quat(&r);
        let kids: Vec<String> = (0..nb).filter(|&c| model.parents[c] == b as i32).map(|c| (2 + c).to_string()).collect();
        nodes.push(format!(
            r#"{{"name":{},"translation":[{},{},{}],"rotation":[{},{},{},{}]{}}}"#,
            json(&model.bones[b]), t[0], t[1], t[2], q[0], q[1], q[2], q[3],
            if kids.is_empty() { String::new() } else { format!(r#","children":[{}]"#, kids.join(",")) }
        ));
        // its inverse bind matrix (column-major): the bind pose's inverse
        let ri = tr(&world_r[b]);
        let ti = mv(&ri, world_t[b]).map(|v| -v);
        let m4 = [ri[0][0], ri[1][0], ri[2][0], 0.0, ri[0][1], ri[1][1], ri[2][1], 0.0, ri[0][2], ri[1][2], ri[2][2], 0.0, ti[0], ti[1], ti[2], 1.0f32];
        for x in m4 { ibm.extend_from_slice(&x.to_le_bytes()); }
    }
    nodes[0] = format!(r#"{{"name":{},"children":[{}]}}"#, json(&shown), top.iter().map(|c| c.to_string()).collect::<Vec<_>>().join(","));
    let vb = push(&ibm, None, &mut bin, &mut views);
    accessors.push(format!(r#"{{"bufferView":{vb},"componentType":5126,"count":{nb},"type":"MAT4"}}"#));
    let joints: Vec<String> = (0..nb).map(|b| (2 + b).to_string()).collect();

    while bin.len() % 4 != 0 { bin.push(0); }
    let json_text = format!(
        r#"{{"asset":{{"version":"2.0","generator":"Delirium Sandbox (GMod player model)"}},"scene":0,"scenes":[{{"nodes":[0]}}],"nodes":[{}],"meshes":[{{"name":{},"primitives":[{}]}}],"skins":[{{"joints":[{}],"inverseBindMatrices":{}}}],"materials":[{}],{}{}"accessors":[{}],"bufferViews":[{}],"buffers":[{{"byteLength":{}}}]}}"#,
        nodes.join(","), json(&shown), prims.join(","), joints.join(","), accessors.len() - 1, materials.join(","),
        if images.is_empty() { String::new() } else { format!(r#""images":[{}],"#, images.join(",")) },
        if textures.is_empty() { String::new() } else { format!(r#""textures":[{}],"#, textures.join(",")) },
        accessors.join(","), views.join(","), bin.len()
    );
    let mut j = json_text.into_bytes();
    while j.len() % 4 != 0 { j.push(b' '); }
    let mut out = Vec::with_capacity(28 + j.len() + bin.len());
    out.extend_from_slice(b"glTF");
    out.extend_from_slice(&2u32.to_le_bytes());
    out.extend_from_slice(&((12 + 8 + j.len() + 8 + bin.len()) as u32).to_le_bytes());
    out.extend_from_slice(&(j.len() as u32).to_le_bytes());
    out.extend_from_slice(b"JSON");
    out.extend_from_slice(&j);
    out.extend_from_slice(&(bin.len() as u32).to_le_bytes());
    out.extend_from_slice(b"BIN\0");
    out.extend_from_slice(&bin);
    std::fs::write(glb_path, &out).map_err(|e| format!("{glb_path}: {e}"))?;
    let tris: usize = by_mat.iter().map(|(_, i)| i.len() / 3).sum();
    Ok(format!(
        "{shown} ({addon}): {tris} triangles, {nb} bones, {} materials{}",
        prims.len(),
        if missing.is_empty() { String::new() } else { format!(", no texture for {} (e.g. {})", missing.len(), missing.iter().take(2).cloned().collect::<Vec<_>>().join(", ")) }
    ))
}
