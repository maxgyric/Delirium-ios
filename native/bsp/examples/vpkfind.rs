// vpkfind <pattern> <x_dir.vpk>...: which pack and archive each file matching holds
fn main() {
    let a: Vec<String> = std::env::args().collect();
    for p in &a[2..] {
        let v = delirium_bsp::vpk::Vpk::open(std::path::Path::new(p)).unwrap();
        let mut hits: Vec<(String, u16)> = v.files.iter().filter(|(k, _)| k.contains(&a[1])).map(|(k, e)| (k.clone(), e.archive())).collect();
        hits.sort();
        for (k, ar) in hits.iter().take(40) { println!("{p}: {k} -> {ar}"); }
    }
}
