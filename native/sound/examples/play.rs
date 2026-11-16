// play <game dir> <out dir> <sound or OBJECT@offset>...: each sound's tree, its waves to .wav (scratch only).
// The decoder here: the ffmpeg command line (the phone links FFmpeg's XMA2 decoder in; ci/xma.c).
use delirium_sound::{Node, Sounds};
use std::io::Write;
fn show(n: &Node, ind: usize) {
    match n {
        Node::Wave { wave, vol, pitch } => println!("{:ind$}wave {wave} vol {vol} pitch {pitch}", ""),
        Node::All { kids, vol, pitch } | Node::Any { kids, vol, pitch } => {
            println!("{:ind$}{} vol {vol} pitch {pitch}", "", if matches!(n, Node::All { .. }) { "ALL" } else { "ANY" });
            for k in kids { show(k, ind + 2); }
        }
    }
}
fn main() {
    let a: Vec<String> = std::env::args().collect();
    let mut s = Sounds::open(std::path::Path::new(&a[1])).unwrap();
    for name in &a[3..] {
        let n = if let Some((o, at)) = name.split_once('@') { s.game_sound(o, at.parse().unwrap()) } else { s.sound(name) };
        println!("== {name}");
        match n { Some(n) => show(&n, 2), None => println!("  (none)") }
    }
    for i in 0..s.used.len() {
        let w = s.used[i];
        let pcm = s.pcm(i).unwrap();
        println!("wave {i}: {:?} -> {} samples", w, pcm.len());
        let mut f = std::fs::File::create(format!("{}/w{i}.wav", a[2])).unwrap();
        let data: Vec<u8> = pcm.iter().flat_map(|v| v.to_le_bytes()).collect();
        let mut h = vec![];
        h.extend(b"RIFF"); h.extend((36 + data.len() as u32).to_le_bytes()); h.extend(b"WAVEfmt ");
        h.extend(16u32.to_le_bytes()); h.extend(1u16.to_le_bytes()); h.extend(1u16.to_le_bytes());
        h.extend(w.rate.to_le_bytes()); h.extend((w.rate * 2).to_le_bytes()); h.extend(2u16.to_le_bytes()); h.extend(16u16.to_le_bytes());
        h.extend(b"data"); h.extend((data.len() as u32).to_le_bytes());
        f.write_all(&h).unwrap(); f.write_all(&data).unwrap();
    }
}

// Here (not on the phone): the XMA through the ffmpeg command, as an XMA2 .wav (the same 34 bytes of XMA2WAVEFORMATEX
// ci/xma.c gives the decoder)
#[unsafe(no_mangle)]
pub unsafe extern "C" fn delirium_xma_decode(data: *const u8, size: i32, channels: i32, rate: i32, out: *mut i16, max: i32) -> i32 {
    let d = unsafe { std::slice::from_raw_parts(data, size as usize) };
    let mut ext = vec![];
    ext.extend(1u16.to_le_bytes()); ext.extend(0u32.to_le_bytes()); ext.extend(0u32.to_le_bytes()); ext.extend((size as u32).to_le_bytes());
    ext.extend([0u8; 16]); ext.push(0); ext.push(4); ext.extend(((size / 2048).max(1) as u16).to_le_bytes());
    let mut fmt = vec![];
    fmt.extend(0x166u16.to_le_bytes()); fmt.extend((channels as u16).to_le_bytes()); fmt.extend((rate as u32).to_le_bytes());
    fmt.extend((rate as u32 * 2).to_le_bytes()); fmt.extend(2048u16.to_le_bytes()); fmt.extend(16u16.to_le_bytes()); fmt.extend((ext.len() as u16).to_le_bytes());
    fmt.extend(&ext);
    let mut riff = b"WAVEfmt ".to_vec(); riff.extend((fmt.len() as u32).to_le_bytes()); riff.extend(&fmt);
    riff.extend(b"data"); riff.extend((size as u32).to_le_bytes()); riff.extend(d);
    let mut f = b"RIFF".to_vec(); f.extend((riff.len() as u32).to_le_bytes()); f.extend(riff);
    let tmp = std::env::temp_dir().join(format!("xma{}.wav", std::process::id()));
    std::fs::write(&tmp, f).unwrap();
    let o = std::process::Command::new("ffmpeg").args(["-v", "error", "-i"]).arg(&tmp).args(["-f", "s16le", "-ac", "1", "-"]).output().unwrap();
    let n = (o.stdout.len() / 2).min(max as usize);
    for i in 0..n { unsafe { *out.add(i) = i16::from_le_bytes([o.stdout[2 * i], o.stdout[2 * i + 1]]) }; }
    n as i32
}
