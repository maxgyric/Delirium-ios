// Hero: GTA V's climbs and vaults, and GTA V's aim and shot (Grand Theft Gmod's clips, made by its exporter from
// your own GTA V; GtaAnim.V). Jump in front of something he can reach (0.5-2.95 m) climbs it the way Grand Theft
// Gmod does (its sh_motion's ledge finding and clip choice): the clip for the ledge's height (two of them blended),
// its own way along and its rise (the clip data's) scaled to the real height, lined up with the wall over its first
// third; then how GTA V gets him up (stand, walk on, run on) or, over something thin with a drop behind, down the
// far side. Aiming holds the gun's GTA V aim (its looking up / ahead / down poses blended by the camera), each shot
// plays its fire clip with a kick on top.
using UnityEngine;

public partial class Game
{
    // ------------------------------------------------------------------ climbing
    bool climbing;
    GtaAnim.Clip clA, clB, clAir;
    float clW, clT, clKz, clShift, clBase, clTurn, clTop, clAirT, clAirBase, clAirY;
    Vector3 clOrigin, clAirPos;
    bool clVault, clRun, clLeft, clLow;
    int clFall;

    // Grand Theft Gmod's numbers (its sh_data D.climb), in metres
    const float kClimbMin = 0.48f, kClimbMax = 3.0f, kClimbLow = 0.8f, kRunDistance = 0.85f, kLowRunSpeed = 2.54f;
    const float kVaultDepth = 1.02f, kAlign = 0.3f, kClimbFade = 0.15f;
    static readonly float[] kProbes = { 0.5f, 0.91f, 1.37f };
    // the ledge heights (m) each pair of clips is for: blended between them
    static readonly (float lo, float hi, string a, string b)[] kBands =
    {
        (0.8f, 1.4f, "80", "140_low"), (1.4f, 1.8f, "140_high", "180_low"), (1.8f, 2.2f, "180_high", "220"), (2.2f, 2.95f, "220_high", "295_low"),
    };

    // how far from the wall (m) each clip starts: the hands land on the edge from there
    static float Reach(GtaAnim.Clip c)
    {
        if (c == null) return 0.46f;
        foreach (var kv in GtaAnim.VClimb)
            if (kv.Value == c) return ReachOf(kv.Key);
        return 0.46f;
    }

    static float ReachOf(string n)
    {
        switch (n)
        {
            case "standclimbup_80": return 17.9f * 0.0254f;
            case "standclimbup_140_low": return 17.5f * 0.0254f;
            case "standclimbup_140_high": return 18.4f * 0.0254f;
            case "standclimbup_180_low": return 18.3f * 0.0254f;
            case "standclimbup_180_high": return 17.7f * 0.0254f;
            case "standclimbup_220": return 17.3f * 0.0254f;
            case "standclimbup_220_high": return 26.9f * 0.0254f;
            case "standclimbup_295_low": return 27.4f * 0.0254f;
            case "runclimbup_80": return 44.0f * 0.0254f;
            case "runclimbup_140_low": return 43.6f * 0.0254f;
            case "runclimbup_140_high": return 43.3f * 0.0254f;
            case "runclimbup_180_low": return 43.4f * 0.0254f;
            case "runclimbup_180_high": return 39.3f * 0.0254f;
            case "runclimbup_220": return 39.4f * 0.0254f;
            case "runclimbup_220_high": return 43.9f * 0.0254f;
            case "runclimbup_295_low": return 44.1f * 0.0254f;
            case "standclimbup_20": return 20.9f * 0.0254f;
            case "standclimbup_80_low": return 21.4f * 0.0254f;
            case "standclimbupclose_20": case "standclimbupclose_80_low": return 4.6f * 0.0254f;
            case "standvault_20": return 17.9f * 0.0254f;
            case "standvault_80": return 18.6f * 0.0254f;
            case "standvaultclose_20": case "standvaultclose_80": return 1.6f * 0.0254f;
        }
        if (n.StartsWith("run_climbup_")) return (n.EndsWith("_close") ? 33.7f : 56.8f) * 0.0254f;
        return 18f * 0.0254f;
    }

    static float Remap(float x, float a, float b) => Mathf.Clamp01((x - a) / (b - a));
    static float Smooth(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }
    static float RiseEnd(GtaAnim.Clip c) => c?.Rise != null && c.Rise.Length > 0 ? c.Rise[c.Rise.Length - 1] : 0f;

    void ClimbStop()
    {
        climbing = false;
        clAir = null;
    }

    // Of pairs (lower, higher) of clips, the one whose reach (blended by `w`) is closest to how far he is (`d`).
    static bool BestReach(string[][] sets, float w, float d, out GtaAnim.Clip a, out GtaAnim.Clip b, out float bw)
    {
        a = b = null;
        bw = 0f;
        float best = float.MaxValue;
        foreach (var set in sets)
        {
            var ca = GtaAnim.Climb(set[0]);
            var cb = GtaAnim.Climb(set[1]);
            if (ca == null && cb == null) continue;
            float ra = ReachOf(ca != null ? set[0] : set[1]), rb = ReachOf(cb != null ? set[1] : set[0]);
            float miss = Mathf.Abs(Mathf.Lerp(ra, rb, w) - d);
            if (miss < best)
            {
                best = miss;
                a = ca ?? cb;
                b = ca != null && cb != null ? cb : null;
                bw = ca != null && cb != null ? w : 0f;
            }
        }
        return a != null;
    }

    // Grand Theft Gmod's pick_climb: the clip(s) for a ledge `h` up, `d` away, at `speed`.
    static bool PickClimb(float h, float d, float speed, bool vault, bool left, out GtaAnim.Clip a, out GtaAnim.Clip b, out float w, out bool run)
    {
        a = b = null;
        w = 0f;
        run = false;
        if (h <= kClimbLow)
        {
            float lw = Remap(h, 0.2f, 0.8f);
            if (speed > kLowRunSpeed)
            {
                string side = left ? "_l" : "_r";
                run = true;
                return BestReach(new[] { new[] { "run_climbup_20" + side, "run_climbup_80" + side }, new[] { "run_climbup_20" + side + "_close", "run_climbup_80" + side + "_close" } }, lw, d, out a, out b, out w);
            }
            return BestReach(vault
                ? new[] { new[] { "standvault_20", "standvault_80" }, new[] { "standvaultclose_20", "standvaultclose_80" } }
                : new[] { new[] { "standclimbup_20", "standclimbup_80_low" }, new[] { "standclimbupclose_20", "standclimbupclose_80_low" } }, lw, d, out a, out b, out w);
        }
        run = d > kRunDistance && speed > 1.52f;
        string prefix = run ? "runclimbup_" : "standclimbup_";
        for (int i = 0; i < kBands.Length; i++)
        {
            var band = kBands[i];
            if (h > band.hi && i < kBands.Length - 1) continue;
            a = GtaAnim.Climb(prefix + band.a);
            b = GtaAnim.Climb(prefix + band.b);
            if (a == null && run) return PickClimb(h, d, 0f, vault, left, out a, out b, out w, out run);
            if (a == null) return false;
            w = b != null ? Remap(h, band.lo, band.hi) : 0f;
            return true;
        }
        return false;
    }

    // Jump pressed: a ledge in front of him he can climb? Then he's climbing it.
    bool TryClimb()
    {
        if (!GtaAnim.V || GtaAnim.VClimb.Count == 0 || hero == null) return false;
        Vector3 flat = new Vector3(heroVel.x, 0f, heroVel.z);
        float speed = flat.magnitude;
        // the way he's facing, or the way the stick pushes him (at a wall he means to climb)
        Vector3 fwd = Quaternion.Euler(0f, heroYaw, 0f) * Vector3.forward;
        if (hStick.magnitude > 0.2f) fwd = Quaternion.Euler(0f, hCamYaw, 0f) * new Vector3(hStick.x, 0f, hStick.y).normalized;
        float range = 0.25f + (speed > 1.5f ? 1.12f : 0.61f);
        // the wall: the nearest steep face at three heights
        float hitD = float.MaxValue;
        Vector3 hitP = Vector3.zero, n = Vector3.zero;
        foreach (float hz in kProbes)
            if (Physics.Raycast(heroPos + Vector3.up * hz, fwd, out var hit, range) && Mathf.Abs(hit.normal.y) < 0.3f && hit.distance < hitD)
            {
                hitD = hit.distance;
                hitP = hit.point;
                n = hit.normal;
            }
        if (hitD == float.MaxValue) return false;
        Vector3 nf = new Vector3(n.x, 0f, n.z).normalized;
        float facing = -Vector3.Dot(nf, fwd);
        if (facing < Mathf.Cos(50f * Mathf.Deg2Rad)) return false;
        Vector3 into = -nf;
        float wallYaw = Mathf.Atan2(into.x, into.z) * Mathf.Rad2Deg;
        float d = hitD * facing;
        // its top: down from above the highest climb, just past the edge
        Vector3 p = hitP + into * 0.13f;
        p.y = heroPos.y + kClimbMax + 0.05f;
        if (Physics.CheckSphere(p, 0.1f)) return false;
        if (!Physics.SphereCast(p, 0.1f, Vector3.down, out var top, kClimbMax + 0.05f - kClimbMin + 0.05f) || top.normal.y < 0.7f) return false;
        float topY = top.point.y, h = topY - heroPos.y;
        if (h < kClimbMin || h > kClimbMax) return false;
        // how deep it is: thin with a drop behind it is vaulted
        float depth = -1f;
        for (float k = 0.2f; k <= kVaultDepth + 0.21f; k += 0.1f)
        {
            Vector3 q = hitP + into * k;
            q.y = topY + 0.2f;
            if (!Physics.Raycast(q, Vector3.down, 0.63f)) { depth = k - 0.05f; break; }
        }
        bool vault = false;
        float drop = 0f;
        if (depth > 0f)
        {
            Vector3 q = hitP + into * (depth + 0.5f);
            q.y = topY + 0.2f;
            drop = Physics.Raycast(q, Vector3.down, out var g, 30f) ? topY - g.point.y : 30f;
            vault = drop > 0.46f && depth <= kVaultDepth;
        }
        bool left = hCur != null && hCur.Cycle ? GtaAnim.At(hCur, hPhase) < 0.5f : (startLeft = !startLeft);
        if (!PickClimb(h, d, speed, vault, left, out var a, out var b, out float w, out bool run)) return false;
        float reach = Mathf.Lerp(Reach(a), Reach(b ?? a), w);
        float along = Mathf.Lerp(GtaAnim.Motion(a, 1f).x, GtaAnim.Motion(b ?? a, 1f).x, w);
        float zend = Mathf.Lerp(RiseEnd(a), RiseEnd(b ?? a), w);
        if (zend < 0.1f) return false;
        float shift = d - reach;
        Vector3 end = heroPos + into * (along + shift);
        end.y = topY + 0.01f;
        // room over his head on the way up, and for him where he ends up
        if (Physics.SphereCast(heroPos + Vector3.up * 0.5f, 0.2f, Vector3.up, out _, h + 1.35f)) return false;
        Vector3 s0 = new Vector3(heroPos.x, topY + 0.05f, heroPos.z), to = end - s0;
        to.y = 0f;
        if (to.sqrMagnitude > 1e-4f
            && Physics.CapsuleCast(s0 + Vector3.up * 0.3f, s0 + Vector3.up * 1.6f, 0.22f, to.normalized, to.magnitude)
            && Physics.CapsuleCast(s0 + Vector3.up * 0.3f, s0 + Vector3.up * 0.9f, 0.22f, to.normalized, to.magnitude)) return false;

        clA = a;
        clB = b;
        clW = w;
        clT = 0f;
        clOrigin = heroPos;
        clBase = wallYaw;
        clTurn = Mathf.DeltaAngle(wallYaw, heroYaw);
        clShift = shift;
        clKz = h / zend;
        clTop = topY;
        clVault = vault;
        clRun = run;
        clLeft = left;
        clLow = h <= kClimbLow;
        clFall = !vault ? 0 : drop <= 0.76f ? 3 : depth > 0.61f ? 2 : depth > 0.3f ? 1 : 0;
        climbing = true;
        clAir = null;
        act = null;
        heroAim = false;
        heroVel = Vector3.zero;
        heroVy = 0f;
        hNextFade = kClimbFade;
        Play(Sfx.Jump, heroPos, 0.55f);
        return true;
    }

    // On the clip: its way along (with the shift that puts his hands on the edge) and its rise scaled to the ledge.
    void ClimbUpdate(float dt)
    {
        float u0 = Mathf.Clamp01(clT / clA.Dur);
        clT += dt;
        float u = Mathf.Clamp01(clT / clA.Dur);
        Vector3 m = GtaAnim.Motion(clA, u);
        float z = GtaAnim.Rise(clA, u);
        if (clB != null && clW > 0f)
        {
            m = Vector3.Lerp(m, GtaAnim.Motion(clB, u), clW);
            z = Mathf.Lerp(z, GtaAnim.Rise(clB, u), clW);
        }
        float al = Smooth(u / kAlign);
        float x = m.x + clShift * al;
        Vector3 was = heroPos;
        heroPos = clOrigin + Quaternion.Euler(0f, clBase, 0f) * new Vector3(-m.y, 0f, x) + Vector3.up * z * clKz;
        heroYaw = clBase + clTurn * (1f - al);
        Vector3 v = (heroPos - was) / Mathf.Max(dt, 1e-3f);
        heroVel = new Vector3(v.x, 0f, v.z);
        heroVy = 0f;
        heroGround = true;
        heroAir = 0f;
        // the hands on the edge
        if (u0 < 0.45f && u >= 0.45f) Play(Sfx.Step, heroPos + Vector3.up * 0.8f, 0.5f);
        if (u >= 1f) ClimbEnd();
    }

    // Up: GTA V's way of getting to his feet (the way he's pushing, if there's room), or over and down the far side.
    void ClimbEnd()
    {
        climbing = false;
        heroYaw = clBase;
        heroPos.y = clTop;
        heroVy = 0f;
        heroAir = 0f;
        heroJumped = false;
        string side = clLeft ? "_l" : "_r";
        bool push = hStick.magnitude >= 0.12f;
        if (clVault)
        {
            string[] falls = { "clamberpose_fall", "clamberpose_fall_wide", "clamberpose_fall_xtrawide", "clamberpose_fall_low" };
            var c = clLow ? GtaAnim.Climb(clRun ? "run_vault_exit" + side : "standvault_exit") : GtaAnim.Climb(falls[clFall]) ?? GtaAnim.Climb("clamberpose_fall");
            heroGround = false;
            if (c != null)
            {
                clAir = c;
                clAirT = 0f;
                clAirBase = clBase;
                clAirPos = heroPos;
                clAirY = heroPos.y;
                hNextFade = 0.1f;
            }
            return;
        }
        GtaAnim.Clip exit;
        bool still = false;
        if (clLow)
        {
            exit = clRun ? GtaAnim.Climb("run_climbup_exit" + side) : push ? GtaAnim.Climb("standclimbup_exit_walk") : null;
            if (exit == null) { exit = GtaAnim.Climb("standclimbup_exit"); still = true; }
        }
        else
        {
            int want = push ? (heroSprint || heroTier >= 2 ? 2 : 1) : 0;
            float room = 0f;
            if (want > 0)
            {
                Vector3 o = heroPos, f = Quaternion.Euler(0f, clBase, 0f) * Vector3.forward;
                room = Physics.CapsuleCast(o + Vector3.up * 0.35f, o + Vector3.up * 1.6f, 0.22f, f, out var hit, 2.03f) ? hit.distance : 2.03f;
            }
            exit = want >= 2 && room >= 2.0f ? GtaAnim.Climb("clamberpose_run") : want >= 1 && room >= 1.0f ? GtaAnim.Climb("clamberpose_walk") : null;
            if (exit == null) { exit = GtaAnim.Climb("clamberpose_stand"); still = true; }
        }
        heroGround = true;
        heroVel = Vector3.zero;
        if (exit != null && Begin(exit, 5, 0f))
        {
            actStill = still;
            actRun = exit.Curve != null && GtaAnim.Motion(exit, 1f).x / exit.Dur > 2.5f;
            hNextFade = 0.1f;
        }
        Play(Sfx.Step, heroPos, 0.6f);
    }

    // Over the top and down the far side: the vault's end clip along (its way and its drop), then he falls as ever.
    void ClimbAir(float dt)
    {
        clAirT += dt;
        float u = Mathf.Clamp01(clAirT / clAir.Dur);
        Vector3 m = GtaAnim.Motion(clAir, u);
        Vector3 to = clAirPos + Quaternion.Euler(0f, clAirBase, 0f) * new Vector3(-m.y, 0f, m.x);
        Vector3 v = new Vector3(to.x - heroPos.x, 0f, to.z - heroPos.z) / Mathf.Max(dt, 1e-3f);
        heroVel = Vector3.ClampMagnitude(v, 9f);
        heroYaw = clAirBase;
        if (clAir.Rise != null && !heroGround)
        {
            float y = clAirY + GtaAnim.Rise(clAir, u);
            heroVy = (y - heroPos.y) / Mathf.Max(dt, 1e-3f);
            heroPos.y = y;
        }
        if (u >= 1f || (heroGround && clAirT > 0.1f))
        {
            clAir = null;
            if (!heroGround) heroVy = Mathf.Min(heroVy, 0f);
        }
    }

    // The top of what he's going over isn't ground to land on (he'd stop on it).
    bool VaultedTop(float y) => clAir != null && y > clTop - 0.1f;

    // ------------------------------------------------------------------ GTA V's aim and shot
    float vRaiseT = -1f, vFireT = -1f, vKick, vKickVel;
    bool vWasAim;

    // The gun in hand's GTA V clips (by its kind), none for his fists or without them
    GtaAnim.GunSet VGun()
    {
        if (!GtaAnim.V) return null;
        var g = kGuns[weapon % kGuns.Length];
        string k = null;
        switch (g.Name)
        {
            case "pistol": k = "wpi"; break;
            case "deagle": k = "wp5"; break;
            case "smg": k = "wsm"; break;
            case "ak47": k = "war"; break;
            case "shotgun": k = "wpu"; break;
            case "sniper": k = "wsr"; break;
        }
        if (k == null) return null;
        if (GtaAnim.VGuns.TryGetValue(k, out var set)) return set;
        // the pistol's for a handgun, the rifle's for a long one
        return GtaAnim.VGuns.TryGetValue(g.Long ? "war" : "wpi", out set) ? set : null;
    }

    // The raise when he starts aiming, the shot's clip (an automatic's loops while it's held), the kick's spring.
    void AimClock(float dt)
    {
        var set = VGun();
        if (heroAim && !vWasAim) vRaiseT = 0f;
        vWasAim = heroAim;
        if (!heroAim) { vRaiseT = -1f; vFireT = -1f; }
        if (vRaiseT >= 0f) vRaiseT += dt;
        if (vFireT >= 0f && set != null && set.Fire[1].Ok)
        {
            vFireT += dt;
            float d = set.Fire[1].Dur;
            if (vFireT >= d)
            {
                var g = kGuns[weapon % kGuns.Length];
                bool held = hFireId != kNone || Input.GetKey(KeyCode.F);
                vFireT = g.Auto && held ? vFireT - d : -1f;
            }
        }
        // the kick: thrown up at once, back on a spring
        vKick = Mathf.SmoothDamp(vKick, 0f, ref vKickVel, 0.07f, 400f, dt);
    }

    // A shot: the fire clip from its start (an automatic's carries on), and the kick (degrees) by the gun.
    void Shot(Gun gun)
    {
        if (VGun() == null) return;
        if (vFireT < 0f || !gun.Auto) vFireT = 0f;
        vRaiseT = -1f;
        float kick = 5f;
        switch (gun.Name)
        {
            case "pistol": kick = 7f; break;
            case "deagle": kick = 13f; break;
            case "smg": kick = 2.5f; break;
            case "ak47": kick = 3.5f; break;
            case "shotgun": kick = 14f; break;
            case "sniper": kick = 15f; break;
        }
        vKick = Mathf.Min(vKick + kick, kick * 2f);
        vKickVel = 0f;
    }

    // His upper body on the gun's GTA V clip (its pitch blended to where the camera looks), the kick on top. False
    // without GTA V's guns (the GTA IV aim then).
    bool AimPoseV(Mannequin body)
    {
        var set = VGun();
        if (set == null) return false;
        float up = -hCamPitch;
        int k2 = up >= 0f ? 0 : 2;
        float wv = up >= 0f ? Mathf.Clamp01(up / 47f) : Mathf.Clamp01(-up / 66f);
        GtaAnim.Clip[] row = set.Aim;
        float ph = 0f;
        if (vFireT >= 0f && set.Fire[1].Ok) { row = set.Fire; ph = vFireT / set.Fire[1].Dur; }
        else if (vRaiseT >= 0f && set.Raise[1].Ok && vRaiseT < set.Raise[1].Dur) { row = set.Raise; ph = vRaiseT / set.Raise[1].Dur; }
        if (!GtaAnim.Pose(row[1], ph, fB)) return false;
        if (wv > 0f && row[k2] != null && row[k2].Ok && GtaAnim.Pose(row[k2], ph, fC)) GtaAnim.Blend(fB, fC, wv);
        foreach (int i in upperBones)
            if (i >= 0) body.Bones[i].rotation = Quaternion.Slerp(body.Bones[i].rotation, body.World(fB, i, heroYaw), hUpper);
        if (vKick > 0.01f)
        {
            // up about his right: the chest a little, the arms the most
            var axis = -body.transform.right;
            var chest = Quaternion.AngleAxis(vKick * 0.25f, axis);
            foreach (int i in bendBones)
                if (i >= 0) body.Bones[i].rotation = chest * body.Bones[i].rotation;
            var arm = Quaternion.AngleAxis(vKick * 0.6f, axis);
            foreach (var n in new[] { "RightArm", "LeftArm" })
            {
                var t = body.Bone(n);
                if (t != null) t.rotation = arm * t.rotation;
            }
        }
        return true;
    }
}
