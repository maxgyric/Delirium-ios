// Skeletons other than Mixamo's: Unity / VRM humanoids (J_Bip_L_UpperLeg), Unreal (thigh_l, spine_01), Source
// (ValveBiped.Bip01_L_Thigh), 3ds Max Biped (Bip01 L Thigh), GTA V (SKEL_L_Thigh), Blender Rigify (DEF-thigh.L)...
// Each bone is told by the words in its name (side + what it is), the spine by the bones between the hips and the
// neck, and renamed to Mixamo's own names: the rest of the game, and Delirium, know those.
using System.Collections.Generic;
using System.Text;
using UnityEngine;

public partial class Mannequin
{
    // words in a bone's name that say nothing of which bone it is
    static readonly HashSet<string> kNoise = new HashSet<string>
    {
        "mixamorig", "valve", "biped", "bip", "skel", "def", "org", "j", "c", "jnt", "joint", "bone", "bn", "rig", "armature", "cc", "base", "sk",
        "x", // Auto-Rig Pro's middle bones: spine_01.x, neck.x, head.x
    };
    // what a sided bone is called -> Mixamo's name without its side
    static readonly Dictionary<string, string> kSided = new Dictionary<string, string>
    {
        { "upleg", "UpLeg" }, { "upperleg", "UpLeg" }, { "thigh", "UpLeg" }, { "hip", "UpLeg" },
        { "leg", "Leg" }, { "lowerleg", "Leg" }, { "calf", "Leg" }, { "shin", "Leg" }, { "knee", "Leg" },
        { "foot", "Foot" }, { "ankle", "Foot" },
        { "toebase", "ToeBase" }, { "toe", "ToeBase" }, { "toes", "ToeBase" }, { "ball", "ToeBase" },
        { "shoulder", "Shoulder" }, { "clavicle", "Shoulder" }, { "collar", "Shoulder" }, { "collarbone", "Shoulder" },
        { "arm", "Arm" }, { "upperarm", "Arm" },
        { "forearm", "ForeArm" }, { "lowerarm", "ForeArm" }, { "elbow", "ForeArm" },
        { "hand", "Hand" }, { "wrist", "Hand" },
    };
    static readonly Dictionary<string, string> kCentre = new Dictionary<string, string>
    {
        { "hips", "Hips" }, { "pelvis", "Hips" }, { "hip", "Hips" }, { "neck", "Neck" }, { "head", "Head" },
    };

    // "J_Bip_L_UpperLeg" -> (left, "upperleg"); "ValveBiped.Bip01_R_Forearm" -> (right, "forearm"); "LeftUpLeg" -> (left, "upleg")
    static void Words(string name, out int side, out string what)
    {
        int cut = Mathf.Max(name.LastIndexOf(':'), name.LastIndexOf('|'));
        if (cut >= 0) name = name.Substring(cut + 1);
        // words: at every non-letter-or-digit, between a small letter and a capital, between letters and digits
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
        side = 0;
        var rest = new StringBuilder();
        foreach (string w in b.ToString().Split(' '))
        {
            if (w.Length == 0 || char.IsDigit(w[0]) || kNoise.Contains(w)) continue;
            if (w == "l" || w == "left") { side = 1; continue; }
            if (w == "r" || w == "right") { side = 2; continue; }
            rest.Append(w);
        }
        what = rest.ToString();
    }

    // a sided bone's kind; Auto-Rig Pro's halves (thigh_twist.l, arm_stretch.l) as the bone they're part of
    static bool SidedKind(string what, out string kind)
    {
        if (kSided.TryGetValue(what, out kind)) return true;
        foreach (string tail in new[] { "twist", "stretch" })
            if (what.Length > tail.Length && what.EndsWith(tail) && kSided.TryGetValue(what.Substring(0, what.Length - tail.Length), out kind)) return true;
        kind = null;
        return false;
    }

    static int Depth(Transform t, Transform top)
    {
        int d = 0;
        for (; t != null && t != top; t = t.parent) d++;
        return d;
    }

    static bool Above(Transform a, Transform b) // a is b's ancestor (or b)
    {
        for (; b != null; b = b.parent)
            if (b == a) return true;
        return false;
    }

    /// Finds the model's skeleton by its bones' names and gives them Mixamo's. Null, or what's missing.
    static string Canonicalize(Transform model)
    {
        // the bones the skins hang on (a rig's control and helper bones aren't among them); none: every node
        var candidates = new List<Transform>();
        var seen = new HashSet<Transform>();
        foreach (var smr in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            foreach (var bone in smr.bones)
                if (bone != null && seen.Add(bone)) candidates.Add(bone);
        if (candidates.Count == 0) candidates.AddRange(model.GetComponentsInChildren<Transform>(true));
        // each of Mixamo's bones: the highest one in the tree with its words (a twist bone under it has them too)
        var pick = new Dictionary<string, Transform>();
        foreach (var t in candidates)
        {
            Words(t.name, out int side, out string what);
            string key = null;
            if (side != 0 && SidedKind(what, out string sided)) key = (side == 1 ? "Left" : "Right") + sided;
            else if (side == 0 && kCentre.TryGetValue(what, out string centre)) key = centre;
            if (key == null) continue;
            if (!pick.TryGetValue(key, out var had) || Depth(t, model) < Depth(had, model)) pick[key] = t;
        }
        // "Leg" for the thigh and "Knee" (or "Calf", "Shin") for the shin below it: both read as the shin; the
        // higher one is the thigh then
        foreach (string side in new[] { "Left", "Right" })
        {
            if (pick.ContainsKey(side + "UpLeg") || !pick.TryGetValue(side + "Leg", out var upper)) continue;
            Transform lower = null;
            foreach (var t in candidates)
            {
                if (t == upper || !Above(upper, t)) continue;
                Words(t.name, out int s2, out string w2);
                if ((s2 == (side == "Left" ? 1 : 2)) && SidedKind(w2, out string k2) && k2 == "Leg" && (lower == null || Depth(t, model) < Depth(lower, model))) lower = t;
            }
            if (lower == null) continue;
            pick[side + "UpLeg"] = upper;
            pick[side + "Leg"] = lower;
        }
        // the hips: else the thighs' shared parent
        if (!pick.ContainsKey("Hips") && pick.TryGetValue("LeftUpLeg", out var lt) && pick.TryGetValue("RightUpLeg", out var rt) && lt.parent == rt.parent && lt.parent != null)
            pick["Hips"] = lt.parent;
        // no neck nor head by name (Rigify's DEF-spine.004-.006): the spine bones above the one the shoulders
        // hang on, the first the neck, the last the head
        if (!pick.ContainsKey("Neck") && !pick.ContainsKey("Head"))
        {
            Transform arm = pick.TryGetValue("LeftShoulder", out var ls) ? ls : pick.TryGetValue("LeftArm", out var la) ? la
                          : pick.TryGetValue("RightShoulder", out var rs) ? rs : pick.TryGetValue("RightArm", out var ra) ? ra : null;
            var up = new List<Transform>();
            for (var at = arm != null ? arm.parent : null; at != null && up.Count < 8; )
            {
                Transform next = null;
                for (int i = 0; i < at.childCount && next == null; i++)
                {
                    var ch = at.GetChild(i);
                    Words(ch.name, out int s3, out string w3);
                    if (s3 == 0 && (w3 == "spine" || w3 == "neck" || w3 == "head") && candidates.Contains(ch)) next = ch;
                }
                if (next == null) break;
                up.Add(next);
                at = next;
            }
            if (up.Count >= 2) pick["Neck"] = up[0];
            if (up.Count >= 1) pick["Head"] = up[up.Count - 1];
        }
        // the spine: what's between the hips and the neck (or the head), from the hips up
        Transform top = pick.TryGetValue("Neck", out var neck) ? neck : pick.TryGetValue("Head", out var head) ? head : null;
        if (top != null && pick.TryGetValue("Hips", out var hips))
        {
            var chain = new List<Transform>();
            for (var t = top.parent; t != null && t != model && !Above(t, hips); t = t.parent) chain.Insert(0, t);
            int n = chain.Count;
            if (n >= 1) pick["Spine"] = chain[0];
            if (n >= 2) pick["Spine1"] = chain[1];
            if (n == 3) pick["Spine2"] = chain[2];
            if (n >= 4)
            {
                pick["Spine2"] = chain[n - 2];
                pick["Spine3"] = chain[n - 1];
            }
        }
        var missing = new List<string>();
        foreach (string need in new[] { "Hips", "Spine", "Head", "LeftUpLeg", "LeftLeg", "LeftFoot", "RightUpLeg", "RightLeg", "RightFoot",
                                        "LeftArm", "LeftForeArm", "LeftHand", "RightArm", "RightForeArm", "RightHand" })
            if (!pick.ContainsKey(need)) missing.Add(need);
        if (missing.Count > 0)
        {
            var names = new List<string>();
            for (int i = 0; i < candidates.Count && i < 40; i++) names.Add(candidates[i].name);
            return "no " + string.Join(", ", missing.ToArray()) + " among its bones (" + string.Join(", ", names.ToArray()) + (candidates.Count > 40 ? "..." : "") + ")";
        }
        // Mixamo's names on them; any other bone Delirium would take for a body part gets a name it won't
        var chosen = new HashSet<Transform>(pick.Values);
        var report = new StringBuilder("[Sandbox] the skeleton:");
        foreach (var kv in pick)
        {
            report.Append(" " + kv.Key + "=" + kv.Value.name);
            kv.Value.name = kv.Key;
        }
        int aux = 0;
        foreach (var t in model.GetComponentsInChildren<Transform>(true))
            if (t != model && !chosen.Contains(t) && Delirium.Native.nm_rig_part_for_name(t.name) >= 0)
                t.name = "aux" + (aux++);
        Debug.Log(report.ToString());
        return null;
    }
}
