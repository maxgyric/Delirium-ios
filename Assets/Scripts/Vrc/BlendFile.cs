// A Blender file (.blend) read as Blender itself reads it: the file is a dump of Blender's own structs, and its
// "DNA" block describes every struct's fields, so any version's data can be found by field names. Old files
// (BLENDER-v279...: 4- or 8-byte pointers) and Blender 5's (BLENDER17-01v0500...: large block heads), gzip- or
// zstd-compressed (zstd by Assets/Plugins/iOS/blendzstd.c, Meta's decoder). Little-endian files only (every
// Blender since PowerPC Macs).
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Vrc
{
    public sealed class BlendFile
    {
        public sealed class Field
        {
            public string Type, Name;  // Name without "*" and "[n]"
            public int Offset, Size, Count = 1;
            public bool Pointer;
        }

        public sealed class Struct
        {
            public string Type;
            public int Size;
            public readonly Dictionary<string, Field> Fields = new Dictionary<string, Field>();
        }

        public sealed class Block
        {
            public string Code;
            public int Sdna;
            public ulong Old;
            public long Len, Nr, At;
            public int Owner = -1;     // the ID block (object, mesh...) whose data it is
        }

        public readonly byte[] B;
        public int PtrSize = 8;
        public int Version;                // 279, 403, 503...
        public readonly List<Block> Blocks = new List<Block>();
        readonly List<Block> sorted = new List<Block>();
        // Blender 5 writes made-up addresses, unique only within an ID's own data: looked up there first
        readonly List<Dictionary<ulong, Block>> byOwner = new List<Dictionary<ulong, Block>>();
        readonly Dictionary<ulong, Block> exact = new Dictionary<ulong, Block>();
        public Struct[] Structs;
        readonly Dictionary<string, Struct> structOf = new Dictionary<string, Struct>();

#if UNITY_IOS && !UNITY_EDITOR
        const string kLib = "__Internal";
#else
        const string kLib = "fbxblob";
#endif
        [DllImport(kLib)] static extern int dz_decompress(byte[] src, long len, out IntPtr data, out long outLen);
        [DllImport(kLib)] static extern void dz_free(IntPtr data);

        public static BlendFile Load(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            if (b.Length > 4 && b[0] == 0x28 && b[1] == 0xB5 && b[2] == 0x2F && b[3] == 0xFD)
            {
                int r = dz_decompress(b, b.LongLength, out IntPtr p, out long n);
                if (r != 0) throw new Exception("its zstd compression didn't unpack (" + r + ")");
                var o = new byte[n];
                Marshal.Copy(p, o, 0, (int)n);
                dz_free(p);
                b = o;
            }
            else if (b.Length > 2 && b[0] == 0x1F && b[1] == 0x8B)
                using (var z = new GZipStream(new MemoryStream(b), CompressionMode.Decompress))
                using (var m = new MemoryStream())
                {
                    z.CopyTo(m);
                    b = m.ToArray();
                }
            return new BlendFile(b);
        }

        BlendFile(byte[] b)
        {
            B = b;
            if (b.Length < 12 || Encoding.ASCII.GetString(b, 0, 7) != "BLENDER") throw new Exception("not a Blender file");
            int p;
            bool large = b[7] == '1' && b[8] == '7';
            if (large)
            {
                // "BLENDER17-01v0503"
                if (b[12] != 'v') throw new Exception("a big-endian Blender file");
                Version = int.Parse(Encoding.ASCII.GetString(b, 13, 4)); // 0503 -> 503
                p = 17;
            }
            else
            {
                PtrSize = b[7] == '_' ? 4 : 8;
                if (b[8] != 'v') throw new Exception("a big-endian Blender file (made on a PowerPC Mac)");
                Version = int.Parse(Encoding.ASCII.GetString(b, 9, 3));
                p = 12;
            }
            while (p + 16 <= b.Length)
            {
                var k = new Block { Code = Encoding.ASCII.GetString(b, p, 4).TrimEnd('\0') };
                if (large)
                {
                    k.Sdna = BitConverter.ToInt32(b, p + 4);
                    k.Old = BitConverter.ToUInt64(b, p + 8);
                    k.Len = BitConverter.ToInt64(b, p + 16);
                    k.Nr = BitConverter.ToInt64(b, p + 24);
                    k.At = p + 32;
                }
                else
                {
                    k.Len = BitConverter.ToInt32(b, p + 4);
                    k.Old = PtrSize == 8 ? BitConverter.ToUInt64(b, p + 8) : BitConverter.ToUInt32(b, p + 8);
                    k.Sdna = BitConverter.ToInt32(b, p + 8 + PtrSize);
                    k.Nr = BitConverter.ToInt32(b, p + 12 + PtrSize);
                    k.At = p + 16 + PtrSize;
                }
                Blocks.Add(k);
                if (k.Code == "ENDB") break;
                p = (int)(k.At + k.Len);
            }
            var dna = Blocks.Find(x => x.Code == "DNA1");
            if (dna == null) throw new Exception("no DNA block");
            ReadDna((int)dna.At);
            sorted.AddRange(Blocks);
            sorted.RemoveAll(x => x.Old == 0);
            sorted.Sort((x, y) => x.Old.CompareTo(y.Old));
            int owner = -1;
            foreach (var k in Blocks)
            {
                if (k.Code.Length == 2) { owner = byOwner.Count; byOwner.Add(new Dictionary<ulong, Block>()); }
                k.Owner = owner;
                if (owner >= 0 && k.Old != 0 && !byOwner[owner].ContainsKey(k.Old)) byOwner[owner][k.Old] = k;
                if (k.Old != 0 && !exact.ContainsKey(k.Old)) exact[k.Old] = k;
            }
        }

        void ReadDna(int p)
        {
            Func<int, int> Align = q => (q + 3) & ~3;
            Func<int, string> Tag = q => Encoding.ASCII.GetString(B, q, 4);
            p += 4; // SDNA
            if (Tag(p) != "NAME") throw new Exception("its DNA isn't readable");
            int n = BitConverter.ToInt32(B, p + 4);
            p += 8;
            var names = new string[n];
            for (int i = 0; i < n; i++) { int e = Array.IndexOf(B, (byte)0, p); names[i] = Encoding.ASCII.GetString(B, p, e - p); p = e + 1; }
            p = Align(p);
            n = BitConverter.ToInt32(B, p + 4);
            p += 8;
            var types = new string[n];
            for (int i = 0; i < n; i++) { int e = Array.IndexOf(B, (byte)0, p); types[i] = Encoding.ASCII.GetString(B, p, e - p); p = e + 1; }
            p = Align(p);
            p += 4;
            var tlen = new int[n];
            for (int i = 0; i < n; i++) tlen[i] = BitConverter.ToInt16(B, p + i * 2);
            p = Align(p + n * 2);
            p += 4;
            int ns = BitConverter.ToInt32(B, p);
            p += 4;
            Structs = new Struct[ns];
            for (int s = 0; s < ns; s++)
            {
                int t = BitConverter.ToInt16(B, p), nf = BitConverter.ToInt16(B, p + 2);
                p += 4;
                var st = new Struct { Type = types[t], Size = tlen[t] };
                int off = 0;
                for (int f = 0; f < nf; f++)
                {
                    int ft = BitConverter.ToInt16(B, p), fn = BitConverter.ToInt16(B, p + 2);
                    p += 4;
                    string raw = names[fn];
                    var fd = new Field { Type = types[ft], Offset = off };
                    fd.Pointer = raw.StartsWith("*") || raw.StartsWith("(");
                    foreach (Match m in Regex.Matches(raw, @"\[(\d+)\]")) fd.Count *= int.Parse(m.Groups[1].Value);
                    fd.Name = Regex.Replace(raw, @"\[.*", "").Replace("*", "").Replace("(", "").Replace(")", "");
                    fd.Size = (fd.Pointer ? PtrSize : tlen[ft]) * fd.Count;
                    off += fd.Size;
                    if (!st.Fields.ContainsKey(fd.Name)) st.Fields[fd.Name] = fd;
                }
                Structs[s] = st;
                if (!structOf.ContainsKey(st.Type)) structOf[st.Type] = st;
            }
        }

        public Struct StructOf(string type) => structOf.TryGetValue(type, out var s) ? s : null;

        /// The block a pointer from the file points into (null: none, or not saved).
        public Block At(ulong ptr, out long offset) => At(ptr, null, out offset);

        public Block At(ulong ptr, Block from, out long offset)
        {
            offset = 0;
            if (ptr == 0) return null;
            if (from != null && from.Owner >= 0 && byOwner[from.Owner].TryGetValue(ptr, out var own)) return own;
            if (exact.TryGetValue(ptr, out var ex)) return ex;
            int lo = 0, hi = sorted.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (sorted[mid].Old <= ptr) lo = mid + 1;
                else hi = mid - 1;
            }
            if (hi < 0) return null;
            var k = sorted[hi];
            if (ptr - k.Old >= (ulong)Math.Max(1, k.Len)) return null;
            offset = (long)(ptr - k.Old);
            return k;
        }

        /// The struct a pointer points at (typed by its block, or as `type`).
        public BRef Deref(ulong ptr, string type = null, Block from = null)
        {
            var k = At(ptr, from, out long off);
            if (k == null) return default(BRef);
            var s = type != null ? StructOf(type) : (k.Sdna >= 0 && k.Sdna < Structs.Length ? Structs[k.Sdna] : null);
            return new BRef(this, s, k.At + off, k);
        }

        /// Every block's struct with that code ("OB", "ME"...).
        public IEnumerable<BRef> Ids(string code)
        {
            foreach (var k in Blocks)
                if (k.Code == code && k.Sdna >= 0 && k.Sdna < Structs.Length) yield return new BRef(this, Structs[k.Sdna], k.At, k);
        }

        public ulong Ptr(long at) => PtrSize == 8 ? BitConverter.ToUInt64(B, (int)at) : BitConverter.ToUInt32(B, (int)at);
    }

    /// A struct in the file.
    public struct BRef
    {
        public readonly BlendFile F;
        public readonly BlendFile.Struct S;
        public readonly long At;
        public readonly BlendFile.Block K;

        public BRef(BlendFile f, BlendFile.Struct s, long at, BlendFile.Block k) { F = f; S = s; At = at; K = k; }

        public bool Ok => F != null && S != null;
        public string Type => S?.Type ?? "";
        public bool Has(string field) => S != null && S.Fields.ContainsKey(field);
        BlendFile.Field Fd(string name) => S != null && S.Fields.TryGetValue(name, out var f) ? f : null;

        /// A number field (char / short / int / float / double / int64...), its i-th if it's an array.
        public double Num(string name, int i = 0, double or = 0)
        {
            var f = Fd(name);
            if (f == null || f.Pointer || i >= f.Count) return or;
            int each = f.Size / f.Count;
            int at = (int)(At + f.Offset + i * each);
            if (at + each > F.B.Length) return or;
            switch (f.Type)
            {
                case "float": return BitConverter.ToSingle(F.B, at);
                case "double": return BitConverter.ToDouble(F.B, at);
                case "char": case "uchar": case "uint8_t": return F.B[at];
                case "int8_t": return (sbyte)F.B[at];
                case "short": case "int16_t": return BitConverter.ToInt16(F.B, at);
                case "ushort": case "uint16_t": return BitConverter.ToUInt16(F.B, at);
                case "int": case "int32_t": return BitConverter.ToInt32(F.B, at);
                case "uint": case "uint32_t": return BitConverter.ToUInt32(F.B, at);
                case "int64_t": return BitConverter.ToInt64(F.B, at);
                case "uint64_t": return BitConverter.ToUInt64(F.B, at);
            }
            return or;
        }

        public float Fl(string name, int i = 0, float or = 0f) => (float)Num(name, i, or);
        public int In(string name, int i = 0, int or = 0) => (int)Num(name, i, or);

        public float[] Floats(string name)
        {
            var f = Fd(name);
            var r = new float[f != null ? f.Count : 0];
            for (int i = 0; i < r.Length; i++) r[i] = Fl(name, i);
            return r;
        }

        public ulong P(string name)
        {
            var f = Fd(name);
            return f != null && f.Pointer ? F.Ptr(At + f.Offset) : 0;
        }

        /// A char array, or a char* string.
        public string Str(string name)
        {
            var f = Fd(name);
            if (f == null) return "";
            if (f.Pointer)
            {
                var k = F.At(F.Ptr(At + f.Offset), K, out long off);
                if (k == null) return "";
                return CStr(F.B, k.At + off, k.Len - off);
            }
            return CStr(F.B, At + f.Offset, f.Size);
        }

        static string CStr(byte[] b, long at, long max)
        {
            int n = 0;
            while (n < max && at + n < b.Length && b[at + n] != 0) n++;
            return Encoding.UTF8.GetString(b, (int)at, n);
        }

        /// An embedded struct field.
        public BRef Sub(string name)
        {
            var f = Fd(name);
            if (f == null || f.Pointer) return default(BRef);
            return new BRef(F, F.StructOf(f.Type), At + f.Offset, K);
        }

        /// What a pointer field points at (as its declared type when the block doesn't say).
        public BRef Go(string name)
        {
            var f = Fd(name);
            if (f == null || !f.Pointer) return default(BRef);
            var r = F.Deref(F.Ptr(At + f.Offset), null, K);
            if (r.Ok && (r.S.Type == f.Type || f.Type == "void" || f.Type == "ID")) return r;
            return F.Deref(F.Ptr(At + f.Offset), f.Type == "void" ? null : f.Type, K);
        }

        /// A ListBase field's items, in order.
        public IEnumerable<BRef> List(string name, string type = null)
        {
            var lb = Sub(name);
            if (!lb.Ok) yield break;
            ulong p = lb.P("first");
            var from = K;
            for (int guard = 0; p != 0 && guard < 100000; guard++)
            {
                var r = F.Deref(p, type, from);
                from = r.K ?? from;
                if (!r.Ok) yield break;
                yield return r;
                p = r.P("next");
            }
        }

        /// The i-th struct of an array a pointer field points at.
        public BRef Index(int i)
        {
            if (S == null) return default(BRef);
            return new BRef(F, S, At + (long)i * S.Size, K);
        }

        /// The raw bytes a pointer field points at (a whole block from where it points).
        public byte[] Bytes(string name) => F.BytesAt(P(name), K);

        public string IdName() => Has("id") ? Sub("id").Str("name") is string s && s.Length > 2 ? s.Substring(2) : "" : "";
    }

    public static class BlendBytes
    {
        public static byte[] BytesAt(this BlendFile f, ulong ptr, BlendFile.Block from = null)
        {
            var k = f.At(ptr, from, out long off);
            if (k == null) return null;
            var r = new byte[k.Len - off];
            Buffer.BlockCopy(f.B, (int)(k.At + off), r, 0, r.Length);
            return r;
        }
    }
}
