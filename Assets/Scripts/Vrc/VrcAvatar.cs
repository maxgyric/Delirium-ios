// A VRChat (SDK3) avatar read from its package without VRChat's SDK: the descriptor in the avatar's prefab, its
// Expressions Menu (toggles, sub-menus), Expression Parameters and FX controller. What each toggle does is worked out
// by running the FX layers' state machines with the toggle's parameter set (the rest at their defaults) and reading
// the clips they land on: objects switched on / off, blend shapes set.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Vrc
{
    public struct Effect
    {
        public string Path, Attr; // Attr: "active", "blend:<shape>", "mat:<slot>" (Value then a texture GUID: not used yet)
        public float Value;
        public string Ref;
        public string Key => Path + "|" + Attr;
    }

    public sealed class Toggle
    {
        public string Menu, Name, Param;
        public float Value;
        public int Kind; // 101 button, 102 toggle
        public bool ParamIsBool;
    }

    public sealed class Avatar
    {
        public string Name, PrefabGuid;
        public Dictionary<string, PackageAsset> Assets;
        public List<UDoc> Prefab;               // the prefab or scene it's in
        public UDoc Descriptor;
        public GNode Root;                      // the avatar's own object (the descriptor's), its parts under it
        public PGFile Graph;
        public PrefabGraph Builder;
        public readonly Dictionary<string, FbxFile> Models = new Dictionary<string, FbxFile>(); // .fbx files read, by GUID
        public readonly List<string> ModelNotes = new List<string>();

        /// A model of the package, read once (null: it isn't there or didn't read).
        public FbxFile Fbx(string guid)
        {
            if (Models.TryGetValue(guid, out var f)) return f;
            Models[guid] = null;
            if (!Assets.TryGetValue(guid, out var a) || a.File == null) { ModelNotes.Add("a model file isn't in the package (" + guid + ")"); return null; }
            string ext = Path.GetExtension(a.Path).ToLowerInvariant();
            if (ext != ".fbx" && ext != ".obj")
            {
                ModelNotes.Add(Path.GetFileName(a.Path) + ": a " + ext + " model isn't read (an .fbx is)");
                return null;
            }
            try { Models[guid] = f = FbxFile.Load(a.File); }
            catch (Exception e) { ModelNotes.Add(Path.GetFileName(a.Path) + ": " + e.Message); }
            return f;
        }
        public readonly List<Toggle> Toggles = new List<Toggle>();
        public readonly Dictionary<string, float> Defaults = new Dictionary<string, float>();       // parameter -> its default
        public readonly Dictionary<string, Effect> Base = new Dictionary<string, Effect>();          // what the FX layers set with every parameter at its default
        public readonly Dictionary<string, List<Effect>> ValueEffects = new Dictionary<string, List<Effect>>(); // "param=value" -> what differs from Base
        public readonly List<string> Notes = new List<string>();

        public static string Key(string param, float value) => param + "=" + value.ToString(CultureInfo.InvariantCulture);

        public string Label = "";               // for a choice: the file and the object it's on
        public string File0 = "";
        public int Score;

        /// The package's best avatar (FindAll's first), made.
        public static Avatar Find(Dictionary<string, PackageAsset> assets)
        {
            var all = FindAll(assets);
            if (all.Count == 0) return null;
            all[0].Make();
            return all[0];
        }

        /// Every avatar in the package's prefabs and scenes (a scene may hold one for PC and one for Quest), the
        /// likeliest first: for PC, with the most toggles.
        public static List<Avatar> FindAll(Dictionary<string, PackageAsset> assets)
        {
            var found = new List<Avatar>();
            foreach (var a in assets.Values.OrderBy(x => x.Path))
            {
                if (a.File == null) continue;
                bool scene = a.Path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase);
                if (!scene && !a.Path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) continue;
                string text = File.ReadAllText(a.File);
                if (!text.Contains("expressionsMenu:")) continue;
                var docs = UYaml.Parse(text);
                foreach (var desc in docs.Where(d => !d.Stripped && d.Body.ContainsKey("expressionsMenu") && d.Body.ContainsKey("baseAnimationLayers")))
                {
                    var av = new Avatar { Name = Path.GetFileNameWithoutExtension(a.Path), PrefabGuid = a.Guid, Assets = assets, Prefab = docs, Descriptor = desc, File0 = a.Path };
                    av.Read(desc);
                    string goName = GoName(docs, desc.Body.FileId("m_GameObject"), assets);
                    av.Label = Path.GetFileName(a.Path) + (goName != "" ? " > " + goName : "");
                    string lower = (a.Path + " " + goName).ToLowerInvariant();
                    av.Score = av.Toggles.Count * 10 + (scene ? 0 : 1);
                    foreach (string w in new[] { "quest", "fallback", "android", "mobile", "lite", "impostor" })
                        if (lower.Contains(w)) av.Score -= 100000;
                    if (goName != "") av.Name = goName;
                    found.Add(av);
                }
            }
            return found.OrderByDescending(x => x.Score).ToList();
        }

        // an object's name in a prefab or scene: its own, or the name its nested prefab gives it
        static string GoName(List<UDoc> docs, long id, Dictionary<string, PackageAsset> assets)
        {
            var go = docs.FirstOrDefault(d => d.Id == id);
            if (go == null) return "";
            if (!go.Stripped) return go.Body.S("m_Name");
            long inst = go.Body.FileId("m_PrefabInstance");
            var pi = docs.FirstOrDefault(d => d.Id == inst);
            if (pi == null) return "";
            foreach (var m in pi.Body.At("m_Modification").L("m_Modifications"))
                if (m.S("propertyPath") == "m_Name" && m.At("target").S("fileID") == go.Body.At("m_CorrespondingSourceObject").S("fileID")) return m.S("value");
            return assets.TryGetValue(pi.Body.Guid("m_SourcePrefab"), out var src) ? Path.GetFileNameWithoutExtension(src.Path) : "";
        }

        /// Its objects made (nested prefabs and the models too).
        public void Make()
        {
            Builder = new PrefabGraph(Assets, Fbx);
            Graph = Builder.Load(Prefab);
            Builder.Finish();
            Root = Graph.Comp.TryGetValue(Descriptor.Id, out var dc) ? dc.Node : null;
            if (Root != null && Root.Name != "") Name = Root.Name;
            // what was added onto the model's parts that weren't found: kept on the avatar
            if (Root != null) foreach (var o in Graph.Orphans) Root.Add(o);
        }

        List<UDoc> Load(string guid)
        {
            if (string.IsNullOrEmpty(guid) || !Assets.TryGetValue(guid, out var a) || a.File == null) return null;
            if (!cache.TryGetValue(guid, out var docs)) cache[guid] = docs = UYaml.Parse(File.ReadAllText(a.File));
            return docs;
        }
        readonly Dictionary<string, List<UDoc>> cache = new Dictionary<string, List<UDoc>>();

        void Read(UDoc desc)
        {
            // parameters
            var pars = Load(desc.Body.Guid("expressionParameters"));
            var parTypes = new Dictionary<string, int>();
            if (pars != null)
                foreach (var p in pars[0].Body.L("parameters"))
                {
                    string n = p.S("name");
                    if (n == "") continue;
                    Defaults[n] = p.F("defaultValue");
                    parTypes[n] = p.I("valueType"); // 0 int, 1 float, 2 bool
                }
            // the menu
            var seenMenus = new HashSet<string>();
            Menu(desc.Body.Guid("expressionsMenu"), "", 0, seenMenus, parTypes);
            // menus in the package on the same parameters that the menu doesn't reach (an edit unlinked them)
            string parGuid = desc.Body.Guid("expressionParameters");
            foreach (var a in Assets.Values.OrderBy(x => x.Path))
            {
                if (a.File == null || seenMenus.Contains(a.Guid) || !a.Path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) continue;
                var info = new FileInfo(a.File);
                if (info.Length > 1 << 20) continue;
                string text = File.ReadAllText(a.File);
                if (!text.Contains("controls:") || parGuid == "" || !text.Contains(parGuid)) continue;
                int before = Toggles.Count;
                Menu(a.Guid, "(not in the menu) " + Path.GetFileNameWithoutExtension(a.Path), 0, seenMenus, parTypes);
                if (Toggles.Count > before) Notes.Add("menu " + Path.GetFileNameWithoutExtension(a.Path) + " isn't reached from the avatar's menu: listed anyway");
            }
            // the playable layers' controllers in VRChat's order (Base, Additive, Gesture, Action, FX: later ones win);
            // toggles are usually in FX, some avatars keep them in Gesture
            var fsms = new List<Fsm>();
            foreach (var l in desc.Body.L("baseAnimationLayers").OrderBy(l => l.I("type") == 5 ? 9 : l.I("type")))
            {
                var ctrl = Load(l.Guid("animatorController"));
                if (ctrl != null) fsms.Add(new Fsm(this, ctrl));
            }
            if (fsms.Count == 0) { Notes.Add("no animator controllers: the toggles do nothing"); return; }
            foreach (var f in fsms)
                foreach (var kv in f.ParamDefaults) if (!Defaults.ContainsKey(kv.Key)) Defaults[kv.Key] = kv.Value;
            Func<Dictionary<string, float>, IEnumerable<Effect>> run = st =>
            {
                var all = new Dictionary<string, Effect>();
                foreach (var f in fsms) foreach (var e in f.Run(st)) all[e.Key] = e;
                return all.Values;
            };
            var state = new Dictionary<string, float>(Defaults);
            foreach (var e in run(state)) Base[e.Key] = e;
            foreach (var t in Toggles)
            {
                foreach (float v in new[] { t.Value, 0f })
                {
                    string k = Key(t.Param, v);
                    if (ValueEffects.ContainsKey(k)) continue;
                    var s = new Dictionary<string, float>(Defaults); s[t.Param] = v;
                    var diff = new List<Effect>();
                    foreach (var e in run(s))
                        if (!Base.TryGetValue(e.Key, out var b) || Math.Abs(b.Value - e.Value) > 1e-4f || b.Ref != e.Ref) diff.Add(e);
                    ValueEffects[k] = diff;
                }
            }
            // toggles nothing listens to (an edit took their layers out): not shown
            var dead = Toggles.Where(t => ValueEffects[Key(t.Param, t.Value)].Count == 0 && ValueEffects[Key(t.Param, 0)].Count == 0).ToList();
            foreach (var t in dead) Toggles.Remove(t);
            if (dead.Count > 0) Notes.Add(dead.Count + " menu items change nothing in this avatar's controllers (" + string.Join(", ", dead.Take(6).Select(t => t.Name)) + (dead.Count > 6 ? "..." : "") + "): left out");
        }

    void Menu(string guid, string path, int depth, HashSet<string> seen, Dictionary<string, int> parTypes)
        {
            if (depth > 8 || !seen.Add(guid)) return;
            var m = Load(guid);
            if (m == null) { Notes.Add("menu " + path + " isn't in the package"); return; }
            foreach (var c in m[0].Body.L("controls"))
            {
                int type = c.I("type");
                string name = c.S("name");
                if (type == 103) Menu(c.Guid("subMenu"), path == "" ? name : path + "/" + name, depth + 1, seen, parTypes);
                else if (type == 101 || type == 102)
                {
                    string param = c.At("parameter").S("name");
                    if (param == "") continue;
                    Toggles.Add(new Toggle { Menu = path, Name = name, Param = param, Value = c.F("value", 1), Kind = type,
                                             ParamIsBool = parTypes.ContainsKey(param) && parTypes[param] == 2 });
                }
                else if (type != 0) Notes.Add("menu " + (path == "" ? "" : path + "/") + name + ": a " + (type == 203 ? "radial" : type == 201 || type == 202 ? "puppet" : "control " + type) + " (not taken)");
            }
        }

        // ------------------------------------------------------------------ the FX controller, run
        sealed class Fsm
        {
            readonly Avatar av;
            readonly Dictionary<long, UDoc> byId = new Dictionary<long, UDoc>();
            readonly List<long> layers = new List<long>();
            public readonly Dictionary<string, float> ParamDefaults = new Dictionary<string, float>();

            public Fsm(Avatar av, List<UDoc> docs)
            {
                this.av = av;
                foreach (var d in docs) byId[d.Id] = d;
                var c = docs.FirstOrDefault(d => d.Type == "AnimatorController");
                if (c == null) return;
                foreach (var p in c.Body.L("m_AnimatorParameters"))
                {
                    int type = p.I("m_Type");
                    ParamDefaults[p.S("m_Name")] = type == 1 ? p.F("m_DefaultFloat") : type == 3 ? p.I("m_DefaultInt") : type == 4 ? p.I("m_DefaultBool") : 0;
                }
                foreach (var l in c.Body.L("m_AnimatorLayers")) layers.Add(l.FileId("m_StateMachine"));
            }

            // VRChat's own parameters as they stand for you alone in a scene
            static readonly Dictionary<string, float> Builtin = new Dictionary<string, float> { {"IsLocal", 1}, {"Grounded", 1}, {"Upright", 1}, {"TrackingType", 3}, {"VRMode", 0}, {"ScaleFactor", 1}, {"ScaleFactorInverse", 1} };

            float P(Dictionary<string, float> s, string n) => s.TryGetValue(n, out float v) ? v : Builtin.TryGetValue(n, out v) ? v : ParamDefaults.TryGetValue(n, out v) ? v : 0f;

            bool Holds(object t, Dictionary<string, float> s)
            {
                foreach (var c in t.L("m_Conditions"))
                {
                    float v = P(s, c.S("m_ConditionEvent")), th = c.F("m_EventTreshold");
                    int mode = c.I("m_ConditionMode");
                    bool ok = mode == 1 ? v != 0 : mode == 2 ? v == 0 : mode == 3 ? v > th : mode == 4 ? v < th :
                              mode == 6 ? Math.Abs(v - th) < 1e-4f : mode == 7 ? Math.Abs(v - th) >= 1e-4f : false;
                    if (!ok) return false;
                }
                return true;
            }

            UDoc Doc(object list) => byId.TryGetValue(long.TryParse(list.S("fileID"), out long id) ? id : 0, out var d) ? d : null;

            void Machines(long sm, List<UDoc> into)
            {
                if (!byId.TryGetValue(sm, out var m) || into.Contains(m)) return;
                into.Add(m);
                foreach (var c in m.Body.L("m_ChildStateMachines")) Machines(c.FileId("m_StateMachine"), into);
            }

            UDoc Enter(UDoc sm, Dictionary<string, float> s, int depth = 0)
            {
                if (sm == null || depth > 8) return null;
                foreach (var tr in sm.Body.L("m_EntryTransitions"))
                {
                    var t = Doc(tr);
                    if (t == null || !Holds(t.Body, s)) continue;
                    if (byId.TryGetValue(t.Body.FileId("m_DstState"), out var st)) return st;
                    if (byId.TryGetValue(t.Body.FileId("m_DstStateMachine"), out var sub)) return Enter(sub, s, depth + 1);
                }
                return byId.TryGetValue(sm.Body.FileId("m_DefaultState"), out var def) ? def : null;
            }

            public IEnumerable<Effect> Run(Dictionary<string, float> s)
            {
                var all = new Dictionary<string, Effect>();
                foreach (long smId in layers)
                {
                    UDoc root;
                    if (!byId.TryGetValue(smId, out root)) continue;
                    var cur = Settle(root, smId, s);
                    if (cur == null) continue;
                    foreach (var e in Motion(cur.Body.At("m_Motion"), s, 0)) all[e.Key] = e; // later layers win
                }
                return all.Values;
            }

            // The state a layer rests in with these parameters: an Any State transition that holds wins (every frame,
            // whatever its state's exit); else from the entry along transitions whose conditions hold (timed ones,
            // without conditions, are an intro: not followed).
            UDoc Settle(UDoc root, long smId, Dictionary<string, float> s)
            {
                var machines = new List<UDoc>();
                Machines(smId, machines);
                foreach (var m in machines)
                    foreach (var tr in m.Body.L("m_AnyStateTransitions"))
                    {
                        var t = Doc(tr);
                        if (t == null || t.Body.I("m_Mute") == 1 || t.Body.L("m_Conditions").Count == 0 || !Holds(t.Body, s)) continue;
                        UDoc d;
                        if (byId.TryGetValue(t.Body.FileId("m_DstState"), out d)) return d;
                    }
                var cur = Enter(root, s);
                var visited = new HashSet<long>();
                for (int step = 0; cur != null && step < 24 && visited.Add(cur.Id); step++)
                {
                    UDoc next = null;
                    bool exit = false;
                    foreach (var tr in cur.Body.L("m_Transitions"))
                    {
                        var t = Doc(tr);
                        if (t == null || t.Body.I("m_Mute") == 1 || t.Body.L("m_Conditions").Count == 0 || !Holds(t.Body, s)) continue;
                        if (t.Body.I("m_IsExit") == 1) { exit = true; break; }
                        UDoc d;
                        if (byId.TryGetValue(t.Body.FileId("m_DstState"), out d)) { next = d; break; }
                        if (byId.TryGetValue(t.Body.FileId("m_DstStateMachine"), out d)) { next = Enter(d, s); break; }
                    }
                    if (exit) next = Enter(root, s);
                    if (next == null || next == cur) break;
                    cur = next;
                }
                return cur;
            }

            IEnumerable<Effect> Motion(object motion, Dictionary<string, float> s, int depth)
            {
                if (motion == null || depth > 6) yield break;
                string guid = motion.S("guid");
                long id = long.TryParse(motion.S("fileID"), out long f) ? f : 0;
                if (id == 0) yield break;
                UDoc d = null;
                if (guid != "" && guid != "0000000000000000f000000000000000")
                {
                    var docs = av.Load(guid);
                    d = docs?.FirstOrDefault(x => x.Id == id) ?? docs?.FirstOrDefault();
                }
                else byId.TryGetValue(id, out d);
                if (d == null) yield break;
                if (d.Type == "BlendTree")
                {
                    var kids = d.Body.L("m_Childs");
                    if (kids.Count == 0) yield break;
                    object pick = kids[0];
                    if (d.Body.I("m_BlendType") == 0) // 1D: the child nearest the parameter
                    {
                        float v = P(s, d.Body.S("m_BlendParameter"));
                        pick = kids.OrderBy(k => Math.Abs(k.F("m_Threshold") - v)).First();
                    }
                    foreach (var e in Motion(pick.At("m_Motion"), s, depth + 1)) yield return e;
                    if (d.Body.I("m_BlendType") == 4) // direct: every child whose weight parameter is up
                        foreach (var k in kids.Skip(1))
                            if (P(s, k.S("m_DirectBlendParameter")) > 0.5f)
                                foreach (var e in Motion(k.At("m_Motion"), s, depth + 1)) yield return e;
                    yield break;
                }
                if (d.Type != "AnimationClip") yield break;
                foreach (var c in d.Body.L("m_FloatCurves"))
                {
                    string attr = c.S("attribute"), path = c.S("path");
                    var keys = c.At("curve").L("m_Curve");
                    if (keys.Count == 0) continue;
                    float v = keys[0].F("value");
                    if (attr == "m_IsActive") yield return new Effect { Path = path, Attr = "active", Value = v };
                    else if (attr.StartsWith("blendShape.")) yield return new Effect { Path = path, Attr = "blend:" + attr.Substring(11), Value = v };
                    else if (attr == "m_Enabled") yield return new Effect { Path = path, Attr = "enabled:" + c.S("classID"), Value = v };
                }
                foreach (var c in d.Body.L("m_PPtrCurves"))
                {
                    string attr = c.S("attribute");
                    var keys = c.L("curve");
                    if (keys.Count == 0 || !attr.StartsWith("m_Materials.Array.data[")) continue;
                    string slot = attr.Substring(23).TrimEnd(']');
                    yield return new Effect { Path = c.S("path"), Attr = "mat:" + slot, Ref = keys[0].At("value").S("guid") };
                }
            }
        }
    }
}
