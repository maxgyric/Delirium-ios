//! A GTA IV motorbike's ride, in GTA's frame (x right, y forward, z up; metres, seconds).
//! GTA IV's own parts, from delirium-data's port of the game's code: the gearbox (`Transmission::process`), the
//! springs (`suspension::Spring` / `force`) and the tyre (`tyre::tyre_force`: traction curve, slip, lateral grip),
//! with handling.dat's numbers. CBike's balance isn't in the port: a lean / pitch controller after handling.dat's
//! bike line (lean angles, lean forces, wheelie / stoppie angles, in-air steer) keeps it up as the game does.
//! The ground comes from the host each step: one plane per wheel under it (Unity's raycasts).
use delirium_data::{transmission::{Controls, Transmission}, tyre::{self, Tyre}, suspension::{self, Spring}, vehicles::Handling,
                    wheelstep::TyreIn};

pub type V3 = [f32; 3];
fn add(a: V3, b: V3) -> V3 { [a[0] + b[0], a[1] + b[1], a[2] + b[2]] }
fn sub(a: V3, b: V3) -> V3 { [a[0] - b[0], a[1] - b[1], a[2] - b[2]] }
fn mulv(a: V3, k: f32) -> V3 { [a[0] * k, a[1] * k, a[2] * k] }
fn dot(a: V3, b: V3) -> f32 { a[0] * b[0] + a[1] * b[1] + a[2] * b[2] }
fn cross(a: V3, b: V3) -> V3 { [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]] }
fn len(a: V3) -> f32 { dot(a, a).sqrt() }
fn unit(a: V3) -> V3 { let l = len(a); if l > 1e-9 { mulv(a, 1.0 / l) } else { a } }

/// A rotation (x, y, z, w).
#[derive(Clone, Copy, Debug)]
pub struct Q(pub [f32; 4]);
impl Q {
    pub fn rot(&self, v: V3) -> V3 {
        let [x, y, z, w] = self.0;
        let u = [x, y, z];
        let t = mulv(cross(u, v), 2.0);
        add(add(v, mulv(t, w)), cross(u, t))
    }
    pub fn inv_rot(&self, v: V3) -> V3 { Q([-self.0[0], -self.0[1], -self.0[2], self.0[3]]).rot(v) }
    fn integrate(&mut self, w: V3, dt: f32) {
        let [x, y, z, qw] = self.0;
        let (a, b, c) = (w[0] * 0.5 * dt, w[1] * 0.5 * dt, w[2] * 0.5 * dt);
        let n = [x + a * qw + b * z - c * y, y + b * qw + c * x - a * z, z + c * qw + a * y - b * x, qw - a * x - b * y - c * z];
        let l = (n[0] * n[0] + n[1] * n[1] + n[2] * n[2] + n[3] * n[3]).sqrt();
        self.0 = [n[0] / l, n[1] / l, n[2] / l, n[3] / l];
    }
}

/// handling.dat's bike line ('!'), angles in radians.
#[derive(Clone, Copy, Debug, Default)]
pub struct BikeLine {
    pub lean_fwd_com: f32, pub lean_fwd_force: f32, pub lean_back_com: f32, pub lean_back_force: f32,
    pub max_lean: f32, pub full_anim_lean: f32, pub des_lean: f32, pub stick_lean: f32, pub brake_stabil: f32,
    pub in_air_steer: f32, pub wheelie_ang: f32, pub stoppie_ang: f32, pub wheelie_steer: f32,
    pub wheelie_stab: f32, pub stoppie_stab: f32,
}
impl BikeLine {
    pub fn from(b: &[f32; 15]) -> Self {
        BikeLine { lean_fwd_com: b[0], lean_fwd_force: b[1], lean_back_com: b[2], lean_back_force: b[3], max_lean: b[4],
                   full_anim_lean: b[5], des_lean: b[6], stick_lean: b[7], brake_stabil: b[8], in_air_steer: b[9],
                   wheelie_ang: b[10], stoppie_ang: b[11], wheelie_steer: b[12], wheelie_stab: b[13], stoppie_stab: b[14] }
    }
}

#[derive(Clone, Copy, Debug, Default)]
pub struct Ground {
    pub hit: bool,
    pub point: V3,
    pub normal: V3,
}

#[derive(Clone, Copy, Debug, Default)]
pub struct Input {
    pub throttle: f32, // 0..1
    pub brake: f32,    // 0..1 (held while stopped: reverse)
    pub steer: f32,    // -1 left .. 1 right
    pub lean: f32,     // -1 back (wheelie) .. 1 forward (stoppie / tuck)
    pub handbrake: bool,
}

pub struct Wheel {
    pub at: V3,          // the wheel's centre at rest, in the bike's frame
    pub radius: f32,
    pub front: bool,
    pub spring: Spring,
    pub tyre: Tyre,
    pub x: f32,          // spring compression (0: hanging; spring.rest: at rest)
    pub load: f32,
    pub slip: f32,
    pub spin: f32,       // rolled angle (radians)
    pub grounded: bool,
    pub contact: V3,
}

pub struct Bike {
    pub h: Handling,
    pub b: BikeLine,
    pub mass: f32,
    pub inertia: V3,     // about the bike's x (pitch), y (roll), z (yaw)
    pub com: V3,
    pub pos: V3,         // the model's origin
    pub q: Q,
    pub vel: V3,
    pub ang: V3,         // world
    pub wheels: [Wheel; 2],
    pub gear: Transmission,
    pub steer: f32,      // the front wheel's angle (radians, + right)
    pub lean: f32,       // roll (radians, + right)
    pub pitch: f32,
    pub time_ms: u32,
    pub fallen: bool,
    pub drive: f32,
    pub shift: f32,
    pub steer_in: f32,
}

const G: f32 = 9.8;

impl Bike {
    pub fn new(h: Handling, b: BikeLine, inertia_per_kg: V3, front: (V3, f32), rear: (V3, f32)) -> Bike {
        let mass = h.mass;
        let mk = |at: V3, r: f32, fr: bool| {
            let spring = Spring::new_at_height(&h, fr, 0.5, r, at[2]);
            let mut tyre = Tyre::new(fr, 0.5);
            if !fr { tyre.flags |= tyre::flags::DRIVEN() | tyre::flags::POWERED(); }
            Wheel { at, radius: r, front: fr, spring, tyre, x: spring.rest, load: 0.0, slip: 0.0, spin: 0.0, grounded: false, contact: [0.0; 3] }
        };
        let com = h.centre_of_mass;
        Bike {
            inertia: [inertia_per_kg[0].max(0.05) * mass, inertia_per_kg[1].max(0.05) * mass, inertia_per_kg[2].max(0.05) * mass],
            com, mass, pos: [0.0; 3], q: Q([0.0, 0.0, 0.0, 1.0]), vel: [0.0; 3], ang: [0.0; 3],
            wheels: [mk(front.0, front.1, true), mk(rear.0, rear.1, false)],
            gear: Transmission::default(), steer: 0.0, lean: 0.0, pitch: 0.0, time_ms: 0, fallen: false, drive: 0.0, shift: 0.0, steer_in: 0.0, h, b,
        }
    }

    fn world_com(&self) -> V3 { add(self.pos, self.q.rot(self.com_now())) }

    /// The centre of mass with the rider's weight moved (LeanFwdCOM / LeanBakCOM).
    fn com_now(&self) -> V3 { [self.com[0], self.com[1] + self.shift, self.com[2]] }

    /// The bike's frame's axes in the world.
    pub fn axes(&self) -> (V3, V3, V3) { (self.q.rot([1.0, 0.0, 0.0]), self.q.rot([0.0, 1.0, 0.0]), self.q.rot([0.0, 0.0, 1.0])) }

    /// One step: `ground` one plane a wheel (front, rear).
    pub fn step(&mut self, input: Input, ground: [Ground; 2], dt: f32) {
        let n = ((dt / (1.0 / 240.0)).ceil() as usize).clamp(1, 16);
        let h = dt / n as f32;
        // the gearbox, once a frame (GTA's per-frame call)
        let (_, fwd, _) = self.axes();
        let along = dot(self.vel, fwd);
        let wheel_speeds = [along, along];
        let grounded = self.wheels.iter().filter(|w| w.grounded).count();
        let mut thr = input.throttle.clamp(0.0, 1.0);
        let mut brake = input.brake.clamp(0.0, 1.0);
        // brake held when (nearly) stopped: reverse, GTA's way (a negative throttle)
        if brake > 0.1 && along < 1.0 && thr < 0.05 { thr = -brake; brake = 0.0; }
        let c = Controls { throttle: thr, brake, handbrake: input.handbrake, engine_off: false, bike: true, flags_f80: 0 };
        self.time_ms = self.time_ms.wrapping_add((dt * 1000.0) as u32);
        // drive wheels: the rear one (drive_bias [front, rear])
        self.drive = self.gear.process(&self.h, c, &wheel_speeds[..1], along, grounded.min(1), dt, self.time_ms);
        // steering: the bars eased toward the arrows (an arrow is all or nothing), and never more lock than the
        // tyres can hold at this speed (the turn's pull v^2 tan(a) / wheelbase within the grip): no sliding round
        let target = input.steer.clamp(-1.0, 1.0);
        let rate = if target.abs() < self.steer_in.abs() || target * self.steer_in < 0.0 { 5.0 } else { 2.5 };
        self.steer_in += (target - self.steer_in).clamp(-rate * dt, rate * dt);
        let speed = len(self.vel);
        let base = (self.wheels[0].at[1] - self.wheels[1].at[1]).abs().max(0.8);
        let grip = G * self.h.traction_max.clamp(0.8, 2.0) * 0.7;
        let hold = (base * grip / speed.max(0.1).powi(2)).atan();
        let lock = self.h.steering_lock.min(hold);
        let want = self.steer_in * lock;
        self.steer += (want - self.steer) * (1.0 - (-dt * 10.0).exp());
        let want_shift = if input.lean > 0.0 { input.lean * self.b.lean_fwd_com } else { input.lean * self.b.lean_back_com };
        self.shift += (want_shift - self.shift) * (1.0 - (-dt * 6.0).exp());
        let saved = (self.pos, self.q, self.vel, self.ang, [self.wheels[0].x, self.wheels[1].x]);
        for _ in 0..n {
            self.substep(input, brake, &ground, h);
            let ok = self.pos.iter().chain(self.vel.iter()).chain(self.ang.iter()).chain(self.q.0.iter()).all(|v| v.is_finite())
                && self.vel.iter().all(|v| v.abs() < 300.0) && self.ang.iter().all(|v| v.abs() < 80.0);
            if !ok {
                // something blew up: back to where this frame began, at rest
                (self.pos, self.q) = (saved.0, saved.1);
                self.vel = [0.0; 3];
                self.ang = [0.0; 3];
                self.wheels[0].x = saved.4[0];
                self.wheels[1].x = saved.4[1];
                for w in self.wheels.iter_mut() { w.slip = 0.0; }
                break;
            }
        }
        let (right, fwd, up) = self.axes();
        self.lean = right[2].atan2(up[2]);
        self.pitch = fwd[2].asin();
        if self.lean.abs() > self.b.max_lean + 0.9 { self.fallen = true; }
    }

    fn substep(&mut self, input: Input, brake: f32, ground: &[Ground; 2], dt: f32) {
        let (right, fwd, up) = self.axes();
        let com = self.world_com();
        let mut force = [0.0, 0.0, -G * self.mass];
        let mut torque = [0.0f32; 3];
        let speed = len(self.vel);
        // air drag (handling.dat's drag: an acceleration of drag * v^2)
        force = add(force, mulv(self.vel, -self.h.drag * speed * self.mass));
        let steer = self.steer;
        let (cs, sn) = (steer.cos(), steer.sin());
        let mut on = 0;
        for i in 0..2 {
            let w = &mut self.wheels[i];
            let s = w.spring;
            // the probe: from the spring's top straight down the bike, `travel` long
            let top_local = [w.at[0], w.at[1], w.at[2] + self.h.suspension_upper];
            let start = add(self.pos, self.q.rot(top_local));
            let down = mulv(up, -1.0);
            let g = ground[i];
            let mut d = f32::MAX;
            if g.hit {
                let den = dot(down, g.normal);
                if den < -0.2 { d = dot(sub(g.point, start), g.normal) / den; }
            }
            let mut x_new = if d >= -0.3 && d <= s.travel { s.travel - d } else { 0.0 };
            // past the spring's travel (a kerb, a step, a hard landing): the frame meets the ground, a contact, not
            // a spring: lifted out of it and its speed into the ground taken away
            if x_new > s.travel {
                let over = (x_new - s.travel).min(0.08);
                self.pos = add(self.pos, mulv(g.normal, over));
                let at = add(start, mulv(down, d));
                let vp = add(self.vel, cross(self.ang, sub(at, com)));
                let into = dot(vp, g.normal);
                if into < 0.0 { self.vel = sub(self.vel, mulv(g.normal, into * 0.6)); }
                x_new = s.travel;
            }
            // (a step's edge moves the ground at once: not a speed of the spring)
            let rate = ((x_new - w.x) / dt).clamp(-3.0, 3.0);
            w.x = x_new;
            w.grounded = x_new > 0.0;
            if !w.grounded { w.load = 0.0; w.slip = 0.0; continue; }
            on += 1;
            let contact = add(start, mulv(down, d));
            w.contact = contact;
            let nrm = g.normal;
            let arm = sub(contact, com);
            // the spring and damper (GTA IV's), along the ground's normal
            let load = suspension::force(&self.h, &s, w.x, rate, self.mass);
            let mut fz = load.force;
            // near the end of its travel: a stiff bump stop
            if w.x > s.travel - 0.05 { fz += (w.x - (s.travel - 0.05)) * self.mass * G * 20.0; }
            w.load = load.tyre.max(0.0);
            let f_sus = mulv(nrm, fz);
            force = add(force, f_sus);
            let tq = cross(arm, f_sus);
            torque = add(torque, if self.fallen { tq } else { sub(tq, mulv(fwd, dot(tq, fwd))) });
            // the tyre's axes on the ground (the front one turned by the steering)
            let wfwd0 = if w.front { add(mulv(fwd, cs), mulv(right, sn)) } else { fwd };
            let side0 = cross(wfwd0, nrm);
            let wside = unit(side0);           // to the right of the tyre's way... (cross(fwd, up) = right)
            let wfwd = unit(cross(nrm, wside));
            let v = add(self.vel, cross(self.ang, sub(contact, com)));
            let along = dot(v, wfwd);
            let across = dot(v, wside);
            let up_v = dot(v, nrm);
            // too light on the ground for grip (a wheel brushing a step's edge): no tyre force, as the game's own
            // wheel step skips it (its load is the divisor of the tyre's sums)
            if !(w.load > self.mass * G * delirium_data::kv!(51)) { w.slip = 0.0; continue; }
            let drive_n = if w.front { 0.0 } else { self.drive * self.mass * G };
            let bias = self.h.brake_bias[if w.front { 0 } else { 1 }];
            let mut brake_n = brake * self.h.brake_force * bias * 0.5 * self.mass * G;
            if input.handbrake && !w.front { brake_n += self.h.handbrake * self.mass * G; }
            let ti = TyreIn { slip_speed: len(v) - up_v, along, across: -across, hold_along: 0.0, hold_across: 0.0, slip: w.slip,
                              brake: brake_n, drive: drive_n, load: w.load, h: dt };
            let inv_mass = 1.0 / self.mass;
            let [f_side, f_along, next] = tyre::tyre_force(&self.h, &mut w.tyre, self.mass, inv_mass, &ti);
            w.slip = if next.is_finite() { next } else { 0.0 };
            let f_t = add(mulv(wfwd, f_along), mulv(wside, -f_side));
            force = add(force, f_t);
            // the tyre's push turns the bike but doesn't roll it (the balance below does that)
            let tq = cross(arm, f_t);
            torque = add(torque, if self.fallen { tq } else { sub(tq, mulv(fwd, dot(tq, fwd))) });
            w.spin += along / w.radius * dt;
        }
        // the balance (CBike's job): roll toward the lean the turn wants, pitch by the rider's lean
        let b = self.b;
        let lean = right[2].atan2(up[2]);
        let roll_rate = dot(self.ang, fwd);
        let yaw_rate = dot(self.ang, up);
        let along = dot(self.vel, fwd);
        if !self.fallen {
            // the lean that balances the turn, the stick's own lean on top, within MaxLean
            // (lean > 0: tipped left; turning left, yaw_rate > 0, wants a left lean; the lean falls as the bike
            // turns about its forward axis)
            let mut target = (along * yaw_rate / G).atan() * (1.0 + b.des_lean * 0.5);
            target = target.clamp(-b.max_lean, b.max_lean);
            // stiffer than gravity's pull over the tyres (m g h / I is about 45 1/s^2 on a PCJ)
            let k = if on > 0 { 160.0 } else { 6.0 };
            let c = if on > 0 { 24.0 } else { 3.0 };
            let lean_rate = -roll_rate;
            let alpha = k * (target - lean) - c * lean_rate;
            torque = add(torque, mulv(fwd, -alpha * self.inertia[1]));
            // pitch: the rider's weight (LeanFwd/BackForce), the wheelie / stoppie limits
            let pitch = fwd[2].asin();
            let pitch_rate = dot(self.ang, right);
            let mut p_acc = 0.0;
            if input.lean > 0.0 { p_acc -= input.lean * b.lean_fwd_force * if on == 0 { 0.5 } else { 0.6 }; }
            if input.lean < 0.0 { p_acc -= input.lean * b.lean_back_force * if on == 0 { 0.5 } else { 0.6 }; }
            if pitch > b.wheelie_ang { p_acc -= (pitch - b.wheelie_ang) * b.wheelie_stab * 4.0 + pitch_rate * 2.0; }
            if pitch < b.stoppie_ang { p_acc -= (pitch - b.stoppie_ang) * b.stoppie_stab * 4.0 + pitch_rate * 2.0; }
            if on == 0 {
                // in the air: the stick turns and tips it (InAirSteer)
                let yaw_acc = -input.steer * b.in_air_steer.abs() * 1.2 - yaw_rate * 1.5; // right: clockwise
                torque = add(torque, mulv(up, yaw_acc * self.inertia[2]));
                p_acc -= pitch_rate * 0.8;
            }
            torque = add(torque, mulv(right, p_acc * self.inertia[0]));
            // braking: BrakeStabil holds its heading
            if on > 0 && brake > 0.0 {
                let y_acc = -yaw_rate * brake * b.brake_stabil.abs() * 6.0;
                torque = add(torque, mulv(up, y_acc * self.inertia[2]));
            }
            // standing still: a foot down keeps it up and it doesn't creep
            if on > 0 && along.abs() < 0.5 && input.throttle < 0.05 { force = add(force, mulv(self.vel, -self.mass * 2.0)); }
        }
        // integrate (semi-implicit), the inertia in the bike's frame
        self.vel = add(self.vel, mulv(force, dt / self.mass));
        let lt = self.q.inv_rot(torque);
        let la = self.q.inv_rot(self.ang);
        let ia = [la[0] * self.inertia[0], la[1] * self.inertia[1], la[2] * self.inertia[2]];
        let gyro = cross(la, ia);
        let acc = [(lt[0] - gyro[0]) / self.inertia[0], (lt[1] - gyro[1]) / self.inertia[1], (lt[2] - gyro[2]) / self.inertia[2]];
        let la = add(la, mulv(acc, dt));
        self.ang = self.q.rot(la);
        // the origin moves with the centre of mass's motion
        let com0 = self.world_com();
        let com1 = add(com0, mulv(self.vel, dt));
        self.q.integrate(self.ang, dt);
        self.pos = sub(com1, self.q.rot(self.com_now()));
    }
}
