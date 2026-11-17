//! Garry's Mod addons (.gma, "GMAD" versions 1-3): a header (who made it, its name), a table of its files (path,
//! size) and their bytes one after another. A Workshop download (garrysmod/cache/workshop, steamapps/workshop) is
//! often that whole file LZMA-compressed ("alone" .lzma: 5 property bytes, its size, the stream): unpacked here.

use std::collections::HashMap;
use std::path::Path;

pub struct Gma {
    pub name: String,
    data: Vec<u8>,
    /// path (lower case, '/') -> (offset, length) in `data`
    pub files: HashMap<String, (usize, usize)>,
    /// in the addon's own order
    pub order: Vec<String>,
}

/// An LZMA "alone" stream (what the Workshop and Source's compressed lumps use) unpacked
pub fn unlzma(d: &[u8]) -> Result<Vec<u8>, String> {
    let mut out = vec![];
    lzma_rs::lzma_decompress(&mut std::io::Cursor::new(d), &mut out).map_err(|e| format!("lzma: {e:?}"))?;
    Ok(out)
}

impl Gma {
    pub fn open(path: &Path) -> Result<Gma, String> {
        let raw = std::fs::read(path).map_err(|e| format!("{}: {e}", path.display()))?;
        Gma::from_bytes(raw)
    }

    pub fn from_bytes(raw: Vec<u8>) -> Result<Gma, String> {
        // compressed (a Workshop download): the LZMA header's first byte is its properties (0x5d usually)
        let data = if raw.get(0..4) == Some(b"GMAD") { raw } else if raw.first().is_some_and(|&p| p < 225) && raw.len() > 13 { unlzma(&raw)? } else { raw };
        if data.get(0..4) != Some(b"GMAD") { return Err("not a Garry's Mod addon (.gma)".into()); }
        let version = data[4];
        let mut at = 5 + 8 + 8; // steam id, time stamp
        let cstr = |at: &mut usize| -> Result<String, String> {
            let s = data.get(*at..).ok_or("gma: short")?;
            let end = s.iter().position(|&c| c == 0).ok_or("gma: short")?;
            *at += end + 1;
            Ok(String::from_utf8_lossy(&s[..end]).into_owned())
        };
        if version > 1 {
            while !cstr(&mut at)?.is_empty() {} // required content
        }
        let name = cstr(&mut at)?;
        let _description = cstr(&mut at)?;
        let _author = cstr(&mut at)?;
        at += 4; // its version
        let mut table = vec![];
        loop {
            let idx = u32::from_le_bytes(data.get(at..at + 4).ok_or("gma: short")?.try_into().unwrap());
            at += 4;
            if idx == 0 { break; }
            let p = cstr(&mut at)?;
            let size = i64::from_le_bytes(data.get(at..at + 8).ok_or("gma: short")?.try_into().unwrap()) as usize;
            at += 12; // size, crc
            table.push((p.replace('\\', "/").to_ascii_lowercase(), size));
        }
        let mut files = HashMap::new();
        let mut order = vec![];
        for (p, size) in table {
            if at + size > data.len() { return Err(format!("gma: {p} past the end")); }
            files.insert(p.clone(), (at, size));
            order.push(p);
            at += size;
        }
        Ok(Gma { name, data, files, order })
    }

    pub fn read(&self, path: &str) -> Option<&[u8]> {
        let &(at, len) = self.files.get(&path.to_ascii_lowercase())?;
        self.data.get(at..at + len)
    }

    /// Its maps (maps/*.bsp)
    pub fn maps(&self) -> Vec<String> {
        self.order.iter().filter(|p| p.starts_with("maps/") && p.ends_with(".bsp")).cloned().collect()
    }
}
