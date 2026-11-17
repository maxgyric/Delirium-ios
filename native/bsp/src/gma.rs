//! Garry's Mod addons (.gma, "GMAD" versions 1-3): a header (who made it, its name), a table of its files (path,
//! size) and their bytes one after another. A Workshop download (garrysmod/cache/workshop, steamapps/workshop) is
//! often that whole file LZMA-compressed ("alone" .lzma: 5 property bytes, its size, the stream): unpacked here.

use std::collections::HashMap;
use std::path::Path;

pub struct Gma {
    pub name: String,
    // the addon's bytes: the whole of it unpacked (a Workshop download), or read from its file as asked (a big
    // addon is never all in memory)
    data: Option<Vec<u8>>,
    file: Option<std::path::PathBuf>,
    /// path (lower case, '/') -> (offset, length)
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
        use std::io::Read;
        let mut f = std::fs::File::open(path).map_err(|e| format!("{}: {e}", path.display()))?;
        let mut head = vec![0u8; 4];
        f.read_exact(&mut head).map_err(|e| format!("{}: {e}", path.display()))?;
        if head != b"GMAD" {
            let raw = std::fs::read(path).map_err(|e| format!("{}: {e}", path.display()))?;
            return Gma::from_bytes(raw);
        }
        // just its header and table (more of it read till the table's all there)
        let len = f.metadata().map(|m| m.len() as usize).unwrap_or(0);
        let mut take = 1 << 16;
        loop {
            let mut buf = vec![0u8; take.min(len)];
            let mut f = std::fs::File::open(path).map_err(|e| e.to_string())?;
            f.read_exact(&mut buf).map_err(|e| e.to_string())?;
            match Gma::parse(&buf, len) {
                Ok((name, files, order)) => return Ok(Gma { name, data: None, file: Some(path.to_path_buf()), files, order }),
                Err(e) if e == "short" && take < len => take *= 4,
                Err(e) => return Err(if e == "short" { "gma: its table is cut short".into() } else { e }),
            }
        }
    }

    pub fn from_bytes(raw: Vec<u8>) -> Result<Gma, String> {
        // compressed (a Workshop download): the LZMA header's first byte is its properties (0x5d usually)
        let data = if raw.get(0..4) == Some(b"GMAD") { raw } else if raw.first().is_some_and(|&p| p < 225) && raw.len() > 13 { unlzma(&raw)? } else { raw };
        let (name, files, order) = Gma::parse(&data, data.len()).map_err(|e| if e == "short" { "gma: its table is cut short".to_string() } else { e })?;
        Ok(Gma { name, data: Some(data), file: None, files, order })
    }

    // the header and table out of the addon's first bytes (`total`: the whole addon's size): "short" if they go on
    fn parse(data: &[u8], total: usize) -> Result<(String, HashMap<String, (usize, usize)>, Vec<String>), String> {
        if data.get(0..4) != Some(b"GMAD") { return Err("not a Garry's Mod addon (.gma)".into()); }
        let version = data[4];
        let mut at = 5 + 8 + 8; // steam id, time stamp
        let cstr = |at: &mut usize| -> Result<String, String> {
            let s = data.get(*at..).ok_or("short")?;
            let end = s.iter().position(|&c| c == 0).ok_or("short")?;
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
            let idx = u32::from_le_bytes(data.get(at..at + 4).ok_or("short")?.try_into().unwrap());
            at += 4;
            if idx == 0 { break; }
            let p = cstr(&mut at)?;
            let size = i64::from_le_bytes(data.get(at..at + 8).ok_or("short")?.try_into().unwrap()) as usize;
            at += 12; // size, crc
            table.push((p.replace('\\', "/").to_ascii_lowercase(), size));
        }
        let mut files = HashMap::new();
        let mut order = vec![];
        for (p, size) in table {
            if at + size > total { return Err(format!("gma: {p} past the end")); }
            files.insert(p.clone(), (at, size));
            order.push(p);
            at += size;
        }
        Ok((name, files, order))
    }

    pub fn read(&self, path: &str) -> Option<Vec<u8>> {
        use std::io::{Read, Seek, SeekFrom};
        let &(at, len) = self.files.get(&path.to_ascii_lowercase())?;
        if let Some(d) = &self.data { return d.get(at..at + len).map(|b| b.to_vec()); }
        let mut f = std::fs::File::open(self.file.as_ref()?).ok()?;
        f.seek(SeekFrom::Start(at as u64)).ok()?;
        let mut b = vec![0u8; len];
        f.read_exact(&mut b).ok()?;
        Some(b)
    }

    /// Its maps (maps/*.bsp)
    pub fn maps(&self) -> Vec<String> {
        self.order.iter().filter(|p| p.starts_with("maps/") && p.ends_with(".bsp")).cloned().collect()
    }
}
