// A GTA IV motorbike for the hero, read from your own game when you ask for one: its model and textures out of
// vehicles.img, its handling.dat numbers out of common.rpf (delirium_bike, in native/bike). The ride is simulated
// there with GTA IV's own gearbox, springs and tyres (Delirium's port of the game's code) and a balance after
// handling.dat's bike line; Unity gives it the ground under each wheel (a ray) and shows the result. The rider sits
// on GTA IV's own clips (veh@bike_*: sit_drive, steer_l / steer_r, steer_fwd / steer_bwd, still, reverse); a hard
// hit or a spill throws him off onto Delirium's ragdoll, the bike falls on Unity's physics.
//
// GTA's frame (x right, y forward, z up) is Unity's (x, z, y); a rotation (x, y, z, w) there is (-x, -z, -y, w) here.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Delirium.Ragdoll;
using UnityEngine;

public partial class Game
{
#if UNITY_IOS && !UNITY_EDITOR
    const string BikeDll = "__Internal";
#else
    const string BikeDll = "delirium_bike";
#endif
    [DllImport(BikeDll)] static extern int db_open(byte[] dir);
    [DllImport(BikeDll)] static extern int db_error(byte[] buf, int len);
    [DllImport(BikeDll)] static extern int db_list(byte[] buf, int len);
    [DllImport(BikeDll)] static extern int db_spawn(byte[] model, float[] paint, out int glbLen);
    [DllImport(BikeDll)] static extern int db_glb(int h, byte[] o, int len);
    [DllImport(BikeDll)] static extern int db_info(int h, float[] o);
    [DllImport(BikeDll)] static extern int db_place(int h, float[] pos, float[] rot, float[] vel);
    [DllImport(BikeDll)] static extern int db_step(int h, float dt, float[] input, float[] ground, float[] o);
    [DllImport(BikeDll)] static extern int db_drop(int h);
    [DllImport(BikeDll)] static extern int db_nudge(int h, float[] pos, float[] vel);

    // ------------------------------------------------------------------ state
    static volatile int bikeState;          // 0 not opened, 1 opening, 2 ready, -1 failed
    static string bikeError = "";
    static string[] bikeModels = new string[0], bikeDicts = new string[0];
    int bikeKind;                           // which of bikeModels the next one is
    // a spawn being built off the main thread
    volatile bool bikeBuilding;
    byte[] bikeGlbReady;
    int bikeHandleReady = -1;
    string bikeBuildError;

    int bikeH = -1;                         // the simulated bike
    GameObject bikeGo;
    string bikeModel = "", bikeDict = "";
    readonly float[] bikeInfo = new float[24], bikeOut = new float[32], bikeIn = new float[5], bikeGround = new float[14];
    Transform bForks, bForksL, bWheelF, bWheelR, bSwing, bSeat;
    Quaternion bForksRest, bWheelFRest, bWheelRRest, bSwingRest;
    Vector3 bForksLRest;
    float bSwingLen = 0.57f;
    Bounds bikeBounds;
    BoxCollider bikeBox;
    Rigidbody bikeWreck;                    // fallen over: Unity's physics has it till it's ridden again
    // riding
    bool riding;
    float rideBlend;                        // getting on: 0 -> 1 over a third of a second
    Quaternion[] rideFrom;
    Vector3 rideFromHips;
    Quaternion rideFromHipsRot;
    float rideSteer, rideLean, ridePhase, rideStill, rideCamIdle;
    float bikeSpeed;
    int bikeGear;
    readonly List<Collider> bikeOwn = new List<Collider>();

    static string BikeErr()
    {
        var b = new byte[512];
        try { db_error(b, b.Length); } catch (Exception) { }
        int n = Array.IndexOf(b, (byte)0);
        return Encoding.UTF8.GetString(b, 0, n < 0 ? b.Length : n);
    }

    static byte[] Zs(string s) => Encoding.UTF8.GetBytes(s + "\0");

    // Your game's bikes, opened on a thread (common.rpf and vehicles.img's table).
    void BikeOpen()
    {
        if (bikeState == 1 || bikeState == 2) return;
        bikeState = 1;
        bikeError = "";
        string exeDir = Directory.Exists(gameExe) ? gameExe : Path.GetDirectoryName(gameExe);
        var folders = new[] { exeDir, @"D:\Gta IV", @"D:\delirium repo\delirium-playroom\game", Application.persistentDataPath };
        var t = new Thread(() =>
        {
            try
            {
                string why = "";
                bool open = false;
                foreach (var f in folders)
                {
                    if (string.IsNullOrEmpty(f) || !Directory.Exists(f)) continue;
                    if (db_open(Zs(f)) >= 0) { open = true; break; }
                    why += BikeErr() + "\n";
                }
                if (!open)
                {
                    bikeError = why != "" ? why.Trim() : "no GTA IV folder";
                    bikeState = -1;
                    return;
                }
                var buf = new byte[4096];
                int n = db_list(buf, buf.Length);
                int z = Array.IndexOf(buf, (byte)0);
                var list = n > 0 ? Encoding.UTF8.GetString(buf, 0, z < 0 ? buf.Length : z).Split(',') : new string[0];
                var models = new List<string>();
                var dicts = new List<string>();
                foreach (var e in list)
                {
                    int c = e.IndexOf(':');
                    if (c <= 0) continue;
                    models.Add(e.Substring(0, c));
                    dicts.Add(e.Substring(c + 1));
                }
                bikeModels = models.ToArray();
                bikeDicts = dicts.ToArray();
                if (bikeModels.Length == 0) { bikeError = "no bikes in your game's vehicles.img"; bikeState = -1; return; }
                bikeState = 2;
            }
            catch (DllNotFoundException)
            {
                bikeError = "the bikes are in the iPhone build (delirium_bike isn't built for this computer)";
                bikeState = -1;
            }
            catch (Exception e)
            {
                bikeError = e.Message;
                bikeState = -1;
            }
        }) { IsBackground = true };
        t.Start();
    }

    // The colours a bike comes in (paint 1, paint 2)
    static readonly Color[][] kBikePaint =
    {
        new[] { new Color(0.62f, 0.04f, 0.04f), new Color(0.05f, 0.05f, 0.06f) },
        new[] { new Color(0.08f, 0.2f, 0.55f), new Color(0.85f, 0.85f, 0.87f) },
        new[] { new Color(0.1f, 0.45f, 0.12f), new Color(0.05f, 0.05f, 0.06f) },
        new[] { new Color(0.9f, 0.75f, 0.1f), new Color(0.06f, 0.06f, 0.07f) },
        new[] { new Color(0.06f, 0.06f, 0.07f), new Color(0.6f, 0.6f, 0.62f) },
        new[] { new Color(0.85f, 0.85f, 0.87f), new Color(0.62f, 0.04f, 0.04f) },
    };

    /// The top bar's Bike button: the next of your game's bikes in front of the hero.
    void BikeButton()
    {
        if (hero == null) return;
        if (bikeState == 0 || bikeState == -1) { BikeOpen(); note = "Bikes: opening your GTA IV's vehicles..."; return; }
        if (bikeState == 1 || bikeBuilding) return;
        if (riding) BikeOff(false);
        string model = bikeModels[bikeKind % bikeModels.Length];
        string dict = bikeDicts[bikeKind % bikeDicts.Length];
        bikeKind++;
        var paint = kBikePaint[UnityEngine.Random.Range(0, kBikePaint.Length)];
        var p = new[] { paint[0].r, paint[0].g, paint[0].b, paint[1].r, paint[1].g, paint[1].b };
        bikeBuilding = true;
        bikeBuildError = null;
        note = "Bike: " + model + "...";
        var t = new Thread(() =>
        {
            try
            {
                int h = db_spawn(Zs(model), p, out int len);
                if (h < 0) { bikeBuildError = BikeErr(); return; }
                var data = new byte[len];
                db_glb(h, data, len);
                bikeHandleReady = h;
                bikeGlbReady = data;
            }
            catch (Exception e) { bikeBuildError = e.Message; }
            finally { bikeBuilding = false; }
        }) { IsBackground = true };
        bikeModel = model;
        bikeDict = dict;
        t.Start();
    }

    // A built bike onto the stage (the main thread's part).
    void BikeArrive()
    {
        if (bikeBuildError != null) { note = "Bike: " + bikeBuildError; bikeBuildError = null; return; }
        if (bikeGlbReady == null) return;
        var data = bikeGlbReady;
        int h = bikeHandleReady;
        bikeGlbReady = null;
        BikeRemove();
        GlbScene.Made made;
        try { made = GlbScene.Load(data, "Bike " + bikeModel, ModelMaterial()); }
        catch (Exception e) { note = "Bike: its model didn't load: " + e.Message; db_drop(h); return; }
        bikeH = h;
        bikeGo = made.Root;
        db_info(h, bikeInfo);
        var all = bikeGo.GetComponentsInChildren<Transform>(true);
        Func<string, Transform> Find = n => Array.Find(all, x => string.Equals(x.name, n, StringComparison.OrdinalIgnoreCase));
        bForks = Find("forks_u");
        bForksL = Find("forks_l");
        bWheelF = Find("wheel_lf") ?? Find("wheel_rf");
        bWheelR = Find("wheel_lr") ?? Find("wheel_rr");
        bSwing = Find("swingarm");
        bSeat = Find("seat_f") ?? Find("rider");
        if (bForks != null) bForksRest = bForks.localRotation;
        if (bForksL != null) bForksLRest = bForksL.localPosition;
        if (bWheelF != null) bWheelFRest = bWheelF.localRotation;
        if (bWheelR != null) bWheelRRest = bWheelR.localRotation;
        if (bSwing != null)
        {
            bSwingRest = bSwing.localRotation;
            if (bWheelR != null) bSwingLen = Mathf.Max(0.2f, bWheelR.localPosition.magnitude);
        }
        // its body's box, over the wheels (for what it runs into); the hero's own capsule casts meet it too
        var rs = bikeGo.GetComponentsInChildren<Renderer>();
        bikeBounds = new Bounds(Vector3.zero, Vector3.zero);
        bool any = false;
        foreach (var r in rs)
        {
            var b = r.bounds;
            if (!any) { bikeBounds = b; any = true; } else bikeBounds.Encapsulate(b);
        }
        bikeBox = bikeGo.AddComponent<BoxCollider>();
        bikeBox.center = bikeBounds.center + Vector3.up * 0.12f;
        bikeBox.size = new Vector3(Mathf.Max(0.3f, bikeBounds.size.x * 0.7f), Mathf.Max(0.3f, bikeBounds.size.y - 0.3f), bikeBounds.size.z * 0.92f);
        // (below the seat: the rider thrown off doesn't start inside it)
        if (bSeat != null)
        {
            float top = bikeGo.transform.InverseTransformPoint(bSeat.position).y - 0.08f;
            float bottom = bikeBox.center.y - bikeBox.size.y * 0.5f;
            if (top > bottom + 0.2f) { bikeBox.center = new Vector3(bikeBox.center.x, (top + bottom) * 0.5f, bikeBox.center.z); bikeBox.size = new Vector3(bikeBox.size.x, top - bottom, bikeBox.size.z); }
        }
        bikeOwn.Clear();
        bikeOwn.AddRange(bikeGo.GetComponentsInChildren<Collider>(true));
        // never part of the map to Delirium (a ragdoll keeps the level it read: a copy would stay where it was parked)
        DeliriumRagdolls.SandboxNotLevel.Add(bikeGo.transform);
        // to Delirium a car: whoever it runs into gets GTA IV's hit by a vehicle
        try { nm.RegisterMover(bikeGo.transform, DeliriumRagdolls.MoverKind.Car, Vector3.zero, true); } catch (Exception) { }
        // in front of the hero, facing his way
        Vector3 fwd = Quaternion.Euler(0f, heroYaw, 0f) * Vector3.forward;
        Vector3 at = heroPos + fwd * 2.8f + Quaternion.Euler(0f, heroYaw, 0f) * Vector3.right * 0.9f;
        if (Physics.Raycast(at + Vector3.up * 3f, Vector3.down, out var hit, 20f)) at.y = hit.point.y;
        float lift = -bikeInfo[2] + bikeInfo[3] + 0.02f; // the front wheel's bottom on the ground
        BikePlace(at + Vector3.up * lift, Quaternion.Euler(0f, heroYaw, 0f), Vector3.zero);
        note = "";
    }

    void BikeRemove()
    {
        if (riding) BikeOff(false);
        if (bikeGo != null) { try { nm.UnregisterCar(bikeGo.transform); nm.NotMovers.Remove(bikeGo.transform); DeliriumRagdolls.SandboxNotLevel.Remove(bikeGo.transform); } catch (Exception) { } Destroy(bikeGo); }
        bikeGo = null;
        bikeWreck = null;
        if (bikeH >= 0) { try { db_drop(bikeH); } catch (Exception) { } }
        bikeH = -1;
    }

    // ------------------------------------------------------------------ frames: Unity <-> GTA
    static void ToGta(Vector3 u, float[] a, int at) { a[at] = u.x; a[at + 1] = u.z; a[at + 2] = u.y; }
    static Vector3 FromGta(float[] a, int at) => new Vector3(a[at], a[at + 2], a[at + 1]);
    static Quaternion RotFromGta(float[] a, int at) => new Quaternion(-a[at], -a[at + 2], -a[at + 1], a[at + 3]);
    static void RotToGta(Quaternion q, float[] a, int at) { a[at] = -q.x; a[at + 1] = -q.z; a[at + 2] = -q.y; a[at + 3] = q.w; }

    void BikePlace(Vector3 pos, Quaternion rot, Vector3 vel)
    {
        if (bikeH < 0) return;
        var p = new float[3];
        var r = new float[4];
        var v = new float[3];
        ToGta(pos, p, 0);
        RotToGta(rot, r, 0);
        ToGta(vel, v, 0);
        db_place(bikeH, p, r, v);
        bikeGo.transform.SetPositionAndRotation(pos, rot);
    }

    // The ground under one wheel: a ray down the bike from its spring's top (not the bike, the rider, a man).
    readonly RaycastHit[] bikeHits = new RaycastHit[12];

    // The ground under one wheel: a ball (most of the wheel's size) cast down the bike from its spring's top, so the
    // wheel rolls up a step's edge instead of popping onto it (not the bike, the rider, a man).
    void BikeRay(int wheel, Transform t)
    {
        int k = wheel * 7;
        int w = wheel * 4;
        float r = bikeInfo[w + 3];
        float rs = r * 0.7f;
        // the wheel's place in the bike's frame (GTA's), the spring's top above it
        Vector3 top = t.TransformPoint(new Vector3(bikeInfo[w], bikeInfo[w + 2] + bikeInfo[10], bikeInfo[w + 1]));
        Vector3 centre = t.TransformPoint(new Vector3(bikeInfo[w], bikeInfo[w + 2], bikeInfo[w + 1]));
        Vector3 down = -t.up;
        float reach = bikeInfo[8] + 0.6f;
        Vector3 from = top - down * 0.3f;
        int n = Physics.SphereCastNonAlloc(from, rs, down, bikeHits, reach + 0.3f, ~0, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue;
        int pick = -1;
        for (int i = 0; i < n; i++)
        {
            var c = bikeHits[i].collider;
            if (bikeOwn.Contains(c)) continue;
            if (hero != null && c.transform.IsChildOf(hero.Body.transform)) continue;
            if (c.attachedRigidbody != null && !c.attachedRigidbody.isKinematic) continue; // a ragdoll's part, a crate
            if (bikeHits[i].distance <= 0f) continue; // the ball already in something at its start: not under it
            // higher than most of the wheel (a wall, a raised bed's side): not something it rolls up; the body meets it
            float rise = Vector3.Dot(from + down * (bikeHits[i].distance + rs) - centre, -down);
            if (rise > -0.15f * r) continue;
            if (bikeHits[i].distance < best) { best = bikeHits[i].distance; pick = i; }
        }
        if (pick < 0) { bikeGround[k] = 0f; return; }
        // the wheel's lowest point where the ball stopped; the ground's slope there, a step's edge leaned toward up
        Vector3 contact = from + down * (best + rs);
        Vector3 nrm = bikeHits[pick].normal;
        if (Vector3.Dot(nrm, -down) < 0.5f) nrm = (nrm - down).normalized;
        bikeGround[k] = 1f;
        ToGta(contact, bikeGround, k + 1);
        ToGta(nrm, bikeGround, k + 4);
    }

    // ------------------------------------------------------------------ every frame
    void BikeUpdate(float dt)
    {
        if (bikeGlbReady != null || bikeBuildError != null) BikeArrive();
        if (bikeState == -1 && note.StartsWith("Bikes: opening")) note = "Bikes: " + bikeError;
        if (bikeState == 2 && note.StartsWith("Bikes: opening")) { note = ""; BikeButton(); }
        if (bikeH < 0 || bikeGo == null) return;
        if (bikeClearAt > 0f && Time.time > bikeClearAt) { nm.NotMovers.Remove(bikeGo.transform); bikeClearAt = 0f; }
        if (bikeWreck != null) return; // on Unity's physics till it's picked up
        var t = bikeGo.transform;
        // the controls: the arrows on the screen (or the keys)
        if (!Has(bkL)) bkL = kNone;
        if (!Has(bkR)) bkR = kNone;
        if (!Has(bkGas)) bkGas = kNone;
        if (!Has(bkBrake)) bkBrake = kNone;
        if (!Has(bkFwd)) bkFwd = kNone;
        if (!Has(bkBack)) bkBack = kNone;
        float thr = 0f, brk = 0f, steer = 0f, lean = 0f;
        if (riding && rideBlend >= 1f && !hero.Killed)
        {
            thr = bkGas != kNone || Input.GetKey(KeyCode.W) ? 1f : 0f;
            brk = bkBrake != kNone || Input.GetKey(KeyCode.S) ? 1f : 0f;
            steer = (bkR != kNone || Input.GetKey(KeyCode.D) ? 1f : 0f) - (bkL != kNone || Input.GetKey(KeyCode.A) ? 1f : 0f);
            lean = (bkFwd != kNone || Input.GetKey(KeyCode.UpArrow) ? 1f : 0f) - (bkBack != kNone || Input.GetKey(KeyCode.DownArrow) ? 1f : 0f);
        }
        else brk = riding ? 0f : 0.3f; // left alone: its brake on
        rideSteer = Mathf.MoveTowards(rideSteer, steer, dt * 4f);
        rideLean = Mathf.MoveTowards(rideLean, lean, dt * 4f);
        bikeIn[0] = thr; bikeIn[1] = brk; bikeIn[2] = steer; bikeIn[3] = lean; bikeIn[4] = 0f;
        BikeRay(0, t);
        BikeRay(1, t);
        Vector3 before = t.position;
        Quaternion beforeRot = t.rotation;
        if (db_step(bikeH, Mathf.Min(dt, 0.05f), bikeIn, bikeGround, bikeOut) < 0) { note = "Bike: " + BikeErr(); return; }
        Vector3 pos = FromGta(bikeOut, 0);
        Quaternion rot = RotFromGta(bikeOut, 3);
        Vector3 vel = FromGta(bikeOut, 7);
        // never a broken number into Unity's physics (it took everything's collisions with it): back where it was
        if (!Finite(pos) || !Finite(vel) || float.IsNaN(rot.x) || float.IsNaN(rot.w) || Mathf.Abs(rot.x * rot.x + rot.y * rot.y + rot.z * rot.z + rot.w * rot.w - 1f) > 0.1f)
        {
            BikePlace(before, beforeRot, Vector3.zero);
            return;
        }
        Vector3 velIn = vel; // before what it hits takes any of it
        bikeSpeed = bikeOut[16];
        bikeGear = (int)bikeOut[17];
        bool fallen = bikeOut[25] > 0.5f;
        // what it runs into: pushed back out of it; hard enough, a crash
        t.SetPositionAndRotation(pos, rot);
        Physics.SyncTransforms();
        float hitSpeed = 0f;
        bool pushed = false;
        Vector3 hitNormal = Vector3.zero;
        var near = Physics.OverlapBox(t.TransformPoint(bikeBox.center), bikeBox.size * 0.5f, rot, ~0, QueryTriggerInteraction.Ignore);
        foreach (var c in near)
        {
            if (bikeOwn.Contains(c) || c.isTrigger) continue;
            if (hero != null && c.transform.IsChildOf(hero.Body.transform)) continue;
            var man = ManOf(c);
            if (man != null)
            {
                // a man in the way: knocked flying (GTA IV's bike into a pedestrian)
                if (vel.magnitude > 2f && !man.Rising)
                {
                    nm.Ragdoll(man.Body.transform, vel * 8f + Vector3.up * vel.magnitude * 3f);
                    Play(Sfx.Punch, man.Body.Hips.position, 0.8f);
                }
                continue;
            }
            if (c.attachedRigidbody != null && !c.attachedRigidbody.isKinematic)
            {
                c.attachedRigidbody.AddForceAtPosition(vel * 40f, c.ClosestPoint(t.position), ForceMode.Impulse);
                continue;
            }
            if (Physics.ComputePenetration(bikeBox, t.position, rot, c, c.transform.position, c.transform.rotation, out Vector3 dir, out float dist))
            {
                // the ground under it (its springs bottomed out on a landing, a step): the wheels' business, just
                // not through it; a wall: pushed out, and how hard it went in
                bool ground = dir.y > 0.6f;
                pos += dir * Mathf.Min(dist, 0.3f);
                pushed = true;
                float into = -Vector3.Dot(vel, dir);
                if (into > 0f)
                {
                    if (!ground && into > hitSpeed) { hitSpeed = into; hitNormal = dir; }
                    vel += dir * into * (ground ? 1f : 1.2f);
                }
            }
        }
        if (pushed)
        {
            var p = new float[3];
            var v = new float[3];
            ToGta(pos, p, 0);
            ToGta(vel, v, 0);
            db_nudge(bikeH, p, v);
            if (hitSpeed > 3f) Play(Sfx.Land, pos, Mathf.Clamp01(hitSpeed / 10f));
        }
        t.SetPositionAndRotation(pos, rot);
        BikeLook();
        if (riding && rideBlend >= 1f && (fallen || hitSpeed > 7f)) BikeCrash(velIn, vel, hitNormal);
        else if (fallen) BikeFall(vel);
    }

    static bool Finite(Vector3 v) => !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z) && !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);

    Man ManOf(Collider c)
    {
        foreach (var m in men)
            if (m != hero && m.Body != null && c.transform.IsChildOf(m.Body.transform)) return m;
        return null;
    }

    // Its parts as the ride has them: the bars turned, the wheels rolling, the springs in.
    void BikeLook()
    {
        float steerDeg = bikeOut[13] * Mathf.Rad2Deg;
        if (bForks != null) bForks.localRotation = bForksRest * Quaternion.AngleAxis(steerDeg, Vector3.up);
        float rest = bikeInfo[9];
        if (bForksL != null) bForksL.localPosition = bForksLRest + Vector3.up * Mathf.Clamp(bikeOut[19] - rest, -0.15f, 0.2f);
        if (bWheelF != null) bWheelF.localRotation = bWheelFRest * Quaternion.AngleAxis(bikeOut[21] * Mathf.Rad2Deg, Vector3.right);
        if (bSwing != null)
        {
            float a = Mathf.Asin(Mathf.Clamp((bikeOut[20] - rest) / bSwingLen, -0.6f, 0.6f)) * Mathf.Rad2Deg;
            bSwing.localRotation = bSwingRest * Quaternion.AngleAxis(a, Vector3.right);
        }
        if (bWheelR != null) bWheelR.localRotation = bWheelRRest * Quaternion.AngleAxis(bikeOut[22] * Mathf.Rad2Deg, Vector3.right);
    }

    // Over on its side with nobody on it: Unity's physics has it.
    void BikeFall(Vector3 vel) => BikeFall(vel, Vector3.zero);

    void BikeFall(Vector3 vel, Vector3 spin)
    {
        if (bikeWreck != null) return;
        try { nm.UnregisterCar(bikeGo.transform); } catch (Exception) { }
        bikeWreck = bikeGo.AddComponent<Rigidbody>();
        bikeWreck.mass = Mathf.Max(80f, bikeInfo[11]);
        bikeWreck.linearVelocity = vel;
        bikeWreck.angularVelocity = new Vector3(bikeOut[10], bikeOut[12], bikeOut[11]) * -1f + spin;
        bikeWreck.interpolation = RigidbodyInterpolation.Interpolate;
    }

    // ------------------------------------------------------------------ on and off
    bool NearBike() => bikeGo != null && hero != null && heroMode == 0 && !riding && Vector3.Distance(heroPos, bikeGo.transform.position) < 3.2f;

    void BikeOn()
    {
        if (bikeGo == null || hero == null || heroMode != 0 || riding) return;
        // a fallen bike stood up where it lies, facing its way
        if (bikeWreck != null)
        {
            Destroy(bikeWreck);
            bikeWreck = null;
            Vector3 f = Vector3.ProjectOnPlane(bikeGo.transform.forward, Vector3.up);
            if (f.sqrMagnitude < 0.01f) f = Vector3.ProjectOnPlane(bikeGo.transform.up, Vector3.up);
            Vector3 at = bikeGo.transform.position;
            if (Physics.Raycast(at + Vector3.up * 2f, Vector3.down, out var hit, 10f, ~0, QueryTriggerInteraction.Ignore)) at.y = hit.point.y;
            BikePlace(at + Vector3.up * (-bikeInfo[2] + bikeInfo[3] + 0.05f), Quaternion.LookRotation(f.normalized, Vector3.up), Vector3.zero);
        }
        try { nm.RegisterMover(bikeGo.transform, DeliriumRagdolls.MoverKind.Car, Vector3.zero, true); } catch (Exception) { }
        riding = true;
        rideBlend = 0f;
        // on the bike, part of it: Delirium's car knocks down whoever's in its way but its own
        heroParentWas = hero.Body.transform.parent;
        hero.Body.transform.SetParent(bikeGo.transform, true);
        heroAim = false;
        heroMode = 3;
        var body = hero.Body;
        if (rideFrom == null || rideFrom.Length != body.Bones.Length) rideFrom = new Quaternion[body.Bones.Length];
        for (int i = 0; i < body.Bones.Length; i++) rideFrom[i] = body.Bones[i].localRotation;
        rideFromHips = body.Hips.position;
        rideFromHipsRot = body.Hips.rotation;
        hCamYaw = bikeGo.transform.eulerAngles.y;
        rideCamIdle = 9f;
        Play(Sfx.Land, heroPos, 0.4f);
    }

    // Off on the left (GTA's driver side), standing.
    Transform heroParentWas;
    float bikeClearAt;

    // The hero's body back where it lived (not the bike's any more).
    void Unseat()
    {
        if (hero != null && bikeGo != null && hero.Body.transform.parent == bikeGo.transform)
            hero.Body.transform.SetParent(heroParentWas, true);
    }

    void BikeOff(bool thrown)
    {
        if (!riding) return;
        riding = false;
        Unseat();
        if (thrown || hero == null) return;
        var t = bikeGo.transform;
        Vector3 side = -Vector3.ProjectOnPlane(t.right, Vector3.up).normalized;
        Vector3 at = t.position + side * 0.75f;
        if (Physics.Raycast(at + Vector3.up * 2f, Vector3.down, out var hit, 10f, ~0, QueryTriggerInteraction.Ignore)) at.y = hit.point.y;
        HeroPlace(at, t.eulerAngles.y);
        heroVel = Vector3.ProjectOnPlane(FromGta(bikeOut, 7), Vector3.up) * 0.3f;
        hHand = true; // the riding pose faded over into his own
        hHandT = 0f;
        var body = hero.Body;
        hHandPos = body.Hips.position;
        hHandRot = body.Hips.rotation;
        if (hHandLocal == null || hHandLocal.Length != body.Bones.Length) hHandLocal = new Quaternion[body.Bones.Length];
        for (int i = 0; i < body.Bones.Length; i++) hHandLocal[i] = body.Bones[i].localRotation;
    }

    // A hard hit or a spill: the rider onto Delirium's ragdoll going the way the bike was, the bike onto Unity's.
    // `velIn`: how it was going before the hit (the rider keeps it: over the bars, GTA IV's way); `vel`: the bike's
    // after the wall took its share
    void BikeCrash(Vector3 velIn, Vector3 vel, Vector3 hitNormal)
    {
        // not a car to Delirium any more before the rider is its ragdoll (its box would be in his world)
        try { nm.UnregisterCar(bikeGo.transform); } catch (Exception) { }
        float speed = velIn.magnitude;
        // the bike first: a loose body (and kept out of his ragdoll's world till he's clear of it), then him
        var bt = bikeGo.transform;
        nm.NotMovers.Add(bt);
        bikeClearAt = Time.time + 1.5f;
        BikeOff(true);
        BikeFall(vel * 0.6f + hitNormal * 1.5f + Vector3.up * Mathf.Clamp(speed * 0.08f, 0.5f, 2.5f), bt.right * Mathf.Clamp(speed * 0.25f, 1f, 6f));
        heroMode = 0;
        heroGround = false;
        Vector3 fly = velIn * 0.7f + Vector3.up * Mathf.Clamp(speed * 0.15f, 1.5f, 3.5f);
        heroVel = new Vector3(fly.x, 0f, fly.z);
        heroVy = fly.y;
        heroPos = hero.Body.Hips.position;
        Knock(speed > 12f);
        if (heroRb != null && !heroRb.isKinematic) heroRb.linearVelocity = fly;
        Play(Sfx.Punch, bikeGo.transform.position, 1f);
    }

    // ------------------------------------------------------------------ the rider
    GtaAnim.Frame[] rA, rB;

    GtaAnim.Clip RideClip(string name, bool cycle = false) => GtaAnim.Vehicle(bikeDict, name, cycle);

    // GTA IV's clips about the seat: sit_drive, leaned with the stick (steer_l / _r, steer_fwd / _bwd), still
    // when stopped, reverse going back; then the whole rider turned with the bike.
    void BikePose()
    {
        if (!riding || hero == null || bikeGo == null || bSeat == null) return;
        var body = hero.Body;
        int n = body.Bones.Length;
        if (rA == null || rA.Length != fA.Length) { rA = new GtaAnim.Frame[fA.Length]; rB = new GtaAnim.Frame[fA.Length]; }
        var sit = RideClip("sit_drive", true);
        if (sit == null || !sit.Ok) return;
        ridePhase += Time.deltaTime / Mathf.Max(0.5f, sit.Dur);
        ridePhase -= Mathf.Floor(ridePhase);
        bool ok = GtaAnim.PoseSeat(sit, ridePhase, rA);
        if (!ok) return;
        // stopped: a foot down
        float speed = Mathf.Abs(bikeSpeed);
        rideStill = Mathf.MoveTowards(rideStill, speed < 0.6f && bikeIn[0] < 0.05f ? 1f : 0f, Time.deltaTime * 3f);
        var still = RideClip("still");
        if (rideStill > 0f && still.Ok && GtaAnim.PoseSeat(still, 0.02f, rB)) GtaAnim.Blend(rA, rB, rideStill);
        if (bikeSpeed < -0.3f)
        {
            var rev = RideClip("reverse", true);
            if (rev.Ok && GtaAnim.PoseSeat(rev, Mathf.Repeat(Time.time / rev.Dur, 1f), rB)) GtaAnim.Blend(rA, rB, Mathf.Clamp01(-bikeSpeed));
        }
        float sw = Mathf.Abs(rideSteer) * (1f - rideStill);
        if (sw > 0.01f)
        {
            var c = RideClip(rideSteer < 0f ? "steer_l" : "steer_r");
            if (c.Ok && GtaAnim.PoseSeat(c, 1f, rB)) GtaAnim.Blend(rA, rB, sw);
        }
        float lw = Mathf.Abs(rideLean) * (1f - rideStill);
        if (lw > 0.01f)
        {
            var c = RideClip(rideLean > 0f ? "steer_fwd" : "steer_bwd");
            if (c.Ok && GtaAnim.PoseSeat(c, 1f, rB)) GtaAnim.Blend(rA, rB, lw);
        }
        var bt = bikeGo.transform;
        float yaw = Mathf.Atan2(bt.forward.x, bt.forward.z) * Mathf.Rad2Deg;
        // the seat, without the bike's lean and pitch; those turn the whole rider about it after
        Vector3 seat = bSeat.position;
        body.transform.SetPositionAndRotation(seat, Quaternion.Euler(0f, yaw, 0f));
        body.Apply(rA, seat, yaw);
        Quaternion tilt = bt.rotation * Quaternion.Inverse(Quaternion.Euler(0f, yaw, 0f));
        var hips = body.Hips;
        hips.SetPositionAndRotation(seat + tilt * (hips.position - seat), tilt * hips.rotation);
        // getting on: from how he stood
        if (rideBlend < 1f)
        {
            rideBlend = Mathf.Min(1f, rideBlend + Time.deltaTime / 0.35f);
            float k = Mathf.SmoothStep(0f, 1f, rideBlend);
            var locals = new Quaternion[n];
            for (int i = 1; i < n; i++) locals[i] = body.Bones[i].localRotation;
            hips.SetPositionAndRotation(Vector3.Lerp(rideFromHips, hips.position, k), Quaternion.Slerp(rideFromHipsRot, hips.rotation, k));
            for (int i = 1; i < n; i++) body.Bones[i].localRotation = Quaternion.Slerp(rideFrom[i], locals[i], k);
        }
        heroPos = new Vector3(seat.x, bt.position.y, seat.z);
        heroYaw = yaw;
    }

    // Behind the bike, turned with it unless you've looked round lately.
    void BikeCamera()
    {
        if (gunModel != null && gunModel.activeSelf) gunModel.SetActive(false); // both hands on the bars
        var bt = bikeGo.transform;
        float yaw = Mathf.Atan2(bt.forward.x, bt.forward.z) * Mathf.Rad2Deg;
        rideCamIdle += Time.unscaledDeltaTime;
        if (hDragId != kNone) rideCamIdle = 0f;
        if (rideCamIdle > 1.2f)
        {
            float k = 1f - Mathf.Exp(-3f * Time.unscaledDeltaTime * Mathf.Clamp01(Mathf.Abs(bikeSpeed) / 3f + 0.2f));
            hCamYaw = Mathf.LerpAngle(hCamYaw, yaw, k);
            hCamPitch = Mathf.Lerp(hCamPitch, 10f, k);
        }
        Vector3 want = bt.position + Vector3.up * 1.25f;
        hPivot = Vector3.Lerp(hPivot, want, 1f - Mathf.Exp(-16f * Time.unscaledDeltaTime));
        var turn = Quaternion.Euler(hCamPitch, hCamYaw, 0f);
        float dist = 3.6f + Mathf.Clamp(Mathf.Abs(bikeSpeed) * 0.04f, 0f, 1.6f);
        Vector3 at = hPivot - turn * Vector3.forward * dist + Vector3.up * 0.2f;
        if (Physics.Linecast(hPivot, at, out var hit) && !bikeOwn.Contains(hit.collider) && !hit.collider.transform.IsChildOf(hero.Body.transform))
            at = hit.point + (hPivot - at).normalized * 0.15f;
        cam.transform.SetPositionAndRotation(at, turn);
    }

    // ------------------------------------------------------------------ the screen
    // riding: steering arrows on the left; Gas, Brake and the rider's lean (wheelie / stoppie) on the right
    int bkL = kNone, bkR = kNone, bkGas = kNone, bkBrake = kNone, bkFwd = kNone, bkBack = kNone;

    Vector2 BtnRide(float w, float h) => new Vector2(w - 190f, h - 275f);
    Vector2 BkLeft(float w, float h) => new Vector2(85f, h - 105f);
    Vector2 BkRight(float w, float h) => new Vector2(215f, h - 105f);
    Vector2 BkGas(float w, float h) => new Vector2(w - 90f, h - 100f);
    Vector2 BkBrake(float w, float h) => new Vector2(w - 215f, h - 85f);
    Vector2 BkFwd(float w, float h) => new Vector2(w - 90f, h - 225f);
    Vector2 BkBack(float w, float h) => new Vector2(w - 205f, h - 205f);
    Vector2 BkOff(float w, float h) => new Vector2(w - 70f, 120f);
    const float kBigR = 52f;

    readonly Texture2D[] arrowTexs = new Texture2D[4];

    // An arrow (a triangle): dir 0 up, 1 right, 2 down, 3 left
    void Arrow(Vector2 at, float size, int dir, Color c)
    {
        if (arrowTexs[dir] == null)
        {
            const int n = 64;
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false);
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    // in screen terms: u across, v down the picture (texture rows run up)
                    float u = (x + 0.5f) / n, v = 1f - (y + 0.5f) / n;
                    // along: 0 at the base, 1 at the tip; across: 0 on the axis, 1 at the base's corners
                    float along, across;
                    switch (dir)
                    {
                        case 0: along = 1f - v; across = Mathf.Abs(u - 0.5f) * 2f; break;
                        case 1: along = u; across = Mathf.Abs(v - 0.5f) * 2f; break;
                        case 2: along = v; across = Mathf.Abs(u - 0.5f) * 2f; break;
                        default: along = 1f - u; across = Mathf.Abs(v - 0.5f) * 2f; break;
                    }
                    float inside = Mathf.Min((1f - along) - across * 0.88f, along - 0.1f) * n * 0.5f;
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(255f * Mathf.Clamp01(inside)));
                }
            t.SetPixels32(px);
            t.Apply();
            arrowTexs[dir] = t;
        }
        var was = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(new Rect(at.x - size * 0.5f, at.y - size * 0.5f, size, size), arrowTexs[dir]);
        GUI.color = was;
    }

    void BikeGui(float w, float h)
    {
        if (riding)
        {
            var lit = new Color(0.35f, 0.75f, 1f, 0.65f);
            var knob = new Color(1f, 1f, 1f, 0.25f);
            var ink = new Color(1f, 1f, 1f, 0.9f);
            string kmh = Mathf.RoundToInt(Mathf.Abs(bikeSpeed) * 3.6f) + " km/h  " + (bikeGear == 0 ? "R" : bikeGear.ToString());
            GUI.Label(new Rect(w * 0.5f - 90f, h - 70f, 180f, 30f), kmh, new GUIStyle(chip) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = 18 });
            Disc(BkLeft(w, h), kBigR, bkL != kNone ? lit : knob);
            Arrow(BkLeft(w, h), 46f, 3, ink);
            Disc(BkRight(w, h), kBigR, bkR != kNone ? lit : knob);
            Arrow(BkRight(w, h), 46f, 1, ink);
            Disc(BkGas(w, h), kBigR, bkGas != kNone ? lit : knob, "Gas");
            Disc(BkBrake(w, h), kBtnR + 4f, bkBrake != kNone ? lit : knob, "Brake");
            Disc(BkFwd(w, h), kBtnR, bkFwd != kNone ? lit : knob);
            Arrow(BkFwd(w, h), 34f, 0, ink);
            Disc(BkBack(w, h), kBtnR, bkBack != kNone ? lit : knob);
            Arrow(BkBack(w, h), 34f, 2, ink);
            Disc(BkOff(w, h), kBtnR - 4f, knob, "Off");
        }
        else if (NearBike()) Disc(BtnRide(w, h), kBtnR, new Color(0.35f, 0.75f, 1f, 0.6f), "Ride");
    }

    /// A finger put down: true when the bike's buttons take it (while riding: every one but the camera's drag).
    bool BikeTap(Vector2 at, int finger, float w, float h)
    {
        if (riding)
        {
            if (Vector2.Distance(at, BkOff(w, h)) < kBtnR + 4f) { BikeOff(false); return true; }
            if (Vector2.Distance(at, BkLeft(w, h)) < kBigR + 10f) { bkL = finger; return true; }
            if (Vector2.Distance(at, BkRight(w, h)) < kBigR + 10f) { bkR = finger; return true; }
            if (Vector2.Distance(at, BkGas(w, h)) < kBigR + 8f) { bkGas = finger; return true; }
            if (Vector2.Distance(at, BkBrake(w, h)) < kBtnR + 10f) { bkBrake = finger; return true; }
            if (Vector2.Distance(at, BkFwd(w, h)) < kBtnR + 8f) { bkFwd = finger; return true; }
            if (Vector2.Distance(at, BkBack(w, h)) < kBtnR + 8f) { bkBack = finger; return true; }
            // the rest of the left side: nothing (no stick); the rest: the camera
            return at.x < w * 0.4f && at.y > h * 0.55f;
        }
        if (NearBike() && Vector2.Distance(at, BtnRide(w, h)) < kBtnR + 6f) { BikeOn(); return true; }
        return false;
    }
}
