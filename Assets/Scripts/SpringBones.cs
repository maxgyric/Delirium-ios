// Physics bones: hair, breasts, skirts, tails and accessories that swing as the man walks, falls and gets up.
//
// The VRM spring bone model (VRM 1.0's VRMC_springBone and VRM 0.x's secondaryAnimation; the same sums as UniVRM):
// each bone of a chain has a tip (the next bone, or a short stub past the last one). Every frame the tip keeps some of
// its last move (1 - drag), is pulled back toward the bone's rest direction (stiffness) and along gravity, is held at
// the bone's length from the bone, and is pushed out of the body's colliders (spheres and capsules on its bones);
// then the bone is turned to point at it.
//
// A file without spring settings: its extra bones are picked by name (hair, breast, skirt, tail, earring...) and get
// a preset each, and the colliders are made from the body's own bones.
//
// Set up once on a character's template (Setup); every man copied from it carries the settings (the Transforms in
// them point into his own copy: Unity's Instantiate does that) and runs them after his final pose each frame.
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

[DefaultExecutionOrder(200)] // after DeliriumRagdolls and Game.LateUpdate (100): the body's pose is final
public class SpringBones : MonoBehaviour
{
    /// Settings > Physics bones.
    public static bool Enabled = true;

    [Serializable]
    public class Joint
    {
        public Transform Bone;          // the bone this joint turns
        public Vector3 Tip;             // its tip in the bone's own space (the next bone's place, or a stub)
        public Quaternion RestLocal;    // the bone's local rotation in the bind pose
        public float Stiffness, Drag, Gravity;
        public Vector3 GravityDir;      // in the world
        public float Radius;            // the tip's own radius, in the bone's units (scaled by it)
        public Transform Center;        // its swing measured in this bone's space (VRM's "center"); null: the world
        public float Immobile = 1f;     // how much of Center's own motion it ignores (VRChat's immobile; VRM: all)
        public float MaxAngle;          // how far from its rest it may swing, degrees (0: any way; a VRChat PhysBone's limit)
        public int[] Colliders = new int[0];
        [NonSerialized] public Vector3 Prev, Curr; // the tip now and a frame ago, in the world
    }

    [Serializable]
    public class Col
    {
        public Transform Bone;
        public Vector3 Offset, Tail;    // in the bone's own space; Tail is a capsule's other end
        public float Radius;            // in the bone's units
        public bool Capsule;
    }

    public List<Joint> Joints = new List<Joint>();  // a bone's joint before its children's
    public List<Col> Cols = new List<Col>();
    public string Source = "";

    bool running, wasEnabled;
    Vector3 lastAnchor;

    // ---------------------------------------------------------------------------------------------------- run time

    void OnEnable() => running = false;

    readonly Dictionary<Transform, Matrix4x4> centerLast = new Dictionary<Transform, Matrix4x4>(), centerMove = new Dictionary<Transform, Matrix4x4>();

    void Restart()
    {
        foreach (var j in Joints)
        {
            if (j.Bone == null) continue;
            Vector3 tip = j.Bone.TransformPoint(j.Tip);
            j.Prev = j.Curr = tip;
        }
        if (Joints.Count > 0 && Joints[0].Bone != null) lastAnchor = Joints[0].Bone.position;
        centerLast.Clear();
        running = true;
    }

    /// Back to the bind pose's own turn (the setting turned off).
    void Rest()
    {
        foreach (var j in Joints)
            if (j.Bone != null) j.Bone.localRotation = j.RestLocal;
    }

    void LateUpdate()
    {
        if (!Enabled)
        {
            if (wasEnabled) Rest();
            wasEnabled = false;
            running = false;
            return;
        }
        wasEnabled = true;
        if (Joints.Count == 0) return;
        float dt = Mathf.Min(Time.deltaTime, 1f / 30f);
        if (dt <= 0f) return; // paused
        // put somewhere else in one go (reset, a new man): no swing from where he was
        var anchor = Joints[0].Bone;
        if (anchor == null) return;
        if (!running || (anchor.position - lastAnchor).sqrMagnitude > 4f) Restart();
        lastAnchor = anchor.position;
        // how each centre moved since the last frame (a joint takes its Immobile share of that with it)
        centerMove.Clear();
        foreach (var j in Joints)
        {
            if (j.Center == null || centerMove.ContainsKey(j.Center)) continue;
            var now = j.Center.localToWorldMatrix;
            centerMove[j.Center] = centerLast.TryGetValue(j.Center, out var last) ? now * last.inverse : Matrix4x4.identity;
            centerLast[j.Center] = now;
        }

        foreach (var j in Joints)
        {
            var bone = j.Bone;
            if (bone == null) continue;
            float scale = Mathf.Abs(bone.lossyScale.x);
            if (scale < 1e-6f) continue; // a cut-off limb: shrunk to nothing
            Vector3 head = bone.position;
            Quaternion parent = bone.parent != null ? bone.parent.rotation : Quaternion.identity;
            Quaternion rest = parent * j.RestLocal;
            Vector3 restDir = rest * Vector3.Scale(j.Tip, bone.lossyScale);
            float length = restDir.magnitude;
            if (length < 1e-6f) continue;
            restDir /= length;

            Vector3 curr = j.Curr, prev = j.Prev;
            if (j.Center != null && centerMove.TryGetValue(j.Center, out var move))
            {
                curr = Vector3.Lerp(curr, move.MultiplyPoint3x4(curr), j.Immobile);
                prev = Vector3.Lerp(prev, move.MultiplyPoint3x4(prev), j.Immobile);
            }
            Vector3 next = curr + (curr - prev) * (1f - j.Drag) + restDir * (j.Stiffness * dt) + j.GravityDir * (j.Gravity * dt);
            next = head + (next - head).normalized * length;

            // out of the body
            float own = j.Radius * scale;
            foreach (int ci in j.Colliders)
            {
                if (ci < 0 || ci >= Cols.Count) continue;
                var c = Cols[ci];
                if (c.Bone == null) continue;
                float cs = Mathf.Abs(c.Bone.lossyScale.x);
                if (cs < 1e-6f) continue;
                Vector3 a = c.Bone.TransformPoint(c.Offset);
                Vector3 centre = a;
                if (c.Capsule)
                {
                    Vector3 b = c.Bone.TransformPoint(c.Tail), ab = b - a;
                    float t = ab.sqrMagnitude > 1e-12f ? Mathf.Clamp01(Vector3.Dot(next - a, ab) / ab.sqrMagnitude) : 0f;
                    centre = a + ab * t;
                }
                float r = own + c.Radius * cs;
                Vector3 away = next - centre;
                if (away.sqrMagnitude >= r * r) continue;
                float d = away.magnitude;
                Vector3 n = d > 1e-6f ? away / d : restDir;
                next = centre + n * r;
                next = head + (next - head).normalized * length;
            }

            // no further from its rest than its limit (a breast doesn't swing through the chest)
            if (j.MaxAngle > 0f)
            {
                Vector3 dir = (next - head) / length;
                float ang = Vector3.Angle(restDir, dir);
                if (ang > j.MaxAngle) next = head + Vector3.Slerp(restDir, dir, j.MaxAngle / ang) * length;
            }

            j.Prev = j.Curr;
            j.Curr = next;
            bone.rotation = Quaternion.FromToRotation(restDir, (next - head).normalized) * rest;
        }
    }

    // ------------------------------------------------------------------------------------------------------- set-up

    static Vector3 V(object o, string key)              // a VRM 1.0 vector [x, y, z], glTF -> Unity (x mirrored)
    {
        float[] a = Json.Nums(o, key);
        return a != null && a.Length >= 3 ? new Vector3(-a[0], a[1], a[2]) : Vector3.zero;
    }

    static Vector3 V0(object o, string key)             // a VRM 0.x vector {x, y, z}
    {
        object v = Json.Get(o, key);
        return v == null ? Vector3.zero : new Vector3(-Json.Num(v, "x"), Json.Num(v, "y"), Json.Num(v, "z"));
    }

    static Transform Node(GlbScene.Made made, int i) =>
        made.Nodes != null && i >= 0 && i < made.Nodes.Length && made.Nodes[i] != null ? made.Nodes[i].transform : null;

    /// The tip of a bone with no next joint: past it by 7 cm, along the line from its parent (UniVRM's stub).
    static Vector3 Stub(Transform bone)
    {
        Vector3 dir = bone.parent != null ? bone.position - bone.parent.position : Vector3.up;
        if (dir.sqrMagnitude < 1e-10f) dir = bone.rotation * Vector3.up;
        return bone.InverseTransformPoint(bone.position + dir.normalized * 0.07f * Mathf.Abs(bone.lossyScale.x));
    }

    /// Reads the model's spring bones (or picks them by name) onto its template. A line for the log / the files note.
    public static string Setup(GameObject template, GlbScene.Made made)
    {
        var sb = template.AddComponent<SpringBones>();
        object ext = Json.Get(made.File?.Root, "extensions");
        object vrm1 = Json.Get(ext, "VRMC_springBone");
        object vrm0 = Json.Get(Json.Get(ext, "VRM"), "secondaryAnimation");
        if (vrm1 != null) sb.FromVrm1(made, vrm1);
        else if (vrm0 != null) sb.FromVrm0(made, vrm0);
        if (sb.Joints.Count == 0) sb.ByName(template.transform, made);
        if (sb.Joints.Count == 0)
        {
            Destroy(sb);
            return "";
        }
        return sb.Joints.Count + " physics bones (" + sb.Source + ")";
    }

    void Add(Transform bone, Vector3 tip, float stiffness, float drag, float gravity, Vector3 gravityDir, float radius,
             Transform center, int[] colliders, HashSet<Transform> taken)
    {
        if (bone == null || !taken.Add(bone)) return;
        Joints.Add(new Joint
        {
            Bone = bone, Tip = tip, RestLocal = bone.localRotation,
            Stiffness = stiffness, Drag = Mathf.Clamp01(drag), Gravity = gravity,
            GravityDir = gravityDir.sqrMagnitude > 1e-8f ? gravityDir.normalized : Vector3.down,
            Radius = radius, Center = center, Colliders = colliders,
        });
    }

    // VRM 1.0: springs of joints (each joint's tip is the next one's bone; the last only ends the one before it),
    // colliders in groups, sphere or capsule.
    void FromVrm1(GlbScene.Made made, object src)
    {
        Source = "VRM 1.0";
        foreach (object c in Json.Arr(Json.Get(src, "colliders")) ?? new List<object>())
        {
            object shape = Json.Get(c, "shape");
            object sphere = Json.Get(shape, "sphere"), capsule = Json.Get(shape, "capsule");
            object s = capsule ?? sphere;
            Cols.Add(new Col
            {
                Bone = Node(made, Json.Int(c, "node")),
                Offset = V(s, "offset"), Tail = capsule != null ? V(capsule, "tail") : Vector3.zero,
                Radius = Json.Num(s, "radius"), Capsule = capsule != null,
            });
        }
        var groups = new List<int[]>();
        foreach (object g in Json.Arr(Json.Get(src, "colliderGroups")) ?? new List<object>())
            groups.Add(Json.Ints(g, "colliders") ?? new int[0]);
        var taken = new HashSet<Transform>();
        foreach (object spring in Json.Arr(Json.Get(src, "springs")) ?? new List<object>())
        {
            var cols = new List<int>();
            foreach (int gi in Json.Ints(spring, "colliderGroups") ?? new int[0])
                if (gi >= 0 && gi < groups.Count) cols.AddRange(groups[gi]);
            Transform center = Node(made, Json.Int(spring, "center"));
            var joints = Json.Arr(Json.Get(spring, "joints")) ?? new List<object>();
            for (int k = 0; k + 1 < joints.Count; k++)
            {
                var bone = Node(made, Json.Int(joints[k], "node"));
                var next = Node(made, Json.Int(joints[k + 1], "node"));
                if (bone == null || next == null) continue;
                object p = joints[k];
                Vector3 g = Json.Has(p, "gravityDir") ? V(p, "gravityDir") : Vector3.down;
                Add(bone, bone.InverseTransformPoint(next.position), Json.Num(p, "stiffness", 1f), Json.Num(p, "dragForce", 0.5f),
                    Json.Num(p, "gravityPower", 0f), g, Json.Num(p, "hitRadius", 0f), center, cols.ToArray(), taken);
                if (Joints.Count > 0 && Joints[Joints.Count - 1].Bone == bone)
                {
                    Joints[Joints.Count - 1].MaxAngle = Json.Num(p, "maxAngle", 0f);
                    Joints[Joints.Count - 1].Immobile = Mathf.Clamp01(Json.Num(spring, "immobile", 1f));
                }
            }
        }
    }

    // VRM 0.x: bone groups, each a list of root bones whose whole trees swing with the group's settings (every bone's
    // tip its first child, a leaf's a stub); colliders: spheres in groups, one bone a group.
    void FromVrm0(GlbScene.Made made, object src)
    {
        Source = "VRM 0.x";
        var groups = new List<int[]>();
        foreach (object g in Json.Arr(Json.Get(src, "colliderGroups")) ?? new List<object>())
        {
            var bone = Node(made, Json.Int(g, "node"));
            var mine = new List<int>();
            foreach (object c in Json.Arr(Json.Get(g, "colliders")) ?? new List<object>())
            {
                mine.Add(Cols.Count);
                Cols.Add(new Col { Bone = bone, Offset = V0(c, "offset"), Radius = Json.Num(c, "radius") });
            }
            groups.Add(mine.ToArray());
        }
        var taken = new HashSet<Transform>();
        foreach (object grp in Json.Arr(Json.Get(src, "boneGroups")) ?? new List<object>())
        {
            var cols = new List<int>();
            foreach (int gi in Json.Ints(grp, "colliderGroups") ?? new int[0])
                if (gi >= 0 && gi < groups.Count) cols.AddRange(groups[gi]);
            float stiff = Json.Num(grp, "stiffiness", Json.Num(grp, "stiffness", 1f)); // (sic: the spec's spelling)
            float drag = Json.Num(grp, "dragForce", 0.4f), grav = Json.Num(grp, "gravityPower", 0f), r = Json.Num(grp, "hitRadius", 0.02f);
            Vector3 dir = Json.Has(grp, "gravityDir") ? V0(grp, "gravityDir") : Vector3.down;
            Transform center = Node(made, Json.Int(grp, "center"));
            int[] arr = cols.ToArray();
            foreach (int root in Json.Ints(grp, "bones") ?? new int[0])
                Tree(Node(made, root), t => Add(t, t.childCount > 0 ? t.InverseTransformPoint(t.GetChild(0).position) : Stub(t),
                                                stiff, drag, grav, dir, r, center, arr, taken));
        }
    }

    static void Tree(Transform t, Action<Transform> each)
    {
        if (t == null) return;
        each(t);
        for (int i = 0; i < t.childCount; i++) Tree(t.GetChild(i), each);
    }

    // ---------------------------------------------------------------------------------------- by name, no settings

    enum Kind { None, Hair, Breast, Cloth, Tail, Accessory }

    // (stiffness, drag, gravity, tip radius in metres)
    static readonly Dictionary<Kind, (float s, float d, float g, float r)> kPreset = new Dictionary<Kind, (float, float, float, float)>
    {
        { Kind.Hair, (1.0f, 0.4f, 0.1f, 0.02f) },
        { Kind.Breast, (3.0f, 0.2f, 0.0f, 0.03f) },
        { Kind.Cloth, (0.8f, 0.4f, 0.3f, 0.03f) },
        { Kind.Tail, (1.5f, 0.3f, 0.05f, 0.02f) },
        { Kind.Accessory, (1.0f, 0.5f, 0.4f, 0.01f) },
    };

    static readonly string[] kHair = { "hair", "bang", "fringe", "ponytail", "pony", "twintail", "braid", "tress", "ahoge", "kami", "maegami", "sidehair", "backhair" };
    static readonly string[] kBreast = { "breast", "bust", "boob", "oppai", "mune" };
    static readonly string[] kCloth = { "skirt", "dress", "cloth", "cape", "coat", "cloak", "robe", "sleeve", "frill", "apron", "scarf", "hood", "ribbon", "sash", "hem", "tie", "necktie" };
    static readonly string[] kTail = { "tail", "ear", "ears", "kemomimi", "mimi" };
    static readonly string[] kAcc = { "earring", "pendant", "necklace", "chain", "charm", "tassel", "strap", "acc", "accessory", "phys", "physics", "spring", "dyn", "dynamic", "jiggle", "swing", "sway" };
    static readonly string[] kNot = { "finger", "thumb", "index", "middle", "ring", "pinky", "little", "eye", "jaw", "tongue", "teeth", "toe", "twist", "ik", "pole", "target", "ctrl", "control", "helper" };

    static List<string> Words(string name)
    {
        int cut = Mathf.Max(name.LastIndexOf(':'), name.LastIndexOf('|'));
        if (cut >= 0) name = name.Substring(cut + 1);
        var b = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            char ch = name[i];
            if (!char.IsLetterOrDigit(ch)) { b.Append(' '); continue; }
            if (i > 0)
            {
                char p = name[i - 1];
                if ((char.IsLower(p) && char.IsUpper(ch)) || (char.IsLetter(p) && char.IsDigit(ch)) || (char.IsDigit(p) && char.IsLetter(ch))) b.Append(' ');
            }
            b.Append(char.ToLowerInvariant(ch));
        }
        var words = new List<string>();
        foreach (var w in b.ToString().Split(' '))
            if (w.Length > 0 && !char.IsDigit(w[0])) words.Add(w);
        return words;
    }

    static bool Has(List<string> words, string[] keys, bool prefix)
    {
        foreach (var w in words)
            foreach (var k in keys)
                if (w == k || (prefix && k.Length >= 4 && w.StartsWith(k))) return true;
        return false;
    }

    static Kind KindOf(string name)
    {
        var w = Words(name);
        if (w.Count == 0 || Has(w, kNot, true)) return Kind.None;
        if (Has(w, kHair, true)) return Kind.Hair;
        if (Has(w, kBreast, true)) return Kind.Breast;
        if (Has(w, kCloth, true)) return Kind.Cloth;
        if (Has(w, kAcc, true)) return Kind.Accessory;
        if (Has(w, kTail, false)) return Kind.Tail;
        return Kind.None;
    }

    static readonly HashSet<string> kBody = new HashSet<string>
    {
        "Hips", "Spine", "Spine1", "Spine2", "Spine3", "Neck", "Head",
        "LeftShoulder", "LeftArm", "LeftForeArm", "LeftHand", "RightShoulder", "RightArm", "RightForeArm", "RightHand",
        "LeftUpLeg", "LeftLeg", "LeftFoot", "LeftToeBase", "RightUpLeg", "RightLeg", "RightFoot", "RightToeBase",
    };

    void ByName(Transform model, GlbScene.Made made)
    {
        Source = "picked by name";
        // the bones the skins hang on, by their names in the file (the body's are renamed by then)
        var bones = new HashSet<Transform>();
        foreach (var smr in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            foreach (var b in smr.bones)
                if (b != null) bones.Add(b);
        var original = new Dictionary<Transform, string>();
        if (made.Nodes != null && made.NodeNames != null)
            for (int i = 0; i < made.Nodes.Length && i < made.NodeNames.Length; i++)
                if (made.Nodes[i] != null) original[made.Nodes[i].transform] = made.NodeNames[i];
        System.Func<Transform, Kind> kindOf = t =>
            !bones.Contains(t) || kBody.Contains(t.name) ? Kind.None : KindOf(original.TryGetValue(t, out var n) ? n : t.name);

        var roots = new List<(Transform t, Kind k)>();
        foreach (var t in model.GetComponentsInChildren<Transform>(true))
        {
            Kind k = kindOf(t);
            if (k == Kind.None) continue;
            // a chain's first bone: its parent isn't one of them
            if (t.parent != null && kindOf(t.parent) != Kind.None) continue;
            roots.Add((t, k));
        }
        if (roots.Count == 0) return;

        var body = BodyColliders(model);
        var taken = new HashSet<Transform>();
        foreach (var (root, kind) in roots)
        {
            var p = kPreset[kind];
            int[] cols = body.For(kind);
            Tree(root, t =>
            {
                if (t != root && (t.GetComponent<Renderer>() != null || (kindOf(t) == Kind.None && t.childCount == 0 && !bones.Contains(t)))) return; // a mesh, not a bone
                float s = Mathf.Max(Mathf.Abs(t.lossyScale.x), 1e-6f);
                Add(t, t.childCount > 0 ? t.InverseTransformPoint(t.GetChild(0).position) : Stub(t),
                    p.s, p.d, p.g, Vector3.down, p.r / s, null, cols, taken);
            });
        }
    }

    class BodySet
    {
        public List<int> Head = new List<int>(), Trunk = new List<int>(), Arms = new List<int>(), Legs = new List<int>();
        public int[] For(Kind k)
        {
            var l = new List<int>();
            switch (k)
            {
                case Kind.Hair: l.AddRange(Head); l.AddRange(Trunk); l.AddRange(Arms); break;
                case Kind.Breast: l.AddRange(Arms); break; // the chest it sits on would only push it out
                case Kind.Cloth: case Kind.Tail: l.AddRange(Trunk); l.AddRange(Legs); l.AddRange(Arms); break;
                default: l.AddRange(Head); l.AddRange(Trunk); l.AddRange(Arms); l.AddRange(Legs); break;
            }
            return l.ToArray();
        }
    }

    // capsules on the body's bones, slimmer than a body (a man 1.78 m tall: the files are scaled to that)
    BodySet BodyColliders(Transform model)
    {
        var set = new BodySet();
        var byName = new Dictionary<string, Transform>();
        foreach (var t in model.GetComponentsInChildren<Transform>(true))
            if (kBody.Contains(t.name) && !byName.ContainsKey(t.name)) byName[t.name] = t;
        Transform B(string n) => byName.TryGetValue(n, out var t) ? t : null;
        void Cap(List<int> into, string from, string to, float r)
        {
            var a = B(from);
            var b = B(to);
            if (a == null) return;
            float s = Mathf.Max(Mathf.Abs(a.lossyScale.x), 1e-6f);
            into.Add(Cols.Count);
            Cols.Add(new Col
            {
                Bone = a, Offset = Vector3.zero, Tail = b != null ? a.InverseTransformPoint(b.position) : Vector3.zero,
                Radius = r / s, Capsule = b != null,
            });
        }
        var head = B("Head");
        if (head != null)
        {
            float s = Mathf.Max(Mathf.Abs(head.lossyScale.x), 1e-6f);
            set.Head.Add(Cols.Count);
            Cols.Add(new Col { Bone = head, Offset = head.InverseTransformPoint(head.position + Vector3.up * 0.08f), Radius = 0.085f / s });
        }
        Cap(set.Trunk, "Hips", "Spine", 0.10f);
        Cap(set.Trunk, "Spine", B("Spine2") != null ? "Spine2" : "Neck", 0.10f);
        Cap(set.Trunk, B("Spine2") != null ? "Spine2" : "Spine1", "Neck", 0.10f);
        foreach (string side in new[] { "Left", "Right" })
        {
            Cap(set.Arms, side + "Arm", side + "ForeArm", 0.045f);
            Cap(set.Arms, side + "ForeArm", side + "Hand", 0.04f);
            Cap(set.Legs, side + "UpLeg", side + "Leg", 0.075f);
            Cap(set.Legs, side + "Leg", side + "Foot", 0.05f);
        }
        return set;
    }
}
