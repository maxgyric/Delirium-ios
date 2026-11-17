// skyfaces <skyname> <out dir> <content folder>: a skybox's six pictures as PNGs
fn main() {
    let a: Vec<String> = std::env::args().collect();
    let c = delirium_bsp::content::Content::new(&[], &[std::path::PathBuf::from(&a[3])], None);
    for s in ["rt", "bk", "lf", "ft", "up", "dn"] {
        let base = c.material(&format!("skybox/{}{s}", a[1])).and_then(|m| m.base).unwrap_or(format!("skybox/{}{s}", a[1]));
        if let Some(v) = c.find(&format!("materials/{base}.vtf")) {
            let (w, h, rgba) = delirium_bsp::vtf::decode(&v, 1024).unwrap();
            println!("{s} {base} {w}x{h}");
            std::fs::write(format!("{}/{s}.png", a[2]), delirium_bsp::content::png(w, h, &rgba)).unwrap();
        }
    }
}
