use std::ffi::CString;
use delirium_anim::*;
fn main() {
    let a: Vec<String> = std::env::args().collect();
    let dir = CString::new(a[1].clone()).unwrap();
    let mut buf = vec![0i8; 1 << 20];
    unsafe {
        if da_open(dir.as_ptr()) != 0 { da_error(buf.as_mut_ptr(), buf.len() as i32); println!("err {}", std::ffi::CStr::from_ptr(buf.as_ptr()).to_string_lossy()); return; }
        let f = CString::new(a[2].clone()).unwrap();
        da_dicts(f.as_ptr(), buf.as_mut_ptr(), buf.len() as i32);
        let dicts = std::ffi::CStr::from_ptr(buf.as_ptr()).to_string_lossy().to_string();
        for d in dicts.split(',').filter(|s| !s.is_empty()) {
            let dc = CString::new(d).unwrap();
            da_dict_clips(dc.as_ptr(), buf.as_mut_ptr(), buf.len() as i32);
            let clips = std::ffi::CStr::from_ptr(buf.as_ptr()).to_string_lossy().to_string();
            let mut out = vec![];
            for c in clips.split(',').filter(|s| !s.is_empty()) {
                let cc = CString::new(c).unwrap();
                let id = da_clip(dc.as_ptr(), cc.as_ptr());
                let (mut dur, mut sp) = (0f32, 0f32);
                if id >= 0 { da_clip_info(id, &mut dur, &mut sp); }
                out.push(format!("{c}({dur:.2})"));
            }
            println!("{d}: {}", out.join(" "));
        }
    }
}
