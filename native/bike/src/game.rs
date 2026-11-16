//! Your GTA IV's files: handling.dat (common.rpf), the bikes' models and textures (xbox360/models/cdimages/
//! vehicles.img), keys out of default.xex (or GTAIV.exe, pc/... on the PC version).
use crate::{glb, sim};
use delirium_data::{archive_key::ArchiveKey, img, kv, resource, rpf, vehicles, xex};
use std::collections::HashMap;
use std::io::{Read, Seek, SeekFrom};
use std::path::{Path, PathBuf};

pub struct Game {
    pub handling: HashMap<String, vehicles::Handling>,
    pub bikes: HashMap<String, [f32; 15]>,
    vehicles: PathBuf,
    table: Vec<img::Entry>,
}

/// A bike's model name in vehicles.img -> its handling.dat id.
pub const BIKES: &[(&str, &str, &str)] = &[
    // model, handling, anim dictionary
    ("pcj", "PCJ", "veh@bike_spt"),
    ("nrg900", "NRG900", "veh@bike_spt"),
    ("sanchez", "SANCHEZ", "veh@bike_dirt"),
    ("faggio", "FAGGIO", "veh@bike_scooter"),
    ("hellfury", "HELLFURY", "veh@bike_chopper"),
    ("bobber", "BOBBER", "veh@bike_chopper"),
    ("zombieb", "ZOMBIE", "veh@bike_chopper"),
];

impl Game {
    pub fn open(dir: &Path) -> Result<Game, String> {
        let pc = dir.join("GTAIV.exe").is_file();
        // the key and the numbers: from the clips' opening if it's done (default.xex is slow to unpack), else here
        let mut prog: Option<Vec<u8>> = None;
        let key = match (kv::open(), delirium_anim::KEY.get()) {
            (true, Some(k)) => k.clone(),
            _ => {
                let exe = if pc { dir.join("GTAIV.exe") } else { dir.join("default.xex") };
                let p = xex::program(&exe)?;
                if !kv::unlock(&p) {
                    return Err(format!("{}: not a version the numbers are read from", exe.display()));
                }
                delirium_data::LE.store(pc, std::sync::atomic::Ordering::Relaxed);
                let mut head = vec![0u8; 0x810];
                let platform = if pc { dir.join("pc/models/cdimages/playerped.rpf") } else { dir.join("xbox360.rpf") };
                std::fs::File::open(&platform).and_then(|mut f| f.read_exact(&mut head)).map_err(|e| format!("{}: {e}", platform.display()))?;
                let k = ArchiveKey::find_by_trial(&p, &head).map(|(k, _)| k).ok_or("no archive key in the game's program")?;
                prog = Some(p);
                k
            }
        };
        // handling.dat
        let common = std::fs::read(dir.join("common.rpf")).map_err(|e| format!("common.rpf: {e} (copy it next to default.xex)"))?;
        let toc = match rpf::read_toc(&common, &key) {
            Ok(t) => t,
            Err(_) => {
                let p = match prog { Some(p) => p, None => xex::program(&if pc { dir.join("GTAIV.exe") } else { dir.join("default.xex") })? };
                let ckey = ArchiveKey::find_by_trial(&p, &common[..0x810.min(common.len())]).map(|(k, _)| k).ok_or("common.rpf: no key")?;
                rpf::read_toc(&common, &ckey)?
            }
        };
        let e = toc.iter().find(|e| e.path.eq_ignore_ascii_case("data/handling.dat")).ok_or("handling.dat isn't in common.rpf")?;
        let text = String::from_utf8_lossy(&rpf::read(&common, e)?).into_owned();
        let handling = vehicles::read_handling(&text);
        let bikes = vehicles::read_bike(&text);
        // vehicles.img's table
        let vehicles = if pc { dir.join("pc/models/cdimages/vehicles.img") } else { dir.join("xbox360/models/cdimages/vehicles.img") };
        let mut f = std::fs::File::open(&vehicles).map_err(|e| format!("vehicles.img: {e} (copy xbox360/models/cdimages/vehicles.img)"))?;
        let mut h = vec![0u8; 20];
        f.read_exact(&mut h).map_err(|e| e.to_string())?;
        let n = img::table_len(&h, Some(&key)).ok_or("vehicles.img: not an IMG archive")?;
        let mut head = vec![0u8; n];
        f.seek(SeekFrom::Start(0)).map_err(|e| e.to_string())?;
        f.read_exact(&mut head).map_err(|e| e.to_string())?;
        let table = img::read_table(&head, Some(&key))?;
        Ok(Game { handling, bikes, vehicles, table })
    }

    fn resource(&self, name: &str) -> Result<resource::Resource, String> {
        let e = self.table.iter().find(|e| e.name.eq_ignore_ascii_case(name)).ok_or(format!("{name} isn't in vehicles.img"))?;
        let mut f = std::fs::File::open(&self.vehicles).map_err(|e| e.to_string())?;
        f.seek(SeekFrom::Start(e.offset)).map_err(|e| e.to_string())?;
        let mut raw = vec![0u8; e.size() as usize];
        f.read_exact(&mut raw).map_err(|e| e.to_string())?;
        resource::parse(&raw)
    }

    /// The bikes this game has (model, handling and bike line all there).
    pub fn bike_list(&self) -> Vec<&'static str> {
        BIKES.iter().filter(|(m, h, _)| self.handling.contains_key(*h) && self.bikes.contains_key(*h)
            && self.table.iter().any(|e| e.name.eq_ignore_ascii_case(&format!("{m}.xft")))).map(|b| b.0).collect()
    }

    /// A bike: its model and a simulation of it, at rest at the origin.
    pub fn bike(&mut self, model: &str, paint: [[f32; 3]; 2]) -> Result<(glb::Built, sim::Bike), String> {
        let (_, hid, _) = BIKES.iter().find(|b| b.0 == model).ok_or("not a bike")?;
        let frag = self.resource(&format!("{model}.xft"))?;
        let own = self.resource(&format!("{model}.xtd")).ok();
        let share = self.resource("vehshare.xtd").ok();
        let dicts: Vec<&resource::Resource> = own.iter().chain(share.iter()).collect();
        let built = glb::build(&frag, &dicts, paint)?;
        let h = self.handling.get(*hid).cloned().ok_or("not in handling.dat")?;
        let line = sim::BikeLine::from(self.bikes.get(*hid).ok_or("no bike line in handling.dat")?);
        let find = |names: &[&str]| built.parts.iter().position(|p| names.iter().any(|n| p.name.eq_ignore_ascii_case(n)));
        let fi = find(&["wheel_lf", "wheel_rf"]).ok_or("no front wheel")?;
        let ri = find(&["wheel_lr", "wheel_rr"]).ok_or("no rear wheel")?;
        let r = |i: usize| if built.radius[i] > 0.1 { built.radius[i] } else { 0.3 };
        let inertia = delirium_data::model::fragment_inertia(&frag).unwrap_or([0.4, 0.1, 0.3]);
        let bike = sim::Bike::new(h, line, inertia, (built.parts[fi].world, r(fi)), (built.parts[ri].world, r(ri)));
        Ok((built, bike))
    }
}
