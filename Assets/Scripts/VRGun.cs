// The Quest (VR): the gun in your right hand. The menu's "Gun" picks it (the phone's guns: pistol, Desert Eagle, SMG,
// AK-47, shotgun, sniper rifle); the right trigger fires it along its barrel (held for the SMG and AK-47), the left
// trigger uses a map's button or door. Your height: the menu's "Height", stood straight.
//
// Its look: GTA V's own when your Grand Theft Gmod folder has its weapons (models/**/w_*.mdl beside your GTA IV
// files: grandtheftgmod / grandtheftgmod_0899), read with the GMod model reader (a loose .mdl: native/bsp
// player.rs) and turned so its barrel is the gun's; else the phone's boxes.
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public partial class Game
{
    bool VrArmed() => heroOn && hero != null && !kGuns[weapon % kGuns.Length].Fists && heroMode == 0 && !riding;

    void VrCalibrate()
    {
        if (cam == null) return;
        vrEye = cam.transform.localPosition.y; // (the rig's origin is your floor)
        PlayerPrefs.SetFloat(kPrefEye, vrEye);
        PlayerPrefs.Save();
        note = "Your height: about " + Mathf.RoundToInt((vrEye + 0.11f) * 100f) + " cm (eyes " + vrEye.ToString("0.00") + " m). He's drawn to it.";
        VrMenuBuild();
    }

    // ------------------------------------------------------------------ firing
    void VrFire()
    {
        var gun = kGuns[weapon % kGuns.Length];
        if (gun.Fists || gunModel == null) return;
        heroNextFire = Time.time + gun.Between;
        Vector3 muzzle = gunModel.transform.TransformPoint(gunMuzzle);
        Vector3 dir = gunModel.transform.right; // (the gun's +X: its barrel)
        var ray = new Ray(muzzle, dir);
        var m = Pick(ray, out Vector3 _);
        Flash(muzzle, 0.14f);
        PlayGun(muzzle);
        Missed(ray, m);
        if (vrR != null) vrR.HapticPulse(gun.Long ? 0.9f : 0.6f, gun.Long ? 0.08f : 0.05f);
        vrKick = Mathf.Min(vrKick + (gun.Long ? 6f : 9f), 18f);
        if (m == null || m.Rising) return;
        var owner = m.Body.transform;
        Attacked(m);
        if (m.Killed)
        {
            nm.ShootAlong(owner, ray.origin, ray.direction, 20f, "POWERFUL_WEAPON", 0f);
            return;
        }
        Shoot(m, ray);
    }
    float vrKick; // degrees the barrel's kicked up, eased back

    // ------------------------------------------------------------------ in your hand
    // (VrBodyLate, after the body) the gun at your right hand, its grip in your palm, its barrel along your pointing
    static readonly Vector3 kGrip = new Vector3(-0.01f, -0.06f, 0f); // where the hand holds the phone's gun (its frame)

    void VrGunHold()
    {
        if (gunModel == null || vrR == null || !VrArmed() || vrThird || !vrR.isActiveAndEnabled) return;
        vrKick = Mathf.MoveTowards(vrKick, 0f, Time.deltaTime * 60f);
        var hand = vrR.transform;
        Quaternion rot = hand.rotation * Quaternion.Euler(-vrKick, 0f, 0f) * Quaternion.Euler(0f, -90f, 0f);
        Vector3 palm = hand.position + hand.rotation * new Vector3(0f, -0.005f, -0.01f);
        gunModel.transform.SetPositionAndRotation(palm - rot * kGrip, rot);
    }

    // ------------------------------------------------------------------ GTA V's guns
    int vrGunDressed = -1;
    GameObject vrGunDressedOn;

    // (VrCamera) the gun just made (boxes, GunInHand): in the world (not his scaled body), and GTA V's look if it's there
    void VrGunDress()
    {
        if (gunModel == null) return;
        int k = weapon % kGuns.Length;
        if (vrGunDressedOn == gunModel && vrGunDressed == k) return;
        vrGunDressedOn = gunModel;
        vrGunDressed = k;
        gunModel.transform.SetParent(null, true);
        var look = VrGunLook(kGuns[k].Name, out Vector3 muzzle);
        if (look == null) return;
        for (int i = gunModel.transform.childCount - 1; i >= 0; i--) Destroy(gunModel.transform.GetChild(i).gameObject);
        var g = Instantiate(look, gunModel.transform, false);
        g.SetActive(true);
        g.transform.localPosition = kGrip;
        gunMuzzle = kGrip + g.transform.localRotation * Vector3.Scale(muzzle, g.transform.localScale);
    }

    // each gun's GTA V model: its file's name has one of these (the first that's there; never the ones after "-")
    static readonly Dictionary<string, string[]> kGunFiles = new Dictionary<string, string[]>
    {
        { "pistol", new[] { "w_pi_pistol.mdl", "w_pi_combatpistol.mdl", "w_pi_pistol", "pistol", "-50", "-heavy", "-sns", "-vintage" } },
        { "deagle", new[] { "w_pi_pistol50.mdl", "pistol50", "deagle", "heavypistol", "revolver" } },
        { "smg", new[] { "w_sb_smg.mdl", "w_sb_microsmg.mdl", "smg", "mp5", "-assault" } },
        { "ak47", new[] { "w_ar_assaultrifle.mdl", "ak47", "assaultrifle", "carbinerifle", "rifle", "-sniper", "-marksman", "-heavy" } },
        { "shotgun", new[] { "w_sg_pumpshotgun.mdl", "pumpshotgun", "shotgun" } },
        { "sniper", new[] { "w_sr_sniperrifle.mdl", "sniperrifle", "sniper", "marksman" } },
    };
    static readonly string[] kGunParts = { "_mag", "mag1", "mag2", "_supp", "_flsh", "_scope", "_afgrip", "_clip", "_comp", "_barrel", "_ammo", "_luxe", "_boxmag", "_drum" };

    List<string> vrGunMdls;
    readonly Dictionary<string, GameObject> vrGunLooks = new Dictionary<string, GameObject>();
    readonly Dictionary<string, Vector3> vrGunMuzzles = new Dictionary<string, Vector3>();

    // the weapons' .mdl files in Grand Theft Gmod's folder (and any addon folder beside your files), once
    List<string> VrGunFiles()
    {
        if (vrGunMdls != null) return vrGunMdls;
        vrGunMdls = new List<string>();
        var bases = new List<string>();
        foreach (var b in new[] { dataDir, Directory.Exists(gameExe ?? "") ? gameExe : Path.GetDirectoryName(gameExe ?? ""), gmodDir })
        {
            if (string.IsNullOrEmpty(b)) continue;
            foreach (var sub in new[] { "", "grandtheftgmod_0899", "grandtheftgmod" })
            {
                string d = sub == "" ? b : Path.Combine(b, sub);
                if (Directory.Exists(Path.Combine(d, "models")) && !bases.Contains(d)) bases.Add(d);
            }
        }
        foreach (var d in bases)
        {
            try
            {
                foreach (var f in Directory.GetFiles(Path.Combine(d, "models"), "*.mdl", SearchOption.AllDirectories))
                {
                    string n = Path.GetFileName(f).ToLowerInvariant();
                    if (n.StartsWith("v_") || n.StartsWith("c_")) continue; // (a first-person model, with arms)
                    if (!n.StartsWith("w_") && f.Replace('\\', '/').ToLowerInvariant().IndexOf("weapon", StringComparison.Ordinal) < 0) continue;
                    vrGunMdls.Add(f);
                }
            }
            catch (Exception e) { Debug.LogWarning("[Sandbox] VR guns in " + d + ": " + e.Message); }
        }
        Debug.Log("[Sandbox] VR guns: " + vrGunMdls.Count + " weapon models in " + string.Join(", ", bases));
        return vrGunMdls;
    }

    string VrGunFile(string gun)
    {
        if (!kGunFiles.TryGetValue(gun, out var want)) return null;
        var files = VrGunFiles();
        var not = new List<string>();
        foreach (var w in want) if (w.StartsWith("-")) not.Add(w.Substring(1));
        foreach (var w in want)
        {
            if (w.StartsWith("-")) continue;
            foreach (var f in files)
            {
                string n = Path.GetFileName(f).ToLowerInvariant();
                if (w.EndsWith(".mdl") ? n != w : n.IndexOf(w, StringComparison.Ordinal) < 0) continue;
                bool part = false;
                foreach (var p in kGunParts) if (n.Contains(p)) part = true;
                foreach (var x in not) if (n.Contains(x)) part = true;
                if (!part) return f;
            }
        }
        return null;
    }

    // its model, laid in the gun's frame (+X the barrel, +Y its top, its origin, the grip, at ours): kept, copied
    GameObject VrGunLook(string gun, out Vector3 muzzle)
    {
        muzzle = new Vector3(0.2f, 0.03f, 0f);
        if (vrGunLooks.TryGetValue(gun, out var have))
        {
            if (have != null) muzzle = vrGunMuzzles[gun];
            return have;
        }
        vrGunLooks[gun] = null;
        string file = VrGunFile(gun);
        if (file == null) return null;
        try
        {
            var data = MdlToGlb(file, out string why);
            if (data == null) { Debug.LogWarning("[Sandbox] VR gun " + gun + ": " + file + ": " + why); return null; }
            var made = GlbScene.Load(data, Path.GetFileNameWithoutExtension(file), ModelMaterial());
            if (made?.Root == null) return null;
            var root = made.Root;
            root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            root.transform.localScale = Vector3.one;
            foreach (var c in root.GetComponentsInChildren<Collider>(true)) Destroy(c);
            foreach (var a in root.GetComponentsInChildren<Animator>(true)) Destroy(a);
            // its points (as it stands) for its size and its way
            var pts = new List<Vector3>();
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var baked = new Mesh();
                smr.BakeMesh(baked);
                var m = smr.transform.localToWorldMatrix;
                foreach (var v in baked.vertices) pts.Add(m.MultiplyPoint3x4(v));
                Destroy(baked);
                smr.updateWhenOffscreen = true;
                smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null || !mf.sharedMesh.isReadable) continue;
                var m = mf.transform.localToWorldMatrix;
                foreach (var v in mf.sharedMesh.vertices) pts.Add(m.MultiplyPoint3x4(v));
            }
            if (pts.Count < 8) { Destroy(root); return null; }
            Vector3 lo = pts[0], hi = pts[0], mid = Vector3.zero;
            foreach (var p in pts) { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); mid += p; }
            mid /= pts.Count;
            Vector3 size = hi - lo;
            // the barrel: the longest way (Source's and GTA's guns point +X, here +Z), toward the end further from the
            // grip; the top: the next longest, the side its bulk is on
            int ax = size.z >= size.x && size.z >= size.y ? 2 : size.x >= size.y ? 0 : 1;
            Vector3 fwd = Vector3.zero;
            fwd[ax] = Mathf.Abs(hi[ax]) >= Mathf.Abs(lo[ax]) ? 1f : -1f;
            int upAx = ax == 1 ? 2 : (ax == 2 ? (size.y >= size.x ? 1 : 0) : (size.y >= size.z ? 1 : 2));
            if (ax != 1 && upAx != 1 && size.y > 0.3f * Mathf.Max(size.x, size.z)) upAx = 1;
            Vector3 up = Vector3.zero;
            up[upAx] = upAx == 1 ? 1f : (mid[upAx] >= 0f ? 1f : -1f);
            // a gun of a sensible length (a file in the wrong units)
            float len = size[ax];
            float wantLen = gun == "pistol" ? 0.2f : gun == "deagle" ? 0.27f : gun == "smg" ? 0.45f : gun == "shotgun" ? 0.95f : gun == "sniper" ? 1.15f : 0.85f;
            float k = len > 0.01f && (len < wantLen * 0.4f || len > wantLen * 2.5f) ? wantLen / len : 1f;
            var holder = new GameObject("GTA V " + gun);
            holder.SetActive(false);
            DontDestroyOnLoad(holder);
            root.transform.SetParent(holder.transform, false);
            root.transform.localRotation = Quaternion.LookRotation(Vector3.right, Vector3.up) * Quaternion.Inverse(Quaternion.LookRotation(fwd, up));
            holder.transform.localScale = Vector3.one * k;
            // the muzzle: the barrel's end, at the middle of the gun's top half
            Vector3 end = mid;
            end[ax] = fwd[ax] > 0f ? hi[ax] : lo[ax];
            muzzle = root.transform.localRotation * end;
            vrGunLooks[gun] = holder;
            vrGunMuzzles[gun] = muzzle;
            Debug.Log("[Sandbox] VR gun " + gun + ": " + Path.GetFileName(file) + " (" + why + "), " + len.ToString("0.00") + " m long" + (k != 1f ? " scaled x" + k.ToString("0.###") : ""));
            return holder;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[Sandbox] VR gun " + gun + ": " + e.Message);
            return null;
        }
    }
}
