// Delirium Sandbox: the launcher (where your own game's default.xex and the three exported files are) and the
// sandbox itself (mannequins on Delirium's ragdoll: push, shoot, punch, blast, drag, burn, kill them).
// Everything is made in code: any scene works, even an empty one.
using System;
using System.Collections.Generic;
using System.IO;
using Delirium.Ragdoll;
using UnityEngine;

[DefaultExecutionOrder(100)] // after DeliriumRagdolls: the stand-up below has the last word on the bones
public partial class Game : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (FindFirstObjectByType<Game>() != null) return;
        var go = new GameObject("Game");
        DontDestroyOnLoad(go);
        go.AddComponent<Game>();
    }

    // ------------------------------------------------------------------ the launcher
    const string kPrefExe = "delirium.gameExe", kPrefData = "delirium.dataDir", kPrefOk = "delirium.ok";
    string gameExe, dataDir, error = "", lastLog = "";
    bool running;
    DeliriumRagdolls nm;

    void Awake()
    {
        Application.targetFrameRate = 60;
        Application.logMessageReceived += (text, stack, type) =>
        {
            if (type == LogType.Error || type == LogType.Exception) lastLog = text;
        };
        StartLog();
        // a log line without the stack it came from: in the editor each one cost a millisecond or two (a hitch when a
        // ragdoll starts and says so); warnings and errors keep theirs
        Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
        gameExe = PlayerPrefs.GetString(kPrefExe, DefaultExe());
        dataDir = PlayerPrefs.GetString(kPrefData, DefaultData());
        PathsAtStart();
        GmodFolderAtStart();
    }

    // The game's own log beside the project (in a build: in the app's folder): what Delirium and the sandbox said,
    // every warning and error. Read when something doesn't work.
    static string logPath;
    static readonly object logLock = new object();

    void StartLog()
    {
        if (logPath != null) return;
        try
        {
            logPath = Application.isEditor ? Path.Combine(Path.GetDirectoryName(Application.dataPath), "sandbox-log.txt") : Path.Combine(Application.persistentDataPath, "sandbox-log.txt");
            // the run before's kept (when the game was closed by iOS its last lines say what it was doing)
            try { if (File.Exists(logPath)) File.Copy(logPath, Path.Combine(Path.GetDirectoryName(logPath), "sandbox-log-previous.txt"), true); } catch (Exception) { }
            File.WriteAllText(logPath, "Delirium Sandbox " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + ", Unity " + Application.unityVersion + ", " + Application.platform + "\n");
            Application.logMessageReceivedThreaded += (text, stack, type) =>
            {
                bool bad = type != LogType.Log;
                if (!bad && !(text.StartsWith("[Delirium]") || text.StartsWith("[NM]") || text.StartsWith("[Hero]") || text.StartsWith("[Sandbox]"))) return;
                string line = DateTime.Now.ToString("HH:mm:ss") + " " + type + ": " + text + "\n";
                if (type == LogType.Exception || type == LogType.Error) line += stack + "\n";
                lock (logLock)
                {
                    try { File.AppendAllText(logPath, line); } catch (Exception) { }
                }
            };
        }
        catch (Exception) { logPath = ""; }
        // iOS says the game's using too much memory (before it closes it): said in the log, and whatever's unused let go
        Application.lowMemory += () =>
        {
            Debug.LogWarning("[Sandbox] iOS: memory low (" + UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / 1048576 + " MB in use)");
            Resources.UnloadUnusedAssets();
            GC.Collect();
        };
    }

    void Start()
    {
        if (Application.platform == RuntimePlatform.Android)
            Debug.Log("[Sandbox] XR at start: enabled " + UnityEngine.XR.XRSettings.enabled + ", active " + UnityEngine.XR.XRSettings.isDeviceActive + ", device \"" + UnityEngine.XR.XRSettings.loadedDeviceName + "\"");
        // it started with these paths the last time: straight in
        if (PlayerPrefs.GetInt(kPrefOk, 0) == 1) Launch();
    }

    static string DefaultExe() => Application.isMobilePlatform ? Path.Combine(Application.persistentDataPath, "default.xex") : @"D:\Gta IV\default.xex";
    static string DefaultData() => Application.isMobilePlatform ? Application.persistentDataPath : @"D:\delirium repo\CS-Delirium\mod-files";

    // On the phone Delirium can't run curl for the key that opens default.xex (it's in Xenia's source): fetched
    // here once, put where Delirium looks (the same xex.key it keeps on a PC).
    const string kKeySrc = "https://raw.githubusercontent.com/xenia-project/xenia/95a5c3ee250f80c3b9d139658649d9ffb6db3eec/src/xenia/cpu/xex_module.cc";
    bool fetchingKey;

    static string KeyFile()
    {
        if (Application.platform == RuntimePlatform.Android) return Path.Combine(Application.persistentDataPath, ".config", "delirium", "xex.key");
        string home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrEmpty(home)) home = Path.GetDirectoryName(Application.persistentDataPath);
        return Path.Combine(home, "Library", "Application Support", "delirium", "xex.key");
    }

    bool NeedsKey()
    {
        if ((Application.platform != RuntimePlatform.IPhonePlayer && Application.platform != RuntimePlatform.Android) || File.Exists(KeyFile())) return false;
        string exe = Directory.Exists(gameExe) ? Path.Combine(gameExe, "default.xex") : gameExe;
        try
        {
            using (var f = File.OpenRead(exe))
            {
                var head = new byte[4];
                return f.Read(head, 0, 4) == 4 && head[0] == 'X' && head[1] == 'E' && head[2] == 'X' && head[3] == '2';
            }
        }
        catch { return false; }
    }

    System.Collections.IEnumerator FetchKeyThenLaunch()
    {
        fetchingKey = true;
        error = "Getting the key that opens default.xex (once, from the internet)...";
        using (var web = UnityEngine.Networking.UnityWebRequest.Get(kKeySrc))
        {
            web.timeout = 30;
            yield return web.SendWebRequest();
            fetchingKey = false;
            byte[] key = web.result == UnityEngine.Networking.UnityWebRequest.Result.Success ? KeyOf(web.downloadHandler.text) : null;
            if (key == null)
            {
                error = "Couldn't get the key that opens default.xex: " + (web.error ?? "not in the page") + ". Check the internet and press Start again.";
                PlayerPrefs.SetInt(kPrefOk, 0);
                yield break;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(KeyFile()));
                File.WriteAllBytes(KeyFile(), key);
            }
            catch (Exception e)
            {
                error = "Couldn't keep the key: " + e.Message;
                yield break;
            }
        }
        Launch();
    }

    // xe_xex2_retail_key[16] = { 0x.., ... } in Xenia's xex_module.cc
    static byte[] KeyOf(string src)
    {
        int at = src.IndexOf("xe_xex2_retail_key[16] = {", StringComparison.Ordinal);
        if (at < 0) return null;
        int end = src.IndexOf('}', at);
        if (end < 0) return null;
        var bytes = new List<byte>();
        foreach (var part in src.Substring(at, end - at).Split('{')[1].Split(','))
        {
            string h = part.Trim();
            if (h.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) h = h.Substring(2);
            if (byte.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out byte b)) bytes.Add(b);
        }
        return bytes.Count == 16 ? bytes.ToArray() : null;
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    [System.Runtime.InteropServices.DllImport("delirium")] static extern int delirium_set_home(byte[] path);
#endif

    void Launch()
    {
        if (fetchingKey) return;
        error = "";
#if UNITY_ANDROID && !UNITY_EDITOR
        // Android gives an app no HOME: Delirium keeps its key under the game's own folder
        try { delirium_set_home(System.Text.Encoding.UTF8.GetBytes(Application.persistentDataPath + "\0")); } catch (Exception e) { Debug.LogWarning("[Sandbox] HOME: " + e.Message); }
#endif
        gameExe = gameExe.Trim().Trim('"');
        dataDir = dataDir.Trim().Trim('"');
        if (NeedsKey()) { StartCoroutine(FetchKeyThenLaunch()); return; }
        string xrt = Path.Combine(dataDir, "character.xrt"), build = Path.Combine(dataDir, "build.txt"), tasks = Path.Combine(dataDir, "taskparams.txt");
        if (!File.Exists(gameExe) && !Directory.Exists(gameExe)) error = "No such file: " + gameExe;
        else if (!File.Exists(xrt)) error = "No character.xrt in " + dataDir;
        else if (!File.Exists(build)) error = "No build.txt in " + dataDir;
        if (error != "")
        {
            PlayerPrefs.SetInt(kPrefOk, 0);
            return;
        }
        // DeliriumRagdolls reads its settings in Awake: made on a sleeping object, set, then woken
        var go = new GameObject("Delirium");
        go.SetActive(false);
        nm = go.AddComponent<DeliriumRagdolls>();
        nm.gameExe = gameExe;
        nm.xrt = xrt;
        nm.buildFile = build;
        nm.taskParams = File.Exists(tasks) ? tasks : "";
        nm.getUpAfter = 0f;          // getting up is ours (below)
        nm.headshotKills = true;
        lastLog = "";
        go.SetActive(true);
        if (nm == null || !nm.enabled)
        {
            error = lastLog != "" ? lastLog : "Delirium didn't start (see the Console)";
            if (go != null) Destroy(go);
            nm = null;
            PlayerPrefs.SetInt(kPrefOk, 0);
            return;
        }
        PlayerPrefs.SetString(kPrefExe, gameExe);
        PlayerPrefs.SetString(kPrefData, dataDir);
        PlayerPrefs.SetInt(kPrefOk, 1);
        PlayerPrefs.Save();
        Mannequin.ArmDrop = ArmDropOf(build);
        running = true;
        BuildSandbox();
    }

    // How far below level the game man's own arms hang in Delirium's body (its upper arm's part to its forearm's):
    // the mannequin is bound the same, so the body fitted to it is the game's and a man at rest holds his arms as
    // the game's does (bound in a T, he stood with them half out).
    static float ArmDropOf(string buildPath)
    {
        try
        {
            IntPtr b = Delirium.Native.nm_build_load(buildPath);
            if (b == IntPtr.Zero) return 0f;
            float[] upper = new float[3], lower = new float[3], rot = new float[4];
            Delirium.Native.nm_build_part_frame(b, 14, upper, rot);
            Delirium.Native.nm_build_part_frame(b, 15, lower, rot);
            Delirium.Native.nm_build_destroy(b);
            float dx = lower[0] - upper[0], dy = lower[1] - upper[1], dz = lower[2] - upper[2]; // Delirium's space: Z up
            float drop = Mathf.Atan2(-dz, Mathf.Sqrt(dx * dx + dy * dy)) * Mathf.Rad2Deg;
            Debug.Log("[Sandbox] the game man's arms hang " + drop.ToString("0") + " degrees below level: the mannequin's bind pose");
            return Mathf.Clamp(drop, 0f, 80f);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[Sandbox] the arms' angle: " + e.Message);
            return 0f;
        }
    }

    // ------------------------------------------------------------------ the sandbox
    enum Tool { Push, Shoot, Punch, Blast, Drag, Fire, Kill }
    static readonly string[] kToolNames = { "Push", "Shoot", "Punch", "Blast", "Drag", "Fire", "Kill" };

    class Man
    {
        public Mannequin Body;
        public Vector3 Home;
        public float Yaw;
        public float Health = 200f;
        public float Down;           // seconds off his feet
        public float FireLeft;       // seconds still burning
        public float FireTick;       // till his health is next told to the reaction
        public bool Hero;            // the one you play (Hero.cs)
        public Bot BotOf;            // a hostile man: his clips and his mind (Bots.cs)
        public float FlinchUntil;    // a near miss's flinch and look end then
        public float AttackerUntil;  // the hero is his attacker till then (Hero.Attacked)
        public float AttackerTick;
        public bool HighFalling;     // GTA IV's high fall is playing (till he's down)
        public bool Curled;          // curled up before this get-up already
        public int[] Touch;          // what each part touched last frame (Sounds.cs)
        public float SfxAt;
        public float[] TouchedAt;    // when each part last touched the world (Sounds.cs)
        public float StepAt;
        // getting up with GTA IV's own clip (when your game's clips are loaded)
        public GtaAnim.Clip RiseClip;
        public float RiseDur, ClipYaw, ClipDz;
        public Vector3 ClipOrigin;
        public bool Cut;             // a limb cut off: he stays down till he's reset
        public bool Broken;          // Delirium wouldn't take him (the Console says why)
        public bool Killed;
        public bool Limp;            // dead: a plain ragdoll now (DeliriumRagdolls.Die)
        public Vector3 LastVel;      // his centre's speed last frame, for a hit's damage
        public bool VelSet;
        public float HurtAt;         // the last hit's time (the health bar flashes)
        // standing up: from where he lay to the standing pose
        public bool Rising;
        public float RiseT;
        public Vector3 FromPos;
        public Quaternion FromRot;
        public Quaternion[] FromLocal;
    }

    readonly List<Man> men = new List<Man>();
    Material matBody, matGround, matProp, matFlash;
    Camera cam;
    Tool tool = Tool.Push;
    string[] weapons = { "pistol" }, weaponLabels = { "Pistol" };

    class Gun
    {
        public string Name, Label, Set, Row; // Delirium's reaction set; the row's rest: damage:pellets:ms:clip:accuracy:range
        public float Push;                    // N s a bullet (a shotgun: each pellet that strikes)
        public float Between, Damage;   // seconds between shots; health a shot takes
        public bool Long, Pellets;      // held as a rifle; a shotgun's fan
        public int AimClip;             // GtaAnim.Aim's (-1: none, his fists)
        public bool Fists => Set == null;
        public bool Auto => Between > 0f && Between < 0.2f; // fires for as long as it's held (SMG, AK-47)
    }
    static readonly Gun[] kGuns =
    {
        new Gun { Name = "fists", Label = "Fists", AimClip = -1 },
        new Gun { Name = "pistol", Label = "Pistol", Set = "PISTOL_WEAPON", Push = 16f, Row = "25:1:300:17:0.5:50", Between = 0.28f, Damage = 25f, AimClip = 0 },
        new Gun { Name = "deagle", Label = "Desert Eagle", Set = "POWERFUL_WEAPON", Push = 30f, Row = "50:1:450:9:0.5:60", Between = 0.45f, Damage = 50f, AimClip = 1 },
        new Gun { Name = "smg", Label = "SMG", Set = "SMG_WEAPON", Push = 10f, Row = "15:1:80:30:0.5:50", Between = 0.08f, Damage = 15f, Long = true, AimClip = 2 },
        new Gun { Name = "ak47", Label = "AK-47", Set = "POWERFUL_WEAPON", Push = 20f, Row = "35:1:120:30:0.5:70", Between = 0.12f, Damage = 35f, Long = true, AimClip = 3 },
        new Gun { Name = "shotgun", Label = "Shotgun", Set = "POWERFUL_WEAPON", Push = 5f, Row = "80:16:900:8:0.1:35", Between = 0.9f, Damage = 80f, Long = true, Pellets = true, AimClip = 4 },
        new Gun { Name = "sniper", Label = "Sniper rifle", Set = "POWERFUL_WEAPON", Push = 55f, Row = "150:1:1200:5:0.9:200", Between = 1.2f, Damage = 150f, Long = true, AimClip = 5 },
    };

    // A shot of the gun in hand at `m` along `ray` (from the camera): his reaction, and his health.
    void Shoot(Man m, Ray ray) => ShootWith(m, ray, kGuns[weapon % kGuns.Length]);

    void ShootWith(Man m, Ray ray, Gun gun)
    {
        var owner = m.Body.transform;
        if (gun.Pellets)
        {
            int hits = nm.Shotgun(owner, ray.origin, ray.direction, gun.Name, god ? 200f : m.Health);
            if (hits > 0 && !god) m.Health = Mathf.Max(0f, m.Health - gun.Damage * Mathf.Min(hits, 16) / 16f);
        }
        else
        {
            float left = nm.ShootWeapon(owner, ray.origin, ray.direction, gun.Name, god ? 200f + gun.Damage : m.Health);
            if (left >= 0f && !god) m.Health = left;
        }
        // unlimited health: nothing a gun does kills (head shots are off too while it's on)
        if (!god && (m.Health <= 0f || nm.State(owner).Dead)) m.Killed = true;
    }

    // A bullet along `ray` that wasn't for them: whoever it passes close flinches (and dives, with "Dive when shot
    // at", if shot at again soon). Delirium judges how close.
    void Missed(Ray ray, Man struck)
    {
        FxBullet(ray, struck); // (its blood, or its impact, and its tracer: Fx.cs)
        // only with "Dive when shot at" on: the near miss's flinch holds an arm and his eyes on where the bullet
        // passed (he "pointed" at the shooter's old place), so it's also let go after a second (Mind)
        if (!dive) return;
        foreach (var x in men)
        {
            if (x == struck || x.Hero || x.Rising || x.Killed) continue;
            if (Vector3.Dot(x.Body.Hips.position - ray.origin, ray.direction) <= 0f) continue; // behind the gun
            if (nm.State(x.Body.transform).Ragdolled && nm.NearMiss(x.Body.transform, ray.origin, ray.direction)) x.FlinchUntil = Time.time + 1f;
        }
    }

    // A flash that grows and goes (a blast, a muzzle).
    void Flash(Vector3 at, float radius)
    {
        var s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        s.name = "Flash";
        DestroyImmediate(s.GetComponent<Collider>());
        s.GetComponent<Renderer>().sharedMaterial = matFlash;
        s.transform.position = at;
        s.transform.localScale = Vector3.zero;
        flashes.Add((s.transform, 0f, radius));
    }
    int weapon = 1; // the pistol (0: his fists)
    string note = "";
    // the camera's orbit
    float camYaw = 200f, camPitch = 18f, camDist = 6f;
    Vector3 camTarget = new Vector3(0f, 1f, 0f);
    bool orbiting;
    Vector2 lastPointer;
    float lastPinch;
    // dragging one of them about
    Man held;
    int heldPart = -1;
    float heldDepth;
    // blasts' flashes
    readonly List<(Transform t, float age, float radius)> flashes = new List<(Transform, float, float)>();
    readonly List<Collider> level = new List<Collider>();
    readonly List<Rect> uiRects = new List<Rect>();
    const float kRise = 1.25f;
    const int kMaxMen = 12;

    Material Mat(Color c)
    {
        var src = Resources.Load<Material>("DeliriumMat");
        var m = src != null ? new Material(src) : new Material(Shader.Find("Standard"));
        m.color = c;
        return m;
    }

    void BuildSandbox()
    {
        matBody = Mat(new Color(0.93f, 0.55f, 0.2f));
        matGround = Mat(new Color(0.42f, 0.45f, 0.48f));
        matProp = Mat(new Color(0.7f, 0.7f, 0.72f));
        matFlash = Mat(new Color(1f, 0.75f, 0.2f));

        cam = Camera.main;
        if (cam == null)
        {
            var c = new GameObject("Main Camera");
            c.tag = "MainCamera";
            cam = c.AddComponent<Camera>();
            c.AddComponent<AudioListener>();
        }
        cam.nearClipPlane = 0.05f;
        cam.farClipPlane = 300f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.62f, 0.74f, 0.86f);
        if (FindFirstObjectByType<Light>() == null)
        {
            var l = new GameObject("Sun").AddComponent<Light>();
            l.type = LightType.Directional;
            l.shadows = LightShadows.Soft;
            l.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        }
        // soft light from all round (sky above, a paler band at the horizon, the ground's bounce below), the sun's
        // shadows not black: closer to Sketchfab's look than one sun and grey
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.78f, 0.84f, 0.95f);
        RenderSettings.ambientEquatorColor = new Color(0.66f, 0.68f, 0.66f);
        RenderSettings.ambientGroundColor = new Color(0.42f, 0.40f, 0.36f);
        var sun = FindFirstObjectByType<Light>();
        if (sun != null && sun.type == LightType.Directional)
        {
            sun.intensity = 1.05f;
            sun.shadowStrength = 0.55f;
        }

        BuildLevel();

        // GTA IV's clips for Hero and the get-ups, from the game's folder (the exe's own, else the Playroom's copy)
        StartClips();
        AddMan();
        FilesInit(); // your own models and map from the last time (Models.cs)
    }

    // The sandbox's own level: boxes only (Delirium reads box colliders as they are).
    void BuildLevel()
    {
        Box("Ground", new Vector3(0f, -0.5f, 0f), new Vector3(80f, 1f, 80f), Quaternion.identity, matGround);
        for (int i = 0; i < 6; i++) // stairs up to a platform
            Box("Step" + i, new Vector3(7f + i * 0.35f, 0.1f + i * 0.2f, 0f), new Vector3(0.35f, 0.2f, 3f), Quaternion.identity, matProp);
        Box("Platform", new Vector3(11.1f, 0.6f, 0f), new Vector3(4f, 1.2f, 3f), Quaternion.identity, matProp);
        Box("Wall", new Vector3(0f, 1f, 7f), new Vector3(5f, 2f, 0.3f), Quaternion.identity, matProp);
        Box("Ramp", new Vector3(-8f, 0.75f, 0f), new Vector3(6f, 0.3f, 3f), Quaternion.Euler(0f, 0f, -16f), matProp);
        Box("Kerb", new Vector3(0f, 0.1f, -6f), new Vector3(8f, 0.2f, 0.4f), Quaternion.identity, matProp);
        Physics.SyncTransforms(); // the physics knows where they are before anything casts a ray at them
        DeliriumRagdolls.SandboxWholeLevel(level, 0f); // all of it to Delirium at once: the ragdolls' collision
    }

    void StartClips()
    {
        string exeDir = Directory.Exists(gameExe) ? gameExe : Path.GetDirectoryName(gameExe);
        GtaAnim.Load(new[] { exeDir, @"D:\delirium repo\delirium-playroom\game", Application.persistentDataPath });
        // the guns: Delirium's table (reaction set : push : damage : pellets : ms between shots : clip : accuracy : range)
        weapons = new string[kGuns.Length];
        weaponLabels = new string[kGuns.Length];
        for (int i = 0; i < kGuns.Length; i++)
        {
            weapons[i] = kGuns[i].Name;
            weaponLabels[i] = kGuns[i].Label;
        }
        LoadGuns();
        DeliriumRagdolls.SandboxFloor = 0f; // the ground's top: a floor under the level's triangles
        DeliriumRagdolls.SandboxBind = (Transform owner, Transform bone, out Matrix4x4 bind) =>
        {
            var body = owner.GetComponent<Mannequin>();
            if (body != null) return body.BindOf(bone, out bind);
            bind = Matrix4x4.identity;
            return false;
        };
        SoundsInit();
        SettingsLoad();
    }

    // Delirium's table from ours, every bullet's push times the menu's "Gun push".
    void LoadGuns()
    {
        var table = new List<string>();
        foreach (var g in kGuns)
            if (!g.Fists) table.Add(g.Name + "=" + g.Set + ":" + (g.Push * gunPush).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + ":" + g.Row);
        if (DeliriumRagdolls.LoadWeapons(string.Join(", ", table.ToArray())) < 0) Debug.LogWarning("the guns' table: Delirium refused it");
    }

    void Box(string name, Vector3 at, Vector3 size, Quaternion turn, Material m)
    {
        var b = GameObject.CreatePrimitive(PrimitiveType.Cube);
        b.name = name;
        b.transform.SetPositionAndRotation(at, turn);
        b.transform.localScale = size;
        b.GetComponent<Renderer>().sharedMaterial = m;
        level.Add(b.GetComponent<Collider>());
    }

    void AddMan()
    {
        if (men.Count >= kMaxMen) return;
        int i = men.Count;
        // the first in the middle, the rest in a ring round him, all facing the camera's side
        Vector3 home = HomeOf(i);
        float yaw = 180f;
        if (heroOn && hero != null && LookedAt(out Vector3 spot)) // as the hero: where you look, facing you
        {
            home = spot;
            Vector3 to = Vector3.ProjectOnPlane(hero.Body.Hips.position - spot, Vector3.up);
            if (to.sqrMagnitude > 0.01f) yaw = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg;
        }
        var man = new Man { Home = home, Yaw = yaw };
        man.Body = MakeBody("Man" + (i + 1), home, man.Yaw, false);
        men.Add(man);
    }

    // A man taken away (not the hero): let go of, his body gone (Delirium frees his doll when it sees him gone)
    void RemoveMan(Man m)
    {
        if (m == null || m.Hero) return;
        if (held == m) Drop();
        if (m.BotOf != null) BotGone(m.BotOf);
        try { nm?.Unleash(m.Body.transform, -1); } catch (Exception) { }
        if (m.Body != null) Destroy(m.Body.gameObject);
        men.Remove(m);
        sel = Mathf.Clamp(sel, 0, Mathf.Max(0, men.Count - 1));
    }

    // The last one added that isn't the hero
    void RemoveLastMan()
    {
        for (int i = men.Count - 1; i >= 0; i--)
            if (!men[i].Hero) { RemoveMan(men[i]); return; }
    }

    void RemoveAllMen()
    {
        for (int i = men.Count - 1; i >= 0; i--)
            if (!men[i].Hero) RemoveMan(men[i]);
    }

    // The ground where the camera looks (past the men and the hero's gun), up to 40 m; else 8 m ahead.
    // Off a wall, half a metre back towards you; then down onto whatever is under that.
    bool LookedAt(out Vector3 spot)
    {
        spot = Vector3.zero;
        if (cam == null) return false;
        Physics.SyncTransforms();
        var ray = new Ray(cam.transform.position, cam.transform.forward);
        var hits = Physics.RaycastAll(ray, 40f, ~0, QueryTriggerInteraction.Ignore);
        System.Array.Sort(hits, (x, y) => x.distance.CompareTo(y.distance));
        bool found = false;
        foreach (var h in hits)
        {
            if (Ours(h.collider.transform)) continue;
            spot = h.point;
            if (h.normal.y < 0.5f) spot -= ray.direction * 0.5f;
            found = true;
            break;
        }
        if (!found)
        {
            Vector3 flat = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
            if (flat.sqrMagnitude < 0.01f) flat = Vector3.ProjectOnPlane(cam.transform.up, Vector3.up);
            spot = hero.Body.Hips.position + flat.normalized * 8f;
        }
        // down onto the ground (the first thing that isn't a man), or the sandbox floor
        var down = Physics.RaycastAll(spot + Vector3.up * 1.5f, Vector3.down, 60f, ~0, QueryTriggerInteraction.Ignore);
        System.Array.Sort(down, (x, y) => x.distance.CompareTo(y.distance));
        foreach (var h in down)
        {
            if (Ours(h.collider.transform)) continue;
            spot.y = h.point.y;
            return true;
        }
        if (mapRoot == null) { spot.y = 0f; return true; }
        return false; // nothing under it on a map (over the edge): the usual place instead
    }

    bool Ours(Transform t)
    {
        if (gunModel != null && t.IsChildOf(gunModel.transform)) return true;
        foreach (var m in men)
            if (m.Body != null && t.IsChildOf(m.Body.transform)) return true;
        return false;
    }

    void ResetAll()
    {
        Drop();
        wounds.Clear(); // (their bleeding stops with it)
        foreach (var b in bots) BotReset(b);
        foreach (var m in men)
        {
            if (nm.State(m.Body.transform).Ragdolled) nm.GetUp(m.Body.transform, 0.001f);
            m.Body.Stand(m.Home, m.Yaw);
            m.Health = 200f;
            m.Down = m.FireLeft = 0f;
            m.Killed = m.Rising = m.Broken = m.Cut = m.Limp = m.VelSet = false;
            if (m.Hero) HeroPlace(m.Home, m.Yaw);
        }
    }

    void Update()
    {
        VrUpdate(); // (a headset: its head, hands and buttons; nothing on the phone)
        HeroBodyBox(); // (the iPhone: his body pushes ragdolls)
        if (!running || nm == null) return;
        SettingsUpdate();
        FxUpdate();
        AddonsUpdate();
        BrowserUpdate();
        float dt = Time.deltaTime;
        foreach (var m in men) Mind(m, dt);
        EntsUpdate(dt); // (a GMod map's doors, lifts, trains, timers: before the hero, who rides them)
        if (heroOn) { HeroUpdate(dt); BikeUpdate(dt); }
        else Pointer();
        BotsUpdate(dt); // (the hostile men: Bots.cs)
        SwingsUpdate(dt); // (GTA IV's swingsets: Objects.cs)
        GtaSoundsUpdate();
        BikeAudio(dt);
        for (int i = flashes.Count - 1; i >= 0; i--)
        {
            Transform t = flashes[i].t;
            float age = flashes[i].age + Time.unscaledDeltaTime, radius = flashes[i].radius;
            if (age > 0.3f)
            {
                Destroy(t.gameObject);
                flashes.RemoveAt(i);
                continue;
            }
            t.localScale = Vector3.one * (radius * 2f * Mathf.Clamp01(age / 0.3f) * 0.6f);
            flashes[i] = (t, age, radius);
        }
    }

    // One man's upkeep: on Delirium whenever he isn't standing up; up again once he has lain still.
    void Mind(Man m, float dt)
    {
        if (m.Rising || m.Broken || (m.Hero && riding)) { m.VelSet = false; return; } // (riding: his pose is the bike's)
        var owner = m.Body.transform;
        var st = nm.State(owner);
        if (m.BotOf != null && m.BotOf.Mode == 0)
        {
            if (!st.Ragdolled) { m.VelSet = false; return; } // on his clips (Bots.cs)
            m.BotOf.Mode = 1;                // hit: Delirium has him
            m.BotOf.Mel = null;
        }
        if (m.Hero && heroMode == 0)
        {
            if (!st.Ragdolled) { m.VelSet = false; return; } // on his clips
            heroMode = 1;                    // something took him (a blast, a reaction from the menu)
            heroNoclip = false;
            heroAim = false;
            heroKnocked = 0f;
        }
        if (!st.Ragdolled)
        {
            m.Broken = !nm.Ragdoll(owner, Vector3.zero); // he stands by his own balance
            if (!m.Broken) Outfit(m);
            else Debug.LogWarning("[Sandbox] " + m.Body.name + " wasn't taken; his bind pose: " + m.Body.BindReport());
            m.Down = 0f;
            return;
        }
        Impacts(m, st, dt);
        if (m.FireLeft > 0f)
        {
            m.FireLeft -= dt;
            if (!Immortal(m)) m.Health = Mathf.Max(m.Health - 12f * dt, 0f);
            if (m.Health <= 0f)
            {
                nm.OnFire(owner, false, 0f);
                nm.Kill(owner);
                m.Killed = true;
                m.FireLeft = 0f;
            }
            else if (m.FireLeft <= 0f) nm.OnFire(owner, false, m.Health);
            else if ((m.FireTick -= dt) <= 0f)
            {
                m.FireTick = 1f;
                nm.OnFire(owner, true, m.Health);
            }
        }
        Contacts(m, st);
        Falling(m, st);
        if (m.FlinchUntil > 0f && Time.time >= m.FlinchUntil)
        {
            m.FlinchUntil = 0f;
            if (st.Balancing && !st.Fallen)
            {
                nm.React(owner, "upperBodyFlinch start=false");
                nm.React(owner, "headLook start=false");
            }
        }
        if (m.AttackerUntil > 0f)
        {
            if (Time.time >= m.AttackerUntil || hero == null)
            {
                m.AttackerUntil = 0f;
                nm.SetAttacker(owner, null, false);
            }
            else if (Time.time >= m.AttackerTick)
            {
                m.AttackerTick = Time.time + 0.2f;
                nm.SetAttacker(owner, hero.Body.transform, false); // where he is now
            }
        }
        if (m.Killed || st.Dead)
        {
            // dead, whatever killed him: a plain ragdoll from now on, not euphoria
            if (!m.Limp) { nm.Die(owner); m.Limp = true; m.Killed = true; m.Health = 0f; }
            return;
        }
        m.Down = st.Fallen ? m.Down + dt : 0f;
        bool still = st.LyingFor > 0.5f || (m.Down > 4.5f && st.Velocity.magnitude < 0.6f);
        if (autoGetUp && !m.Cut && m.Down > 1.6f && still && m.FireLeft <= 0f && held != m)
        {
            // "Curl up before getting up": sometimes, a while curled on the ground first
            if ((extra & 2) != 0 && !m.Curled && UnityEngine.Random.value < 0.5f)
            {
                m.Curled = true;
                nm.React(owner, "bodyFoetal start=true");
                m.Down = -1.2f;
                return;
            }
            m.Curled = false;
            BeginRise(m);
        }
    }

    // A ragdoll dropping fast with air under him: GTA IV's high fall (arms wheeling, legs pedalling, braced for the
    // ground), once a fall; over when he's stopped coming down.
    void Falling(Man m, DeliriumRagdolls.DollState st)
    {
        if (m.HighFalling)
        {
            if (st.Velocity.y > -1f) m.HighFalling = false;
            return;
        }
        if (m.Killed || st.Dead || st.Velocity.y > -5.5f) return;
        if (Physics.Raycast(m.Body.Hips.position, Vector3.down, 2.5f)) return; // nearly down already
        if (Tumble(m.Body.transform, st.Velocity))
        {
            m.HighFalling = true;
            Debug.Log("[Sandbox] " + m.Body.name + ": a high fall");
        }
    }

    // GTA IV's high fall, but turning over in the air: its arms wheeling and legs pedalling without the push that held
    // him upright to land on his feet (pdStrength 0, not turned to the way he falls), and a turn to start with (a
    // somersault, some of it sideways, faster the faster he's going)
    bool Tumble(Transform owner, Vector3 vel)
    {
        if (!nm.React(owner, "highFall start=true pdStrength=0 orientateBodyToFallDirection=false"))
            return nm.HighFall(owner, false);
        Vector3 along = Vector3.ProjectOnPlane(vel, Vector3.up);
        Vector3 fwd = along.sqrMagnitude > 0.25f ? along.normalized : Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f) * Vector3.forward;
        Vector3 side = Vector3.Cross(Vector3.up, fwd);
        Vector3 axis = (side * (UnityEngine.Random.value < 0.75f ? 1f : -1f) + fwd * UnityEngine.Random.Range(-0.6f, 0.6f)).normalized;
        float rate = Mathf.Clamp(vel.magnitude * 0.3f, 1.5f, 4.5f) * UnityEngine.Random.Range(0.7f, 1.2f);
        nm.Spin(owner, axis * rate);
        return true;
    }

    bool Immortal(Man m) => god || (m.Hero && heroImmortal);

    // The world hitting him: GTA IV's way of hurting a ragdoll by how hard it's stopped. His centre's speed lost in a
    // frame while touching something (a fall's landing, a wall at speed off the bike, a blast's throw into it): from
    // 7 m/s lost (about a 3 m drop) it hurts, a 10 m drop is about all of a man's 200, more kills.
    void Impacts(Man m, DeliriumRagdolls.DollState st, float dt)
    {
        Vector3 v = st.Velocity;
        if (!m.VelSet || dt <= 0f) { m.LastVel = v; m.VelSet = true; return; }
        float lost = (v - m.LastVel).magnitude;
        m.LastVel = v;
        if (lost < 7f || m.Killed || st.Contacts == null) return;
        bool touching = false;
        foreach (int c in st.Contacts) if (c != 0) { touching = true; break; }
        if (!touching) return;
        float damage = Mathf.Pow(lost - 7f, 1.4f) * 12f + 4f;
        m.HurtAt = Time.time;
        if (Immortal(m)) return;
        m.Health = Mathf.Max(0f, m.Health - damage);
        if (m.Health <= 0f)
        {
            m.Killed = true;
            Debug.Log($"[Sandbox] {m.Body.name}: killed by a hit ({lost:F1} m/s lost)");
        }
    }

    void BeginRise(Man m)
    {
        var body = m.Body;
        var owner = body.transform;
        m.FromPos = body.Hips.position;
        m.FromRot = body.Hips.rotation;
        m.FromLocal = new Quaternion[body.Bones.Length];
        for (int i = 0; i < body.Bones.Length; i++) m.FromLocal[i] = body.Bones[i].localRotation;
        // the way he'll face: his front's, or lying flat, his feet's side on his back and his head's on his front
        Vector3 f = Vector3.ProjectOnPlane(m.FromRot * Vector3.forward, Vector3.up);
        if (f.magnitude < 0.35f)
        {
            Vector3 fwd = m.FromRot * Vector3.forward;
            f = Vector3.ProjectOnPlane(m.FromRot * Vector3.up, Vector3.up) * (fwd.y > 0f ? -1f : 1f);
        }
        if (f.magnitude < 1e-3f) f = Vector3.forward;
        nm.GetUp(owner, 0.001f); // the ragdoll lets go; the root is now on the floor under his hips
        float floor = owner.position.y;
        owner.SetPositionAndRotation(new Vector3(m.FromPos.x, floor, m.FromPos.z), Quaternion.LookRotation(f.normalized, Vector3.up));
        m.Rising = true;
        m.RiseT = 0f;
        m.Down = 0f;
        m.RiseClip = null;
        m.RiseDur = kRise;
        ClipRise(m, floor);
        Rise(m);
    }

    // GTA IV's own get-up (from his front or his back) when the game's clips are loaded: turned so the clip's
    // hips-to-head lies along the body's, moved so its hips are where his are, on the ground under him.
    void ClipRise(Man m, float floor)
    {
        if (GtaAnim.State != 2) return;
        var body = m.Body;
        bool faceUp = (m.FromRot * Vector3.forward).y > 0f;
        var clip = faceUp ? GtaAnim.GetUpBack : GtaAnim.GetUpFront;
        Frames();
        if (!clip.Ok || !GtaAnim.Pose(clip, 0f, fA, true)) return;
        int head = body.Index("Head");
        var unturned = Quaternion.Euler(0f, -90f, 0f);
        Vector3 dc = Vector3.ProjectOnPlane(unturned * (fA[head].P - fA[0].P), Vector3.up);
        Vector3 dr = Vector3.ProjectOnPlane(body.Bones[head].position - m.FromPos, Vector3.up);
        float yaw = dc.magnitude > 1e-3f && dr.magnitude > 1e-3f ? Vector3.SignedAngle(dc, dr, Vector3.up) : body.transform.eulerAngles.y;
        Vector3 at = Quaternion.Euler(0f, yaw - 90f, 0f) * fA[0].P * body.Scale;
        m.ClipOrigin = new Vector3(m.FromPos.x - at.x, floor, m.FromPos.z - at.z);
        m.ClipDz = Mathf.Clamp(m.FromPos.y - (floor + at.y), -0.3f, 0.3f);
        m.ClipYaw = yaw;
        m.RiseClip = clip;
        m.RiseDur = clip.Dur;
    }

    // The stand-up: a plain blend from how he lay into the standing pose (GTA IV's own get-up clips come with Hero).
    void Rise(Man m)
    {
        var body = m.Body;
        var owner = body.transform;
        if (m.RiseClip != null)
        {
            float phase = Mathf.Clamp01(m.RiseT / m.RiseDur);
            if (GtaAnim.Pose(m.RiseClip, phase, fA, true))
            {
                // his own height where he lay faded out over the clip's first 60%; his pose as he lay over its first 0.3 s
                body.Apply(fA, m.ClipOrigin, m.ClipYaw, m.ClipDz * (1f - Mathf.SmoothStep(0f, 1f, phase / 0.6f)));
                float f = Mathf.SmoothStep(0f, 1f, m.RiseT / 0.3f);
                if (f < 1f)
                {
                    var locals = new Quaternion[body.Bones.Length];
                    for (int i = 1; i < locals.Length; i++) locals[i] = body.Bones[i].localRotation;
                    body.Hips.SetPositionAndRotation(Vector3.Lerp(m.FromPos, body.Hips.position, f), Quaternion.Slerp(m.FromRot, body.Hips.rotation, f));
                    for (int i = 1; i < locals.Length; i++) body.Bones[i].localRotation = Quaternion.Slerp(m.FromLocal[i], locals[i], f);
                }
                return;
            }
            m.RiseClip = null; // the library refused: the plain blend from here
        }
        float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(m.RiseT / kRise));
        Vector3 to = owner.position + Vector3.up * body.HipHeight;
        Vector3 at = Vector3.Lerp(m.FromPos, to, k);
        at.y = Mathf.Lerp(m.FromPos.y, to.y, k * k); // the hips come up late: knees under him first
        body.Hips.SetPositionAndRotation(at, Quaternion.Slerp(m.FromRot, owner.rotation, k));
        for (int i = 1; i < body.Bones.Length; i++) body.Bones[i].localRotation = Quaternion.Slerp(m.FromLocal[i], body.StandLocal[i], k);
    }

    void LateUpdate()
    {
        if (!running || nm == null) return;
        PhysViewGather();
        foreach (var m in men)
        {
            if (!m.Rising) continue;
            m.RiseT += Time.deltaTime;
            Rise(m);
            if (m.RiseT >= m.RiseDur)
            {
                m.Rising = false;
                if (m.RiseClip != null)
                {
                    // the root under where the clip left him, facing his way; the bones stay where they are
                    var hips = m.Body.Hips;
                    Vector3 hp = hips.position;
                    Quaternion hr = hips.rotation;
                    Vector3 f = Vector3.ProjectOnPlane(hr * Vector3.forward, Vector3.up);
                    m.Body.transform.SetPositionAndRotation(new Vector3(hp.x, m.ClipOrigin.y, hp.z),
                        f.magnitude > 0.1f ? Quaternion.LookRotation(f.normalized, Vector3.up) : m.Body.transform.rotation);
                    hips.SetPositionAndRotation(hp, hr);
                }
                if (m.Hero) HeroUp();
                else if (m.BotOf != null) BotTakeBack(m.BotOf);
                else if (nm.Ragdoll(m.Body.transform, Vector3.zero)) Outfit(m);
            }
        }
        BotsPose(); // (the hostile men on their clips)
        if (heroOn)
        {
            BikePose();
            HeroPose();
            if (vrOn) VrCamera(); // (the headset's: your head is the camera)
            else if (riding && bikeGo != null) BikeCamera();
            else HeroCamera();
            return;
        }
        if (vrOn) return; // (no hero in VR: you walk about yourself, VR.cs)
        // the camera: round the first man (or the held one)
        Man follow = held ?? Sel;
        if (follow != null)
        {
            Vector3 want = follow.Body.Hips.position + Vector3.up * 0.2f;
            camTarget = Vector3.Lerp(camTarget, want, 1f - Mathf.Exp(-6f * Time.unscaledDeltaTime));
        }
        var turn = Quaternion.Euler(camPitch, camYaw, 0f);
        cam.transform.SetPositionAndRotation(camTarget - turn * Vector3.forward * camDist, turn);
    }

    // ------------------------------------------------------------------ the pointer (mouse, or one finger)
    bool OverUi(Vector2 screen)
    {
        float s = UiScale();
        var p = new Vector2(screen.x / s, (Screen.height - screen.y) / s);
        foreach (var r in uiRects)
            if (r.Contains(p)) return true;
        return false;
    }

    // The man and the point on him under the ray: the nearest bone within a hand's width of it.
    Man Pick(Ray ray, out Vector3 point)
    {
        Man best = null;
        float bestAlong = float.MaxValue;
        point = default;
        foreach (var m in men)
        {
            if (m.Hero) continue; // not yourself
            var bones = m.Body.Bones;
            for (int i = 0; i < bones.Length; i++)
            {
                // the bone's joint and the middle of its link to its parent
                for (int half = 0; half < 2; half++)
                {
                    Vector3 p = bones[i].position;
                    if (half == 1)
                    {
                        if (i == 0) continue;
                        p = (p + bones[i].parent.position) * 0.5f;
                    }
                    Vector3 to = p - ray.origin;
                    float along = Vector3.Dot(to, ray.direction);
                    if (along <= 0f) continue;
                    float off = (to - ray.direction * along).magnitude;
                    if (off < 0.2f && along < bestAlong)
                    {
                        bestAlong = along;
                        best = m;
                        point = ray.origin + ray.direction * along;
                    }
                }
            }
        }
        return best;
    }

    void Pointer()
    {
        // two fingers: pinch to zoom
        if (Input.touchCount >= 2)
        {
            float d = Vector2.Distance(Input.GetTouch(0).position, Input.GetTouch(1).position);
            if (lastPinch > 0f) camDist = Mathf.Clamp(camDist * lastPinch / Mathf.Max(d, 1f), 2f, 25f);
            lastPinch = d;
            orbiting = false;
            Drop();
            return;
        }
        lastPinch = 0f;
        Vector2 pos = Input.mousePosition;
        if (!OverUi(pos)) camDist = Mathf.Clamp(camDist * (1f - Input.mouseScrollDelta.y * 0.1f), 2f, 25f);
        if (Input.GetMouseButtonDown(0) && !OverUi(pos))
        {
            var ray = ViewRay(pos);
            var man = Pick(ray, out Vector3 point);
            if (tool == Tool.Blast && man == null)
            {
                if (Physics.Raycast(ray, out var hit, 200f)) Blast(hit.point);
                else orbiting = true;
            }
            else if (man != null)
            {
                sel = men.IndexOf(man);
                Use(man, ray, point);
                var g = kGuns[weapon % kGuns.Length];
                if (tool == Tool.Shoot && g.Auto) { spraying = true; sprayNext = Time.time + g.Between; }
            }
            else orbiting = true;
            lastPointer = pos;
        }
        bool rmb = Input.touchCount == 0 && Input.GetMouseButton(1); // a second finger is a pinch, not the right button
        if (rmb && Input.GetMouseButtonDown(1)) { orbiting = true; lastPointer = pos; }
        if (Input.GetMouseButton(0) || rmb)
        {
            Vector2 d = pos - lastPointer;
            lastPointer = pos;
            if (spraying && Input.GetMouseButton(0)) Spray(pos);
            if (held != null)
            {
                // the leash's end follows the pointer, at the depth he was taken at
                var ray = ViewRay(pos);
                Vector3 anchor = ray.origin + ray.direction * heldDepth;
                anchor.y = Mathf.Max(anchor.y, 0.15f);
                nm.LeashAnchor(held.Body.transform, heldPart, anchor, 0.12f);
            }
            else if (orbiting)
            {
                float k = 180f / Mathf.Max(Screen.height, 1);
                camYaw += d.x * k;
                camPitch = Mathf.Clamp(camPitch - d.y * k, -5f, 80f);
            }
        }
        else
        {
            orbiting = spraying = false;
            Drop();
        }
    }

    // An automatic held down (started on a man): a shot every Between wherever the pointer is, man or not.
    bool spraying;
    float sprayNext;
    void Spray(Vector2 pos)
    {
        var g = kGuns[weapon % kGuns.Length];
        if (tool != Tool.Shoot || !g.Auto) { spraying = false; return; }
        if (OverUi(pos) || Time.time < sprayNext) return;
        sprayNext = Mathf.Max(sprayNext + g.Between, Time.time - g.Between); // keeps the rate; no burst after a hitch
        var ray = ViewRay(pos);
        var man = Pick(ray, out Vector3 point);
        if (man != null) Use(man, ray, point);
        else
        {
            PlayGun(cam.transform.position);
            Missed(ray, null);
        }
    }

    void Drop()
    {
        if (held == null) return;
        nm.Unleash(held.Body.transform, -1);
        held = null;
        heldPart = -1;
    }

    void Use(Man m, Ray ray, Vector3 point)
    {
        var owner = m.Body.transform;
        if (m.Rising) return; // a moment: he's getting up
        Vector3 flat = Vector3.ProjectOnPlane(ray.direction, Vector3.up).normalized;
        switch (tool)
        {
            case Tool.Push:
                nm.Ragdoll(owner, (flat + Vector3.up * 0.1f) * pokeForce);
                break;
            case Tool.Shoot:
                if (kGuns[weapon % kGuns.Length].Fists)
                {
                    nm.MeleeHit(owner, point, (flat + Vector3.up * 0.15f) * 75f, cam.transform.position);
                    Play(Sfx.Punch, point);
                    if (UnityEngine.Random.value < 0.4f) FxBlood(point, flat, 0.3f);
                    break;
                }
                PlayGun(cam.transform.position);
                Missed(ray, m);
                if (m.Killed) { nm.ShootAlong(owner, ray.origin, ray.direction, 20f, "POWERFUL_WEAPON", 0f); break; }
                Shoot(m, ray);
                break;
            case Tool.Punch:
                nm.MeleeHit(owner, point, (flat + Vector3.up * 0.15f) * 75f, cam.transform.position);
                Play(Sfx.Punch, point);
                if (UnityEngine.Random.value < 0.4f) FxBlood(point, flat, 0.3f);
                break;
            case Tool.Blast:
                Blast(new Vector3(point.x, 0.2f, point.z) - flat * 1.2f);
                break;
            case Tool.Drag:
                heldPart = nm.Leash(owner, point, point, 0.12f);
                if (heldPart >= 0)
                {
                    held = m;
                    heldDepth = Vector3.Dot(point - ray.origin, ray.direction);
                }
                break;
            case Tool.Fire:
                if (m.Killed) break;
                if (m.FireLeft > 0f) { m.FireLeft = 0f; nm.OnFire(owner, false, m.Health); }
                else { m.FireLeft = 7f; m.FireTick = 1f; nm.OnFire(owner, true, m.Health); }
                break;
            case Tool.Kill:
                nm.Kill(owner);
                m.Killed = true;
                m.Health = 0f;
                break;
        }
    }

    void Blast(Vector3 at)
    {
        float radius = blastRadius;
        foreach (var m in men)
        {
            if (m.Rising) continue;
            if (Vector3.Distance(m.Body.Hips.position, at) < radius) nm.Explode(m.Body.transform, at, radius, blastSpeed);
        }
        Flash(at, radius);
        Play(Sfx.Boom, at, 1f);
    }

    // ------------------------------------------------------------------ the screen (IMGUI: mouse and fingers alike)
    static float uiZoom = 1f; // Settings > Buttons size
    static float UiScale() => Mathf.Max(1f, Screen.height / 720f) * uiZoom;
    GUIStyle big, field, label;

    void Styles()
    {
        BuildSkin(); // Ui.cs
        GUI.skin = skin;
    }

    void OnGUI()
    {
        Styles();
        DrawView(); // Settings: the 3D view at a lower resolution, under everything else
        float s = UiScale();
        GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));
        float w = Screen.width / s, h = Screen.height / s;
        if (!running)
        {
            LauncherGui(w, h);
            return;
        }
        uiRects.Clear();
        GUI.Box(new Rect(0f, 0f, w, 54f), GUIContent.none, chip); // the top bar's strip
        if (heroOn)
        {
            // the hero's screen: his sticks and buttons, Reset and the menu (the gun is picked there)
            topX = 8f;
            if (Button("Reset", 80f)) ResetAll();
            if (Button(menuOpen ? "Menu <" : "Menu >", 90f)) menuOpen = !menuOpen;
            if (Button(heroNoclip ? "Noclip: on" : "Noclip", 120f)) ToggleNoclip();
            if (Button(bikeState == 1 || bikeBuilding ? "Bike..." : bikeGo == null ? "Bike" : "Next bike", 110f)) BikeButton();
            if (Button("Settings", 100f)) OpenSettings();
            if (Button(browserOpen ? "Models <" : "Models", 100f)) OpenBrowser();
            FpsGui(w);
            if (browserOpen) { BrowserGui(w, h); return; } // (the sticks hidden under it)
            HeroGui(w, h);
            if (menuOpen) MenuGui(w, h);
            return;
        }
        // the tools along the bottom
        var bar = new Rect(8f, h - 56f, w - 16f, 48f);
        uiRects.Add(bar);
        tool = (Tool)GUI.Toolbar(bar, (int)tool, kToolNames, big);
        // the rest along the top
        topX = 8f;
        if (Button("Reset", 80f)) ResetAll();
        if (Button("+ Man (" + men.Count + ")", 110f)) AddMan();
        if (Button("+ Hostile", 110f)) AddHostile();
        if (Button(GtaAnim.State == 1 ? "Hero (loading)" : "Add hero", 130f)) AddHero();
        if (Button(speed < 1f ? "Slow-mo: on" : "Slow-mo: off", 130f)) SetSpeed(speed < 1f ? 1f : 0.25f);
        if (tool == Tool.Shoot && Button("Gun: " + weaponLabels[weapon % weaponLabels.Length], 170f)) weapon = (weapon + 1) % weapons.Length;
        if (Button(menuOpen ? "Menu <" : "Menu >", 90f)) menuOpen = !menuOpen;
        if (Button("Settings", 100f)) OpenSettings();
        if (Button(browserOpen ? "Models <" : "Models", 100f)) OpenBrowser();
        FpsGui(w);
        if (Button("Paths", 80f))
        {
            PlayerPrefs.SetInt(kPrefOk, 0);
            PlayerPrefs.Save();
            note = "The launcher shows the next time the game starts.";
        }
        if (note != "") GUI.Label(new Rect(8f, 88f, Mathf.Min(w - 16f, 560f), 26f), note, chip);
        if (Sel != null)
        {
            var m = Sel;
            var st = nm.State(m.Body.transform);
            string what = m.Killed || st.Dead ? "dead" : m.Rising ? "getting up" : m.FireLeft > 0f ? "burning" : st.Fallen ? "down" : st.Balancing ? "on his feet" : "ragdoll";
            GUI.Label(new Rect(8f, 60f, 300f, 24f), m.Body.name + "  ·  " + what + "  ·  health " + (god ? "unlimited" : Mathf.RoundToInt(m.Health).ToString()), chip);
        }
        if (browserOpen) BrowserGui(w, h);
        else if (menuOpen) MenuGui(w, h);
    }

    float topX;
    bool Button(string text, float width)
    {
        var r = new Rect(topX, 8f, width, 40f);
        uiRects.Add(r);
        topX += width + 6f;
        return GUI.Button(r, text, big);
    }

    void LauncherGui(float w, float h)
    {
        float bw = Mathf.Min(w - 60f, 720f), x = (w - bw) * 0.5f, y = Mathf.Max(30f, h * 0.5f - 180f);
        GUI.Box(new Rect(x - 22f, y - 20f, bw + 44f, 380f), GUIContent.none, panel);
        GUI.Label(new Rect(x, y, bw, 36f), "Delirium Sandbox", title);
        y += 50f;
        GUI.Label(new Rect(x, y, bw, 24f), "Your own game: default.xex (or GTAIV.exe, or the folder it's in)", label);
        y += 26f;
        gameExe = GUI.TextField(new Rect(x, y, bw, 32f), gameExe ?? "", field);
        y += 44f;
        GUI.Label(new Rect(x, y, bw, 24f), "The folder with character.xrt, build.txt and taskparams.txt", label);
        y += 26f;
        dataDir = GUI.TextField(new Rect(x, y, bw, 32f), dataDir ?? "", field);
        y += 48f;
        if (GUI.Button(new Rect(x, y, 160f, 44f), "Start", big)) Launch();
        // the iPhone: a folder with default.xex (and xbox360.rpf, xbox360/anim) and the three exported files, from the Files app
        if (CanPick && GUI.Button(new Rect(x + 176f, y, 220f, 44f), "Choose folder...", big)) ChooseFolder();
        y += 56f;
        if (error != "") GUI.Label(new Rect(x, y, bw, 120f), error, new GUIStyle(label) { normal = { textColor = new Color(1f, 0.45f, 0.4f) } });
    }
}
