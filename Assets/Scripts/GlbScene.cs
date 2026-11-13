// A .glb file as Unity objects, made at run time (a character or a map picked from the phone's files): the nodes
// as GameObjects, meshes (skinned ones with their bones), base colours and textures on the sandbox's own material.
//
// glTF is right-handed, Unity left-handed: X is mirrored. A point (x, y, z) is (-x, y, z), a rotation (x, y, z, w)
// is (x, -y, -z, w), a matrix M is S M S (S: X negated), a triangle's winding is turned round, a texture's v is
// 1 - v. A model facing glTF's front (+Z) faces Unity's +Z.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public static class GlbScene
{
    /// What was made, for whoever wants to count or free it.
    public class Made
    {
        public GameObject Root;
        public int Triangles, Skinned, SeeThrough, Coloured;
        public int Textures, TexturesFailed;   // pictures loaded / not (TextureWhy: the first one's reason)
        public string TextureWhy = "";
        public readonly List<UnityEngine.Object> Owned = new List<UnityEngine.Object>(); // meshes, textures, materials
        public GameObject[] Nodes;     // the file's nodes, by their index in it (spring bones point at them)
        public string[] NodeNames;     // their names in the file (a skeleton's bones are renamed later)
        public Glb File;
    }

    static Matrix4x4 Mirror(float[] m, int at)
    {
        var r = new Matrix4x4();
        for (int col = 0; col < 4; col++)
            for (int row = 0; row < 4; row++)
            {
                float v = m[at + col * 4 + row];
                if ((row == 0) != (col == 0) && row < 3) v = -v; // S M S: the X row and the X column, not both
                r[row, col] = v;
            }
        return r;
    }

    public static Made Load(byte[] data, string name, Material baseMaterial)
    {
        var glb = Glb.Parse(data);
        var made = new Made { Root = new GameObject(name), File = glb };
        var nodes = glb.List("nodes");
        var objects = new GameObject[nodes.Count];
        made.Nodes = objects;
        made.NodeNames = new string[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            var go = new GameObject(Json.Str(nodes[i], "name", "node" + i));
            objects[i] = go;
            made.NodeNames[i] = go.name;
        }
        // the tree, then each node's place in its parent
        var hasParent = new bool[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            int[] kids = Json.Ints(nodes[i], "children");
            if (kids == null) continue;
            foreach (int k in kids)
                if (k >= 0 && k < objects.Length && k != i)
                {
                    objects[k].transform.SetParent(objects[i].transform, false);
                    hasParent[k] = true;
                }
        }
        for (int i = 0; i < nodes.Count; i++)
        {
            var t = objects[i].transform;
            if (!hasParent[i]) t.SetParent(made.Root.transform, false);
            float[] m = Json.Nums(nodes[i], "matrix");
            if (m != null && m.Length == 16)
            {
                var mm = Mirror(m, 0);
                Vector3 x = mm.GetColumn(0), y = mm.GetColumn(1), z = mm.GetColumn(2);
                t.localPosition = mm.GetColumn(3);
                t.localScale = new Vector3(x.magnitude, y.magnitude, z.magnitude);
                if (z.sqrMagnitude > 1e-12f && y.sqrMagnitude > 1e-12f) t.localRotation = Quaternion.LookRotation(z, y);
                continue;
            }
            float[] p = Json.Nums(nodes[i], "translation"), r = Json.Nums(nodes[i], "rotation"), s = Json.Nums(nodes[i], "scale");
            if (p != null && p.Length == 3) t.localPosition = new Vector3(-p[0], p[1], p[2]);
            if (r != null && r.Length == 4) t.localRotation = new Quaternion(r[0], -r[1], -r[2], r[3]);
            if (s != null && s.Length == 3) t.localScale = new Vector3(s[0], s[1], s[2]);
        }

        var textures = new Dictionary<int, Texture2D>();
        var materials = new Dictionary<int, Material>();
        Material fallback = null;
        for (int i = 0; i < nodes.Count; i++)
        {
            object mesh = glb.Item("meshes", Json.Int(nodes[i], "mesh"));
            if (mesh == null) continue;
            object skin = glb.Item("skins", Json.Int(nodes[i], "skin"));
            var prims = Json.Arr(Json.Get(mesh, "primitives"));
            if (prims == null) continue;
            for (int p = 0; p < prims.Count; p++)
            {
                if (Json.Int(prims[p], "mode", 4) != 4) continue; // triangles only
                object attrs = Json.Get(prims[p], "attributes");
                int posAcc = Json.Int(attrs, "POSITION");
                if (posAcc < 0) continue;
                float[] pos = glb.Floats(posAcc, out int pc);
                if (pc != 3 || pos.Length < 9) continue;
                int n = pos.Length / 3;
                var verts = new Vector3[n];
                for (int v = 0; v < n; v++) verts[v] = new Vector3(-pos[v * 3], pos[v * 3 + 1], pos[v * 3 + 2]);
                var m = new Mesh { name = objects[i].name };
                if (n > 65535) m.indexFormat = IndexFormat.UInt32;
                m.vertices = verts;
                float[] nrm = glb.Floats(Json.Int(attrs, "NORMAL"), out int nc);
                bool normals = nc == 3 && nrm.Length == pos.Length;
                if (normals)
                {
                    var ns = new Vector3[n];
                    for (int v = 0; v < n; v++) ns[v] = new Vector3(-nrm[v * 3], nrm[v * 3 + 1], nrm[v * 3 + 2]);
                    m.normals = ns;
                }
                float[] uv = glb.Floats(Json.Int(attrs, "TEXCOORD_0"), out int uc);
                if (uc == 2 && uv.Length == n * 2)
                {
                    var us = new Vector2[n];
                    for (int v = 0; v < n; v++) us[v] = new Vector2(uv[v * 2], 1f - uv[v * 2 + 1]);
                    m.uv = us;
                }
                // the vertices' own colours (a map's shading baked into it); white where the file has none
                float[] col = glb.Floats(Json.Int(attrs, "COLOR_0"), out int cc);
                // (8 bits a channel, as a texture's: a quarter of the memory of floats, nothing seen lost)
                var colours = new Color32[n];
                bool coloured = (cc == 3 || cc == 4) && col.Length == n * cc;
                for (int v = 0; v < n; v++)
                    colours[v] = coloured ? (Color32)new Color(col[v * cc], col[v * cc + 1], col[v * cc + 2], cc == 4 ? col[v * cc + 3] : 1f) : new Color32(255, 255, 255, 255);
                m.colors32 = colours;
                if (coloured) made.Coloured++;
                int[] idx;
                int idxAcc = Json.Int(prims[p], "indices");
                if (idxAcc >= 0) idx = glb.Ints(idxAcc, out int unusedComps);
                else
                {
                    idx = new int[n];
                    for (int v = 0; v < n; v++) idx[v] = v;
                }
                int tris = idx.Length / 3;
                var wound = new int[tris * 3];
                bool bad = false;
                for (int k = 0; k < tris; k++)
                {
                    int a = idx[k * 3], b = idx[k * 3 + 1], c = idx[k * 3 + 2];
                    if (a < 0 || b < 0 || c < 0 || a >= n || b >= n || c >= n) { bad = true; break; }
                    wound[k * 3] = a;
                    wound[k * 3 + 1] = c; // the mirror turned the triangle round
                    wound[k * 3 + 2] = b;
                }
                if (bad || tris == 0) { UnityEngine.Object.Destroy(m); continue; }
                m.triangles = wound;
                m.OptimizeIndexBuffers(); // the same triangles, in the order the GPU's vertex cache likes best
                if (!normals) m.RecalculateNormals();
                m.RecalculateBounds();
                made.Triangles += tris;
                made.Owned.Add(m);

                // one primitive: on the node itself; more: a child each
                GameObject go = objects[i];
                if (prims.Count > 1)
                {
                    go = new GameObject(objects[i].name + "_" + p);
                    go.transform.SetParent(objects[i].transform, false);
                }
                int matIndex = Json.Int(prims[p], "material");
                Material mat;
                if (matIndex < 0 || glb.Item("materials", matIndex) == null)
                {
                    if (fallback == null) { fallback = new Material(baseMaterial) { color = new Color(0.75f, 0.75f, 0.78f) }; made.Owned.Add(fallback); }
                    mat = fallback;
                }
                else if (!materials.TryGetValue(matIndex, out mat))
                {
                    mat = MakeMaterial(glb, matIndex, baseMaterial, textures, made);
                    materials[matIndex] = mat;
                }

                int[] joints = skin == null ? null : Json.Ints(skin, "joints");
                int[] vj = glb.Ints(Json.Int(attrs, "JOINTS_0"), out int jc);
                float[] vw = glb.Floats(Json.Int(attrs, "WEIGHTS_0"), out int wc);
                if (joints != null && joints.Length > 0 && jc == 4 && wc == 4 && vj.Length == n * 4 && vw.Length == n * 4)
                {
                    var bw = new BoneWeight[n];
                    for (int v = 0; v < n; v++)
                    {
                        float w0 = vw[v * 4], w1 = vw[v * 4 + 1], w2 = vw[v * 4 + 2], w3 = vw[v * 4 + 3], sum = w0 + w1 + w2 + w3;
                        if (sum < 1e-6f) { w0 = 1f; sum = 1f; }
                        bw[v] = new BoneWeight
                        {
                            boneIndex0 = Mathf.Clamp(vj[v * 4], 0, joints.Length - 1), weight0 = w0 / sum,
                            boneIndex1 = Mathf.Clamp(vj[v * 4 + 1], 0, joints.Length - 1), weight1 = w1 / sum,
                            boneIndex2 = Mathf.Clamp(vj[v * 4 + 2], 0, joints.Length - 1), weight2 = w2 / sum,
                            boneIndex3 = Mathf.Clamp(vj[v * 4 + 3], 0, joints.Length - 1), weight3 = w3 / sum,
                        };
                    }
                    m.boneWeights = bw;
                    var bones = new Transform[joints.Length];
                    for (int b = 0; b < bones.Length; b++)
                        bones[b] = joints[b] >= 0 && joints[b] < objects.Length ? objects[joints[b]].transform : made.Root.transform;
                    var binds = new Matrix4x4[joints.Length];
                    float[] ibm = glb.Floats(Json.Int(skin, "inverseBindMatrices"), out int ic);
                    for (int b = 0; b < binds.Length; b++)
                        binds[b] = ic == 16 && ibm.Length >= (b + 1) * 16 ? Mirror(ibm, b * 16) : Matrix4x4.identity;
                    m.bindposes = binds;
                    var smr = go.AddComponent<SkinnedMeshRenderer>();
                    smr.sharedMesh = m;
                    smr.bones = bones;
                    int rootNode = Json.Int(skin, "skeleton");
                    smr.rootBone = rootNode >= 0 && rootNode < objects.Length ? objects[rootNode].transform : bones[0];
                    smr.sharedMaterial = mat;
                    smr.updateWhenOffscreen = true; // its bounds follow the bones wherever a ragdoll throws them
                    made.Skinned++;
                }
                else
                {
                    go.AddComponent<MeshFilter>().sharedMesh = m;
                    go.AddComponent<MeshRenderer>().sharedMaterial = mat;
                }
            }
        }
        return made;
    }

    // The sandbox's own material (a shader the build surely has) with the glTF material's base colour and texture.
    static Material MakeMaterial(Glb glb, int index, Material baseMaterial, Dictionary<int, Texture2D> textures, Made made)
    {
        object src = glb.Item("materials", index);
        var mat = new Material(baseMaterial) { name = Json.Str(src, "name", "material" + index) };
        made.Owned.Add(mat);
        object pbr = Json.Get(src, "pbrMetallicRoughness");
        float[] c = Json.Nums(pbr, "baseColorFactor");
        mat.color = c != null && c.Length >= 3 ? new Color(c[0], c[1], c[2], 1f) : Color.white;
        // matt: the file's own shine needs its roughness map, which isn't read (Standard's default is glossy plastic)
        if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0.05f);
        if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);
        // see-through parts cut out (leaves, fences, glass panes): MASK at its cutoff; BLEND too, at a half (no
        // sorting of see-through faces); OPAQUE: nothing cut
        string alpha = Json.Str(src, "alphaMode", "OPAQUE");
        float cut = alpha == "MASK" ? Json.Num(src, "alphaCutoff", 0.5f) : alpha == "BLEND" ? 0.5f : 0f;
        if (mat.HasProperty("_Cutoff")) mat.SetFloat("_Cutoff", cut);
        if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", Json.Flag(src, "doubleSided") ? 0f : 2f);
        if (alpha != "OPAQUE") made.SeeThrough++;
        int tex = Json.Int(Json.Get(pbr, "baseColorTexture"), "index");
        int image = Json.Int(glb.Item("textures", tex), "source");
        if (image >= 0)
        {
            if (!textures.TryGetValue(image, out Texture2D t))
            {
                byte[] bytes = glb.Image(image);
                string mime = Json.Str(glb.Item("images", image), "mimeType", "?");
                if (bytes == null)
                {
                    made.TexturesFailed++;
                    if (made.TextureWhy == "") made.TextureWhy = "image " + image + " (" + mime + ") isn't inside the .glb";
                }
                else
                {
                    t = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = "image" + image };
                    bool ok = t.LoadImage(bytes, true); // PNG or JPEG; true: freed from the CPU's side once it's on the GPU
                    if (!ok)
                    {
                        // once more the plainest way (kept readable, no mipmaps of our asking)
                        UnityEngine.Object.Destroy(t);
                        t = new Texture2D(2, 2) { name = "image" + image };
                        ok = t.LoadImage(bytes, false);
                    }
                    if (!ok)
                    {
                        UnityEngine.Object.Destroy(t);
                        t = null;
                        made.TexturesFailed++;
                        if (made.TextureWhy == "")
                            made.TextureWhy = "image " + image + " (" + mime + ", " + bytes.Length + " bytes, starts " +
                                              BitConverter.ToString(bytes, 0, Math.Min(4, bytes.Length)) + ") didn't decode";
                    }
                    else
                    {
                        made.Owned.Add(t);
                        made.Textures++;
                    }
                }
                textures[image] = t;
            }
            if (t != null) mat.mainTexture = t;
        }
        return mat;
    }
}
