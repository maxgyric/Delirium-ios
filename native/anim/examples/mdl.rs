// mdl <file.mdl> <sequence> [frames]: a Source model's skeleton, the sequence's frames as world joints (for a plot)
use delirium_anim::mdl::Mdl;
fn main() {
    let a: Vec<String> = std::env::args().collect();
    let m = Mdl::read(std::fs::read(&a[1]).unwrap()).unwrap();
    let ai = m.seqs[&a[2].to_ascii_lowercase()];
    let ad = &m.anims[ai];
    eprintln!("{} fps {} frames {} flags {:x}", ad.name, ad.fps, ad.frames, ad.flags);
    let n: usize = a.get(3).map(|s| s.parse().unwrap()).unwrap_or(4);
    for k in 0..n {
        let f = k * (ad.frames - 1) / (n - 1).max(1);
        let local = m.frame(ai, f).unwrap();
        let mut w: Vec<(bevy_math::Vec3, bevy_math::Quat)> = vec![];
        for (i, b) in m.bones.iter().enumerate() {
            let (p, q) = local[i];
            w.push(match b.parent { Some(pa) => (w[pa].0 + w[pa].1 * p, (w[pa].1 * q).normalize()), None => (p, q) });
        }
        let joints: Vec<String> = m.bones.iter().enumerate().map(|(i, b)| format!("{}:{}:{:.2},{:.2},{:.2}", b.name, b.parent.map_or(-1, |p| p as i32), w[i].0.x, w[i].0.y, w[i].0.z)).collect();
        println!("F {f} {}", joints.join(" "));
    }
}
