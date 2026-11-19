// Hero: a mannequin you play in third person, moving with GTA IV's own clips (GtaAnim) while he's on his feet and
// on Delirium's ragdoll when he's knocked down or "drunk" (a ragdoll you steer). Two sticks and three buttons on the
// screen (move, look; Jump, Aim, Drunk; Fire while aiming); on a PC also WASD, Space, J, the right mouse button
// (aim), a drag (look) and a click while aiming (fire).
using System.Collections.Generic;
using UnityEngine;

public partial class Game
{
    // ------------------------------------------------------------------ state
    bool heroOn;
    Man hero;
    int heroMode;                  // 0 on his clips, 1 knocked (a ragdoll till he's up again), 2 drunk
    bool heroDrunk, heroAim;
    Vector3 heroPos, heroVel;      // his feet; flat velocity (m/s)
    float heroVy, heroYaw;         // degrees
    bool heroGround = true, heroJumpStill, heroJumped;
    float heroSpotLand;
    float heroAir, heroLandUntil, heroSpotUntil, heroKnocked, heroNextFire, heroLean, heroBump;
    bool heroLeaning;
    // the clips: the legs' one (and its gait partner), cross-faded from the one before
    GtaAnim.Clip hCur, hCurB, hPrev, hPrevB;
    float hCurW, hPrevW, hPhase, hPrevPhase, hRate, hPrevRate, hFade, hUpper, hPhaseSet = -1f;
    float hFadeLen = 0.3f, hNextFade;     // this cross-fade's length; the next one's when it isn't the usual
    GtaAnim.Frame[] fA, fB, fC;
    int[] upperBones, bendBones;
    // from a ragdoll back onto the clips: the pose he was in, faded out
    bool hHand;
    float hHandT;
    Vector3 hHandPos;
    Quaternion hHandRot;
    Quaternion[] hHandLocal;
    // the camera
    float hCamYaw, hCamPitch = 12f;
    Vector3 hPivot;
    // the screen's controls
    Vector2 hStick;
    int hStickId = kNone, hDragId = kNone, hFireId = kNone, hSprintId = kNone;
    bool heroSprint;
    float sprintPower, heroYawVel, heroHeading, heroHeadingRate, heroSpeedRate;
    bool heroLanded;          // just down from a jump or a fall: GTA V's landing (VAct)
    Vector3 heroIntent, heroVelSlid; // his velocity before a wall slid it, and after (the heading is the first's)
    float heroTilt, heroTiltVel, heroYawLast, heroDrawY, heroDrawYV;
    float heroFallFrom;       // the top of his fall (Knock's high fall from 4.5 m down)
    float heroLandAir;
    int heroTier = 1;              // 1 walk, 2 run, 3 sprint: each its own clip
    GameObject gunModel;
    int gunShown = -1;
    Vector3 gunMuzzle;             // in the gun's own space
    Vector2 hDragLast;
    bool hJumpReq;
    Texture2D disc;
    Material matHero;
    const int kNone = int.MinValue, kMouse = -77;
    const float kStickR = 72f, kBtnR = 40f;

    void Frames()
    {
        if (fA != null) return;
        int n = Mannequin.BoneCount;
        fA = new GtaAnim.Frame[n];
        fB = new GtaAnim.Frame[n];
        fC = new GtaAnim.Frame[n];
    }

    // ------------------------------------------------------------------ in and out of the mode
    void AddHero()
    {
        if (heroOn) return;
        if (GtaAnim.State != 2)
        {
            note = GtaAnim.State == 1 ? "Hero: GTA IV's clips are still loading, try in a moment." : "Hero: " + (GtaAnim.Error != "" ? GtaAnim.Error : "GTA IV's clips aren't loaded");
            return;
        }
        note = "";
        Drop();
        Frames();
        if (matHero == null) matHero = Mat(new Color(0.25f, 0.55f, 0.95f));
        // a few steps from the man you control, on the camera's side of him
        Vector3 at = new Vector3(0f, 0f, -3f);
        if (Sel != null)
        {
            Vector3 side = Vector3.ProjectOnPlane(cam.transform.position - Sel.Body.Hips.position, Vector3.up);
            at = Feet(Sel) + (side.magnitude > 0.1f ? side.normalized : Vector3.back) * 3f;
        }
        if (Physics.Raycast(at + Vector3.up * 3f, Vector3.down, out var hit, 30f)) at.y = hit.point.y;
        float yaw = camYaw;
        hero = new Man { Home = at, Yaw = yaw, Hero = true };
        hero.Body = MakeBody("Hero", at, yaw, true);
        men.Add(hero);
        HeroBones();
        heroOn = true;
        hCamYaw = camYaw;
        hCamPitch = 12f;
        hPivot = at + Vector3.up * 1.4f;
        menuOpen = false;
        heroRb = null;
        HeroPlace(at, yaw);
    }

    // Which of his bones the aim and the punch move (their places in his body's Bones).
    void HeroBones()
    {
        var b = hero.Body;
        upperBones = new[] { b.Index("Spine1"), b.Index("Spine2"), b.Index("Spine3"), b.Index("Neck"), b.Index("Head"),
            b.Index("LeftShoulder"), b.Index("LeftArm"), b.Index("LeftForeArm"), b.Index("LeftHand"),
            b.Index("RightShoulder"), b.Index("RightArm"), b.Index("RightForeArm"), b.Index("RightHand") };
        // bent once each (a model's Spine3 is its Spine2)
        bendBones = b.Bone("Spine3") == b.Bone("Spine2") ? new[] { b.Index("Spine1"), b.Index("Spine2") } : new[] { b.Index("Spine1"), b.Index("Spine2"), b.Index("Spine3") };
    }

    void RemoveHero()
    {
        if (!heroOn) return;
        BikeRemove();
        if (heroCC != null)
        {
            Delirium.Ragdoll.DeliriumRagdolls.SandboxNotLevel.Remove(heroCC.transform);
            Destroy(heroCC.gameObject);
            heroCC = null;
        }
        if (gunModel != null) Destroy(gunModel);
        gunModel = null;
        gunShown = -1;
        if (hero != null)
        {
            if (nm.State(hero.Body.transform).Ragdolled) nm.GetUp(hero.Body.transform, 0.001f);
            nm.ClearOverrides(hero.Body.transform);
            Destroy(hero.Body.gameObject);
            men.Remove(hero);
        }
        hero = null;
        heroOn = heroDrunk = heroAim = false;
        if (camFovBase > 0f) cam.fieldOfView = camFovBase;
        camFovBase = 0f;
        camOut = -1f;
        sel = 0;
        if (men.Count == 0) AddMan();
        camYaw = hCamYaw;
    }

    // On his feet at `at`, on his clips, sober (after a reset).
    void HeroPlace(Vector3 at, float yaw)
    {
        if (riding) { riding = false; Unseat(); }
        heroMode = 0;
        act = null;
        ClimbStop();
        heroDrunk = heroLeaning = heroDrunkPending = false;
        heroPos = at;
        heroYaw = yaw;
        heroVel = Vector3.zero;
        heroVy = 0f;
        heroGround = true;
        hCur = hCurB = hPrev = hPrevB = null;
        hFade = 0f;
        hHand = false;
        if (hero != null) HeroOverrides(false);
    }

    // The drunk's balance: no arms thrown out at every push (he'd walk like a scarecrow); off: the menu's own.
    void HeroOverrides(bool drunk)
    {
        var owner = hero.Body.transform;
        nm.ClearOverrides(owner);
        foreach (var e in paramEdits)
        {
            int dot = e.Key.IndexOf('.');
            if (dot > 0) nm.SetOverride(owner, e.Key.Substring(0, dot), e.Key.Substring(dot + 1), e.Value);
        }
        if (drunk)
        {
            nm.SetOverride(owner, "bodyBalance", "armsOutOnPush", 0f);
            nm.SetOverride(owner, "bodyBalance", "returningToBalanceArmsOut", 0f);
        }
    }

    bool heroDrunkPending;

    // ------------------------------------------------------------------ noclip: flying through everything
    bool heroNoclip;
    int hJumpId = kNone, hDownId = kNone;

    void ToggleNoclip()
    {
        if (hero == null || hero.Killed || riding) return;
        heroNoclip = !heroNoclip;
        heroDrunkPending = false;
        if (heroNoclip)
        {
            // off the ragdoll (or a get-up), onto his clips where he is
            if (heroMode != 0 || hero.Rising)
            {
                hero.Rising = false;
                heroDrunk = false;
                HeroOverrides(false);
                HeroTakeBack(Vector3.zero);
            }
            heroAir = 0f;
        }
        else
        {
            // he falls from where he is
            heroVel = new Vector3(heroVel.x, 0f, heroVel.z);
            heroVy = 0f;
            heroGround = false;
            heroAir = 0f;
        }
        heroVel = Vector3.zero;
    }

    // Through walls and floors, the way the camera looks; Jump up, Down down, Sprint fast.
    void Fly(float dt)
    {
        float speed = heroSprint ? 18f : 6f;
        Vector3 wish = Quaternion.Euler(hCamPitch, hCamYaw, 0f) * new Vector3(hStick.x, 0f, hStick.y);
        float up = (hJumpId != kNone || Input.GetKey(KeyCode.Space) ? 1f : 0f) - (hDownId != kNone || Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.C) ? 1f : 0f);
        wish += Vector3.up * up;
        if (wish.magnitude > 1f) wish = wish.normalized;
        heroVel = Vector3.MoveTowards(heroVel, wish * speed, 30f * dt);
        heroPos += heroVel * dt;
        heroVy = 0f;
        heroGround = false;
        heroAir = 0f;
        hJumpReq = false;
        Vector3 flat = new Vector3(heroVel.x, 0f, heroVel.z);
        float wantYaw = heroAim ? hCamYaw : flat.magnitude > 0.3f ? Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg : heroYaw;
        heroYaw = Mathf.MoveTowardsAngle(heroYaw, wantYaw, 540f * dt);
        Step(dt);
    }

    // On his feet on something: the ground close under his hips and him not dropping. (In a long fall NM's balance
    // still says "balancing": he was put back on his clips at the ground under him, mid-air.)
    bool Landed(Delirium.Ragdoll.DeliriumRagdolls.DollState st)
    {
        return st.Velocity.y > -1.5f && Physics.Raycast(hero.Body.Hips.position, Vector3.down, 1.5f);
    }

    void ToggleDrunk()
    {
        if (heroNoclip || riding) return;
        if (hero == null || hero.Rising || hero.Killed) return;
        // (in the air too, now a ragdoll made mid-air keeps its place: it lands drunk)
        heroDrunkPending = false;
        var owner = hero.Body.transform;
        heroDrunk = !heroDrunk;
        if (heroDrunk)
        {
            HeroOverrides(true);
            if (heroMode == 0)
            {
                // from the pose his clip has him in, going the way he was going
                if (!HeroRagdoll()) heroDrunk = false;
                else ClimbStop();
            }
            if (heroDrunk) heroMode = 2;
            heroAim = false;
        }
        else
        {
            HeroOverrides(false);
            var st = nm.State(owner);
            if (heroMode == 2 && st.Ragdolled && !st.Fallen && Landed(st)) HeroTakeBack(st.Velocity);
            else if (heroMode == 2) heroMode = 1; // down, or still in the air: up first (or landed), then his clips
        }
    }

    // Onto Delirium from his clips, the whole body moving as he was (a jump's rise, a run's pace). Delirium starts
    // a ragdoll with its owner's Rigidbody's velocity; a push through the hips alone threw him.
    Rigidbody heroRb;
    Mannequin heroRefused; // the body Delirium wouldn't take: not asked again every frame (a fall asked 60 times a second)

    bool HeroRagdoll()
    {
        var owner = hero.Body.transform;
        if (heroRefused == hero.Body) return false;
        if (heroRb == null)
        {
            heroRb = owner.gameObject.AddComponent<Rigidbody>();
            heroRb.useGravity = false;
        }
        heroRb.isKinematic = false;
        heroRb.linearVelocity = heroVel + Vector3.up * (heroGround ? 0f : heroVy);
        bool ok = nm.Ragdoll(owner, Vector3.zero);
        if (ok) Outfit(hero);
        else
        {
            heroRb.isKinematic = true;
            heroRefused = hero.Body;
            Debug.LogWarning("[Sandbox] the hero wasn't taken; his bind pose: " + hero.Body.BindReport());
        }
        return ok;
    }

    // From the ragdoll (on his feet) back onto the clips: where he stands, facing his way, the pose faded over.
    void HeroTakeBack(Vector3 velocity)
    {
        var body = hero.Body;
        hHandPos = body.Hips.position;
        hHandRot = body.Hips.rotation;
        if (hHandLocal == null) hHandLocal = new Quaternion[body.Bones.Length];
        for (int i = 0; i < body.Bones.Length; i++) hHandLocal[i] = body.Bones[i].localRotation;
        heroPos = Feet(hero);
        heroYaw = Yaw(hero);
        if (nm.State(body.transform).Ragdolled) nm.GetUp(body.transform, 0.001f);
        heroMode = 0;
        ClimbStop();
        heroVel = Vector3.ProjectOnPlane(velocity, Vector3.up);
        if (heroVel.magnitude > 4f) heroVel = heroVel.normalized * 4f;
        heroVy = 0f;
        heroGround = true;
        hCur = hCurB = hPrev = hPrevB = null;
        hFade = 0f;
        hHand = true;
        hHandT = 0f;
        heroKnocked = 0f;
    }

    // A get-up's end (Game's Rise): his clips again, or a ragdoll again if he's still drunk.
    void HeroUp()
    {
        if (heroDrunk)
        {
            HeroOverrides(true);
            if (nm.Ragdoll(hero.Body.transform, Vector3.zero)) Outfit(hero);
            heroMode = 2;
            return;
        }
        HeroTakeBack(Vector3.zero);
    }

    // ------------------------------------------------------------------ every frame
    void HeroUpdate(float dt)
    {
        sel = men.IndexOf(hero);
        HeroInput(dt);
        // his Rigidbody is only how Delirium learns his speed: never the physics' to move
        if (heroRb != null && !heroRb.isKinematic && !nm.State(hero.Body.transform).Ragdolled) heroRb.isKinematic = true;
        if (hero.Rising || hero.Broken || riding || heroMode != 0) MoverOff();
        if (hero.Rising || hero.Broken) return;
        if (riding) return; // on the bike: Bike.cs has him
        var owner = hero.Body.transform;
        if (heroMode != 0)
        {
            var st = nm.State(owner);
            if (!st.Ragdolled) return;
            heroPos = Feet(hero);
            if (heroMode == 2 && !st.Fallen && !hero.Killed && !hero.HighFalling) Drunk(st, dt); // falling: his to wheel
            // knocked but he kept his feet: his clips again once he's steady
            if (heroMode == 1 && !st.Fallen && st.Balancing && Landed(st) && !hero.Killed && !st.Dead && hero.FireLeft <= 0f)
            {
                heroKnocked += dt;
                if (heroKnocked > 2.2f) HeroTakeBack(st.Velocity);
            }
            else heroKnocked = 0f;
            return;
        }
        if (heroNoclip) { MoverOff(); Fly(dt); }
        else Walk(dt);
        bool fists = kGuns[weapon % kGuns.Length].Fists;
        if (fists) heroAim = false;
        if ((heroAim || fists) && (hFireId != kNone || Input.GetKey(KeyCode.F)) && Time.time >= heroNextFire)
        {
            if (fists) Punch();
            else Fire();
        }
        // the blow lands part way through its swing
        if (heroPunchAt >= 0f && !heroPunchHit && Time.time >= heroPunchAt + heroPunchDur * 0.4f)
        {
            heroPunchHit = true;
            var t = PunchTarget(1.5f, 65f);
            if (t != null)
            {
                Vector3 fwd = Quaternion.Euler(0f, heroYaw, 0f) * Vector3.forward;
                Vector3 at = t.Body.Bone(punchCount % 3 == 0 ? "Head" : "Spine3").position;
                nm.MeleeHit(t.Body.transform, at, (fwd + Vector3.up * 0.15f) * 85f, hero.Body.Bone("Head").position);
                Attacked(t);
                Play(Sfx.Punch, at);
            }
        }
    }

    void Walk(float dt)
    {
        if (climbing)
        {
            ClimbUpdate(dt);
            Step(dt);
            return;
        }
        float walk = GtaAnim.Move[1].Ok ? GtaAnim.Move[1].Speed : 1.5f, run = GtaAnim.Move[2].Ok ? GtaAnim.Move[2].Speed : 3.6f;
        float mag = Mathf.Clamp01(hStick.magnitude);
        Vector3 wish = Quaternion.Euler(0f, hCamYaw, 0f) * new Vector3(hStick.x, 0f, hStick.y);
        // GTA V's: the stick walks (slower pushed lightly), Sprint held jogs, Sprint tapped quickly sprints (each tap
        // tops up the sprint, which runs down by itself: keep tapping to keep it)
        float sprint = GtaAnim.Move[3].Ok ? GtaAnim.Move[3].Speed : run * 1.5f;
        sprintPower = Mathf.Max(0f, sprintPower - dt * 0.55f);
        bool sprinting = sprintPower > (heroTier == 3 ? 0.2f : 0.5f);
        heroTier = heroAim ? 1 : sprinting ? 3 : heroSprint ? 2 : 1;
        float target = mag < 0.12f ? 0f : heroTier == 3 ? sprint : heroTier == 2 ? run : walk * Mathf.Clamp(mag / 0.7f, 0.5f, 1f);
        if (heroAim && GtaAnim.Strafe[1, 0].Ok) target = Mathf.Min(target, GtaAnim.Strafe[1, 0].Speed);
        // GTA V's: aiming he walks (strafes), Sprint held he runs
        if (heroAim && GtaAnim.V && GtaAnim.Strafe[0, 0].Ok && !heroSprint) target = Mathf.Min(target, GtaAnim.Strafe[0, 0].Speed * Mathf.Clamp(mag / 0.7f, 0.5f, 1f));
        Vector3 want = wish.sqrMagnitude > 1e-6f ? wish.normalized * target : Vector3.zero;
        if (Time.time < heroSpotUntil) want = Vector3.zero;
        if (hJumpReq && heroGround && Time.time >= heroSpotUntil && !heroNoclip && TryClimb())
        {
            // a ledge he can reach in front of him: GTA V's climb (or vault) instead of a jump
            hJumpReq = false;
            Step(dt);
            return;
        }
        if (hJumpReq) act = null;
        if (clAir != null) ClimbAir(dt); // off the far side of what he vaulted
        else if (!VAct(dt, mag, wish, walk, run))
        {
            if (heroAim || heroNoclip)
                heroVel = Vector3.MoveTowards(heroVel, want, (heroGround ? 14f : 3f) * dt);
            else
            {
                // his speed and his heading apart (GTA V's weight). The heading is where he means to go, not where a
                // wall slid him (else he turned along every wall he walked into, and never faced one to climb it).
                // It swings round at a steady rate (quick at a walk, a wide arc at a sprint), eased in and out; a
                // hard turn takes some speed off; the speed springs to the gait's.
                Vector3 basis = heroVel == heroVelSlid ? heroIntent : heroVel;
                float cur = basis.magnitude;
                if (cur > 0.05f) heroHeading = Mathf.Atan2(basis.x, basis.z) * Mathf.Rad2Deg;
                else { heroHeading = heroYaw; heroHeadingRate = 0f; }
                float off = 0f;
                if (want.sqrMagnitude > 1e-6f)
                {
                    float wy = Mathf.Atan2(want.x, want.z) * Mathf.Rad2Deg;
                    float fast = Mathf.InverseLerp(walk, sprint, cur);
                    if (cur < 0.3f) { heroHeading = wy; heroHeadingRate = 0f; }
                    else
                    {
                        off = Mathf.DeltaAngle(heroHeading, wy);
                        float top = Mathf.Lerp(560f, 230f, fast);
                        float rate = Mathf.Clamp(off * 8f, -top, top);
                        heroHeadingRate = Mathf.MoveTowards(heroHeadingRate, rate, Mathf.Lerp(3200f, 1600f, fast) * dt);
                        heroHeading += heroHeadingRate * dt;
                    }
                }
                else heroHeadingRate = Mathf.MoveTowards(heroHeadingRate, 0f, 3200f * dt);
                float wantSp = want.magnitude;
                if (cur > walk * 0.8f) wantSp *= Mathf.Lerp(1f, 0.6f, Mathf.InverseLerp(50f, 160f, Mathf.Abs(off)));
                float ease = !heroGround ? 0.9f : wantSp > cur ? (cur < walk ? 0.28f : cur < run ? 0.38f : 0.55f) : (wantSp < 0.1f ? 0.22f : 0.35f);
                cur = Mathf.SmoothDamp(cur, wantSp, ref heroSpeedRate, ease, 12f, dt);
                heroVel = Quaternion.Euler(0f, heroHeading, 0f) * Vector3.forward * cur;
            }
            // he faces the way he's going (his heading), not a step behind it
            bool going = heroVel.magnitude > 0.3f || (want.sqrMagnitude > 1e-6f && !heroAim);
            float wantYaw = heroAim ? hCamYaw : going && !heroNoclip ? heroHeading : heroVel.magnitude > 0.3f ? Mathf.Atan2(heroVel.x, heroVel.z) * Mathf.Rad2Deg : heroYaw;
            if (Time.time < heroFaceUntil) wantYaw = heroFaceYaw; // a punch: at the man he's hitting
            if (heroAim || Time.time < heroFaceUntil) { heroYaw = Mathf.MoveTowardsAngle(heroYaw, wantYaw, 720f * dt); heroYawVel = 0f; }
            else heroYaw = Mathf.SmoothDampAngle(heroYaw, wantYaw, ref heroYawVel, 0.05f, 900f, dt);
        }

        if (heroSpotLand > 0f && Time.time >= heroSpotLand)
        {
            heroSpotLand = 0f;
            Play(Sfx.Land, heroPos, 0.8f);
        }
        if (heroDrunkPending && heroGround && Time.time >= heroSpotUntil)
        {
            ToggleDrunk();
            if (heroMode != 0) return;
        }
        // only on the move: a jump on the spot is no use (the stick pushed, or him still going)
        if (hJumpReq && heroGround && Time.time >= heroSpotUntil && (mag > 0.2f || heroVel.magnitude > 1f))
        {
            // off the ground, standing or moving (GTA IV's jump on the spot, a clip that held him in place, got in the way)
            heroVy = 4.7f;
            heroGround = false;
            heroAir = 0f;
            heroJumped = true;
            Play(Sfx.Jump, heroPos, 0.7f);
        }
        hJumpReq = false;

        // over the ground, sliding along what's in the way (the capsule's foot is above a stair's step)
        heroIntent = heroVel;
        bool was = heroGround;
        if (clAir != null && clAir.Rise != null)
        {
            // over a vaulted top and down its far side: his way and drop are the clip's (ClimbAir), the top itself
            // not ground to stop on
            heroPos += new Vector3(heroVel.x, 0f, heroVel.z) * dt;
            float reach = 0.7f + Mathf.Max(0.03f, -heroVy * dt);
            if (heroVy <= 0f && Physics.SphereCast(heroPos + Vector3.up * 0.9f, 0.2f, Vector3.down, out var floor, reach) && floor.point.y <= heroPos.y + 0.72f && !VaultedTop(floor.point.y))
            {
                heroGround = true;
                heroVy = 0f;
                heroPos.y = floor.point.y;
                Play(Sfx.Land, heroPos, 0.9f);
                heroLanded = true;
                heroLandAir = heroAir;
                heroJumped = false;
            }
            else
            {
                heroGround = false;
                heroAir += dt;
            }
        }
        else
        {
            // Unity's character controller: it never ends up inside the map (a cast that starts touching a mesh
            // misses all of it: on the stairs his capsule touched the next step, and he walked through the wall
            // and fell out of the world), steps up stairs, keeps him on slopes
            var cc = HeroMover();
            PlaceMover();
            if (!heroGround) heroVy -= 14f * dt;
            Vector3 motion = heroVel * dt + Vector3.up * (heroGround ? -0.04f : heroVy * dt);
            var f = cc.Move(motion);
            Vector3 moved = cc.transform.position - heroPos;
            heroPos = cc.transform.position;
            if ((f & CollisionFlags.Sides) != 0)
            {
                // slid along what's in the way: his velocity what he really did (his heading stays his own)
                Vector3 real = new Vector3(moved.x, 0f, moved.z) / dt;
                heroVel = Vector3.ClampMagnitude(real, heroVel.magnitude);
            }
            if ((f & CollisionFlags.Above) != 0 && heroVy > 0f) heroVy = 0f;
            bool down = (f & CollisionFlags.Below) != 0;
            if (!down && was && heroVy <= 0f)
            {
                // a step or a slope down: kept on it if it's close under him, else he's off an edge
                Vector3 at = heroPos;
                var f2 = cc.Move(Vector3.down * 0.55f);
                if ((f2 & CollisionFlags.Below) != 0) { down = true; heroPos = cc.transform.position; }
                else { heroPos = at; PlaceMover(); }
            }
            if (down && heroVy <= 0f)
            {
                heroGround = true;
                heroVy = 0f;
                heroFallFrom = heroPos.y;
                if (!was) { Play(Sfx.Land, heroPos, 0.9f); heroLanded = true; heroLandAir = heroAir; }
                heroJumped = false;
            }
            else
            {
                if (was && heroVy <= 0f) { heroAir = 0f; heroJumpStill = false; }
                heroFallFrom = Mathf.Max(heroFallFrom, heroPos.y); // (on the ground it's where he stands)
                heroGround = false;
                heroAir += dt;
                // a long way down: Delirium's high fall (by how far he's fallen, not how fast: a vault's drop off its
                // clip came out fast, and a ledge a couple of metres down threw him into a ragdoll)
                if (heroFallFrom - heroPos.y > 4.5f && heroVy < -6f) Knock(true);
            }
        }
        heroVelSlid = heroVel;
        // walking into someone shoves him
        heroBump -= dt;
        if (heroBump <= 0f && heroVel.magnitude > 1f)
            foreach (var m in men)
            {
                if (m == hero || m.Rising) continue;
                Vector3 to = Vector3.ProjectOnPlane(m.Body.Hips.position - heroPos, Vector3.up);
                if (to.magnitude > 0.5f || Mathf.Abs(m.Body.Hips.position.y - heroPos.y - 0.95f) > 0.7f || Vector3.Dot(to, heroVel) <= 0f) continue;
                nm.Ragdoll(m.Body.transform, heroVel.normalized * (20f + heroVel.magnitude * 12f));
                Play(Sfx.Punch, m.Body.Hips.position, 0.5f);
                heroBump = 0.35f;
            }
        Step(dt);
    }

    // His body in the map's collisions while he's on his clips (not a ragdoll's: Delirium's world doesn't see it,
    // nor do the game's rays: it's on Ignore Raycast).
    CharacterController heroCC;

    CharacterController HeroMover()
    {
        if (heroCC != null) return heroCC;
        var go = new GameObject("Hero mover") { layer = 2 };
        go.transform.position = heroPos;
        heroCC = go.AddComponent<CharacterController>();
        heroCC.radius = 0.25f;
        heroCC.height = 1.7f;
        heroCC.skinWidth = 0.03f;
        heroCC.center = new Vector3(0f, 0.85f + 0.03f, 0f);
        heroCC.stepOffset = 0.5f;
        heroCC.slopeLimit = 55f;
        heroCC.minMoveDistance = 0f;
        Delirium.Ragdoll.DeliriumRagdolls.SandboxNotLevel.Add(go.transform);
        return heroCC;
    }

    // Where heroPos is (moved by a climb, a reset, a get-up): put there, not swept there.
    void PlaceMover()
    {
        if (heroCC == null) return;
        if ((heroCC.transform.position - heroPos).sqrMagnitude > 1e-6f || !heroCC.enabled)
        {
            heroCC.enabled = false;
            heroCC.transform.position = heroPos;
            heroCC.enabled = true;
        }
    }

    void MoverOff()
    {
        if (heroCC != null && heroCC.enabled) heroCC.enabled = false;
    }

    // Knocked off his clips: Delirium has him till he's up again.
    void Knock(bool highFall)
    {
        if (heroMode != 0) return;
        act = null;
        ClimbStop();
        var owner = hero.Body.transform;
        if (!HeroRagdoll()) return;
        if (highFall && Tumble(owner, new Vector3(heroVel.x, heroVy, heroVel.z))) hero.HighFalling = true;
        heroMode = 1;
        heroAim = false;
        heroKnocked = 0f;
    }

    bool OneShot(GtaAnim.Clip c) => c != null && (c == GtaAnim.Jump[3] || c == GtaAnim.JumpRifle[3]);

    // The clip for what he's doing, its rate (cycles a second), its gait partner and weight.
    // The clips the gun in hand goes with: a rifle's carry and jump for a long gun.
    GtaAnim.Clip[] MoveSet() => kGuns[weapon % kGuns.Length].Long && GtaAnim.MoveRifle[0].Ok && GtaAnim.MoveRifle[1].Ok ? GtaAnim.MoveRifle : GtaAnim.Move;
    GtaAnim.Clip[] JumpSet() => kGuns[weapon % kGuns.Length].Long && GtaAnim.JumpRifle[1].Ok ? GtaAnim.JumpRifle : GtaAnim.Jump;

    GtaAnim.Clip PickClip(out float rate, out GtaAnim.Clip b, out float w)
    {
        b = null;
        w = 0f;
        rate = 1f;
        hPhaseSet = -1f;
        float speed = heroVel.magnitude;
        var j = JumpSet();
        var move = MoveSet();
        // flying: the in-air clip while he moves, his idle while he hovers
        if (heroNoclip)
        {
            if (speed > 0.5f && !heroAim && j[1].Ok) { rate = 1f / j[1].Dur; return j[1]; }
            if (!heroAim) { rate = 1f / move[0].Dur; return move[0]; }
            speed = new Vector3(heroVel.x, 0f, heroVel.z).magnitude;
        }
        // GTA V's climb (two of its heights blended) or the vault's way down
        if (climbing) { hPhaseSet = Mathf.Clamp01(clT / clA.Dur); rate = 0f; b = clB; w = clB != null ? clW : 0f; return clA; }
        if (clAir != null) { hPhaseSet = Mathf.Clamp01(clAirT / clAir.Dur); rate = 0f; return clAir; }
        // GTA V's start, stop or turn: its own clip, its own clock (VAct)
        if (act != null) { hPhaseSet = Mathf.Clamp01(actT / act.Dur); rate = 0f; return act; }
        // a jump on the spot is its clip from start to end (the clip itself lifts him and lands him)
        if (Time.time < heroSpotUntil && j[3].Ok) { rate = 1f / j[3].Dur; return j[3]; }
        // in the air: the one clip; take-offs and landings are cross-fades (their own clips carry him along the
        // ground, and he'd slide and snap back)
        // GTA V's: the take-off's clip through the jump (it holds the leap), then its fall
        if (!heroGround && heroJumped && !heroAim && GtaAnim.V && GtaAnim.VLaunch.Ok && heroAir < GtaAnim.VLaunch.Dur * 0.85f)
        {
            hPhaseSet = heroAir / GtaAnim.VLaunch.Dur;
            rate = 0f;
            return GtaAnim.VLaunch;
        }
        if (!heroGround && j[1].Ok) { rate = 1f / j[1].Dur; return j[1]; }
        if (heroAim)
        {
            if (speed < 0.3f && GtaAnim.StrafeIdle.Ok) { rate = 1f / GtaAnim.StrafeIdle.Dur; return GtaAnim.StrafeIdle; }
            // the way he goes from the way he faces, in 8 sectors turning left from straight ahead
            float a = -Vector3.SignedAngle(Quaternion.Euler(0f, heroYaw, 0f) * Vector3.forward, heroVel, Vector3.up);
            int sector = (Mathf.RoundToInt(a / 45f) + 8) % 8;
            var c = GtaAnim.Gait(new[] { GtaAnim.Strafe[0, sector], GtaAnim.Strafe[1, sector] }, speed, out rate, out b, out w);
            if (c != null) return c;
        }
        if (speed >= 0.3f && GtaAnim.V)
        {
            // GTA V's gaits blended by his speed (walk into jog into sprint, one stride carried through)
            var g = GtaAnim.Gait(new[] { move[1], move[2], move[3] }, speed, out rate, out b, out w);
            if (g != null) return g;
        }
        if (speed >= 0.3f)
        {
            // the gait's own clip, its stride matched to his speed within 0.45-1.25 of its own
            var c = move[Mathf.Clamp(heroTier, 1, 3)];
            if (!c.Ok) c = move[c == move[3] ? 2 : 1];
            if (c.Ok && c.Speed > 0.1f)
            {
                rate = 1f / c.Dur * Mathf.Clamp(speed / c.Speed, 0.45f, 1.25f);
                return c;
            }
        }
        rate = 1f / move[0].Dur;
        return move[0];
    }

    // The clips' clock: one gait is one motion (walk into run keeps the step); another clip fades in over 0.2 s.
    void Step(float dt)
    {
        var c = PickClip(out float rate, out var b, out float w);
        bool along = c == hCur || (c != null && (c == hCurB || (b != null && b == hCur)));
        if (!along)
        {
            hPrev = hCur; hPrevB = hCurB; hPrevW = hCurW; hPrevPhase = hPhase; hPrevRate = hRate;
            hFade = hPrev != null ? 1f : 0f;
            hFadeLen = hNextFade > 0f ? hNextFade : GtaAnim.V ? 0.3f : 0.2f;
            hNextFade = 0f;
            bool cyclic = c != null && hCur != null && !OneShot(c) && !OneShot(hCur);
            if (!cyclic) hPhase = 0f;
        }
        if (hNextPhase >= 0f && c != null && c.Cycle) { hPhase = hNextPhase; hNextPhase = -1f; }
        hCur = c; hCurB = b; hCurW = w; hRate = rate;
        if (hPhaseSet >= 0f) hPhase = hPhaseSet;
        else
        {
            float before = hPhase;
            hPhase += dt * rate;
            // a foot down twice a cycle (GTA IV's clips; GTA V's have their heels marked: FootSteps below)
            if (heroGround && c != null && c.Cycle && c.Tags == null && heroVel.magnitude > 0.3f && Mathf.Floor(before * 2f) != Mathf.Floor(hPhase * 2f))
                Play(Sfx.Step, heroPos, heroTier == 3 ? 0.9f : heroTier == 2 ? 0.7f : 0.45f);
            hPhase = OneShot(c) ? Mathf.Min(hPhase, 1f) : hPhase - Mathf.Floor(hPhase);
        }
        FootSteps(c);
        hPrevPhase += dt * hPrevRate;
        hPrevPhase -= Mathf.Floor(hPrevPhase);
        hFade = Mathf.Max(0f, hFade - dt / Mathf.Max(hFadeLen, 0.01f));
        bool gunV = VGun() != null;
        hUpper = Mathf.MoveTowards(hUpper, heroAim ? 1f : 0f, dt / (gunV && heroAim ? 0.1f : 0.18f));
        AimClock(dt);
    }

    // After the frame's moves: his pose from the clips onto the bones (LateUpdate, before the camera).
    void HeroPose()
    {
        if (hero == null || heroMode != 0 || hero.Rising || hCur == null) return;
        var body = hero.Body;
        if (heroRb != null && !heroRb.isKinematic) heroRb.isKinematic = true; // ours to move, not the physics'
        // drawn a little behind a step up or down the stairs (the controller steps at once), never in the air
        float dy = heroPos.y - heroDrawY;
        if (!heroGround || climbing || clAir != null || Mathf.Abs(dy) > 0.8f) { heroDrawY = heroPos.y; heroDrawYV = 0f; }
        else heroDrawY = Mathf.SmoothDamp(heroDrawY, heroPos.y, ref heroDrawYV, 0.07f, Mathf.Infinity, Mathf.Max(Time.deltaTime, 1e-4f));
        Vector3 drawPos = new Vector3(heroPos.x, heroDrawY, heroPos.z);
        body.transform.SetPositionAndRotation(drawPos, Quaternion.Euler(0f, heroYaw, 0f));
        if (!GtaAnim.Pose(hCur, GtaAnim.At(hCur, hPhase), fA)) return;
        if (hCurB != null && hCurW > 0f && GtaAnim.Pose(hCurB, GtaAnim.At(hCurB, hPhase), fB)) GtaAnim.Blend(fA, fB, hCurW);
        if (hFade > 0f && hPrev != null && GtaAnim.Pose(hPrev, GtaAnim.At(hPrev, hPrevPhase), fC))
        {
            if (hPrevB != null && hPrevW > 0f && GtaAnim.Pose(hPrevB, GtaAnim.At(hPrevB, hPrevPhase), fB)) GtaAnim.Blend(fC, fB, hPrevW);
            GtaAnim.Blend(fA, fC, hFade);
        }
        body.Apply(fA, drawPos, heroYaw);
        // GTA V's lean into a turn: the faster he goes and the harder he turns, the more (about his feet)
        {
            float dtp = Mathf.Max(Time.deltaTime, 1e-4f);
            float yawRate = Mathf.DeltaAngle(heroYawLast, heroYaw) / dtp;
            heroYawLast = heroYaw;
            float sp = new Vector3(heroVel.x, 0f, heroVel.z).magnitude;
            float lean = heroGround && !heroAim && !climbing && !heroNoclip ? -Mathf.Atan(sp * yawRate * Mathf.Deg2Rad / 9.81f) * Mathf.Rad2Deg * 0.3f : 0f;
            heroTilt = Mathf.SmoothDamp(heroTilt, Mathf.Clamp(lean, -12f, 12f), ref heroTiltVel, 0.12f, Mathf.Infinity, dtp);
            if (Mathf.Abs(heroTilt) > 0.05f) body.Hips.RotateAround(drawPos, body.transform.forward, heroTilt);
        }
        // the upper body: the gun's aim (its clip's last frame) over the legs' clip, bent up and down with the camera
        if (hUpper > 0f && AimPoseV(body)) { }
        else if (hUpper > 0f)
        {
            int ac = kGuns[weapon % kGuns.Length].AimClip;
            var aim = GtaAnim.Aim[ac >= 0 && ac < GtaAnim.Aim.Length ? ac : 0];
            if (aim == null || !aim.Ok) aim = GtaAnim.Aim[kGuns[weapon % kGuns.Length].Long ? 3 : 0];
            if (aim == null || !aim.Ok) aim = GtaAnim.Aim[0];
            if (GtaAnim.Pose(aim, 1f, fB))
            {
                var turn = Quaternion.Euler(0f, heroYaw - 90f, 0f);
                foreach (int i in upperBones)
                    if (i >= 0) body.Bones[i].rotation = Quaternion.Slerp(body.Bones[i].rotation, body.World(fB, i, heroYaw), hUpper);
            }
            var bend = Quaternion.AngleAxis(hCamPitch / 3f * hUpper, body.transform.right);
            foreach (int i in bendBones)
                if (i >= 0) body.Bones[i].rotation = bend * body.Bones[i].rotation;
        }
        // a punch: the game's blow on his upper body (his legs go on as they were); none found: a plain jab
        if (heroPunchAt >= 0f)
        {
            float t = Time.time - heroPunchAt;
            if (t >= heroPunchDur) heroPunchAt = -1f;
            else
            {
                float wgt = Mathf.Clamp01(t / 0.08f) * Mathf.Clamp01((heroPunchDur - t) / 0.14f);
                if (heroPunchClip != null && heroPunchClip.Ok && GtaAnim.Pose(heroPunchClip, Mathf.Clamp01(t / heroPunchClip.Dur), fB))
                {
                    var turn = Quaternion.Euler(0f, heroYaw - 90f, 0f);
                    foreach (int i in upperBones)
                        if (i >= 0) body.Bones[i].rotation = Quaternion.Slerp(body.Bones[i].rotation, body.World(fB, i, heroYaw), wgt);
                }
                else
                {
                    // the right arm thrown straight ahead and back
                    float reach = Mathf.Sin(Mathf.PI * Mathf.Clamp01(t / heroPunchDur));
                    Transform arm = body.Bone("RightArm"), fore = body.Bone("RightForeArm");
                    Vector3 fwd = body.transform.forward + Vector3.up * 0.1f;
                    arm.rotation = Quaternion.Slerp(arm.rotation, Quaternion.FromToRotation(fore.position - arm.position, fwd) * arm.rotation, reach);
                    Transform hand = body.Bone("RightHand");
                    fore.rotation = Quaternion.Slerp(fore.rotation, Quaternion.FromToRotation(hand.position - fore.position, fwd) * fore.rotation, reach);
                }
            }
        }
        if (hHand)
        {
            hHandT += Time.deltaTime;
            float k = Mathf.SmoothStep(0f, 1f, hHandT / 0.3f);
            if (hHandT >= 0.3f) hHand = false;
            else
            {
                var locals = new Quaternion[body.Bones.Length];
                for (int i = 1; i < locals.Length; i++) locals[i] = body.Bones[i].localRotation;
                body.Hips.SetPositionAndRotation(Vector3.Lerp(hHandPos, body.Hips.position, k), Quaternion.Slerp(hHandRot, body.Hips.rotation, k));
                for (int i = 1; i < locals.Length; i++) body.Bones[i].localRotation = Quaternion.Slerp(hHandLocal[i], locals[i], k);
            }
        }
    }

    // GTA V's camera: behind him and a little over, eased (never snapped) to the aim's over-the-shoulder and back,
    // further out the faster he goes (a little wider at a sprint), swinging round behind him by itself while he
    // runs and you leave it alone, pulled in from walls at once and let out slowly.
    float hcDist = 3.4f, hcDistV, camSide = 0.2f, camSideV, camHigh = 1.4f, camHighV, camOut = -1f, camFov, camFovBase;
    Vector3 camPivotV;
    float hLookAt;

    void HeroCamera()
    {
        if (hero == null) return;
        if (gunModel != null && !gunModel.activeSelf) gunModel.SetActive(true); // off the bike: back in his hand
        GunInHand();
        float dt = Mathf.Max(Time.unscaledDeltaTime, 1e-4f);
        bool onFeet = heroMode == 0 && !hero.Rising;
        bool aiming = heroAim && onFeet;
        float sp = onFeet ? new Vector3(heroVel.x, 0f, heroVel.z).magnitude : 0f;
        hcDist = Mathf.SmoothDamp(hcDist, aiming ? 1.75f : 3.3f + Mathf.InverseLerp(2f, 7f, sp) * 0.8f, ref hcDistV, aiming ? 0.12f : 0.4f, Mathf.Infinity, dt);
        camSide = Mathf.SmoothDamp(camSide, aiming ? 0.55f : 0.2f, ref camSideV, 0.12f, Mathf.Infinity, dt);
        camHigh = Mathf.SmoothDamp(camHigh, aiming ? 1.55f : 1.4f, ref camHighV, 0.15f, Mathf.Infinity, dt);
        // behind him by itself: on the move, a moment after you last turned it (not when he runs at you)
        if (onFeet && !aiming && !riding && sp > 1f && Time.unscaledTime - hLookAt > 1.2f && !climbing)
        {
            float d = Mathf.DeltaAngle(hCamYaw, heroYaw);
            if (Mathf.Abs(d) < 135f) hCamYaw += d * (1f - Mathf.Exp(-(0.5f + 1.5f * Mathf.InverseLerp(1f, 7f, sp)) * dt));
            hCamPitch = Mathf.Lerp(hCamPitch, 10f, 1f - Mathf.Exp(-0.7f * dt));
        }
        Vector3 want = onFeet ? heroPos + Vector3.up * camHigh : hero.Body.Hips.position + Vector3.up * 0.5f;
        // a little lag (more up and down: no bob from his steps)
        Vector3 p = hPivot;
        p.x = Mathf.SmoothDamp(p.x, want.x, ref camPivotV.x, 0.05f, Mathf.Infinity, dt);
        p.z = Mathf.SmoothDamp(p.z, want.z, ref camPivotV.z, 0.05f, Mathf.Infinity, dt);
        p.y = Mathf.SmoothDamp(p.y, want.y, ref camPivotV.y, 0.12f, Mathf.Infinity, dt);
        if ((p - want).sqrMagnitude > 25f) p = want; // a jump in place (a reset): no long swing
        hPivot = p;
        var turn = Quaternion.Euler(hCamPitch, hCamYaw, 0f);
        Vector3 back = -(turn * Vector3.forward);
        Vector3 focus = hPivot + turn * Vector3.right * camSide;
        if (Physics.Linecast(hPivot, focus, out var side)) focus = side.point - (focus - hPivot).normalized * 0.12f;
        // not through a wall: pulled in at once, let out slowly
        float room = Physics.SphereCast(focus, 0.18f, back, out var hit, hcDist) ? Mathf.Max(0.3f, hit.distance) : hcDist;
        camOut = camOut < 0f || room < camOut ? room : Mathf.MoveTowards(camOut, room, 2.5f * dt);
        cam.transform.SetPositionAndRotation(focus + back * camOut, turn);
        // a touch wider at a sprint, closer in when aiming
        if (camFovBase <= 0f) camFov = camFovBase = cam.fieldOfView;
        float fov = camFovBase + (onFeet && heroTier == 3 && sp > 5f ? 6f : 0f) - (aiming ? 6f : 0f);
        camFov = Mathf.Lerp(camFov, fov, 1f - Mathf.Exp(-5f * dt));
        cam.fieldOfView = camFov;
    }

    // The stick steers the ragdoll: a lean the way you push it and a helping hand on his hips, to a walking pace.
    void Drunk(Delirium.Ragdoll.DeliriumRagdolls.DollState st, float dt)
    {
        var owner = hero.Body.transform;
        float mag = Mathf.Clamp01(hStick.magnitude);
        Vector3 dir = Quaternion.Euler(0f, hCamYaw, 0f) * new Vector3(hStick.x, 0f, hStick.y);
        dir = dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector3.zero;
        if (mag > 0.12f)
        {
            float along = Vector3.Dot(Vector3.ProjectOnPlane(st.Velocity, Vector3.up), dir);
            if (along < 1.6f * mag) nm.Ragdoll(owner, dir * ((st.Mass > 1f ? st.Mass : 75f) * 4f * dt * mag));
        }
        heroLean -= dt;
        if (heroLean <= 0f && (mag > 0.12f || heroLeaning))
        {
            heroLean = 0.25f;
            heroLeaning = mag > 0.12f;
            float lean = heroLeaning ? 0.1f * mag : 0f;
            nm.React(owner, "leanInDirection start=true leanAmount=" + lean.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " dir=" + V(heroLeaning ? dir : Vector3.forward));
        }
        if (hJumpReq && st.Balancing && mag > 0.2f) nm.Jump(owner, false, dir); // only on the move, as on his clips
        hJumpReq = false;
    }

    // His fists: a blow at whoever is in front of him (the game's own clip when one was found).
    float heroPunchAt = -1f, heroPunchDur = 0.4f, heroFaceUntil, heroFaceYaw;
    GtaAnim.Clip heroPunchClip;
    bool heroPunchHit;
    int punchCount;

    void Punch()
    {
        punchCount++;
        heroPunchClip = GtaAnim.Punches.Count > 0 ? GtaAnim.Punches[punchCount % GtaAnim.Punches.Count] : null;
        heroPunchDur = heroPunchClip != null ? Mathf.Clamp(heroPunchClip.Dur, 0.35f, 0.9f) : 0.38f;
        heroPunchAt = Time.time;
        heroPunchHit = false;
        heroNextFire = Time.time + heroPunchDur * 0.85f;
        var t = PunchTarget(2.2f, 100f);
        if (t != null)
        {
            Vector3 to = t.Body.Hips.position - heroPos;
            heroFaceYaw = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg;
            heroFaceUntil = Time.time + heroPunchDur;
        }
        Play(Sfx.Jump, heroPos + Vector3.up * 1.3f, 0.45f); // the swing's air
    }

    // The nearest man in front of him within `reach` (m) and `cone` (degrees to each side).
    Man PunchTarget(float reach, float cone)
    {
        Man best = null;
        float bestD = reach;
        Vector3 fwd = Quaternion.Euler(0f, heroYaw, 0f) * Vector3.forward;
        foreach (var m in men)
        {
            if (m.Hero || m.Rising) continue;
            Vector3 to = m.Body.Hips.position - (heroPos + Vector3.up * 0.95f);
            if (Mathf.Abs(to.y) > 1.1f) continue;
            to.y = 0f;
            if (to.magnitude < bestD && (to.magnitude < 0.4f || Vector3.Angle(fwd, to) < cone))
            {
                bestD = to.magnitude;
                best = m;
            }
        }
        return best;
    }

    // "Look at who hit him" (only with that reaction on): the hero is his attacker for 2 s, followed as he moves,
    // then forgotten. Unarmed to Delirium: told of a gun, he threw an arm up at it and kept pointing where it had been.
    void Attacked(Man m)
    {
        if ((extra & 8) == 0) return;
        m.AttackerUntil = Time.time + 2f;
        nm.SetAttacker(m.Body.transform, hero.Body.transform, false);
    }

    void Fire()
    {
        var gun = kGuns[weapon % kGuns.Length];
        heroNextFire = Time.time + gun.Between;
        var ray = new Ray(cam.transform.position, cam.transform.forward);
        var m = Pick(ray, out Vector3 point);
        hCamPitch -= gun.Between < 0.2f ? 0.1f : gun.Long ? 0.5f : 0.3f; // the kick
        Shot(gun);
        Vector3 muzzle = gunModel != null ? gunModel.transform.TransformPoint(gunMuzzle) : hero.Body.Hips.position;
        Flash(muzzle, 0.14f);
        PlayGun(muzzle);
        Missed(ray, m);
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

    // The hero's gun in his right hand: a few boxes, enough to tell a pistol from a rifle.
    void GunInHand()
    {
        int k = weapon % kGuns.Length;
        if (hero == null) return;
        if (gunShown == k)
        {
            if (gunModel != null) GunAim();
            return;
        }
        if (gunModel != null) Destroy(gunModel);
        gunModel = null;
        if (kGuns[k].Fists)
        {
            gunShown = k; // empty hands
            return;
        }
        gunShown = k;
        gunModel = new GameObject("Gun " + kGuns[k].Label);
        // under his root, not his hand: a loaded model's bones carry its own scale (a file in centimetres), and the
        // gun grew with them. It's put in his hand every frame anyway (GunAim).
        gunModel.transform.SetParent(hero.Body.transform, false);
        // the hand's bone: +X along the fingers, +Z the thumb's side (up when he aims); the gun is built with +X its
        // barrel and +Y its top
        gunModel.transform.localPosition = new Vector3(0.075f, 0f, 0.015f);
        gunModel.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        if (matGun == null)
        {
            matGun = Mat(new Color(0.13f, 0.13f, 0.15f));
            matWood = Mat(new Color(0.45f, 0.27f, 0.13f));
        }
        float len = 0.2f;
        switch (kGuns[k].Name)
        {
            case "pistol":
                Part(0.05f, 0.03f, 0.17f, 0.034f, 0.026f, matGun);
                Part(-0.01f, -0.035f, 0.036f, 0.1f, 0.026f, matGun, -12f);
                len = 0.14f;
                break;
            case "deagle":
                Part(0.065f, 0.035f, 0.23f, 0.048f, 0.034f, matGun);
                Part(-0.01f, -0.04f, 0.042f, 0.11f, 0.03f, matWood, -12f);
                len = 0.18f;
                break;
            case "smg":
                Part(0.07f, 0.03f, 0.26f, 0.06f, 0.04f, matGun);
                Part(0.24f, 0.035f, 0.1f, 0.022f, 0.022f, matGun);
                Part(0.1f, -0.08f, 0.03f, 0.16f, 0.024f, matGun);
                Part(-0.01f, -0.035f, 0.036f, 0.09f, 0.028f, matGun, -12f);
                len = 0.29f;
                break;
            case "ak47":
                Part(0.12f, 0.03f, 0.4f, 0.055f, 0.04f, matGun);
                Part(0.46f, 0.04f, 0.3f, 0.02f, 0.02f, matGun);
                Part(0.33f, 0.02f, 0.18f, 0.045f, 0.044f, matWood);
                Part(-0.2f, 0.005f, 0.26f, 0.06f, 0.035f, matWood, 6f);
                Part(0.17f, -0.09f, 0.045f, 0.17f, 0.028f, matGun, 18f);
                Part(-0.01f, -0.035f, 0.036f, 0.09f, 0.028f, matWood, -12f);
                len = 0.61f;
                break;
            case "shotgun":
                Part(0.1f, 0.03f, 0.38f, 0.052f, 0.042f, matGun);
                Part(0.5f, 0.045f, 0.46f, 0.03f, 0.03f, matGun);
                Part(0.44f, 0.01f, 0.2f, 0.038f, 0.046f, matWood);
                Part(-0.22f, 0f, 0.28f, 0.065f, 0.036f, matWood, 8f);
                len = 0.73f;
                break;
            default: // sniper
                Part(0.12f, 0.03f, 0.44f, 0.052f, 0.04f, matGun);
                Part(0.6f, 0.04f, 0.56f, 0.022f, 0.022f, matGun);
                Part(0.12f, 0.09f, 0.24f, 0.04f, 0.04f, matGun);
                Part(-0.22f, 0f, 0.3f, 0.07f, 0.036f, matWood, 6f);
                Part(0.14f, -0.05f, 0.04f, 0.07f, 0.028f, matGun);
                len = 0.88f;
                break;
        }
        gunMuzzle = new Vector3(len, 0.035f, 0f);
        GunAim();
    }

    // The gun in his hand, its barrel where it should point: at what the camera's on while he aims, along his
    // forearm while he carries it (worked out in the world: the hand bone's own roll doesn't matter).
    void GunAim()
    {
        var body = hero.Body;
        Transform hand = body.Bone("RightHand"), fore = body.Bone("RightForeArm");
        Vector3 carry = (hand.position - fore.position).normalized;
        Vector3 dir = carry;
        if (heroMode == 0 && hUpper > 0f)
        {
            Vector3 aimed = (cam.transform.position + cam.transform.forward * 60f - hand.position).normalized;
            dir = Vector3.Slerp(carry, aimed, hUpper).normalized;
        }
        Vector3 up = Mathf.Abs(Vector3.Dot(dir, Vector3.up)) > 0.95f ? body.transform.forward : Vector3.up;
        var turn = Quaternion.LookRotation(dir, up) * Quaternion.Euler(0f, -90f, 0f); // the gun's +X is its barrel
        gunModel.transform.SetPositionAndRotation(hand.position + dir * 0.06f - turn * Vector3.up * 0.01f, turn);
    }

    Material matGun, matWood;

    // One box of the gun: its middle (along the barrel, up), its size (length, height, width), tilted about its width.
    void Part(float x, float y, float length, float height, float width, Material m, float tilt = 0f)
    {
        var b = GameObject.CreatePrimitive(PrimitiveType.Cube);
        DestroyImmediate(b.GetComponent<Collider>());
        b.transform.SetParent(gunModel.transform, false);
        b.transform.localPosition = new Vector3(x, y, 0f);
        b.transform.localRotation = Quaternion.Euler(0f, 0f, tilt);
        b.transform.localScale = new Vector3(length, height, width);
        b.GetComponent<Renderer>().sharedMaterial = m;
    }

    // ------------------------------------------------------------------ the sticks and buttons
    Vector2 PadL(float w, float h) => new Vector2(125f, h - 125f);
    Vector2 BtnJump(float w, float h) => new Vector2(w - 95f, h - 95f);
    Vector2 BtnSprint(float w, float h) => new Vector2(w - 200f, h - 70f);
    Vector2 BtnAim(float w, float h) => new Vector2(w - 185f, h - 170f);
    Vector2 BtnDrunk(float w, float h) => new Vector2(w - 80f, h - 200f);
    Vector2 BtnFire(float w, float h) => new Vector2(85f, 150f); // top left: the stick's thumb, off the camera's side
    Vector2 BtnUse(float w, float h) => new Vector2(w - 290f, h - 125f); // (shown by a map's button or door in reach)

    struct Finger
    {
        public int Id;
        public Vector2 Pos, Screen;   // the screen's point in the interface's units (y down), and in pixels (y up)
        public bool Began, Ended;
    }

    readonly List<Finger> fingers = new List<Finger>();

    bool Has(int id)
    {
        foreach (var f in fingers)
            if (f.Id == id && !f.Ended) return true;
        return false;
    }

    void HeroInput(float dt)
    {
        if (vrOn) return; // (the controllers set the stick and the buttons: VR.cs)
        float s = UiScale(), w = Screen.width / s, h = Screen.height / s;
        fingers.Clear();
        if (Input.touchCount > 0)
            for (int i = 0; i < Input.touchCount; i++)
            {
                var t = Input.GetTouch(i);
                fingers.Add(new Finger
                {
                    Id = t.fingerId, Screen = t.position, Pos = new Vector2(t.position.x / s, (Screen.height - t.position.y) / s),
                    Began = t.phase == TouchPhase.Began, Ended = t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled,
                });
            }
        else if (Input.GetMouseButton(0) || Input.GetMouseButtonUp(0))
        {
            Vector2 p = Input.mousePosition;
            fingers.Add(new Finger { Id = kMouse, Screen = p, Pos = new Vector2(p.x / s, (Screen.height - p.y) / s), Began = Input.GetMouseButtonDown(0), Ended = Input.GetMouseButtonUp(0) });
        }
        // a finger that's gone without saying so
        if (!Has(hStickId)) hStickId = kNone;
        if (!Has(hSprintId)) hSprintId = kNone;
        if (!Has(hDragId)) hDragId = kNone;
        if (!Has(hFireId)) hFireId = kNone;
        if (!Has(hJumpId)) hJumpId = kNone;
        if (!Has(hDownId)) hDownId = kNone;
        if (!Has(radialFinger)) radialFinger = -99;

        if (Input.GetKeyDown(KeyCode.E)) UseNow();
        hStick = Vector2.zero;
        foreach (var f in fingers)
        {
            if (f.Began && !OverUi(f.Screen))
            {
                if (RadialTap(f.Pos, f.Id, w, h)) { } // the VRChat avatar's Expressions button and its wheel
                else if (useTarget != null && Vector2.Distance(f.Pos, BtnUse(w, h)) < kBtnR + 6f) UseNow(); // a map's button, a door
                else if (BikeTap(f.Pos, f.Id, w, h)) { } // Ride, Off (Gas and Brake are Jump's and Sprint's places)
                else if (Vector2.Distance(f.Pos, BtnJump(w, h)) < kBtnR + 6f) { hJumpReq = true; hJumpId = f.Id; }
                else if (Vector2.Distance(f.Pos, BtnAim(w, h)) < kBtnR + 6f) { if (heroMode == 0 && !kGuns[weapon % kGuns.Length].Fists) heroAim = !heroAim; }
                else if (Vector2.Distance(f.Pos, BtnDrunk(w, h)) < kBtnR + 6f) { if (heroNoclip) hDownId = f.Id; else ToggleDrunk(); }
                else if ((heroAim || (kGuns[weapon % kGuns.Length].Fists && heroMode == 0)) && Vector2.Distance(f.Pos, BtnFire(w, h)) < kBtnR + 6f) hFireId = f.Id;
                else if (Vector2.Distance(f.Pos, BtnSprint(w, h)) < kBtnR + 6f) hSprintId = f.Id;
                else if (Vector2.Distance(f.Pos, PadL(w, h)) < kStickR + 45f) hStickId = f.Id;
                else
                {
                    hDragId = f.Id;
                    hDragLast = f.Pos; // empty screen only turns the camera (Fire is its button, or F)
                }
            }
            if (f.Ended) continue;
            if (f.Id == radialFinger) { RadialDrag(f.Pos, w, h); continue; }
            if (f.Id == hStickId)
            {
                Vector2 v = (f.Pos - PadL(w, h)) / kStickR;
                if (v.magnitude > 1f) v = v.normalized;
                hStick = new Vector2(v.x, -v.y);
            }
            else if (f.Id == hDragId)
            {
                Vector2 d = f.Pos - hDragLast;
                hDragLast = f.Pos;
                hCamYaw += d.x * 0.28f;
                hCamPitch += d.y * 0.22f;
                if (d.sqrMagnitude > 0.5f) hLookAt = Time.unscaledTime;
            }
        }
        // a PC's keys
        Vector2 keys = new Vector2((Input.GetKey(KeyCode.D) ? 1f : 0f) - (Input.GetKey(KeyCode.A) ? 1f : 0f), (Input.GetKey(KeyCode.W) ? 1f : 0f) - (Input.GetKey(KeyCode.S) ? 1f : 0f));
        if (keys.sqrMagnitude > 0f) hStick = keys.normalized * (Input.GetKey(KeyCode.LeftAlt) ? 0.5f : 1f);
        if (Input.GetKeyDown(KeyCode.Space)) hJumpReq = true;
        if (Input.GetKeyDown(KeyCode.J)) ToggleDrunk();
        if (Input.GetKeyDown(KeyCode.N)) ToggleNoclip();
        if (Input.GetKeyDown(KeyCode.E)) { if (riding) BikeOff(false); else if (NearBike()) BikeOn(); }
        // the right mouse button aims (on a phone Unity calls a second finger the right button: not that)
        if (Input.touchCount == 0 && Input.GetMouseButtonDown(1) && heroMode == 0 && !kGuns[weapon % kGuns.Length].Fists) heroAim = !heroAim;
        bool sprintWas = heroSprint;
        heroSprint = hSprintId != kNone || Input.GetKey(KeyCode.LeftShift);
        if (heroSprint && !sprintWas) sprintPower = Mathf.Min(sprintPower + 0.4f, 1.2f); // a tap
        hCamPitch = Mathf.Clamp(hCamPitch, -40f, 65f);
        if (heroMode != 0) heroAim = false;
    }

    void Disc(Vector2 at, float r, Color c, string text = null)
    {
        if (disc == null)
        {
            const int n = 96;
            disc = new Texture2D(n, n, TextureFormat.RGBA32, false);
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = Mathf.Sqrt((x - n / 2f + 0.5f) * (x - n / 2f + 0.5f) + (y - n / 2f + 0.5f) * (y - n / 2f + 0.5f)) / (n / 2f);
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(255f * Mathf.Clamp01((1f - d) * 24f)));
                }
            disc.SetPixels32(px);
            disc.Apply();
        }
        var was = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(new Rect(at.x - r, at.y - r, r * 2f, r * 2f), disc);
        GUI.color = was;
        if (text != null) GUI.Label(new Rect(at.x - r, at.y - 12f, r * 2f, 24f), text, padText);
    }

    GUIStyle padText;

    void HeroGui(float w, float h)
    {
        if (riding)
        {
            BikeGui(w, h);
            RadialGui(w, h);
            return;
        }
        BikeGui(w, h);
        bool fists = kGuns[weapon % kGuns.Length].Fists;
        var ring = new Color(1f, 1f, 1f, 0.16f);
        var knob = new Color(1f, 1f, 1f, 0.45f);
        var lit = new Color(0.35f, 0.75f, 1f, 0.6f);
        Disc(PadL(w, h), kStickR + 18f, ring);
        Disc(PadL(w, h) + new Vector2(hStick.x, -hStick.y) * kStickR, 30f, knob);
        Disc(BtnSprint(w, h), kBtnR, heroSprint ? lit : knob * new Color(1f, 1f, 1f, 0.6f), "Sprint");
        Disc(BtnJump(w, h), kBtnR, heroNoclip && hJumpId != kNone ? lit : knob * new Color(1f, 1f, 1f, 0.6f), heroNoclip ? "Up" : "Jump");
        if (!fists) Disc(BtnAim(w, h), kBtnR, heroAim ? lit : knob * new Color(1f, 1f, 1f, 0.6f), "Aim");
        if (fists && heroMode == 0) Disc(BtnFire(w, h), kBtnR, new Color(1f, 0.45f, 0.35f, 0.6f), "Punch");
        if (useTarget != null && !riding) Disc(BtnUse(w, h), kBtnR, new Color(1f, 0.85f, 0.3f, 0.7f), "Use");
        if (heroNoclip) Disc(BtnDrunk(w, h), kBtnR, hDownId != kNone ? lit : knob * new Color(1f, 1f, 1f, 0.6f), "Down");
        else Disc(BtnDrunk(w, h), kBtnR, heroDrunk ? lit : knob * new Color(1f, 1f, 1f, 0.6f), "Drunk");
        if (heroAim)
        {
            Disc(BtnFire(w, h), kBtnR, new Color(1f, 0.45f, 0.35f, 0.6f), "Fire");
            Disc(new Vector2(w * 0.5f, h * 0.5f), 4f, new Color(1f, 1f, 1f, 0.9f)); // where the shot goes
        }
        RadialGui(w, h);
        string what = hero.Killed ? "dead (Reset)" : hero.Rising ? "getting up" : heroMode == 2 ? "drunk" : heroMode == 1 ? "knocked" : kGuns[weapon % kGuns.Length].Label;
        if (what != "") GUI.Label(new Rect(w * 0.5f - 90f, 60f, 180f, 24f), what, new GUIStyle(chip) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold });
        HealthBar(w, hero);
    }

    // His health under the status: green to red, white for a moment when he's hurt; "∞" when he can't die
    void HealthBar(float w, Man m)
    {
        if (m == null) return;
        var r = new Rect(w * 0.5f - 90f, 88f, 180f, 12f);
        var back = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.45f);
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        bool forever = Immortal(m);
        float f = forever ? 1f : Mathf.Clamp01(m.Health / 200f);
        var c = forever ? new Color(0.35f, 0.75f, 1f, 0.9f) : Color.Lerp(new Color(0.9f, 0.2f, 0.15f, 0.9f), new Color(0.3f, 0.85f, 0.35f, 0.9f), f);
        if (Time.time - m.HurtAt < 0.15f) c = Color.white;
        GUI.color = c;
        GUI.DrawTexture(new Rect(r.x + 1f, r.y + 1f, (r.width - 2f) * f, r.height - 2f), Texture2D.whiteTexture);
        GUI.color = back;
        GUI.Label(new Rect(r.x, r.y + 11f, r.width, 18f), forever ? "HP ∞" : "HP " + Mathf.CeilToInt(m.Health), new GUIStyle(chip) { alignment = TextAnchor.MiddleCenter, fontSize = 11 });
    }

    // ------------------------------------------------------------------ GTA V's starts, stops and turns
    // (GtaAnim.V: Grand Theft Gmod's clips) Pushing off from standing plays the start for the way he's to go (left
    // or right foot, straight, 90 or 180 degrees round), letting go while walking or running the stop, pulling
    // back at a run the 180 turn: each clip moves and turns him itself (its root's way from the clip data, its
    // turn bent to the stick's way), up to its blend-out mark; then the loop takes over on the same foot.
    GtaAnim.Clip act;
    int actKind;              // 1 start, 2 stop, 3 turn
    float actT, actYaw, actOff, actEnd, hNextPhase = -1f;
    Vector3 actPos;
    bool actRun, startLeft, actStill;   // actStill: a climb's end that leaves him standing
    float actNextAt, actSpeed;
    float hStepPhase = -1f;
    GtaAnim.Clip hStepClip;

    bool VAct(float dt, float mag, Vector3 wish, float walkSp, float runSp)
    {
        bool landed = heroLanded;
        heroLanded = false;
        if (!GtaAnim.V || heroAim || !heroGround || heroNoclip || Time.time < heroSpotUntil || Time.time < heroFaceUntil)
        {
            act = null;
            return false;
        }
        Vector3 flat = new Vector3(heroVel.x, 0f, heroVel.z);
        float speed = flat.magnitude;
        bool push = mag >= 0.12f;
        Vector3 fwd = Quaternion.Euler(0f, heroYaw, 0f) * Vector3.forward;
        if (act == null && Time.time < actNextAt && !landed) return false; // a moment between two (no turn after turn)
        if (act == null)
        {
            if (landed && heroLandAir > 0.3f && GtaAnim.VLand[0] != null)
            {
                // down from a jump or a fall: still going, the landing that runs on; stopped, the one that stops
                bool on = push && speed > 1f;
                var set = on ? GtaAnim.VLand : GtaAnim.VLandStop;
                if (!Begin(set[(startLeft = !startLeft) ? 0 : 1], on ? 4 : 2, 0f)) return false;
                actRun = speed > (walkSp + runSp) * 0.5f;
                actSpeed = speed;
            }
            else if (push && speed < 0.4f)
            {
                // the way to go from the way he faces (right +), and the start that turns most like it
                float a = Vector3.SignedAngle(fwd, wish, Vector3.up);
                bool run = heroTier >= 2;
                var set = run ? GtaAnim.VRStart : GtaAnim.VWStart;
                int i = Mathf.Abs(a) < 45f ? ((startLeft = !startLeft) ? 0 : 3) : a < 0f ? (a > -135f ? 1 : 2) : (a < 135f ? 4 : 5);
                if (!Begin(set[i], 1, -a)) return false;
                actRun = run;
            }
            else if (!push && speed > 1f && hCur != null && hCur.Cycle && hCur.Tags != null)
            {
                // on the foot that's next down: the last heel down was the other one
                bool run = speed > (walkSp + runSp) * 0.5f;
                int side = GtaAnim.LastHeel(hCur, GtaAnim.At(hCur, hPhase), out _);
                var set = run ? GtaAnim.VRStop : GtaAnim.VWStop;
                if (!Begin(set[side == 2 ? 0 : 1], 2, 0f)) return false;
            }
            else if (push && speed > runSp * 0.75f && Vector3.Angle(flat, wish) > 150f && Vector3.Angle(fwd, wish) > 150f)
            {
                float a = Vector3.SignedAngle(fwd, wish, Vector3.up);
                if (!Begin(GtaAnim.VRunTurn[a < 0f ? 0 : 1], 3, -a)) return false;
                actRun = true;
            }
            else return false;
        }
        // let go of a start, pushed during a stop: back to the plain moves
        if ((actKind == 1 && !push) || (actKind == 2 && push) || (actKind == 5 && actStill && push && actT / act.Dur > 0.45f)) { act = null; actNextAt = Time.time + 0.25f; return false; }
        float ph0 = actT / act.Dur;
        actT += dt;
        float ph = actT / act.Dur;
        if (ph >= actEnd)
        {
            Finish();
            actNextAt = Time.time + 0.35f;
            return false;
        }
        // steered by the stick as it goes (a start, a landing, a turn, a climb's walk or run on): the clip's way bent
        // round to the stick's, quickly enough to be there by its end
        bool steer = push && (actKind == 1 || actKind == 3 || actKind == 4 || (actKind == 5 && !actStill));
        float wishYaw = Mathf.Atan2(wish.x, wish.z) * Mathf.Rad2Deg;
        var m = GtaAnim.Motion(act, ph);
        if (actKind == 4 && m.sqrMagnitude < 1e-6f)
        {
            // a landing without its own way along the ground: on at the speed he came down with, easing off a little
            if (steer) heroYaw = Mathf.MoveTowardsAngle(heroYaw, wishYaw, 320f * dt);
            heroVel = Quaternion.Euler(0f, heroYaw, 0f) * Vector3.forward * Mathf.Max(actSpeed * Mathf.Lerp(1f, 0.85f, ph), 1f);
            return true;
        }
        if (steer)
        {
            float endTurn = GtaAnim.Motion(act, actEnd).z;
            float target = Mathf.DeltaAngle(actYaw - endTurn, wishYaw);
            float miss = Mathf.Abs(Mathf.DeltaAngle(actOff, target));
            // pushed right round the other way in a start: let it go, the plain moves turn him
            if (actKind == 1 && miss > 110f) { act = null; actNextAt = Time.time + 0.1f; return false; }
            float remain = Mathf.Max((actEnd - ph) * act.Dur, 0.08f);
            actOff = Mathf.MoveTowardsAngle(actOff, target, Mathf.Max(miss / remain, actKind == 1 ? 160f : 300f) * dt);
        }
        // its root's step this frame (the clip's own frame, where it started) turned by the steering
        Vector3 d = m - GtaAnim.Motion(act, ph0);
        Vector3 v = Quaternion.Euler(0f, actYaw + actOff, 0f) * new Vector3(-d.y, 0f, d.x) / Mathf.Max(dt, 1e-3f);
        heroVel = Vector3.ClampMagnitude(v, 9f);
        heroYaw = actYaw + actOff - m.z;
        return true;
    }

    bool Begin(GtaAnim.Clip c, int kind, float leftTurn)
    {
        if (c == null || !c.Ok || c.Curve == null) return false;
        act = c;
        actKind = kind;
        actT = 0f;
        actPos = heroPos;
        actYaw = heroYaw;
        // the turn the stick wants beyond the clip's own (VAct bends it round over the clip, and on as it moves)
        actOff = 0f;
        float b = GtaAnim.Mark(c, 3);
        actEnd = b > 0.05f ? b : 1f;
        return true;
    }

    // its end: a stop stands him still; a start or a turn hands over to the walk or run on the foot it's on
    void Finish()
    {
        var done = act;
        act = null;
        float ph = actEnd;
        if (actKind == 2 || (actKind == 5 && actStill))
        {
            heroVel = Vector3.zero;
            return;
        }
        var loop = GtaAnim.Move[actRun ? 2 : 1];
        if (loop == null || !loop.Ok) return;
        int side = GtaAnim.LastHeel(done, ph, out float at);
        float since = (ph - at) * done.Dur;
        float heel = side > 0 ? GtaAnim.Mark(loop, side) : -1f;
        if (heel >= 0f)
        {
            float lp = heel + since / loop.Dur;
            lp -= loop.Sync; // Step's phase is the clip's less its sync (GtaAnim.At)
            hNextPhase = lp - Mathf.Floor(lp);
        }
        // on at the loop's own pace, the way he faces
        Vector3 f = Quaternion.Euler(0f, heroYaw, 0f) * Vector3.forward;
        heroVel = f * Mathf.Max(heroVel.magnitude, loop.Speed * 0.8f);
    }

    // A footstep as a GTA V clip's heel comes down (its marks), whatever clip it is
    void FootSteps(GtaAnim.Clip c)
    {
        if (c == null || c.Tags == null || !heroGround) { hStepClip = null; return; }
        float p = GtaAnim.At(c, hPhase);
        if (c == hStepClip && hStepPhase >= 0f && p != hStepPhase)
        {
            float a = hStepPhase, b = p;
            bool wrapped = b < a;
            for (int i = 0; i + 1 < c.Tags.Length; i += 2)
            {
                int k = (int)c.Tags[i];
                if (k != 1 && k != 2) continue;
                float t = c.Tags[i + 1];
                bool crossed = wrapped ? (t > a || t <= b) : (t > a && t <= b);
                if (crossed && (c.Cycle ? heroVel.magnitude > 0.3f : true))
                {
                    Play(Sfx.Step, heroPos, heroTier == 3 ? 0.9f : heroTier == 2 ? 0.7f : 0.45f);
                    break;
                }
            }
        }
        hStepClip = c;
        hStepPhase = p;
    }
}
