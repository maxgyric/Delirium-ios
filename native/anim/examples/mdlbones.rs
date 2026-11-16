// mdlbones <file.mdl>: a Source model's bones (and its bind pose's lowest foot)
use delirium_anim::mdl::Mdl;
fn main() {
    let a: Vec<String> = std::env::args().collect();
    let m = Mdl::read(std::fs::read(&a[1]).unwrap()).unwrap();
    let mut w: Vec<(bevy_math::Vec3, bevy_math::Quat)> = vec![];
    for b in &m.bones {
        w.push(match b.parent { Some(p) => (w[p].0 + w[p].1 * b.pos, (w[p].1 * b.quat).normalize()), None => (b.pos, b.quat) });
    }
    for (i, b) in m.bones.iter().enumerate() { println!("{i} {} {:?} z {:.2}", b.name, b.parent, w[i].0.z); }
    println!("{} anims, {} seqs", m.anims.len(), m.seqs.len());
}
