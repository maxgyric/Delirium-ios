// cargo run --release -p delirium-sound --example list -- <game dir> <rpf>...
use delirium_data::{archive_key::ArchiveKey, rpf, xex};
fn main() {
    let a: Vec<String> = std::env::args().collect();
    let dir = std::path::Path::new(&a[1]);
    let p = xex::program(&dir.join("default.xex")).unwrap();
    let mut key: Option<ArchiveKey> = None;
    for f in &a[2..] {
        let d = std::fs::read(f).unwrap();
        if key.is_none() { key = ArchiveKey::find_by_trial(&p, &d[..0x810]).map(|x| x.0); }
        let k = key.clone().expect("key");
        let toc = rpf::read_toc(&d, &k).unwrap();
        println!("== {f}: {} entries", toc.len());
        for e in &toc {
            let head = rpf::read(&d, e).map(|b| b.iter().take(16).map(|x| format!("{x:02x}")).collect::<String>()).unwrap_or_default();
            println!("{:40} off {:9} size {:9} comp {} res {:?} {}", e.path, e.offset, e.size, e.compressed, e.resource, head);
        }
    }
}
