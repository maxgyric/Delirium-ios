// A .glb file read (glTF 2.0 binary): its JSON as plain dictionaries and lists, its accessors as arrays of numbers.
// Plain C# (nothing of Unity), so it reads the same in the editor, on an iPhone and in a test. What it doesn't read:
// .gltf with outside files, Draco / meshopt compression, sparse accessors, KTX2 textures.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

/// A small JSON reader: objects are Dictionary&lt;string, object&gt;, arrays List&lt;object&gt;, numbers double.
public static class Json
{
    public static object Parse(string s)
    {
        int i = 0;
        object v = Value(s, ref i);
        return v;
    }

    static void Space(string s, ref int i)
    {
        while (i < s.Length && (s[i] == ' ' || s[i] == '\n' || s[i] == '\r' || s[i] == '\t')) i++;
    }

    static object Value(string s, ref int i)
    {
        Space(s, ref i);
        if (i >= s.Length) throw new FormatException("JSON ends early");
        char c = s[i];
        if (c == '{')
        {
            var o = new Dictionary<string, object>();
            i++;
            Space(s, ref i);
            if (s[i] == '}') { i++; return o; }
            while (true)
            {
                Space(s, ref i);
                string key = Text(s, ref i);
                Space(s, ref i);
                if (s[i] != ':') throw new FormatException("JSON: ':' expected at " + i);
                i++;
                o[key] = Value(s, ref i);
                Space(s, ref i);
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return o; }
                throw new FormatException("JSON: ',' or '}' expected at " + i);
            }
        }
        if (c == '[')
        {
            var a = new List<object>();
            i++;
            Space(s, ref i);
            if (s[i] == ']') { i++; return a; }
            while (true)
            {
                a.Add(Value(s, ref i));
                Space(s, ref i);
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return a; }
                throw new FormatException("JSON: ',' or ']' expected at " + i);
            }
        }
        if (c == '"') return Text(s, ref i);
        if (string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
        if (string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
        if (string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
        int from = i;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-' || s[i] == '+' || s[i] == '.' || s[i] == 'e' || s[i] == 'E')) i++;
        if (i == from) throw new FormatException("JSON: a value expected at " + i);
        return double.Parse(s.Substring(from, i - from), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    static string Text(string s, ref int i)
    {
        if (s[i] != '"') throw new FormatException("JSON: a string expected at " + i);
        i++;
        var b = new StringBuilder();
        while (s[i] != '"')
        {
            char c = s[i++];
            if (c != '\\') { b.Append(c); continue; }
            c = s[i++];
            switch (c)
            {
                case 'n': b.Append('\n'); break;
                case 't': b.Append('\t'); break;
                case 'r': b.Append('\r'); break;
                case 'b': b.Append('\b'); break;
                case 'f': b.Append('\f'); break;
                case 'u': b.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber)); i += 4; break;
                default: b.Append(c); break; // \" \\ \/
            }
        }
        i++;
        return b.ToString();
    }

    public static Dictionary<string, object> Obj(object o) => o as Dictionary<string, object>;
    public static List<object> Arr(object o) => o as List<object>;

    public static object Get(object o, string key)
    {
        var d = o as Dictionary<string, object>;
        return d != null && d.TryGetValue(key, out object v) ? v : null;
    }

    public static bool Has(object o, string key) => Get(o, key) != null;
    public static int Int(object o, string key, int absent = -1) => Get(o, key) is double d ? (int)d : absent;
    public static float Num(object o, string key, float absent = 0f) => Get(o, key) is double d ? (float)d : absent;
    public static string Str(object o, string key, string absent = null) => Get(o, key) as string ?? absent;
    public static bool Flag(object o, string key) => Get(o, key) is bool b && b;

    public static float[] Nums(object o, string key)
    {
        var a = Get(o, key) as List<object>;
        if (a == null) return null;
        var r = new float[a.Count];
        for (int i = 0; i < r.Length; i++) r[i] = a[i] is double d ? (float)d : 0f;
        return r;
    }

    public static int[] Ints(object o, string key)
    {
        var a = Get(o, key) as List<object>;
        if (a == null) return null;
        var r = new int[a.Count];
        for (int i = 0; i < r.Length; i++) r[i] = a[i] is double d ? (int)d : 0;
        return r;
    }
}

public class Glb
{
    public Dictionary<string, object> Root;
    public byte[] Bin = new byte[0];

    public static Glb Parse(byte[] data)
    {
        if (data == null || data.Length < 20 || BitConverter.ToUInt32(data, 0) != 0x46546C67u)
            throw new FormatException("not a .glb file (export as glTF Binary, .glb)");
        if (BitConverter.ToUInt32(data, 4) != 2) throw new FormatException("glTF version " + BitConverter.ToUInt32(data, 4) + ": only 2.0 is read");
        var g = new Glb();
        int at = 12;
        while (at + 8 <= data.Length)
        {
            int length = (int)BitConverter.ToUInt32(data, at);
            uint kind = BitConverter.ToUInt32(data, at + 4);
            at += 8;
            if (length < 0 || at + length > data.Length) throw new FormatException("the .glb file is cut short");
            if (kind == 0x4E4F534Au) g.Root = Json.Obj(Json.Parse(Encoding.UTF8.GetString(data, at, length)));
            else if (kind == 0x004E4942u && g.Bin.Length == 0)
            {
                g.Bin = new byte[length];
                Buffer.BlockCopy(data, at, g.Bin, 0, length);
            }
            at += length + (4 - length % 4) % 4;
        }
        if (g.Root == null) throw new FormatException("the .glb file has no JSON part");
        foreach (var x in g.List("extensionsRequired"))
        {
            string e = x as string;
            if (e == "KHR_draco_mesh_compression" || e == "EXT_meshopt_compression")
                throw new FormatException("the file is compressed (" + e + "): export it without mesh compression");
        }
        return g;
    }

    public List<object> List(string key) => Json.Arr(Json.Get(Root, key)) ?? new List<object>();

    public object Item(string list, int index)
    {
        var l = List(list);
        return index >= 0 && index < l.Count ? l[index] : null;
    }

    static int Components(string type)
    {
        switch (type)
        {
            case "SCALAR": return 1;
            case "VEC2": return 2;
            case "VEC3": return 3;
            case "VEC4": return 4;
            case "MAT2": return 4;
            case "MAT3": return 9;
            case "MAT4": return 16;
            default: throw new FormatException("accessor type " + type);
        }
    }

    static int Size(int componentType)
    {
        switch (componentType)
        {
            case 5120: case 5121: return 1;
            case 5122: case 5123: return 2;
            case 5125: case 5126: return 4;
            default: throw new FormatException("component type " + componentType);
        }
    }

    // Where an accessor's numbers are: the first one's place in Bin, the step between elements; false: none (zeros).
    bool Where(object acc, out int start, out int stride, out int count, out int comps, out int kind)
    {
        count = Json.Int(acc, "count", 0);
        comps = Components(Json.Str(acc, "type", "SCALAR"));
        kind = Json.Int(acc, "componentType", 5126);
        start = stride = 0;
        if (Json.Has(acc, "sparse")) throw new FormatException("sparse accessors aren't read");
        object view = Item("bufferViews", Json.Int(acc, "bufferView"));
        if (view == null) return false;
        if (Json.Int(view, "buffer", 0) != 0 || Json.Has(Item("buffers", 0), "uri")) throw new FormatException("the data is in an outside file: export one .glb");
        start = Json.Int(view, "byteOffset", 0) + Json.Int(acc, "byteOffset", 0);
        stride = Json.Int(view, "byteStride", 0);
        if (stride <= 0) stride = comps * Size(kind);
        if (count > 0 && start + (long)(count - 1) * stride + comps * Size(kind) > Bin.Length) throw new FormatException("an accessor runs past the file's data");
        return true;
    }

    /// An accessor as floats (count * comps); whole-number types as they are, or 0..1 / -1..1 when normalized.
    public float[] Floats(int accessor, out int comps)
    {
        object acc = Item("accessors", accessor);
        if (acc == null) { comps = 0; return new float[0]; }
        bool there = Where(acc, out int start, out int stride, out int count, out comps, out int kind);
        var r = new float[count * comps];
        if (!there) return r;
        bool norm = Json.Flag(acc, "normalized");
        int size = Size(kind);
        for (int i = 0; i < count; i++)
            for (int c = 0; c < comps; c++)
            {
                int at = start + i * stride + c * size;
                float v;
                switch (kind)
                {
                    case 5126: v = BitConverter.ToSingle(Bin, at); break;
                    case 5121: v = norm ? Bin[at] / 255f : Bin[at]; break;
                    case 5123: v = norm ? BitConverter.ToUInt16(Bin, at) / 65535f : BitConverter.ToUInt16(Bin, at); break;
                    case 5120: v = norm ? Math.Max((sbyte)Bin[at] / 127f, -1f) : (sbyte)Bin[at]; break;
                    case 5122: v = norm ? Math.Max(BitConverter.ToInt16(Bin, at) / 32767f, -1f) : BitConverter.ToInt16(Bin, at); break;
                    default: v = BitConverter.ToUInt32(Bin, at); break;
                }
                r[i * comps + c] = v;
            }
        return r;
    }

    /// An accessor as whole numbers (indices, joints).
    public int[] Ints(int accessor, out int comps)
    {
        object acc = Item("accessors", accessor);
        if (acc == null) { comps = 0; return new int[0]; }
        bool there = Where(acc, out int start, out int stride, out int count, out comps, out int kind);
        var r = new int[count * comps];
        if (!there) return r;
        int size = Size(kind);
        for (int i = 0; i < count; i++)
            for (int c = 0; c < comps; c++)
            {
                int at = start + i * stride + c * size;
                switch (kind)
                {
                    case 5121: r[i * comps + c] = Bin[at]; break;
                    case 5123: r[i * comps + c] = BitConverter.ToUInt16(Bin, at); break;
                    case 5125: r[i * comps + c] = (int)BitConverter.ToUInt32(Bin, at); break;
                    case 5120: r[i * comps + c] = (sbyte)Bin[at]; break;
                    case 5122: r[i * comps + c] = BitConverter.ToInt16(Bin, at); break;
                    default: r[i * comps + c] = (int)BitConverter.ToSingle(Bin, at); break;
                }
            }
        return r;
    }

    /// An image's file (PNG / JPEG) out of the data; null when it's an outside file.
    public byte[] Image(int image)
    {
        object img = Item("images", image);
        object view = img == null ? null : Item("bufferViews", Json.Int(img, "bufferView"));
        if (view == null) return null;
        int start = Json.Int(view, "byteOffset", 0), length = Json.Int(view, "byteLength", 0);
        if (length <= 0 || start + length > Bin.Length) return null;
        var bytes = new byte[length];
        Buffer.BlockCopy(Bin, start, bytes, 0, length);
        return bytes;
    }
}
