// Objects you put in the scene (Menu > Objects, the VR menu): for now GTA IV's swingset, glitch and all. Built here
// (a frame of painted steel, two seats on chains that really swing), it does what Liberty City's did: anything that
// leans on it a moment (a man, the hero, his bike, a loose thing) the swing gets agitated, rattles, and then flings
// it, high and far, the ground cracking under its leg. The men and the hero go as Delirium's ragdolls; the bike as a
// wreck, its rider over the bars.
using System.Collections.Generic;
using UnityEngine;

public partial class Game
{
    class Swingset
    {
        public GameObject Go;
        public Rigidbody[] Seats;
        public Bounds Zone;                       // its own space (local), where it takes hold
        public readonly Dictionary<Object, float> Leaning = new Dictionary<Object, float>(); // each one's time on it
        public readonly Dictionary<Object, float> Wait = new Dictionary<Object, float>();    // when it goes
        public readonly Dictionary<Object, float> Rest = new Dictionary<Object, float>();    // flung: let be a while
        public float Rattle;
    }

    readonly List<Swingset> swings = new List<Swingset>();
    float swingPower = 1f;

    // ------------------------------------------------------------------ the menu
    void ObjectsTab()
    {
        Head("GTA IV's swingset");
        Info("Liberty City's famous glitch: lean on it (walk into it, push a man or your bike against it) and after a moment it flings you a few blocks.");
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Put a swingset here", btn, GUILayout.Height(36f))) AddSwing();
        if (swings.Count > 0 && GUILayout.Button("Remove them (" + swings.Count + ")", btn, GUILayout.Height(36f))) ClearSwings();
        GUILayout.EndHorizontal();
        swingPower = Slide("Fling", swingPower, 0.3f, 2.5f, "x");
    }

    // ------------------------------------------------------------------ made
    void AddSwing()
    {
        if (swings.Count >= 8) { note = "Eight swingsets is plenty."; return; }
        Vector3 at;
        float yaw;
        if (LookedAt(out Vector3 spot)) at = spot;
        else if (cam != null) at = cam.transform.position + Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized * 4f;
        else at = Vector3.zero;
        if (Physics.Raycast(at + Vector3.up * 1.5f, Vector3.down, out var g, 10f, VrWorldMask(), QueryTriggerInteraction.Ignore)) at = g.point;
        Vector3 face = cam != null ? Vector3.ProjectOnPlane(cam.transform.position - at, Vector3.up) : Vector3.forward;
        yaw = face.sqrMagnitude > 0.01f ? Mathf.Atan2(face.x, face.z) * Mathf.Rad2Deg : 0f;
        swings.Add(MakeSwing(at, yaw));
    }

    void ClearSwings()
    {
        foreach (var s in swings) if (s.Go != null) Destroy(s.Go);
        swings.Clear();
    }

    Swingset MakeSwing(Vector3 at, float yaw)
    {
        var s = new Swingset();
        var root = new GameObject("Swingset");
        root.transform.SetPositionAndRotation(at, Quaternion.Euler(0f, yaw, 0f));
        s.Go = root;
        var steel = Mat(new Color(0.18f, 0.36f, 0.62f));
        var bar = Mat(new Color(0.75f, 0.2f, 0.16f));
        var chain = Mat(new Color(0.55f, 0.55f, 0.58f));
        var seat = Mat(new Color(0.1f, 0.1f, 0.1f));
        const float W = 3.4f, H = 2.35f, Spread = 0.85f;
        // three A-frames (the ends and the middle pole between the seats), a top bar over them
        foreach (float x in new[] { -W / 2f, 0f, W / 2f })
            foreach (float z in new[] { -Spread, Spread })
            {
                Vector3 foot = new Vector3(x, 0f, z), top = new Vector3(x, H, 0f);
                Pole(root.transform, foot, top, 0.045f, x == 0f ? bar : steel, true);
            }
        Pole(root.transform, new Vector3(-W / 2f - 0.1f, H, 0f), new Vector3(W / 2f + 0.1f, H, 0f), 0.05f, bar, true);
        // the seats: each hangs on two chains from the bar, on a hinge (it swings)
        var anchor = root.AddComponent<Rigidbody>();
        anchor.isKinematic = true;
        var seats = new List<Rigidbody>();
        foreach (float x in new[] { -W / 4f, W / 4f })
        {
            var sg = new GameObject("Seat");
            sg.transform.SetParent(root.transform, false);
            sg.transform.localPosition = new Vector3(x, 0.5f, 0f);
            var board = GameObject.CreatePrimitive(PrimitiveType.Cube);
            board.transform.SetParent(sg.transform, false);
            board.transform.localScale = new Vector3(0.48f, 0.04f, 0.2f);
            board.GetComponent<Renderer>().sharedMaterial = seat;
            foreach (float cx in new[] { -0.22f, 0.22f })
            {
                var c = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                Destroy(c.GetComponent<Collider>());
                c.transform.SetParent(sg.transform, false);
                c.transform.localScale = new Vector3(0.015f, (H - 0.5f) / 2f, 0.015f);
                c.transform.localPosition = new Vector3(cx, (H - 0.5f) / 2f, 0f);
                c.GetComponent<Renderer>().sharedMaterial = chain;
            }
            var rb = sg.AddComponent<Rigidbody>();
            rb.mass = 6f;
            rb.angularDamping = 0.3f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            var hinge = sg.AddComponent<HingeJoint>();
            hinge.connectedBody = anchor;
            hinge.anchor = new Vector3(0f, H - 0.5f, 0f);
            hinge.axis = Vector3.right;
            seats.Add(rb);
        }
        s.Seats = seats.ToArray();
        s.Zone = new Bounds(new Vector3(0f, H / 2f, 0f), new Vector3(W + 0.5f, H + 0.2f, Spread * 2f + 0.5f));
        return s;
    }

    void Pole(Transform root, Vector3 a, Vector3 b, float r, Material mat, bool solid)
    {
        var p = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        p.transform.SetParent(root, false);
        p.transform.localPosition = (a + b) * 0.5f;
        p.transform.localRotation = Quaternion.FromToRotation(Vector3.up, (b - a).normalized);
        p.transform.localScale = new Vector3(r * 2f, (b - a).magnitude / 2f, r * 2f);
        p.GetComponent<Renderer>().sharedMaterial = mat;
        if (!solid) Destroy(p.GetComponent<Collider>());
    }

    // ------------------------------------------------------------------ each frame: who's leaning on it
    void SwingsUpdate(float dt)
    {
        for (int i = swings.Count - 1; i >= 0; i--)
        {
            var s = swings[i];
            if (s.Go == null) { swings.RemoveAt(i); continue; }
            var t = s.Go.transform;
            var here = new List<Object>();
            // men (the hero by his feet while he's on them)
            foreach (var m in men)
            {
                if (m.Body == null) continue;
                Vector3 p = m.Hero && heroMode == 0 ? heroPos + Vector3.up * 0.9f : m.Body.Hips.position;
                if (m.Hero && riding) continue; // (on his bike: the bike's)
                if (s.Zone.Contains(t.InverseTransformPoint(p))) here.Add(m.Body);
            }
            // the bike (ridden, parked or a wreck)
            if (bikeGo != null && s.Zone.Contains(t.InverseTransformPoint(bikeGo.transform.position + Vector3.up * 0.5f))) here.Add(bikeGo);
            // loose things
            foreach (var c in Physics.OverlapBox(t.TransformPoint(s.Zone.center), s.Zone.extents, t.rotation, ~0, QueryTriggerInteraction.Ignore))
            {
                var rb = c.attachedRigidbody;
                if (rb == null || rb.isKinematic || rb.transform.IsChildOf(t) || rb.GetComponent<VrHand>() != null) continue;
                if (c.transform.root.name.Contains("ragdoll") || bikeGo != null && rb.transform.IsChildOf(bikeGo.transform)) continue;
                if (!here.Contains(rb)) here.Add(rb);
            }
            // the time each has leant on it; long enough (its own moment, a quarter to a second and a half): flung
            var gone = new List<Object>();
            foreach (var k in s.Leaning.Keys) if (!here.Contains(k)) gone.Add(k);
            foreach (var k in gone) { s.Leaning.Remove(k); s.Wait.Remove(k); }
            float agitation = 0f;
            foreach (var o in here)
            {
                if (o == null) continue;
                if (s.Rest.TryGetValue(o, out float until) && Time.time < until) continue;
                if (!s.Wait.ContainsKey(o)) s.Wait[o] = Random.Range(0.25f, 1.5f);
                s.Leaning[o] = (s.Leaning.TryGetValue(o, out float had) ? had : 0f) + dt;
                agitation = Mathf.Max(agitation, s.Leaning[o] / s.Wait[o]);
                if (s.Leaning[o] >= s.Wait[o])
                {
                    s.Leaning.Remove(o);
                    s.Wait.Remove(o);
                    s.Rest[o] = Time.time + 2f;
                    Fling(s, o);
                }
            }
            // agitated: the seats thrash, the frame rattles
            if (agitation > 0f)
            {
                foreach (var rb in s.Seats)
                    rb.AddTorque(t.right * Random.Range(-1f, 1f) * 40f * agitation + Random.insideUnitSphere * 10f * agitation, ForceMode.Acceleration);
                s.Rattle -= dt;
                if (s.Rattle <= 0f)
                {
                    s.Rattle = Random.Range(0.08f, 0.2f);
                    Play(Sfx.Impact, t.position + Vector3.up * 1.2f, 0.25f + 0.4f * agitation);
                }
            }
        }
    }

    // flung: high and away from the swing, a bit to either side, faster for a heavy thing
    void Fling(Swingset s, Object o)
    {
        var t = s.Go.transform;
        Vector3 from = t.position + Vector3.up * 0.5f;
        Vector3 pos = o is Component comp ? comp.transform.position : o is GameObject go ? go.transform.position : from;
        Vector3 away = Vector3.ProjectOnPlane(pos - t.position, Vector3.up);
        if (away.sqrMagnitude < 0.01f) away = t.forward * (Random.value < 0.5f ? 1f : -1f);
        away = Quaternion.Euler(0f, Random.Range(-40f, 40f), 0f) * away.normalized;
        float up = Random.Range(35f, 70f) * Mathf.Deg2Rad;
        Vector3 dir = (away * Mathf.Cos(up) + Vector3.up * Mathf.Sin(up)).normalized;
        float speed = Random.Range(22f, 42f) * swingPower;
        Vector3 vel = dir * speed;
        Vector3 spin = Random.insideUnitSphere * 12f;
        Play(Sfx.Boom, t.position + Vector3.up, 0.5f);
        Play(Sfx.Impact, t.position + Vector3.up, 1f);
        foreach (var rb in s.Seats) rb.AddForce(Random.insideUnitSphere * 30f, ForceMode.VelocityChange);
        if (o is Mannequin body)
        {
            var m = men.Find(x => x.Body == body);
            if (m == null) return;
            if (m.Hero && heroMode == 0)
            {
                // the hero: off his clips, going that way (his rigidbody tells Delirium how fast)
                heroVel = new Vector3(vel.x, 0f, vel.z);
                heroVy = vel.y;
                heroGround = false;
                Knock(false);
                if (heroRb != null && !heroRb.isKinematic) heroRb.linearVelocity = vel;
            }
            if (m.Rising) m.Rising = false;
            // the blast of it, from under and behind him
            nm.Explode(body.transform, body.Hips.position - dir * 1.2f, 3f, speed);
            Outfit(m);
            m.HurtAt = Time.time;
            return;
        }
        if (o is GameObject g && g == bikeGo)
        {
            if (riding && hero != null) BikeCrash(vel, vel, Vector3.up);
            else if (bikeWreck == null) BikeFall(vel, spin);
            if (bikeWreck != null)
            {
                bikeWreck.linearVelocity = vel * 0.9f;
                bikeWreck.angularVelocity = spin;
            }
            return;
        }
        if (o is Rigidbody r && r != null)
        {
            r.linearVelocity = vel * Mathf.Clamp(60f / Mathf.Max(1f, r.mass), 0.5f, 1f);
            r.angularVelocity = spin;
        }
    }
}
