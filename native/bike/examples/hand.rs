use delirium_data::{rpf, xex, kv, archive_key::ArchiveKey, vehicles};
fn main() {
    let a: Vec<String> = std::env::args().collect();
    let dir = std::path::Path::new(&a[1]);
    let prog = xex::program(&dir.join("default.xex")).unwrap();
    assert!(kv::unlock(&prog));
    let c = std::fs::read(dir.join("common.rpf")).unwrap();
    let key = ArchiveKey::find_by_trial(&prog, &c[..0x810]).map(|(k, _)| k).unwrap();
    let toc = rpf::read_toc(&c, &key).unwrap();
    let e = toc.iter().find(|e| e.path.eq_ignore_ascii_case("data/handling.dat")).unwrap();
    let txt = String::from_utf8_lossy(&rpf::read(&c, e).unwrap()).into_owned();
    let h = vehicles::read_handling(&txt);
    let b = vehicles::read_bike(&txt);
    for n in ["PCJ", "NRG900", "SANCHEZ", "FAGGIO"] { println!("{n} {:?}\n  bike {:?}", h.get(n), b.get(n)); }
    println!("G {} drag72 {} kph {} grav34 {} k18 {} kv1065 {} kv116 {} kv33 {} kv112 {} kv64 {}", delirium_data::kv!(34), delirium_data::kv!(72), delirium_data::kv!(547), delirium_data::kv!(34), delirium_data::kv!(18), delirium_data::kv!(1065 f32), delirium_data::kv!(116), delirium_data::kv!(33), delirium_data::kv!(112), delirium_data::kv!(64));
}
