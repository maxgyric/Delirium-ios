//! GTA IV's motorbikes, read from your own game: the model (glb), handling.dat's numbers, and the ride.
//! C API for Unity ([DllImport]): db_open, db_error, db_list, db_spawn, db_glb, db_info, db_step, db_place, db_drop.
//! Everything in GTA's frame (x right, y forward, z up); Unity swaps y and z.
pub mod game;
pub mod glb;
pub mod sim;
pub mod tex;

use std::ffi::{c_char, CStr};
use std::sync::Mutex;

struct State {
    game: game::Game,
    bikes: Vec<Option<(sim::Bike, Vec<u8>)>>,
}

static STATE: Mutex<Option<State>> = Mutex::new(None);
static ERR: Mutex<String> = Mutex::new(String::new());

fn fail(e: impl Into<String>) -> i32 {
    *ERR.lock().unwrap_or_else(|e| e.into_inner()) = e.into();
    -1
}

unsafe fn text<'a>(p: *const c_char) -> Option<&'a str> {
    if p.is_null() { None } else { unsafe { CStr::from_ptr(p) }.to_str().ok() }
}

unsafe fn put(s: &str, buf: *mut c_char, len: i32) -> i32 {
    if !buf.is_null() && len > 0 {
        let n = s.len().min(len as usize - 1);
        unsafe {
            std::ptr::copy_nonoverlapping(s.as_ptr(), buf as *mut u8, n);
            *buf.add(n) = 0;
        }
    }
    s.len() as i32
}

fn with<R>(f: impl FnOnce(&mut State) -> Result<R, String>) -> Result<R, String> {
    let mut g = STATE.lock().unwrap_or_else(|e| e.into_inner());
    let s = g.as_mut().ok_or("call db_open first")?;
    f(s)
}

/// Your GTA IV's folder (default.xex, xbox360.rpf, common.rpf, xbox360/models/cdimages/vehicles.img). 0 or -1.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn db_open(dir: *const c_char) -> i32 {
    let Some(d) = (unsafe { text(dir) }) else { return fail("db_open: no folder") };
    match game::Game::open(std::path::Path::new(d)) {
        Ok(g) => {
            *STATE.lock().unwrap_or_else(|e| e.into_inner()) = Some(State { game: g, bikes: vec![] });
            0
        }
        Err(e) => fail(e),
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn db_error(buf: *mut c_char, len: i32) -> i32 {
    let e = ERR.lock().unwrap_or_else(|e| e.into_inner()).clone();
    unsafe { put(&e, buf, len) }
}

/// The bikes your game has, "model:anim dictionary" comma separated; how many.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn db_list(buf: *mut c_char, len: i32) -> i32 {
    match with(|s| Ok(s.game.bike_list())) {
        Ok(l) => {
            let v: Vec<String> = l.iter().map(|m| format!("{m}:{}", game::BIKES.iter().find(|b| b.0 == *m).map(|b| b.2).unwrap_or(""))).collect();
            unsafe { put(&v.join(","), buf, len) };
            v.len() as i32
        }
        Err(e) => fail(e),
    }
}

/// A bike (`model`, its two paint colours as 6 floats): a handle, its .glb's size (db_glb copies it out).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn db_spawn(model: *const c_char, paint: *const f32, glb_len: *mut i32) -> i32 {
    let Some(m) = (unsafe { text(model) }) else { return fail("db_spawn: no model") };
    let p = if paint.is_null() { [0.6, 0.05, 0.05, 0.05, 0.05, 0.05] } else { let s = unsafe { std::slice::from_raw_parts(paint, 6) }; [s[0], s[1], s[2], s[3], s[4], s[5]] };
    match with(|s| {
        let (built, bike) = s.game.bike(m, [[p[0], p[1], p[2]], [p[3], p[4], p[5]]])?;
        let n = built.glb.len();
        s.bikes.push(Some((bike, built.glb)));
        Ok((s.bikes.len() - 1, n))
    }) {
        Ok((h, n)) => {
            if !glb_len.is_null() { unsafe { *glb_len = n as i32 }; }
            h as i32
        }
        Err(e) => fail(format!("db_spawn: {e}")),
    }
}

/// The bike's .glb into `out` (`len` bytes); then it's let go.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn db_glb(h: i32, out: *mut u8, len: i32) -> i32 {
    match with(|s| {
        let b = s.bikes.get_mut(h as usize).and_then(|b| b.as_mut()).ok_or("no such bike")?;
        let n = b.1.len().min(len.max(0) as usize);
        unsafe { std::ptr::copy_nonoverlapping(b.1.as_ptr(), out, n) };
        b.1 = vec![];
        Ok(n as i32)
    }) {
        Ok(n) => n,
        Err(e) => fail(e),
    }
}

/// 24 floats: front wheel at (3), radius, rear wheel at (3), radius, travel, rest, upper, mass, max lean,
/// full-anim lean, top speed (m/s), com (3), steering lock, wheelie angle, stoppie angle, gears, lean fwd/back com.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn db_info(h: i32, out: *mut f32) -> i32 {
    match with(|s| {
        let (b, _) = s.bikes.get(h as usize).and_then(|b| b.as_ref()).ok_or("no such bike")?;
        let (f, r) = (&b.wheels[0], &b.wheels[1]);
        let v = [f.at[0], f.at[1], f.at[2], f.radius, r.at[0], r.at[1], r.at[2], r.radius, f.spring.travel, f.spring.rest,
                 b.h.suspension_upper, b.mass, b.b.max_lean, b.b.full_anim_lean, b.h.drive_max_velocity, b.com[0], b.com[1], b.com[2],
                 b.h.steering_lock, b.b.wheelie_ang, b.b.stoppie_ang, b.h.gears as f32, b.b.lean_fwd_com, b.b.lean_back_com];
        unsafe { std::ptr::copy_nonoverlapping(v.as_ptr(), out, v.len()) };
        Ok(())
    }) {
        Ok(()) => 0,
        Err(e) => fail(e),
    }
}

/// Puts it at `pos` (3) turned `rot` (x y z w), moving `vel` (3; null: still).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn db_place(h: i32, pos: *const f32, rot: *const f32, vel: *const f32) -> i32 {
    match with(|s| {
        let (b, _) = s.bikes.get_mut(h as usize).and_then(|b| b.as_mut()).ok_or("no such bike")?;
        let p = unsafe { std::slice::from_raw_parts(pos, 3) };
        let q = unsafe { std::slice::from_raw_parts(rot, 4) };
        b.pos = [p[0], p[1], p[2]];
        b.q = sim::Q([q[0], q[1], q[2], q[3]]);
        b.vel = if vel.is_null() { [0.0; 3] } else { let v = unsafe { std::slice::from_raw_parts(vel, 3) }; [v[0], v[1], v[2]] };
        b.ang = [0.0; 3];
        b.fallen = false;
        b.gear = Default::default();
        for w in b.wheels.iter_mut() { w.x = w.spring.rest; w.slip = 0.0; }
        Ok(())
    }) {
        Ok(()) => 0,
        Err(e) => fail(e),
    }
}

/// One step: `input` 5 floats (throttle 0..1, brake 0..1, steer -1..1 (+ right), lean -1 back..1 forward,
/// handbrake 0/1); `ground` 14 floats (a wheel each: hit 0/1, point (3), normal (3)); `out` 32 floats:
/// pos (3), rot (4), vel (3), ang (3), steer angle, lean, pitch, speed along, gear, revs, front x, rear x,
/// front spin, rear spin, front on, rear on, fallen, drive, front contact (3) .. (see Bike.cs)
#[unsafe(no_mangle)]
pub unsafe extern "C" fn db_step(h: i32, dt: f32, input: *const f32, ground: *const f32, out: *mut f32) -> i32 {
    match with(|s| {
        let (b, _) = s.bikes.get_mut(h as usize).and_then(|b| b.as_mut()).ok_or("no such bike")?;
        let i = unsafe { std::slice::from_raw_parts(input, 5) };
        let g = unsafe { std::slice::from_raw_parts(ground, 14) };
        let inp = sim::Input { throttle: i[0], brake: i[1], steer: i[2], lean: i[3], handbrake: i[4] > 0.5 };
        let gr = |k: usize| sim::Ground { hit: g[k] > 0.5, point: [g[k + 1], g[k + 2], g[k + 3]], normal: [g[k + 4], g[k + 5], g[k + 6]] };
        b.step(inp, [gr(0), gr(7)], dt.clamp(0.0, 0.1));
        let (_, fwd, _) = b.axes();
        let along = b.vel[0] * fwd[0] + b.vel[1] * fwd[1] + b.vel[2] * fwd[2];
        let v = [b.pos[0], b.pos[1], b.pos[2], b.q.0[0], b.q.0[1], b.q.0[2], b.q.0[3], b.vel[0], b.vel[1], b.vel[2],
                 b.ang[0], b.ang[1], b.ang[2], b.steer, b.lean, b.pitch, along, b.gear.gear as f32, b.gear.revs,
                 b.wheels[0].x, b.wheels[1].x, b.wheels[0].spin, b.wheels[1].spin,
                 b.wheels[0].grounded as i32 as f32, b.wheels[1].grounded as i32 as f32, b.fallen as i32 as f32, b.drive,
                 b.shift, 0.0, 0.0, 0.0, 0.0];
        unsafe { std::ptr::copy_nonoverlapping(v.as_ptr(), out, v.len()) };
        Ok(())
    }) {
        Ok(()) => 0,
        Err(e) => fail(e),
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn db_drop(h: i32) -> i32 {
    match with(|s| {
        if let Some(b) = s.bikes.get_mut(h as usize) { *b = None; }
        Ok(())
    }) {
        Ok(()) => 0,
        Err(e) => fail(e),
    }
}
