// convert <map.bsp> <out.glb>: a Source map to a .glb (what the sandbox does on the phone)
fn main() {
    let a: Vec<String> = std::env::args().collect();
    match delirium_bsp::convert(&a[1], &a[2]) {
        Ok(s) => println!("{s}"),
        Err(e) => { eprintln!("{e}"); std::process::exit(1) }
    }
}
