// vpkls <x_dir.vpk>...: how many materials (.vmt / .vtf) each pack has, and what's in it by top folder
fn main() {
    for p in std::env::args().skip(1) {
        let v = delirium_bsp::vpk::Vpk::open(std::path::Path::new(&p)).unwrap();
        let mut by = std::collections::BTreeMap::new();
        for k in v.files.keys() { *by.entry(k.split('/').next().unwrap_or("").to_string()).or_insert(0) += 1; }
        let vtf = v.files.keys().filter(|k| k.ends_with(".vtf")).count();
        let vmt = v.files.keys().filter(|k| k.ends_with(".vmt")).count();
        println!("{p}: {} files, vtf {vtf}, vmt {vmt}, {:?}", v.files.len(), by);
    }
}
