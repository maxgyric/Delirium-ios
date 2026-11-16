// GTA IV's own clips (walk, run, strafe, jump, aim, get up) on the mannequin: delirium_anim (the library made for
// CS Delirium) reads them at run time from your own game (xbox360.rpf, xbox360\anim\anim.img) and turns each pose
// onto the skeleton it's given. Nothing of the game is in the project.
//
// The library works in a right-handed, Z-up space with the model facing +X; Unity is left-handed, Y up. A point
// (x, y, z) there is (x, z, y) here, a rotation (x, y, z, w) is (-x, -z, -y, w): the same swap Delirium's own
// Unity package makes.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using UnityEngine;

public static class GtaAnim
{
#if UNITY_IOS && !UNITY_EDITOR
    const string Dll = "__Internal";
#else
    const string Dll = "delirium_anim";
#endif
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int da_open(byte[] gameDir);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int da_error(byte[] buf, int len);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int da_clip(byte[] dict, byte[] name);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int da_dicts(byte[] filter, byte[] buf, int len);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int da_dict_clips(byte[] dict, byte[] buf, int len);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int da_clip_info(int c, out float duration, out float speed);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern float da_clip_sync(int c);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int da_target(int n, IntPtr[] names, int[] parents, float[] local, byte[] buf, int len);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int da_pose(int c, float phase, float[] o, int[] moved);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int da_pose_root(int c, float phase, float[] o, int[] moved);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int da_pose_seat(int c, float phase, float[] o, int[] moved);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int da_pose_ground(int c, float phase, float[] o, int[] moved);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int dv_open(byte[] dir);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int dv_clip(byte[] name);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int dv_clip_data(int c, int what, float[] o, int cap);

    static byte[] Z(string s) => Encoding.UTF8.GetBytes(s + "\0");

    public class Clip
    {
        public int Id = -1;
        public float Dur = 1f, Speed, Sync;
        public bool Cycle;
        public bool Ok => Id >= 0;
        // (GTA V's) its root's way over the clip (x forward, y left m, turn left deg; samples evenly over it) and
        // its marks (kind, phase pairs: 1 left heel down, 2 right heel down, 3 where it blends out)
        public float[] Curve, Tags;
        // (GTA V's climbs) its root's rise over the clip (m, sampled as Curve); posed about the model's ground (the
        // rise and the way along are the clip data's: Hero moves him by them)
        public float[] Rise;
        public bool Ground;
        public int Frames => Mathf.RoundToInt(Dur * 30f) + 1;
    }

    /// 0 not started, 1 loading, 2 ready, -1 failed (Error says why).
    public static volatile int State;
    public static string Error = "";
    public static int Bones;

    // move[gait]: idle, walk, run, sprint; strafe[0 walk / 1 run][8 ways from ahead, turning left]; jump: take off,
    // in the air, land, on the spot; aim[0 handgun / 1 rifle]; get up: from his front, from his back
    public static readonly Clip[] Move = new Clip[4], MoveRifle = new Clip[4];
    public static readonly Clip[,] Strafe = new Clip[2, 8];
    public static Clip StrafeIdle = new Clip();
    public static readonly Clip[] Jump = new Clip[4], JumpRifle = new Clip[4];
    public static readonly Clip[] Aim = new Clip[6]; // handgun, deagle, mp5k, ak47, shotgun, rifle (the sniper's)
    public static Clip GetUpFront = new Clip(), GetUpBack = new Clip();
    /// GTA V's moves (Grand Theft Gmod's male_move.mdl, made from your GTA V by its exporter, and its clip data) in
    /// place of GTA IV's when they're on the phone: the idle, walk, run and sprint, the jump's take-off and the fall.
    public static bool V;
    public static string VError = "";
    public static Clip VLaunch = new Clip(), VFall = new Clip();
    // starts from standing (walk / run): left foot first straight, turning left 90, 180; right foot first straight,
    // turning right 90, 180
    public static readonly Clip[] VWStart = new Clip[6], VRStart = new Clip[6];
    // stops (walk / run): on the left foot, on the right; the run's 180 turn left, right
    public static readonly Clip[] VWStop = new Clip[2], VRStop = new Clip[2], VRunTurn = new Clip[2];
    // landings: running on (left, right foot), stopping (left, right)
    public static readonly Clip[] VLand = new Clip[2], VLandStop = new Clip[2];

    /// Where a GTA V clip has carried its root by `phase`: x forward, y left (m), z its turn left (degrees), from
    /// where it started.
    public static Vector3 Motion(Clip c, float phase)
    {
        var k = c?.Curve;
        if (k == null || k.Length < 3) return Vector3.zero;
        int n = k.Length / 3;
        // a sample every third frame (the addon's clip data; the last one may fall short of the end)
        float x = Mathf.Min(Mathf.Clamp01(phase) * (c.Frames - 1) / 3f, n - 1);
        int i = Mathf.Min((int)x, n - 1), j = Mathf.Min(i + 1, n - 1);
        float f = x - i;
        return new Vector3(Mathf.Lerp(k[i * 3], k[j * 3], f), Mathf.Lerp(k[i * 3 + 1], k[j * 3 + 1], f), Mathf.Lerp(k[i * 3 + 2], k[j * 3 + 2], f));
    }

    /// How high a GTA V climb has lifted its root by `phase` (m)
    public static float Rise(Clip c, float phase)
    {
        var k = c?.Rise;
        if (k == null || k.Length == 0) return 0f;
        float x = Mathf.Min(Mathf.Clamp01(phase) * (c.Frames - 1) / 3f, k.Length - 1);
        int i = Mathf.Min((int)x, k.Length - 1), j = Mathf.Min(i + 1, k.Length - 1);
        return Mathf.Lerp(k[i], k[j], x - i);
    }

    /// GTA V's climbs, vaults and their ends, by the addon's name ("standclimbup_80", "clamberpose_stand")
    public static readonly Dictionary<string, Clip> VClimb = new Dictionary<string, Clip>();
    public static Clip Climb(string name) => name != null && VClimb.TryGetValue(name, out var c) && c.Ok ? c : null;

    /// A gun's GTA V clips (male_guns.mdl): its aim, its shot (the kick) and its raise, each looking up, ahead and
    /// down [0 high, 1 med, 2 low]
    public class GunSet
    {
        public readonly Clip[] Aim = new Clip[3], Fire = new Clip[3], Raise = new Clip[3];
    }
    public static readonly Dictionary<string, GunSet> VGuns = new Dictionary<string, GunSet>();

    /// A GTA V clip's first mark of `kind` (1 left heel, 2 right heel, 3 blend-out), or -1
    public static float Mark(Clip c, int kind, float after = -1f)
    {
        var t = c?.Tags;
        if (t == null) return -1f;
        for (int i = 0; i + 1 < t.Length; i += 2)
            if ((int)t[i] == kind && t[i + 1] > after) return t[i + 1];
        return -1f;
    }

    /// The last heel down at or before `phase` (1 left, 2 right) and its phase; 0 when none
    public static int LastHeel(Clip c, float phase, out float at)
    {
        at = 0f;
        var t = c?.Tags;
        int side = 0;
        float best = -1f;
        if (t == null) return 0;
        for (int i = 0; i + 1 < t.Length; i += 2)
        {
            int k = (int)t[i];
            if ((k == 1 || k == 2) && t[i + 1] <= phase && t[i + 1] >= best) { best = t[i + 1]; side = k; }
        }
        at = Mathf.Max(0f, best);
        return side;
    }
    /// The game's unarmed blows (found by name among its melee dictionaries; none: the hero's punch is a plain jab).
    public static readonly List<Clip> Punches = new List<Clip>();

    static string[] Names(byte[] buf)
    {
        int n = Array.IndexOf(buf, (byte)0);
        string s = Encoding.UTF8.GetString(buf, 0, n < 0 ? buf.Length : n);
        return s.Length == 0 ? new string[0] : s.Split(',');
    }

    // The melee dictionaries' clips that are a punch by their name; the lists go to the log (to pick better ones).
    static void FindPunches()
    {
        var buf = new byte[32768];
        if (da_dicts(Z("melee"), buf, buf.Length) < 0) return;
        var dicts = new List<string>(Names(buf));
        Debug.Log("[Hero] melee dictionaries: " + string.Join(", ", dicts.ToArray()));
        dicts.Sort((a, b) => (a.Contains("unarmed") ? 0 : 1).CompareTo(b.Contains("unarmed") ? 0 : 1));
        string[] words = { "punch", "jab", "cross", "hook", "uppercut" };
        foreach (var d in dicts)
        {
            if (Punches.Count >= 4) break;
            Array.Clear(buf, 0, buf.Length);
            if (da_dict_clips(Z(d), buf, buf.Length) < 0) continue;
            var clips = Names(buf);
            Debug.Log("[Hero] " + d + ": " + string.Join(", ", clips));
            foreach (var c in clips)
            {
                string low = c.ToLowerInvariant();
                bool punch = false;
                foreach (var w in words) punch = punch || low.Contains(w);
                if (!punch || low.Contains("hit_") || low.Contains("react") || low.Contains("block") || low.Contains("dodge")) continue;
                var clip = Get(d, c);
                if (clip.Ok && clip.Dur > 0.2f && clip.Dur < 2.5f) Punches.Add(clip);
                if (Punches.Count >= 4) break;
            }
        }
        Debug.Log("[Hero] punches found: " + Punches.Count);
    }

    static string LastError()
    {
        var buf = new byte[512];
        da_error(buf, buf.Length);
        int n = Array.IndexOf(buf, (byte)0);
        return Encoding.UTF8.GetString(buf, 0, n < 0 ? buf.Length : n);
    }

    static Clip Get(string dict, string name, bool cycle = false)
    {
        var c = new Clip { Cycle = cycle };
        c.Id = da_clip(Z(dict), Z(name));
        if (c.Id < 0)
        {
            Debug.LogWarning("[Hero] no " + dict + "/" + name + ": " + LastError());
            return c;
        }
        da_clip_info(c.Id, out c.Dur, out c.Speed);
        c.Dur = Mathf.Max(c.Dur, 0.05f);
        if (cycle) c.Sync = Mathf.Max(0f, da_clip_sync(c.Id));
        return c;
    }

    /// Opens the game's clips on a thread: `folders` tried in turn (the first that has the archives).
    public static void Load(string[] folders)
    {
        if (State == 1 || State == 2) return;
        State = 1;
        Error = "";
        // the mannequin's skeleton, as the library wants it (pure numbers: fine off the main thread)
        Mannequin.Skeleton(out string[] names, out int[] parents, out float[] local);
        var t = new Thread(() =>
        {
            try
            {
                bool open = false;
                string why = "";
                foreach (var f in folders)
                {
                    if (string.IsNullOrEmpty(f) || !Directory.Exists(f)) continue;
                    if (da_open(Z(f)) >= 0) { open = true; break; }
                    why += f + ": " + LastError() + "\n";
                }
                if (!open)
                {
                    Error = why != "" ? why.Trim() : "no GTA IV folder with xbox360.rpf and xbox360\\anim\\anim.img";
                    State = -1;
                    return;
                }
                var ptrs = new IntPtr[names.Length];
                for (int i = 0; i < names.Length; i++) ptrs[i] = Marshal.StringToHGlobalAnsi(names[i]);
                var report = new byte[1024];
                int moving = da_target(names.Length, ptrs, parents, local, report, report.Length);
                foreach (var p in ptrs) Marshal.FreeHGlobal(p);
                if (moving < 0)
                {
                    Error = LastError();
                    State = -1;
                    return;
                }
                Bones = names.Length;
                string[] gaits = { "idle", "walk", "run", "sprint" };
                for (int g = 0; g < 4; g++) Move[g] = Get("move_player", gaits[g], g != 0);
                for (int g = 0; g < 4; g++) MoveRifle[g] = Get("move_rifle", gaits[g], g != 0);
                string[] dirs = { "", "_strafe_l45", "_strafe_l", "_strafe_bl45", "_strafe_b", "_strafe_br45", "_strafe_r", "_strafe_r45" };
                for (int k = 0; k < 8; k++)
                {
                    Strafe[0, k] = Get("move_combat_strafe", "walk" + dirs[k], true);
                    Strafe[1, k] = Get("move_combat_strafe", "run" + dirs[k], true);
                }
                StrafeIdle = Get("move_combat_strafe", "idle");
                Jump[0] = Get("jump_std", "jump_takeoff_l");
                Jump[1] = Get("jump_std", "jump_inair_l");
                Jump[2] = Get("jump_std", "jump_land_l");
                Jump[3] = Get("jump_std", "jump_on_spot");
                JumpRifle[0] = Get("jump_rifle", "jump_takeoff_l");
                JumpRifle[1] = Get("jump_rifle", "jump_inair_l");
                JumpRifle[2] = Get("jump_rifle", "jump_land_l");
                JumpRifle[3] = Get("jump_rifle", "jump_on_spot");
                string[] guns = { "gun@handgun", "gun@deagle", "gun@mp5k", "gun@ak47", "gun@shotgun", "gun@rifle" };
                for (int g = 0; g < guns.Length; g++) Aim[g] = Get(guns[g], "holster_2_aim");
                GetUpFront = Get("get_up", "get_up_fast");
                GetUpBack = Get("get_up_back", "get_up_fast");
                try { FindPunches(); } catch (Exception e) { Debug.LogWarning("[Hero] punches: " + e.Message); }
                try { LoadV(folders); } catch (Exception e) { VError = e.Message; }
                int n = Array.IndexOf(report, (byte)0);
                Debug.Log("[Hero] GTA IV's clips ready: " + Encoding.UTF8.GetString(report, 0, n < 0 ? 0 : n));
                State = Move[0].Ok && Move[1].Ok ? 2 : -1;
                if (State < 0) Error = "the game's move_player clips didn't load";
            }
            catch (DllNotFoundException)
            {
                Error = "delirium_anim.dll is missing: run build-anim.bat in the project's folder, then start again";
                State = -1;
            }
            catch (Exception e)
            {
                Error = e.Message;
                State = -1;
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    static Clip GetV(string seq, bool cycle)
    {
        var c = new Clip { Cycle = cycle };
        c.Id = dv_clip(Z(seq));
        if (c.Id < 0)
        {
            Debug.LogWarning("[Hero] GTA V: no " + seq + ": " + LastError());
            return c;
        }
        da_clip_info(c.Id, out c.Dur, out c.Speed);
        c.Dur = Mathf.Max(c.Dur, 0.05f);
        if (cycle) c.Sync = Mathf.Max(0f, da_clip_sync(c.Id));
        var buf = new float[4096];
        int n = dv_clip_data(c.Id, 0, buf, buf.Length);
        if (n > 0) { c.Curve = new float[n]; Array.Copy(buf, c.Curve, n); }
        n = dv_clip_data(c.Id, 1, buf, buf.Length);
        if (n > 0) { c.Tags = new float[n]; Array.Copy(buf, c.Tags, n); }
        n = dv_clip_data(c.Id, 2, buf, buf.Length);
        if (n > 0) { c.Rise = new float[n]; Array.Copy(buf, c.Rise, n); }
        return c;
    }

    // Grand Theft Gmod's files: male_move.mdl and lib_male_move*.lua beside default.xex, or the addon's folder
    // (grandtheftgmod or grandtheftgmod_0899) there as it is
    static void LoadV(string[] folders)
    {
        string why = "";
        foreach (var f in folders)
        {
            if (string.IsNullOrEmpty(f) || !Directory.Exists(f)) continue;
            foreach (var d in new[] { f, Path.Combine(f, "grandtheftgmod_0899"), Path.Combine(f, "grandtheftgmod") })
            {
                if (!Directory.Exists(d)) continue;
                try
                {
                    if (dv_open(Z(d)) >= 0) { V = true; break; }
                }
                catch (EntryPointNotFoundException) { VError = "this build's library has no GTA V clips"; return; }
                why += d + ": " + LastError() + "\n";
            }
            if (V) break;
        }
        if (!V) { VError = why != "" ? why.Trim() : "no male_move.mdl"; return; }
        var mv = new[] { GetV("gtgl_male_move_idle", false), GetV("gtgl_male_move_walk", true), GetV("gtgl_male_move_run", true), GetV("gtgl_male_move_sprint", true) };
        if (!(mv[0].Ok && mv[1].Ok && mv[2].Ok)) { V = false; VError = "male_move.mdl: no idle / walk / run"; return; }
        for (int g = 0; g < 4; g++) if (mv[g].Ok) Move[g] = mv[g];
        VLaunch = GetV("gtgl_male_move_jump_launch_l", false);
        VFall = GetV("gtgl_male_move_fall_med", true);
        if (VFall.Ok) Jump[1] = VFall;
        string[] starts = { "l_0", "l_m90", "l_m180", "r_0", "r_90", "r_180" };
        for (int i = 0; i < 6; i++)
        {
            VWStart[i] = GetV("gtgl_male_move_wstart_" + starts[i], false);
            VRStart[i] = GetV("gtgl_male_move_rstart_" + starts[i], false);
        }
        VWStop[0] = GetV("gtgl_male_move_wstop_l_0", false);
        VWStop[1] = GetV("gtgl_male_move_wstop_r_0", false);
        VRStop[0] = GetV("gtgl_male_move_rstop_l", false);
        VRStop[1] = GetV("gtgl_male_move_rstop_r", false);
        VLand[0] = GetV("gtgl_male_move_jump_land_l", false);
        VLand[1] = GetV("gtgl_male_move_jump_land_r", false);
        if (!VLand[1].Ok) VLand[1] = VLand[0];
        if (!VLand[0].Ok) VLand[0] = VLand[1].Ok ? VLand[1] : null;
        if (VLand[0] == null) VLand[1] = null;
        VLandStop[0] = GetV("gtgl_male_move_land_stop_l", false);
        VLandStop[1] = GetV("gtgl_male_move_land_stop_r", false);
        if (!VLandStop[1].Ok) VLandStop[1] = VLandStop[0];
                VRunTurn[0] = GetV("gtgl_male_move_run_turn_180_l", false);
        VRunTurn[1] = GetV("gtgl_male_move_run_turn_180_r", false);
        // the strafes while aiming (8 ways from ahead turning left, walk and run) and the stance
        string[] ways = { "fwd_0", "fwd_m45", "bwd_m90", "bwd_m135", "bwd_180", "bwd_135", "fwd_90", "fwd_45" };
        var sw = new Clip[2, 8];
        bool strafes = true;
        for (int g = 0; g < 2; g++)
            for (int k = 0; k < 8; k++)
            {
                sw[g, k] = GetV("gtgl_male_move_mstrafe_" + (g == 0 ? "walk_" : "run_") + ways[k] + "_loop", true);
                strafes &= sw[g, k].Ok;
            }
        var si = GetV("gtgl_male_move_mstrafe_idle", true);
        if (strafes && si.Ok)
        {
            for (int g = 0; g < 2; g++) for (int k = 0; k < 8; k++) Strafe[g, k] = sw[g, k];
            StrafeIdle = si;
        }
        // climbs and vaults, and how they end (Hero's climb)
        string[] climbs =
        {
            "standclimbup_20", "standclimbup_80", "standclimbup_80_low", "standclimbup_140_low", "standclimbup_140_high", "standclimbup_180_low",
            "standclimbup_180_high", "standclimbup_220", "standclimbup_220_high", "standclimbup_295_low", "standclimbupclose_20", "standclimbupclose_80_low",
            "runclimbup_80", "runclimbup_140_low", "runclimbup_140_high", "runclimbup_180_low", "runclimbup_180_high", "runclimbup_220",
            "runclimbup_220_high", "runclimbup_295_low",
            "run_climbup_20_l", "run_climbup_80_l", "run_climbup_20_r", "run_climbup_80_r",
            "run_climbup_20_l_close", "run_climbup_80_l_close", "run_climbup_20_r_close", "run_climbup_80_r_close",
            "standvault_20", "standvault_80", "standvaultclose_20", "standvaultclose_80",
            "standclimbup_exit", "standclimbup_exit_walk", "run_climbup_exit_l", "run_climbup_exit_r",
            "standvault_exit", "run_vault_exit_l", "run_vault_exit_r",
            "clamberpose_stand", "clamberpose_walk", "clamberpose_run",
            "clamberpose_fall", "clamberpose_fall_wide", "clamberpose_fall_xtrawide", "clamberpose_fall_low",
        };
        foreach (var n in climbs)
        {
            var c = GetV("gtgl_male_move_" + n, false);
            c.Ground = true;
            if (c.Ok) VClimb[n] = c;
        }
        // the guns' aims and shots (male_guns.mdl beside male_move.mdl)
        foreach (var g in new[] { "wpi", "wp5", "wsm", "war", "wpu", "wsr" })
        {
            var set = new GunSet();
            string[] pitch = { "high", "med", "low" };
            for (int k = 0; k < 3; k++)
            {
                set.Aim[k] = GetV("gtgl_male_guns_" + g + "_aim_" + pitch[k] + "_loop", false);
                set.Fire[k] = GetV("gtgl_male_guns_" + g + "_fire_" + pitch[k], false);
                set.Raise[k] = GetV("gtgl_male_guns_" + g + "_idle_2_aim_fwd_" + pitch[k], false);
            }
            if (set.Aim[1].Ok) VGuns[g] = set;
        }
        Debug.Log($"[Hero] GTA V's moves: walk {Move[1].Speed:F2} m/s, run {Move[2].Speed:F2}, sprint {Move[3].Speed:F2}; strafes {(strafes && si.Ok ? "yes" : "no")}, {VClimb.Count} climb clips, guns: {string.Join(",", VGuns.Keys)}");
    }

    /// One bone's frame in the model's space (Unity's axes, the model facing +X).
    public struct Frame
    {
        public Vector3 P;
        public Quaternion R;
    }

    static float[] raw;
    static int[] moved;

    /// Clip `c` at `phase` into `into` (a frame a bone, the mannequin's order); `root`: carried over the ground by
    /// the clip (a get-up). False if the library refuses.
    public static bool Pose(Clip c, float phase, Frame[] into, bool root = false)
    {
        if (State != 2 || c == null || !c.Ok) return false;
        if (raw == null || raw.Length != Bones * 7)
        {
            raw = new float[Bones * 7];
            moved = new int[Bones];
        }
        phase = Mathf.Clamp01(phase);
        int r;
        if (c.Ground && !noGround)
        {
            try { r = da_pose_ground(c.Id, phase, raw, moved); }
            catch (EntryPointNotFoundException) { noGround = true; r = da_pose(c.Id, phase, raw, moved); }
        }
        else r = root ? da_pose_root(c.Id, phase, raw, moved) : da_pose(c.Id, phase, raw, moved);
        if (r < 0) return false;
        for (int b = 0; b < Bones && b < into.Length; b++)
        {
            int k = b * 7;
            into[b].P = new Vector3(raw[k], raw[k + 2], raw[k + 1]);
            into[b].R = new Quaternion(-raw[k + 3], -raw[k + 5], -raw[k + 4], raw[k + 6]);
        }
        return true;
    }

    /// A clip of `dict` (a bike's veh@bike_spt...), loaded when first asked for (the hero's are loaded at start).
    public static Clip Vehicle(string dict, string name, bool cycle = false)
    {
        if (State != 2) return new Clip();
        string k = dict + "/" + name;
        if (vehicleClips.TryGetValue(k, out var c)) return c;
        c = Get(dict, name, cycle);
        vehicleClips[k] = c;
        return c;
    }

    static readonly Dictionary<string, Clip> vehicleClips = new Dictionary<string, Clip>();
    static bool noSeat, noGround;

    /// As Pose, about the clip's own root (in a vehicle: its seat) and not stood on its feet: the pelvis where the
    /// clip has it from the seat. A library without it (an old desktop build): false.
    public static bool PoseSeat(Clip c, float phase, Frame[] into)
    {
        if (State != 2 || c == null || !c.Ok || noSeat) return false;
        if (raw == null || raw.Length != Bones * 7)
        {
            raw = new float[Bones * 7];
            moved = new int[Bones];
        }
        int r;
        try { r = da_pose_seat(c.Id, Mathf.Clamp01(phase), raw, moved); }
        catch (EntryPointNotFoundException) { noSeat = true; return false; }
        if (r < 0) return false;
        for (int b = 0; b < Bones && b < into.Length; b++)
        {
            int k = b * 7;
            into[b].P = new Vector3(raw[k], raw[k + 2], raw[k + 1]);
            into[b].R = new Quaternion(-raw[k + 3], -raw[k + 5], -raw[k + 4], raw[k + 6]);
        }
        return true;
    }

    /// A cycle's phase lined up by its step (every gait's clips step together); a one-off's as it is.
    public static float At(Clip c, float phase)
    {
        if (!c.Cycle) return phase;
        float p = phase + c.Sync;
        return p - Mathf.Floor(p);
    }

    public static void Blend(Frame[] a, Frame[] b, float w)
    {
        for (int i = 0; i < a.Length; i++)
        {
            a[i].P = Vector3.Lerp(a[i].P, b[i].P, w);
            a[i].R = Quaternion.Slerp(a[i].R, b[i].R, w);
        }
    }

    /// Two clips of a gait (in order of speed) around the speed `ms`, weighted by it; `rate` the blend's cycles a
    /// second, its stride matched to the speed within 0.45-1.25 of the clips' own (beyond: a cartoon's steps).
    public static Clip Gait(IList<Clip> list, float ms, out float rate, out Clip b, out float w)
    {
        b = null;
        w = 0f;
        rate = 1f;
        var v = new List<Clip>();
        foreach (var c in list)
            if (c != null && c.Ok && c.Speed > 0.1f) v.Add(c);
        if (v.Count == 0) return null;
        Clip a = ms <= v[0].Speed ? v[0] : v[v.Count - 1];
        for (int i = 0; i + 1 < v.Count; i++)
            if (ms > v[i].Speed && ms < v[i + 1].Speed)
            {
                a = v[i];
                b = v[i + 1];
                w = (ms - a.Speed) / (b.Speed - a.Speed);
                break;
            }
        float sp = b != null ? a.Speed + (b.Speed - a.Speed) * w : a.Speed;
        float cyc = b != null ? 1f / a.Dur + (1f / b.Dur - 1f / a.Dur) * w : 1f / a.Dur;
        rate = cyc * Mathf.Clamp(ms / sp, 0.45f, 1.25f);
        return a;
    }
}
