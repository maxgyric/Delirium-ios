// skytest <skyname> <out.png> <content folder>: a skybox's panorama (to check its faces meet)
fn main() {
    let a: Vec<String> = std::env::args().collect();
    let c = delirium_bsp::content::Content::new(&[], &[std::path::PathBuf::from(&a[3])], None);
    let mut e = std::collections::HashMap::new();
    e.insert("classname".to_string(), "worldspawn".to_string());
    e.insert("skyname".to_string(), a[1].clone());
    let (w, h, rgba, _) = delirium_bsp::sky::panorama(&c, &[e], 2048).expect("no sky");
    std::fs::write(&a[2], delirium_bsp::content::png(w, h, &rgba)).unwrap();
}
