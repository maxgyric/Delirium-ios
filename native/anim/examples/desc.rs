use std::ffi::CString;
fn main() {
    let a: Vec<String> = std::env::args().collect();
    let dir = CString::new(a[1].clone()).unwrap();
    unsafe { assert_eq!(delirium_anim::da_open(dir.as_ptr()), 0); }
    for c in a[3].split(',') { print!("{}", delirium_anim::describe(&a[2], c)); }
}
