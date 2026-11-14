// Unity's YAML (prefabs, materials, controllers, clips, VRChat's menus): "--- !u!<class> &<fileID>" documents, block
// maps and lists, {flow: maps}, [] lists. Values stay strings; the few things read are read by name.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Vrc
{
    public sealed class UDoc
    {
        public int Class;
        public long Id;
        public string Type;              // "GameObject", "MonoBehaviour"...
        public Dictionary<string, object> Body;
    }

    public static class UYaml
    {
        public static List<UDoc> Parse(string text)
        {
            var docs = new List<UDoc>();
            var lines = text.Replace("\r", "").Split('\n');
            int i = 0;
            while (i < lines.Length)
            {
                string l = lines[i];
                if (!l.StartsWith("--- !u!")) { i++; continue; }
                var d = new UDoc();
                var head = l.Substring(7).Split(' ');
                int.TryParse(head[0], out d.Class);
                if (head.Length > 1 && head[1].StartsWith("&")) long.TryParse(head[1].Substring(1), out d.Id);
                i++;
                if (i >= lines.Length) break;
                string typeLine = lines[i];
                d.Type = typeLine.TrimEnd(':', ' ');
                i++;
                int end = i;
                while (end < lines.Length && !lines[end].StartsWith("--- ")) end++;
                int p = i;
                d.Body = ParseMap(lines, ref p, end, 2);
                docs.Add(d);
                i = end;
            }
            return docs;
        }

        static int Indent(string l)
        {
            int n = 0;
            while (n < l.Length && l[n] == ' ') n++;
            return n;
        }

        static bool Blank(string l) => l.Trim().Length == 0;

        static Dictionary<string, object> ParseMap(string[] lines, ref int i, int end, int indent)
        {
            var map = new Dictionary<string, object>();
            while (i < end)
            {
                string l = lines[i];
                if (Blank(l)) { i++; continue; }
                int ind = Indent(l);
                if (ind < indent) break;
                if (ind > indent) { i++; continue; } // a wrapped scalar's continuation: dropped
                string t = l.Substring(ind);
                if (t.StartsWith("- ")) break;
                int colon = KeyColon(t);
                if (colon < 0) { i++; continue; }
                string key = t.Substring(0, colon).Trim();
                string rest = t.Substring(colon + 1).Trim();
                i++;
                if (rest.Length > 0) map[key] = Scalar(rest, lines, ref i, end, ind);
                else map[key] = Child(lines, ref i, end, ind);
            }
            return map;
        }

        // what follows "key:" on later lines: a list ("- " at the same or deeper indent) or a map (deeper)
        static object Child(string[] lines, ref int i, int end, int keyIndent)
        {
            while (i < end && Blank(lines[i])) i++;
            if (i >= end) return "";
            int ind = Indent(lines[i]);
            string t = lines[i].Substring(ind);
            if (t.StartsWith("- ") || t == "-") return ParseList(lines, ref i, end, ind);
            if (ind > keyIndent) return ParseMap(lines, ref i, end, ind);
            return "";
        }

        static List<object> ParseList(string[] lines, ref int i, int end, int indent)
        {
            var list = new List<object>();
            while (i < end)
            {
                string l = lines[i];
                if (Blank(l)) { i++; continue; }
                int ind = Indent(l);
                if (ind != indent) break;
                string t = l.Substring(ind);
                if (!(t.StartsWith("- ") || t == "-")) break;
                string item = t.Length > 2 ? t.Substring(2) : "";
                int colon = KeyColon(item);
                if (item.StartsWith("{") || item.StartsWith("[") || colon < 0)
                {
                    i++;
                    list.Add(item.Length > 0 ? Scalar(item, lines, ref i, end, ind) : Child(lines, ref i, end, ind));
                    continue;
                }
                // "- key: value" starts a map whose keys sit at indent + 2
                lines[i] = new string(' ', ind + 2) + item;
                list.Add(ParseMap(lines, ref i, end, ind + 2));
            }
            return list;
        }

        static int KeyColon(string t)
        {
            if (t.StartsWith("{") || t.StartsWith("[") || t.StartsWith("'") || t.StartsWith("\"")) return -1;
            for (int k = 0; k < t.Length; k++)
                if (t[k] == ':' && (k + 1 == t.Length || t[k + 1] == ' ')) return k;
            return -1;
        }

        static object Scalar(string s, string[] lines, ref int i, int end, int ind)
        {
            if (s.StartsWith("{")) return Flow(s);
            if (s.StartsWith("[")) return s.Trim() == "[]" ? new List<object>() : (object)FlowList(s);
            if (s.StartsWith("'") || s.StartsWith("\""))
            {
                // quoted, maybe over several lines
                var sb = new StringBuilder(s);
                char q = s[0];
                while (!(sb.Length > 1 && sb[sb.Length - 1] == q) && i < end) sb.Append(' ').Append(lines[i++].Trim());
                string v = sb.ToString();
                return v.Length >= 2 ? v.Substring(1, v.Length - 2).Replace("''", "'") : v;
            }
            return s;
        }

        static Dictionary<string, object> Flow(string s)
        {
            var map = new Dictionary<string, object>();
            s = s.Trim().TrimStart('{').TrimEnd('}');
            foreach (var part in s.Split(','))
            {
                int c = part.IndexOf(':');
                if (c > 0) map[part.Substring(0, c).Trim()] = part.Substring(c + 1).Trim();
            }
            return map;
        }

        static List<object> FlowList(string s)
        {
            var list = new List<object>();
            foreach (var p in s.Trim().TrimStart('[').TrimEnd(']').Split(',')) if (p.Trim().Length > 0) list.Add(p.Trim());
            return list;
        }

        // ---- reading
        public static object At(this object node, string key) => node is Dictionary<string, object> m && m.TryGetValue(key, out var v) ? v : null;
        public static string S(this object node, string key) => node.At(key) as string ?? "";
        public static List<object> L(this object node, string key) => node.At(key) as List<object> ?? new List<object>();
        public static float F(this object node, string key, float or = 0) =>
            float.TryParse(node.S(key), NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : or;
        public static int I(this object node, string key, int or = 0) => int.TryParse(node.S(key), out int v) ? v : or;
        public static long FileId(this object node, string key) => long.TryParse(node.At(key).S("fileID"), out long v) ? v : 0;
        public static string Guid(this object node, string key) => node.At(key).S("guid");
    }
}
