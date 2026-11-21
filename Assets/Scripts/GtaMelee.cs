// GTA V's unarmed melee, as Grand Theft Gmod has it: its moves read from the addon's own data (lua/grandtheftgmod/
// sh_data.lua, D.melee: each blow's clip, when it's chosen (how far the man is, which way, his facing, a chance),
// its priority, its homing, its strike's limbs, its damage, what it can chain into), its clips from male_move.mdl
// (the blows' strike frames from the clip data). Read from your folder as the game runs: nothing of it is kept here.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

public static partial class GtaAnim
{
    public static string VDir = "";

    public class MeleeMove
    {
        public string Id, ClipName, Sb, Branch, Ground, Ko, Roll, Victim;
        public int Prio;
        public float Bias = 1f, DMin, DMax = 2f, AMin = -180f, AMax = 180f, HomDist = 1.4f, HomSpeed = 7.5f, Dmg = 20f, Chance = 1f;
        public bool Heavy, Kill, NoRun, Run, Still, HasFacing;
        public Vector2 Facing;
        public Clip C;
        public bool Ok => C != null && C.Ok;
    }

    // the lists: aimed at a man (targeted), the chains' next blows (combo), on a man down (ground), and at no one
    public static readonly List<MeleeMove> MeleeTargeted = new List<MeleeMove>(), MeleeCombo = new List<MeleeMove>(), MeleeGround = new List<MeleeMove>();
    public static readonly Dictionary<string, MeleeMove> MeleeNoTarget = new Dictionary<string, MeleeMove>();
    public static readonly Dictionary<string, string[]> MeleeBranches = new Dictionary<string, string[]>();
    // a strike set: its points (1..10) and each one's reach (m)
    public static readonly Dictionary<string, (int point, float r)[]> MeleeStrikes = new Dictionary<string, (int, float)[]>();
    public static bool Melee => MeleeTargeted.Count > 0 || MeleeNoTarget.Count > 0;

    static readonly Regex kField = new Regex(@"(\w+)\s*=\s*(""[^""]*""|\{[^{}]*\}|-?[\d.]+|true|false)");

    static void LoadMelee()
    {
        string file = Path.Combine(VDir, "lua", "grandtheftgmod", "sh_data.lua");
        if (!File.Exists(file)) { Debug.Log("[Hero] GTA V's melee: no " + file); return; }
        var lines = File.ReadAllLines(file);
        int at = Array.FindIndex(lines, l => l.StartsWith("D.melee = {"));
        if (at < 0) return;
        string section = "", sub = "";
        var cache = new Dictionary<string, Clip>();
        for (int i = at + 1; i < lines.Length; i++)
        {
            string l = lines[i];
            if (l.StartsWith("}")) break;
            // (a line that goes on: joined till its braces close)
            while (Depth(l) > 0 && !Regex.IsMatch(l, @"=\s*\{\s*$") && i + 1 < lines.Length && !lines[i + 1].StartsWith("}")) l += " " + lines[++i].Trim();
            // its sections: a tab in ("\ttargeted = {"), and theirs two in ("\t\tmoves = {")
            var head = Regex.Match(l, @"^\t(\w+) = \{\s*$");
            if (head.Success) { section = head.Groups[1].Value; sub = ""; continue; }
            if (section == "swim") continue; // (in the water: none here)
            var head2 = Regex.Match(l, @"^\t\t(\w+) = \{\s*$");
            if (head2.Success) { sub = head2.Groups[1].Value; continue; }
            string t = l.Trim();
            if (section == "branches")
            {
                var b = Regex.Match(t, @"^(\w+) = \{(.*)\},?$");
                if (b.Success)
                {
                    var ids = new List<string>();
                    foreach (Match q in Regex.Matches(b.Groups[2].Value, "\"([^\"]+)\"")) ids.Add(q.Groups[1].Value);
                    MeleeBranches[b.Groups[1].Value] = ids.ToArray();
                }
                continue;
            }
            if (section == "strike_sets")
            {
                foreach (Match q in Regex.Matches(t, @"(\w+) = \{ ((?:\{ \d+, [\d.]+ \},? ?)+)\}"))
                {
                    var pts = new List<(int, float)>();
                    foreach (Match p in Regex.Matches(q.Groups[2].Value, @"\{ (\d+), ([\d.]+) \}"))
                        pts.Add((int.Parse(p.Groups[1].Value), float.Parse(p.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)));
                    MeleeStrikes[q.Groups[1].Value] = pts.ToArray();
                }
                continue;
            }
            if (!t.Contains("clip = \"")) continue;
            // a move: "{ id = ..., clip = ..., ... }" (or "still = { clip = ... }" in no_target)
            string key = null;
            var named = Regex.Match(t, @"^(\w+) = \{ clip");
            if (named.Success) { key = named.Groups[1].Value; t = t.Substring(t.IndexOf('{') + 1); }
            var m = new MeleeMove();
            foreach (Match f in kField.Matches(t))
            {
                string k = f.Groups[1].Value, v = f.Groups[2].Value;
                string str = v.StartsWith("\"") ? v.Trim('"') : null;
                float num = 0f;
                bool isNum = !v.StartsWith("\"") && !v.StartsWith("{") && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out num);
                float[] pair = v.StartsWith("{") ? Nums(v) : null;
                switch (k)
                {
                    case "id": m.Id = str; break;
                    case "clip": m.ClipName = str; break;
                    case "sb": m.Sb = str; break;
                    case "branch": m.Branch = str; break;
                    case "ground": m.Ground = str; break;
                    case "ko": m.Ko = str; break;
                    case "roll": m.Roll = str; break;
                    case "victim": m.Victim = str; break;
                    case "prio": if (isNum) m.Prio = (int)num; break;
                    case "bias": if (isNum) m.Bias = num; break;
                    case "dmin": if (isNum) m.DMin = num; break;
                    case "dmax": if (isNum) m.DMax = num; break;
                    case "amin": if (isNum) m.AMin = num; break;
                    case "amax": if (isNum) m.AMax = num; break;
                    case "dmg": if (isNum) m.Dmg = num; break;
                    case "chance": if (isNum) m.Chance = num; break;
                    case "heavy": m.Heavy = v == "true"; break;
                    case "kill": m.Kill = v == "true"; break;
                    case "no_run": m.NoRun = v == "true"; break;
                    case "run": m.Run = v == "true"; break;
                    case "still": m.Still = v == "true"; break;
                    case "hom": if (pair != null && pair.Length >= 2) { m.HomDist = pair[0]; m.HomSpeed = pair[1]; } break;
                    case "facing": if (pair != null && pair.Length >= 2) { m.Facing = new Vector2(pair[0], pair[1]); m.HasFacing = true; } break;
                }
            }
            if (m.ClipName == null) continue;
            if (!cache.TryGetValue(m.ClipName, out var c)) cache[m.ClipName] = c = GetV("gtgl_male_move_" + m.ClipName, false);
            m.C = c;
            if (!m.Ok) continue;
            if (m.Id == null) m.Id = key ?? m.ClipName;
            if (section == "no_target" && key != null) MeleeNoTarget[key] = m;
            else if (section == "targeted") MeleeTargeted.Add(m);
            else if (section == "combo") MeleeCombo.Add(m);
            else if (section == "ground" && sub == "moves") MeleeGround.Add(m);
        }
        int strikes = 0;
        foreach (var c in cache.Values) if (c.Ok && c.Strike != null) strikes++;
        Debug.Log($"[Hero] GTA V's melee: {MeleeTargeted.Count} aimed blows, {MeleeCombo.Count} in chains, {MeleeGround.Count} on the ground, {MeleeNoTarget.Count} at no one, {MeleeBranches.Count} chains, {MeleeStrikes.Count} strike sets; {strikes}/{cache.Count} clips with their strike");
    }

    static int Depth(string l)
    {
        int d = 0;
        foreach (char ch in l) { if (ch == '{') d++; else if (ch == '}') d--; }
        return d;
    }

    static float[] Nums(string braces)
    {
        var list = new List<float>();
        foreach (var part in braces.Trim('{', '}', ' ').Split(','))
            if (float.TryParse(part.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f)) list.Add(f);
        return list.ToArray();
    }

    /// A move by its id in the aimed blows or the chains'
    public static MeleeMove MeleeById(string id)
    {
        foreach (var m in MeleeTargeted) if (m.Id == id) return m;
        foreach (var m in MeleeCombo) if (m.Id == id) return m;
        return null;
    }
}
