// Hostile men ("+ Hostile"): on GTA V's clips like the hero, after you. With fists they come at you and fight GTA V's
// way (the same blows as yours: HeroMelee's choice, homing and strike points); with a gun they keep their distance,
// aim and shoot (their bullets are real: Delirium's reaction, blood, your health). Hit them (a blow, a bullet, a
// hand in VR) and Delirium has them, a ragdoll; they get up (GTA IV's get-up) and come on again. Their blows and
// bullets put you on Delirium too, unless you're in VR in your own eyes (then they don't land: only when you're
// drunk or down).
using System.Collections.Generic;
using UnityEngine;

public partial class Game
{
    public class Bot
    {
        public Man M;
        public int Gun;              // kGuns'
        public int Mode;             // 0 on his clips, 1 Delirium's (hit)
        public Vector3 Pos, Vel;
        public float Yaw, VelY;
        public GtaAnim.Clip Cur, Prev;
        public float Phase, PrevPhase, Fade;
        // a blow
        public GtaAnim.MeleeMove Mel;
        public float MelT, MelYaw, StrikeFrom, StrikeTo, Reach, MelEnd;
        public bool MelHit, Chain;
        public float NextAct, NextShot, Knocked;
        public int Burst;
        public GameObject GunGo;
        public Vector3 Muzzle;
        // from how he lay (or stood as a ragdoll) into his clips
        public bool Hand;
        public float HandT;
        public Vector3 HandPos;
        public Quaternion HandRot;
        public Quaternion[] HandLocal;
    }

    readonly List<Bot> bots = new List<Bot>();
    int hostileWeapon;
    const string kPrefHostileGun = "delirium.hostileGun";
    GtaAnim.Frame[] bA, bB, bC;

    void AddHostile()
    {
        if (men.Count >= kMaxMen) return;
        if (GtaAnim.State != 2) { note = "The hostile men need your game's clips (they load with the hero)."; return; }
        int before = men.Count;
        AddMan();
        if (men.Count == before) return;
        var m = men[men.Count - 1];
        m.Body.name = "Hostile" + (bots.Count + 1);
        hostileWeapon = PlayerPrefs.GetInt(kPrefHostileGun, hostileWeapon);
        var b = new Bot { M = m, Pos = m.Home, Yaw = m.Yaw, NextAct = Time.time + 1f };
        m.BotOf = b;
        bots.Add(b);
        BotArm(b, hostileWeapon);
    }

    // his weapon: a gun's boxes (in the world: put in his hand every frame), or none
    void BotArm(Bot b, int gun)
    {
        b.Gun = gun % kGuns.Length;
        if (b.GunGo != null) Destroy(b.GunGo);
        b.GunGo = null;
        if (kGuns[b.Gun].Fists) return;
        var keep = gunModel;
        gunModel = new GameObject("Hostile's " + kGuns[b.Gun].Label);
        float len = GunParts(kGuns[b.Gun].Name);
        b.GunGo = gunModel;
        gunModel = keep;
        b.Muzzle = new Vector3(len, 0.035f, 0f);
    }

    void BotGone(Bot b)
    {
        if (b.GunGo != null) Destroy(b.GunGo);
        bots.Remove(b);
    }

    void BotReset(Bot b)
    {
        b.Mode = 0;
        b.Pos = b.M.Home;
        b.Yaw = b.M.Yaw;
        b.Vel = Vector3.zero;
        b.VelY = 0f;
        b.Mel = null;
        b.Cur = b.Prev = null;
        b.Hand = false;
        b.NextAct = Time.time + 1f;
    }

    // up again (his get-up's end, or steady on his feet): his clips from where he is, his pose faded over
    void BotTakeBack(Bot b)
    {
        var m = b.M;
        var body = m.Body;
        b.HandPos = body.Hips.position;
        b.HandRot = body.Hips.rotation;
        if (b.HandLocal == null || b.HandLocal.Length != body.Bones.Length) b.HandLocal = new Quaternion[body.Bones.Length];
        for (int i = 0; i < body.Bones.Length; i++) b.HandLocal[i] = body.Bones[i].localRotation;
        b.Pos = Feet(m);
        b.Yaw = Yaw(m);
        if (nm.State(body.transform).Ragdolled) nm.GetUp(body.transform, 0.001f);
        b.Mode = 0;
        b.Hand = true;
        b.HandT = 0f;
        b.Vel = Vector3.zero;
        b.VelY = 0f;
        b.Mel = null;
        b.Knocked = 0f;
        b.Cur = b.Prev = null;
        b.NextAct = Time.time + 0.6f;
        m.Down = 0f;
    }

    // ------------------------------------------------------------------ each frame: their minds
    void BotsUpdate(float dt)
    {
        for (int i = bots.Count - 1; i >= 0; i--)
        {
            var b = bots[i];
            if (b.M?.Body == null || !men.Contains(b.M)) { if (b.GunGo != null) Destroy(b.GunGo); bots.RemoveAt(i); continue; }
            var m = b.M;
            if (m.Killed || m.Rising || m.Broken) continue;
            if (b.Mode == 1)
            {
                // knocked but on his feet: his clips again once he's steady
                var st = nm.State(m.Body.transform);
                if (st.Ragdolled && !st.Fallen && st.Balancing && !st.Dead && m.FireLeft <= 0f)
                {
                    b.Knocked += dt;
                    if (b.Knocked > 1.8f) BotTakeBack(b);
                }
                else b.Knocked = 0f;
                continue;
            }
            BotThink(b, dt);
        }
    }

    // the man he's after: the hero, on his feet or down (not dead)
    Man BotQuarry() => heroOn && hero != null && hero.Body != null && !hero.Killed ? hero : null;

    Vector3 Where(Man t) => t.Hero && heroMode == 0 ? heroPos : (nm.State(t.Body.transform).Ragdolled ? t.Body.Hips.position : t.Body.transform.position);

    void BotThink(Bot b, float dt)
    {
        var t = BotQuarry();
        var gun = kGuns[b.Gun];
        Vector3 want = Vector3.zero;
        float faceYaw = b.Yaw;
        float walk = GtaAnim.Move[1].Ok ? GtaAnim.Move[1].Speed : 1.5f, run = GtaAnim.Move[2].Ok ? GtaAnim.Move[2].Speed : 3.6f;
        if (b.Mel != null) { BotMelee(b, t, dt); BotMove(b, dt); return; }
        if (t != null)
        {
            Vector3 to = Where(t) - b.Pos;
            to.y = 0f;
            float d = to.magnitude;
            if (d > 0.01f) faceYaw = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg;
            Vector3 dir = d > 0.01f ? to / d : Vector3.zero;
            if (gun.Fists)
            {
                // closing in (running from afar), then a blow when he's in reach and ready
                float reachIn = 1.6f;
                if (d > reachIn) want = dir * (d > 5f ? run : walk);
                if (d < 2.4f && Time.time >= b.NextAct && GtaAnim.Melee && BotStartMelee(b, t, null)) { BotMove(b, dt); return; }
            }
            else
            {
                // a gun: in 5-13 m, with a clear line, shooting in bursts
                if (d > 13f) want = dir * walk;
                else if (d < 4.5f) want = -dir * walk * 0.8f;
                if (d < 30f && Time.time >= b.NextShot && Mathf.Abs(Mathf.DeltaAngle(b.Yaw, faceYaw)) < 20f) BotShoot(b, t);
            }
        }
        b.Vel = Vector3.MoveTowards(b.Vel, want, 8f * dt);
        b.Yaw = Mathf.MoveTowardsAngle(b.Yaw, faceYaw, 360f * dt);
        BotMove(b, dt);
        // his clip: a gun held up (GTA V's aim idle) standing, else his walk or run by his speed (a gun: backing off
        // is the same walk, he faces you)
        float sp = new Vector3(b.Vel.x, 0f, b.Vel.z).magnitude;
        GtaAnim.Clip c;
        if (sp < 0.3f) c = !gun.Fists && GtaAnim.StrafeIdle != null && GtaAnim.StrafeIdle.Ok ? GtaAnim.StrafeIdle : GtaAnim.Move[0];
        else
        {
            float a = -Vector3.SignedAngle(Quaternion.Euler(0f, b.Yaw, 0f) * Vector3.forward, b.Vel, Vector3.up);
            int sector = (Mathf.RoundToInt(a / 45f) + 8) % 8;
            c = sector != 0 && GtaAnim.Strafe[0, sector] != null && GtaAnim.Strafe[0, sector].Ok ? GtaAnim.Strafe[0, sector] : sp > (walk + run) * 0.5f ? GtaAnim.Move[2] : GtaAnim.Move[1];
        }
        BotPlay(b, c, c != null && c.Speed > 0.1f && sp > 0.3f ? sp / c.Speed : 1f, dt);
    }

    // a clip on its loop (a new one faded in over a quarter second)
    void BotPlay(Bot b, GtaAnim.Clip c, float rate, float dt)
    {
        if (c == null || !c.Ok) return;
        if (c != b.Cur)
        {
            b.Prev = b.Cur;
            b.PrevPhase = b.Phase;
            b.Fade = b.Prev != null ? 1f : 0f;
            b.Cur = c;
            b.Phase = 0f;
        }
        b.Phase += dt * rate / Mathf.Max(0.05f, c.Dur);
        if (b.Phase > 1f) b.Phase -= Mathf.Floor(b.Phase);
        if (b.Prev != null) b.PrevPhase = (b.PrevPhase + dt / Mathf.Max(0.05f, b.Prev.Dur)) % 1f;
        b.Fade = Mathf.Max(0f, b.Fade - dt / 0.25f);
    }

    // moved by his speed: on the ground under him (a step up or down), not through walls; falling off an edge
    void BotMove(Bot b, float dt)
    {
        Vector3 step = new Vector3(b.Vel.x, 0f, b.Vel.z) * dt;
        if (step.sqrMagnitude > 1e-8f)
        {
            Vector3 eye = b.Pos + Vector3.up * 0.9f;
            foreach (var h in Physics.SphereCastAll(eye, 0.25f, step.normalized, step.magnitude + 0.15f, VrWorldMask(), QueryTriggerInteraction.Ignore))
            {
                if (h.collider.attachedRigidbody != null || h.distance <= 0f) continue;
                // (along the wall)
                step = Vector3.ProjectOnPlane(step, new Vector3(h.normal.x, 0f, h.normal.z).normalized);
                break;
            }
            b.Pos += step;
        }
        if (Physics.Raycast(b.Pos + Vector3.up * 0.8f, Vector3.down, out var g, 1.6f, VrWorldMask(), QueryTriggerInteraction.Ignore) && g.point.y > b.Pos.y - 0.45f - Mathf.Max(0f, -b.VelY) * dt)
        {
            b.Pos.y = g.point.y;
            b.VelY = 0f;
        }
        else
        {
            b.VelY -= 9.81f * dt;
            b.Pos.y += b.VelY * dt;
            // a long fall: Delirium's
            if (b.VelY < -7f) { var owner = b.M.Body.transform; b.M.Body.transform.position = b.Pos; if (nm.Ragdoll(owner, Vector3.zero)) { Outfit(b.M); b.Mode = 1; } }
        }
    }

    // ------------------------------------------------------------------ his blows (HeroMelee's, aimed at the hero)
    bool BotStartMelee(Bot b, Man t, string[] chain)
    {
        Vector3 to = Where(t) - b.Pos;
        to.y = 0f;
        float d = to.magnitude;
        Vector3 fwd = Quaternion.Euler(0f, b.Yaw, 0f) * Vector3.forward;
        float a = -Vector3.SignedAngle(fwd, to, Vector3.up);
        Vector3 his = t.Body.transform.forward;
        float facing = Vector3.Angle(new Vector3(his.x, 0f, his.z), -to);
        bool down = t.Hero ? heroMode != 0 && nm.State(t.Body.transform).Fallen : nm.State(t.Body.transform).Fallen;
        var list = new List<GtaAnim.MeleeMove>();
        if (down) { foreach (var mv in GtaAnim.MeleeGround) if (mv.Ground == "low" && Fits(mv, d, a, facing, false)) list.Add(mv); }
        else if (chain != null) { foreach (var id in chain) { var mv = GtaAnim.MeleeById(id); if (mv != null && Fits(mv, d, a, facing, false) && mv.Ko == null) list.Add(mv); } }
        else foreach (var mv in GtaAnim.MeleeTargeted)
            {
                if (mv.Heavy || mv.Roll != null) continue; // (no takedowns on you)
                if (mv.Ko != null && t.Health > 60f) continue;
                if (Fits(mv, d, a, facing, false)) list.Add(mv);
            }
        var pick = Choose(list);
        if (pick == null) return false;
        b.Mel = pick;
        b.MelT = 0f;
        b.MelYaw = b.Yaw;
        b.MelHit = false;
        b.Chain = Random.value < 0.55f; // he follows it with the next in its chain, often
        float mk = GtaAnim.Mark(pick.C, 3);
        b.MelEnd = mk > 0.2f ? mk : 1f;
        var s = pick.C.Strike;
        float last = Mathf.Max(1f, pick.C.Frames - 1);
        b.StrikeFrom = s != null ? s[0] / last : 0.3f;
        b.StrikeTo = s != null ? (s[0] + s[1] - 1f) / last : 0.45f;
        b.Reach = MeleeReach(pick);
        return true;
    }

    void BotMelee(Bot b, Man t, float dt)
    {
        var c = b.Mel.C;
        float ph0 = b.MelT / c.Dur;
        b.MelT += dt;
        float ph = b.MelT / c.Dur;
        if (ph > b.StrikeTo + 0.05f && b.Chain && t != null && b.Mel.Branch != null && GtaAnim.MeleeBranches.TryGetValue(b.Mel.Branch, out var ids))
        {
            var was = b.Mel;
            b.Mel = null;
            if (BotStartMelee(b, t, ids)) { if (b.Mel == was) b.Chain = false; return; }
        }
        if (ph >= b.MelEnd || b.Mel == null)
        {
            b.Mel = null;
            b.NextAct = Time.time + Random.Range(0.5f, 1.4f);
            return;
        }
        var mo = GtaAnim.Motion(c, ph);
        var m0 = GtaAnim.Motion(c, ph0);
        Vector3 step = Quaternion.Euler(0f, b.MelYaw, 0f) * new Vector3(-(mo.y - m0.y), 0f, mo.x - m0.x);
        if (t != null && ph < b.StrikeFrom)
        {
            Vector3 to = Where(t) - b.Pos;
            to.y = 0f;
            float dist = to.magnitude;
            if (dist > 0.05f)
            {
                float wantYaw = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg;
                b.MelYaw = Mathf.MoveTowardsAngle(b.MelYaw, wantYaw + GtaAnim.Motion(c, b.StrikeFrom).z, 400f * dt);
                float ownFwd = (GtaAnim.Motion(c, b.StrikeFrom) - mo).x;
                float extra = dist - ownFwd - (b.Reach + 0.08f);
                float left = Mathf.Max(dt, (b.StrikeFrom - ph) * c.Dur);
                float move = Mathf.Clamp(extra * Mathf.Clamp01(dt / left), -2.5f * dt, 4f * dt);
                step += to / dist * move;
            }
        }
        b.Vel = dt > 1e-4f ? Vector3.ClampMagnitude(step / dt, 8f) : Vector3.zero;
        b.Yaw = b.MelYaw - mo.z;
        b.Cur = c;
        b.Phase = Mathf.Clamp01(ph);
        // his strike: its points through its window, against your body
        if (t != null && !b.MelHit && ph >= b.StrikeFrom - 0.01f && ph <= b.StrikeTo + 0.02f) BotStrike(b, t, ph0, ph);
    }

    void BotStrike(Bot b, Man t, float ph0, float ph)
    {
        var c = b.Mel.C;
        var s = c.Strike;
        if (s == null) return;
        int f0 = (int)s[0], n = (int)s[1];
        float last = Mathf.Max(1f, c.Frames - 1);
        int a = Mathf.Clamp(Mathf.FloorToInt(ph0 * last) - f0, 0, n - 1), z = Mathf.Clamp(Mathf.CeilToInt(ph * last) - f0, 0, n - 1);
        (int point, float r)[] set;
        if (b.Mel.Sb == null || !GtaAnim.MeleeStrikes.TryGetValue(b.Mel.Sb, out set)) set = new[] { (1, 0.12f), (4, 0.12f) };
        var rot = Quaternion.Euler(0f, b.Yaw, 0f);
        Vector3 fwd = rot * Vector3.forward, left = rot * Vector3.left;
        for (int f = a; f <= z && !b.MelHit; f++)
        {
            int at = 2 + f * 30;
            if (at + 30 > s.Length) break;
            foreach (var pr in set)
            {
                if (pr.point < 1 || pr.point > 10) continue;
                int k = at + (pr.point - 1) * 3;
                Vector3 w = b.Pos + fwd * s[k] + left * s[k + 1] + Vector3.up * s[k + 2];
                if (!MeleeTouches(t, w, pr.r + 0.08f, out Vector3 on)) continue;
                b.MelHit = true;
                BotHits(b, t, on, fwd);
                break;
            }
        }
    }

    // you in VR in your own eyes: their blows and bullets don't land (drunk, or down: they do)
    bool HeroUntouchable(Man t) => t.Hero && vrOn && !vrThird && heroMode == 0;

    void BotHits(Bot b, Man t, Vector3 at, Vector3 fwd)
    {
        Play(Sfx.Punch, at, 0.8f);
        if (HeroUntouchable(t)) return;
        var mv = b.Mel;
        bool big = mv.Ko != null;
        float force = big ? 85f : 30f + mv.Dmg * 0.8f;
        Vector3 dir = (fwd + Vector3.up * (mv.Ground != null ? -0.2f : 0.15f)).normalized;
        var head = b.M.Body.Bone("Head");
        nm.MeleeHit(t.Body.transform, at, dir * force, head != null ? head.position : b.Pos + Vector3.up * 1.6f);
        if (Random.value < (big ? 0.7f : 0.3f)) FxBlood(at, fwd, big ? 0.5f : 0.3f);
        t.HurtAt = Time.time;
        if (!Immortal(t))
        {
            t.Health = Mathf.Max(0f, t.Health - mv.Dmg * 1.5f);
            if (t.Health <= 0f && !t.Killed) { nm.Kill(t.Body.transform); t.Killed = true; }
        }
    }

    // ------------------------------------------------------------------ his gun
    void BotShoot(Bot b, Man t)
    {
        var gun = kGuns[b.Gun];
        if (b.GunGo == null) return;
        // bursts for the automatics, single shots for the rest
        if (gun.Auto)
        {
            if (b.Burst <= 0) b.Burst = Random.Range(3, 7);
            b.Burst--;
            b.NextShot = Time.time + (b.Burst > 0 ? gun.Between * 1.3f : Random.Range(0.9f, 1.8f));
        }
        else b.NextShot = Time.time + Mathf.Max(gun.Between, 0.25f) * 1.5f + Random.Range(0.5f, 1.2f);
        Vector3 muzzle = b.GunGo.transform.TransformPoint(b.Muzzle);
        var chest = t.Body.Bone("Spine2") ?? t.Body.Hips;
        Vector3 aim = (chest.position - muzzle).normalized;
        float spread = gun.Long && !gun.Auto ? 0.8f : gun.Auto ? 3.5f : 2.5f;
        aim = Quaternion.AngleAxis(Random.Range(0f, 360f), aim) * Quaternion.AngleAxis(Random.Range(0f, spread), Vector3.Cross(aim, Vector3.up).normalized + Vector3.up * 1e-3f) * aim;
        var ray = new Ray(muzzle, aim);
        FxMuzzle(muzzle, b.GunGo.transform.right, b.GunGo.transform.TransformDirection(new Vector3(0f, 0.5f, -1f)), gun);
        PlayGunOf(gun, muzzle);
        // you, if the line meets you before the map does
        Man struck = null;
        if (FxOnBody(ray, t, out Vector3 at, out _, out _, out _, out _))
        {
            float far = (at - muzzle).magnitude;
            if (!BlockedByWorld(muzzle, aim, far)) struck = t;
        }
        FxBullet(ray, struck, gun);
        if (struck == null || HeroUntouchable(struck)) return;
        ShootWith(struck, ray, gun);
        struck.HurtAt = Time.time;
    }

    // the map (not a body, not a hand) between
    static bool BlockedByWorld(Vector3 from, Vector3 dir, float far)
    {
        foreach (var h in Physics.RaycastAll(from, dir, far - 0.05f, VrWorldMask(), QueryTriggerInteraction.Ignore))
            if (h.collider.attachedRigidbody == null) return true;
        return false;
    }

    // ------------------------------------------------------------------ their poses (LateUpdate)
    void BotsPose()
    {
        if (bots.Count == 0) return;
        int n = Mannequin.BoneCount;
        if (bA == null) { bA = new GtaAnim.Frame[n]; bB = new GtaAnim.Frame[n]; bC = new GtaAnim.Frame[n]; }
        var t = BotQuarry();
        foreach (var b in bots)
        {
            var m = b.M;
            if (b.Mode != 0 || m.Body == null || m.Rising || m.Killed || m.Broken || b.Cur == null) { if (b.GunGo != null && (m.Killed || b.Mode != 0)) BotGunCarry(b, null); continue; }
            var body = m.Body;
            body.transform.SetPositionAndRotation(b.Pos, Quaternion.Euler(0f, b.Yaw, 0f));
            float ph = b.Mel != null ? b.Phase : GtaAnim.At(b.Cur, b.Phase);
            if (!GtaAnim.Pose(b.Cur, ph, bA)) continue;
            if (b.Fade > 0f && b.Prev != null && GtaAnim.Pose(b.Prev, GtaAnim.At(b.Prev, b.PrevPhase), bC)) GtaAnim.Blend(bA, bC, b.Fade);
            body.Apply(bA, b.Pos, b.Yaw);
            if (b.Hand)
            {
                // how he lay or stood as a ragdoll, faded into the clip
                b.HandT += Time.deltaTime;
                float f = Mathf.SmoothStep(0f, 1f, b.HandT / 0.35f);
                if (f >= 1f) b.Hand = false;
                else
                {
                    var locals = new Quaternion[body.Bones.Length];
                    for (int i = 1; i < locals.Length; i++) locals[i] = body.Bones[i].localRotation;
                    body.Hips.SetPositionAndRotation(Vector3.Lerp(b.HandPos, body.Hips.position, f), Quaternion.Slerp(b.HandRot, body.Hips.rotation, f));
                    for (int i = 1; i < locals.Length; i++) body.Bones[i].localRotation = Quaternion.Slerp(b.HandLocal[i], locals[i], f);
                }
            }
            BotGunCarry(b, b.Mel == null ? t : null);
        }
    }

    // his gun in his right hand: at you while he's on his feet and after you, along his forearm otherwise
    void BotGunCarry(Bot b, Man at)
    {
        if (b.GunGo == null) return;
        var body = b.M.Body;
        Transform hand = body.Bone("RightHand"), fore = body.Bone("RightForeArm");
        if (hand == null || fore == null) return;
        Vector3 dir = (hand.position - fore.position).normalized;
        if (at != null && at.Body != null)
        {
            var chest = at.Body.Bone("Spine2") ?? at.Body.Hips;
            dir = (chest.position - hand.position).normalized;
        }
        Vector3 up = Mathf.Abs(Vector3.Dot(dir, Vector3.up)) > 0.95f ? body.transform.forward : Vector3.up;
        var turn = Quaternion.LookRotation(dir, up) * Quaternion.Euler(0f, -90f, 0f);
        b.GunGo.transform.SetPositionAndRotation(hand.position + dir * 0.06f - turn * Vector3.up * 0.01f, turn);
    }
}
