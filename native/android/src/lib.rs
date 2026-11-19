//! libdelirium.so: the Quest (Android) build's native code in one shared library: Delirium's C API (nm_*), the
//! animation reader (da_*), the bike, the sounds and the map converter, as libdelirium_ios.a has them for the
//! iPhone. Unity calls it through [DllImport("delirium")].
pub use delirium_anim;
pub use delirium_bike;
pub use delirium_bsp;
pub use delirium_physics;
pub use delirium_sound;

use std::ffi::{CStr, c_char};

/// Keeps every crate's exported functions in the library.
#[unsafe(no_mangle)]
pub extern "C" fn delirium_android_version() -> i32 {
    1
}

/// Where Delirium keeps its own files (the key that opens default.xex): Android gives an app no HOME, so the game
/// sets it to its own folder before Delirium starts.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn delirium_set_home(path: *const c_char) -> i32 {
    if path.is_null() {
        return -1;
    }
    let Ok(p) = (unsafe { CStr::from_ptr(path) }).to_str() else { return -1 };
    // (once, before any other thread reads the environment)
    unsafe { std::env::set_var("HOME", p) };
    0
}

/// GTA IV's XMA sounds without FFmpeg's decoder (build.rs: DELIRIUM_XMA_DIR not set): none decoded. With it,
/// ci/xma.c's delirium_xma_decode is linked in instead.
#[cfg(not(xma_ffmpeg))]
#[unsafe(no_mangle)]
pub extern "C" fn delirium_xma_decode(_data: *const u8, _size: i32, _channels: i32, _rate: i32, _out: *mut i16, _max: i32) -> i32 {
    -1
}
