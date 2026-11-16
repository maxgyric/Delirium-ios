//! For C# (Sounds.cs): open the game's sounds once, ask for sounds by name (or by a game object's field), then
//! each wave's samples. All on one lock; the decoding is done off Unity's thread by the caller.
use crate::Sounds;
use std::ffi::{c_char, CStr};
use std::sync::Mutex;

static STATE: Mutex<Option<Sounds>> = Mutex::new(None);
static ERROR: Mutex<String> = Mutex::new(String::new());

fn fail(e: impl Into<String>) -> i32 {
    *ERROR.lock().unwrap() = e.into();
    -1
}

fn text(p: *const c_char) -> Option<String> {
    if p.is_null() { return None; }
    unsafe { CStr::from_ptr(p) }.to_str().ok().map(str::to_owned)
}

/// The game's folder (where default.xex, audio.rpf and xbox360/audio/sfx/ are). 0, or -1 (ds_error).
#[unsafe(no_mangle)]
pub extern "C" fn ds_open(dir: *const c_char) -> i32 {
    let Some(dir) = text(dir) else { return fail("no folder") };
    match Sounds::open(std::path::Path::new(&dir)) {
        Ok(s) => {
            *STATE.lock().unwrap() = Some(s);
            0
        }
        Err(e) => fail(e),
    }
}

/// The last error's text into `out` (`cap` bytes, 0-terminated); its length.
#[unsafe(no_mangle)]
pub extern "C" fn ds_error(out: *mut u8, cap: i32) -> i32 {
    let e = ERROR.lock().unwrap();
    let b = e.as_bytes();
    let n = b.len().min((cap.max(1) - 1) as usize);
    if !out.is_null() {
        unsafe {
            std::ptr::copy_nonoverlapping(b.as_ptr(), out, n);
            *out.add(n) = 0;
        }
    }
    n as i32
}

fn put(node: Option<crate::Node>, out: *mut i32, cap: i32) -> i32 {
    let Some(n) = node else { return 0 };
    let mut v = vec![];
    n.flat(&mut v);
    if v.len() > cap as usize { return fail("too big"); }
    unsafe { std::ptr::copy_nonoverlapping(v.as_ptr(), out, v.len()) };
    v.len() as i32
}

/// A sound by name, flat (Node::flat) into `out`: its length, 0 when it isn't there, -1 on an error.
#[unsafe(no_mangle)]
pub extern "C" fn ds_sound(name: *const c_char, out: *mut i32, cap: i32) -> i32 {
    let Some(name) = text(name) else { return fail("no name") };
    let mut g = STATE.lock().unwrap();
    let Some(s) = g.as_mut() else { return fail("not open") };
    put(s.sound(&name), out, cap)
}

/// The sound a game.dat16 object names at byte `at` of it (a bike's engine, a gun's shot), as ds_sound.
#[unsafe(no_mangle)]
pub extern "C" fn ds_game_sound(object: *const c_char, at: i32, out: *mut i32, cap: i32) -> i32 {
    let Some(object) = text(object) else { return fail("no name") };
    let mut g = STATE.lock().unwrap();
    let Some(s) = g.as_mut() else { return fail("not open") };
    put(s.game_sound(&object, at.max(0) as usize), out, cap)
}

/// Wave `n` (a number from a sound): `info` = [rate, samples, loop start (-1: once)]. 0 or -1.
#[unsafe(no_mangle)]
pub extern "C" fn ds_wave(n: i32, info: *mut i32) -> i32 {
    let g = STATE.lock().unwrap();
    let Some(s) = g.as_ref() else { return fail("not open") };
    let Some(w) = s.used.get(n.max(0) as usize) else { return fail("no such wave") };
    let v = [w.rate as i32, w.samples as i32, w.loop_start];
    unsafe { std::ptr::copy_nonoverlapping(v.as_ptr(), info, 3) };
    0
}

/// Wave `n`'s samples (16-bit mono) into `out` (`cap` of them): how many, or -1.
#[unsafe(no_mangle)]
pub extern "C" fn ds_pcm(n: i32, out: *mut i16, cap: i32) -> i32 {
    let g = STATE.lock().unwrap();
    let Some(s) = g.as_ref() else { return fail("not open") };
    match s.pcm(n.max(0) as usize) {
        Ok(p) => {
            let k = p.len().min(cap.max(0) as usize);
            unsafe { std::ptr::copy_nonoverlapping(p.as_ptr(), out, k) };
            k as i32
        }
        Err(e) => fail(e),
    }
}

/// Lets the banks go (once every wave is out).
#[unsafe(no_mangle)]
pub extern "C" fn ds_close() {
    *STATE.lock().unwrap() = None;
}
