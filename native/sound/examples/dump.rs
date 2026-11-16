// cargo run --release -p delirium-sound --example dump -- <game dir> <rpf> <outdir>: every entry to a file (scratch only)
use delirium_data::{archive_key::ArchiveKey, rpf, xex};
fn main() {
    let a: Vec<String> = std::env::args().collect();
    let p = xex::program(&std::path::Path::new(&a[1]).join("default.xex")).unwrap();
    let a0 = std::fs::read(std::path::Path::new(&a[1]).join("audio.rpf")).unwrap();
    let k = ArchiveKey::find_by_trial(&p, &a0[..0x810]).unwrap().0;
    let d = std::fs::read(&a[2]).unwrap();
    for e in rpf::read_toc(&d, &k).unwrap() {
        let out = std::path::Path::new(&a[3]).join(e.path.replace('/', "_"));
        std::fs::write(out, rpf::read(&d, &e).unwrap()).unwrap();
    }
}
