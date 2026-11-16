// skid <game dir> <bike>: when does it slide? full gas from rest while steering, braking in a turn, flicking L/R
use delirium_bike::{game::Game, sim::{Ground, Input}};
fn main() {
    let a: Vec<String> = std::env::args().collect();
    let mut g = Game::open(std::path::Path::new(&a[1])).unwrap();
    let flat = [Ground { hit: true, point: [0.0, 0.0, 0.0], normal: [0.0, 0.0, 1.0] }; 2];
    let dt = 1.0 / 60.0;
    let tests: Vec<(&str, f32, Vec<(f32, Input)>)> = vec![
        ("gas+steer from rest", 0.0, vec![(3.0, Input { throttle: 1.0, steer: 1.0, ..Default::default() })]),
        ("brake in turn 60", 60.0, vec![(1.0, Input { throttle: 0.5, steer: 1.0, ..Default::default() }), (2.0, Input { brake: 1.0, steer: 1.0, ..Default::default() })]),
        ("flick L/R 60", 60.0, vec![(0.6, Input { throttle: 0.7, steer: -1.0, ..Default::default() }), (0.6, Input { throttle: 0.7, steer: 1.0, ..Default::default() }), (0.6, Input { throttle: 0.7, steer: -1.0, ..Default::default() }), (0.6, Input { throttle: 0.7, steer: 1.0, ..Default::default() })]),
        ("gas+steer 20", 20.0, vec![(3.0, Input { throttle: 1.0, steer: 1.0, ..Default::default() })]),
        ("handbrake turn 50", 50.0, vec![(1.5, Input { throttle: 0.5, steer: 1.0, handbrake: true, ..Default::default() })]),
    ];
    for (name, kmh, phases) in tests {
        let (_, mut b) = g.bike(&a[2], [[0.6, 0.05, 0.05], [0.05; 3]]).unwrap();
        b.pos = [0.0, 0.0, 0.8];
        for _ in 0..120 { b.step(Input::default(), flat, dt); }
        let mut t = 0.0;
        while ((b.vel[0].powi(2) + b.vel[1].powi(2)).sqrt() * 3.6) < kmh && t < 30.0 { b.step(Input { throttle: 1.0, ..Default::default() }, flat, dt); t += dt; }
        print!("{name:22}: ");
        let mut max_slip: f32 = 0.0;
        let mut k = 0;
        for (dur, inp) in phases {
            for _ in 0..(dur / dt) as usize {
                b.step(inp, flat, dt);
                let (r, f, _) = b.axes();
                let vl = b.vel[0] * f[0] + b.vel[1] * f[1];
                let vs = b.vel[0] * r[0] + b.vel[1] * r[1];
                let v = (vl * vl + vs * vs).sqrt();
                let slip = if v > 1.0 { vs.atan2(vl.abs()).to_degrees() } else { 0.0 };
                max_slip = max_slip.max(slip.abs());
                k += 1;
                if k % 20 == 0 { print!("[slip {:5.1} yaw/s {:6.1} v {:4.0} rs {:.2}{}] ", slip, (b.ang[2]).to_degrees(), v * 3.6, b.wheels[1].slip, if b.fallen { " F" } else { "" }); }
            }
        }
        println!(" MAX {max_slip:.1}");
    }
}
