// The objects of a prefab or scene as Unity would build them: plain objects, and nested prefabs (a model's own
// prefab, other prefabs) instantiated with their overrides — names, switched on/off, materials, blend shapes,
// removed parts, objects and components added onto their parts. Most VRChat avatars sit in a scene as the model
// (.fbx) with the descriptor and PhysBones added; others are a prefab, plain or nested.
//
// A model's objects are made from the .fbx itself (ufbx). Unity names their meshes by xxHash64 ("Type:Mesh->name0"),
// which is matched; its GameObjects' and components' IDs are worked out some other way that isn't known here, so
// an override or an added object pointing at one of them is matched only on the model's root (its IDs are fixed).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Vrc
{
    public sealed class GNode
    {
        public string Name = "";
        public bool Active = true;
        public float[] T = { 0, 0, 0 }, R = { 0, 0, 0, 1 }, S = { 1, 1, 1 }; // in its parent, glTF's frame
        public GNode Parent;
        public readonly List<GNode> Kids = new List<GNode>();
        public readonly List<GComp> Comps = new List<GComp>();
        public GNode Scope;          // the root of the model it came from (bones are looked for there first)

        public void Add(GNode kid)
        {
            if (kid.Parent != null) kid.Parent.Kids.Remove(kid);
            kid.Parent = this;
            Kids.Add(kid);
        }
    }

    public sealed class GComp
    {
        public int Class;            // 137 skinned mesh renderer, 23 mesh renderer, 33 mesh filter, 95 animator, 114 script...
        public Dictionary<string, object> Body;
        public GNode Node;
        public bool Enabled = true;
        public FbxMesh Mesh;         // a model's renderer: its mesh
        public string FbxGuid;
        public PGFile File;          // where its references point
    }

    /// One file's objects by the IDs they have in it.
    public sealed class PGFile
    {
        public readonly Dictionary<long, GNode> Go = new Dictionary<long, GNode>(), Tf = new Dictionary<long, GNode>();
        public readonly Dictionary<long, GComp> Comp = new Dictionary<long, GComp>();
        public readonly List<GNode> Roots = new List<GNode>();
        public readonly List<GNode> Orphans = new List<GNode>(); // added onto a part of a model that couldn't be found

        public GNode Node(object reference) => long.TryParse(reference.S("fileID"), out long id) && id != 0 ? Node(id) : null;
        public GNode Node(long id) => Tf.TryGetValue(id, out var n) ? n : Go.TryGetValue(id, out n) ? n : Comp.TryGetValue(id, out var c) ? c.Node : null;
        public GComp Component(object reference) => long.TryParse(reference.S("fileID"), out long id) && Comp.TryGetValue(id, out var c) ? c : null;
    }

    public sealed class PrefabGraph
    {
        // a model prefab's root: fixed IDs
        public const long kRootGo = 919132149155446097, kRootTf = -8679921383154817045, kRootAnimator = 5866666021909216657;

        readonly Dictionary<string, PackageAsset> assets;
        readonly Func<string, FbxFile> fbx;
        public readonly List<string> Notes = new List<string>();
        int unmatched, unplaced, lostComps;

        public PrefabGraph(Dictionary<string, PackageAsset> assets, Func<string, FbxFile> fbx)
        {
            this.assets = assets;
            this.fbx = fbx;
        }

        public void Finish()
        {
            if (unmatched > 0) Notes.Add(unmatched + " overrides on the model's own parts (switched off, materials...) couldn't be matched to them: the model's defaults kept");
            if (lostComps > 0) Notes.Add(lostComps + " components added onto the model's own parts (PhysBones...) couldn't be matched to them: left out");
            if (unplaced > 0) Notes.Add(unplaced + " things added onto the model's bones couldn't be placed (kept on the avatar's root)");
        }

        /// A prefab or scene file's objects (nested prefabs made too).
        public PGFile Load(List<UDoc> docs, int depth = 0)
        {
            var f = new PGFile();
            var byId = new Dictionary<long, UDoc>();
            foreach (var d in docs) byId[d.Id] = d;
            // nested prefabs first: their objects are what the stripped stand-ins mean
            var instances = new Dictionary<long, PGFile>();
            foreach (var d in docs.Where(x => x.Class == 1001 && !x.Stripped))
            {
                var sub = depth < 6 ? Instance(d, depth) : null;
                if (sub != null) instances[d.Id] = sub;
            }
            foreach (var d in docs.Where(x => x.Stripped))
            {
                long inst = d.Body.FileId("m_PrefabInstance"), src = d.Body.FileId("m_CorrespondingSourceObject");
                if (!instances.TryGetValue(inst, out var sub)) continue;
                if (d.Class == 1) { if (sub.Go.TryGetValue(src, out var n)) f.Go[d.Id] = n; }
                else if (d.Class == 4 || d.Class == 224) { if (sub.Tf.TryGetValue(src, out var n)) f.Tf[d.Id] = n; }
                else if (sub.Comp.TryGetValue(src, out var c)) f.Comp[d.Id] = c;
            }
            // plain objects
            foreach (var d in docs.Where(x => x.Class == 1 && !x.Stripped))
                f.Go[d.Id] = new GNode { Name = d.Body.S("m_Name"), Active = d.Body.I("m_IsActive", 1) != 0 };
            foreach (var d in docs.Where(x => (x.Class == 4 || x.Class == 224) && !x.Stripped))
            {
                if (!f.Go.TryGetValue(d.Body.FileId("m_GameObject"), out var n)) continue;
                var p = V(d.Body, "m_LocalPosition", 0f);
                var q = d.Body.At("m_LocalRotation");
                n.T = new[] { -p[0], p[1], p[2] };
                n.R = new[] { q.F("x"), -q.F("y"), -q.F("z"), q.F("w", 1f) };
                n.S = V(d.Body, "m_LocalScale", 1f);
                f.Tf[d.Id] = n;
            }
            foreach (var d in docs.Where(x => x.Class != 1 && x.Class != 4 && x.Class != 224 && x.Class != 1001 && !x.Stripped && x.Body.ContainsKey("m_GameObject")))
            {
                var n = f.Node(d.Body.FileId("m_GameObject"));
                if (n == null) { if (byId.TryGetValue(d.Body.FileId("m_GameObject"), out var sg) && sg.Stripped) lostComps++; continue; }
                var c = new GComp { Class = d.Class, Body = d.Body, Node = n, Enabled = d.Body.I("m_Enabled", 1) != 0, File = f };
                n.Comps.Add(c);
                f.Comp[d.Id] = c;
            }
            // the tree: each plain transform under its father (a nested prefab's part, maybe)
            foreach (var d in docs.Where(x => (x.Class == 4 || x.Class == 224) && !x.Stripped))
            {
                if (!f.Tf.TryGetValue(d.Id, out var n)) continue;
                long father = d.Body.FileId("m_Father");
                if (father == 0) { f.Roots.Add(n); continue; }
                var p = f.Node(father);
                if (p != null) p.Add(n);
                else { f.Orphans.Add(n); unplaced++; }
            }
            // children order as the transforms list them
            foreach (var d in docs.Where(x => (x.Class == 4 || x.Class == 224) && !x.Stripped))
            {
                if (!f.Tf.TryGetValue(d.Id, out var n)) continue;
                var order = d.Body.L("m_Children").Select(c => f.Node(c)).Where(c => c != null && c.Parent == n).ToList();
                if (order.Count == n.Kids.Count) { n.Kids.Clear(); n.Kids.AddRange(order); }
            }
            // the nested prefabs' roots in place
            foreach (var kv in instances)
            {
                var d = byId[kv.Key];
                var parent = f.Node(d.Body.At("m_Modification").FileId("m_TransformParent"));
                foreach (var r in kv.Value.Roots)
                {
                    if (parent != null) parent.Add(r);
                    else f.Roots.Add(r);
                }
            }
            return f;
        }

        static float[] V(object o, string key, float or)
        {
            var v = o.At(key);
            return new[] { v.F("x", or), v.F("y", or), v.F("z", or) };
        }

        // A nested prefab: its source made, its overrides applied.
        PGFile Instance(UDoc d, int depth)
        {
            var mod = d.Body.At("m_Modification");
            string guid = d.Body.Guid("m_SourcePrefab");
            if (!assets.TryGetValue(guid, out var a) || a.File == null) { Notes.Add("a nested prefab isn't in the package (" + guid + ")"); return null; }
            string ext = Path.GetExtension(a.Path).ToLowerInvariant();
            PGFile sub;
            bool model = ext == ".fbx" || ext == ".obj";
            if (model) sub = Model(guid, a);
            else if (ext == ".prefab") sub = Load(UYaml.Parse(System.IO.File.ReadAllText(a.File)), depth + 1);
            else return null;
            if (sub == null) return null;
            // overrides
            foreach (var m in mod.L("m_Modifications"))
            {
                long target = long.TryParse(m.At("target").S("fileID"), out long t) ? t : 0;
                string prop = m.S("propertyPath"), value = m.S("value");
                if (sub.Go.TryGetValue(target, out var go))
                {
                    if (prop == "m_IsActive") go.Active = value != "0";
                    else if (prop == "m_Name") go.Name = value;
                }
                else if (sub.Tf.TryGetValue(target, out var tf))
                {
                    float v = float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float fv) ? fv : 0f;
                    bool root = model && sub.Roots.Contains(tf);
                    if (model && !root) continue; // a model's bones keep the model's own frame
                    if (prop.StartsWith("m_LocalScale.")) tf.S["xyz".IndexOf(prop[prop.Length - 1])] = v;
                    else if (prop == "m_LocalPosition.x") tf.T[0] = -v;
                    else if (prop == "m_LocalPosition.y") tf.T[1] = v;
                    else if (prop == "m_LocalPosition.z") tf.T[2] = v;
                    else if (prop == "m_LocalRotation.x") tf.R[0] = v;
                    else if (prop == "m_LocalRotation.y") tf.R[1] = -v;
                    else if (prop == "m_LocalRotation.z") tf.R[2] = -v;
                    else if (prop == "m_LocalRotation.w") tf.R[3] = v;
                }
                else if (sub.Comp.TryGetValue(target, out var c))
                {
                    if (prop == "m_Enabled") c.Enabled = value != "0";
                    else if (prop.StartsWith("m_Materials.Array.data["))
                    {
                        int i = int.Parse(prop.Substring(23).TrimEnd(']'), CultureInfo.InvariantCulture);
                        var mats = c.Body.L("m_Materials");
                        if (!c.Body.ContainsKey("m_Materials")) c.Body["m_Materials"] = mats;
                        while (mats.Count <= i) mats.Add(new Dictionary<string, object> { { "fileID", "0" } });
                        mats[i] = m.At("objectReference");
                    }
                    else if (prop.StartsWith("m_BlendShapeWeights.Array.data["))
                    {
                        int i = int.Parse(prop.Substring(31).TrimEnd(']'), CultureInfo.InvariantCulture);
                        var w = c.Body.L("m_BlendShapeWeights");
                        if (!c.Body.ContainsKey("m_BlendShapeWeights")) c.Body["m_BlendShapeWeights"] = w;
                        while (w.Count <= i) w.Add("0");
                        w[i] = value;
                    }
                    else if (prop == "m_Mesh") c.Body["m_Mesh"] = m.At("objectReference");
                }
                else if (model && prop != "m_RootOrder" && !prop.StartsWith("m_LocalEulerAnglesHint")) unmatched++;
            }
            foreach (var r in mod.L("m_RemovedComponents"))
                if (sub.Comp.TryGetValue(long.TryParse(r.S("fileID"), out long id) ? id : 0, out var c)) c.Node.Comps.Remove(c);
            foreach (var r in mod.L("m_RemovedGameObjects"))
                if (sub.Go.TryGetValue(long.TryParse(r.S("fileID"), out long id) ? id : 0, out var n) && n.Parent != null) n.Parent.Kids.Remove(n);
            return sub;
        }

        // A model's own prefab, made from the .fbx: its nodes, a renderer on each mesh (the materials the import
        // settings give it), an Animator on the root.
        PGFile Model(string guid, PackageAsset a)
        {
            var file = fbx(guid);
            if (file == null) return null;
            var f = new PGFile();
            var nodes = new GNode[file.List.Count];
            for (int i = 0; i < nodes.Length; i++)
            {
                var fn = file.List[i];
                nodes[i] = new GNode { Name = fn.Parent < 0 ? Path.GetFileNameWithoutExtension(a.Path) : fn.Name, T = (float[])fn.T.Clone(), R = (float[])fn.R.Clone(), S = (float[])fn.S.Clone() };
            }
            GNode root = null;
            for (int i = 0; i < nodes.Length; i++)
            {
                int p = file.List[i].Parent;
                if (p >= 0 && p < nodes.Length) nodes[p].Add(nodes[i]);
                else if (root == null) root = nodes[i];
            }
            if (root == null) return null;
            // a model with one top node: Unity makes that node the root itself (its parts, its children)
            if (root.Kids.Count == 1 && root.Comps.Count == 0)
            {
                var top = root.Kids[0];
                root.Kids.Clear();
                foreach (var k in top.Kids.ToList()) root.Add(k);
                root.S = top.S;
                for (int i = 0; i < nodes.Length; i++) if (nodes[i] == top) nodes[i] = root;
            }
            // the root at the model's origin (the scene places it)
            root.T = new float[] { 0, 0, 0 };
            root.R = new float[] { 0, 0, 0, 1 };
            foreach (var n in nodes) n.Scope = root;
            f.Roots.Add(root);
            f.Go[kRootGo] = root;
            f.Tf[kRootTf] = root;
            var remap = Remap(a.Meta);
            for (int i = 0; i < nodes.Length; i++)
            {
                int mi = file.List[i].Mesh;
                if (mi < 0) continue;
                var mesh = file.Meshes[mi];
                var mats = new List<object>();
                foreach (var slot in mesh.Slots)
                    mats.Add(remap.TryGetValue(slot.Key, out var r) ? r : new Dictionary<string, object> { { "fileID", "0" }, { "name", slot.Key } });
                var body = new Dictionary<string, object> { { "m_Materials", mats }, { "m_Enabled", "1" } };
                var c = new GComp { Class = 137, Body = body, Node = nodes[i], Mesh = mesh, FbxGuid = guid, File = f };
                nodes[i].Comps.Add(c);
                f.Comp[XxHash64.FileId("Mesh", mesh.Name, 0) ^ 0x5A5A] = c; // (no known ID: kept apart)
            }
            var anim = new GComp
            {
                Class = 95, Node = root, File = f,
                Body = new Dictionary<string, object> { { "m_Avatar", new Dictionary<string, object> { { "fileID", "9000000" }, { "guid", guid } } } },
            };
            root.Comps.Add(anim);
            f.Comp[kRootAnimator] = anim;
            return f;
        }

        // the model's materials as its import settings point them (externalObjects: name -> material)
        static Dictionary<string, object> Remap(string meta)
        {
            var r = new Dictionary<string, object>();
            foreach (Match m in Regex.Matches(meta ?? "", @"type: UnityEngine:Material\s*\n\s*assembly: [^\n]*\n\s*name: ([^\n]*)\n\s*second: (\{[^}]*\})"))
            {
                string name = m.Groups[1].Value.Trim();
                if (name.Length >= 2 && (name[0] == '\'' || name[0] == '"')) name = name.Substring(1, name.Length - 2);
                var flow = new Dictionary<string, object>();
                foreach (var part in m.Groups[2].Value.Trim('{', '}').Split(','))
                {
                    int c = part.IndexOf(':');
                    if (c > 0) flow[part.Substring(0, c).Trim()] = part.Substring(c + 1).Trim();
                }
                r[name] = flow;
            }
            return r;
        }
    }
}
