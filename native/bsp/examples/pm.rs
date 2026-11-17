fn main() {
    let a: Vec<String> = std::env::args().collect();
    let g = delirium_bsp::gma::Gma::open(std::path::Path::new(&a[1].split('#').next().unwrap())).unwrap();
    for (p, n) in delirium_bsp::player::models(&g) { println!("model {p} | {n}"); }
    let roots: Vec<std::path::PathBuf> = a.get(3).map(|r| vec![std::path::PathBuf::from(r)]).unwrap_or_default();
    println!("{:?}", delirium_bsp::player::convert(&a[1], &a[2], &roots, 1024));
}
