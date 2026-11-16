use delirium_bike::{game::Game, sim::{Ground, Input}};
fn main() {
    let a: Vec<String> = std::env::args().collect();
    let g = Game::open(std::path::Path::new(&a[1])).unwrap();
    println!("bikes {:?}", g.bike_list());
    let (built, mut b) = g.bike(&a[2], [[0.6, 0.05, 0.05], [0.05; 3]]).unwrap();
    println!("wheels {:?} r {} / {:?} r {}, travel {} rest {}", b.wheels[0].at, b.wheels[0].radius, b.wheels[1].at, b.wheels[1].radius, b.wheels[0].spring.travel, b.wheels[0].spring.rest);
    let _ = built;
    b.pos = [0.0, 0.0, 0.8];
    let flat = |b: &delirium_bike::sim::Bike| { let mut gr = [Ground::default(); 2]; for i in 0..2 { gr[i] = Ground { hit: true, point: [0.0, 0.0, 0.0], normal: [0.0, 0.0, 1.0] }; let _ = b; } gr };
    let dt = 1.0 / 60.0;
    let mut t = 0.0;
    let mut log = |b: &delirium_bike::sim::Bike, t: f32, what: &str| {
        let v = (b.vel[0].powi(2) + b.vel[1].powi(2)).sqrt();
        let (_, f, _) = b.axes();
        println!("{what:8} t {t:5.2} pos [{:6.2} {:6.2} {:5.2}] v {:5.1} km/h  gear {} lean {:5.1} pitch {:5.1} heading {:6.1} x [{:.3} {:.3}] {}", b.pos[0], b.pos[1], b.pos[2], v * 3.6, b.gear.gear, b.lean.to_degrees(), b.pitch.to_degrees(), f[0].atan2(f[1]).to_degrees(), b.wheels[0].x, b.wheels[1].x, if b.fallen {"FALLEN"} else {""});
    };
    let phases: Vec<(&str, f32, Input)> = vec![
        ("settle", 2.0, Input::default()),
        ("full gas", 12.0, Input { throttle: 1.0, ..Default::default() }),
        ("steer R", 4.0, Input { throttle: 0.6, steer: 0.6, ..Default::default() }),
        ("straight", 2.0, Input { throttle: 0.6, ..Default::default() }),
        ("steer L", 3.0, Input { throttle: 0.6, steer: -1.0, ..Default::default() }),
        ("brake", 5.0, Input { brake: 1.0, ..Default::default() }),
        ("wheelie", 3.0, Input { throttle: 1.0, lean: -1.0, ..Default::default() }),
        ("stop", 6.0, Input { brake: 1.0, ..Default::default() }),
        ("reverse", 3.0, Input { brake: 1.0, ..Default::default() }),
    ];
    for (name, dur, inp) in phases {
        let mut k = 0;
        let steps = (dur / dt) as usize;
        for _ in 0..steps {
            let gr = flat(&b);
            b.step(inp, gr, dt);
            t += dt;
            k += 1;
            if k % 30 == 0 || k == steps { log(&b, t, name); }
        }
    }
}
