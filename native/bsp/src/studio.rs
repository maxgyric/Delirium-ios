//! Source's models (.mdl with its .vvd and .vtx, versions 44-49): their triangles at the most detailed level, each
//! mesh with its material (the .mdl's texture names, looked for in its material folders). Positions in the model's
//! space (inches, Source's axes), as a static prop's or a player model's in its bind pose.
//!
//! .mdl: studiohdr_t (textures 64 bytes each at 204/208, material folders at 212/216, skins at 220-228, body parts
//! at 232/236), body parts (16) -> models (148) -> meshes (116). .vvd ("IDSV"): every vertex (48 bytes: bone weights,
//! position, normal, texture coordinates), the fixups that pick LOD 0's. .vtx (version 7): body parts -> models ->
//! LODs -> meshes (9) -> strip groups (25, or 33 from newer compilers) -> vertices (9: the mesh's vertex) and indices.

use crate::bsp::V3;

fn i32_at(d: &[u8], at: usize) -> Option<i32> { d.get(at..at + 4).map(|b| i32::from_le_bytes(b.try_into().unwrap())) }
fn u16_at(d: &[u8], at: usize) -> Option<u16> { d.get(at..at + 2).map(|b| u16::from_le_bytes([b[0], b[1]])) }
fn f32_at(d: &[u8], at: usize) -> Option<f32> { i32_at(d, at).map(|v| f32::from_bits(v as u32)) }
fn v3_at(d: &[u8], at: usize) -> Option<V3> { Some([f32_at(d, at)?, f32_at(d, at + 4)?, f32_at(d, at + 8)?]) }
fn text_at(d: &[u8], at: usize) -> String {
    let s = d.get(at..).unwrap_or_default();
    String::from_utf8_lossy(s.split(|&c| c == 0).next().unwrap_or_default()).into_owned()
}

#[derive(Clone)]
pub struct Vertex {
    pub pos: V3,
    pub nrm: V3,
    pub uv: [f32; 2],
    pub bones: [u8; 3],
    pub weights: [f32; 3],
    pub nbones: u8,
}

/// One mesh: its material's index into `Model::materials` (the skin's), its triangles as vertex indices
pub struct Mesh {
    pub material: usize,
    pub tris: Vec<[u32; 3]>,
}

pub struct Model {
    pub vertices: Vec<Vertex>,
    pub meshes: Vec<Mesh>,
    /// the materials' paths ("models/props/x/y", no extension), in the .mdl's texture order
    pub materials: Vec<String>,
    /// skin families: texture index for each skin reference
    pub skins: Vec<Vec<usize>>,
    pub bones: Vec<String>,
    /// each bone's parent (-1: none)
    pub parents: Vec<i32>,
    /// each bone's poseToBone (3 rows of 4: the model's space to the bone's, in its bind pose)
    pub pose_to_bone: Vec<[[f32; 4]; 3]>,
}

impl Model {
    /// `find`: a file of the model's ("models/x.vvd") from wherever the map's content is
    pub fn read(mdl_path: &str, find: &dyn Fn(&str) -> Option<Vec<u8>>, exists: &dyn Fn(&str) -> bool) -> Result<Model, String> {
        let mdl = find(mdl_path).ok_or(format!("{mdl_path}: not found"))?;
        if mdl.get(0..4) != Some(b"IDST") { return Err(format!("{mdl_path}: not a model")); }
        let version = i32_at(&mdl, 4).unwrap_or(0);
        if !(44..=49).contains(&version) { return Err(format!("{mdl_path}: model version {version}")); }
        let base = mdl_path.trim_end_matches(".mdl");
        let vvd = find(&format!("{base}.vvd")).ok_or(format!("{base}.vvd: not found"))?;
        let vtx = ["dx90.vtx", "vtx", "dx80.vtx", "sw.vtx"].iter().find_map(|e| find(&format!("{base}.{e}"))).ok_or(format!("{base}.vtx: not found"))?;
        let g = |at: usize| i32_at(&mdl, at).unwrap_or(0);

        // bones (names, for player models)
        let (nb, bi) = (g(156) as usize, g(160) as usize);
        let bones = (0..nb.min(256)).map(|b| { let o = bi + b * 216; text_at(&mdl, o + i32_at(&mdl, o).unwrap_or(0) as usize) }).collect();
        let parents = (0..nb.min(256)).map(|b| i32_at(&mdl, bi + b * 216 + 4).unwrap_or(-1)).collect();
        let pose_to_bone = (0..nb.min(256)).map(|b| {
            let o = bi + b * 216 + 96;
            let mut m = [[0f32; 4]; 3];
            for r in 0..3 { for c in 0..4 { m[r][c] = f32_at(&mdl, o + (r * 4 + c) * 4).unwrap_or(if r == c { 1.0 } else { 0.0 }); } }
            m
        }).collect();

        // materials: each texture name tried in each material folder
        let (nt, ti) = (g(204) as usize, g(208) as usize);
        let (ncd, cdi) = (g(212) as usize, g(216) as usize);
        let cds: Vec<String> = (0..ncd.min(64)).map(|k| text_at(&mdl, i32_at(&mdl, cdi + k * 4).unwrap_or(0) as usize).replace('\\', "/").to_ascii_lowercase()).collect();
        let mut materials = vec![];
        for t in 0..nt.min(512) {
            let o = ti + t * 64;
            let name = text_at(&mdl, o + i32_at(&mdl, o).unwrap_or(0) as usize).replace('\\', "/").to_ascii_lowercase();
            let name = name.trim_start_matches('/').to_string();
            let found = cds.iter().map(|cd| format!("{}{}{}", cd.trim_matches('/'), if cd.trim_matches('/').is_empty() { "" } else { "/" }, name))
                .find(|p| exists(&format!("materials/{p}.vmt")));
            materials.push(found.unwrap_or_else(|| format!("{}{}", cds.first().map(|c| format!("{}/", c.trim_matches('/'))).unwrap_or_default(), name)));
        }
        let (nref, nfam, si) = (g(220) as usize, g(224) as usize, g(228) as usize);
        let mut skins = vec![];
        for f in 0..nfam.min(64) {
            skins.push((0..nref.min(512)).map(|r| u16_at(&mdl, si + (f * nref + r) * 2).unwrap_or(r as u16) as usize).collect());
        }
        if skins.is_empty() { skins.push((0..materials.len()).collect()); }

        // the vertices (LOD 0's: through the fixups when there are any)
        if vvd.get(0..4) != Some(b"IDSV") { return Err(format!("{base}.vvd: not a vertex file")); }
        let lod0 = i32_at(&vvd, 16).unwrap_or(0) as usize;
        let (nfix, fixat, vat) = (i32_at(&vvd, 48).unwrap_or(0) as usize, i32_at(&vvd, 52).unwrap_or(0) as usize, i32_at(&vvd, 56).unwrap_or(0) as usize);
        let vert = |i: usize| -> Option<Vertex> {
            let o = vat + i * 48;
            Some(Vertex {
                weights: [f32_at(&vvd, o)?, f32_at(&vvd, o + 4)?, f32_at(&vvd, o + 8)?],
                bones: [*vvd.get(o + 12)?, *vvd.get(o + 13)?, *vvd.get(o + 14)?],
                nbones: *vvd.get(o + 15)?,
                pos: v3_at(&vvd, o + 16)?,
                nrm: v3_at(&vvd, o + 28)?,
                uv: [f32_at(&vvd, o + 40)?, f32_at(&vvd, o + 44)?],
            })
        };
        let mut vertices = vec![];
        if nfix == 0 {
            for i in 0..lod0 { vertices.push(vert(i).ok_or("vvd: short")?); }
        } else {
            for f in 0..nfix {
                let o = fixat + f * 12;
                let (lod, src, n) = (i32_at(&vvd, o).unwrap_or(-1), i32_at(&vvd, o + 4).unwrap_or(0) as usize, i32_at(&vvd, o + 8).unwrap_or(0) as usize);
                if lod >= 0 { for i in src..src + n { vertices.push(vert(i).ok_or("vvd: short")?); } }
            }
        }

        // the triangles: the .mdl's and the .vtx's body parts, models and meshes side by side
        let (nbp, bpi) = (g(232) as usize, g(236) as usize);
        let vt = |at: usize| i32_at(&vtx, at).unwrap_or(0);
        if vt(0) != 7 { return Err(format!("{base}.vtx: version {}", vt(0))); }
        let (vnbp, vbpo) = (vt(28) as usize, vt(32) as usize);
        let mut meshes = vec![];
        for bp in 0..nbp.min(vnbp).min(64) {
            let mo = bpi + bp * 16;
            let (nmod, modi) = (i32_at(&mdl, mo + 4).unwrap_or(0) as usize, i32_at(&mdl, mo + 12).unwrap_or(0) as usize);
            let vbp = vbpo + bp * 8;
            let (vnm, vmo) = (vt(vbp) as usize, vt(vbp + 4) as usize);
            // the first model of each body part (the others are its alternatives: a helmet or none)
            for m in 0..nmod.min(vnm).min(1) {
                let mm = mo + modi + m * 148;
                let (nmesh, meshi, vstart) = (i32_at(&mdl, mm + 72).unwrap_or(0) as usize, i32_at(&mdl, mm + 76).unwrap_or(0) as usize, i32_at(&mdl, mm + 84).unwrap_or(0) as usize / 48);
                let vm = vbp + vmo + m * 8;
                let (nlod, lodo) = (vt(vm) as usize, vt(vm + 4) as usize);
                if nlod == 0 { continue; }
                let lod = vm + lodo; // LOD 0
                let (vnmesh, vmesho) = (vt(lod) as usize, vt(lod + 4) as usize);
                for k in 0..nmesh.min(vnmesh).min(256) {
                    let me = mm + meshi + k * 116;
                    let (material, voff) = (i32_at(&mdl, me).unwrap_or(0) as usize, i32_at(&mdl, me + 12).unwrap_or(0) as usize);
                    let vme = lod + vmesho + k * 9;
                    let (nsg, sgo) = (vt(vme) as usize, vt(vme + 4) as usize);
                    let mut tris = vec![];
                    // a strip group's size: 25, or 33 (newer compilers): the one whose next group's offsets fit
                    let sgsize = if nsg > 1 && strip_ok(&vtx, vme + sgo + 25) { 25 } else if nsg > 1 && strip_ok(&vtx, vme + sgo + 33) { 33 } else { 25 };
                    for sg in 0..nsg.min(64) {
                        let s = vme + sgo + sg * sgsize;
                        let (nv, vo, ni, io) = (vt(s) as usize, vt(s + 4) as usize, vt(s + 8) as usize, vt(s + 12) as usize);
                        let vid = |i: usize| u16_at(&vtx, s + vo + i * 9 + 4).map(|v| v as usize);
                        let idx = |i: usize| u16_at(&vtx, s + io + i * 2).map(|v| v as usize);
                        let mut t = 0;
                        while t + 2 < ni {
                            let mut tri = [0u32; 3];
                            let mut ok = true;
                            for c in 0..3 {
                                match idx(t + c).filter(|&i| i < nv).and_then(vid) {
                                    Some(v) if vstart + voff + v < vertices.len() => tri[c] = (vstart + voff + v) as u32,
                                    _ => ok = false,
                                }
                            }
                            if ok { tris.push(tri); }
                            t += 3;
                        }
                    }
                    meshes.push(Mesh { material, tris });
                }
            }
        }
        Ok(Model { vertices, meshes, materials, skins, bones, parents, pose_to_bone })
    }
}

// a strip group header's counts look sane at `at`
fn strip_ok(d: &[u8], at: usize) -> bool {
    match (i32_at(d, at), i32_at(d, at + 8)) {
        (Some(nv), Some(ni)) => (0..1_000_000).contains(&nv) && (0..3_000_000).contains(&ni) && ni % 3 == 0,
        _ => false,
    }
}

/// Source's AngleMatrix (pitch, yaw, roll in degrees): a model's space turned into the map's
pub fn angle_matrix(a: V3) -> [[f32; 3]; 3] {
    let (sp, cp) = a[0].to_radians().sin_cos();
    let (sy, cy) = a[1].to_radians().sin_cos();
    let (sr, cr) = a[2].to_radians().sin_cos();
    [
        [cp * cy, sr * sp * cy - cr * sy, cr * sp * cy + sr * sy],
        [cp * sy, sr * sp * sy + cr * cy, cr * sp * sy - sr * cy],
        [-sp, sr * cp, cr * cp],
    ]
}

pub fn rotate(m: &[[f32; 3]; 3], v: V3) -> V3 {
    [m[0][0] * v[0] + m[0][1] * v[1] + m[0][2] * v[2], m[1][0] * v[0] + m[1][1] * v[1] + m[1][2] * v[2], m[2][0] * v[0] + m[2][1] * v[1] + m[2][2] * v[2]]
}
