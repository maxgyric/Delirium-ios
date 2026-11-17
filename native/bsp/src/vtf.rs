//! Valve's textures (.vtf, 7.0-7.5): one picture out of the mip chain (the largest no bigger than asked for, its first
//! frame and face) as 8-bit RGBA, top row first. DXT1/3/5 and the plain 8-bit formats are decoded.

fn rgb565(c: u16) -> [u8; 3] {
    let (r, g, b) = ((c >> 11) & 31, (c >> 5) & 63, c & 31);
    [((r << 3) | (r >> 2)) as u8, ((g << 2) | (g >> 4)) as u8, ((b << 3) | (b >> 2)) as u8]
}

fn block_colors(b: &[u8], dxt1: bool) -> [[u8; 4]; 4] {
    let (c0, c1) = (u16::from_le_bytes([b[0], b[1]]), u16::from_le_bytes([b[2], b[3]]));
    let (a, z) = (rgb565(c0), rgb565(c1));
    let mix = |p: u16, q: u16, d: u16| -> [u8; 4] {
        let f = |i: usize| ((a[i] as u16 * p + z[i] as u16 * q) / d) as u8;
        [f(0), f(1), f(2), 255]
    };
    if c0 > c1 || !dxt1 {
        [[a[0], a[1], a[2], 255], [z[0], z[1], z[2], 255], mix(2, 1, 3), mix(1, 2, 3)]
    } else {
        [[a[0], a[1], a[2], 255], [z[0], z[1], z[2], 255], mix(1, 1, 2), [0, 0, 0, 0]]
    }
}

fn dxt(data: &[u8], w: usize, h: usize, kind: u32) -> Option<Vec<u8>> {
    let bs = if kind == 1 { 8 } else { 16 };
    let (bw, bh) = (w.div_ceil(4).max(1), h.div_ceil(4).max(1));
    if data.len() < bw * bh * bs { return None; }
    let mut out = vec![0u8; w * h * 4];
    for by in 0..bh {
        for bx in 0..bw {
            let b = &data[(by * bw + bx) * bs..][..bs];
            let (alpha, color) = if kind == 1 { (None, b) } else { (Some(&b[..8]), &b[8..16]) };
            let cols = block_colors(color, kind == 1);
            let idx = u32::from_le_bytes(color[4..8].try_into().unwrap());
            let mut alphas = [255u8; 16];
            if let Some(a) = alpha {
                if kind == 3 {
                    for i in 0..16 {
                        let v = (a[i / 2] >> ((i % 2) * 4)) & 15;
                        alphas[i] = v * 17;
                    }
                } else {
                    let (a0, a1) = (a[0] as u32, a[1] as u32);
                    let mut t = [0u32; 8];
                    t[0] = a0;
                    t[1] = a1;
                    if a0 > a1 {
                        for i in 1..7 { t[i + 1] = ((7 - i as u32) * a0 + i as u32 * a1) / 7; }
                    } else {
                        for i in 1..5 { t[i + 1] = ((5 - i as u32) * a0 + i as u32 * a1) / 5; }
                        t[6] = 0;
                        t[7] = 255;
                    }
                    let bits = u64::from_le_bytes([a[2], a[3], a[4], a[5], a[6], a[7], 0, 0]);
                    for i in 0..16 { alphas[i] = t[((bits >> (3 * i)) & 7) as usize] as u8; }
                }
            }
            for py in 0..4 {
                for px in 0..4 {
                    let (x, y) = (bx * 4 + px, by * 4 + py);
                    if x >= w || y >= h { continue; }
                    let i = py * 4 + px;
                    let mut c = cols[((idx >> (2 * i)) & 3) as usize];
                    if kind != 1 { c[3] = alphas[i]; }
                    out[(y * w + x) * 4..][..4].copy_from_slice(&c);
                }
            }
        }
    }
    Some(out)
}

/// Bytes a picture of `w` x `h` takes in format `f` (None: a format not read)
fn size_of(f: u32, w: usize, h: usize) -> Option<usize> {
    let (w, h) = (w.max(1), h.max(1));
    Some(match f {
        13 | 20 => w.div_ceil(4) * h.div_ceil(4) * 8,
        14 | 15 => w.div_ceil(4) * h.div_ceil(4) * 16,
        0 | 1 | 11 | 12 | 16 | 23 | 26 => w * h * 4,
        2 | 3 | 9 | 10 => w * h * 3,
        4 | 6 | 17 | 18 | 19 | 21 | 22 => w * h * 2,
        5 | 8 => w * h,
        24 | 25 => w * h * 8,
        _ => return None,
    })
}

fn to_rgba(f: u32, d: &[u8], w: usize, h: usize) -> Option<Vec<u8>> {
    let n = w * h;
    let mut out = vec![255u8; n * 4];
    let px = |bpp: usize, map: &dyn Fn(&[u8]) -> [u8; 4]| -> Option<Vec<u8>> {
        if d.len() < n * bpp { return None; }
        let mut o = vec![0u8; n * 4];
        for i in 0..n { o[i * 4..i * 4 + 4].copy_from_slice(&map(&d[i * bpp..i * bpp + bpp])); }
        Some(o)
    };
    match f {
        13 | 20 => return dxt(d, w, h, 1),
        14 => return dxt(d, w, h, 3),
        15 => return dxt(d, w, h, 5),
        0 => return px(4, &|p| [p[0], p[1], p[2], p[3]]),
        1 => return px(4, &|p| [p[3], p[2], p[1], p[0]]),
        11 => return px(4, &|p| [p[1], p[2], p[3], p[0]]),
        12 => return px(4, &|p| [p[2], p[1], p[0], p[3]]),
        16 => return px(4, &|p| [p[2], p[1], p[0], 255]),
        2 | 9 => return px(3, &|p| [p[0], p[1], p[2], 255]),
        3 | 10 => return px(3, &|p| [p[2], p[1], p[0], 255]),
        5 => return px(1, &|p| [p[0], p[0], p[0], 255]),
        8 => return px(1, &|p| [255, 255, 255, p[0]]),
        6 => return px(2, &|p| [p[0], p[0], p[0], p[1]]),
        4 | 17 => {
            if d.len() < n * 2 { return None; }
            for i in 0..n {
                let c = rgb565(u16::from_le_bytes([d[i * 2], d[i * 2 + 1]]));
                let c = if f == 17 { [c[2], c[1], c[0]] } else { c };
                out[i * 4..i * 4 + 3].copy_from_slice(&c);
            }
        }
        _ => return None,
    }
    Some(out)
}

/// The picture: (width, height, RGBA), the largest mip whose sides are at most `max_side`
pub fn decode(v: &[u8], max_side: usize) -> Result<(usize, usize, Vec<u8>), String> {
    let u16_at = |at: usize| v.get(at..at + 2).map(|b| u16::from_le_bytes([b[0], b[1]]) as usize).ok_or("vtf: short");
    let u32_at = |at: usize| v.get(at..at + 4).map(|b| u32::from_le_bytes(b.try_into().unwrap())).ok_or("vtf: short");
    if v.get(0..4) != Some(b"VTF\0") { return Err("not a .vtf".into()); }
    let minor = u32_at(8)?;
    let header = u32_at(12)? as usize;
    let (w, h) = (u16_at(16)?, u16_at(18)?);
    let flags = u32_at(20)?;
    let frames = u16_at(24)?.max(1);
    let format = u32_at(52)?;
    let mips = *v.get(56).ok_or("vtf: short")? as usize;
    let low_format = u32_at(57)?;
    let (lw, lh) = (*v.get(61).ok_or("vtf: short")? as usize, *v.get(62).ok_or("vtf: short")? as usize);
    let depth = if minor >= 2 { u16_at(63)?.max(1) } else { 1 };
    let faces = if flags & 0x4000 != 0 { if minor < 5 { 7 } else { 6 } } else { 1 };
    let mut base = None;
    if minor >= 3 {
        let count = u32_at(68)? as usize;
        for r in 0..count.min(32) {
            let o = 80 + r * 8;
            if v.get(o..o + 3) == Some(&[0x30, 0, 0]) { base = Some(u32_at(o + 4)? as usize); }
        }
    }
    let base = match base {
        Some(b) => b,
        None => header + if lw > 0 && lh > 0 && low_format != u32::MAX { size_of(low_format, lw, lh).unwrap_or(0) } else { 0 },
    };
    let mips = mips.max(1);
    let mut k = 0;
    while k + 1 < mips && (w >> k).max(h >> k) > max_side { k += 1; }
    let mut at = base;
    for m in (k + 1..mips).rev() {
        at += size_of(format, (w >> m).max(1), (h >> m).max(1)).ok_or(format!("vtf format {format} isn't read"))? * frames * faces * depth;
    }
    let (mw, mh) = ((w >> k).max(1), (h >> k).max(1));
    let len = size_of(format, mw, mh).ok_or(format!("vtf format {format} isn't read"))?;
    let d = v.get(at..at + len).ok_or("vtf: picture past the end")?;
    let rgba = to_rgba(format, d, mw, mh).ok_or(format!("vtf format {format} isn't read"))?;
    Ok((mw, mh, rgba))
}
