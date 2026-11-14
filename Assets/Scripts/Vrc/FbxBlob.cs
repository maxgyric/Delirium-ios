// An .fbx read by ufbx (Assets/Plugins/iOS/ufbx.c, MIT) through fbxblob.c: every mesh as it sits in the bind pose, in
// glTF's frame (right-handed, Y up, metres), with its skin (4 bones a vertex), blend shapes and triangles per material.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Vrc
{
    public sealed class FbxMesh
    {
        public string Name;
        public int Count;                      // vertices
        public float[] Pos, Nrm, Uv;           // 3, 3, 2 a vertex
        public bool Skinned;                   // false: one "bone", the mesh's own node
        public string[] Bones;
        public float[][] Ibm, Bind;            // a bone's inverse bind matrix and bind matrix (column-major 4x4)
        public int[] Joints;                   // 4 a vertex
        public float[] Weights;                // 4 a vertex
        public readonly List<KeyValuePair<string, int[]>> Slots = new List<KeyValuePair<string, int[]>>();   // material name, triangles
        public readonly List<KeyValuePair<string, float[]>> Shapes = new List<KeyValuePair<string, float[]>>(); // name, offsets (3 a vertex)
    }

    public sealed class FbxFile
    {
        public readonly List<FbxMesh> Meshes = new List<FbxMesh>();
        public readonly Dictionary<string, float[]> Nodes = new Dictionary<string, float[]>(); // name -> world matrix
        public readonly Dictionary<string, string> Parents = new Dictionary<string, string>(); // name -> its parent's ("": none)

#if UNITY_IOS && !UNITY_EDITOR
        const string kLib = "__Internal";
#else
        const string kLib = "fbxblob";
#endif
        [DllImport(kLib)] static extern int fx_load(byte[] path, out IntPtr data, out int len);
        [DllImport(kLib)] static extern void fx_free(IntPtr data);

        public static FbxFile Load(string path)
        {
            byte[] p = Encoding.UTF8.GetBytes(path + "\0");
            int r = fx_load(p, out IntPtr ptr, out int len);
            var bytes = new byte[Math.Max(len, 0)];
            if (len > 0) Marshal.Copy(ptr, bytes, 0, len);
            fx_free(ptr);
            if (r != 0) throw new Exception("the .fbx didn't read: " + Encoding.UTF8.GetString(bytes).TrimEnd('\0'));
            return Parse(bytes);
        }

        static FbxFile Parse(byte[] b)
        {
            var f = new FbxFile();
            var r = new BinaryReader(new MemoryStream(b), Encoding.UTF8);
            if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "FXB3") throw new Exception("fbxblob: not its format");
            Func<string> str = () => Encoding.UTF8.GetString(r.ReadBytes(r.ReadInt32()));
            Func<int, float[]> floats = n => { var a = new float[n]; for (int i = 0; i < n; i++) a[i] = r.ReadSingle(); return a; };
            int meshes = r.ReadInt32();
            for (int m = 0; m < meshes; m++)
            {
                var mesh = new FbxMesh { Name = str() };
                int nv = mesh.Count = r.ReadInt32();
                mesh.Pos = floats(nv * 3);
                mesh.Nrm = floats(nv * 3);
                mesh.Uv = floats(nv * 2);
                mesh.Skinned = r.ReadInt32() != 0;
                int nb = r.ReadInt32();
                mesh.Bones = new string[nb];
                mesh.Ibm = new float[nb][];
                mesh.Bind = new float[nb][];
                for (int i = 0; i < nb; i++)
                {
                    mesh.Bones[i] = str();
                    mesh.Ibm[i] = floats(16);
                    mesh.Bind[i] = floats(16);
                }
                mesh.Joints = new int[nv * 4];
                mesh.Weights = new float[nv * 4];
                for (int v = 0; v < nv; v++)
                {
                    for (int q = 0; q < 4; q++) mesh.Joints[v * 4 + q] = r.ReadInt32();
                    for (int q = 0; q < 4; q++) mesh.Weights[v * 4 + q] = r.ReadSingle();
                }
                int slots = r.ReadInt32();
                for (int s = 0; s < slots; s++)
                {
                    string name = str();
                    int n = r.ReadInt32();
                    var idx = new int[n];
                    for (int i = 0; i < n; i++) idx[i] = r.ReadInt32();
                    mesh.Slots.Add(new KeyValuePair<string, int[]>(name, idx));
                }
                int shapes = r.ReadInt32();
                for (int s = 0; s < shapes; s++)
                {
                    string name = str();
                    mesh.Shapes.Add(new KeyValuePair<string, float[]>(name, floats(nv * 3)));
                }
                f.Meshes.Add(mesh);
            }
            int nodes = r.ReadInt32();
            for (int i = 0; i < nodes; i++)
            {
                string name = str(), parent = str();
                var m4 = floats(16);
                if (!f.Nodes.ContainsKey(name)) { f.Nodes[name] = m4; f.Parents[name] = parent; }
            }
            return f;
        }
    }

    /// 4x4 matrices as glTF keeps them: 16 floats, column-major.
    public static class M4
    {
        public static float[] Identity() => new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

        public static float[] Mul(float[] a, float[] b)
        {
            var r = new float[16];
            for (int c = 0; c < 4; c++)
                for (int row = 0; row < 4; row++)
                {
                    float s = 0;
                    for (int k = 0; k < 4; k++) s += a[k * 4 + row] * b[c * 4 + k];
                    r[c * 4 + row] = s;
                }
            return r;
        }

        // translation, rotation (x, y, z, w), scale
        public static float[] Trs(float[] t, float[] q, float[] s)
        {
            float x = q[0], y = q[1], z = q[2], w = q[3];
            float xx = x * x, yy = y * y, zz = z * z, xy = x * y, xz = x * z, yz = y * z, wx = w * x, wy = w * y, wz = w * z;
            return new float[]
            {
                (1 - 2 * (yy + zz)) * s[0], (2 * (xy + wz)) * s[0], (2 * (xz - wy)) * s[0], 0,
                (2 * (xy - wz)) * s[1], (1 - 2 * (xx + zz)) * s[1], (2 * (yz + wx)) * s[1], 0,
                (2 * (xz + wy)) * s[2], (2 * (yz - wx)) * s[2], (1 - 2 * (xx + yy)) * s[2], 0,
                t[0], t[1], t[2], 1,
            };
        }

        public static float[] Point(float[] m, float x, float y, float z) =>
            new[] { m[0] * x + m[4] * y + m[8] * z + m[12], m[1] * x + m[5] * y + m[9] * z + m[13], m[2] * x + m[6] * y + m[10] * z + m[14] };

        public static float[] Invert(float[] m)
        {
            var inv = new double[16];
            double[] a = new double[16];
            for (int i = 0; i < 16; i++) a[i] = m[i];
            inv[0] = a[5] * a[10] * a[15] - a[5] * a[11] * a[14] - a[9] * a[6] * a[15] + a[9] * a[7] * a[14] + a[13] * a[6] * a[11] - a[13] * a[7] * a[10];
            inv[4] = -a[4] * a[10] * a[15] + a[4] * a[11] * a[14] + a[8] * a[6] * a[15] - a[8] * a[7] * a[14] - a[12] * a[6] * a[11] + a[12] * a[7] * a[10];
            inv[8] = a[4] * a[9] * a[15] - a[4] * a[11] * a[13] - a[8] * a[5] * a[15] + a[8] * a[7] * a[13] + a[12] * a[5] * a[11] - a[12] * a[7] * a[9];
            inv[12] = -a[4] * a[9] * a[14] + a[4] * a[10] * a[13] + a[8] * a[5] * a[14] - a[8] * a[6] * a[13] - a[12] * a[5] * a[10] + a[12] * a[6] * a[9];
            inv[1] = -a[1] * a[10] * a[15] + a[1] * a[11] * a[14] + a[9] * a[2] * a[15] - a[9] * a[3] * a[14] - a[13] * a[2] * a[11] + a[13] * a[3] * a[10];
            inv[5] = a[0] * a[10] * a[15] - a[0] * a[11] * a[14] - a[8] * a[2] * a[15] + a[8] * a[3] * a[14] + a[12] * a[2] * a[11] - a[12] * a[3] * a[10];
            inv[9] = -a[0] * a[9] * a[15] + a[0] * a[11] * a[13] + a[8] * a[1] * a[15] - a[8] * a[3] * a[13] - a[12] * a[1] * a[11] + a[12] * a[3] * a[9];
            inv[13] = a[0] * a[9] * a[14] - a[0] * a[10] * a[13] - a[8] * a[1] * a[14] + a[8] * a[2] * a[13] + a[12] * a[1] * a[10] - a[12] * a[2] * a[9];
            inv[2] = a[1] * a[6] * a[15] - a[1] * a[7] * a[14] - a[5] * a[2] * a[15] + a[5] * a[3] * a[14] + a[13] * a[2] * a[7] - a[13] * a[3] * a[6];
            inv[6] = -a[0] * a[6] * a[15] + a[0] * a[7] * a[14] + a[4] * a[2] * a[15] - a[4] * a[3] * a[14] - a[12] * a[2] * a[7] + a[12] * a[3] * a[6];
            inv[10] = a[0] * a[5] * a[15] - a[0] * a[7] * a[13] - a[4] * a[1] * a[15] + a[4] * a[3] * a[13] + a[12] * a[1] * a[7] - a[12] * a[3] * a[5];
            inv[14] = -a[0] * a[5] * a[14] + a[0] * a[6] * a[13] + a[4] * a[1] * a[14] - a[4] * a[2] * a[13] - a[12] * a[1] * a[6] + a[12] * a[2] * a[5];
            inv[3] = -a[1] * a[6] * a[11] + a[1] * a[7] * a[10] + a[5] * a[2] * a[11] - a[5] * a[3] * a[10] - a[9] * a[2] * a[7] + a[9] * a[3] * a[6];
            inv[7] = a[0] * a[6] * a[11] - a[0] * a[7] * a[10] - a[4] * a[2] * a[11] + a[4] * a[3] * a[10] + a[8] * a[2] * a[7] - a[8] * a[3] * a[6];
            inv[11] = -a[0] * a[5] * a[11] + a[0] * a[7] * a[9] + a[4] * a[1] * a[11] - a[4] * a[3] * a[9] - a[8] * a[1] * a[7] + a[8] * a[3] * a[5];
            inv[15] = a[0] * a[5] * a[10] - a[0] * a[6] * a[9] - a[4] * a[1] * a[10] + a[4] * a[2] * a[9] + a[8] * a[1] * a[6] - a[8] * a[2] * a[5];
            double det = a[0] * inv[0] + a[1] * inv[4] + a[2] * inv[8] + a[3] * inv[12];
            var r = new float[16];
            if (Math.Abs(det) < 1e-30) return Identity();
            for (int i = 0; i < 16; i++) r[i] = (float)(inv[i] / det);
            return r;
        }
    }
}
