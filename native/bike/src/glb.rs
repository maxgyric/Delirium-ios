//! A GTA IV vehicle (.xft fragment + its .xtd, vehshare.xtd) as one .glb: a node per skeleton bone (bodyshell,
//! forks_u, wheel_lf, swingarm...), each part's triangles on the bone they're bound to, textures as PNG. GTA's frame
//! (x right, y forward, z up) becomes glTF's (-x, z, y): Unity then has the bike facing +Z.
use crate::tex::{self, Tex};
use delirium_data::{model, resource::Resource, skeleton};
use std::collections::HashMap;

type M = [[f32; 4]; 4];

fn q2m(q: [f32; 4], t: [f32; 3]) -> M {
    let [x, y, z, w] = q;
    [
        [1.0 - 2.0 * (y * y + z * z), 2.0 * (x * y + z * w), 2.0 * (x * z - y * w), 0.0],
        [2.0 * (x * y - z * w), 1.0 - 2.0 * (x * x + z * z), 2.0 * (y * z + x * w), 0.0],
        [2.0 * (x * z + y * w), 2.0 * (y * z - x * w), 1.0 - 2.0 * (x * x + y * y), 0.0],
        [t[0], t[1], t[2], 1.0],
    ]
}
// row-vector matrices: p' = p * M
fn mul(a: &M, b: &M) -> M {
    let mut r = [[0.0; 4]; 4];
    for i in 0..4 { for j in 0..4 { r[i][j] = (0..4).map(|k| a[i][k] * b[k][j]).sum(); } }
    r
}
fn point(m: &M, p: [f32; 3]) -> [f32; 3] {
    std::array::from_fn(|j| p[0] * m[0][j] + p[1] * m[1][j] + p[2] * m[2][j] + m[3][j])
}
fn dir(m: &M, p: [f32; 3]) -> [f32; 3] {
    std::array::from_fn(|j| p[0] * m[0][j] + p[1] * m[1][j] + p[2] * m[2][j])
}
fn inv_rigid(m: &M) -> M {
    let mut r = [[0.0; 4]; 4];
    for i in 0..3 { for j in 0..3 { r[i][j] = m[j][i]; } }
    let t = [m[3][0], m[3][1], m[3][2]];
    for j in 0..3 { r[3][j] = -(t[0] * r[0][j] + t[1] * r[1][j] + t[2] * r[2][j]); }
    r[3][3] = 1.0;
    r
}
fn gl(p: [f32; 3]) -> [f32; 3] { [-p[0], p[2], p[1]] }

pub struct Part {
    pub name: String,
    pub parent: Option<usize>,
    pub local: [f32; 3],
    pub rot: [f32; 4],
    pub world: [f32; 3],
}

pub struct Built {
    pub glb: Vec<u8>,
    /// per bone: its geometry's furthest point from the bone across the wheel's plane (y, z), a wheel's radius
    pub radius: Vec<f32>,
    pub parts: Vec<Part>,
    pub min: [f32; 3],
    pub max: [f32; 3],
}

#[derive(Default)]
struct Out {
    json_meshes: Vec<String>,
    materials: Vec<String>,
    images: Vec<String>,
    textures: Vec<String>,
    views: Vec<String>,
    accessors: Vec<String>,
    bin: Vec<u8>,
    mat_of: HashMap<String, usize>,
    tex_of: HashMap<String, Option<usize>>,
}

impl Out {
    fn view(&mut self, data: &[u8], target: Option<u32>) -> usize {
        while self.bin.len() % 4 != 0 { self.bin.push(0); }
        let at = self.bin.len();
        self.bin.extend_from_slice(data);
        let t = target.map(|t| format!(",\"target\":{t}")).unwrap_or_default();
        self.views.push(format!("{{\"buffer\":0,\"byteOffset\":{at},\"byteLength\":{}{t}}}", data.len()));
        self.views.len() - 1
    }
    fn floats(&mut self, v: &[f32], comps: usize, ty: &str, minmax: bool) -> usize {
        let bytes: Vec<u8> = v.iter().flat_map(|f| f.to_le_bytes()).collect();
        let bv = self.view(&bytes, Some(34962));
        let mut mm = String::new();
        if minmax {
            let (mut lo, mut hi) = (vec![f32::MAX; comps], vec![f32::MIN; comps]);
            for c in v.chunks(comps) { for k in 0..comps { lo[k] = lo[k].min(c[k]); hi[k] = hi[k].max(c[k]); } }
            mm = format!(",\"min\":{lo:?},\"max\":{hi:?}");
        }
        self.accessors.push(format!("{{\"bufferView\":{bv},\"componentType\":5126,\"count\":{},\"type\":\"{ty}\"{mm}}}", v.len() / comps));
        self.accessors.len() - 1
    }
    fn indices(&mut self, v: &[u32]) -> usize {
        let bytes: Vec<u8> = v.iter().flat_map(|f| f.to_le_bytes()).collect();
        let bv = self.view(&bytes, Some(34963));
        self.accessors.push(format!("{{\"bufferView\":{bv},\"componentType\":5125,\"count\":{},\"type\":\"SCALAR\"}}", v.len()));
        self.accessors.len() - 1
    }
    fn texture(&mut self, name: &str, texes: &HashMap<String, Tex>) -> Option<usize> {
        let k = tex::key(name);
        if let Some(t) = self.tex_of.get(&k) { return *t; }
        let r = texes.get(&k).map(|t| {
            let png = tex::png(t.w, t.h, &t.rgba);
            let bv = self.view(&png, None);
            self.images.push(format!("{{\"bufferView\":{bv},\"mimeType\":\"image/png\"}}"));
            self.textures.push(format!("{{\"source\":{}}}", self.images.len() - 1));
            self.textures.len() - 1
        });
        self.tex_of.insert(k, r);
        r
    }
    fn material(&mut self, shader: &str, texture: Option<&str>, texes: &HashMap<String, Tex>, paint: &[[f32; 3]; 2]) -> usize {
        let key = format!("{shader}|{}", texture.unwrap_or(""));
        if let Some(&m) = self.mat_of.get(&key) { return m; }
        let s = shader.to_ascii_lowercase();
        let painted = s.contains("paint");
        let glass = s.contains("glass");
        let emissive = s.contains("emissive") || s.contains("lights");
        let mut col = if s.contains("paint2") { paint[1] } else if painted { paint[0] } else { [1.0, 1.0, 1.0] };
        let mut tex_json = String::new();
        let mut alpha = false;
        if !painted {
            if let Some(t) = texture.and_then(|t| self.texture(t, texes)) {
                tex_json = format!(",\"baseColorTexture\":{{\"index\":{t}}}");
                alpha = texture.map(|n| texes.get(&tex::key(n)).map(|t| t.alpha).unwrap_or(false)).unwrap_or(false);
            } else {
                col = [0.35, 0.35, 0.37];
            }
        }
        let a = if glass { 0.45 } else { 1.0 };
        let mode = if glass { ",\"alphaMode\":\"BLEND\"" } else if alpha { ",\"alphaMode\":\"MASK\",\"alphaCutoff\":0.5" } else { "" };
        let rough = if painted { 0.35 } else if glass { 0.1 } else { 0.7 };
        let metal = if painted { 0.3 } else { 0.0 };
        let em = if emissive && !glass { ",\"emissiveFactor\":[0.0,0.0,0.0]" } else { "" };
        self.materials.push(format!(
            "{{\"name\":\"{shader}\",\"pbrMetallicRoughness\":{{\"baseColorFactor\":[{},{},{},{a}],\"metallicFactor\":{metal},\"roughnessFactor\":{rough}{tex_json}}}{mode}{em},\"doubleSided\":{}}}",
            col[0], col[1], col[2], glass));
        self.mat_of.insert(key, self.materials.len() - 1);
        self.materials.len() - 1
    }
}

/// The model. `dicts`: the textures it may use (its own .xtd, vehshare.xtd); `paint`: its two paint colours.
pub fn build(frag: &Resource, dicts: &[&Resource], paint: [[f32; 3]; 2]) -> Result<Built, String> {
    let root = model::fragment_root(frag)?;
    let bones = skeleton::read(frag, root)?;
    let mut texes: HashMap<String, Tex> = HashMap::new();
    if let Some(at) = model::embedded_textures(frag, root) {
        // a dictionary in the model itself (rare for vehicles)
        let _ = at;
    }
    for d in dicts {
        if let Ok(e) = model::dictionary(d) {
            for t in tex::read_dict(d, &e) { texes.entry(tex::key(&t.name)).or_insert(t); }
        }
    }
    // the bones' bind matrices in the model's frame
    let mut world: Vec<M> = Vec::with_capacity(bones.len());
    for b in &bones {
        let l = q2m(b.rotation, b.translation);
        let w = match b.parent { Some(p) if p < world.len() => mul(&l, &world[p]), _ => l };
        world.push(w);
    }
    let inv: Vec<M> = world.iter().map(inv_rigid).collect();
    let mut out = Out::default();
    // per bone: (material -> positions, normals, uvs, indices)
    type Geo = (Vec<f32>, Vec<f32>, Vec<f32>, Vec<u32>);
    let mut per_bone: Vec<Vec<(usize, Geo)>> = (0..bones.len()).map(|_| vec![]).collect();
    let (mut lo, mut hi) = ([f32::MAX; 3], [f32::MIN; 3]);
    let mut radius = vec![0.0f32; bones.len()];
    let mut add = |bone: usize, mat: usize, pts: &[[f32; 3]], nrm: &[[f32; 3]], uv: &[[f32; 2]], tri: &[u32], local: bool,
                   per_bone: &mut Vec<Vec<(usize, Geo)>>| {
        let list = &mut per_bone[bone];
        let k = match list.iter().position(|(m, _)| *m == mat) { Some(k) => k, None => { list.push((mat, Default::default())); list.len() - 1 } };
        let g = &mut list[k].1;
        let base = (g.0.len() / 3) as u32;
        for (i, p) in pts.iter().enumerate() {
            let (lp, ln) = if local { (*p, nrm.get(i).copied().unwrap_or([0.0, 0.0, 1.0])) } else { (point(&inv[bone], *p), dir(&inv[bone], nrm.get(i).copied().unwrap_or([0.0, 0.0, 1.0]))) };
            let wp = if local { point(&world[bone], *p) } else { *p };
            radius[bone] = radius[bone].max((lp[1] * lp[1] + lp[2] * lp[2]).sqrt());
            for k in 0..3 { lo[k] = lo[k].min(wp[k]); hi[k] = hi[k].max(wp[k]); }
            g.0.extend_from_slice(&gl(lp));
            g.1.extend_from_slice(&gl(ln));
            let t = uv.get(i).copied().unwrap_or([0.0, 0.0]);
            g.2.extend_from_slice(&[t[0], t[1]]);
        }
        // the mirror (x -> -x) turns the winding: swap two corners
        for t in tri.chunks_exact(3) { g.3.extend_from_slice(&[base + t[0], base + t[2], base + t[1]]); }
    };
    let main = model::meshes(frag, root, root)?;
    for m in &main {
        let mat = out.material(&m.shader, m.texture.as_deref(), &texes, &paint);
        // split by the bone each vertex is bound to (vehicles are rigid: one bone a vertex)
        let bone_of = |v: usize| -> usize {
            let j = m.joints.get(v).map(|j| j[0] as usize).unwrap_or(0);
            m.bone_map.get(j).map(|&b| b as usize).unwrap_or(j).min(bones.len().saturating_sub(1))
        };
        let mut groups: HashMap<usize, Vec<u32>> = HashMap::new();
        for t in m.indices.chunks_exact(3) {
            groups.entry(bone_of(t[0] as usize)).or_default().extend_from_slice(t);
        }
        for (b, tri) in groups {
            add(b, mat, &m.positions, &m.normals, &m.uvs, &tri, false, &mut per_bone);
        }
    }
    // the fragment's children: wheels and the like, drawn on their own bone
    if let Ok(children) = model::fragment_children(frag) {
        for (bone, d) in children {
            let Ok(ms) = model::meshes(frag, d, root) else { continue };
            let b = bone.min(bones.len().saturating_sub(1));
            for m in &ms {
                let mat = out.material(&m.shader, m.texture.as_deref(), &texes, &paint);
                add(b, mat, &m.positions, &m.normals, &m.uvs, &m.indices, true, &mut per_bone);
            }
        }
    }
    // nodes
    let mut nodes = vec![];
    let mut kids: Vec<Vec<usize>> = (0..bones.len()).map(|_| vec![]).collect();
    for (i, b) in bones.iter().enumerate() {
        if let Some(p) = b.parent { if p < bones.len() { kids[p].push(i); } }
    }
    for (i, b) in bones.iter().enumerate() {
        let mesh = if per_bone[i].is_empty() { String::new() } else {
            let prims: Vec<String> = std::mem::take(&mut per_bone[i]).into_iter().map(|(mat, g)| {
                let p = out.floats(&g.0, 3, "VEC3", true);
                let n = out.floats(&g.1, 3, "VEC3", false);
                let t = out.floats(&g.2, 2, "VEC2", false);
                let ix = out.indices(&g.3);
                format!("{{\"attributes\":{{\"POSITION\":{p},\"NORMAL\":{n},\"TEXCOORD_0\":{t}}},\"indices\":{ix},\"material\":{mat}}}")
            }).collect();
            out.json_meshes.push(format!("{{\"name\":\"{}\",\"primitives\":[{}]}}", b.name, prims.join(",")));
            format!(",\"mesh\":{}", out.json_meshes.len() - 1)
        };
        let t = gl(b.translation);
        let q = b.rotation;
        let r = [-q[0], q[2], q[1], q[3]];
        let ch = if kids[i].is_empty() { String::new() } else { format!(",\"children\":{:?}", kids[i]) };
        nodes.push(format!("{{\"name\":\"{}\",\"translation\":[{},{},{}],\"rotation\":[{},{},{},{}]{mesh}{ch}}}", b.name, t[0], t[1], t[2], r[0], r[1], r[2], r[3]));
    }
    let roots: Vec<usize> = bones.iter().enumerate().filter(|(_, b)| b.parent.is_none_or(|p| p >= bones.len())).map(|(i, _)| i).collect();
    while out.bin.len() % 4 != 0 { out.bin.push(0); }
    let j = |v: &Vec<String>| v.join(",");
    let json = format!(
        "{{\"asset\":{{\"version\":\"2.0\",\"generator\":\"Delirium Sandbox (your GTA IV)\"}},\"scene\":0,\"scenes\":[{{\"nodes\":{roots:?}}}],\"nodes\":[{}],\"meshes\":[{}],\"materials\":[{}],\"textures\":[{}],\"images\":[{}],\"accessors\":[{}],\"bufferViews\":[{}],\"buffers\":[{{\"byteLength\":{}}}]}}",
        j(&nodes), j(&out.json_meshes), j(&out.materials), j(&out.textures), j(&out.images), j(&out.accessors), j(&out.views), out.bin.len());
    let mut js = json.into_bytes();
    while js.len() % 4 != 0 { js.push(b' '); }
    let mut glb = vec![];
    glb.extend_from_slice(b"glTF");
    glb.extend_from_slice(&2u32.to_le_bytes());
    glb.extend_from_slice(&((12 + 8 + js.len() + 8 + out.bin.len()) as u32).to_le_bytes());
    glb.extend_from_slice(&(js.len() as u32).to_le_bytes());
    glb.extend_from_slice(b"JSON");
    glb.extend_from_slice(&js);
    glb.extend_from_slice(&(out.bin.len() as u32).to_le_bytes());
    glb.extend_from_slice(b"BIN\0");
    glb.extend_from_slice(&out.bin);
    let parts = bones.iter().enumerate().map(|(i, b)| Part {
        name: b.name.clone(), parent: b.parent, local: b.translation, rot: b.rotation,
        world: [world[i][3][0], world[i][3][1], world[i][3][2]],
    }).collect();
    Ok(Built { glb, radius, parts, min: lo, max: hi })
}
