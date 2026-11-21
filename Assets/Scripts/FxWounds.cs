// Wounds: where a bullet goes into a man, on his body's surface (each part a capsule round its bones: the line of
// the shot meets the nearest), a wound that moves with that part. Out of it: a burst at the hit (and out the far
// side for a big gun), then it bleeds, drops that fall from it wherever he is (standing, staggering, a ragdoll
// flying) and land as splats. A head, a neck, or now and then a thigh's or an arm's artery: it spurts, a pulse
// with each heartbeat (a wet sound each), weaker as it goes, slower once he's dead.
// And falls: a body (his hips) that comes down faster than 9 m/s and stops: blood where it hit.
using System.Collections.Generic;
using UnityEngine;

public partial class Game
{
    // a part: its two joints, its radius, how likely a shot there opens an artery
    static readonly (string a, string b, float r, float artery)[] kParts =
    {
        ("Hips", "Spine", 0.15f, 0.1f), ("Spine", "Spine1", 0.15f, 0.08f), ("Spine1", "Spine2", 0.15f, 0.08f),
        ("Spine2", "Spine3", 0.15f, 0.12f), ("Spine3", "Neck", 0.14f, 0.15f), ("Neck", "Head", 0.06f, 1f),
        ("LeftUpLeg", "LeftLeg", 0.085f, 0.35f), ("LeftLeg", "LeftFoot", 0.06f, 0.08f),
        ("RightUpLeg", "RightLeg", 0.085f, 0.35f), ("RightLeg", "RightFoot", 0.06f, 0.08f),
        ("LeftShoulder", "LeftArm", 0.065f, 0.15f), ("LeftArm", "LeftForeArm", 0.055f, 0.25f), ("LeftForeArm", "LeftHand", 0.045f, 0.15f),
        ("RightShoulder", "RightArm", 0.065f, 0.15f), ("RightArm", "RightForeArm", 0.055f, 0.25f), ("RightForeArm", "RightHand", 0.045f, 0.15f),
    };

    class Wound
    {
        public Man M;
        public Transform Bone;
        public Vector3 Local, LocalNormal;
        public float Start, Until, Strength, NextDrop, NextPulse;
        public bool Artery;
        public Vector3 Was;
        public bool HasWas;
    }
    readonly List<Wound> wounds = new List<Wound>();
    int MaxWounds => OnQuest ? 10 : 24;

    // the shot's way meets his body: where (on the surface), the part's bone, its outward way there; and where it
    // comes out the other side
    bool FxOnBody(Ray ray, Man m, out Vector3 at, out Vector3 normal, out Transform bone, out float artery, out Vector3 exit)
    {
        at = normal = exit = default;
        bone = null;
        artery = 0f;
        if (m?.Body == null) return false;
        float best = float.MaxValue;
        var body = m.Body;
        Vector3 o = ray.origin, d = ray.direction.normalized;
        foreach (var p in kParts)
        {
            Transform a = body.Bone(p.a), b = body.Bone(p.b);
            if (a == null || b == null) continue;
            if (RayCapsule(o, d, a.position, b.position, p.r, out float tin, out Vector3 inAt, out Vector3 n, out Vector3 outAt) && tin < best)
            {
                best = tin; at = inAt; normal = n; exit = outAt; bone = a; artery = p.artery;
            }
        }
        Transform head = body.Bone("Head"), neck = body.Bone("Neck");
        if (head != null)
        {
            Vector3 up = neck != null ? (head.position - neck.position).normalized : Vector3.up;
            Vector3 c = head.position + up * 0.08f;
            if (RayCapsule(o, d, c - up * 0.02f, c + up * 0.03f, 0.105f, out float hin, out Vector3 hAt, out Vector3 hn, out Vector3 hOut) && hin < best)
            {
                best = hin; at = hAt; normal = hn; exit = hOut; bone = head; artery = 1f;
            }
        }
        return bone != null;
    }

    // the line o + d t (d unit) into the capsule round a-b of radius r: where it goes in (t, the point, the surface's
    // outward way) and out
    static bool RayCapsule(Vector3 o, Vector3 d, Vector3 a, Vector3 b, float r, out float tin, out Vector3 at, out Vector3 normal, out Vector3 exit)
    {
        tin = 0f;
        at = normal = exit = default;
        Vector3 u = b - a;
        float uu = Vector3.Dot(u, u);
        float s, t;
        if (uu < 1e-8f) { s = 0f; t = Vector3.Dot(a - o, d); }
        else
        {
            // the nearest points of the line and the axis (the axis' clamped to the part)
            Vector3 w = o - a;
            float ud = Vector3.Dot(u, d), uw = Vector3.Dot(u, w), dw = Vector3.Dot(d, w);
            float den = uu - ud * ud;
            s = den > 1e-8f ? Mathf.Clamp01((uw - ud * dw) / den) : 0f;
            t = Vector3.Dot(a + u * s - o, d);
            s = Mathf.Clamp01(Vector3.Dot(o + d * t - a, u) / uu);
            t = Vector3.Dot(a + u * s - o, d);
        }
        Vector3 axis = a + u * s;
        float dist = (o + d * t - axis).magnitude;
        if (dist > r || t <= 0f) return false;
        float half = Mathf.Sqrt(r * r - dist * dist);
        tin = t - half;
        at = o + d * tin;
        normal = (at - axis).sqrMagnitude > 1e-8f ? (at - axis).normalized : -d;
        exit = o + d * (t + half);
        return true;
    }

    // a bullet into a man: the burst, the wound (FxBullet)
    // (where it went in, for the tracer; false: it missed him after all)
    bool FxShotMan(Ray ray, Man m, Gun gun, out Vector3 at)
    {
        Vector3 d = ray.direction.normalized;
        if (!FxOnBody(ray, m, out at, out Vector3 normal, out Transform bone, out float artery, out Vector3 exit))
        {
            // (none of his parts on the line: the nearest to it)
            if (!FxOnMan(ray, m, out at)) return false;
            FxBlood(at, d, gun.Damage / 30f);
            return true;
        }
        FxMake();
        if (fx.Drops == null) return true;
        float amount = Mathf.Clamp(gun.Damage / 30f, 0.4f, 3f);
        Vector3 bodyVel = FxBodyVel(m);
        // in: a puff and a few drops back out of the hole
        for (int i = 0; i < 2 + Mathf.RoundToInt(amount); i++)
            Emit(fx.Mist, P(at + normal * 0.01f, bodyVel + (normal * 0.6f + Rnd(0.3f)), UnityEngine.Random.Range(0.05f, 0.1f) * Mathf.Sqrt(amount), UnityEngine.Random.Range(0.15f, 0.3f), kBloodMist));
        int back = Mathf.RoundToInt((OnQuest ? 4 : 6) * amount);
        for (int i = 0; i < back; i++)
            Emit(fx.Drops, P(at + normal * 0.005f, bodyVel + (normal + Rnd(0.5f) - d * 0.3f) * UnityEngine.Random.Range(0.8f, 2.5f), UnityEngine.Random.Range(0.018f, 0.040f), UnityEngine.Random.Range(0.8f, 1.6f), kBlood));
        // out: a big gun's goes through and sprays on
        if (gun.Damage >= 30f || gun.Pellets)
        {
            int through = Mathf.RoundToInt((OnQuest ? 6 : 10) * amount);
            for (int i = 0; i < through; i++)
                Emit(fx.Drops, P(exit + d * 0.01f, bodyVel + (d + Rnd(0.35f)) * UnityEngine.Random.Range(2f, 5f), UnityEngine.Random.Range(0.022f, 0.054f), UnityEngine.Random.Range(0.6f, 1.4f), kBlood));
            Emit(fx.Mist, P(exit, bodyVel + d * 1.5f, 0.14f * Mathf.Sqrt(amount), 0.35f, kBloodMist));
            if (Physics.Raycast(exit + d * 0.05f, d, out var hit, 2.8f, VrWorldMask(), QueryTriggerInteraction.Ignore) && FxCanMark(hit.collider))
                FxMark(fx.Splat, hit.point, hit.normal, UnityEngine.Random.Range(0.25f, 0.5f) * Mathf.Sqrt(amount), new Color(0.38f, 0.02f, 0.02f, 0.92f), hit.collider);
        }
        // the wound, on that part
        bool art = UnityEngine.Random.value < artery;
        if (wounds.Count >= MaxWounds) wounds.RemoveAt(0);
        wounds.Add(new Wound
        {
            M = m, Bone = bone, Local = bone.InverseTransformPoint(at), LocalNormal = bone.InverseTransformDirection(normal),
            Start = Time.time, Until = Time.time + (art ? UnityEngine.Random.Range(12f, 20f) : UnityEngine.Random.Range(4f, 8f)),
            Strength = art ? 1f : Mathf.Clamp(amount * 0.6f, 0.4f, 1f), Artery = art,
            NextDrop = Time.time + 0.1f, NextPulse = Time.time + 0.15f,
        });
        return true;
    }

    // his body's speed now (his hips', from the last frames)
    Vector3 FxBodyVel(Man m) => fallTrack.TryGetValue(m, out var f) ? f.Vel : Vector3.zero;

    // ------------------------------------------------------------------ each frame
    void WoundsUpdate()
    {
        if (wounds.Count == 0 || fx?.Drops == null) return;
        float now = Time.time, dt = Mathf.Max(Time.deltaTime, 1e-4f);
        for (int i = wounds.Count - 1; i >= 0; i--)
        {
            var w = wounds[i];
            if (w.Bone == null || w.M == null || w.M.Body == null || !men.Contains(w.M) || now > w.Until) { wounds.RemoveAt(i); continue; }
            Vector3 p = w.Bone.TransformPoint(w.Local), n = w.Bone.TransformDirection(w.LocalNormal).normalized;
            Vector3 v = w.HasWas ? (p - w.Was) / dt : Vector3.zero;
            if (v.sqrMagnitude > 400f) v = Vector3.zero; // (a teleport)
            w.Was = p;
            w.HasWas = true;
            float left = Mathf.Clamp01((w.Until - now) / Mathf.Max(0.1f, w.Until - w.Start));
            if (w.Artery)
            {
                // a heartbeat's spurt: a stream for a fifth of a second, harder at its start; slower once he's dead
                float beat = w.M.Killed ? 1.3f : 0.75f;
                if (now >= w.NextPulse)
                {
                    w.NextPulse = now + beat * UnityEngine.Random.Range(0.9f, 1.1f);
                    w.NextDrop = now;
                    Play(Sfx.Squirt, p, 0.35f + 0.5f * left * w.Strength);
                }
                float into = beat - (w.NextPulse - now); // seconds into this beat
                if (into < 0.22f && now >= w.NextDrop)
                {
                    w.NextDrop = now + (OnQuest ? 0.035f : 0.02f);
                    float push = (1f - into / 0.22f) * (1.2f + 3.2f * left * w.Strength) * (w.M.Killed ? 0.6f : 1f);
                    Emit(fx.Drops, P(p + n * 0.01f, v + (n + Rnd(0.12f)) * push, UnityEngine.Random.Range(0.022f, 0.040f), UnityEngine.Random.Range(0.8f, 1.5f), kBlood));
                }
                else if (into >= 0.22f && now >= w.NextDrop)
                {
                    // between beats: it runs
                    w.NextDrop = now + 0.12f;
                    Emit(fx.Drops, P(p + n * 0.01f, v + n * 0.25f + Rnd(0.1f), 0.022f, 1.2f, kBlood));
                }
            }
            else if (now >= w.NextDrop)
            {
                // it bleeds: drops, fewer as it goes
                w.NextDrop = now + Mathf.Lerp(0.6f, 0.14f, left * w.Strength) * UnityEngine.Random.Range(0.7f, 1.3f);
                Emit(fx.Drops, P(p + n * 0.01f, v + n * 0.2f + Rnd(0.15f), UnityEngine.Random.Range(0.018f, 0.032f), 1.3f, kBlood));
            }
        }
    }

    // ------------------------------------------------------------------ falls
    class Fall { public Vector3 Pos, Vel; public bool Has; public float Quiet; }
    readonly Dictionary<Man, Fall> fallTrack = new Dictionary<Man, Fall>();

    void FallsUpdate()
    {
        float dt = Mathf.Max(Time.deltaTime, 1e-4f);
        if (fallTrack.Count > men.Count + 8)
        {
            var gone = new List<Man>();
            foreach (var k in fallTrack.Keys) if (!men.Contains(k) || k.Body == null) gone.Add(k);
            foreach (var k in gone) fallTrack.Remove(k);
        }
        foreach (var m in men)
        {
            if (m?.Body?.Hips == null) continue;
            if (!fallTrack.TryGetValue(m, out var f)) fallTrack[m] = f = new Fall();
            Vector3 p = m.Body.Hips.position;
            if (!f.Has) { f.Pos = p; f.Has = true; continue; }
            Vector3 moved = p - f.Pos;
            f.Pos = p;
            if (moved.magnitude > 3f) { f.Vel = Vector3.zero; continue; } // (put somewhere: a reset, a teleport)
            Vector3 v = moved / dt;
            Vector3 was = f.Vel;
            f.Vel = v;
            // came down hard, and stopped
            if (Time.time >= f.Quiet && was.y < -9f && v.y > was.y * 0.35f && Physics.Raycast(p + Vector3.up * 0.2f, Vector3.down, out var g, 1.2f, VrWorldMask(), QueryTriggerInteraction.Ignore))
            {
                f.Quiet = Time.time + 1f;
                float k = Mathf.Clamp01((-was.y - 9f) / 10f);
                FxMake();
                if (fx.Drops == null) continue;
                Vector3 at = g.point + Vector3.up * 0.05f;
                for (int i = 0; i < Mathf.RoundToInt((OnQuest ? 8 : 14) * (0.5f + k)); i++)
                    Emit(fx.Drops, P(at + Rnd(0.15f), (Vector3.up * 0.8f + Rnd(1f)) * UnityEngine.Random.Range(1f, 3f + 3f * k), UnityEngine.Random.Range(0.022f, 0.054f), UnityEngine.Random.Range(0.5f, 1.1f), kBlood));
                if (FxCanMark(g.collider))
                    FxMark(fx.Splat, g.point, g.normal, 0.45f + 0.7f * k, new Color(0.36f, 0.02f, 0.02f, 0.92f), g.collider);
                // and he bleeds a while from where he hit (his head, from high enough)
                Transform part = k > 0.5f ? m.Body.Bone("Head") : m.Body.Hips;
                if (part != null)
                {
                    if (wounds.Count >= MaxWounds) wounds.RemoveAt(0);
                    wounds.Add(new Wound
                    {
                        M = m, Bone = part, Local = Vector3.zero, LocalNormal = part.InverseTransformDirection(Vector3.up),
                        Start = Time.time, Until = Time.time + 4f + 6f * k, Strength = 0.5f + 0.5f * k, Artery = k > 0.7f,
                        NextDrop = Time.time + 0.2f, NextPulse = Time.time + 0.4f,
                    });
                }
            }
        }
    }
}
