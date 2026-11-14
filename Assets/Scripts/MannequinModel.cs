// A loaded character (a Mixamo-rigged model from a .glb) in the mannequin's place. The rest of the game keeps
// talking to a Mannequin: the same 23 bones in the same order, found in the model by name.
//
// The game's clips are worked out for the mannequin's skeleton and carried over as rotations in the world, so the
// model is first put in the mannequin's own shape (arms at the same angle, legs along the same lines): that pose is
// its reference. Its meshes are re-bound to that pose too, because Delirium takes the bind pose as the body's own
// (bound in a T, a man at rest holds his arms half out).
using System.Collections.Generic;
using UnityEngine;

public partial class Mannequin
{
    public Quaternion[] Ref;     // each bone's rotation in the body's space in the reference pose (the mannequin: none)
    public float Scale = 1f;     // his hips' height over the mannequin's: a clip's travel in his size
    Vector3[] restScale;
    // a loaded model: every one of its transforms' place in the body's space in the reference pose (its bind pose)
    Dictionary<Transform, KeyValuePair<Vector3, Quaternion>> refPose;

    /// A bone's bind pose in the world as he stands now (Delirium fits its body to it); false: the mannequin, whose
    /// mesh says it well enough.
    public bool BindOf(Transform bone, out Matrix4x4 bind)
    {
        bind = Matrix4x4.identity;
        if (refPose == null || bone == null || !refPose.TryGetValue(bone, out var at)) return false;
        bind = transform.localToWorldMatrix * Matrix4x4.TRS(at.Key, at.Value, Vector3.one);
        return true;
    }

    /// For the log when Delirium won't take him: where his main bones are in the bind pose.
    public string BindReport()
    {
        if (refPose == null) return "the mannequin";
        var s = new System.Text.StringBuilder("hips' height " + HipHeight.ToString("0.###") + ", scale " + Scale.ToString("0.###") + "; ");
        foreach (string n in new[] { "Hips", "LeftUpLeg", "LeftLeg", "LeftFoot", "Spine", "Spine2", "Neck", "Head", "LeftShoulder", "LeftArm", "LeftForeArm", "LeftHand" })
        {
            var b = Bone(n);
            if (b != null && refPose.TryGetValue(b, out var at)) s.Append(n + " " + at.Key.ToString("0.###") + " ");
        }
        return s.ToString();
    }

    // "mixamorig:LeftArm", "mixamorig1_LeftArm", "Armature|LeftArm" -> "leftarm"
    static string Plain(string name)
    {
        int cut = Mathf.Max(name.LastIndexOf(':'), name.LastIndexOf('|'));
        if (cut >= 0) name = name.Substring(cut + 1);
        string low = name.ToLowerInvariant();
        if (low.StartsWith("mixamorig"))
        {
            low = low.Substring(9);
            while (low.Length > 0 && (char.IsDigit(low[0]) || low[0] == '_')) low = low.Substring(1);
        }
        return low;
    }

    // The model's bone called `bone` here: among the skinned meshes' bones first (a mesh may be named "Head" too).
    static Transform FindBone(Transform model, string bone)
    {
        string want = bone.ToLowerInvariant();
        foreach (var smr in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            foreach (var b in smr.bones)
                if (b != null && Plain(b.name) == want) return b;
        foreach (var t in model.GetComponentsInChildren<Transform>(true))
            if (Plain(t.name) == want && t.GetComponent<Renderer>() == null) return t;
        return null;
    }

    static Vector3 JointOf(string bone)
    {
        for (int i = 0; i < kJoints.Length; i++)
            if (kJoints[i].name == bone) return JointAt(i);
        return Vector3.zero;
    }

    /// Makes a loaded model (the only child of `wrap`, at the origin, unturned) ready to be a man: facing +Z, a
    /// man's size, feet on the ground, in the reference pose, its meshes bound to that pose. Null, or what's wrong.
    public static string Prepare(GameObject wrap)
    {
        var model = wrap.transform.GetChild(0);
        if (model.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length == 0) return "no skinned mesh in the file: it has to be a rigged character";
        // Mixamo's names on whatever skeleton it has (Rigs.cs)
        string wrong = Canonicalize(model);
        if (wrong != null) return wrong;
        System.Func<string, Transform> B = n => FindBone(model, n);
        // his left on Unity's left when he faces +Z
        if (B("LeftUpLeg").position.x > B("RightUpLeg").position.x) model.Rotate(0f, 180f, 0f, Space.World);
        // a man's size (a file in centimetres is a hundred times a man)
        System.Func<float> low = () =>
        {
            float y = Mathf.Min(B("LeftFoot").position.y, B("RightFoot").position.y);
            foreach (string toe in new[] { "LeftToeBase", "RightToeBase" })
                if (B(toe) != null) y = Mathf.Min(y, B(toe).position.y);
            return y;
        };
        float tall = (B("Head").position.y - low()) * 1.144f; // the mannequin's head joint to toe is 1.53 of his 1.75 m
        if (tall < 1e-4f) return "the skeleton has no height";
        if (tall < 1.3f || tall > 2.3f) model.localScale *= 1.78f / tall;
        // a toe the mannequin has and the model hasn't. (No dummy Spine3: an empty bone on Spine2's own place was a
        // joint on its parent's, and Delirium refused the whole skeleton. Spine2 stands in for it: FromTemplate.)
        foreach (string side in new[] { "Left", "Right" })
            if (B(side + "ToeBase") == null)
            {
                var foot = B(side + "Foot");
                var d = new GameObject(side + "ToeBase").transform;
                d.SetParent(foot, false);
                d.position = foot.position + new Vector3(0f, -0.06f, 0.13f); // ahead of the ankle and lower, as his
            }
        // his feet on the ground (the mannequin's toe joints are 3 cm up), his hips over the origin
        Vector3 hips = B("Hips").position;
        model.position += new Vector3(-hips.x, 0.03f - low(), -hips.z);

        // the reference pose: every limb along the mannequin's own lines
        foreach (string side in new[] { "Left", "Right" })
        {
            Line(B(side + "Arm"), B(side + "ForeArm"), JointOf(side + "ForeArm") - JointOf(side + "Arm"));
            Line(B(side + "ForeArm"), B(side + "Hand"), JointOf(side + "Hand") - JointOf(side + "ForeArm"));
            Line(B(side + "UpLeg"), B(side + "Leg"), JointOf(side + "Leg") - JointOf(side + "UpLeg"));
            Line(B(side + "Leg"), B(side + "Foot"), JointOf(side + "Foot") - JointOf(side + "Leg"));
        }
        foreach (var smr in model.GetComponentsInChildren<SkinnedMeshRenderer>(true)) Rebind(smr);
        return null;
    }

    // Turns `bone` so the line to its `child` lies along `along`.
    static void Line(Transform bone, Transform child, Vector3 along)
    {
        Vector3 now = child.position - bone.position;
        if (now.sqrMagnitude < 1e-10f || along.sqrMagnitude < 1e-10f) return;
        bone.rotation = Quaternion.FromToRotation(now, along) * bone.rotation;
    }

    // The mesh as it stands in the bones' pose now, bound to that pose: the vertices skinned by hand (every scale
    // in the tree counted), the bind poses the bones' places now. Skinned again it is where it was.
    static void Rebind(SkinnedMeshRenderer smr)
    {
        var src = smr.sharedMesh;
        if (src == null) return;
        var bones = smr.bones;
        var binds = src.bindposes;
        var weights = src.boneWeights;
        var verts = src.vertices;
        var normals = src.normals;
        if (binds.Length != bones.Length || weights.Length != verts.Length) return;
        foreach (var b in bones)
            if (b == null) return;
        Matrix4x4 toMesh = smr.transform.worldToLocalMatrix;
        var skin = new Matrix4x4[bones.Length];
        var now = new Matrix4x4[bones.Length];
        for (int i = 0; i < bones.Length; i++)
        {
            skin[i] = toMesh * bones[i].localToWorldMatrix * binds[i];
            now[i] = bones[i].worldToLocalMatrix * smr.transform.localToWorldMatrix;
        }
        bool hasNormals = normals.Length == verts.Length;
        for (int v = 0; v < verts.Length; v++)
        {
            var w = weights[v];
            Vector3 p = Vector3.zero, n = Vector3.zero;
            for (int k = 0; k < 4; k++)
            {
                int bone = k == 0 ? w.boneIndex0 : k == 1 ? w.boneIndex1 : k == 2 ? w.boneIndex2 : w.boneIndex3;
                float wt = k == 0 ? w.weight0 : k == 1 ? w.weight1 : k == 2 ? w.weight2 : w.weight3;
                if (wt <= 0f || bone < 0 || bone >= skin.Length) continue;
                p += skin[bone].MultiplyPoint3x4(verts[v]) * wt;
                if (hasNormals) n += skin[bone].MultiplyVector(normals[v]) * wt;
            }
            verts[v] = p;
            if (hasNormals) normals[v] = n.sqrMagnitude > 1e-12f ? n.normalized : Vector3.up;
        }
        var mesh = Instantiate(src);
        mesh.name = src.name + " (bound)";
        mesh.vertices = verts;
        if (hasNormals) mesh.normals = normals;
        // its blend shapes' offsets turned with the vertices they move
        if (src.blendShapeCount > 0)
        {
            mesh.ClearBlendShapes();
            var d = new Vector3[verts.Length];
            var dn = new Vector3[verts.Length];
            var dt = new Vector3[verts.Length];
            for (int s = 0; s < src.blendShapeCount; s++)
                for (int f = 0; f < src.GetBlendShapeFrameCount(s); f++)
                {
                    src.GetBlendShapeFrameVertices(s, f, d, dn, dt);
                    for (int v = 0; v < d.Length; v++)
                    {
                        if (d[v].sqrMagnitude == 0f) continue;
                        var w = weights[v];
                        Vector3 r = Vector3.zero;
                        for (int k = 0; k < 4; k++)
                        {
                            int bone = k == 0 ? w.boneIndex0 : k == 1 ? w.boneIndex1 : k == 2 ? w.boneIndex2 : w.boneIndex3;
                            float wt = k == 0 ? w.weight0 : k == 1 ? w.weight1 : k == 2 ? w.weight2 : w.weight3;
                            if (wt > 0f && bone >= 0 && bone < skin.Length) r += skin[bone].MultiplyVector(d[v]) * wt;
                        }
                        d[v] = r;
                    }
                    mesh.AddBlendShapeFrame(src.GetBlendShapeName(s), src.GetBlendShapeFrameWeight(s, f), d, null, null);
                }
        }
        mesh.bindposes = now;
        mesh.RecalculateBounds();
        smr.sharedMesh = mesh;
    }

    /// A man of a prepared model (a copy of `template`), standing at `at`.
    public static Mannequin FromTemplate(GameObject template, string name, Vector3 at, float yaw)
    {
        var root = new GameObject(name);
        var m = root.AddComponent<Mannequin>();
        var copy = Instantiate(template);
        copy.name = "Model";
        copy.transform.SetParent(root.transform, false);
        copy.transform.localPosition = Vector3.zero;
        copy.transform.localRotation = Quaternion.identity;
        copy.SetActive(true);
        m.refPose = new Dictionary<Transform, KeyValuePair<Vector3, Quaternion>>();
        foreach (var t in copy.GetComponentsInChildren<Transform>(true)) // the root is at the origin, unturned
            m.refPose[t] = new KeyValuePair<Vector3, Quaternion>(t.position, t.rotation);
        int n = kJoints.Length;
        m.Bones = new Transform[n];
        m.Ref = new Quaternion[n];
        m.StandLocal = new Quaternion[n];
        m.restScale = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            m.Bones[i] = FindBone(copy.transform, kJoints[i].name);
        }
        // the bones a skeleton may not have: a spine bone is the one below it; a neck or a collarbone is the next
        // bone (the head, the upper arm), which a clip turns right after it anyway
        for (int i = 0; i < n; i++)
        {
            string bone = kJoints[i].name;
            if (m.Bones[i] == null && (bone == "Spine1" || bone == "Spine2" || bone == "Spine3")) m.Bones[i] = m.Bones[i - 1];
        }
        for (int i = n - 2; i >= 0; i--)
        {
            string bone = kJoints[i].name;
            if (m.Bones[i] == null && (bone == "Neck" || bone == "LeftShoulder" || bone == "RightShoulder")) m.Bones[i] = m.Bones[i + 1];
        }
        for (int i = 0; i < n; i++)
        {
            if (m.Bones[i] == null)
            {
                Destroy(root);
                throw new System.InvalidOperationException("the model lost its bone " + kJoints[i].name);
            }
            m.Ref[i] = m.Bones[i].rotation; // the root is at the origin, unturned: the body's space is the world
            m.restScale[i] = m.Bones[i].localScale;
        }
        m.Hips = m.Bones[0];
        m.HipHeight = m.Hips.position.y;
        m.Scale = m.HipHeight / kJoints[0].at.y;
        // the standing pose: the mannequin's, each bone turned in the world as his is
        var stand = new Quaternion[n];
        for (int i = 0; i < n; i++)
        {
            int parent = -1;
            for (int j = 0; j < i; j++)
                if (kJoints[j].name == kJoints[i].parent) parent = j;
            stand[i] = parent >= 0 ? stand[parent] * StandLocalOf(kJoints[i].name) : StandLocalOf(kJoints[i].name);
            m.Bones[i].rotation = stand[i] * m.Ref[i]; // parents first
        }
        for (int i = 0; i < n; i++) m.StandLocal[i] = m.Bones[i].localRotation;
        // its skins' bounds: a box round the hips big enough for any pose a ragdoll throws him into, not worked out
        // again from every vertex every frame (updateWhenOffscreen): the same picture, a heavy model far cheaper
        foreach (var smr in copy.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            smr.updateWhenOffscreen = false;
            smr.rootBone = m.Hips;
            float s = Mathf.Max(m.Hips.lossyScale.x, 1e-4f);
            smr.localBounds = new Bounds(Vector3.zero, Vector3.one * (5f / s));
        }
        root.AddComponent<Animator>(); // Delirium's "character": the object with its Animator
        m.Stand(at, yaw);
        return m;
    }
}
