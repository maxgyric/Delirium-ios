// convert <map.bsp> <out.glb> [content folder]...: a Source map to a .glb (what the sandbox does on the phone)
fn main() {
    let a: Vec<String> = std::env::args().collect();
    let roots: Vec<std::path::PathBuf> = a[3..].iter().map(std::path::PathBuf::from).collect();
    match delirium_bsp::convert(&a[1], &a[2], &roots, 512) {
        Ok(s) => println!("{s}"),
        Err(e) => { eprintln!("{e}"); std::process::exit(1) }
    }
}
