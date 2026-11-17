//! Where a map's materials come from: its own pakfile (a zip inside the .bsp), then your game's packs (.vpk) and loose
//! folders (a "materials" folder's parent), in that order. A material (.vmt) names its texture (.vtf); a cubemap
//! "patch" material includes another. Pictures as PNG for the .glb.

use crate::vpk::Vpk;
use std::collections::HashMap;
use std::path::{Path, PathBuf};

/// The pakfile (a zip, its entries stored or deflated): name -> bytes
pub fn read_zip(z: &[u8]) -> HashMap<String, Vec<u8>> {
    let mut out = HashMap::new();
    // the end of central directory record
    let Some(eocd) = (0..z.len().saturating_sub(21)).rev().take(70000).find(|&i| z[i..i + 4] == [0x50, 0x4b, 0x05, 0x06]) else { return out };
    let u16_at = |at: usize| z.get(at..at + 2).map(|b| u16::from_le_bytes([b[0], b[1]]) as usize).unwrap_or(0);
    let u32_at = |at: usize| z.get(at..at + 4).map(|b| u32::from_le_bytes(b.try_into().unwrap()) as usize).unwrap_or(0);
    let (count, mut at) = (u16_at(eocd + 10), u32_at(eocd + 16));
    for _ in 0..count {
        if z.get(at..at + 4) != Some(&[0x50, 0x4b, 0x01, 0x02]) { break; }
        let method = u16_at(at + 10);
        let csize = u32_at(at + 20);
        let full = u32_at(at + 24);
        let (nlen, xlen, clen) = (u16_at(at + 28), u16_at(at + 30), u16_at(at + 32));
        let local = u32_at(at + 42);
        let name = String::from_utf8_lossy(z.get(at + 46..at + 46 + nlen).unwrap_or_default()).replace('\\', "/").to_ascii_lowercase();
        at += 46 + nlen + xlen + clen;
        if z.get(local..local + 4) != Some(&[0x50, 0x4b, 0x03, 0x04]) { continue; }
        let data_at = local + 30 + u16_at(local + 26) + u16_at(local + 28);
        let Some(d) = z.get(data_at..data_at + csize) else { continue };
        let bytes = match method {
            0 => d.to_vec(),
            8 => match miniz_oxide::inflate::decompress_to_vec(d) { Ok(b) => b, Err(_) => continue },
            14 => {
                // zip's LZMA: 4 bytes (version, properties' size), 5 properties, the stream; its size the entry's
                if d.len() < 9 { continue; }
                let size = full as u64;
                let mut alone = d[4..9].to_vec();
                alone.extend_from_slice(&size.to_le_bytes());
                alone.extend_from_slice(&d[9..]);
                match crate::gma::unlzma(&alone) { Ok(b) => b, Err(_) => continue }
            }
            _ => continue,
        };
        out.insert(name, bytes);
    }
    out
}

pub struct Content {
    pak: HashMap<String, Vec<u8>>,
    gma: Option<crate::gma::Gma>, // the addon the map came in
    vpks: Vec<Vpk>,
    loose: Vec<PathBuf>,
    pub missing_archives: usize,
}

/// Every "_dir.vpk" and every folder with a "materials" folder in it, under `root` (a few levels down)
fn scan(root: &Path, depth: usize, vpks: &mut Vec<PathBuf>, loose: &mut Vec<PathBuf>) {
    let Ok(rd) = std::fs::read_dir(root) else { return };
    for e in rd.flatten() {
        let p = e.path();
        let n = e.file_name().to_string_lossy().to_ascii_lowercase();
        if p.is_dir() {
            if n == "materials" { loose.push(root.to_path_buf()); } else if depth > 0 { scan(&p, depth - 1, vpks, loose); }
        } else if n.ends_with("_dir.vpk") {
            vpks.push(p);
        }
    }
}

impl Content {
    pub fn new(pakfile: &[u8], roots: &[PathBuf], gma: Option<crate::gma::Gma>) -> Content {
        let (mut vp, mut loose) = (vec![], vec![]);
        for r in roots { scan(r, 4, &mut vp, &mut loose); }
        vp.sort();
        vp.dedup();
        loose.sort();
        loose.dedup();
        // Garry's Mod's own first, then the games it mounts
        vp.sort_by_key(|p| { let n = p.to_string_lossy().to_ascii_lowercase(); if n.contains("garrysmod") { 0 } else if n.contains("hl2") { 1 } else { 2 } });
        let mut vpks = vec![];
        let mut missing_archives = 0;
        for p in vp {
            if let Ok(v) = Vpk::open(&p) {
                missing_archives += v.archives().1.len();
                vpks.push(v);
            }
        }
        Content { pak: read_zip(pakfile), gma, vpks, loose, missing_archives }
    }

    pub fn packs(&self) -> usize { self.vpks.len() }

    pub fn find(&self, path: &str) -> Option<Vec<u8>> {
        let path = path.replace('\\', "/").to_ascii_lowercase();
        if let Some(b) = self.pak.get(&path) { return Some(b.clone()); }
        if let Some(b) = self.gma.as_ref().and_then(|g| g.read(&path)) { return Some(b.to_vec()); }
        for v in &self.vpks {
            if let Some(b) = v.read(&path) { return Some(b); }
        }
        for l in &self.loose {
            if let Ok(b) = std::fs::read(l.join(&path)) { return Some(b); }
        }
        None
    }

    /// A material by its name ("concrete/concretefloor001a"): its texture's name and how it's drawn
    pub fn material(&self, name: &str) -> Option<Vmt> {
        let mut path = format!("materials/{}.vmt", name.trim_end_matches(".vmt"));
        for _ in 0..4 {
            let text = String::from_utf8_lossy(&self.find(&path)?).into_owned();
            let kv = keyvalues(&text);
            let shader = kv.first().map(|(k, _)| k.to_ascii_lowercase()).unwrap_or_default();
            let get = |k: &str| kv.iter().find(|(a, _)| a.eq_ignore_ascii_case(k)).map(|(_, v)| v.clone());
            if shader == "patch" {
                // a cubemap patch (maps/<map>/...): the material it includes, its own $basetexture if inserted
                let inc = get("include")?;
                if let Some(b) = get("$basetexture") { return Some(Vmt { base: Some(b), translucent: false, alphatest: false, shader }); }
                path = inc.replace('\\', "/").to_ascii_lowercase();
                continue;
            }
            let flag = |k: &str| get(k).map(|v| v.trim() == "1").unwrap_or(false);
            return Some(Vmt { base: get("$basetexture"), translucent: flag("$translucent"), alphatest: flag("$alphatest"), shader });
        }
        None
    }
}

pub struct Vmt {
    pub base: Option<String>,
    pub translucent: bool,
    pub alphatest: bool,
    pub shader: String,
}

/// A VMT's KeyValues flattened: (key, value) for every pair at any depth; the first key is the shader's name
fn keyvalues(text: &str) -> Vec<(String, String)> {
    let mut toks = vec![];
    let mut it = text.chars().peekable();
    while let Some(&c) = it.peek() {
        if c.is_whitespace() { it.next(); continue; }
        if c == '/' {
            it.next();
            if it.peek() == Some(&'/') { for c in it.by_ref() { if c == '\n' { break; } } }
            continue;
        }
        if c == '{' || c == '}' { toks.push(c.to_string()); it.next(); continue; }
        let mut s = String::new();
        if c == '"' {
            it.next();
            for c in it.by_ref() { if c == '"' { break; } s.push(c); }
        } else {
            while let Some(&c) = it.peek() {
                if c.is_whitespace() || c == '{' || c == '}' || c == '"' { break; }
                s.push(c);
                it.next();
            }
        }
        toks.push(s);
    }
    let mut out = vec![];
    let mut i = 0;
    while i < toks.len() {
        let t = &toks[i];
        if t == "{" || t == "}" { i += 1; continue; }
        let next = toks.get(i + 1).cloned().unwrap_or_default();
        if next == "{" { out.push((t.clone(), String::new())); i += 2; } else { out.push((t.clone(), next)); i += 2; }
    }
    out
}

fn crc32(d: &[u8]) -> u32 {
    let mut table = [0u32; 256];
    for i in 0..256u32 {
        let mut c = i;
        for _ in 0..8 { c = if c & 1 != 0 { 0xedb88320 ^ (c >> 1) } else { c >> 1 }; }
        table[i as usize] = c;
    }
    let mut c = 0xffffffffu32;
    for &b in d { c = table[((c ^ b as u32) & 0xff) as usize] ^ (c >> 8); }
    c ^ 0xffffffff
}

/// RGBA (top row first) as a PNG: RGB when it's all opaque
pub fn png(w: usize, h: usize, rgba: &[u8]) -> Vec<u8> {
    let opaque = rgba.chunks_exact(4).all(|p| p[3] == 255);
    let bpp = if opaque { 3 } else { 4 };
    let mut raw = Vec::with_capacity(h * (w * bpp + 1));
    for y in 0..h {
        raw.push(1); // "Sub": each byte less the one a pixel before (smaller when deflated)
        let row = &rgba[y * w * 4..(y + 1) * w * 4];
        for x in 0..w {
            for c in 0..bpp {
                let v = row[x * 4 + c];
                let left = if x > 0 { row[(x - 1) * 4 + c] } else { 0 };
                raw.push(v.wrapping_sub(left));
            }
        }
    }
    let z = miniz_oxide::deflate::compress_to_vec_zlib(&raw, 3);
    let mut out = vec![0x89, b'P', b'N', b'G', 0x0d, 0x0a, 0x1a, 0x0a];
    let mut chunk = |kind: &[u8; 4], body: &[u8]| {
        out.extend_from_slice(&(body.len() as u32).to_be_bytes());
        let mut c = kind.to_vec();
        c.extend_from_slice(body);
        out.extend_from_slice(&c);
        out.extend_from_slice(&crc32(&c).to_be_bytes());
    };
    let mut ihdr = vec![];
    ihdr.extend_from_slice(&(w as u32).to_be_bytes());
    ihdr.extend_from_slice(&(h as u32).to_be_bytes());
    ihdr.extend_from_slice(&[8, if opaque { 2 } else { 6 }, 0, 0, 0]);
    chunk(b"IHDR", &ihdr);
    chunk(b"IDAT", &z);
    chunk(b"IEND", &[]);
    out
}
