// A Blender file's character as a .glb the sandbox loads (and a sidecar for the Expr. wheel): its meshes as they
// sit in the file (modifiers other than the armature not applied), skinned to its armature's bones by their vertex
// groups; its shape keys as blend shapes (radials on the wheel); its materials' base colour texture (from the
// Principled BSDF, packed in the file or next to it); each mesh a part to show or hide (those hidden in the file
// start hidden). Blender's Z up made glTF's Y up.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Vrc
{
    public sealed class BlendModel
    {
        public readonly List<string> Notes = new List<string>();
        public readonly List<TexJob> Textures = new List<TexJob>();
        public int Triangles, Meshes, Shapes;
        public string Name = "model";

        readonly BlendFile f;
        readonly string dir;          // where its textures may be (the .blend's folder, a .zip's)

        sealed class Node
        {
            public string Name;
            public float[] World;     // glTF frame
            public int Parent = -1;
            public readonly List<int> Kids = new List<int>();
            public int Mesh = -1, Skin = -1;
            public bool Hidden;
        }
        readonly List<Node> nodes = new List<Node>();

        // the .glb
        readonly List<object> gMeshes = new List<object>(), gSkins = new List<object>(), gMaterials = new List<object>(), gAccessors = new List<object>(),
                              gViews = new List<object>(), gTextures = new List<object>(), gImages = new List<object>();
        readonly MemoryStream bin = new MemoryStream();
        readonly Dictionary<ulong, int> matOf = new Dictionary<ulong, int>();
        readonly Dictionary<string, int> texOf = new Dictionary<string, int>();
        // the wheel
        readonly List<object[]> shapeToggles = new List<object[]>();   // node, shape name, default 0..1, mesh name
        readonly List<object[]> partToggles = new List<object[]>();    // node, name, shown

        BlendModel(BlendFile f, string dir) { this.f = f; this.dir = dir; }

        public static BlendModel Build(string path, string textureDir, Action<string> status)
        {
            var f = BlendFile.Load(path);
            var m = new BlendModel(f, textureDir ?? Path.GetDirectoryName(path)) { Name = Path.GetFileNameWithoutExtension(path) };
            if (f.Version < 250) throw new Exception("a Blender " + f.Version / 100 + "." + f.Version % 100 + " file: from 2.5 on are read");
            if (status != null) status("reading its objects");
            m.Read();
            return m;
        }

        // ------------------------------------------------------------------ maths (Blender's frame, column-major)
        static readonly float[] C = { 1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1 };  // Blender -> glTF: (x, y, z) -> (x, z, -y)
        static readonly float[] Ci = M4.Invert(C);
        static float[] Gl(float[] m) => M4.Mul(M4.Mul(C, m), Ci);

        static float[] Euler(float x, float y, float z, int mode)
        {
            float[] rx = { 1, 0, 0, 0, 0, (float)Math.Cos(x), (float)Math.Sin(x), 0, 0, -(float)Math.Sin(x), (float)Math.Cos(x), 0, 0, 0, 0, 1 };
            float[] ry = { (float)Math.Cos(y), 0, -(float)Math.Sin(y), 0, 0, 1, 0, 0, (float)Math.Sin(y), 0, (float)Math.Cos(y), 0, 0, 0, 0, 1 };
            float[] rz = { (float)Math.Cos(z), (float)Math.Sin(z), 0, 0, -(float)Math.Sin(z), (float)Math.Cos(z), 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
            // Blender's orders: 1 XYZ (X first), 2 XZY, 3 YXZ, 4 YZX, 5 ZXY, 6 ZYX
            switch (mode)
            {
                case 2: return M4.Mul(ry, M4.Mul(rz, rx));
                case 3: return M4.Mul(rz, M4.Mul(rx, ry));
                case 4: return M4.Mul(rx, M4.Mul(rz, ry));
                case 5: return M4.Mul(ry, M4.Mul(rx, rz));
                case 6: return M4.Mul(rx, M4.Mul(ry, rz));
                default: return M4.Mul(rz, M4.Mul(ry, rx));
            }
        }

        static float[] Quat(float w, float x, float y, float z) => M4.Trs(new float[] { 0, 0, 0 }, new[] { x, y, z, w }, new float[] { 1, 1, 1 });

        static float[] Mat(BRef r, string field)
        {
            var a = r.Floats(field);
            return a.Length == 16 ? a : M4.Identity();
        }

        float[] Local(BRef ob)
        {
            var loc = ob.Floats("loc");
            var dloc = ob.Floats("dloc");
            var size = ob.Floats("size");
            var dscale = ob.Has("dscale") ? ob.Floats("dscale") : new float[] { 1, 1, 1 };
            int mode = ob.In("rotmode", 0, 1);
            float[] rot;
            if (mode == 0)
            {
                var q = ob.Floats("quat");
                rot = q.Length == 4 ? Quat(q[0], q[1], q[2], q[3]) : M4.Identity();
            }
            else if (mode == -1)
            {
                var ax = ob.Floats("rotAxis");
                float ang = ob.Fl("rotAngle"), s = (float)Math.Sin(ang / 2);
                float l = (float)Math.Sqrt(ax[0] * ax[0] + ax[1] * ax[1] + ax[2] * ax[2]);
                rot = l > 1e-6f ? Quat((float)Math.Cos(ang / 2), ax[0] / l * s, ax[1] / l * s, ax[2] / l * s) : M4.Identity();
            }
            else
            {
                var e = ob.Floats("rot");
                rot = Euler(e[0], e[1], e[2], mode);
            }
            var t = new[] { loc[0] + (dloc.Length > 0 ? dloc[0] : 0), loc[1] + (dloc.Length > 1 ? dloc[1] : 0), loc[2] + (dloc.Length > 2 ? dloc[2] : 0) };
            var m = M4.Mul(M4.Trs(t, new float[] { 0, 0, 0, 1 }, new float[] { 1, 1, 1 }), rot);
            return M4.Mul(m, M4.Trs(new float[] { 0, 0, 0 }, new float[] { 0, 0, 0, 1 },
                new[] { size[0] * dscale[0], size[1] * dscale[1], size[2] * dscale[2] }));
        }

        // An object's matrix in the world (Blender's frame), its rest pose: parents, parent bones.
        readonly Dictionary<ulong, float[]> worldOf = new Dictionary<ulong, float[]>();
        float[] World(BRef ob, int depth = 0)
        {
            ulong key = (ulong)ob.At;
            if (worldOf.TryGetValue(key, out var w)) return w;
            var local = Local(ob);
            var par = ob.Go("parent");
            if (par.Ok && depth < 32)
            {
                var pw = World(par, depth + 1);
                if (ob.In("partype") == 7) // a bone's tip
                {
                    var bone = BoneOf(par, ob.Str("parsubstr"));
                    if (bone.Ok) pw = M4.Mul(pw, M4.Mul(Mat(bone, "arm_mat"), M4.Trs(new[] { 0, bone.Fl("length"), 0 }, new float[] { 0, 0, 0, 1 }, new float[] { 1, 1, 1 })));
                }
                w = M4.Mul(M4.Mul(pw, Mat(ob, "parentinv")), local);
            }
            else w = local;
            worldOf[key] = w;
            return w;
        }

        BRef BoneOf(BRef armOb, string name)
        {
            var arm = armOb.Go("data");
            foreach (var b in Bones(arm)) if (b.Str("name") == name) return b;
            return default(BRef);
        }

        IEnumerable<BRef> Bones(BRef arm)
        {
            var stack = new Stack<BRef>(arm.List("bonebase", "Bone").Reverse());
            while (stack.Count > 0)
            {
                var b = stack.Pop();
                yield return b;
                foreach (var c in b.List("childbase", "Bone").Reverse()) stack.Push(c);
            }
        }

        // ------------------------------------------------------------------ the scene
        void Read()
        {
            // the objects the first scene's view layer has (an excluded collection's aren't); shown or not
            var objects = new List<KeyValuePair<BRef, bool>>();
            var seen = new HashSet<long>();
            foreach (var sc in f.Ids("SC"))
            {
                foreach (var vl in sc.List("view_layers"))
                {
                    foreach (var b in vl.List("object_bases"))
                    {
                        var ob = b.Go("object");
                        int fl = b.In("flag");
                        if (ob.Ok && seen.Add(ob.At)) objects.Add(new KeyValuePair<BRef, bool>(ob, (fl & 2) != 0 && (fl & 128) != 0));
                    }
                    break;
                }
                if (objects.Count == 0)
                    foreach (var b in sc.List("base")) // before 2.8
                    {
                        var ob = b.Go("object");
                        if (ob.Ok && seen.Add(ob.At)) objects.Add(new KeyValuePair<BRef, bool>(ob, true));
                    }
                break;
            }
            if (objects.Count == 0)
                foreach (var ob in f.Ids("OB")) objects.Add(new KeyValuePair<BRef, bool>(ob, true));
            objects.RemoveAll(o => o.Key.IdName().StartsWith("WGT", StringComparison.OrdinalIgnoreCase)); // Rigify's bone shapes

            nodes.Add(new Node { Name = Name, World = M4.Identity() });
            // armatures and their bones
            var boneNode = new Dictionary<string, Dictionary<string, int>>(); // armature object -> bone name -> node
            var obNode = new Dictionary<long, int>();
            foreach (var o in objects.Where(x => x.Key.In("type") == 25 && (x.Value || objects.Any(m => m.Value && m.Key.Go("parent").At == x.Key.At))))
            {
                var ob = o.Key;
                var w = World(ob);
                int an = Add(ob.IdName(), Gl(w), 0);
                obNode[ob.At] = an;
                var map = new Dictionary<string, int>();
                boneNode[ob.IdName()] = map;
                var nodeOfBone = new Dictionary<long, int>();
                // only the bones that deform (and those something's parented to): Rigify's hundreds of controls left
                // out. Each under the nearest one of those above it; a Rigify DEF- bone under the one its ORG- twin's
                // parents lead to (DEF-thigh.L sits under the controls, ORG-thigh.L under ORG-spine: so DEF-spine).
                var all = Bones(ob.Go("data")).ToList();
                var byName = new Dictionary<string, BRef>();
                foreach (var b in all) if (!byName.ContainsKey(b.Str("name"))) byName[b.Str("name")] = b;
                var held = new HashSet<string>(objects.Where(x => x.Key.Go("parent").At == ob.At && x.Key.In("partype") == 7).Select(x => x.Key.Str("parsubstr")));
                var keep = new HashSet<string>(all.Where(b => (b.In("flag") & (1 << 12)) == 0 || held.Contains(b.Str("name"))).Select(b => b.Str("name")));
                Func<BRef, BRef, string> nearestKept = (b, walk) =>
                {
                    string nm = b.Str("name");
                    for (int guard = 0; walk.Ok && guard < 256; guard++, walk = walk.Go("parent"))
                    {
                        string wn = walk.Str("name");
                        if (keep.Contains(wn) && wn != nm) return wn;
                        if (wn.StartsWith("ORG-") && keep.Contains("DEF-" + wn.Substring(4)) && "DEF-" + wn.Substring(4) != nm) return "DEF-" + wn.Substring(4);
                    }
                    return null;
                };
                Func<BRef, string> keptAbove = b =>
                {
                    string nm = b.Str("name");
                    string r = nearestKept(b, b.Go("parent"));
                    if (r == null && nm.StartsWith("DEF-") && byName.TryGetValue("ORG-" + nm.Substring(4), out var org)) r = nearestKept(b, org.Go("parent"));
                    if (r != null) return r;
                    // none: the kept bone whose tail its head sits on (a chain Rigify drives by constraints)
                    var head = b.Floats("arm_head");
                    float best = 0.02f * Math.Max(0.05f, b.Fl("length"));
                    foreach (var c in all)
                    {
                        string cn = c.Str("name");
                        if (cn == nm || !keep.Contains(cn)) continue;
                        var t = c.Floats("arm_tail");
                        if (t.Length < 3 || head.Length < 3) continue;
                        float d = (float)Math.Sqrt((t[0] - head[0]) * (t[0] - head[0]) + (t[1] - head[1]) * (t[1] - head[1]) + (t[2] - head[2]) * (t[2] - head[2]));
                        if (d < best) { best = d; r = cn; }
                    }
                    return r;
                };
                var parentOf = new Dictionary<string, string>();
                foreach (var b in all) if (keep.Contains(b.Str("name"))) parentOf[b.Str("name")] = keptAbove(b);
                // no loops
                foreach (var nm in parentOf.Keys.ToList())
                {
                    var seenUp = new HashSet<string> { nm };
                    for (string u = parentOf[nm]; u != null; u = parentOf.TryGetValue(u, out var uu) ? uu : null)
                        if (!seenUp.Add(u)) { parentOf[nm] = null; break; }
                }
                // parents before children (and no loops)
                var made = new HashSet<string>();
                Action<string, int> make = null;
                make = (nm, depth) =>
                {
                    if (made.Contains(nm) || depth > 256) return;
                    string pn = parentOf[nm];
                    if (pn != null && !made.Contains(pn)) make(pn, depth + 1);
                    if (made.Contains(nm)) return;
                    made.Add(nm);
                    int parent = pn != null && map.TryGetValue(pn, out int pi) ? pi : an;
                    map[nm] = Add(nm, Gl(M4.Mul(w, Mat(byName[nm], "arm_mat"))), parent);
                };
                foreach (var nm in parentOf.Keys.ToList()) make(nm, 0);
            }
            if (boneNode.Count == 0) Notes.Add("no armature: a statue");
            // meshes
            foreach (var o in objects.Where(x => x.Key.In("type") == 1))
            {
                try { AddMesh(o.Key, !o.Value, boneNode, obNode); }
                catch (Exception e) { Notes.Add(o.Key.IdName() + ": " + e.Message); }
            }
            if (Meshes == 0) throw new Exception("no meshes in it");
        }

        int Add(string name, float[] world, int parent)
        {
            var n = new Node { Name = name, World = world, Parent = parent };
            nodes.Add(n);
            nodes[parent].Kids.Add(nodes.Count - 1);
            return nodes.Count - 1;
        }

        // ------------------------------------------------------------------ a mesh's data, any version
        sealed class Attr
        {
            public byte[] Data;
            public int Domain = -1;     // 0 point, 1 edge, 2 face, 3 corner
            public int Type = -1;       // CustomData type (before 5.0)
        }

        // every named attribute: Blender 5's attribute storage, else CustomData layers
        static Dictionary<string, Attr> Attributes(BRef me, out List<KeyValuePair<string, Attr>> uvs)
        {
            var r = new Dictionary<string, Attr>();
            uvs = new List<KeyValuePair<string, Attr>>();
            var st = me.Sub("attribute_storage");
            if (st.Ok && st.In("dna_attributes_num") > 0)
            {
                var arr = st.Go("dna_attributes");
                int n = st.In("dna_attributes_num");
                var attrT = me.F.StructOf("Attribute");
                for (int i = 0; i < n && arr.Ok; i++)
                {
                    var a = new BRef(me.F, attrT, arr.At + (long)i * attrT.Size, arr.K);
                    string name = a.Str("name");
                    int storage = a.In("storage_type");
                    var holder = a.Go("data");
                    byte[] data = null;
                    if (storage == 0) data = me.F.BytesAt(new BRef(me.F, me.F.StructOf("AttributeArray"), holder.At, holder.K).P("data"), holder.K);
                    var at = new Attr { Data = data, Domain = a.In("domain") };
                    r[name] = at;
                    if (at.Domain == 3 && a.In("data_type") == 6 && !name.StartsWith(".")) uvs.Add(new KeyValuePair<string, Attr>(name, at));
                }
            }
            foreach (var cd in new[] { "vdata", "edata", "pdata", "ldata" })
            {
                var c = me.Sub(cd);
                if (!c.Ok) continue;
                int tot = c.In("totlayer");
                var layers = c.Go("layers");
                var lt = me.F.StructOf("CustomDataLayer");
                for (int i = 0; i < tot && layers.Ok; i++)
                {
                    var l = new BRef(me.F, lt, layers.At + (long)i * lt.Size, layers.K);
                    int type = l.In("type");
                    string name = l.Str("name");
                    var at = new Attr { Data = me.F.BytesAt(l.P("data"), l.K), Domain = Array.IndexOf(new[] { "vdata", "edata", "pdata", "ldata" }, cd), Type = type };
                    string key = name != "" ? name : "#" + type;
                    if (!r.ContainsKey(key)) r[key] = at;
                    if (!r.ContainsKey("#" + cd + type)) r["#" + cd + type] = at;
                    if (cd == "ldata" && (type == 49 || type == 16) && !name.StartsWith(".")) uvs.Add(new KeyValuePair<string, Attr>(name, at));
                }
            }
            return r;
        }

        static float[] FloatsOf(byte[] d)
        {
            var r = new float[d.Length / 4];
            Buffer.BlockCopy(d, 0, r, 0, r.Length * 4);
            return r;
        }

        static int[] IntsOf(byte[] d)
        {
            var r = new int[d.Length / 4];
            Buffer.BlockCopy(d, 0, r, 0, r.Length * 4);
            return r;
        }

        void AddMesh(BRef ob, bool hidden, Dictionary<string, Dictionary<string, int>> boneNode, Dictionary<long, int> obNode)
        {
            var me = ob.Go("data");
            if (!me.Ok || me.Type != "Mesh") return;
            string name = ob.IdName();
            int nv = me.In("totvert", 0, me.In("verts_num")), np = me.In("totpoly", 0, me.In("faces_num")), nl = me.In("totloop", 0, me.In("corners_num"));
            var attrs = Attributes(me, out var uvLayers);
            // positions
            float[] pos = null;
            if (attrs.TryGetValue("position", out var pa) && pa.Data != null) pos = FloatsOf(pa.Data);
            else if (attrs.TryGetValue("#vdata0", out var mv) && mv.Data != null)
            {
                var mvt = me.F.StructOf("MVert");
                pos = new float[nv * 3];
                for (int i = 0; i < nv; i++)
                    for (int k = 0; k < 3; k++) pos[i * 3 + k] = BitConverter.ToSingle(mv.Data, i * mvt.Size + k * 4);
            }
            if (pos == null || pos.Length < nv * 3) { Notes.Add(name + ": its vertices weren't found"); return; }
            // faces: offsets + corner vertices (3.6+), else MPoly / MLoop
            int[] faceStart, faceLen, cornerVert, faceMat;
            byte[] off = me.Bytes("poly_offset_indices") ?? me.Bytes("face_offset_indices");
            if (off != null && attrs.TryGetValue(".corner_vert", out var cv) && cv.Data != null)
            {
                var o = IntsOf(off);
                faceStart = new int[np];
                faceLen = new int[np];
                for (int i = 0; i < np; i++) { faceStart[i] = o[i]; faceLen[i] = o[i + 1] - o[i]; }
                cornerVert = IntsOf(cv.Data);
            }
            else if (attrs.TryGetValue("#pdata25", out var mp) && attrs.TryGetValue("#ldata26", out var ml) && mp.Data != null && ml.Data != null)
            {
                var mpt = me.F.StructOf("MPoly");
                var mlt = me.F.StructOf("MLoop");
                faceStart = new int[np];
                faceLen = new int[np];
                var mats = new int[np];
                int loopstart = mpt.Fields["loopstart"].Offset, totloop = mpt.Fields["totloop"].Offset;
                bool hasMat = mpt.Fields.TryGetValue("mat_nr", out var matF);
                for (int i = 0; i < np; i++)
                {
                    faceStart[i] = BitConverter.ToInt32(mp.Data, i * mpt.Size + loopstart);
                    faceLen[i] = BitConverter.ToInt32(mp.Data, i * mpt.Size + totloop);
                    if (hasMat) mats[i] = BitConverter.ToInt16(mp.Data, i * mpt.Size + matF.Offset);
                }
                cornerVert = new int[nl];
                for (int i = 0; i < nl; i++) cornerVert[i] = BitConverter.ToInt32(ml.Data, i * mlt.Size);
                attrs["#mat_nr"] = new Attr { Data = mats.SelectMany(BitConverter.GetBytes).ToArray() };
            }
            else { Notes.Add(name + ": its faces weren't found (a Blender version not read)"); return; }
            faceMat = attrs.TryGetValue("material_index", out var mi) && mi.Data != null ? IntsOf(mi.Data)
                    : attrs.TryGetValue("#mat_nr", out var mn) ? IntsOf(mn.Data) : new int[np];
            // the UV map (the active / render one: the first)
            float[] uv = null;
            string activeUv = me.Str("active_uv_map_attribute");
            var uvl = uvLayers.FirstOrDefault(x => x.Key == activeUv);
            if (uvl.Value == null && uvLayers.Count > 0) uvl = uvLayers[0];
            if (uvl.Value != null && uvl.Value.Data != null)
            {
                if (uvl.Value.Type == 16) // MLoopUV { uv[2], flag }
                {
                    int stride = me.F.StructOf("MLoopUV")?.Size ?? 12;
                    uv = new float[nl * 2];
                    for (int i = 0; i < nl; i++) { uv[i * 2] = BitConverter.ToSingle(uvl.Value.Data, i * stride); uv[i * 2 + 1] = BitConverter.ToSingle(uvl.Value.Data, i * stride + 4); }
                }
                else uv = FloatsOf(uvl.Value.Data);
            }
            // vertex groups -> bones
            var groupNames = me.List("vertex_group_names", "bDeformGroup").Select(g => g.Str("name")).ToList();
            if (groupNames.Count == 0) groupNames = ob.List("defbase", "bDeformGroup").Select(g => g.Str("name")).ToList();
            BRef armOb = default(BRef);
            foreach (var md in ob.List("modifiers"))
                if (md.Type == "ArmatureModifierData" || md.Sub("modifier").In("type") == 8)
                {
                    var a = md.Go("object");
                    if (a.Ok) { armOb = a; break; }
                }
            var par = ob.Go("parent");
            if (!armOb.Ok && par.Ok && par.In("type") == 25 && ob.In("partype") == 4) armOb = par;
            Dictionary<string, int> bones = null;
            if (armOb.Ok) boneNode.TryGetValue(armOb.IdName(), out bones);
            var noDeform = new HashSet<string>();
            if (armOb.Ok) foreach (var b in Bones(armOb.Go("data"))) if ((b.In("flag") & (1 << 12)) != 0) noDeform.Add(b.Str("name"));
            foreach (var md in ob.List("modifiers"))
            {
                int t = md.Has("type") ? md.In("type") : md.Sub("modifier").In("type");
                string mname = md.Has("name") ? md.Str("name") : md.Sub("modifier").Str("name");
                if (t != 8 && t != 0) Notes.Add(name + ": its " + mname + " modifier isn't applied");
            }
            // the node it's on: under its parent bone / object, else the root
            var world = World(ob);
            int parent = 0;
            if (par.Ok)
            {
                if (ob.In("partype") == 7 && boneNode.TryGetValue(par.IdName(), out var pm) && pm.TryGetValue(ob.Str("parsubstr"), out int pbn)) parent = pbn;
                else if (obNode.TryGetValue(par.At, out int pon)) parent = pon;
            }
            int self = Add(name, Gl(world), parent);
            nodes[self].Hidden = hidden;
            // joints: the bones its groups name, then itself (for what no bone holds)
            var joints = new List<int>();
            var jointOfGroup = new int[groupNames.Count];
            for (int g = 0; g < groupNames.Count; g++)
            {
                jointOfGroup[g] = -1;
                if (bones != null && !noDeform.Contains(groupNames[g]) && bones.TryGetValue(groupNames[g], out int bn))
                {
                    int ji = joints.IndexOf(bn);
                    if (ji < 0) { ji = joints.Count; joints.Add(bn); }
                    jointOfGroup[g] = ji;
                }
            }
            int selfJoint = joints.Count;
            joints.Add(self);
            // per-vertex weights (MDeformVert: dw*, totweight)
            var vj = new int[nv * 4];
            var vw = new float[nv * 4];
            Attr dv;
            bool weighted = false;
            if ((attrs.TryGetValue("#vdata2", out dv) || attrs.TryGetValue("#2", out dv)) && dv.Data != null)
            {
                var dvt = me.F.StructOf("MDeformVert");
                int dwOff = dvt.Fields["dw"].Offset, totOff = dvt.Fields["totweight"].Offset;
                for (int v = 0; v < nv && (v + 1) * dvt.Size <= dv.Data.Length; v++)
                {
                    ulong dwp = me.F.PtrSize == 8 ? BitConverter.ToUInt64(dv.Data, v * dvt.Size + dwOff) : BitConverter.ToUInt32(dv.Data, v * dvt.Size + dwOff);
                    int tw = BitConverter.ToInt32(dv.Data, v * dvt.Size + totOff);
                    var dw = me.F.BytesAt(dwp, me.K);
                    if (dw == null) continue;
                    for (int k = 0; k < tw && (k + 1) * 8 <= dw.Length; k++)
                    {
                        int g = BitConverter.ToInt32(dw, k * 8);
                        float w = BitConverter.ToSingle(dw, k * 8 + 4);
                        if (g < 0 || g >= jointOfGroup.Length || jointOfGroup[g] < 0 || w <= 0f) continue;
                        // the 4 strongest
                        int slot = 0;
                        for (int q = 1; q < 4; q++) if (vw[v * 4 + q] < vw[v * 4 + slot]) slot = q;
                        if (w > vw[v * 4 + slot]) { vw[v * 4 + slot] = w; vj[v * 4 + slot] = jointOfGroup[g]; }
                    }
                }
            }
            for (int v = 0; v < nv; v++)
            {
                float s = vw[v * 4] + vw[v * 4 + 1] + vw[v * 4 + 2] + vw[v * 4 + 3];
                if (s <= 1e-6f) { vj[v * 4] = selfJoint; vw[v * 4] = 1f; vw[v * 4 + 1] = vw[v * 4 + 2] = vw[v * 4 + 3] = 0f; }
                else { weighted = true; for (int q = 0; q < 4; q++) vw[v * 4 + q] /= s; }
            }
            if (bones != null && !weighted) Notes.Add(name + ": no weights on its armature's bones");

            // shape keys (relative to the basis), with their current values
            var shapes = new List<KeyValuePair<string, float[]>>();
            var shapeVal = new List<float>();
            var key = me.Go("key");
            if (key.Ok)
            {
                var blocks = key.List("block", "KeyBlock").ToList();
                float[] basis = null;
                for (int i = 0; i < blocks.Count; i++)
                {
                    var kd = blocks[i].Bytes("data");
                    if (kd == null || kd.Length < nv * 12) continue;
                    var co = FloatsOf(kd);
                    if (basis == null) { basis = co; continue; }
                    var d = new float[nv * 3];
                    bool any = false;
                    for (int k = 0; k < nv * 3; k++) { d[k] = co[k] - basis[k]; any |= Math.Abs(d[k]) > 1e-7f; }
                    if (!any) continue;
                    shapes.Add(new KeyValuePair<string, float[]>(blocks[i].Str("name"), d));
                    shapeVal.Add(blocks[i].Fl("curval"));
                }
            }

            // corners -> vertices (a corner's own UV splits a vertex)
            var w4 = Gl(world);
            var dir = M4.Mul(C, world);
            var outIndex = new Dictionary<long, int>();
            var src = new List<int>();
            var uvs = new List<float>();
            var cornerOut = new int[nl];
            for (int c = 0; c < nl && c < cornerVert.Length; c++)
            {
                int v = cornerVert[c];
                float u0 = uv != null && c * 2 + 1 < uv.Length ? uv[c * 2] : 0f, u1 = uv != null && c * 2 + 1 < uv.Length ? uv[c * 2 + 1] : 0f;
                long k = ((long)v << 40) ^ ((long)BitConverter.ToInt32(BitConverter.GetBytes(u0), 0) << 20) ^ BitConverter.ToInt32(BitConverter.GetBytes(u1), 0);
                if (!outIndex.TryGetValue(k, out int o) || src[o] != v)
                {
                    o = src.Count;
                    outIndex[k] = o;
                    src.Add(v);
                    uvs.Add(u0);
                    uvs.Add(1f - u1); // glTF's v runs down
                }
                cornerOut[c] = o;
            }
            int n = src.Count;
            var P = new float[n * 3];
            for (int i = 0; i < n; i++)
            {
                int v = src[i];
                var p = M4.Point(dir, pos[v * 3], pos[v * 3 + 1], pos[v * 3 + 2]);
                P[i * 3] = p[0]; P[i * 3 + 1] = p[1]; P[i * 3 + 2] = p[2];
            }
            // triangles per material slot (fans)
            int slots = Math.Max(1, me.In("totcol"));
            var tris = new List<int>[slots];
            for (int s = 0; s < slots; s++) tris[s] = new List<int>();
            for (int fi = 0; fi < np; fi++)
            {
                int s = fi < faceMat.Length ? Math.Max(0, Math.Min(slots - 1, faceMat[fi])) : 0;
                for (int k = 1; k + 1 < faceLen[fi]; k++)
                {
                    tris[s].Add(cornerOut[faceStart[fi]]);
                    tris[s].Add(cornerOut[faceStart[fi] + k]);
                    tris[s].Add(cornerOut[faceStart[fi] + k + 1]);
                }
            }
            // smooth normals
            var N = new float[n * 3];
            var byVert = new float[nv * 3];
            foreach (var t in tris)
                for (int i = 0; i + 2 < t.Count; i += 3)
                {
                    int a = t[i], b = t[i + 1], c = t[i + 2];
                    float ex = P[b * 3] - P[a * 3], ey = P[b * 3 + 1] - P[a * 3 + 1], ez = P[b * 3 + 2] - P[a * 3 + 2];
                    float fx = P[c * 3] - P[a * 3], fy = P[c * 3 + 1] - P[a * 3 + 1], fz = P[c * 3 + 2] - P[a * 3 + 2];
                    float nx = ey * fz - ez * fy, ny = ez * fx - ex * fz, nz = ex * fy - ey * fx;
                    foreach (int q in new[] { a, b, c }) { byVert[src[q] * 3] += nx; byVert[src[q] * 3 + 1] += ny; byVert[src[q] * 3 + 2] += nz; }
                }
            for (int i = 0; i < n; i++)
            {
                int v = src[i];
                float x = byVert[v * 3], y = byVert[v * 3 + 1], z = byVert[v * 3 + 2], l = (float)Math.Sqrt(x * x + y * y + z * z);
                if (l > 0) { N[i * 3] = x / l; N[i * 3 + 1] = y / l; N[i * 3 + 2] = z / l; } else N[i * 3 + 1] = 1f;
            }
            var J = new int[n * 4];
            var W = new float[n * 4];
            for (int i = 0; i < n; i++) for (int q = 0; q < 4; q++) { J[i * 4 + q] = vj[src[i] * 4 + q]; W[i * 4 + q] = vw[src[i] * 4 + q]; }

            // the .glb mesh
            int aP = Floats(P, 3, "VEC3", true), aN = Floats(N, 3, "VEC3", false), aT = Floats(uvs.ToArray(), 2, "VEC2", false),
                aJ = UShorts(J, 4), aW = Floats(W, 4, "VEC4", false);
            var shapeAcc = new List<int>();
            foreach (var sh in shapes)
            {
                var d = new float[n * 3];
                for (int i = 0; i < n; i++)
                {
                    int v = src[i];
                    float x = sh.Value[v * 3], y = sh.Value[v * 3 + 1], z = sh.Value[v * 3 + 2];
                    // a direction: the object's matrix without its move
                    d[i * 3] = dir[0] * x + dir[4] * y + dir[8] * z;
                    d[i * 3 + 1] = dir[1] * x + dir[5] * y + dir[9] * z;
                    d[i * 3 + 2] = dir[2] * x + dir[6] * y + dir[10] * z;
                }
                shapeAcc.Add(Floats(d, 3, "VEC3", true));
            }
            var matArray = me.P("mat");
            var prims = new List<object>();
            for (int s = 0; s < slots; s++)
            {
                if (tris[s].Count == 0) continue;
                var prim = new Dictionary<string, object>
                {
                    { "attributes", new Dictionary<string, object> { { "POSITION", aP }, { "NORMAL", aN }, { "TEXCOORD_0", aT }, { "JOINTS_0", aJ }, { "WEIGHTS_0", aW } } },
                    { "indices", Indices(tris[s].ToArray()) }, { "mode", 4 },
                };
                ulong slotMat = matArray != 0 ? SlotMaterial(matArray, s, me.K) : 0;
                prim["material"] = Material(slotMat, me.K);
                if (shapeAcc.Count > 0) prim["targets"] = shapeAcc.Select(a => (object)new Dictionary<string, object> { { "POSITION", a } }).ToList();
                prims.Add(prim);
                Triangles += tris[s].Count / 3;
            }
            if (prims.Count == 0) return;
            var gm = new Dictionary<string, object> { { "name", name }, { "primitives", prims } };
            if (shapes.Count > 0)
            {
                gm["extras"] = new Dictionary<string, object> { { "targetNames", shapes.Select(x => (object)x.Key).ToList() } };
                gm["weights"] = shapeVal.Select(x => (object)(double)x).ToList();
                for (int i = 0; i < shapes.Count; i++) shapeToggles.Add(new object[] { self, shapes[i].Key, shapeVal[i], name });
                Shapes += shapes.Count;
            }
            nodes[self].Mesh = gMeshes.Count;
            gMeshes.Add(gm);
            var ibm = new float[joints.Count * 16];
            for (int j = 0; j < joints.Count; j++) Array.Copy(M4.Invert(nodes[joints[j]].World), 0, ibm, j * 16, 16);
            nodes[self].Skin = gSkins.Count;
            gSkins.Add(new Dictionary<string, object> { { "joints", joints.Select(x => (object)x).ToList() }, { "inverseBindMatrices", Floats(ibm, 16, "MAT4", false) } });
            partToggles.Add(new object[] { self, name, !hidden });
            Meshes++;
        }

        ulong SlotMaterial(ulong arrayPtr, int slot, BlendFile.Block owner)
        {
            var k = f.At(arrayPtr, owner, out long off);
            if (k == null) return 0;
            long at = k.At + off + (long)slot * f.PtrSize;
            if (at + f.PtrSize > k.At + k.Len) return 0;
            return f.Ptr(at);
        }

        // ------------------------------------------------------------------ materials
        int Material(ulong ptr, BlendFile.Block from)
        {
            var ma = f.Deref(ptr, null, from);
            ulong key = ma.Ok ? (ulong)ma.At : 0;
            if (matOf.TryGetValue(key, out int have)) return have;
            int index = gMaterials.Count;
            matOf[key] = index;
            var pbr = new Dictionary<string, object> { { "metallicFactor", 0.0 }, { "roughnessFactor", 0.9 } };
            var g = new Dictionary<string, object> { { "name", "material" }, { "pbrMetallicRoughness", pbr }, { "extras", new Dictionary<string, object> { { "toon", true } } } };
            gMaterials.Add(g);
            if (!ma.Ok || ma.Type != "Material") { pbr["baseColorFactor"] = new List<object> { 0.8, 0.8, 0.8, 1.0 }; return index; }
            g["name"] = ma.IdName();
            float[] color = { ma.Fl("r", 0, 0.8f), ma.Fl("g", 0, 0.8f), ma.Fl("b", 0, 0.8f) };
            BRef image = default(BRef);
            bool alpha = false;
            var tree = ma.Go("nodetree");
            if (tree.Ok && ma.In("use_nodes", 0, 1) != 0 || tree.Ok && f.Version >= 500)
            {
                var nodesL = tree.List("nodes", "bNode").ToList();
                var links = tree.List("links", "bNodeLink").ToList();
                var bsdf = nodesL.FirstOrDefault(x => x.Str("idname") == "ShaderNodeBsdfPrincipled") ;
                if (!bsdf.Ok) bsdf = nodesL.FirstOrDefault(x => x.Str("idname") == "ShaderNodeBsdfDiffuse");
                if (bsdf.Ok)
                {
                    var link = links.FirstOrDefault(l => l.Go("tonode").At == bsdf.At && (l.Go("tosock").Str("name") == "Base Color" || l.Go("tosock").Str("name") == "Color"));
                    if (link.Ok) image = Upstream(link.Go("fromnode"), links, 0);
                    else
                    {
                        // an unlinked colour: the socket's own
                        var sock = bsdf.List("inputs", "bNodeSocket").FirstOrDefault(s => s.Str("name") == "Base Color" || s.Str("name") == "Color");
                        var dv = sock.Ok ? sock.Go("default_value") : default(BRef);
                        if (dv.Ok && dv.K != null && dv.K.Len >= 12)
                            for (int i = 0; i < 3; i++) color[i] = BitConverter.ToSingle(f.B, (int)(dv.At + i * 4));
                    }
                    alpha = links.Any(l => l.Go("tonode").At == bsdf.At && l.Go("tosock").Str("name") == "Alpha");
                }
                if (!image.Ok) // a picture anywhere in it, that isn't data (normals, roughness)
                    foreach (var nd in nodesL.Where(x => x.Str("idname") == "ShaderNodeTexImage"))
                    {
                        var im = nd.Go("id");
                        if (im.Ok && !im.Sub("colorspace_settings").Str("name").Contains("Non-Color")) { image = im; break; }
                    }
            }
            // before 2.8 (Blender Internal): the material's first image texture slot
            if (!image.Ok && ma.Has("mtex"))
                for (int i = 0; i < 18 && !image.Ok; i++)
                {
                    var mf = ma.S.Fields["mtex"];
                    var mt = f.Deref(f.Ptr(ma.At + mf.Offset + i * f.PtrSize), "MTex", ma.K);
                    if (!mt.Ok) continue;
                    var tex = mt.Go("tex");
                    var im = tex.Ok ? tex.Go("ima") : default(BRef);
                    if (im.Ok) image = im;
                }
            int bm = ma.In("blend_method");
            if (alpha || bm == 3 || bm == 4 || bm == 5) { g["alphaMode"] = "MASK"; g["alphaCutoff"] = 0.5; }
            g["doubleSided"] = (ma.In("blend_flag") & 4) == 0; // Blender draws backfaces unless culling is on
            pbr["baseColorFactor"] = new List<object> { (double)color[0], (double)color[1], (double)color[2], 1.0 };
            if (image.Ok)
            {
                int ti = Texture(image, alpha);
                if (ti >= 0) { pbr["baseColorTexture"] = new Dictionary<string, object> { { "index", ti } }; pbr["baseColorFactor"] = new List<object> { 1.0, 1.0, 1.0, 1.0 }; }
            }
            return index;
        }


        // the picture feeding a node, through mixes, hue / gamma tweaks...
        BRef Upstream(BRef node, List<BRef> links, int depth)
        {
            if (!node.Ok || depth > 8) return default(BRef);
            if (node.Str("idname") == "ShaderNodeTexImage") return node.Go("id");
            foreach (var l in links.Where(l => l.Go("tonode").At == node.At))
            {
                var r = Upstream(l.Go("fromnode"), links, depth + 1);
                if (r.Ok) return r;
            }
            return default(BRef);
        }

        int Texture(BRef im, bool alpha)
        {
            string id = im.IdName();
            if (texOf.TryGetValue(id, out int have)) return have;
            texOf[id] = -1;
            byte[] data = null;
            var pf = im.Go("packedfile");
            if (!pf.Ok) foreach (var ipf in im.List("packedfiles")) { pf = ipf.Go("packedfile"); if (pf.Ok) break; }
            if (pf.Ok)
            {
                var raw = pf.Bytes("data");
                int size = pf.In("size");
                if (raw != null && size > 0 && size <= raw.Length) { data = new byte[size]; Buffer.BlockCopy(raw, 0, data, 0, size); }
            }
            string file = null;
            if (data == null)
            {
                string path = im.Has("filepath") ? im.Str("filepath") : im.Str("name");
                file = FindNear(path);
                if (file == null) { Notes.Add("texture " + Path.GetFileName(path.Replace('\\', '/')) + " isn't packed in the file nor next to it"); return -1; }
            }
            var job = new TexJob { Guid = id, File = file, Bytes = data, Alpha = alpha, Ext = file != null ? Path.GetExtension(file).ToLowerInvariant() : Magic(data) };
            if (job.Ext != ".png" && job.Ext != ".jpg" && job.Ext != ".jpeg" && job.Ext != ".tga") { Notes.Add("texture " + id + ": a " + job.Ext + " isn't read (PNG, JPEG, TGA are)"); return -1; }
            texOf[id] = Textures.Count;
            Textures.Add(job);
            return Textures.Count - 1;
        }

        static string Magic(byte[] d) => d.Length > 3 && d[0] == 0x89 && d[1] == 'P' ? ".png" : d.Length > 2 && d[0] == 0xFF && d[1] == 0xD8 ? ".jpg" : ".tga";

        // a texture path from the file ("//textures\skin.png"): next to the .blend, else anywhere under its folder by name
        string FindNear(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string p = path.Replace('\\', '/');
            if (p.StartsWith("//")) p = p.Substring(2);
            string direct = Path.Combine(dir, p);
            if (File.Exists(direct)) return direct;
            string name = Path.GetFileName(p);
            if (name == "" || !Directory.Exists(dir)) return null;
            try
            {
                foreach (var x in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    if (string.Equals(Path.GetFileName(x), name, StringComparison.OrdinalIgnoreCase)) return x;
            }
            catch (Exception) { }
            return null;
        }

        // ------------------------------------------------------------------ the .glb
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
            if (minMax && comps == 3 && a.Length >= 3)
            {
                var mn = new double[] { double.MaxValue, double.MaxValue, double.MaxValue };
                var mx = new double[] { double.MinValue, double.MinValue, double.MinValue };
                for (int i = 0; i < a.Length; i++) { mn[i % 3] = Math.Min(mn[i % 3], a[i]); mx[i % 3] = Math.Max(mx[i % 3], a[i]); }
                acc["min"] = mn.Select(x => (object)x).ToList();
                acc["max"] = mx.Select(x => (object)x).ToList();
            }
            gAccessors.Add(acc);
            return gAccessors.Count - 1;
        }

        int UShorts(int[] a, int comps)
        {
            var bytes = new byte[a.Length * 2];
            for (int i = 0; i < a.Length; i++) { bytes[i * 2] = (byte)(a[i] & 255); bytes[i * 2 + 1] = (byte)((a[i] >> 8) & 255); }
            gAccessors.Add(new Dictionary<string, object> { { "bufferView", View(bytes) }, { "componentType", 5123 }, { "count", a.Length / comps }, { "type", "VEC4" } });
            return gAccessors.Count - 1;
        }

        int Indices(int[] idx)
        {
            var bytes = new byte[idx.Length * 4];
            Buffer.BlockCopy(idx, 0, bytes, 0, bytes.Length);
            gAccessors.Add(new Dictionary<string, object> { { "bufferView", View(bytes) }, { "componentType", 5125 }, { "count", idx.Length }, { "type", "SCALAR" } });
            return gAccessors.Count - 1;
        }

        static List<object> L(float[] a) => a.Select(x => (object)(double)x).ToList();

        // a local matrix as glTF's translation / rotation / scale
        static void Decompose(float[] m, out float[] t, out float[] r, out float[] s)
        {
            t = new[] { m[12], m[13], m[14] };
            s = new float[3];
            var c = new float[9];
            for (int k = 0; k < 3; k++)
            {
                s[k] = (float)Math.Sqrt(m[k * 4] * m[k * 4] + m[k * 4 + 1] * m[k * 4 + 1] + m[k * 4 + 2] * m[k * 4 + 2]);
                for (int q = 0; q < 3; q++) c[k * 3 + q] = s[k] > 1e-8f ? m[k * 4 + q] / s[k] : 0f;
            }
            // c: columns; rotation matrix R[row,col] = c[col*3+row]
            float m00 = c[0], m11 = c[4], m22 = c[8], m01 = c[3], m10 = c[1], m02 = c[6], m20 = c[2], m12 = c[7], m21 = c[5];
            float tr = m00 + m11 + m22;
            double qw, qx, qy, qz;
            if (tr > 0) { double S = Math.Sqrt(tr + 1.0) * 2; qw = 0.25 * S; qx = (m21 - m12) / S; qy = (m02 - m20) / S; qz = (m10 - m01) / S; }
            else if (m00 > m11 && m00 > m22) { double S = Math.Sqrt(1.0 + m00 - m11 - m22) * 2; qw = (m21 - m12) / S; qx = 0.25 * S; qy = (m01 + m10) / S; qz = (m02 + m20) / S; }
            else if (m11 > m22) { double S = Math.Sqrt(1.0 + m11 - m00 - m22) * 2; qw = (m02 - m20) / S; qx = (m01 + m10) / S; qy = 0.25 * S; qz = (m12 + m21) / S; }
            else { double S = Math.Sqrt(1.0 + m22 - m00 - m11) * 2; qw = (m10 - m01) / S; qx = (m02 + m20) / S; qy = (m12 + m21) / S; qz = 0.25 * S; }
            double ql = Math.Sqrt(qw * qw + qx * qx + qy * qy + qz * qz);
            r = new[] { (float)(qx / ql), (float)(qy / ql), (float)(qz / ql), (float)(qw / ql) };
        }

        public byte[] Glb()
        {
            foreach (var t in Textures)
            {
                int image = -1;
                if (t.Out != null) { image = gImages.Count; gImages.Add(new Dictionary<string, object> { { "bufferView", View(t.Out) }, { "mimeType", t.Mime } }); }
                gTextures.Add(image >= 0 ? new Dictionary<string, object> { { "source", image } } : new Dictionary<string, object>());
            }
            foreach (Dictionary<string, object> m in gMaterials)
            {
                var pbr = m["pbrMetallicRoughness"] as Dictionary<string, object>;
                if (pbr.TryGetValue("baseColorTexture", out object bt) && !(gTextures[(int)((Dictionary<string, object>)bt)["index"]] as Dictionary<string, object>).ContainsKey("source"))
                {
                    pbr.Remove("baseColorTexture");
                    pbr["baseColorFactor"] = new List<object> { 0.8, 0.8, 0.8, 1.0 };
                }
            }
            var gNodes = new List<object>();
            for (int i = 0; i < nodes.Count; i++)
            {
                var n = nodes[i];
                var g = new Dictionary<string, object> { { "name", n.Name } };
                var local = n.Parent < 0 ? n.World : M4.Mul(M4.Invert(nodes[n.Parent].World), n.World);
                Decompose(local, out var t, out var r, out var s);
                g["translation"] = L(t);
                g["rotation"] = L(r);
                if (Math.Abs(s[0] - 1) > 1e-5 || Math.Abs(s[1] - 1) > 1e-5 || Math.Abs(s[2] - 1) > 1e-5) g["scale"] = L(s);
                if (n.Kids.Count > 0) g["children"] = n.Kids.Select(k => (object)k).ToList();
                if (n.Mesh >= 0) { g["mesh"] = n.Mesh; g["skin"] = n.Skin; }
                gNodes.Add(g);
            }
            var root = new Dictionary<string, object>
            {
                { "asset", new Dictionary<string, object> { { "version", "2.0" }, { "generator", "Delirium Sandbox .blend import" } } },
                { "scene", 0 }, { "scenes", new List<object> { new Dictionary<string, object> { { "nodes", new List<object> { 0 } } } } },
                { "nodes", gNodes }, { "meshes", gMeshes }, { "skins", gSkins }, { "materials", gMaterials }, { "textures", gTextures },
                { "images", gImages }, { "accessors", gAccessors }, { "bufferViews", gViews }, { "samplers", new List<object>() },
            };
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

        // ------------------------------------------------------------------ the wheel (VrcLook's sidecar)
        public string Sidecar()
        {
            var targets = new List<object>();
            var targetOf = new Dictionary<int, int>();
            Func<int, int> T = nd => { if (!targetOf.TryGetValue(nd, out int t)) { t = targets.Count; targetOf[nd] = t; targets.Add(nd); } return t; };
            var toggles = new List<object>();
            var effects = new Dictionary<string, object>();
            var defaults = new Dictionary<string, object>();
            var start = new List<object>();
            Func<string, float, string> K = (p, v) => p + "=" + v.ToString(CultureInfo.InvariantCulture);
            var shapeItems = new Dictionary<string, List<object>>();
            foreach (var s in shapeToggles)
            {
                int nd = (int)s[0];
                string shape = (string)s[1], mesh = (string)s[3];
                float def = (float)s[2];
                string param = "shape:" + mesh + ":" + shape;
                defaults[param] = (double)def;
                for (int i = 0; i <= 10; i++)
                    effects[K(param, i / 10f)] = new List<object> { new List<object> { T(nd), "blend:" + shape, (double)(i * 10f) } };
                if (!shapeItems.TryGetValue(mesh, out var l)) shapeItems[mesh] = l = new List<object>();
                l.Add(Item(shape, 203, toggles.Count));
                toggles.Add(new Dictionary<string, object> { { "menu", "Shape keys/" + mesh }, { "name", shape }, { "param", param }, { "value", 1.0 }, { "button", false }, { "radial", true } });
            }
            var partItems = new List<object>();
            foreach (var p in partToggles)
            {
                int nd = (int)p[0];
                string param = "show:" + (string)p[1];
                bool shown = (bool)p[2];
                defaults[param] = shown ? 1.0 : 0.0;
                if (!shown) start.Add(new List<object> { T(nd), "active", 0.0 });
                effects[K(param, 1f)] = new List<object> { new List<object> { T(nd), "active", 1.0 } };
                effects[K(param, 0f)] = new List<object> { new List<object> { T(nd), "active", 0.0 } };
                partItems.Add(Item((string)p[1], 102, toggles.Count));
                toggles.Add(new Dictionary<string, object> { { "menu", "Parts" }, { "name", (string)p[1] }, { "param", param }, { "value", 1.0 }, { "button", false } });
            }
            var rootItems = new List<object>();
            if (shapeItems.Count > 0)
            {
                // one wheel holds 8 items: pages of 7 and "More"
                var meshes = shapeItems.Select(kv => Menu(kv.Key, Pages(kv.Value))).ToList();
                rootItems.Add(Menu("Shape keys", meshes.Count == 1 ? (List<object>)((Dictionary<string, object>)meshes[0])["items"] : Pages(meshes)));
            }
            if (partItems.Count > 1) rootItems.Add(Menu("Parts", Pages(partItems)));
            var root = new Dictionary<string, object>
            {
                { "avatar", Name }, { "targets", targets }, { "start", start }, { "base", new List<object>() }, { "effects", effects },
                { "toggles", toggles }, { "parts", new List<object>() }, { "defaults", defaults }, { "icons", new List<object>() },
                { "menu", Menu("Expressions", rootItems, 0) }, { "notes", Notes.Select(x => (object)x).ToList() },
            };
            return JsonText.Write(root);
        }

        static object Item(string name, int kind, int toggle) => new Dictionary<string, object> { { "name", name }, { "kind", kind }, { "icon", -1 }, { "toggle", toggle } };
        static object Menu(string name, List<object> items, int kind = 103) => new Dictionary<string, object> { { "name", name }, { "kind", kind }, { "icon", -1 }, { "items", items } };

        static List<object> Pages(List<object> items)
        {
            if (items.Count <= 8) return items;
            var r = items.Take(7).ToList();
            r.Add(Menu("More", Pages(items.Skip(7).ToList())));
            return r;
        }
    }
}
