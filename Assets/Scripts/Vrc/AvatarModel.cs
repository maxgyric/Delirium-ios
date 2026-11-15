// A VRChat avatar (Avatar.Find) made into a .glb the sandbox loads like any character, and its toggles into a
// sidecar file next to it (<name>.vrc.json, read by VrcLook):
//  - the avatar prefab's own tree (names, places, which objects start switched off), so the toggles' clip paths
//    find their objects;
//  - each renderer's mesh from its .fbx (ufbx), skinned to the prefab's bones (matched by name), its blend shapes;
//  - its materials' main texture and colour, both faces when the material culls nothing (Culling Off), the back
//    culled otherwise (a material set to cull its front shows black inside-out: the package's own advice is to set it
//    to Back, which this does);
//  - the humanoid bones named as Mixamo's (from the .fbx's Unity humanoid mapping), so any skeleton plays the clips;
//  - VRChat PhysBones as VRM 1.0 spring bones (SpringBones reads them).
// Textures are handed out (Textures) to be shrunk and encoded where Unity's image code is (VrcImport), then written.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Vrc
{
    public sealed class TexJob
    {
        public string Guid, File, Ext;
        public bool Alpha;          // a cut-out or see-through material uses it: kept as PNG
        public byte[] Out;          // the encoded picture for the .glb (null: left out)
        public string Mime = "image/png";
        public int Max = 2048;      // its longest side at most
    }

    public sealed class AvatarModel
    {
        public readonly List<string> Notes = new List<string>();
        public readonly List<TexJob> Textures = new List<TexJob>();
        public readonly List<TexJob> Icons = new List<TexJob>();      // the expressions menu's icons (kept in the sidecar)
        readonly Dictionary<string, int> iconOf = new Dictionary<string, int>();
        public int Triangles, MeshCount, Springs;

        sealed class Node
        {
            public string Name;
            public GNode G;
            public float[] T, R, S;
            public readonly List<int> Kids = new List<int>();
            public int Parent = -1;
            public bool Active = true;
            public string Path;
            public float[] World;
            public int Mesh = -1, Skin = -1;
        }

        readonly Avatar av;
        readonly List<Node> nodes = new List<Node>();
        readonly Dictionary<GNode, int> nodeOf = new Dictionary<GNode, int>();
        readonly Dictionary<string, int> nodeOfPath = new Dictionary<string, int>();
        readonly List<int> disabledRenderers = new List<int>();
        readonly Dictionary<int, Dictionary<string, float>> blendDefaults = new Dictionary<int, Dictionary<string, float>>();

        // the .glb being written
        readonly List<object> gMeshes = new List<object>(), gSkins = new List<object>(), gMaterials = new List<object>(),
                              gAccessors = new List<object>(), gViews = new List<object>(), gTextures = new List<object>(), gImages = new List<object>();
        readonly MemoryStream bin = new MemoryStream();
        readonly Dictionary<string, int> matOf = new Dictionary<string, int>();
        readonly Dictionary<string, int> texOf = new Dictionary<string, int>();
        object springExt;

        AvatarModel(Avatar av) { this.av = av; }

        // ------------------------------------------------------------------ the tree
        public static AvatarModel Build(Avatar av, Action<string> status)
        {
            var m = new AvatarModel(av);
            if (av.Root == null) throw new Exception("the avatar's own object isn't in its prefab or scene");
            m.Tree(av.Root, -1, "");
            if (status != null) status("reading the meshes");
            m.Renderers();
            m.Humanoid();
            try { m.PhysBones(); }
            catch (Exception e) { m.Notes.Add("PhysBones: " + e.Message); }
            m.Notes.InsertRange(0, av.Builder.Notes.Concat(av.ModelNotes));
            m.MenuIcons(av.MenuRoot);
            return m;
        }

        // the menu's pictures, small
        void MenuIcons(MenuNode m)
        {
            foreach (var i in m.Items)
            {
                if (i.Icon != "" && !iconOf.ContainsKey(i.Icon) && av.Assets.TryGetValue(i.Icon, out var a) && a.File != null)
                {
                    string ext = Path.GetExtension(a.Path).ToLowerInvariant();
                    if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".tga")
                    {
                        iconOf[i.Icon] = Icons.Count;
                        Icons.Add(new TexJob { Guid = i.Icon, File = a.File, Ext = ext, Alpha = true, Max = 128 });
                    }
                }
                if (i.Kind == 103) MenuIcons(i);
            }
        }

        object MenuJson(MenuNode m, Dictionary<Toggle, int> toggleIndex)
        {
            var o = new Dictionary<string, object>
            {
                { "name", m.Name }, { "kind", m.Kind },
                { "icon", m.Icon != "" && iconOf.TryGetValue(m.Icon, out int ic) && Icons[ic].Out != null ? ic : -1 },
            };
            if (m.Toggle != null && toggleIndex.TryGetValue(m.Toggle, out int ti)) o["toggle"] = ti;
            if (m.Kind == 103 || m.Kind == 0) o["items"] = m.Items.Select(x => MenuJson(x, toggleIndex)).ToList();
            return o;
        }

        static GComp Comp(GNode n, params int[] classes) => n?.Comps.FirstOrDefault(c => classes.Contains(c.Class));
        int Index(GNode n) => n != null && nodeOf.TryGetValue(n, out int i) ? i : -1;

        static float[] V3(object o, string key, float or = 0f)
        {
            var v = o.At(key);
            return new[] { v.F("x", or), v.F("y", or), v.F("z", or) };
        }

        void Tree(GNode g, int parent, string path)
        {
            var n = new Node { Name = g.Name, G = g, Parent = parent, Active = parent < 0 || g.Active, Path = path };
            // the avatar itself at the origin, unturned
            n.T = parent < 0 ? new float[] { 0, 0, 0 } : (float[])g.T.Clone();
            n.R = parent < 0 ? new float[] { 0, 0, 0, 1 } : (float[])g.R.Clone();
            n.S = (float[])g.S.Clone();
            int me = nodes.Count;
            nodes.Add(n);
            nodeOf[g] = me;
            if (!nodeOfPath.ContainsKey(path)) nodeOfPath[path] = me;
            var local = M4.Trs(n.T, n.R, n.S);
            n.World = parent < 0 ? local : M4.Mul(nodes[parent].World, local);
            if (parent >= 0) nodes[parent].Kids.Add(me);
            foreach (var k in g.Kids) Tree(k, me, path == "" ? k.Name : path + "/" + k.Name);
        }

        // ------------------------------------------------------------------ meshes
        readonly Dictionary<string, Dictionary<long, FbxMesh>> meshOfId = new Dictionary<string, Dictionary<long, FbxMesh>>();

        FbxFile Fbx(string guid) => av.Fbx(guid);

        static string Plain(string s) => new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        // The mesh a renderer's {fileID, guid} means. Unity names a model's meshes by hashed IDs the package doesn't
        // list, so: the mesh named as the renderer's object (it's so in nearly every avatar); then by elimination.
        FbxMesh MeshFor(string guid, long id, string goName, List<KeyValuePair<long, string>> allRefs)
        {
            var f = Fbx(guid);
            if (f == null || f.Meshes.Count == 0) return null;
            if (!meshOfId.TryGetValue(guid, out var map))
            {
                meshOfId[guid] = map = new Dictionary<long, FbxMesh>();
                var refs = allRefs.GroupBy(r => r.Key).ToList();
                var used = new HashSet<FbxMesh>();
                // Unity's own IDs for them: xxHash64("Type:Mesh->" + name + n)
                var seen = new Dictionary<string, int>();
                var ids = new Dictionary<long, FbxMesh>();
                foreach (var m in f.Meshes)
                {
                    int n = seen.TryGetValue(m.Name, out int c) ? c : 0;
                    seen[m.Name] = n + 1;
                    ids[XxHash64.FileId("Mesh", m.Name, n)] = m;
                }
                foreach (var g in refs)
                    if (ids.TryGetValue(g.Key, out var hit)) { map[g.Key] = hit; used.Add(hit); }
                foreach (var g in refs) // exact names
                {
                    if (map.ContainsKey(g.Key)) continue;
                    var hit = f.Meshes.FirstOrDefault(m => !used.Contains(m) && g.Any(r => m.Name == r.Value));
                    if (hit != null) { map[g.Key] = hit; used.Add(hit); }
                }
                foreach (var g in refs) // the same letters and digits ("Microphone" for "Frenni - Microphone": one contains the other)
                {
                    if (map.ContainsKey(g.Key)) continue;
                    var hit = f.Meshes.FirstOrDefault(m => !used.Contains(m) && g.Any(r => Plain(m.Name) == Plain(r.Value)))
                              ?? f.Meshes.FirstOrDefault(m => !used.Contains(m) && g.Any(r => Plain(r.Value).Length > 2 && (Plain(m.Name).Contains(Plain(r.Value)) || Plain(r.Value).Contains(Plain(m.Name)))));
                    if (hit != null) { map[g.Key] = hit; used.Add(hit); }
                }
                var left = f.Meshes.Where(m => !used.Contains(m)).ToList();
                var unmatched = refs.Where(g => !map.ContainsKey(g.Key)).ToList();
                if (unmatched.Count == 1 && left.Count == 1) map[unmatched[0].Key] = left[0];
                else if (unmatched.Count > 0)
                    Notes.Add(unmatched.Count + " meshes of " + Path.GetFileName(av.Assets[guid].Path) + " weren't told apart (" + string.Join(", ", unmatched.Select(g => g.First().Value).Take(4)) + ")");
            }
            return map.TryGetValue(id, out var mesh) ? mesh : null;
        }

        void Renderers()
        {
            // every renderer's mesh reference first (a model's meshes are matched all at once)
            var rs = new List<KeyValuePair<int, GComp>>();
            var refsOf = new Dictionary<string, List<KeyValuePair<long, string>>>();
            for (int i = 0; i < nodes.Count; i++)
            {
                var r = Comp(nodes[i].G, 137, 23);
                if (r == null) continue;
                if (r.Mesh != null) { rs.Add(new KeyValuePair<int, GComp>(i, r)); continue; } // a model's own
                object mref = r.Class == 137 ? r.Body.At("m_Mesh") : Comp(nodes[i].G, 33)?.Body.At("m_Mesh");
                string guid = mref.S("guid");
                long id = long.TryParse(mref.S("fileID"), out long fid) ? fid : 0;
                if (guid == "" || id == 0) continue;
                rs.Add(new KeyValuePair<int, GComp>(i, r));
                if (!refsOf.TryGetValue(guid, out var l)) refsOf[guid] = l = new List<KeyValuePair<long, string>>();
                l.Add(new KeyValuePair<long, string>(id, nodes[i].Name));
            }
            // a material the package lacks: the pictures its renderers' other materials use (an edit that retextured
            // the body did its eyes' and brows' materials on the same atlas too)
            foreach (var kv in rs)
            {
                var mats = kv.Value.Body.L("m_Materials");
                var have = new List<string>();
                var lack = new List<string>();
                foreach (var m in mats)
                {
                    string g = m.S("guid");
                    if (g == "") continue;
                    string tex = av.Assets.TryGetValue(g, out var ma) && ma.File != null && ma.Path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase) ? MainTexOf(ma) : null;
                    if (tex != null) have.Add(tex);
                    else if (!av.Assets.ContainsKey(g)) lack.Add(g);
                }
                foreach (string g in lack)
                {
                    if (!siblingTex.TryGetValue(g, out var l)) siblingTex[g] = l = new List<string>();
                    l.AddRange(have);
                }
            }
            var shared = new Dictionary<FbxMesh, int[]>(); // a mesh's accessors, written once
            foreach (var kv in rs)
            {
                int ni = kv.Key;
                var r = kv.Value;
                FbxMesh mesh;
                string guid;
                if (r.Mesh != null) { mesh = r.Mesh; guid = r.FbxGuid; }
                else
                {
                    object mref = r.Class == 137 ? r.Body.At("m_Mesh") : Comp(nodes[ni].G, 33)?.Body.At("m_Mesh");
                    guid = mref.S("guid");
                    long id = long.Parse(mref.S("fileID"));
                    mesh = MeshFor(guid, id, nodes[ni].Name, refsOf[guid]);
                }
                if (mesh == null) continue;
                if (!r.Enabled) disabledRenderers.Add(ni);
                try { AddRenderer(ni, r, mesh, Fbx(guid), shared); }
                catch (Exception e) { Notes.Add(nodes[ni].Name + ": " + e.Message); }
            }
            foreach (var kv in lostBones)
                Notes.Add(kv.Value + (kv.Value == 1 ? " mesh is" : " meshes are") + " skinned to bones the avatar doesn't have (" + kv.Key + "): held to the nearest ones it has");
        }

        // a node by a bone's name: the renderer's own bones first, then the whole tree
        int BoneNode(string name, List<int> rendererBones, GNode scope = null)
        {
            foreach (int b in rendererBones) if (b >= 0 && nodes[b].Name == name) return b;
            if (scope != null)
                for (int i = 0; i < nodes.Count; i++)
                    if (nodes[i].Name == name && nodes[i].G != null && nodes[i].G.Scope == scope) return i;
            for (int i = 0; i < nodes.Count; i++) if (nodes[i].Name == name) return i;
            foreach (int b in rendererBones) if (b >= 0 && Plain(nodes[b].Name) == Plain(name)) return b;
            return -1;
        }

        readonly Dictionary<string, int> lostBones = new Dictionary<string, int>();

        void AddRenderer(int ni, GComp r, FbxMesh mesh, FbxFile file, Dictionary<FbxMesh, int[]> shared)
        {
            var node = nodes[ni];
            // joints: the .fbx's bones on the prefab's (an unskinned mesh: its own object)
            var rBones = new List<int>();
            foreach (var b in r.Body.L("m_Bones")) rBones.Add(Index(r.File.Node(b)));
            int rootBone = Index(r.File.Node(r.Body.At("m_RootBone")));
            if (rootBone < 0) rootBone = ni;
            var scope = r.Node.Scope;
            var joints = new int[mesh.Bones.Length];
            var missing = new List<string>();
            for (int j = 0; j < joints.Length; j++)
            {
                if (!mesh.Skinned) { joints[j] = ni; continue; }
                joints[j] = BoneNode(mesh.Bones[j], rBones, scope);
                if (joints[j] >= 0) continue;
                missing.Add(mesh.Bones[j]);
                // a bone the avatar lost: its nearest parent in the .fbx that it has
                string up = mesh.Bones[j];
                for (int k = 0; k < 64 && joints[j] < 0 && file.Parents.TryGetValue(up, out up) && up != ""; k++) joints[j] = BoneNode(up, rBones, scope);
                if (joints[j] < 0) joints[j] = rootBone;
            }
            if (missing.Count > 0)
            {
                string k = string.Join(", ", missing.Take(3)) + (missing.Count > 3 ? "..." : "");
                lostBones[k] = lostBones.TryGetValue(k, out int c) ? c + 1 : 1;
            }

            if (!shared.TryGetValue(mesh, out int[] acc))
            {
                acc = new int[5 + mesh.Shapes.Count];
                acc[0] = Floats(mesh.Pos, 3, "VEC3", true);
                acc[1] = Floats(mesh.Nrm, 3, "VEC3", false);
                acc[2] = Floats(mesh.Uv, 2, "VEC2", false);
                acc[3] = UShorts(mesh.Joints, 4, "VEC4");
                acc[4] = Floats(mesh.Weights, 4, "VEC4", false);
                for (int s = 0; s < mesh.Shapes.Count; s++) acc[5 + s] = Floats(mesh.Shapes[s].Value, 3, "VEC3", true);
                shared[mesh] = acc;
                MeshCount++;
            }
            // materials: the renderer's, one a sub-mesh
            var mats = r.Body.L("m_Materials");
            var prims = new List<object>();
            for (int s = 0; s < mesh.Slots.Count; s++)
            {
                if (mesh.Slots[s].Value.Length == 0) continue;
                if (s >= mats.Count && mats.Count > 0) continue; // Unity draws no sub-mesh without a material
                var attrs = new Dictionary<string, object> { { "POSITION", acc[0] }, { "NORMAL", acc[1] }, { "TEXCOORD_0", acc[2] }, { "JOINTS_0", acc[3] }, { "WEIGHTS_0", acc[4] } };
                var prim = new Dictionary<string, object> { { "attributes", attrs }, { "indices", Indices(mesh, s) }, { "mode", 4 } };
                int mi = mats.Count > 0 ? Material(mats[s], mesh.Slots[s].Key) : Material(null, mesh.Slots[s].Key);
                if (mi >= 0) prim["material"] = mi;
                var targets = new List<object>();
                for (int k = 0; k < mesh.Shapes.Count; k++) targets.Add(new Dictionary<string, object> { { "POSITION", acc[5 + k] } });
                if (targets.Count > 0) prim["targets"] = targets;
                prims.Add(prim);
                Triangles += mesh.Slots[s].Value.Length / 3;
            }
            if (prims.Count == 0) return;
            var gm = new Dictionary<string, object> { { "name", node.Name }, { "primitives", prims } };
            if (mesh.Shapes.Count > 0)
            {
                gm["extras"] = new Dictionary<string, object> { { "targetNames", mesh.Shapes.Select(x => (object)x.Key).ToList() } };
                // its blend shapes as the prefab sets them (Unity: 0..100, in the mesh's order)
                var w = r.Body.L("m_BlendShapeWeights");
                var weights = new List<object>();
                var named = new Dictionary<string, float>();
                for (int k = 0; k < mesh.Shapes.Count; k++)
                {
                    float v = k < w.Count ? Num(w[k]) : 0f;
                    weights.Add(v / 100f);
                    if (v != 0f) named[mesh.Shapes[k].Key] = v;
                }
                gm["weights"] = weights;
                if (named.Count > 0) blendDefaults[ni] = named;
            }
            node.Mesh = gMeshes.Count;
            gMeshes.Add(gm);
            var ibms = new float[joints.Length * 16];
            for (int j = 0; j < joints.Length; j++) Array.Copy(mesh.Ibm[j], 0, ibms, j * 16, 16);
            node.Skin = gSkins.Count;
            gSkins.Add(new Dictionary<string, object>
            {
                { "joints", joints.Select(x => (object)x).ToList() },
                { "inverseBindMatrices", Floats(ibms, 16, "MAT4", false) },
                { "skeleton", rootBone },
            });
        }

        static float Num(object o) => float.TryParse(o as string, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;

        // ------------------------------------------------------------------ materials
        readonly Dictionary<string, string> fbxMaterialRemap = new Dictionary<string, string>();

        int Material(object mref, string fbxName)
        {
            string guid = mref.S("guid");
            string key = guid != "" ? guid + ":" + mref.S("fileID") : "fbx:" + fbxName;
            if (matOf.TryGetValue(key, out int have)) return have;
            int index = gMaterials.Count;
            matOf[key] = index;
            var g = new Dictionary<string, object> { { "name", fbxName }, { "extras", new Dictionary<string, object> { { "toon", true } } } };
            var pbr = new Dictionary<string, object> { { "metallicFactor", 0.0 }, { "roughnessFactor", 0.9 } };
            g["pbrMetallicRoughness"] = pbr;
            gMaterials.Add(g);
            PackageAsset a;
            if (guid == "" || !av.Assets.TryGetValue(guid, out a) || a.File == null || !a.Path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
            {
                // not in the package: a material of the same name in it, else a picture named for it
                a = ByName(fbxName, ".mat");
                if (a == null)
                {
                    var pic = ByName(fbxName, ".png") ?? ByName(fbxName, ".jpg") ?? ByName(fbxName, ".tga");
                    // ...unless the renderer's other materials use another picture of the same size: that one
                    if (pic != null && siblingTex.TryGetValue(guid, out var sib))
                    {
                        var size = PngSize(pic);
                        var alt = sib.Where(x => x != pic.Guid && av.Assets.ContainsKey(x) && size != null && PngSize(av.Assets[x]) == size)
                                     .GroupBy(x => x).OrderByDescending(x => x.Count()).Select(x => x.Key).FirstOrDefault();
                        if (alt != null) pic = av.Assets[alt];
                    }
                    if (pic != null)
                    {
                        int ti = Texture(pic.Guid, false);
                        if (ti >= 0) pbr["baseColorTexture"] = new Dictionary<string, object> { { "index", ti } };
                        Notes.Add("material " + fbxName + " isn't in the package: its picture found by name or by its mesh's other materials (" + Path.GetFileName(pic.Path) + ")");
                    }
                    else
                    {
                        pbr["baseColorFactor"] = new List<object> { 0.8, 0.8, 0.8, 1.0 };
                        Notes.Add("material " + fbxName + " isn't in the package: grey");
                    }
                    return index;
                }
                Notes.Add("material " + fbxName + " isn't in the package: " + Path.GetFileName(a.Path) + " taken by its name");
            }
            var docs = UYaml.Parse(File.ReadAllText(a.File));
            var mat = docs.FirstOrDefault(d => d.Type == "Material");
            if (mat == null) return index;
            g["name"] = mat.Body.S("m_Name");
            var props = mat.Body.At("m_SavedProperties");
            var floats = new Dictionary<string, float>();
            foreach (var e in props.L("m_Floats"))
                if (e is Dictionary<string, object> d) foreach (var kv in d) floats[kv.Key] = Num(kv.Value);
            object color = null, mainTex = null, emColor = null, emMap = null;
            foreach (var e in props.L("m_Colors"))
                if (e is Dictionary<string, object> d)
                {
                    if (d.ContainsKey("_Color")) color = d["_Color"];
                    if (d.ContainsKey("_EmissionColor")) emColor = d["_EmissionColor"];
                }
            foreach (var e in props.L("m_TexEnvs"))
                if (e is Dictionary<string, object> d)
                {
                    if (d.ContainsKey("_MainTex")) mainTex = d["_MainTex"];
                    if (d.ContainsKey("_EmissionMap")) emMap = d["_EmissionMap"];
                }
            // drawn with the sandbox's toon shader (a VRChat avatar's look)
            g["extras"] = new Dictionary<string, object> { { "toon", true } };
            // its glow: Poiyomi's emission switched on, or Standard's _EMISSION keyword
            bool glows = floats.TryGetValue("_EnableEmission", out float en) ? en > 0.5f : (mat.Body.S("m_ShaderKeywords").Contains("_EMISSION") || mat.Body.L("m_ValidKeywords").Any(k => k as string == "_EMISSION"));
            if (glows && emColor != null)
            {
                float strength = floats.TryGetValue("_EmissionStrength", out float es) ? es : 1f;
                double er = Math.Min(1.0, emColor.F("r") * strength), eg = Math.Min(1.0, emColor.F("g") * strength), eb = Math.Min(1.0, emColor.F("b") * strength);
                if (er + eg + eb > 0.01)
                {
                    g["emissiveFactor"] = new List<object> { er, eg, eb };
                    string eguid = emMap.At("m_Texture").S("guid");
                    int ei = eguid != "" ? Texture(eguid, false) : -1;
                    if (ei >= 0) g["emissiveTexture"] = new Dictionary<string, object> { { "index", ei } };
                    // Poiyomi's "base colour as map": the glow in the texture's own colours
                    if (floats.TryGetValue("_EmissionBaseColorAsMap", out float bm) && bm > 0.5f)
                        ((Dictionary<string, object>)g["extras"])["emissionTimesBase"] = true;
                }
            }
            if (color != null) pbr["baseColorFactor"] = new List<object> { (double)color.F("r", 1), (double)color.F("g", 1), (double)color.F("b", 1), 1.0 };
            // see-through: Poiyomi's and Standard's render mode, else the queue it draws in
            float mode = floats.TryGetValue("_Mode", out float md) ? md : 0f;
            int queue = mat.Body.I("m_CustomRenderQueue", -1);
            string tags = string.Join(" ", mat.Body.L("stringTagMap").Select(x => x as string ?? "")) + " " + (mat.Body.At("stringTagMap") as Dictionary<string, object> ?? new Dictionary<string, object>()).Values.Aggregate("", (s, v) => s + " " + v);
            bool cutout = mode == 1 || tags.Contains("TransparentCutout") || (queue >= 2450 && queue < 3000);
            bool blend = !cutout && (mode >= 2 || queue >= 3000 || tags.Contains("Transparent"));
            if (cutout || blend)
            {
                g["alphaMode"] = cutout ? "MASK" : "BLEND";
                if (cutout) g["alphaCutoff"] = (double)(floats.TryGetValue("_Cutoff", out float c) ? Math.Max(0.05f, Math.Min(0.95f, c)) : 0.5f);
            }
            // Culling: Off draws both faces; Front (which shows a model black, inside out) becomes Back, as the
            // avatars' own instructions say to set it
            if (floats.TryGetValue("_Cull", out float cull) && cull < 0.5f) g["doubleSided"] = true;
            string tguid = mainTex.At("m_Texture").S("guid");
            if (tguid != "")
            {
                int ti = Texture(tguid, cutout || blend);
                if (ti >= 0) pbr["baseColorTexture"] = new Dictionary<string, object> { { "index", ti } };
            }
            return index;
        }

        // the package's asset of that type named as `name` ("FrenniFazclaire.001" ~ "Frenni Fazclaire Texture.png"): its
        // letters and digits the same, give or take a trailing word for what it is
        static readonly string[] kPictureWords = { "texture", "tex", "albedo", "diffuse", "basecolor", "basecolour", "color", "colour", "maintex", "base", "d", "col" };
        PackageAsset ByName(string name, string ext)
        {
            string want = Plain(Regex.Replace(name ?? "", @"\.\d+$", ""));
            if (want.Length < 3) return null;
            PackageAsset best = null;
            int bestScore = 0;
            foreach (var x in av.Assets.Values)
            {
                if (x.File == null || !x.Path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) continue;
                string have = Plain(Path.GetFileNameWithoutExtension(x.Path));
                int score = 0;
                if (have == want) score = 3;
                else if (ext != ".mat")
                    foreach (string w in kPictureWords)
                        if (have == want + w) score = 2;
                if (score > bestScore || (score == bestScore && score > 0 && x.Path.Length < best.Path.Length)) { best = x; bestScore = score; }
            }
            return best;
        }

        readonly Dictionary<string, List<string>> siblingTex = new Dictionary<string, List<string>>();

        static string MainTexOf(PackageAsset mat)
        {
            try
            {
                var m = Regex.Match(File.ReadAllText(mat.File), @"- _MainTex:\s*\n\s*m_Texture: \{fileID: -?\d+, guid: ([0-9a-f]{32})");
                return m.Success ? m.Groups[1].Value : null;
            }
            catch (Exception) { return null; }
        }

        // a PNG's "width x height" (null: not a PNG)
        static string PngSize(PackageAsset a)
        {
            try
            {
                using (var f = File.OpenRead(a.File))
                {
                    var b = new byte[24];
                    if (f.Read(b, 0, 24) < 24 || b[0] != 0x89 || b[1] != (byte)'P') return null;
                    return ((b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19]) + "x" + ((b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23]);
                }
            }
            catch (Exception) { return null; }
        }

        int Texture(string guid, bool alpha)
        {
            if (texOf.TryGetValue(guid, out int have))
            {
                if (alpha && have >= 0) Textures[have].Alpha = true;
                return have;
            }
            texOf[guid] = -1;
            if (!av.Assets.TryGetValue(guid, out var a) || a.File == null) { Notes.Add("a texture isn't in the package"); return -1; }
            string ext = Path.GetExtension(a.Path).ToLowerInvariant();
            if (ext != ".png" && ext != ".jpg" && ext != ".jpeg" && ext != ".tga")
            {
                Notes.Add(Path.GetFileName(a.Path) + ": a " + ext + " texture isn't read (PNG, JPEG and TGA are)");
                return -1;
            }
            int i = Textures.Count;
            Textures.Add(new TexJob { Guid = guid, File = a.File, Ext = ext, Alpha = alpha });
            texOf[guid] = i;
            return i;
        }

        // ------------------------------------------------------------------ the humanoid
        static readonly Dictionary<string, string> kMixamo = new Dictionary<string, string>
        {
            { "Hips", "Hips" }, { "Spine", "Spine" }, { "Chest", "Spine1" }, { "UpperChest", "Spine2" }, { "Neck", "Neck" }, { "Head", "Head" },
            { "LeftShoulder", "LeftShoulder" }, { "LeftUpperArm", "LeftArm" }, { "LeftLowerArm", "LeftForeArm" }, { "LeftHand", "LeftHand" },
            { "RightShoulder", "RightShoulder" }, { "RightUpperArm", "RightArm" }, { "RightLowerArm", "RightForeArm" }, { "RightHand", "RightHand" },
            { "LeftUpperLeg", "LeftUpLeg" }, { "LeftLowerLeg", "LeftLeg" }, { "LeftFoot", "LeftFoot" }, { "LeftToes", "LeftToeBase" },
            { "RightUpperLeg", "RightUpLeg" }, { "RightLowerLeg", "RightLeg" }, { "RightFoot", "RightFoot" }, { "RightToes", "RightToeBase" },
        };

        // The model's Unity humanoid (the Animator's avatar: its .fbx's import settings) names the body's bones.
        void Humanoid()
        {
            // the avatar's Animator; else a model's under it
            var animator = Comp(av.Root, 95);
            if (animator == null || animator.Body.At("m_Avatar").S("guid") == "")
                foreach (var n in nodes)
                {
                    var a2 = Comp(n.G, 95);
                    if (a2 != null && a2.Body.At("m_Avatar").S("guid") != "") { animator = a2; break; }
                }
            string guid = animator?.Body.At("m_Avatar").S("guid") ?? "";
            // the import settings that map the most of these bones: the Animator's model's, else any model's
            // in the package (an Animator pointing at an avatar asset, or at none)
            var names = new HashSet<string>(nodes.Select(x => x.Name));
            MatchCollection pairs = null;
            int best = 0;
            var metas = new List<string>();
            if (guid != "" && av.Assets.TryGetValue(guid, out var own) && own.Meta != "") metas.Add(own.Meta);
            metas.AddRange(av.Assets.Values.Where(x => x.Meta.Contains("humanDescription:") && x.Meta.Contains("humanName:")).Select(x => x.Meta));
            foreach (string meta in metas)
            {
                var ps = Regex.Matches(meta, @"- boneName: (.*)\r?\n\s*humanName: (.*)");
                int hits = ps.Cast<Match>().Count(m => kMixamo.ContainsKey(Unquote(m.Groups[2].Value.Trim())) && names.Contains(Unquote(m.Groups[1].Value.Trim())));
                if (hits > best) { best = hits; pairs = ps; }
            }
            if (pairs == null)
            {
                Notes.Add("no humanoid mapping: bones told by their names");
                return;
            }
            var renamed = new HashSet<int>();
            foreach (Match m in pairs)
            {
                string bone = Unquote(m.Groups[1].Value.Trim()), human = Unquote(m.Groups[2].Value.Trim());
                if (!kMixamo.TryGetValue(human, out string to)) continue;
                int n = -1;
                for (int i = 0; i < nodes.Count && n < 0; i++) if (nodes[i].Name == bone && nodes[i].Mesh < 0) n = i;
                if (n < 0) continue;
                nodes[n].Name = to;
                renamed.Add(n);
            }
            // no other object keeps one of those names
            var taken = new HashSet<string>(renamed.Select(i => nodes[i].Name.ToLowerInvariant()));
            for (int i = 0; i < nodes.Count; i++)
                if (!renamed.Contains(i) && taken.Contains(nodes[i].Name.ToLowerInvariant())) nodes[i].Name += "_";
            if (renamed.Count < 10) Notes.Add("the humanoid mapping named only " + renamed.Count + " bones");
        }

        static string Unquote(string s) => s.Length >= 2 && (s[0] == '\'' || s[0] == '"') && s[s.Length - 1] == s[0] ? s.Substring(1, s.Length - 2).Replace("''", "'") : s;

        // ------------------------------------------------------------------ PhysBones -> VRM spring bones
        void PhysBones()
        {
            int hips = nodes.FindIndex(x => x.Name == "Hips");
            var colliders = new List<object>();
            var colliderIndex = new Dictionary<GComp, int>();
            var springs = new List<object>();
            var groups = new List<object>();
            foreach (var n in nodes.ToList())
            {
                if (n.G == null) continue;
                foreach (var mb in n.G.Comps.Where(c => c.Class == 114).ToList())
                {
                    var b = mb.Body;
                    if (!b.ContainsKey("shapeType") || !b.ContainsKey("insideBounds")) continue;
                    int at = Index(mb.File.Node(b.At("rootTransform")));
                    if (at < 0) at = nodes.IndexOf(n);
                    int shape = b.I("shapeType");
                    if (shape == 2) continue; // a plane
                    float rad = b.F("radius", 0.05f), h = b.F("height", 0f);
                    float[] p = V3(b, "position");
                    var off = new List<object> { (double)-p[0], (double)p[1], (double)p[2] };
                    object sh;
                    if (shape == 1 && h > rad * 2f)
                    {
                        var q = b.At("rotation");
                        float[] rot = { q.F("x"), -q.F("y"), -q.F("z"), q.F("w", 1f) };
                        var m = M4.Trs(new[] { -p[0], p[1], p[2] }, rot, new float[] { 1, 1, 1 });
                        float half = h / 2f - rad;
                        var a0 = M4.Point(m, 0, -half, 0);
                        var a1 = M4.Point(m, 0, half, 0);
                        sh = new Dictionary<string, object> { { "capsule", new Dictionary<string, object> {
                            { "offset", new List<object> { (double)a0[0], (double)a0[1], (double)a0[2] } }, { "radius", (double)rad },
                            { "tail", new List<object> { (double)a1[0], (double)a1[1], (double)a1[2] } } } } };
                    }
                    else sh = new Dictionary<string, object> { { "sphere", new Dictionary<string, object> { { "offset", off }, { "radius", (double)rad } } } };
                    colliderIndex[mb] = colliders.Count;
                    colliders.Add(new Dictionary<string, object> { { "node", at }, { "shape", sh } });
                }
            }
            foreach (var n in nodes.ToList())
            {
                if (n.G == null) continue;
                foreach (var mb in n.G.Comps.Where(c => c.Class == 114).ToList())
                {
                    var b = mb.Body;
                    if (!b.ContainsKey("pull") || !b.ContainsKey("rootTransform") || !mb.Enabled) continue;
                    int root = Index(mb.File.Node(b.At("rootTransform")));
                    if (root < 0) root = nodes.IndexOf(n);
                    var ignore = new HashSet<int>();
                    foreach (var x in b.L("ignoreTransforms")) { int ig = Index(mb.File.Node(x)); if (ig >= 0) ignore.Add(ig); }
                    // VRChat's pull / spring (momentum) / stiffness / gravity as VRM's stiffness, drag, gravity
                    float pull = b.F("pull", 0.2f), spring = b.F("spring", 0.2f), stiff = b.F("stiffness", 0.2f), grav = b.F("gravity", 0f);
                    double vStiff = 0.3 + pull * 2.5 + stiff * 1.0, vDrag = Math.Max(0.1, Math.Min(0.95, 1.0 - spring * 0.7)), vGrav = grav * 1.5;
                    // its limit (angle, hinge, polar); none: 75 degrees, so nothing folds through the body
                    int limitType = b.I("limitType");
                    double maxAngle = limitType == 0 ? 75.0 : limitType == 3 ? Math.Max(b.F("maxAngleX", 45f), b.F("maxAngleZ", 45f)) : b.F("maxAngleX", 45f);
                    if (maxAngle <= 0.5) maxAngle = 75.0;
                    double radius = b.F("radius", 0f);
                    var mine = new List<object>();
                    foreach (var c in b.L("colliders"))
                    {
                        var cc = mb.File.Component(c);
                        if (cc != null && colliderIndex.TryGetValue(cc, out int ci)) mine.Add(ci);
                    }
                    int group = -1;
                    if (mine.Count > 0) { group = groups.Count; groups.Add(new Dictionary<string, object> { { "colliders", mine } }); }
                    float[] endpoint = V3(b, "endpointPosition");
                    // every chain from the root to a leaf
                    var chains = new List<List<int>>();
                    Chains(root, new List<int>(), ignore, chains);
                    foreach (var chain in chains)
                    {
                        // a tip past the last bone, so it swings too
                        if (chain[chain.Count - 1] < 0)
                        {
                            chain.RemoveAt(chain.Count - 1);
                            chain.Add(EndNode(chain[chain.Count - 1], endpoint));
                        }
                        if (chain.Count < 2) continue;
                        var js = new List<object>();
                        foreach (int j in chain)
                            js.Add(new Dictionary<string, object> { { "node", j }, { "stiffness", vStiff }, { "dragForce", vDrag }, { "gravityPower", vGrav },
                                                                    { "gravityDir", new List<object> { 0.0, -1.0, 0.0 } }, { "hitRadius", radius }, { "maxAngle", maxAngle } });
                        var sp = new Dictionary<string, object> { { "name", nodes[root].Name }, { "joints", js } };

                        if (group >= 0) sp["colliderGroups"] = new List<object> { group };
                        springs.Add(sp);
                    }
                }
            }
            if (springs.Count == 0) return;
            Springs = springs.Count;
            springExt = new Dictionary<string, object> { { "specVersion", "1.0" }, { "colliders", colliders }, { "colliderGroups", groups }, { "springs", springs } };
        }

        // The chains under a PhysBone's root: straight runs of bones, each ending at a leaf (then a tip is added past
        // it) or at a fork (the fork's bone is its tip and doesn't swing itself: it can't point at two ways at once,
        // as VRChat's "multi child: ignore"). A trailing -1: add a tip.
        void Chains(int at, List<int> path, HashSet<int> ignore, List<List<int>> into)
        {
            if (ignore.Contains(at) || path.Count > 64) return;
            var kids = nodes[at].Kids.Where(k => !ignore.Contains(k) && nodes[k].Mesh < 0).ToList();
            if (kids.Count == 1) { path.Add(at); Chains(kids[0], path, ignore, into); path.RemoveAt(path.Count - 1); return; }
            if (kids.Count == 0) { var c = new List<int>(path) { at, -1 }; into.Add(c); return; }
            if (path.Count > 0) into.Add(new List<int>(path) { at });
            foreach (int k in kids) Chains(k, new List<int>(), ignore, into);
        }

        int EndNode(int leaf, float[] endpoint)
        {
            var n = nodes[leaf];
            float[] t;
            if (endpoint[0] != 0 || endpoint[1] != 0 || endpoint[2] != 0) t = new[] { -endpoint[0], endpoint[1], endpoint[2] };
            else
            {
                // along the bone, as far again as its parent is from it (in its own frame)
                float[] inv = M4.Invert(M4.Trs(new float[] { 0, 0, 0 }, n.R, n.S));
                t = M4.Point(inv, n.T[0] * 0.6f, n.T[1] * 0.6f, n.T[2] * 0.6f);
            }
            var e = new Node { Name = n.Name + "_end", T = t, R = new float[] { 0, 0, 0, 1 }, S = new float[] { 1, 1, 1 }, Parent = leaf, Path = n.Path + "/_end", Active = true };
            e.World = M4.Mul(n.World, M4.Trs(e.T, e.R, e.S));
            nodes.Add(e);
            n.Kids.Add(nodes.Count - 1);
            return nodes.Count - 1;
        }

        // ------------------------------------------------------------------ the .glb's data
        void Align() { while (bin.Length % 4 != 0) bin.WriteByte(0); }

        int View(byte[] data)
        {
            Align();
            int at = (int)bin.Length;
            bin.Write(data, 0, data.Length);
            gViews.Add(new Dictionary<string, object> { { "buffer", 0 }, { "byteOffset", at }, { "byteLength", data.Length } });
            return gViews.Count - 1;
        }

        int Floats(float[] a, int comps, string type, bool minMax)
        {
            var bytes = new byte[a.Length * 4];
            Buffer.BlockCopy(a, 0, bytes, 0, bytes.Length);
            var acc = new Dictionary<string, object> { { "bufferView", View(bytes) }, { "componentType", 5126 }, { "count", a.Length / comps }, { "type", type } };
            if (minMax && comps == 3)
            {
                var mn = new double[] { double.MaxValue, double.MaxValue, double.MaxValue };
                var mx = new double[] { double.MinValue, double.MinValue, double.MinValue };
                for (int i = 0; i < a.Length; i++) { mn[i % 3] = Math.Min(mn[i % 3], a[i]); mx[i % 3] = Math.Max(mx[i % 3], a[i]); }
                if (a.Length == 0) mn = mx = new double[3];
                acc["min"] = mn.Select(x => (object)x).ToList();
                acc["max"] = mx.Select(x => (object)x).ToList();
            }
            gAccessors.Add(acc);
            return gAccessors.Count - 1;
        }

        int UShorts(int[] a, int comps, string type)
        {
            var bytes = new byte[a.Length * 2];
            for (int i = 0; i < a.Length; i++) { bytes[i * 2] = (byte)(a[i] & 255); bytes[i * 2 + 1] = (byte)((a[i] >> 8) & 255); }
            gAccessors.Add(new Dictionary<string, object> { { "bufferView", View(bytes) }, { "componentType", 5123 }, { "count", a.Length / comps }, { "type", type } });
            return gAccessors.Count - 1;
        }

        int Indices(FbxMesh mesh, int slot)
        {
            int[] idx = mesh.Slots[slot].Value;
            var bytes = new byte[idx.Length * 4];
            Buffer.BlockCopy(idx, 0, bytes, 0, bytes.Length);
            gAccessors.Add(new Dictionary<string, object> { { "bufferView", View(bytes) }, { "componentType", 5125 }, { "count", idx.Length }, { "type", "SCALAR" } });
            return gAccessors.Count - 1;
        }

        static List<object> L(float[] a) => a.Select(x => (object)(double)x).ToList();

        /// The .glb (the textures in Textures encoded by now).
        public byte[] Glb()
        {
            foreach (var t in Textures)
            {
                int image = -1;
                if (t.Out != null)
                {
                    image = gImages.Count;
                    gImages.Add(new Dictionary<string, object> { { "bufferView", View(t.Out) }, { "mimeType", t.Mime } });
                }
                gTextures.Add(image >= 0 ? new Dictionary<string, object> { { "source", image } } : new Dictionary<string, object>());
            }
            // a material whose picture didn't make it: no texture
            foreach (Dictionary<string, object> m in gMaterials)
            {
                var pbr = m["pbrMetallicRoughness"] as Dictionary<string, object>;
                if (pbr.TryGetValue("baseColorTexture", out object bt) && !(gTextures[(int)Json0(bt)] as Dictionary<string, object>).ContainsKey("source")) pbr.Remove("baseColorTexture");
                if (m.TryGetValue("emissiveTexture", out object et) && !(gTextures[(int)Json0(et)] as Dictionary<string, object>).ContainsKey("source")) m.Remove("emissiveTexture");
            }
            var gNodes = new List<object>();
            foreach (var n in nodes)
            {
                var g = new Dictionary<string, object> { { "name", n.Name } };
                if (n.T[0] != 0 || n.T[1] != 0 || n.T[2] != 0) g["translation"] = L(n.T);
                if (n.R[0] != 0 || n.R[1] != 0 || n.R[2] != 0 || n.R[3] != 1) g["rotation"] = L(n.R);
                if (n.S[0] != 1 || n.S[1] != 1 || n.S[2] != 1) g["scale"] = L(n.S);
                if (n.Kids.Count > 0) g["children"] = n.Kids.Select(k => (object)k).ToList();
                if (n.Mesh >= 0) { g["mesh"] = n.Mesh; g["skin"] = n.Skin; }
                gNodes.Add(g);
            }
            var root = new Dictionary<string, object>
            {
                { "asset", new Dictionary<string, object> { { "version", "2.0" }, { "generator", "Delirium Sandbox VRChat import" } } },
                { "scene", 0 },
                { "scenes", new List<object> { new Dictionary<string, object> { { "nodes", new List<object> { 0 } } } } },
                { "nodes", gNodes },
                { "meshes", gMeshes }, { "skins", gSkins }, { "materials", gMaterials },
                { "textures", gTextures }, { "images", gImages }, { "accessors", gAccessors }, { "bufferViews", gViews },
                { "samplers", new List<object>() },
            };
            if (springExt != null)
            {
                root["extensionsUsed"] = new List<object> { "VRMC_springBone" };
                root["extensions"] = new Dictionary<string, object> { { "VRMC_springBone", springExt } };
            }
            Align();
            root["buffers"] = new List<object> { new Dictionary<string, object> { { "byteLength", (int)bin.Length } } };
            byte[] json = Encoding.UTF8.GetBytes(JsonText.Write(root));
            int jl = (json.Length + 3) / 4 * 4;
            var o = new MemoryStream();
            var w = new BinaryWriter(o);
            w.Write(0x46546C67u); w.Write(2u); w.Write((uint)(12 + 8 + jl + 8 + bin.Length));
            w.Write((uint)jl); w.Write(0x4E4F534Au); w.Write(json); for (int i = json.Length; i < jl; i++) w.Write((byte)0x20);
            w.Write((uint)bin.Length); w.Write(0x004E4942u); w.Write(bin.ToArray());
            return o.ToArray();
        }

        static object Json0(object bt) => (bt as Dictionary<string, object>)["index"];

        static readonly HashSet<string> kTuckWords = new HashSet<string> { "on", "off", "hide", "hidden", "shrink", "fix", "tuck", "under", "clothes", "clothing" };

        static HashSet<string> Words(string s)
        {
            var r = new HashSet<string>();
            foreach (Match m in Regex.Matches(s ?? "", @"[A-Za-z]+|\d+")) r.Add(m.Value.ToLowerInvariant());
            return r;
        }

        /// The toggles for VrcLook: what each menu item does, to objects by their node index in the .glb.
        public string Sidecar()
        {
            var targets = new List<object>();
            var targetOf = new Dictionary<int, int>();
            Func<int, int> T = n => { if (!targetOf.TryGetValue(n, out int t)) { t = targets.Count; targetOf[n] = t; targets.Add(n); } return t; };
            int lost = 0, swaps = 0;
            var lostPaths = new List<string>();
            Func<IEnumerable<Effect>, List<object>> effects = list =>
            {
                var r = new List<object>();
                foreach (var e in list)
                {
                    if (e.Attr.StartsWith("mat:")) { swaps++; continue; }
                    if (e.Attr.StartsWith("enabled:") && e.Attr != "enabled:137" && e.Attr != "enabled:23") continue;
                    if (!nodeOfPath.TryGetValue(e.Path, out int n)) { if (lost++ < 3) lostPaths.Add(e.Path); continue; }
                    r.Add(new List<object> { T(n), e.Attr.StartsWith("enabled:") ? "enabled" : e.Attr, (double)e.Value });
                }
                return r;
            };
            var start = new List<object>();
            for (int i = 0; i < nodes.Count; i++) if (!nodes[i].Active) start.Add(new List<object> { T(i), "active", 0.0 });
            foreach (int i in disabledRenderers) start.Add(new List<object> { T(i), "enabled", 0.0 });
            foreach (var kv in blendDefaults) foreach (var b in kv.Value) start.Add(new List<object> { T(kv.Key), "blend:" + b.Key, (double)b.Value });
            // every mesh, to show or hide by hand (outfits an edit left without toggles)
            var parts = new List<object>();
            for (int i = 0; i < nodes.Count; i++)
            {
                if (nodes[i].Mesh < 0) continue;
                // the blend shapes that tuck the body in under this piece ("DEFAULT Pants ON" for "Frenni - DEFAULT -
                // Pants"): let out when it's hidden
                var mine = Words(nodes[i].Name);
                var tucks = new List<object>();
                foreach (var kv in blendDefaults)
                    if (kv.Key != i)
                        foreach (var b in kv.Value)
                        {
                            var w = Words(b.Key);
                            w.ExceptWith(kTuckWords);
                            if (w.Count > 0 && w.IsSubsetOf(mine)) tucks.Add(new List<object> { T(kv.Key), "blend:" + b.Key, 0.0 });
                        }
                parts.Add(new List<object> { T(i), nodes[i].Path, tucks });
            }
            var baseFx = effects(av.Base.Values);
            var fx = new Dictionary<string, object>();
            foreach (var kv in av.ValueEffects) fx[kv.Key] = effects(kv.Value);
            if (lost > 0) Notes.Add(lost + " animated things aren't in the avatar's tree (" + string.Join(", ", lostPaths) + ")");
            if (swaps > 0) Notes.Add(swaps + " material swaps aren't done (only objects and blend shapes)");
            var toggles = av.Toggles.Select(t => (object)new Dictionary<string, object>
            {
                { "menu", t.Menu }, { "name", t.Name }, { "param", t.Param }, { "value", (double)t.Value }, { "button", t.Kind == 101 }, { "radial", t.Kind == 203 },
            }).ToList();
            var toggleIndex = new Dictionary<Toggle, int>();
            for (int i = 0; i < av.Toggles.Count; i++) toggleIndex[av.Toggles[i]] = i;
            var root = new Dictionary<string, object>
            {
                { "avatar", av.Name }, { "targets", targets }, { "start", start }, { "base", baseFx }, { "effects", fx }, { "toggles", toggles }, { "parts", parts },
                { "menu", MenuJson(av.MenuRoot, toggleIndex) },
                { "icons", Icons.Select(x => (object)(x.Out != null ? Convert.ToBase64String(x.Out) : "")).ToList() },
                { "defaults", av.Defaults.ToDictionary(kv => kv.Key, kv => (object)(double)kv.Value) },
                { "notes", av.Notes.Concat(Notes).Select(x => (object)x).ToList() },
            };
            return JsonText.Write(root);
        }
    }

    /// JSON out (maps, lists, strings, numbers, true/false).
    public static class JsonText
    {
        public static string Write(object o)
        {
            var b = new StringBuilder();
            W(b, o);
            return b.ToString();
        }

        static void W(StringBuilder b, object o)
        {
            if (o == null) { b.Append("null"); return; }
            if (o is string s) { Str(b, s); return; }
            if (o is bool f) { b.Append(f ? "true" : "false"); return; }
            if (o is int i) { b.Append(i.ToString(CultureInfo.InvariantCulture)); return; }
            if (o is long l) { b.Append(l.ToString(CultureInfo.InvariantCulture)); return; }
            if (o is double d) { b.Append(double.IsNaN(d) || double.IsInfinity(d) ? "0" : d.ToString("R", CultureInfo.InvariantCulture)); return; }
            if (o is float fl) { W(b, (double)fl); return; }
            if (o is Dictionary<string, object> m)
            {
                b.Append('{');
                bool first = true;
                foreach (var kv in m)
                {
                    if (!first) b.Append(',');
                    first = false;
                    Str(b, kv.Key);
                    b.Append(':');
                    W(b, kv.Value);
                }
                b.Append('}');
                return;
            }
            if (o is System.Collections.IEnumerable e)
            {
                b.Append('[');
                bool first = true;
                foreach (var x in e)
                {
                    if (!first) b.Append(',');
                    first = false;
                    W(b, x);
                }
                b.Append(']');
                return;
            }
            Str(b, o.ToString());
        }

        static void Str(StringBuilder b, string s)
        {
            b.Append('"');
            foreach (char c in s)
            {
                if (c == '"') b.Append("\\\"");
                else if (c == '\\') b.Append("\\\\");
                else if (c < 0x20) b.Append("\\u" + ((int)c).ToString("x4"));
                else b.Append(c);
            }
            b.Append('"');
        }
    }
}
