fn main() {
    let a: Vec<String> = std::env::args().collect();
    let g = delirium_bsp::gma::Gma::open(std::path::Path::new(&a[1])).unwrap();
    println!("{}", g.name);
    for p in &g.order { println!("{} {}", g.files[p].1, p); }
}
