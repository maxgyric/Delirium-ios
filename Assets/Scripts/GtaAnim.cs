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

    static byte[] Z(string s) => Encoding.UTF8.GetBytes(s + "\0");

    public class Clip
    {
        public int Id = -1;
        public float Dur = 1f, Speed, Sync;
        public bool Cycle;
        public bool Ok => Id >= 0;
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
        int r = root ? da_pose_root(c.Id, phase, raw, moved) : da_pose(c.Id, phase, raw, moved);
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
    static bool noSeat;

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
