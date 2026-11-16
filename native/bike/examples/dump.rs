use delirium_data::{img, xex, kv, archive_key::ArchiveKey, resource};
fn main() {
    let a: Vec<String> = std::env::args().collect();
    let dir = std::path::Path::new(&a[1]);
    let prog = xex::program(&dir.join("default.xex")).unwrap();
    assert!(kv::unlock(&prog));
    let p = std::fs::read(dir.join("xbox360.rpf")).unwrap();
    let key = ArchiveKey::find_by_trial(&prog, &p[..0x810]).map(|(k, _)| k);
    let v = std::fs::read(dir.join("xbox360/models/cdimages/vehicles.img")).unwrap();
    let t = img::read_table(&v, key.as_ref()).unwrap();
    let get = |n: &str| { let e = t.iter().find(|e| e.name.eq_ignore_ascii_case(n)).unwrap(); resource::parse(&v[e.offset as usize..e.offset as usize + e.size() as usize]).unwrap() };
    let frag = get(&format!("{}.xft", a[2]));
    let own = get(&format!("{}.xtd", a[2]));
    let share = get("vehshare.xtd");
    let b = delirium_bike::glb::build(&frag, &[&own, &share], [[0.6, 0.05, 0.05], [0.05, 0.05, 0.05]]).unwrap();
    std::fs::write(&a[3], &b.glb).unwrap();
    println!("{} bytes, min {:?} max {:?}", b.glb.len(), b.min, b.max);
}
