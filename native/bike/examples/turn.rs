// turn <game dir> <bike>: the handling at speed: hold an arrow at 30/60/100 km/h; heading change, side slip
use delirium_bike::{game::Game, sim::{Ground, Input}};
fn main() {
    let a: Vec<String> = std::env::args().collect();
    let mut g = Game::open(std::path::Path::new(&a[1])).unwrap();
    let flat = [Ground { hit: true, point: [0.0, 0.0, 0.0], normal: [0.0, 0.0, 1.0] }; 2];
    let dt = 1.0 / 60.0;
    for &(kmh, thr) in &[(30.0f32, 0.6f32), (60.0, 0.6), (100.0, 0.6), (100.0, 1.0), (60.0, 0.0)] {
        let (_, mut b) = g.bike(&a[2], [[0.6, 0.05, 0.05], [0.05; 3]]).unwrap();
        b.pos = [0.0, 0.0, 0.8];
        for _ in 0..120 { b.step(Input::default(), flat, dt); }
        let mut t = 0.0;
        while ((b.vel[0].powi(2) + b.vel[1].powi(2)).sqrt() * 3.6) < kmh && t < 30.0 { b.step(Input { throttle: 1.0, ..Default::default() }, flat, dt); t += dt; }
        let h0 = { let (_, f, _) = b.axes(); f[0].atan2(f[1]) };
        let mut max_slip: f32 = 0.0;
        print!("{kmh:5} km/h thr {thr}: ");
        for k in 0..150 {
            let steer = if k < 90 { 1.0 } else { -1.0 };
            b.step(Input { throttle: thr, steer, ..Default::default() }, flat, dt);
            let (r, f, _) = b.axes();
            let vl = b.vel[0] * f[0] + b.vel[1] * f[1];
            let vs = b.vel[0] * r[0] + b.vel[1] * r[1];
            let slip = vs.atan2(vl.abs().max(0.1)).to_degrees();
            max_slip = max_slip.max(slip.abs());
            if k % 15 == 14 {
                let h = f[0].atan2(f[1]);
                print!("[{:.2}s turn {:5.1} slip {:5.1} lean {:5.1} v {:4.0}{}] ", (k + 1) as f32 * dt, (h - h0).to_degrees(), slip, b.lean.to_degrees(), (vl.powi(2) + vs.powi(2)).sqrt() * 3.6, if b.fallen { " FALLEN" } else { "" });
            }
        }
        println!(" max slip {max_slip:.1}");
    }
}
