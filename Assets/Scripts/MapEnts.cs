// A GMod map's working parts: its entities run the way Source runs them, as far as the sandbox goes. Buttons you
// use, doors and lifts that open and move (and carry the hero), trains along their tracks, things that spin,
// sounds and music (ambient_generic), teleports, pushes, triggers, relays, timers, cases, counters, and the
// outputs that wire them ("OnPressed" -> "track1,PlaySound,,0,-1"), on time.
//
// From the converter: the map's entities beside its .glb (".ents": each key a line, a brush model's box), the
// sounds they name (".sounds/<path>"), and the faces of brush entities that move or are told to (and named props)
// in meshes of their own, "[ent N]": here each entity's are gathered under one object, its pivot at its origin,
// and moved as the entity moves. They keep their colliders (moved with them) and are left out of the static
// batches and of Delirium's level (its world is still: a ragdoll goes through a door that has moved).
//
// Source's units and axes: x 0.0254 m, (x, y, z) -> (-x, z, -y) in the map's object (its scale on top). A turn by
// a in Source is a turn by -a about the turned axis here (the axes are mirrored).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Delirium.Ragdoll;
using UnityEngine;

public partial class Game
{
    class EntOut
    {
        public string Output, Target, Input, Param;
        public float Delay;
        public int Times;
    }

    class Ent
    {
        public int Index;
        public string Class = "", Name = "";
        public readonly List<KeyValuePair<string, string>> Keys = new List<KeyValuePair<string, string>>();
        public readonly List<EntOut> Outs = new List<EntOut>();
        public Transform T;              // its own meshes (or a pivot of its own), null if it has none
        public Vector3 SrcOrigin, SrcMins, SrcMaxs;
        public bool HasBox, Enabled = true, Killed, Locked, Inside;
        public Bounds LocalBox;          // its box in T's space (it moves with it: a trigger on a train)
        public Vector3 HomePos;          // T's place and turn when closed
        public Quaternion HomeRot;
        // doors, buttons, movelinear: 0 closed .. 1 open; Dir +1 opening, -1 closing
        public float Pos, Dir, Wait, WaitLeft, Speed;
        public Vector3 MoveTo;           // its place, open (world)
        public Vector3 Axis;             // a turning door's axis (world)
        public float Angle;              // its turn, open (degrees, Unity's sense)
        public bool Turns;
        // func_rotating
        public float Spin, SpinTarget, SpinMax;
        public Ent Attached;             // phys_motor: what it turns
        public Vector3 Hub;              // phys_motor: a point on its axis (world)
        // trains
        public Ent Path;
        public float TrainSpeed, TrainMax;
        // sounds
        public AudioSource Audio;
        public float Volume = 1f;
        // timers
        public float NextFire = -1f;
        public float Value;              // math_counter / logic_branch
        public string Get(string k, string def = "")
        {
            for (int i = Keys.Count - 1; i >= 0; i--)
                if (string.Equals(Keys[i].Key, k, StringComparison.OrdinalIgnoreCase)) return Keys[i].Value;
            return def;
        }
        public float F(string k, float def = 0f) => float.TryParse(Get(k), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : def;
        public int I(string k, int def = 0) => int.TryParse(Get(k), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : (int)F(k, def);
        public bool Flag(int bit) => (I("spawnflags") & bit) != 0;
    }

    readonly List<Ent> ents = new List<Ent>();
    readonly Dictionary<string, List<Ent>> entsByName = new Dictionary<string, List<Ent>>(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<Collider, Ent> entOfCollider = new Dictionary<Collider, Ent>();
    readonly List<(float at, Ent caller, string target, string input, string param)> ioQueue = new List<(float, Ent, string, string, string)>();
    static string bspEnts; // the last converted map's ".ents" (beside bspEnv)
    string entSounds = "";
    float entClock;
    Ent useTarget;

    // ------------------------------------------------------------------ Source's places to the map's
    Vector3 SrcPoint(Vector3 s) => mapRoot.transform.TransformPoint(new Vector3(-s.x, s.z, -s.y) * 0.0254f);
    Vector3 SrcDir(Vector3 s) => mapRoot.transform.TransformDirection(new Vector3(-s.x, s.z, -s.y)).normalized;
    float SrcLen(float units) => units * 0.0254f * (mapRoot != null ? mapRoot.transform.lossyScale.x : 1f);

    static Vector3 Vec(string s)
    {
        var p = (s ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        float f(int i) => i < p.Length && float.TryParse(p[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;
        return new Vector3(f(0), f(1), f(2));
    }

    // Source's angles (pitch yaw roll) to the way they point (Source's axes)
    static Vector3 SrcForward(Vector3 a)
    {
        float p = a.x * Mathf.Deg2Rad, y = a.y * Mathf.Deg2Rad;
        return new Vector3(Mathf.Cos(p) * Mathf.Cos(y), Mathf.Cos(p) * Mathf.Sin(y), -Mathf.Sin(p));
    }

    // ------------------------------------------------------------------ reading them (SetMap, before colliders)
    void EntsBuild(GameObject root, string entsFile)
    {
        EntsClear();
        if (string.IsNullOrEmpty(entsFile) || !File.Exists(entsFile)) return;
        entSounds = entsFile.Substring(0, entsFile.Length - ".ents".Length) + ".sounds";
        Ent e = new Ent { Index = 0 };
        foreach (string line in File.ReadAllLines(entsFile))
        {
            if (line == "--")
            {
                ents.Add(e);
                e = new Ent { Index = ents.Count };
                continue;
            }
            int tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            string k = line.Substring(0, tab), v = line.Substring(tab + 1);
            if (k == "#mins") { e.SrcMins = Vec(v); e.HasBox = true; continue; }
            if (k == "#maxs") { e.SrcMaxs = Vec(v); continue; }
            e.Keys.Add(new KeyValuePair<string, string>(k, v));
            if (k.StartsWith("On", StringComparison.OrdinalIgnoreCase) || k.StartsWith("Out", StringComparison.Ordinal) || k == "PressedForward" || k == "PressedBack")
            {
                // "target,input,param,delay,times" (commas, or ESC in newer maps)
                var p = v.Split(v.IndexOf('\u001b') >= 0 ? '\u001b' : ',');
                if (p.Length >= 2)
                    e.Outs.Add(new EntOut
                    {
                        Output = k, Target = p[0].Trim(), Input = p[1].Trim(), Param = p.Length > 2 ? p[2] : "",
                        Delay = p.Length > 3 && float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float d) ? d : 0f,
                        Times = p.Length > 4 && int.TryParse(p[4], out int t) ? t : -1,
                    });
            }
        }
        foreach (var x in ents)
        {
            x.Class = x.Get("classname").ToLowerInvariant();
            if (x.Class == "func_tanktrain") x.Class = "func_tracktrain"; // (a tracktrain that can be shot)
            x.Name = x.Get("targetname");
            x.SrcOrigin = Vec(x.Get("origin"));
            if (x.Name != "")
            {
                if (!entsByName.TryGetValue(x.Name, out var l)) entsByName[x.Name] = l = new List<Ent>();
                l.Add(x);
            }
        }
        // each entity's own meshes under one object, its pivot at its origin
        var groups = new Dictionary<int, Transform>();
        foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
        {
            string n = mf.gameObject.name;
            int at = n.LastIndexOf("[ent ", StringComparison.Ordinal);
            if (at < 0) continue;
            int end = n.IndexOf(']', at);
            if (end < 0 || !int.TryParse(n.Substring(at + 5, end - at - 5), out int idx) || idx < 0 || idx >= ents.Count) continue;
            if (!groups.TryGetValue(idx, out var g))
            {
                var go = new GameObject("ent " + idx + " " + ents[idx].Class + (ents[idx].Name != "" ? " " + ents[idx].Name : ""));
                go.transform.SetParent(root.transform, false);
                go.transform.position = SrcPoint(ents[idx].SrcOrigin);
                g = groups[idx] = go.transform;
                ents[idx].T = g;
                entOfT[g] = ents[idx];
            }
            mf.transform.SetParent(g, true);
        }
        Debug.Log("[Sandbox] map entities: " + ents.Count + ", " + groups.Count + " with meshes of their own");
    }

    void EntsClear()
    {
        foreach (var e in ents)
            if (e.Audio != null) Destroy(e.Audio.gameObject);
        foreach (var t in entMovers)
            if (t != null) try { nm?.UnregisterCar(t); } catch (Exception) { }
        entMovers.Clear();
        ents.Clear();
        entOfT.Clear();
        entsByName.Clear();
        entOfCollider.Clear();
        ioQueue.Clear();
        useTarget = null;
    }

    readonly Dictionary<Transform, Ent> entOfT = new Dictionary<Transform, Ent>();
    readonly List<Transform> entMovers = new List<Transform>(); // given to Delirium as moving boxes

    static readonly HashSet<string> kMoving = new HashSet<string>
    {
        "func_door", "func_door_rotating", "prop_door_rotating", "func_movelinear", "func_water_analog", "func_tracktrain",
        "func_train", "func_rotating", "func_physbox", "func_physbox_multiplayer", "func_button", "func_rot_button", "momentary_rot_button",
    };

    // one that moves (or hangs from one that might): not in Delirium's still world, but a moving box of its own
    static bool EntMoves(Ent e) => e != null && (kMoving.Contains(e.Class) || e.Get("parentname") != "");

    // the entity an object of the map is part of, or null
    Ent EntOfObject(GameObject g)
    {
        for (var t = g.transform; t != null; t = t.parent)
            if (entOfT.TryGetValue(t, out var e)) return e;
        return null;
    }

    // (SetMap, after the colliders: the movers get a kinematic body, their colliders known)
    void EntsStart()
    {
        if (ents.Count == 0) return;
        entClock = 0f;
        var parents = new List<Ent>();
        foreach (var e in ents)
        {
            if (e.T != null)
            {
                foreach (var c in e.T.GetComponentsInChildren<Collider>(true)) entOfCollider[c] = e;
                var rb = e.T.gameObject.AddComponent<Rigidbody>();
                rb.isKinematic = true;
                rb.useGravity = false;
            }
            if (e.Get("parentname") != "") parents.Add(e);
        }
        // a child moves with its parent (a button on a lift, a car or a trigger on a train): one without meshes of
        // its own gets an empty object there to move
        foreach (var e in parents)
        {
            var p = Find(e.Get("parentname").Split(',')[0], e);
            if (p.Count == 0 || p[0] == e) continue;
            if (p[0].T == null) Pivot(p[0]);
            if (e.T == null) Pivot(e);
            e.T.SetParent(p[0].T, true);
        }
        foreach (var e in ents)
        {
            if (e.T == null) continue;
            e.HomePos = e.T.position;
            e.HomeRot = e.T.rotation;
            if (e.HasBox)
            {
                // its box in its own space (the 8 corners' at the start)
                Vector3 a = SrcPoint(e.SrcMins), b = SrcPoint(e.SrcMaxs);
                var lb = new Bounds(e.T.InverseTransformPoint(a), Vector3.zero);
                for (int c = 0; c < 8; c++)
                    lb.Encapsulate(e.T.InverseTransformPoint(new Vector3((c & 1) != 0 ? a.x : b.x, (c & 2) != 0 ? a.y : b.y, (c & 4) != 0 ? a.z : b.z)));
                e.LocalBox = lb;
            }
        }
        foreach (var e in ents) Setup(e);
        // the ragdolls meet the moving parts: a box each of their pieces (trains hit like trains, the rest like objects)
        var given = new HashSet<Transform>();
        foreach (var e in ents)
        {
            if (e.T == null || !EntMoves(e) || e.Class == "func_button" || e.Class == "func_rot_button") continue;
            bool train = e.Class == "func_tracktrain" || e.Class == "func_train";
            for (var up = e.T.parent; up != null && !train; up = up.parent)
                if (entOfT.TryGetValue(up, out var pe) && (pe.Class == "func_tracktrain" || pe.Class == "func_train")) train = true;
            foreach (var mc in e.T.GetComponentsInChildren<MeshCollider>(true))
            {
                if (mc.sharedMesh == null || !given.Add(mc.transform)) continue;
                var mb = mc.sharedMesh.bounds;
                var size = Vector3.Scale(mb.size, mc.transform.lossyScale);
                if (size.x < 0.05f && size.y < 0.05f && size.z < 0.05f) continue;
                try
                {
                    nm?.RegisterMover(mc.transform, train ? DeliriumRagdolls.MoverKind.Train : DeliriumRagdolls.MoverKind.Object,
                        new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z)), false, train, train || e.Class == "func_rotating", mb.center);
                    entMovers.Add(mc.transform);
                }
                catch (Exception ex) { Debug.LogWarning("[Sandbox] map mover " + e.Class + ": " + ex.Message); }
            }
        }
        if (entMovers.Count > 0) Debug.Log("[Sandbox] map: " + entMovers.Count + " moving pieces given to Delirium");
        // the start: logic_auto's outputs
        foreach (var e in ents)
            if (e.Class == "logic_auto")
            {
                Fire(e, "OnMapSpawn");
                Fire(e, "OnNewGame");
                Fire(e, "OnMultiNewMap");
                Fire(e, "OnMultiNewRound");
            }
    }

    void Pivot(Ent e)
    {
        var go = new GameObject("ent " + e.Index + " " + e.Class + (e.Name != "" ? " " + e.Name : ""));
        go.transform.SetParent(mapRoot.transform, false);
        go.transform.position = SrcPoint(e.SrcOrigin);
        e.T = go.transform;
        entOfT[e.T] = e;
    }

    void Setup(Ent e)
    {
        switch (e.Class)
        {
            case "func_door":
            case "func_water_analog":
            case "func_movelinear":
            case "func_button":
            {
                // along its move direction by its size there less its lip (func_movelinear: by its movedistance)
                Vector3 md = SrcForward(Vec(e.Get("movedir", "0 0 0")));
                Vector3 size = e.SrcMaxs - e.SrcMins;
                float dist = e.Class == "func_movelinear" ? e.F("movedistance", 100f)
                    : Mathf.Abs(md.x * size.x) + Mathf.Abs(md.y * size.y) + Mathf.Abs(md.z * size.z) - e.F("lip");
                if (e.Class == "func_button" && (e.Flag(1) || dist < 0.5f)) dist = 0f; // "don't move"
                e.Speed = Mathf.Max(SrcLen(e.F("speed", 100f)), 0.01f);
                if (e.T != null) e.MoveTo = e.HomePos + SrcDir(md) * SrcLen(Mathf.Max(0f, dist));
                e.Wait = e.F("wait", e.Class == "func_button" ? 1f : 4f);
                e.Locked = e.Class == "func_button" ? e.Flag(2048) : e.Flag(2048);
                // a door that starts open
                if (e.Class == "func_door" && (e.Flag(1) || e.I("spawnpos") == 1)) SetPos(e, 1f);
                if (e.Class == "func_movelinear") SetPos(e, Mathf.Clamp01(e.F("startposition")));
                break;
            }
            case "func_door_rotating":
            case "prop_door_rotating":
            case "func_rot_button":
            {
                e.Turns = true;
                Vector3 axis;
                if (e.Class == "prop_door_rotating") axis = SrcRotateAxis(Vec(e.Get("angles")), new Vector3(0f, 0f, 1f));
                else axis = e.Flag(64) ? new Vector3(1f, 0f, 0f) : e.Flag(128) ? new Vector3(0f, 1f, 0f) : new Vector3(0f, 0f, 1f);
                e.Axis = SrcDir(axis);
                float deg = e.F("distance", 90f);
                if (e.Flag(2)) deg = -deg; // "reverse direction"
                e.Angle = -deg;
                e.Speed = Mathf.Max(e.F("speed", 100f), 1f); // degrees a second
                e.Wait = e.F(e.Class == "prop_door_rotating" ? "returndelay" : "wait", e.Class == "func_rot_button" ? 1f : -1f);
                e.Locked = e.Flag(2048);
                // a prop door that opens the other way only, or starts open the other way ("spawnpos" 2: open back)
                if (e.Class == "prop_door_rotating" && (e.I("opendir") == 2 || e.I("spawnpos") == 2)) e.Angle = -e.Angle;
                if (e.Class != "func_rot_button" && (e.Flag(1) || e.I("spawnpos") == 1 || e.Class == "prop_door_rotating" && e.I("spawnpos") == 2)) SetPos(e, 1f);
                break;
            }
            case "func_rotating":
            {
                Vector3 axis = e.Flag(4) ? new Vector3(1f, 0f, 0f) : e.Flag(8) ? new Vector3(0f, 1f, 0f) : new Vector3(0f, 0f, 1f);
                e.Axis = SrcDir(axis);
                e.SpinMax = e.F("maxspeed", 100f) * (e.Flag(2) ? -1f : 1f);
                if (e.Flag(1)) e.SpinTarget = e.Spin = e.SpinMax;
                break;
            }
            case "phys_motor":
            {
                // turns what it's attached to about its axis (its origin toward its "axis" point), at its speed
                var att = Find(e.Get("attach1"), e);
                if (att.Count == 0) break;
                if (att[0].T == null) Pivot(att[0]);
                e.Attached = att[0];
                e.Hub = SrcPoint(e.SrcOrigin);
                Vector3 to = SrcPoint(Vec(e.Get("axis"))) - e.Hub;
                e.Axis = to.sqrMagnitude > 1e-8f ? to.normalized : Vector3.up;
                e.SpinMax = e.F("speed", 30f);
                if (e.Flag(1)) e.SpinTarget = e.Spin = e.SpinMax;
                break;
            }
            case "func_brush":
                if (e.I("StartDisabled") == 1) Show(e, false);
                if (e.I("Solidity") == 1) SetSolid(e, false); // "never solid"
                break;
            case "func_wall_toggle":
                if (e.Flag(1)) Show(e, false);
                break;
            case "func_illusionary":
                SetSolid(e, false);
                break;
            case "trigger_once":
            case "trigger_multiple":
            case "trigger_teleport":
            case "trigger_push":
            case "trigger_hurt":
            case "logic_relay":
            case "logic_timer":
            case "func_tracktrain":
                if (e.I("StartDisabled") == 1) e.Enabled = false;
                if (e.Class == "logic_timer") ArmTimer(e);
                if (e.Class == "func_tracktrain") TrainStart(e);
                break;
            case "func_train":
                TrainStart(e);
                break;
            case "math_counter":
                e.Value = e.F("startvalue");
                break;
            case "logic_branch":
                e.Value = e.F("InitialValue");
                break;
            case "ambient_generic":
                SoundSetup(e);
                break;
        }
    }

    Vector3 SrcRotateAxis(Vector3 angles, Vector3 local)
    {
        // a prop's own axis, turned by its angles (Source: yaw about z, pitch about y, roll about x)
        var q = Quaternion.AngleAxis(angles.y, Vector3.forward) * Quaternion.AngleAxis(angles.x, Vector3.up) * Quaternion.AngleAxis(angles.z, Vector3.right);
        return q * local; // (Source's axes: forward here is z up)
    }

    // ------------------------------------------------------------------ outputs and inputs
    void Fire(Ent from, string output, string paramOverride = null)
    {
        foreach (var o in from.Outs)
        {
            if (!string.Equals(o.Output, output, StringComparison.OrdinalIgnoreCase) || o.Times == 0) continue;
            if (o.Times > 0) o.Times--;
            ioQueue.Add((entClock + o.Delay, from, o.Target, o.Input, o.Param != "" || paramOverride == null ? o.Param : paramOverride));
        }
    }

    List<Ent> Find(string target, Ent caller)
    {
        var list = new List<Ent>();
        if (string.IsNullOrEmpty(target)) return list;
        string t = target.ToLowerInvariant();
        if (t == "!self" || t == "!caller" || t == "!activator") { if (caller != null) list.Add(caller); return list; }
        if (t == "!player" || t == "!pvsplayer") return list; // (the hero: inputs to him aren't run)
        if (t.EndsWith("*"))
        {
            string pre = t.Substring(0, t.Length - 1);
            foreach (var kv in entsByName)
                if (kv.Key.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) list.AddRange(kv.Value);
            return list;
        }
        if (entsByName.TryGetValue(target, out var l)) list.AddRange(l);
        else foreach (var e in ents) if (string.Equals(e.Class, t, StringComparison.OrdinalIgnoreCase)) list.Add(e); // by class too
        return list;
    }

    void EntInput(Ent e, string input, string param, Ent caller)
    {
        if (e.Killed) return;
        string i = input.ToLowerInvariant();
        float pf = float.TryParse(param, NumberStyles.Float, CultureInfo.InvariantCulture, out float pv) ? pv : 0f;
        // any entity's
        switch (i)
        {
            case "kill":
            case "killhierarchy":
            case "break":
                e.Killed = true;
                Show(e, false);
                if (e.Audio != null) e.Audio.Stop();
                if (i == "break") Fire(e, "OnBreak");
                return;
            case "fireuser1": Fire(e, "OnUser1"); return;
            case "fireuser2": Fire(e, "OnUser2"); return;
            case "fireuser3": Fire(e, "OnUser3"); return;
            case "fireuser4": Fire(e, "OnUser4"); return;
            case "addoutput": AddOutput(e, param); return;
        }
        switch (e.Class)
        {
            case "func_door":
            case "func_door_rotating":
            case "prop_door_rotating":
            case "func_water_analog":
                if (i == "open") Open(e, caller);
                else if (i == "close") Close(e);
                else if (i == "toggle") { if (e.Dir > 0 || e.Pos > 0.5f) Close(e); else Open(e, caller); }
                else if (i == "lock") e.Locked = true;
                else if (i == "unlock") e.Locked = false;
                else if (i == "setspeed") e.Speed = e.Turns ? Mathf.Max(pf, 1f) : Mathf.Max(SrcLen(pf), 0.01f);
                break;
            case "func_movelinear":
                if (i == "open") { e.Dir = 1f; Fire(e, "OnOpen"); }
                else if (i == "close") { e.Dir = -1f; Fire(e, "OnClose"); }
                else if (i == "setposition") { e.Value = Mathf.Clamp01(pf); e.Dir = e.Value > e.Pos ? 1f : -1f; }
                else if (i == "setspeed") e.Speed = Mathf.Max(SrcLen(pf), 0.01f);
                break;
            case "func_button":
            case "func_rot_button":
                if (i == "press") Press(e, caller, true);
                else if (i == "lock") e.Locked = true;
                else if (i == "unlock") e.Locked = false;
                else if (i == "pressin") { e.Dir = 1f; Fire(e, "OnPressed"); }
                else if (i == "pressout") e.Dir = -1f;
                break;
            case "phys_motor":
                if (i == "turnon") e.SpinTarget = e.SpinMax;
                else if (i == "turnoff") e.SpinTarget = 0f;
                else if (i == "setspeed") { e.SpinMax = pf; if (Mathf.Abs(e.SpinTarget) > 0.01f) e.SpinTarget = pf; }
                break;
            case "func_rotating":
                if (i == "start") e.SpinTarget = e.SpinMax;
                else if (i == "stop") e.SpinTarget = 0f;
                else if (i == "toggle") e.SpinTarget = Mathf.Abs(e.SpinTarget) > 0.01f ? 0f : e.SpinMax;
                else if (i == "reverse") { e.SpinMax = -e.SpinMax; e.SpinTarget = -e.SpinTarget; }
                else if (i == "setspeed") e.SpinTarget = e.SpinMax * Mathf.Clamp(pf, -1f, 1f);
                break;
            case "func_tracktrain":
            case "func_train":
                if (i == "startforward" || i == "start" || i == "resume") e.TrainSpeed = e.TrainMax;
                else if (i == "startbackward") e.TrainSpeed = -e.TrainMax;
                else if (i == "stop") e.TrainSpeed = 0f;
                else if (i == "toggle") e.TrainSpeed = Mathf.Abs(e.TrainSpeed) > 0.001f ? 0f : e.TrainMax;
                else if (i == "setspeed" || i == "setspeedreal") e.TrainSpeed = i == "setspeed" ? e.TrainMax * pf : SrcLen(pf);
                else if (i == "setspeeddir") e.TrainSpeed = e.TrainMax * pf;
                else if (i == "teleporttopathtrack" || i == "settrack") { var p = Find(param, caller); if (p.Count > 0) TrainTo(e, p[0], true); }
                break;
            case "ambient_generic":
                if (i == "playsound") SoundPlay(e, true);
                else if (i == "stopsound" || i == "fadeout") SoundPlay(e, false);
                else if (i == "togglesound") SoundPlay(e, e.Audio == null || !e.Audio.isPlaying);
                else if (i == "volume") { e.Volume = Mathf.Clamp01(pf / 10f); if (e.Audio != null) e.Audio.volume = e.Volume; if (pf <= 0f && e.Audio != null) e.Audio.Stop(); }
                else if (i == "fadein") SoundPlay(e, true);
                else if (i == "pitch" && e.Audio != null) e.Audio.pitch = Mathf.Clamp(pf / 100f, 0.1f, 3f);
                break;
            case "logic_relay":
                if (i == "trigger" && e.Enabled) { Fire(e, "OnTrigger"); if (e.Flag(1)) e.Killed = true; }
                else if (i == "enable") e.Enabled = true;
                else if (i == "disable") e.Enabled = false;
                else if (i == "toggle") e.Enabled = !e.Enabled;
                else if (i == "enablerefire") e.Enabled = true;
                else if (i == "cancelpending") ioQueue.RemoveAll(q => q.caller == e);
                break;
            case "logic_timer":
                if (i == "enable") { e.Enabled = true; ArmTimer(e); }
                else if (i == "disable") e.Enabled = false;
                else if (i == "toggle") { e.Enabled = !e.Enabled; if (e.Enabled) ArmTimer(e); }
                else if (i == "firetimer") Fire(e, "OnTimer");
                else if (i == "refiretime") { SetKey(e, "RefireTime", param); ArmTimer(e); }
                else if (i == "resettimer") ArmTimer(e);
                break;
            case "logic_case":
                if (i == "invalue")
                {
                    bool hit = false;
                    for (int c = 1; c <= 16; c++)
                        if (e.Get("Case" + c.ToString("00")) != "" && string.Equals(e.Get("Case" + c.ToString("00")), param, StringComparison.OrdinalIgnoreCase)) { Fire(e, "OnCase" + c.ToString("00")); hit = true; }
                    if (!hit) Fire(e, "OnDefault", param);
                }
                else if (i == "pickrandom" || i == "pickrandomshuffle")
                {
                    var have = new List<int>();
                    for (int c = 1; c <= 16; c++)
                        foreach (var o in e.Outs) if (string.Equals(o.Output, "OnCase" + c.ToString("00"), StringComparison.OrdinalIgnoreCase)) { have.Add(c); break; }
                    if (have.Count > 0) Fire(e, "OnCase" + have[UnityEngine.Random.Range(0, have.Count)].ToString("00"));
                }
                break;
            case "logic_branch":
                if (i == "setvalue" || i == "setvaluetest") { e.Value = pf != 0f || param == "true" ? 1f : 0f; if (i == "setvaluetest") Fire(e, e.Value != 0f ? "OnTrue" : "OnFalse"); }
                else if (i == "toggle" || i == "toggletest") { e.Value = e.Value != 0f ? 0f : 1f; if (i == "toggletest") Fire(e, e.Value != 0f ? "OnTrue" : "OnFalse"); }
                else if (i == "test") Fire(e, e.Value != 0f ? "OnTrue" : "OnFalse");
                break;
            case "math_counter":
            {
                float was = e.Value, min = e.F("min"), max = e.F("max");
                if (i == "add") e.Value += pf;
                else if (i == "subtract") e.Value -= pf;
                else if (i == "setvalue" || i == "setvaluenofire") e.Value = pf;
                else if (i == "multiply") e.Value *= pf;
                else if (i == "divide" && pf != 0f) e.Value /= pf;
                else break;
                if (max > min) e.Value = Mathf.Clamp(e.Value, min, max);
                if (i == "setvaluenofire") break;
                Fire(e, "OutValue", e.Value.ToString(CultureInfo.InvariantCulture));
                if (max > min && e.Value >= max && was < max) Fire(e, "OnHitMax");
                if (max > min && e.Value <= min && was > min) Fire(e, "OnHitMin");
                break;
            }
            case "point_teleport":
                if (i == "teleport")
                    foreach (var t in Find(e.Get("target"), caller))
                        if (t.T != null) t.T.position = SrcPoint(e.SrcOrigin);
                break;
        }
        // enable, disable, toggle, show, hide: whatever it is
        switch (i)
        {
            case "enable":
            case "turnon":
                e.Enabled = true;
                if (e.Class.StartsWith("func_") && e.Class != "func_tracktrain") Show(e, true);
                break;
            case "disable":
            case "turnoff":
                e.Enabled = false;
                if (e.Class.StartsWith("func_") && e.Class != "func_tracktrain") Show(e, false);
                break;
            case "toggle":
                if (e.Class == "func_brush" || e.Class == "func_wall_toggle") Show(e, !Visible(e));
                else if (e.Class.StartsWith("trigger_")) e.Enabled = !e.Enabled;
                break;
            case "enablecollision": SetSolid(e, true); break;
            case "disablecollision": SetSolid(e, false); break;
            case "enabledraw": Show(e, true, false); break;
            case "disabledraw": Show(e, false, false); break;
        }
    }

    static void SetKey(Ent e, string k, string v)
    {
        e.Keys.Add(new KeyValuePair<string, string>(k, v));
    }

    // "AddOutput" "key value" or "OnX target:input:param:delay:times"
    void AddOutput(Ent e, string param)
    {
        int sp = param.IndexOf(' ');
        if (sp <= 0) return;
        string k = param.Substring(0, sp), v = param.Substring(sp + 1);
        if (v.IndexOf(':') >= 0 && k.StartsWith("On", StringComparison.OrdinalIgnoreCase))
        {
            var p = v.Split(':');
            e.Outs.Add(new EntOut
            {
                Output = k, Target = p[0], Input = p.Length > 1 ? p[1] : "", Param = p.Length > 2 ? p[2] : "",
                Delay = p.Length > 3 && float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float d) ? d : 0f,
                Times = p.Length > 4 && int.TryParse(p[4], out int n) ? n : -1,
            });
        }
        else SetKey(e, k, v);
    }

    bool Visible(Ent e)
    {
        if (e.T == null) return e.Enabled;
        foreach (var r in e.T.GetComponentsInChildren<Renderer>(true)) return r.enabled;
        return true;
    }

    void Show(Ent e, bool on, bool solidToo = true)
    {
        if (e.T == null) return;
        foreach (var r in e.T.GetComponentsInChildren<Renderer>(true)) r.enabled = on;
        if (solidToo) SetSolid(e, on);
    }

    void SetSolid(Ent e, bool on)
    {
        if (e.T == null) return;
        foreach (var c in e.T.GetComponentsInChildren<Collider>(true)) c.enabled = on;
    }

    // ------------------------------------------------------------------ doors and buttons
    void Open(Ent e, Ent by)
    {
        if (e.Locked) { Fire(e, "OnLockedUse"); return; }
        if (e.Dir > 0 || (e.Pos >= 1f && e.Dir == 0)) { e.WaitLeft = e.Wait; return; }
        // a door that opens both ways (a prop door's way): away from the hero
        // (Source: a prop door with "opendir" both; a func_door_rotating turning about the up axis that isn't "one way")
        bool bothWays = e.Class == "prop_door_rotating" ? e.I("opendir") == 0 : e.Class == "func_door_rotating" && !e.Flag(16) && !e.Flag(64) && !e.Flag(128);
        if (e.Turns && e.Pos <= 0f && bothWays && hero != null && e.T != null)
        {
            var rs = e.T.GetComponentsInChildren<Renderer>();
            if (rs.Length > 0)
            {
                var b = rs[0].bounds;
                foreach (var r in rs) b.Encapsulate(r.bounds);
                Vector3 swing = Vector3.Cross(e.Axis, b.center - e.T.position) * Mathf.Sign(e.Angle);
                if (Vector3.Dot(swing, b.center - heroPos) < 0f) e.Angle = -e.Angle;
            }
        }
        e.Dir = 1f;
        Fire(e, "OnOpen");
    }

    void Close(Ent e)
    {
        if (e.Pos <= 0f && e.Dir == 0) return;
        e.Dir = -1f;
        Fire(e, "OnClose");
    }

    void Press(Ent e, Ent by, bool viaInput = false)
    {
        if (e.Locked) { Fire(e, "OnUseLocked"); return; }
        if (e.Dir != 0 && !viaInput) return; // (still moving)
        bool toggle = e.Flag(32);
        if (toggle && e.Pos >= 1f) { e.Dir = -1f; Fire(e, "OnOut"); Fire(e, "OnPressed"); return; }
        e.Dir = 1f;
        Fire(e, "OnPressed");
        Fire(e, "OnIn");
    }

    void SetPos(Ent e, float p)
    {
        e.Pos = Mathf.Clamp01(p);
        if (e.T == null) return;
        if (e.Turns) e.T.rotation = Quaternion.AngleAxis(e.Angle * e.Pos, e.Axis) * e.HomeRot;
        else e.T.position = Vector3.Lerp(e.HomePos, e.MoveTo, e.Pos);
    }

    void StepMover(Ent e, float dt)
    {
        if (e.Dir == 0f)
        {
            // open, waiting to go back
            if (e.Pos >= 1f && e.WaitLeft > 0f)
            {
                e.WaitLeft -= dt;
                if (e.WaitLeft <= 0f)
                {
                    e.Dir = -1f;
                    Fire(e, e.Class.EndsWith("button") ? "OnOut" : "OnClose");
                }
            }
            return;
        }
        float len = e.Turns ? Mathf.Abs(e.Angle) : Vector3.Distance(e.HomePos, e.MoveTo);
        float step = len > 1e-4f ? e.Speed * dt / len : 1f;
        float target = e.Class == "func_movelinear" && e.Value > 0f && e.Value < 1f ? e.Value : (e.Dir > 0 ? 1f : 0f);
        float p = Mathf.MoveTowards(e.Pos, target, step);
        SetPos(e, p);
        if (Mathf.Approximately(p, target))
        {
            bool open = e.Dir > 0;
            e.Dir = 0f;
            e.Value = 0f;
            if (open)
            {
                Fire(e, e.Class == "func_movelinear" ? "OnFullyOpen" : e.Class.EndsWith("button") ? "OnIn" : "OnFullyOpen");
                // back again after its wait (-1: stays; a toggle stays)
                bool toggle = e.Flag(32) && (e.Class.EndsWith("button") || e.Class.StartsWith("func_door"));
                e.WaitLeft = e.Wait >= 0f && !toggle && e.Class != "func_movelinear" ? Mathf.Max(e.Wait, 0.01f) : 0f;
            }
            else Fire(e, e.Class == "func_movelinear" ? "OnFullyClosed" : e.Class.EndsWith("button") ? "OnOut" : "OnFullyClosed");
        }
    }

    // ------------------------------------------------------------------ trains
    void TrainStart(Ent e)
    {
        e.TrainMax = SrcLen(Mathf.Max(e.F("speed", 100f), e.F("startspeed", 0f)));
        var first = Find(e.Get("target"), e);
        if (first.Count == 0) return;
        // which way it was built facing: Source's says its +x, but trains are built along their track (a car parented
        // to it pointing down the rails), so the way of its first stretch: it starts as it was placed, then turns
        // with the track (a train's MoveTo holds that way)
        var second = Find(first[0].Get("target"), first[0]);
        e.MoveTo = second.Count > 0 && second[0] != first[0] ? (SrcPoint(second[0].SrcOrigin) - SrcPoint(first[0].SrcOrigin)).normalized : Vector3.zero;
        TrainTo(e, first[0], true);
        // func_train starts going unless it waits for an input; a tracktrain with a start speed goes
        if (e.Class == "func_train" ? e.Name == "" || !e.Flag(1) : e.F("startspeed") > 0f) e.TrainSpeed = e.Class == "func_train" ? SrcLen(e.F("speed", 100f)) : SrcLen(e.F("startspeed"));
        if (e.Class == "func_train") e.TrainMax = SrcLen(e.F("speed", 100f));
    }

    Vector3 TrainSpot(Ent e, Ent node) => SrcPoint(node.SrcOrigin + new Vector3(0f, 0f, e.Class == "func_tracktrain" ? e.F("height") : 0f));

    bool TrainTurns(Ent e) => e.Class == "func_tracktrain" && !e.Flag(16) && e.I("orientationtype", 1) != 0;

    // a tracktrain faces the way it goes: its own +x (Source's) along it
    Quaternion TrainFacing(Ent e, Vector3 d)
    {
        if (e.Flag(1)) d.y = 0f; // "no pitch"
        Vector3 home = e.MoveTo.sqrMagnitude > 1e-6f ? e.MoveTo : SrcDir(new Vector3(1f, 0f, 0f));
        if (e.Flag(1)) home.y = 0f;
        return d.sqrMagnitude < 1e-6f || home.sqrMagnitude < 1e-6f ? e.T.rotation : Quaternion.LookRotation(d) * Quaternion.Inverse(Quaternion.LookRotation(home)) * e.HomeRot;
    }

    void TrainTo(Ent e, Ent node, bool jump)
    {
        e.Path = node;
        if (jump && e.T != null)
        {
            // (Source: its origin onto the node, turned toward the next one)
            e.T.position = TrainSpot(e, node);
            var next = Find(node.Get("target"), node);
            if (next.Count > 0) e.Path = next[0];
            if (TrainTurns(e) && next.Count > 0) e.T.rotation = TrainFacing(e, AheadOnTrack(e, 1f) - e.T.position);
        }
    }

    void StepTrain(Ent e, float dt)
    {
        if (e.T == null || e.Path == null || Mathf.Abs(e.TrainSpeed) < 1e-4f || !e.Enabled) return;
        float left = Mathf.Abs(e.TrainSpeed) * dt;
        for (int guard = 0; guard < 8 && left > 0f; guard++)
        {
            Ent goal = e.TrainSpeed > 0 ? e.Path : PrevNode(e.Path);
            if (goal == null) { e.TrainSpeed = 0f; return; }
            Vector3 to = TrainSpot(e, goal);
            Vector3 d = to - e.T.position;
            float dist = d.magnitude;
            if (dist > left)
            {
                e.T.position += d / dist * left;
                TrainFace(e);
                return;
            }
            e.T.position = to;
            left -= dist;
            // the node passed: its outputs, its speed, the next one
            Fire(goal, "OnPass");
            float sp = goal.F("speed");
            if (sp > 0f) { e.TrainMax = SrcLen(sp); e.TrainSpeed = Mathf.Sign(e.TrainSpeed) * e.TrainMax; }
            float wait = goal.F("wait");
            var next = e.TrainSpeed > 0 ? Find(goal.Get("target"), goal) : null;
            if (e.TrainSpeed > 0)
            {
                if (next == null || next.Count == 0) { e.TrainSpeed = 0f; e.Path = goal; Fire(e, "OnArrivedAtDestinationNode"); return; }
                e.Path = next[0];
            }
            else e.Path = goal;
            if (e.Class == "func_train" && wait != 0f) { e.TrainSpeed = 0f; if (wait > 0f) ioQueue.Add((entClock + wait, e, "!self", "Start", "")); return; }
        }
        TrainFace(e);
    }

    // Source's: facing from its origin to the point its "wheels" (length) further along the track, every step: it
    // turns as it goes round a corner, never sideways
    void TrainFace(Ent e)
    {
        if (!TrainTurns(e)) return;
        Vector3 ahead = AheadOnTrack(e, e.TrainSpeed >= 0f ? 1f : -1f);
        Vector3 d = ahead - e.T.position;
        if (d.sqrMagnitude > 1e-6f) e.T.rotation = TrainFacing(e, e.TrainSpeed >= 0f ? d : -d);
    }

    Vector3 AheadOnTrack(Ent e, float way)
    {
        float left = SrcLen(Mathf.Max(e.F("wheels", 50f), 8f));
        Vector3 p = e.T.position;
        Ent goal = way > 0 ? e.Path : PrevNode(e.Path);
        for (int guard = 0; guard < 16 && goal != null; guard++)
        {
            Vector3 to = TrainSpot(e, goal);
            float dist = Vector3.Distance(p, to);
            if (dist >= left) return p + (to - p) / Mathf.Max(dist, 1e-6f) * left;
            left -= dist;
            p = to;
            if (way > 0) { var n = Find(goal.Get("target"), goal); goal = n.Count > 0 ? n[0] : null; }
            else goal = PrevNode(goal);
        }
        // the track's end: on along its last stretch
        return p + (p - e.T.position).normalized * left;
    }

    Ent PrevNode(Ent node)
    {
        foreach (var e in ents)
            if ((e.Class == "path_track" || e.Class == "path_corner") && string.Equals(e.Get("target"), node.Name, StringComparison.OrdinalIgnoreCase)) return e;
        return null;
    }

    // ------------------------------------------------------------------ sounds
    void SoundSetup(Ent e)
    {
        e.Volume = Mathf.Clamp01(e.F("health", 10f) / 10f);
        string file = e.Get("message").Replace('\\', '/');
        while (file.Length > 0 && !char.IsLetterOrDigit(file[0])) file = file.Substring(1);
        string path = Path.Combine(entSounds, file.ToLowerInvariant());
        if (!File.Exists(path)) return;
        var go = new GameObject("sound " + e.Name + " " + Path.GetFileName(path));
        go.transform.SetParent(mapRoot.transform, false);
        go.transform.position = SrcPoint(e.SrcOrigin);
        var a = go.AddComponent<AudioSource>();
        a.playOnAwake = false;
        a.loop = !e.Flag(32);              // "is NOT looped"
        a.spatialBlend = e.Flag(1) ? 0f : 1f; // "play everywhere"
        a.rolloffMode = AudioRolloffMode.Linear;
        a.minDistance = 1f;
        a.maxDistance = Mathf.Max(2f, SrcLen(e.F("radius", 1250f)));
        a.dopplerLevel = 0f;
        a.volume = e.Volume;
        a.pitch = Mathf.Clamp(e.F("pitch", 100f) / 100f, 0.1f, 3f);
        a.clip = Wav(path);
        e.Audio = a;
        if (a.clip != null && !e.Flag(16)) a.Play(); // (not "start silent")
    }

    void SoundPlay(Ent e, bool on)
    {
        if (e.Audio == null || e.Audio.clip == null) return;
        if (on) { e.Audio.volume = e.Volume; e.Audio.Stop(); e.Audio.Play(); }
        else e.Audio.Stop();
    }

    // A .wav's samples (PCM 8 / 16 / 24 bit, or float), as Source's sounds are
    static AudioClip Wav(string path)
    {
        try
        {
            byte[] d = File.ReadAllBytes(path);
            if (d.Length < 44 || d[0] != 'R' || d[1] != 'I' || d[2] != 'F' || d[3] != 'F') return null;
            int fmt = 0, ch = 1, rate = 44100, bits = 16, at = 12;
            int dataAt = -1, dataLen = 0;
            while (at + 8 <= d.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(d, at, 4);
                int len = BitConverter.ToInt32(d, at + 4);
                if (len < 0) break;
                if (id == "fmt ")
                {
                    fmt = BitConverter.ToInt16(d, at + 8);
                    ch = BitConverter.ToInt16(d, at + 10);
                    rate = BitConverter.ToInt32(d, at + 12);
                    bits = BitConverter.ToInt16(d, at + 22);
                }
                else if (id == "data") { dataAt = at + 8; dataLen = Mathf.Min(len, d.Length - dataAt); break; }
                at += 8 + len + (len & 1);
            }
            if (dataAt < 0 || ch < 1 || (fmt != 1 && fmt != 3 && fmt != -2)) return null;
            int bytes = bits / 8;
            int n = dataLen / bytes;
            var s = new float[n];
            for (int i = 0; i < n; i++)
            {
                int o = dataAt + i * bytes;
                s[i] = fmt == 3 && bits == 32 ? BitConverter.ToSingle(d, o)
                    : bits == 8 ? (d[o] - 128) / 128f
                    : bits == 16 ? BitConverter.ToInt16(d, o) / 32768f
                    : bits == 24 ? ((d[o] | d[o + 1] << 8 | (sbyte)d[o + 2] << 16) / 8388608f)
                    : BitConverter.ToInt32(d, o) / 2147483648f;
            }
            var clip = AudioClip.Create(Path.GetFileNameWithoutExtension(path), n / ch, ch, rate, false);
            clip.SetData(s, 0);
            return clip;
        }
        catch (Exception ex) { Debug.LogWarning("[Sandbox] map sound " + path + ": " + ex.Message); return null; }
    }

    // ------------------------------------------------------------------ timers
    void ArmTimer(Ent e)
    {
        float t = e.I("UseRandomTime") == 1 ? UnityEngine.Random.Range(e.F("LowerRandomBound"), Mathf.Max(e.F("LowerRandomBound"), e.F("UpperRandomBound"))) : e.F("RefireTime", 1f);
        e.NextFire = entClock + Mathf.Max(t, 0.05f);
    }

    // ------------------------------------------------------------------ each frame (before the hero moves)
    void EntsUpdate(float dt)
    {
        if (ents.Count == 0 || mapRoot == null) return;
        entClock += dt;
        // what's due
        for (int guard = 0; guard < 256; guard++)
        {
            int k = -1;
            for (int j = 0; j < ioQueue.Count; j++)
                if (ioQueue[j].at <= entClock && (k < 0 || ioQueue[j].at < ioQueue[k].at)) k = j;
            if (k < 0) break;
            var q = ioQueue[k];
            ioQueue.RemoveAt(k);
            foreach (var t in Find(q.target, q.caller)) EntInput(t, q.input, q.param, q.caller);
        }
        // the hero's place before the movers move (a lift carries him)
        Ent under = null;
        Matrix4x4 underWas = Matrix4x4.identity;
        if (heroOn && hero != null && heroGround && heroCC != null &&
            Physics.Raycast(heroPos + Vector3.up * 0.3f, Vector3.down, out var hit, 0.7f, ~(1 << 2), QueryTriggerInteraction.Ignore) &&
            entOfCollider.TryGetValue(hit.collider, out under) && under.T != null)
            underWas = under.T.localToWorldMatrix;
        bool moved = false;
        foreach (var e in ents)
        {
            if (e.Killed) continue;
            switch (e.Class)
            {
                case "func_door":
                case "func_door_rotating":
                case "prop_door_rotating":
                case "func_button":
                case "func_rot_button":
                case "func_movelinear":
                case "func_water_analog":
                    if (e.Dir != 0f || e.WaitLeft > 0f) { StepMover(e, dt); moved = true; }
                    break;
                case "func_rotating":
                    e.Spin = Mathf.MoveTowards(e.Spin, e.SpinTarget, Mathf.Max(Mathf.Abs(e.SpinMax), 1f) * dt * 0.5f);
                    if (Mathf.Abs(e.Spin) > 0.01f && e.T != null) { e.T.rotation = Quaternion.AngleAxis(-e.Spin * dt, e.Axis) * e.T.rotation; moved = true; }
                    break;
                case "func_tracktrain":
                case "func_train":
                    if (Mathf.Abs(e.TrainSpeed) > 1e-4f) { StepTrain(e, dt); moved = true; }
                    break;
                case "logic_timer":
                    if (e.Enabled && e.NextFire >= 0f && entClock >= e.NextFire) { Fire(e, "OnTimer"); ArmTimer(e); }
                    break;
                case "phys_motor":
                    e.Spin = Mathf.MoveTowards(e.Spin, e.SpinTarget, Mathf.Max(Mathf.Abs(e.SpinMax), 1f) * dt);
                    if (e.Attached != null && e.Attached.T != null && !e.Attached.Killed && Mathf.Abs(e.Spin) > 0.01f)
                    {
                        e.Attached.T.RotateAround(e.Hub, e.Axis, -e.Spin * dt);
                        moved = true;
                    }
                    break;
            }
        }
        if (moved) Physics.SyncTransforms();
        if (under != null && under.T != null)
        {
            Matrix4x4 now = under.T.localToWorldMatrix;
            Vector3 p = now.MultiplyPoint3x4(underWas.inverse.MultiplyPoint3x4(heroPos));
            if ((p - heroPos).sqrMagnitude > 1e-10f)
            {
                Vector3 f0 = underWas.inverse.MultiplyVector(Quaternion.Euler(0f, heroYaw, 0f) * Vector3.forward);
                Vector3 f1 = now.MultiplyVector(f0);
                heroYaw = Mathf.Atan2(f1.x, f1.z) * Mathf.Rad2Deg;
                heroPos = p;
                PlaceMover();
            }
        }
        if (heroOn && hero != null) EntTouches();
        useTarget = heroOn && hero != null ? FindUse() : null;
    }

    Bounds WorldBox(Ent e)
    {
        Vector3 a = SrcPoint(e.SrcMins), b = SrcPoint(e.SrcMaxs);
        var box = new Bounds((a + b) * 0.5f, new Vector3(Mathf.Abs(a.x - b.x), Mathf.Abs(a.y - b.y), Mathf.Abs(a.z - b.z)));
        if (e.T != null) box.center += e.T.position - e.HomePos;
        return box;
    }

    // a point in its box (one that moves and turns: in its own space), the box grown by `grow` metres
    bool InBox(Ent e, Vector3 p, float grow)
    {
        if (e.T == null) { var b = WorldBox(e); b.Expand(grow * 2f); return b.Contains(p); }
        var lb = e.LocalBox;
        float k = e.T.lossyScale.x > 1e-6f ? 1f / e.T.lossyScale.x : 1f;
        lb.Expand(grow * 2f * k);
        return lb.Contains(e.T.InverseTransformPoint(p));
    }

    // the nearest point of its box to p
    Vector3 BoxNear(Ent e, Vector3 p)
    {
        if (e.T == null) return WorldBox(e).ClosestPoint(p);
        return e.T.TransformPoint(e.LocalBox.ClosestPoint(e.T.InverseTransformPoint(p)));
    }

    // triggers the hero walks into
    void EntTouches()
    {
        Vector3 chest = heroPos + Vector3.up * 0.9f;
        foreach (var e in ents)
        {
            if (!e.HasBox || e.Killed) continue;
            bool trig = e.Class.StartsWith("trigger_");
            bool touchDoor = (e.Class == "func_door" || e.Class == "func_door_rotating") && e.Flag(1024);
            bool touchButton = e.Class == "func_button" && e.Flag(256);
            if (!trig && !touchDoor && !touchButton) continue;
            float grow = trig ? 0f : 0.2f;
            bool inside = e.Enabled && (InBox(e, chest, grow) || InBox(e, heroPos + Vector3.up * 0.2f, grow));
            if (inside && !e.Inside)
            {
                e.Inside = true;
                if (touchDoor) Open(e, null);
                else if (touchButton) Press(e, null);
                else EnterTrigger(e);
            }
            else if (!inside && e.Inside)
            {
                e.Inside = false;
                if (trig) { Fire(e, "OnEndTouch"); Fire(e, "OnEndTouchAll"); }
            }
            if (inside && e.Class == "trigger_push") Push(e);
        }
    }

    void EnterTrigger(Ent e)
    {
        Fire(e, "OnStartTouch");
        Fire(e, "OnStartTouchAll");
        Fire(e, "OnTrigger");
        switch (e.Class)
        {
            case "trigger_once":
                e.Killed = true;
                break;
            case "trigger_teleport":
            {
                var to = Find(e.Get("target"), e);
                if (to.Count == 0) break;
                Vector3 at = SrcPoint(to[0].SrcOrigin);
                // facing its yaw (Source's: counter-clockwise from +x)
                float a = Vec(to[0].Get("angles")).y * Mathf.Deg2Rad;
                Vector3 d = SrcDir(new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f));
                HeroPlace(at, Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg);
                PlaceMover();
                Fire(e, "OnTeleport");
                break;
            }
            case "trigger_hurt":
                if (hero != null && e.F("damage", 10f) >= 100f) hero.Health = 0f;
                break;
        }
    }

    void Push(Ent e)
    {
        Vector3 dir = SrcDir(SrcForward(Vec(e.Get("pushdir"))));
        float sp = Mathf.Min(SrcLen(e.F("speed", 40f)), 40f);
        heroVel = Vector3.MoveTowards(heroVel, new Vector3(dir.x, 0f, dir.z) * sp, sp * Time.deltaTime * 4f);
        if (dir.y > 0.2f) { heroVy = Mathf.Max(heroVy, dir.y * Mathf.Min(sp, 12f)); heroGround = false; }
    }

    // ------------------------------------------------------------------ the Use button
    static bool Usable(Ent e)
    {
        if (e.Killed || !e.HasBox && e.T == null) return false;
        switch (e.Class)
        {
            case "func_button":
            case "func_rot_button":
            case "momentary_rot_button":
                return !(e.Flag(256) && !e.Flag(1024)); // not "touch only"
            case "func_door":
            case "func_door_rotating":
                return e.Flag(256) || (e.Name == "" && !e.Flag(1024));
            case "prop_door_rotating":
                return !e.Flag(32768);
        }
        return false;
    }

    Ent FindUse()
    {
        Vector3 chest = heroPos + Vector3.up * 1.2f;
        Vector3 look = cam != null ? cam.transform.forward : Vector3.forward;
        Ent best = null;
        float bestScore = float.MaxValue;
        foreach (var e in ents)
        {
            if (!Usable(e)) continue;
            Vector3 near, centre;
            if (e.HasBox) { near = BoxNear(e, chest); centre = e.T != null ? e.T.TransformPoint(e.LocalBox.center) : WorldBox(e).center; }
            else
            {
                var rs = e.T.GetComponentsInChildren<Renderer>();
                if (rs.Length == 0) continue;
                var box = rs[0].bounds;
                foreach (var r in rs) box.Encapsulate(r.bounds);
                near = box.ClosestPoint(chest);
                centre = box.center;
            }
            float dist = Vector3.Distance(near, chest);
            if (dist > 1.6f) continue;
            Vector3 to = (centre - chest);
            float facing = to.sqrMagnitude > 1e-4f ? Vector3.Dot(look, to.normalized) : 1f;
            if (facing < -0.1f) continue;
            float score = dist - facing * 0.6f;
            if (score < bestScore) { bestScore = score; best = e; }
        }
        return best;
    }

    void UseNow()
    {
        var e = useTarget;
        if (e == null) return;
        Fire(e, "OnPlayerUse");
        if (e.Class == "func_button" || e.Class == "func_rot_button" || e.Class == "momentary_rot_button") Press(e, null);
        else if (e.Dir > 0 || e.Pos >= 1f) Close(e);
        else Open(e, null);
    }
}
