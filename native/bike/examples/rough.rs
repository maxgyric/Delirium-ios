use delirium_bike::{game::Game, sim::{Bike, Ground, Input}};
use std::time::Instant;
fn ground_h(y: f32) -> f32 { // stairs: 6 steps of 0.2 m every 0.35 m from y = 10, then a 1.2 m platform 4 m long, then a drop
    if y < 10.0 { 0.0 } else if y < 12.1 { (((y - 10.0) / 0.35).floor() + 1.0) * 0.2 } else if y < 16.0 { 1.2 } else { 0.0 }
}
fn main() {
    let a: Vec<String> = std::env::args().collect();
    let dir = std::ffi::CString::new(a[1].clone()).unwrap();
    let t0 = Instant::now();
    unsafe { assert_eq!(delirium_anim::da_open(dir.as_ptr()), 0); }
    println!("clips open {:?}", t0.elapsed());
    let t1 = Instant::now();
    let mut g = Game::open(std::path::Path::new(&a[1])).unwrap();
    println!("bikes open {:?}", t1.elapsed());
    for model in ["pcj", "sanchez"] {
        let t2 = Instant::now();
        let (built, _) = g.bike(model, [[0.6, 0.0, 0.0], [0.0; 3]]).unwrap();
        println!("{model} built {:?} glb {} KB", t2.elapsed(), built.glb.len() / 1024);
    }
    for speed in [3.0f32, 8.0, 17.0] {
        let (_, mut b) = g.bike("pcj", [[0.6, 0.0, 0.0], [0.0; 3]]).unwrap();
        b.pos = [0.0, 0.0, 0.55];
        b.vel = [0.0, speed, 0.0];
        let dt = 1.0 / 60.0;
        let mut worst: f32 = 0.0;
        let mut bad = false;
        for k in 0..600 {
            let mut gr = [Ground::default(); 2];
            for i in 0..2 {
                let w = &b.wheels[i];
                let p = [b.pos[0] + b.q.rot(w.at)[0], b.pos[1] + b.q.rot(w.at)[1]];
                gr[i] = Ground { hit: true, point: [p[0], p[1], ground_h(p[1])], normal: [0.0, 0.0, 1.0] };
            }
            let inp = Input { throttle: if k < 240 { 0.5 } else { 0.0 }, brake: if k >= 240 { 0.6 } else { 0.0 }, ..Default::default() };
            b.step(inp, gr, dt);
            if !b.pos.iter().all(|v| v.is_finite()) { bad = true; }
            worst = worst.max(b.lean.abs().to_degrees());
            if k % 60 == 59 { let _: &Bike = &b; println!("  v0 {speed:4.1} t {:4.1} pos y {:6.2} z {:5.2} pitch {:6.1} lean {:5.1} x [{:.2} {:.2}] fallen {}", (k + 1) as f32 * dt, b.pos[1], b.pos[2], b.pitch.to_degrees(), b.lean.to_degrees(), b.wheels[0].x, b.wheels[1].x, b.fallen); }
        }
        println!("v0 {speed}: NaN {bad} worst lean {worst:.1}");
    }
}
