// A VRChat avatar's toggles at run time (an avatar imported by VrcImport: <name>.glb with <name>.vrc.json beside it).
// The sidecar says, for each menu item's value, which objects switch on or off and which blend shapes move, worked
// out from the avatar's animator controllers on import. Here a look is put together from it: the prefab's own
// starting state, what the controllers set with every parameter at its default, then each parameter you changed.
//
// It sits on the model's root in the character's template and so in every man copied from it (Unity copies its
// fields); the toggles' state is kept per file (and saved), and every copy is redone when it changes.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

public class VrcLook : MonoBehaviour
{
    public string Key = "";                 // the model's file
    public string[] Targets = new string[0]; // each target's place under this object: child indices "0/3/1"

    public sealed class Fx
    {
        public int T;
        public string Attr;
        public float V;
    }

    public sealed class Toggle
    {
        public string Menu, Name, Param;
        public float Value;
        public bool Button;
    }

    public sealed class Data
    {
        public string Avatar = "";
        public readonly List<Toggle> Toggles = new List<Toggle>();
        public readonly Dictionary<string, float> Defaults = new Dictionary<string, float>();
        public readonly Dictionary<string, float> State = new Dictionary<string, float>();   // parameters you changed
        public readonly List<Fx> Start = new List<Fx>(), Base = new List<Fx>();
        public readonly Dictionary<string, List<Fx>> Effects = new Dictionary<string, List<Fx>>();
        public readonly List<KeyValuePair<int, string>> Parts = new List<KeyValuePair<int, string>>(); // target, path
        public readonly Dictionary<int, bool> Hidden = new Dictionary<int, bool>();             // parts you switched by hand
        public readonly List<string> Notes = new List<string>();
    }

    static readonly Dictionary<string, Data> all = new Dictionary<string, Data>();

    public Data D => all.TryGetValue(Key, out var d) ? d : null;

    static string Sidecar(string glb) => Path.Combine(Path.GetDirectoryName(glb) ?? "", Path.GetFileNameWithoutExtension(glb) + ".vrc.json");
    public static bool HasToggles(string glb) => File.Exists(Sidecar(glb));
    static string Pref(string key) => "delirium.vrc." + Path.GetFileNameWithoutExtension(key);
    static string K(string param, float value) => param + "=" + value.ToString(CultureInfo.InvariantCulture);

    /// The toggles of the model just read from `path` (if it has a sidecar), on its root. Before Mannequin.Prepare:
    /// the targets are found by the file's own tree.
    public static VrcLook Attach(string path, GlbScene.Made made)
    {
        string side = Sidecar(path);
        if (!File.Exists(side)) return null;
        object j = Json.Parse(File.ReadAllText(side));
        var d = new Data { Avatar = Json.Str(j, "avatar", "") };
        Func<object, List<Fx>> fx = list =>
        {
            var r = new List<Fx>();
            foreach (object e in Json.Arr(list) ?? new List<object>())
            {
                var a = Json.Arr(e);
                if (a == null || a.Count < 3) continue;
                r.Add(new Fx { T = (int)(double)a[0], Attr = a[1] as string ?? "", V = (float)(double)a[2] });
            }
            return r;
        };
        d.Start.AddRange(fx(Json.Get(j, "start")));
        d.Base.AddRange(fx(Json.Get(j, "base")));
        if (Json.Get(j, "effects") is Dictionary<string, object> effects)
            foreach (var kv in effects) d.Effects[kv.Key] = fx(kv.Value);
        if (Json.Get(j, "defaults") is Dictionary<string, object> defs)
            foreach (var kv in defs) if (kv.Value is double v) d.Defaults[kv.Key] = (float)v;
        foreach (object t in Json.Arr(Json.Get(j, "toggles")) ?? new List<object>())
            d.Toggles.Add(new Toggle { Menu = Json.Str(t, "menu", ""), Name = Json.Str(t, "name", ""), Param = Json.Str(t, "param", ""),
                                       Value = Json.Num(t, "value", 1f), Button = Json.Flag(t, "button") });
        foreach (object p in Json.Arr(Json.Get(j, "parts")) ?? new List<object>())
        {
            var a = Json.Arr(p);
            if (a != null && a.Count >= 2) d.Parts.Add(new KeyValuePair<int, string>((int)(double)a[0], a[1] as string ?? ""));
        }
        foreach (object n in Json.Arr(Json.Get(j, "notes")) ?? new List<object>()) d.Notes.Add(n as string ?? "");
        // the targets: node indices in the .glb -> places under the root
        var nodes = Json.Arr(Json.Get(j, "targets")) ?? new List<object>();
        var look = made.Root.AddComponent<VrcLook>();
        look.Key = path;
        look.Targets = new string[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            int n = (int)(double)nodes[i];
            look.Targets[i] = n >= 0 && n < made.Nodes.Length && made.Nodes[n] != null ? PlaceOf(made.Nodes[n].transform, made.Root.transform) : null;
        }
        Load(d, path);
        all[path] = d;
        look.Apply();
        return look;
    }

    static string PlaceOf(Transform t, Transform root)
    {
        var parts = new List<string>();
        for (; t != null && t != root; t = t.parent) parts.Insert(0, t.GetSiblingIndex().ToString(CultureInfo.InvariantCulture));
        return t == root ? string.Join("/", parts.ToArray()) : null;
    }

    Transform[] found;

    Transform Target(int i)
    {
        if (found == null || found.Length != Targets.Length) found = new Transform[Targets.Length];
        if (i < 0 || i >= Targets.Length || Targets[i] == null) return null;
        if (found[i] != null) return found[i];
        Transform t = transform;
        if (Targets[i] != "")
            foreach (string s in Targets[i].Split('/'))
            {
                int k = int.Parse(s, CultureInfo.InvariantCulture);
                if (k >= t.childCount) return null;
                t = t.GetChild(k);
            }
        return found[i] = t;
    }

    void OnEnable() => Apply();

    /// The look as the toggles say now.
    public void Apply()
    {
        var d = D;
        if (d == null) return;
        var final = new Dictionary<string, Fx>();
        Action<List<Fx>> put = list => { foreach (var e in list) final[e.T + "|" + e.Attr] = e; };
        put(d.Start);
        put(d.Base);
        foreach (var kv in d.State)
        {
            float def = d.Defaults.TryGetValue(kv.Key, out float v) ? v : 0f;
            if (Mathf.Abs(kv.Value - def) < 1e-4f) continue;
            if (d.Effects.TryGetValue(K(kv.Key, kv.Value), out var list)) put(list);
        }
        foreach (var kv in d.Hidden) final[kv.Key + "|active"] = new Fx { T = kv.Key, Attr = "active", V = kv.Value ? 0f : 1f };
        foreach (var e in final.Values)
        {
            var t = Target(e.T);
            if (t == null) continue;
            if (e.Attr == "active")
            {
                if (t != transform) t.gameObject.SetActive(e.V > 0.5f);
            }
            else if (e.Attr == "enabled")
            {
                foreach (var r in Renderers<Renderer>(t)) r.enabled = e.V > 0.5f;
            }
            else if (e.Attr.StartsWith("blend:", StringComparison.Ordinal))
            {
                string shape = e.Attr.Substring(6);
                foreach (var smr in Renderers<SkinnedMeshRenderer>(t))
                {
                    var mesh = smr.sharedMesh;
                    int k = mesh != null ? mesh.GetBlendShapeIndex(shape) : -1;
                    if (k >= 0) smr.SetBlendShapeWeight(k, e.V);
                }
            }
        }
    }

    // the renderers that are the target's: its own, and its meshes split per vertex group ("<name>_0", "<name>_1")
    static IEnumerable<T> Renderers<T>(Transform t) where T : Component
    {
        foreach (var r in t.GetComponents<T>()) yield return r;
        for (int i = 0; i < t.childCount; i++)
        {
            var c = t.GetChild(i);
            if (c.name.StartsWith(t.name + "_", StringComparison.Ordinal))
                foreach (var r in c.GetComponents<T>()) yield return r;
        }
    }

    // ------------------------------------------------------------------ the toggles' state
    public static bool IsOn(Data d, Toggle t)
    {
        float v = d.State.TryGetValue(t.Param, out float s) ? s : d.Defaults.TryGetValue(t.Param, out float def) ? def : 0f;
        return Mathf.Abs(v - t.Value) < 1e-4f;
    }

    public static void Set(string key, Toggle t, bool on)
    {
        if (!all.TryGetValue(key, out var d)) return;
        d.State[t.Param] = on ? t.Value : 0f;
        Changed(key, d);
    }

    /// A part as it is now on this look (switched on and drawn).
    public bool Shown(int target)
    {
        var t = Target(target);
        if (t == null || !t.gameObject.activeSelf) return false;
        foreach (var r in Renderers<Renderer>(t)) if (r.enabled) return true;
        return false;
    }

    public static void SetPart(string key, int target, bool shown)
    {
        if (!all.TryGetValue(key, out var d)) return;
        d.Hidden[target] = !shown;
        Changed(key, d);
    }

    public static void Reset(string key)
    {
        if (!all.TryGetValue(key, out var d)) return;
        d.State.Clear();
        d.Hidden.Clear();
        Changed(key, d);
    }

    static void Changed(string key, Data d)
    {
        Save(d, key);
        foreach (var look in FindObjectsByType<VrcLook>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (look.Key == key) look.Apply();
    }

    // "param=value;...|target,target" (hidden parts)
    static void Save(Data d, string key)
    {
        var s = new List<string>();
        foreach (var kv in d.State) s.Add(K(kv.Key, kv.Value));
        var h = new List<string>();
        foreach (var kv in d.Hidden) h.Add((kv.Value ? "" : "+") + kv.Key.ToString(CultureInfo.InvariantCulture));
        PlayerPrefs.SetString(Pref(key), string.Join(";", s.ToArray()) + "|" + string.Join(",", h.ToArray()));
        PlayerPrefs.Save();
    }

    static void Load(Data d, string key)
    {
        string v = PlayerPrefs.GetString(Pref(key), "");
        string[] two = v.Split('|');
        foreach (string kv in two[0].Split(';'))
        {
            int eq = kv.LastIndexOf('=');
            if (eq > 0 && float.TryParse(kv.Substring(eq + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) d.State[kv.Substring(0, eq)] = f;
        }
        if (two.Length > 1)
            foreach (string p in two[1].Split(','))
            {
                if (p == "") continue;
                bool shown = p[0] == '+';
                if (int.TryParse(shown ? p.Substring(1) : p, NumberStyles.Integer, CultureInfo.InvariantCulture, out int t)) d.Hidden[t] = !shown;
            }
    }
}
