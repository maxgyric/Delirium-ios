// A mannequin made in code: a skeleton with Mixamo's bone names (the ones Delirium knows) and a skinned mesh of
// capsules, one per bone. No model file, nothing from any game. Faces +Z, 1.75 m, bound in a T pose.
using System.Collections.Generic;
using UnityEngine;

public partial class Mannequin : MonoBehaviour
{
    public Transform Hips;
    public Transform[] Bones;          // Bones[0] is the hips; parents before children
    public Quaternion[] StandLocal;    // the standing pose (arms down), each bone's local rotation
    public float HipHeight;            // the hips above the root when standing

    /// How far below level the arms hang in the bind pose (degrees). Delirium takes the bind pose as the body's
    /// own: bound in a T, a balancing man holds his arms half out. Set to the game man's own before the first Create.
    public static float ArmDrop = 0f;

    // A point of the T pose where it is with the arms dropped: the arm's bones swing about the shoulder's joint.
    static Vector3 Dropped(string bone, Vector3 p)
    {
        bool left = bone == "LeftArm" || bone == "LeftForeArm" || bone == "LeftHand";
        bool right = bone == "RightArm" || bone == "RightForeArm" || bone == "RightHand";
        if (!left && !right) return p;
        var pivot = new Vector3(left ? -0.175f : 0.175f, 1.42f, 0f);
        return pivot + Quaternion.Euler(0f, 0f, left ? ArmDrop : -ArmDrop) * (p - pivot);
    }

    static Vector3 JointAt(int i) => Dropped(kJoints[i].name, kJoints[i].at);

    static readonly (string name, string parent, Vector3 at)[] kJoints =
    {
        ("Hips", null, new Vector3(0f, 0.98f, 0f)),
        ("LeftUpLeg", "Hips", new Vector3(-0.095f, 0.93f, 0f)),
        ("LeftLeg", "LeftUpLeg", new Vector3(-0.095f, 0.50f, 0.005f)),
        ("LeftFoot", "LeftLeg", new Vector3(-0.095f, 0.09f, -0.02f)),
        ("LeftToeBase", "LeftFoot", new Vector3(-0.095f, 0.03f, 0.12f)),
        ("RightUpLeg", "Hips", new Vector3(0.095f, 0.93f, 0f)),
        ("RightLeg", "RightUpLeg", new Vector3(0.095f, 0.50f, 0.005f)),
        ("RightFoot", "RightLeg", new Vector3(0.095f, 0.09f, -0.02f)),
        ("RightToeBase", "RightFoot", new Vector3(0.095f, 0.03f, 0.12f)),
        ("Spine", "Hips", new Vector3(0f, 1.06f, 0f)),
        ("Spine1", "Spine", new Vector3(0f, 1.15f, 0f)),
        ("Spine2", "Spine1", new Vector3(0f, 1.24f, 0f)),
        ("Spine3", "Spine2", new Vector3(0f, 1.33f, 0f)),
        ("Neck", "Spine3", new Vector3(0f, 1.47f, 0f)),
        ("Head", "Neck", new Vector3(0f, 1.56f, 0.01f)),
        ("LeftShoulder", "Spine3", new Vector3(-0.045f, 1.42f, 0f)),
        ("LeftArm", "LeftShoulder", new Vector3(-0.175f, 1.42f, 0f)),
        ("LeftForeArm", "LeftArm", new Vector3(-0.455f, 1.42f, 0f)),
        ("LeftHand", "LeftForeArm", new Vector3(-0.715f, 1.42f, 0f)),
        ("RightShoulder", "Spine3", new Vector3(0.045f, 1.42f, 0f)),
        ("RightArm", "RightShoulder", new Vector3(0.175f, 1.42f, 0f)),
        ("RightForeArm", "RightArm", new Vector3(0.455f, 1.42f, 0f)),
        ("RightHand", "RightForeArm", new Vector3(0.715f, 1.42f, 0f)),
    };

    // the capsules: (bone, from, to, radius), in the T pose
    static readonly (string bone, Vector3 a, Vector3 b, float r)[] kShapes =
    {
        ("Hips", new Vector3(-0.07f, 0.97f, 0f), new Vector3(0.07f, 0.97f, 0f), 0.105f),
        ("LeftUpLeg", new Vector3(-0.095f, 0.90f, 0f), new Vector3(-0.095f, 0.53f, 0.005f), 0.075f),
        ("LeftLeg", new Vector3(-0.095f, 0.48f, 0.005f), new Vector3(-0.095f, 0.12f, -0.02f), 0.055f),
        ("LeftFoot", new Vector3(-0.095f, 0.045f, -0.03f), new Vector3(-0.095f, 0.04f, 0.14f), 0.042f),
        ("RightUpLeg", new Vector3(0.095f, 0.90f, 0f), new Vector3(0.095f, 0.53f, 0.005f), 0.075f),
        ("RightLeg", new Vector3(0.095f, 0.48f, 0.005f), new Vector3(0.095f, 0.12f, -0.02f), 0.055f),
        ("RightFoot", new Vector3(0.095f, 0.045f, -0.03f), new Vector3(0.095f, 0.04f, 0.14f), 0.042f),
        ("Spine", new Vector3(-0.035f, 1.09f, 0f), new Vector3(0.035f, 1.09f, 0f), 0.105f),
        ("Spine1", new Vector3(-0.04f, 1.19f, 0f), new Vector3(0.04f, 1.19f, 0f), 0.108f),
        ("Spine2", new Vector3(-0.05f, 1.28f, 0f), new Vector3(0.05f, 1.28f, 0f), 0.112f),
        ("Spine3", new Vector3(-0.065f, 1.37f, 0f), new Vector3(0.065f, 1.37f, 0f), 0.112f),
        ("Neck", new Vector3(0f, 1.46f, 0f), new Vector3(0f, 1.54f, 0.005f), 0.05f),
        ("Head", new Vector3(0f, 1.63f, 0.015f), new Vector3(0f, 1.665f, 0.015f), 0.098f),
        ("LeftArm", new Vector3(-0.19f, 1.42f, 0f), new Vector3(-0.43f, 1.42f, 0f), 0.048f),
        ("LeftForeArm", new Vector3(-0.47f, 1.42f, 0f), new Vector3(-0.69f, 1.42f, 0f), 0.04f),
        ("LeftHand", new Vector3(-0.735f, 1.42f, 0f), new Vector3(-0.80f, 1.42f, 0f), 0.036f),
        ("RightArm", new Vector3(0.19f, 1.42f, 0f), new Vector3(0.43f, 1.42f, 0f), 0.048f),
        ("RightForeArm", new Vector3(0.47f, 1.42f, 0f), new Vector3(0.69f, 1.42f, 0f), 0.04f),
        ("RightHand", new Vector3(0.735f, 1.42f, 0f), new Vector3(0.80f, 1.42f, 0f), 0.036f),
    };

    public static Mannequin Create(string name, Vector3 at, float yaw, Material material)
    {
        // built at the origin, unturned (bone space = world space), then moved to its place
        var root = new GameObject(name);
        var m = root.AddComponent<Mannequin>();
        var byName = new Dictionary<string, Transform>();
        var bones = new List<Transform>();
        for (int ji = 0; ji < kJoints.Length; ji++)
        {
            string jName = kJoints[ji].name, jParent = kJoints[ji].parent;
            Vector3 jAt = JointAt(ji);
            var t = new GameObject(jName).transform;
            t.SetParent(jParent == null ? root.transform : byName[jParent], false);
            t.position = jAt;
            t.rotation = Quaternion.identity;
            byName[jName] = t;
            bones.Add(t);
        }
        m.Hips = bones[0];
        m.Bones = bones.ToArray();
        m.HipHeight = kJoints[0].at.y;

        var verts = new List<Vector3>();
        var weights = new List<BoneWeight>();
        var tris = new List<int>();
        foreach (var c in kShapes) Capsule(verts, weights, tris, Dropped(c.bone, c.a), Dropped(c.bone, c.b), c.r, bones.IndexOf(byName[c.bone]));
        var mesh = new Mesh { name = "Mannequin" };
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        mesh.boneWeights = weights.ToArray();
        var skin = new GameObject("Body");
        skin.transform.SetParent(root.transform, false);
        var binds = new Matrix4x4[m.Bones.Length];
        for (int i = 0; i < binds.Length; i++) binds[i] = m.Bones[i].worldToLocalMatrix * skin.transform.localToWorldMatrix;
        mesh.bindposes = binds;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        var smr = skin.AddComponent<SkinnedMeshRenderer>();
        smr.sharedMesh = mesh;
        smr.bones = m.Bones;
        smr.rootBone = m.Hips;
        smr.sharedMaterial = material;
        smr.localBounds = new Bounds(Vector3.zero, Vector3.one * 3f); // around the hips, wherever he's thrown
        smr.updateWhenOffscreen = true;
        // Delirium takes "the object with its Animator" as the character; no controller: it plays nothing
        root.AddComponent<Animator>();

        m.StandLocal = new Quaternion[m.Bones.Length];
        m.Ref = new Quaternion[m.Bones.Length];
        m.restScale = new Vector3[m.Bones.Length];
        for (int i = 0; i < m.Bones.Length; i++)
        {
            m.StandLocal[i] = StandLocalOf(kJoints[i].name);
            m.Ref[i] = Quaternion.identity; // his bones' axes are the body's own
            m.restScale[i] = Vector3.one;
        }
        m.Stand(at, yaw);
        return m;
    }

    /// Stands him at `at` (his feet), turned `yaw` degrees, in the standing pose.
    public void Stand(Vector3 at, float yaw)
    {
        transform.SetPositionAndRotation(at, Quaternion.Euler(0f, yaw, 0f));
        Hips.position = at + Vector3.up * HipHeight;
        for (int i = 0; i < Bones.Length; i++)
        {
            Bones[i].localRotation = StandLocal[i];
            Bones[i].localScale = restScale[i]; // a cut-off limb is back
        }
    }

    // The bones' names as GTA IV's (and 3ds Max Biped's): what delirium_anim knows them by.
    static string GtaName(string bone)
    {
        switch (bone)
        {
            case "Hips": return "Pelvis";
            case "LeftUpLeg": return "L Thigh";
            case "LeftLeg": return "L Calf";
            case "LeftFoot": return "L Foot";
            case "LeftToeBase": return "L Toe0";
            case "RightUpLeg": return "R Thigh";
            case "RightLeg": return "R Calf";
            case "RightFoot": return "R Foot";
            case "RightToeBase": return "R Toe0";
            case "LeftShoulder": return "L Clavicle";
            case "LeftArm": return "L UpperArm";
            case "LeftForeArm": return "L Forearm";
            case "LeftHand": return "L Hand";
            case "RightShoulder": return "R Clavicle";
            case "RightArm": return "R UpperArm";
            case "RightForeArm": return "R Forearm";
            case "RightHand": return "R Hand";
            default: return bone; // Spine, Spine1-3, Neck, Head
        }
    }

    /// The skeleton for delirium_anim (GtaAnim): names, parents and the T pose, each bone in its parent's frame
    /// (7 floats: position, rotation), in the library's right-handed Z-up space (Unity's y and z swapped).
    public static void Skeleton(out string[] names, out int[] parents, out float[] local)
    {
        int n = kJoints.Length;
        names = new string[n];
        parents = new int[n];
        local = new float[n * 7];
        for (int i = 0; i < n; i++)
        {
            names[i] = GtaName(kJoints[i].name);
            parents[i] = -1;
            for (int j = 0; j < i; j++)
                if (kJoints[j].name == kJoints[i].parent) parents[i] = j;
            Vector3 p = JointAt(i) - (parents[i] >= 0 ? JointAt(parents[i]) : Vector3.zero);
            local[i * 7] = p.x;
            local[i * 7 + 1] = p.z;
            local[i * 7 + 2] = p.y;
            local[i * 7 + 6] = 1f; // every bone's axes are the model's
        }
    }

    public static int BoneCount => kJoints.Length;

    /// A pose from GtaAnim (the model's space, facing +X) onto the bones: the model's origin at `origin` (his
    /// feet's ground), facing `yaw` degrees; `lift` raises the whole body.
    public void Apply(GtaAnim.Frame[] pose, Vector3 origin, float yaw, float lift = 0f)
    {
        var turn = Quaternion.Euler(0f, yaw - 90f, 0f); // the library's +X is the way he faces
        Hips.position = origin + turn * (pose[0].P * Scale) + Vector3.up * lift;
        for (int i = 0; i < Bones.Length && i < pose.Length; i++) Bones[i].rotation = turn * pose[i].R * Ref[i]; // parents first
    }

    /// Bone i's rotation in the world for a GtaAnim pose, him facing `yaw` (what Apply gives it).
    public Quaternion World(GtaAnim.Frame[] pose, int i, float yaw) => Quaternion.Euler(0f, yaw - 90f, 0f) * pose[i].R * Ref[i];

    // The standing pose (arms down, a little bend at the elbows) as the mannequin's bones' local rotations.
    static Quaternion StandLocalOf(string bone)
    {
        switch (bone)
        {
            case "LeftArm": return Quaternion.Euler(0f, 0f, 76f - ArmDrop);
            case "RightArm": return Quaternion.Euler(0f, 0f, -(76f - ArmDrop));
            case "LeftForeArm": return Quaternion.Euler(0f, 12f, 0f);
            case "RightForeArm": return Quaternion.Euler(0f, -12f, 0f);
            default: return Quaternion.identity;
        }
    }

    /// A bone's place in Bones by its name here ("LeftArm"...), whatever a loaded model calls it.
    public int Index(string name)
    {
        for (int i = 0; i < kJoints.Length && i < Bones.Length; i++)
            if (kJoints[i].name == name) return i;
        return -1;
    }

    public Transform Bone(string name)
    {
        int i = Index(name);
        return i >= 0 ? Bones[i] : null;
    }

    /// A cut-off limb isn't drawn: its bone (and all under it) shrunk to nothing.
    public void Hide(string bone)
    {
        var b = Bone(bone);
        if (b != null) b.localScale = Vector3.zero;
    }

    // A capsule from a to b, all of it on one bone. Triangles wound so the outside is the front (Unity: clockwise).
    static void Capsule(List<Vector3> verts, List<BoneWeight> weights, List<int> tris, Vector3 a, Vector3 b, float r, int bone)
    {
        const int kAround = 12, kUp = 4;
        Vector3 w = (b - a).normalized;
        Vector3 u = Vector3.Cross(Mathf.Abs(w.y) < 0.9f ? Vector3.up : Vector3.right, w).normalized;
        Vector3 v = Vector3.Cross(w, u);
        int first = verts.Count;
        var bw = new BoneWeight { boneIndex0 = bone, weight0 = 1f };
        // the rings, from a's end to b's: a's half sphere (without its pole), then b's
        int rings = 0;
        for (int end = 0; end < 2; end++)
            for (int i = 0; i < kUp; i++)
            {
                float lat = end == 0 ? -Mathf.PI * 0.5f * (1f - (i + 1f) / kUp) : Mathf.PI * 0.5f * i / kUp;
                Vector3 c = (end == 0 ? a : b) + w * (r * Mathf.Sin(lat));
                float rr = r * Mathf.Cos(lat);
                for (int j = 0; j < kAround; j++)
                {
                    float th = 2f * Mathf.PI * j / kAround;
                    verts.Add(c + (u * Mathf.Cos(th) + v * Mathf.Sin(th)) * rr);
                    weights.Add(bw);
                }
                rings++;
            }
        int poleA = verts.Count;
        verts.Add(a - w * r);
        weights.Add(bw);
        int poleB = verts.Count;
        verts.Add(b + w * r);
        weights.Add(bw);
        System.Func<int, int, int> At = (ring, j) => first + ring * kAround + j % kAround;
        for (int k = 0; k + 1 < rings; k++)
            for (int j = 0; j < kAround; j++)
            {
                tris.Add(At(k, j)); tris.Add(At(k, j + 1)); tris.Add(At(k + 1, j));
                tris.Add(At(k, j + 1)); tris.Add(At(k + 1, j + 1)); tris.Add(At(k + 1, j));
            }
        for (int j = 0; j < kAround; j++)
        {
            tris.Add(poleA); tris.Add(At(0, j + 1)); tris.Add(At(0, j));
            tris.Add(At(rings - 1, j)); tris.Add(At(rings - 1, j + 1)); tris.Add(poleB);
        }
    }
}
