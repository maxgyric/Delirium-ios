//! Valve's packs (.vpk, versions 1 and 2): a "_dir.vpk" with the tree of every file in it (extension, folder,
//! name) and where each one is: in the dir file itself, or in one of its numbered archives ("_000.vpk"...) beside
//! it. Read from your own game's files.

use std::collections::HashMap;
use std::fs::File;
use std::io::{Read, Seek, SeekFrom};
use std::path::{Path, PathBuf};

pub struct Entry {
    archive: u16,
    offset: u32,
    length: u32,
    preload: Vec<u8>,
}

pub struct Vpk {
    dir: PathBuf,
    data_at: u64, // where a dir file's own data starts (archive 0x7fff)
    pub files: HashMap<String, Entry>,
}

fn cstr(d: &[u8], at: &mut usize) -> Result<String, String> {
    let s = d.get(*at..).ok_or("vpk: tree past the end")?;
    let end = s.iter().position(|&c| c == 0).ok_or("vpk: tree past the end")?;
    *at += end + 1;
    Ok(String::from_utf8_lossy(&s[..end]).into_owned())
}

impl Vpk {
    pub fn open(dir_path: &Path) -> Result<Vpk, String> {
        let d = std::fs::read(dir_path).map_err(|e| format!("{}: {e}", dir_path.display()))?;
        let u32_at = |at: usize| -> Result<u32, String> { d.get(at..at + 4).map(|b| u32::from_le_bytes(b.try_into().unwrap())).ok_or("vpk: short".to_string()) };
        if u32_at(0)? != 0x55aa1234 { return Err(format!("{}: not a .vpk", dir_path.display())); }
        let version = u32_at(4)?;
        let tree = u32_at(8)? as usize;
        let header = match version { 1 => 12, 2 => 28, v => return Err(format!("vpk version {v}")) };
        let mut at = header;
        let end = header + tree;
        let mut files = HashMap::new();
        loop {
            let ext = cstr(&d, &mut at)?;
            if ext.is_empty() || at > end { break; }
            loop {
                let folder = cstr(&d, &mut at)?;
                if folder.is_empty() { break; }
                loop {
                    let name = cstr(&d, &mut at)?;
                    if name.is_empty() { break; }
                    let e = d.get(at..at + 18).ok_or("vpk: entry past the end")?;
                    let preload_len = u16::from_le_bytes([e[4], e[5]]) as usize;
                    let archive = u16::from_le_bytes([e[6], e[7]]);
                    let offset = u32::from_le_bytes(e[8..12].try_into().unwrap());
                    let length = u32::from_le_bytes(e[12..16].try_into().unwrap());
                    at += 18;
                    let preload = d.get(at..at + preload_len).ok_or("vpk: preload past the end")?.to_vec();
                    at += preload_len;
                    let path = if folder == " " { format!("{name}.{ext}") } else { format!("{folder}/{name}.{ext}") };
                    files.insert(path.to_ascii_lowercase(), Entry { archive, offset, length, preload });
                }
            }
        }
        Ok(Vpk { dir: dir_path.to_path_buf(), data_at: end as u64, files })
    }

    fn archive_path(&self, k: u16) -> PathBuf {
        let s = self.dir.to_string_lossy();
        let base = s.strip_suffix("_dir.vpk").unwrap_or(&s);
        PathBuf::from(format!("{base}_{k:03}.vpk"))
    }

    /// Which numbered archives exist beside the dir file (and which are missing)
    pub fn archives(&self) -> (Vec<u16>, Vec<u16>) {
        let mut used: Vec<u16> = self.files.values().map(|e| e.archive).filter(|&a| a != 0x7fff).collect();
        used.sort();
        used.dedup();
        used.into_iter().partition(|&a| self.archive_path(a).is_file())
    }

    /// A file by its path ("materials/nature/grass.vtf"), if it's in the pack and its archive is there
    pub fn read(&self, path: &str) -> Option<Vec<u8>> {
        let e = self.files.get(&path.to_ascii_lowercase())?;
        let mut out = e.preload.clone();
        if e.length > 0 {
            let (file, at) = if e.archive == 0x7fff { (self.dir.clone(), self.data_at + e.offset as u64) } else { (self.archive_path(e.archive), e.offset as u64) };
            let mut f = File::open(file).ok()?;
            f.seek(SeekFrom::Start(at)).ok()?;
            let mut b = vec![0u8; e.length as usize];
            f.read_exact(&mut b).ok()?;
            out.extend_from_slice(&b);
        }
        Some(out)
    }
}
