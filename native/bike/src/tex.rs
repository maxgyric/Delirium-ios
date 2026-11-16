//! Xbox 360 textures out of a GTA IV texture dictionary (.xtd, or one inside a model): the GPU's fetch constant
//! (format, size, tiling, byte order), the tiled layout undone, DXT decoded; RGBA8 out.
use delirium_data::resource::{system_size, Resource};

pub struct Tex {
    pub name: String,
    pub w: u32,
    pub h: u32,
    pub rgba: Vec<u8>,
    pub alpha: bool,
}

fn be32(d: &[u8], at: usize) -> Option<u32> {
    d.get(at..at + 4).map(|b| u32::from_be_bytes(b.try_into().unwrap()))
}

fn ptr(r: &Resource, p: u32) -> Option<usize> {
    let off = (p & 0x0FFF_FFFF) as usize;
    match p >> 28 {
        5 => Some(off),
        6 => Some(system_size(r.flags) + off),
        _ => None,
    }
}

/// "pack:/pcj_detail.dds" -> "pcj_detail"
pub fn key(name: &str) -> String {
    let n = name.rsplit(['/', ':', '\\']).next().unwrap_or(name).to_ascii_lowercase();
    n.strip_suffix(".dds").unwrap_or(&n).to_string()
}

/// Every texture of the dictionary at `dict` (a pgDictionary: entries + hashes).
pub fn read_dict(r: &Resource, entries: &[(u32, usize)]) -> Vec<Tex> {
    entries.iter().filter_map(|&(_, at)| read_one(r, at)).collect()
}

fn read_one(r: &Resource, at: usize) -> Option<Tex> {
    let d = &r.data;
    let name_at = ptr(r, be32(d, at + 0x14)?)?;
    let name = String::from_utf8_lossy(d.get(name_at..)?.split(|&b| b == 0).next()?).into_owned();
    let hdr = ptr(r, be32(d, at + 0x18)?)?;
    let (d0, d1, d2) = (be32(d, hdr + 0x1C)?, be32(d, hdr + 0x20)?, be32(d, hdr + 0x24)?);
    let tiled = d0 >> 31 != 0;
    let pitch = (((d0 >> 22) & 0x1FF) * 32).max(32);
    let fmt = d1 & 0x3F;
    let endian = (d1 >> 6) & 3;
    let base = ptr(r, d1 & 0xFFFF_F000)?;
    let w = (d2 & 0x1FFF) + 1;
    let h = ((d2 >> 13) & 0x1FFF) + 1;
    let (block, bpb) = match fmt {
        18 => (4, 8),           // DXT1
        19 | 20 => (4, 16),     // DXT3 / DXT5
        6 => (1, 4),            // A8R8G8B8
        4 => (1, 2),            // R5G6B5
        2 => (1, 1),            // L8
        _ => return None,
    };
    let bw = w.div_ceil(block);
    let bh = h.div_ceil(block);
    let pw = (pitch / block).max(bw).next_multiple_of(32);
    let ph = bh.next_multiple_of(32);
    let size = (pw * ph * bpb) as usize;
    let src = d.get(base..(base + size).min(d.len()))?;
    let mut lin = vec![0u8; (bw * bh * bpb) as usize];
    for y in 0..bh {
        for x in 0..bw {
            let from = if tiled { tiled_offset(x, y, pw, bpb) } else { y * pw + x } as usize * bpb as usize;
            let to = ((y * bw + x) * bpb) as usize;
            if let Some(s) = src.get(from..from + bpb as usize) {
                lin[to..to + bpb as usize].copy_from_slice(s);
            }
        }
    }
    match endian {
        1 => lin.chunks_exact_mut(2).for_each(|c| c.swap(0, 1)),
        2 => lin.chunks_exact_mut(4).for_each(|c| c.reverse()),
        3 => lin.chunks_exact_mut(4).for_each(|c| { c.swap(0, 2); c.swap(1, 3); }),
        _ => {}
    }
    let mut rgba = vec![0u8; (w * h * 4) as usize];
    let mut alpha = false;
    match fmt {
        18 | 19 | 20 => {
            for by in 0..bh {
                for bx in 0..bw {
                    let b = &lin[((by * bw + bx) * bpb) as usize..][..bpb as usize];
                    let px = dxt_block(b, fmt);
                    for (i, p) in px.iter().enumerate() {
                        let (x, y) = (bx * 4 + i as u32 % 4, by * 4 + i as u32 / 4);
                        if x < w && y < h {
                            rgba[((y * w + x) * 4) as usize..][..4].copy_from_slice(p);
                            alpha |= p[3] < 250;
                        }
                    }
                }
            }
        }
        6 => {
            for i in 0..(w * h) as usize {
                // A8R8G8B8 big-endian words, after the swap: B G R A in memory order on a little-endian read
                let s = &lin[i * 4..i * 4 + 4];
                rgba[i * 4..i * 4 + 4].copy_from_slice(&[s[1], s[2], s[3], s[0]]);
                alpha |= s[0] < 250;
            }
        }
        4 => {
            for i in 0..(w * h) as usize {
                let c = u16::from_le_bytes([lin[i * 2 + 1], lin[i * 2]]);
                rgba[i * 4..i * 4 + 4].copy_from_slice(&rgb565(c));
            }
        }
        _ => {
            for i in 0..(w * h) as usize {
                let l = lin[i];
                rgba[i * 4..i * 4 + 4].copy_from_slice(&[l, l, l, 255]);
            }
        }
    }
    Some(Tex { name, w, h, rgba, alpha })
}

// XGAddress2DTiledOffset: the Xbox 360's tiled layout (in blocks of `bpb` bytes, a row `pw` blocks wide)
fn tiled_offset(x: u32, y: u32, pw: u32, bpb: u32) -> u32 {
    let aligned = (pw + 31) & !31;
    let log_bpp = (bpb >> 2) + ((bpb >> 1) >> (bpb >> 2));
    let macro_ = ((x >> 5) + (y >> 5) * (aligned >> 5)) << (log_bpp + 7);
    let micro = ((x & 7) + ((y & 6) << 2)) << log_bpp;
    let offset = macro_ + ((micro & !15) << 1) + (micro & 15) + ((y & 8) << (3 + log_bpp)) + ((y & 1) << 4);
    (((offset & !511) << 3) + ((offset & 448) << 2) + (offset & 63) + ((y & 16) << 7) + ((((y & 8) >> 2) + (x >> 3)) & 3) * 64) >> log_bpp
}

fn rgb565(c: u16) -> [u8; 4] {
    let r = ((c >> 11) & 31) as u32;
    let g = ((c >> 5) & 63) as u32;
    let b = (c & 31) as u32;
    [(r * 255 / 31) as u8, (g * 255 / 63) as u8, (b * 255 / 31) as u8, 255]
}

fn dxt_block(b: &[u8], fmt: u32) -> [[u8; 4]; 16] {
    let (cb, ab) = if fmt == 18 { (b, None) } else { (&b[8..], Some(&b[..8])) };
    let c0 = u16::from_le_bytes([cb[0], cb[1]]);
    let c1 = u16::from_le_bytes([cb[2], cb[3]]);
    let (p0, p1) = (rgb565(c0), rgb565(c1));
    let mix = |a: u8, b: u8, wa: u32, wb: u32| ((a as u32 * wa + b as u32 * wb) / (wa + wb)) as u8;
    let mut pal = [p0, p1, [0; 4], [0; 4]];
    if c0 > c1 || fmt != 18 {
        pal[2] = [mix(p0[0], p1[0], 2, 1), mix(p0[1], p1[1], 2, 1), mix(p0[2], p1[2], 2, 1), 255];
        pal[3] = [mix(p0[0], p1[0], 1, 2), mix(p0[1], p1[1], 1, 2), mix(p0[2], p1[2], 1, 2), 255];
    } else {
        pal[2] = [mix(p0[0], p1[0], 1, 1), mix(p0[1], p1[1], 1, 1), mix(p0[2], p1[2], 1, 1), 255];
        pal[3] = [0, 0, 0, 0];
    }
    let bits = u32::from_le_bytes([cb[4], cb[5], cb[6], cb[7]]);
    let mut out = [[0u8; 4]; 16];
    for (i, o) in out.iter_mut().enumerate() {
        *o = pal[((bits >> (i * 2)) & 3) as usize];
    }
    match (fmt, ab) {
        (19, Some(a)) => {
            for (i, o) in out.iter_mut().enumerate() {
                let n = (a[i / 2] >> ((i % 2) * 4)) & 15;
                o[3] = n * 17;
            }
        }
        (20, Some(a)) => {
            let (a0, a1) = (a[0] as u32, a[1] as u32);
            let mut ap = [a0, a1, 0, 0, 0, 0, 0, 0];
            if a0 > a1 {
                for k in 1..7 { ap[k + 1] = ((7 - k as u32) * a0 + k as u32 * a1) / 7; }
            } else {
                for k in 1..5 { ap[k + 1] = ((5 - k as u32) * a0 + k as u32 * a1) / 5; }
                ap[6] = 0;
                ap[7] = 255;
            }
            let bits = u64::from_le_bytes([a[2], a[3], a[4], a[5], a[6], a[7], 0, 0]);
            for (i, o) in out.iter_mut().enumerate() {
                o[3] = ap[((bits >> (i * 3)) & 7) as usize] as u8;
            }
        }
        _ => {}
    }
    out
}

/// A PNG of RGBA8 pixels.
pub fn png(w: u32, h: u32, rgba: &[u8]) -> Vec<u8> {
    let mut raw = Vec::with_capacity((w * h * 4 + h) as usize);
    for y in 0..h as usize {
        raw.push(0);
        raw.extend_from_slice(&rgba[y * w as usize * 4..(y + 1) * w as usize * 4]);
    }
    let z = miniz_oxide::deflate::compress_to_vec_zlib(&raw, 6);
    let mut out = b"\x89PNG\r\n\x1a\n".to_vec();
    let mut chunk = |t: &[u8], data: &[u8]| {
        out.extend_from_slice(&(data.len() as u32).to_be_bytes());
        let mut c = t.to_vec();
        c.extend_from_slice(data);
        out.extend_from_slice(&c);
        out.extend_from_slice(&crc32(&c).to_be_bytes());
    };
    let mut ihdr = vec![];
    ihdr.extend_from_slice(&w.to_be_bytes());
    ihdr.extend_from_slice(&h.to_be_bytes());
    ihdr.extend_from_slice(&[8, 6, 0, 0, 0]);
    chunk(b"IHDR", &ihdr);
    chunk(b"IDAT", &z);
    chunk(b"IEND", &[]);
    out
}

fn crc32(d: &[u8]) -> u32 {
    let mut c = 0xFFFF_FFFFu32;
    for &b in d {
        c ^= b as u32;
        for _ in 0..8 {
            c = if c & 1 != 0 { 0xEDB8_8320 ^ (c >> 1) } else { c >> 1 };
        }
    }
    !c
}
