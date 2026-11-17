fn main() {
    let a: Vec<String> = std::env::args().collect();
    let g = delirium_bsp::gma::Gma::open(std::path::Path::new(&a[1])).unwrap();
    let m = g.maps()[0].clone();
    let d = g.read(&m).unwrap();
    let map = delirium_bsp::bsp::Map::read(&d).unwrap();
    let pak = &d[map.pak.0..map.pak.0 + map.pak.1];
    let idx = delirium_bsp::content::zip_index(pak);
    println!("index {}", idx.len());
    let c = delirium_bsp::content::Content::new(pak, &[], None);
    for n in &a[2..] {
        let v = c.material(n);
        println!("{n}: {:?}", v.as_ref().map(|v| v.base.clone()));
        if let Some(b) = v.and_then(|v| v.base) {
            let f = c.find(&format!("materials/{b}.vtf"));
            println!("  vtf {:?} decode {:?}", f.as_ref().map(|x| x.len()), f.map(|x| delirium_bsp::vtf::decode(&x, 512).map(|r| (r.0, r.1))));
        }
    }
}
