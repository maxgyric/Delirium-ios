// v <addon folder> <seq>...: GTA V's clips (Grand Theft Gmod's) onto the sandbox mannequin's skeleton: speed, sync,
// and the pose's pelvis height and feet (a check that it stands on the ground, facing +X)
use std::ffi::CString;
fn main() {
    let a: Vec<String> = std::env::args().collect();
    let dir = CString::new(a[1].clone()).unwrap();
    unsafe {
        assert_eq!(delirium_anim::dv_open(dir.as_ptr()), 0, "open");
        // the mannequin's (Mannequin.Skeleton: GTA names, Unity y up -> z up)
        let joints: [(&str, i32, [f32; 3]); 23] = [
            ("Pelvis", -1, [0.0, 0.0, 0.98]), ("L Thigh", 0, [-0.095, 0.0, 0.93]), ("L Calf", 1, [-0.095, 0.005, 0.50]), ("L Foot", 2, [-0.095, -0.02, 0.09]), ("L Toe0", 3, [-0.095, 0.12, 0.03]),
            ("R Thigh", 0, [0.095, 0.0, 0.93]), ("R Calf", 5, [0.095, 0.005, 0.50]), ("R Foot", 6, [0.095, -0.02, 0.09]), ("R Toe0", 7, [0.095, 0.12, 0.03]),
            ("Spine", 0, [0.0, 0.0, 1.06]), ("Spine1", 9, [0.0, 0.0, 1.15]), ("Spine2", 10, [0.0, 0.0, 1.24]), ("Spine3", 11, [0.0, 0.0, 1.33]), ("Neck", 12, [0.0, 0.0, 1.47]), ("Head", 13, [0.0, 0.01, 1.56]),
            ("L Clavicle", 12, [-0.045, 0.0, 1.42]), ("L UpperArm", 15, [-0.175, 0.0, 1.42]), ("L Forearm", 16, [-0.455, 0.0, 1.42]), ("L Hand", 17, [-0.715, 0.0, 1.42]),
            ("R Clavicle", 12, [0.045, 0.0, 1.42]), ("R UpperArm", 19, [0.175, 0.0, 1.42]), ("R Forearm", 20, [0.455, 0.0, 1.42]), ("R Hand", 21, [0.715, 0.0, 1.42])];
        let names: Vec<CString> = joints.iter().map(|j| CString::new(format!("Bip01 {}", j.0)).unwrap()).collect();
        let ptrs: Vec<*const i8> = names.iter().map(|c| c.as_ptr()).collect();
        let parents: Vec<i32> = joints.iter().map(|j| j.1).collect();
        let mut local = vec![];
        for j in joints.iter() {
            let p = if j.1 >= 0 { let q = joints[j.1 as usize].2; [j.2[0] - q[0], j.2[1] - q[1], j.2[2] - q[2]] } else { j.2 };
            local.extend([p[0], p[1], p[2], 0.0, 0.0, 0.0, 1.0]);
        }
        let mut rep = vec![0i8; 1024];
        let moving = delirium_anim::da_target(23, ptrs.as_ptr(), parents.as_ptr(), local.as_ptr(), rep.as_mut_ptr(), 1024);
        println!("target: {moving} moving: {}", std::ffi::CStr::from_ptr(rep.as_ptr()).to_string_lossy());
        for s in &a[2..] {
            // "+name": about the model's ground (da_pose_ground: climbs), its rise (the clip data's) printed
            let ground = s.starts_with('+');
            let s = &s.trim_start_matches('+').to_string();
            let n = CString::new(s.clone()).unwrap();
            let c = delirium_anim::dv_clip(n.as_ptr());
            let (mut d, mut v) = (0.0, 0.0);
            delirium_anim::da_clip_info(c, &mut d, &mut v);
            let sync = delirium_anim::da_clip_sync(c);
            let mut out = vec![0f32; 23 * 7];
            let mut moved = vec![0i32; 23];
            let mut zc = vec![0f32; 256];
            let nz = delirium_anim::dv_clip_data(c, 2, zc.as_mut_ptr(), 256);
            print!("{s}: clip {c} dur {d:.2} speed {v:.2} sync {sync:.2} rise {:?} |", &zc[..nz.max(0) as usize]);
            let pose = |c, p, o: *mut f32, m: *mut i32| if ground { delirium_anim::da_pose_ground(c, p, o, m) } else { delirium_anim::da_pose(c, p, o, m) };
            for k in 0..5 {
                pose(c, k as f32 / 4.0, out.as_mut_ptr(), moved.as_mut_ptr());
                let z = |b: usize| out[b * 7 + 2];
                print!(" pelvis {:.2} feet {:.2}/{:.2} head {:.2} lfoot x {:.2};", z(0), z(3), z(7), z(14), out[3 * 7]);
            }
            println!();
            for k in 0..8 {
                pose(c, k as f32 / 8.0, out.as_mut_ptr(), moved.as_mut_ptr());
                println!("P {s} {k} {}", (0..23).map(|b| format!("{:.3},{:.3},{:.3}", out[b * 7], out[b * 7 + 1], out[b * 7 + 2])).collect::<Vec<_>>().join(" "));
            }
        }
    }
}
