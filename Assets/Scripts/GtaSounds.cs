// GTA IV's own sounds, read from your game's audio files on the phone (audio.rpf next to default.xex, and
// xbox360/audio/sfx/resident.rpf and streamed_vehicles.rpf), in place of the made-up ones: the guns' shots, punches,
// footsteps and landings, a body hitting the ground, the grenade's blast, and each bike's engine (its idle, low and
// high loops, engine and exhaust, crossfaded and pitched by the gearbox's revs), its start, landing, skid and crash.
// What a sound is made of comes from the game's own sounds.dat15 (native/sound): which waves play together, which one
// of several, their volumes and pitches. Without the files the made-up sounds stay.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

public partial class Game
{
    [DllImport(BikeDll)] static extern int ds_open(byte[] dir);
    [DllImport(BikeDll)] static extern int ds_error(byte[] text, int cap);
    [DllImport(BikeDll)] static extern int ds_sound(byte[] name, int[] tree, int cap);
    [DllImport(BikeDll)] static extern int ds_game_sound(byte[] obj, int at, int[] tree, int cap);
    [DllImport(BikeDll)] static extern int ds_wave(int n, int[] info);
    [DllImport(BikeDll)] static extern int ds_pcm(int n, short[] pcm, int cap);
    [DllImport(BikeDll)] static extern void ds_close();

    /// A sound's tree: a wave (0), all its children together (1), one of them (2); each with its gain and pitch.
    class GNode
    {
        public int Kind, Wave;
        public float Gain = 1f, Pitch = 1f;
        public GNode[] Kids;
    }

    // Each made-up sound's GTA IV one: a game.dat16 object's field (the game's own choice for it), or a sound's name
    static readonly (Sfx what, string obj, int at, string name)[] kGtaSfx =
    {
        (Sfx.Step, "FOOTSTEPS_CONCRETE_CLEAN", 32, null),   // a leather shoe's heel on clean concrete
        (Sfx.Jump, "FOOTSTEPS_CONCRETE_CLEAN", 40, null),   // its scuff
        (Sfx.Land, "FOOTSTEPS_CONCRETE_CLEAN", 48, null),   // landing from a jump
        (Sfx.Impact, null, 0, "BODYFALL_BODY"),
        (Sfx.Punch, "MELEE_COMBAT_JAB", 18, null),          // a fist's hit
        (Sfx.Boom, null, 0, "GRENADE"),
        (Sfx.Pistol, "W_GLOCK", 10, null),                  // each gun's shot (its near side)
        (Sfx.Deagle, null, 0, "XFADE_GUN_TEST_DESERT"),
        (Sfx.Smg, "W_UZI", 10, null),
        (Sfx.Ak47, "W_AK47", 10, null),
        (Sfx.Shotgun, "W_PUMPSHOT", 10, null),
        (Sfx.Sniper, "W_RIFLE", 10, null),
    };
    // a bike's (game.dat16 vehicle object): engine low, high, exhaust low, high, revs off, idle engine, idle exhaust,
    // start, landing, ignition
    static readonly int[] kBikeFields = { 18, 22, 26, 30, 34, 146, 150, 206, 298, 310 };
    const int kEngLow = 0, kEngHigh = 1, kExLow = 2, kExHigh = 3, kRevsOff = 4, kIdleEng = 5, kIdleEx = 6, kStart = 7, kBikeLand = 8, kIgnition = 9;
    static readonly Dictionary<string, string> kBikeAudio = new Dictionary<string, string>
    {
        { "pcj", "PCJ" }, { "nrg900", "NRG900" }, { "sanchez", "SANCHEZ" }, { "faggio", "FAGGIO" },
        { "hellfury", "HELLFURY" }, { "bobber", "BOBBER" }, { "zombieb", "ZOMBIEB" },
    };

    GNode[] gtaSfx;
    GNode gtaPunchKnuckle, gtaBikeCrash, gtaSkid;
    readonly Dictionary<string, GNode[]> gtaBikes = new Dictionary<string, GNode[]>();
    AudioClip[] gtaClips;
    bool[] gtaLoops;
    volatile int gtaSoundState;   // 0 not yet, 1 reading, 2 read (clips to make), 3 ready, -1 none
    string gtaSoundError = "";
    float[][] gtaPcm;
    int[] gtaRate;

    // ------------------------------------------------------------------ reading them
    void GtaSoundsLoad()
    {
        if (gtaSoundState != 0) return;
        gtaSoundState = 1;
        string exeDir = Directory.Exists(gameExe) ? gameExe : Path.GetDirectoryName(gameExe);
        var folders = new[] { exeDir, @"D:\Gta IV", Application.persistentDataPath };
        var t = new Thread(() =>
        {
            try
            {
                // after the clips' opening (it leaves the archives' key behind: no unpacking default.xex again)
                for (int i = 0; i < 900 && GtaAnim.State == 1; i++) Thread.Sleep(100);
                string why = "";
                bool open = false;
                foreach (var f in folders)
                {
                    if (string.IsNullOrEmpty(f) || !Directory.Exists(f) || !File.Exists(Path.Combine(f, "audio.rpf"))) continue;
                    if (ds_open(Zs(f)) >= 0) { open = true; break; }
                    why += SoundErr() + "\n";
                }
                if (!open)
                {
                    gtaSoundError = why != "" ? why.Trim() : "no audio.rpf with your GTA IV's files";
                    gtaSoundState = -1;
                    return;
                }
                var buf = new int[1 << 16];
                GNode Get(string obj, int at, string name)
                {
                    int n = name != null ? ds_sound(Zs(name), buf, buf.Length) : ds_game_sound(Zs(obj), at, buf, buf.Length);
                    if (n <= 0) return null;
                    int p = 0;
                    return Parse(buf, ref p);
                }
                var sfxs = new GNode[(int)Sfx.Count];
                foreach (var e in kGtaSfx) sfxs[(int)e.what] = Get(e.obj, e.at, e.name);
                var knuckle = Get("MELEE_COMBAT_JAB", 14, null);
                var crash = Get(null, 0, "MOTORBIKE_MAIN_COLLISIONS_COLLISIONS");
                var skid = Get(null, 0, "VEHICLES_WHEEL_LOOPS_MAIN_TARMAC_SKID");
                var bikes = new Dictionary<string, GNode[]>();
                foreach (var b in kBikeAudio)
                {
                    var set = new GNode[kBikeFields.Length];
                    for (int i = 0; i < set.Length; i++) set[i] = Get(b.Value, kBikeFields[i], null);
                    if (set[kEngLow] != null) bikes[b.Key] = set;
                }
                // every wave they use
                int waves = 0;
                void Count(GNode n)
                {
                    if (n == null) return;
                    if (n.Kind == 0) waves = Math.Max(waves, n.Wave + 1);
                    else foreach (var k in n.Kids) Count(k);
                }
                foreach (var n in sfxs) Count(n);
                Count(knuckle); Count(crash); Count(skid);
                foreach (var set in bikes.Values) foreach (var n in set) Count(n);
                var pcm = new float[waves][];
                var rate = new int[waves];
                var loops = new bool[waves];
                var info = new int[3];
                for (int i = 0; i < waves; i++)
                {
                    if (ds_wave(i, info) < 0) continue;
                    var s = new short[info[1] + 16];
                    int got = ds_pcm(i, s, s.Length);
                    if (got <= 0) continue;
                    var f = new float[got];
                    for (int k = 0; k < got; k++) f[k] = s[k] / 32768f;
                    pcm[i] = f;
                    rate[i] = info[0];
                    loops[i] = info[2] >= 0;
                }
                ds_close();
                gtaPcm = pcm;
                gtaRate = rate;
                gtaLoops = loops;
                gtaSfx = sfxs;
                gtaPunchKnuckle = knuckle;
                gtaBikeCrash = crash;
                gtaSkid = skid;
                lock (gtaBikes) foreach (var b in bikes) gtaBikes[b.Key] = b.Value;
                gtaSoundState = 2;
            }
            catch (Exception e)
            {
                gtaSoundError = e.Message;
                gtaSoundState = -1;
            }
        }) { IsBackground = true };
        t.Start();
    }

    static string SoundErr()
    {
        var b = new byte[1024];
        int n = ds_error(b, b.Length);
        return System.Text.Encoding.UTF8.GetString(b, 0, Math.Max(0, n));
    }

    // Node::flat: kind, volume (1/100 dB), pitch (cents), then the wave or the children's count and the children
    static GNode Parse(int[] f, ref int p)
    {
        var n = new GNode { Kind = f[p], Gain = Mathf.Pow(10f, f[p + 1] / 2000f), Pitch = Mathf.Pow(2f, f[p + 2] / 1200f) };
        int x = f[p + 3];
        p += 4;
        if (n.Kind == 0) { n.Wave = x; return n; }
        n.Kids = new GNode[x];
        for (int i = 0; i < x; i++) n.Kids[i] = Parse(f, ref p);
        return n;
    }

    // On Unity's thread: the waves into clips (once)
    void GtaSoundsUpdate()
    {
        if (gtaSoundState == 0 && GtaAnim.State != 1) GtaSoundsLoad();
        if (gtaSoundState != 2) return;
        gtaClips = new AudioClip[gtaPcm.Length];
        for (int i = 0; i < gtaPcm.Length; i++)
        {
            if (gtaPcm[i] == null || gtaPcm[i].Length == 0) continue;
            var c = AudioClip.Create("gta" + i, gtaPcm[i].Length, 1, gtaRate[i], false);
            c.SetData(gtaPcm[i], 0);
            gtaClips[i] = c;
        }
        gtaPcm = null;
        gtaSoundState = 3;
        int have = 0;
        foreach (var n in gtaSfx) if (n != null) have++;
        Debug.Log($"[Sandbox] GTA IV's sounds: {gtaClips.Length} waves, {have} of {gtaSfx.Length} sounds, {gtaBikes.Count} bikes");
    }

    bool GtaReady => gtaSoundState == 3;

    // ------------------------------------------------------------------ playing them
    /// The game's sound for `what` (false: none, the made-up one plays).
    bool PlayGta(Sfx what, float gain, float pitch)
    {
        if (!GtaReady) return false;
        var n = gtaSfx[(int)what];
        if (n == null) return false;
        PlayNode(n, gain, pitch);
        if (what == Sfx.Punch && gtaPunchKnuckle != null) PlayNode(gtaPunchKnuckle, gain, pitch);
        return true;
    }

    void PlayNode(GNode n, float gain, float pitch)
    {
        if (n == null) return;
        gain *= n.Gain;
        pitch *= n.Pitch;
        switch (n.Kind)
        {
            case 0:
                if (n.Wave >= gtaClips.Length || gtaClips[n.Wave] == null || gain < 0.005f) return;
                var s = voices[voice];
                voice = (voice + 1) % voices.Length;
                s.Stop();
                s.clip = gtaClips[n.Wave];
                s.volume = Mathf.Clamp01(gain);
                s.pitch = Mathf.Clamp(pitch, 0.1f, 3f);
                s.loop = false;
                s.Play();
                break;
            case 1:
                foreach (var k in n.Kids) PlayNode(k, gain, pitch);
                break;
            default:
                if (n.Kids.Length > 0) PlayNode(n.Kids[UnityEngine.Random.Range(0, n.Kids.Length)], gain, pitch);
                break;
        }
    }

    /// A one-off sound of the game at `at` (quieter with distance, as Play).
    void PlayGtaAt(GNode n, Vector3 at, float loud)
    {
        if (!GtaReady || n == null || cam == null || volume <= 0f) return;
        float far = Vector3.Distance(cam.transform.position, at);
        float v = volume * loud / (1f + far * 0.09f);
        if (v < 0.01f) return;
        PlayNode(n, v, Time.timeScale < 0.99f ? Mathf.Lerp(0.55f, 1f, Time.timeScale) : 1f);
    }

    // A loop's one wave (a node down to its first or a random wave): what an engine loop is
    static GNode OneWave(GNode n, ref float gain, ref float pitch)
    {
        while (n != null)
        {
            gain *= n.Gain;
            pitch *= n.Pitch;
            if (n.Kind == 0) return n;
            if (n.Kids == null || n.Kids.Length == 0) return null;
            n = n.Kind == 2 ? n.Kids[UnityEngine.Random.Range(0, n.Kids.Length)] : n.Kids[0];
        }
        return null;
    }

    // ------------------------------------------------------------------ the bike's engine
    class Loop
    {
        public AudioSource Src;
        public float Gain = 1f, Pitch = 1f;
    }
    Loop[] engine;       // engine low, high, exhaust low, high, revs off, idle engine, idle exhaust, skid
    GameObject engineGo;
    float bikeAir, bikeRevs;
    Vector3 bikeLastPos;

    GNode[] BikeSet() => bikeModel != null && GtaReady && gtaBikes.TryGetValue(bikeModel, out var s) ? s : (GtaReady && gtaBikes.TryGetValue("pcj", out var p) ? p : null);

    // Getting on: its start, the loops running
    void EngineStart()
    {
        EngineStop();
        var set = BikeSet();
        if (set == null) return;
        engineGo = new GameObject("BikeEngine");
        DontDestroyOnLoad(engineGo);
        var nodes = new[] { set[kEngLow], set[kEngHigh], set[kExLow], set[kExHigh], set[kRevsOff], set[kIdleEng], set[kIdleEx], gtaSkid };
        engine = new Loop[nodes.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            float g = 1f, p = 1f;
            var w = OneWave(nodes[i], ref g, ref p);
            if (w == null || w.Wave >= gtaClips.Length || gtaClips[w.Wave] == null) continue;
            var src = engineGo.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.spatialBlend = 0f;
            src.clip = gtaClips[w.Wave];
            src.loop = true;
            src.volume = 0f;
            // each loop from its own place: they don't beat against each other in step
            src.time = UnityEngine.Random.value * src.clip.length * 0.9f;
            src.Play();
            engine[i] = new Loop { Src = src, Gain = g, Pitch = p };
        }
        PlayNode(set[kStart] ?? set[kIgnition], volume * 0.8f, 1f);
        bikeRevs = 0.2f;
        bikeAir = 0f;
        if (bikeGo != null) bikeLastPos = bikeGo.transform.position;
    }

    void EngineStop()
    {
        if (engineGo != null) Destroy(engineGo);
        engineGo = null;
        engine = null;
    }

    // Every frame while riding: the revs (the gearbox's, 0.2 idle to 1) choose and pitch the loops; the gas
    // brings the exhaust up; off the gas at high revs, the revs-off loop; sliding sideways, the skid
    void BikeAudio(float dt)
    {
        if (!riding || engine == null || bikeGo == null || bikeOut == null) { if (!riding && engine != null) EngineStop(); return; }
        float revs = Mathf.Clamp(bikeOut[18], 0.15f, 1.1f);
        bikeRevs = Mathf.Lerp(bikeRevs, revs, 1f - Mathf.Exp(-dt * 12f));
        float thr = bikeIn[0];
        float r = Mathf.Clamp01((bikeRevs - 0.2f) / 0.8f);
        float idle = Mathf.Clamp01(1f - r / 0.12f);
        float high = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((r - 0.3f) / 0.5f));
        float on = Mathf.Lerp(0.55f, 1f, thr);
        float off = (1f - thr) * Mathf.Clamp01((r - 0.25f) / 0.4f);
        float slow = Time.timeScale < 0.99f ? Mathf.Lerp(0.55f, 1f, Time.timeScale) : 1f;
        float lowPitch = Mathf.Lerp(0.8f, 1.55f, r), highPitch = Mathf.Lerp(0.62f, 1.18f, r);
        // sideways speed: the skid
        Vector3 pos = bikeGo.transform.position;
        Vector3 vel = dt > 0f ? (pos - bikeLastPos) / dt : Vector3.zero;
        bikeLastPos = pos;
        float side = Mathf.Abs(Vector3.Dot(vel, bikeGo.transform.right));
        bool grounded = bikeOut[23] > 0.5f || bikeOut[24] > 0.5f;
        float skid = grounded ? Mathf.Clamp01((side - 1.5f) / 4f) : 0f;
        float[] gain = { (1f - idle) * (1f - high) * on, (1f - idle) * high * on, (1f - idle) * (1f - high) * on * (0.5f + 0.5f * thr),
                         (1f - idle) * high * on * (0.5f + 0.5f * thr), off * 0.8f, idle, idle * 0.8f, skid };
        float[] pitch = { lowPitch, highPitch, lowPitch, highPitch, Mathf.Lerp(0.8f, 1.3f, r), 1f, 1f, 1f };
        for (int i = 0; i < engine.Length; i++)
        {
            var l = engine[i];
            if (l == null) continue;
            l.Src.volume = Mathf.Clamp01(volume * 0.9f * l.Gain * gain[i]);
            l.Src.pitch = Mathf.Clamp(l.Pitch * pitch[i] * slow, 0.1f, 3f);
        }
        // a landing after a while in the air
        if (!grounded) bikeAir += dt;
        else
        {
            if (bikeAir > 0.35f) { var set = BikeSet(); if (set != null) PlayNode(set[kBikeLand], volume * Mathf.Clamp01(bikeAir), 1f); }
            bikeAir = 0f;
        }
    }
}
