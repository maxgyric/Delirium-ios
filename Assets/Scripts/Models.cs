// Your own files: a character (a Mixamo-rigged .glb) for the hero or for everyone else, and a map (any .glb, its
// meshes the collision). They're read from a folder: on an iPhone the app's own folder in Files ("On My iPhone" >
// Delirium Sandbox), where you copy them; on a PC any folder you type.
using System;
using System.Collections.Generic;
using System.IO;
using Delirium.Ragdoll;
using UnityEngine;

public partial class Game
{
    const string kPrefMapScale = "delirium.mapScale";
    string mapPath = "";
    float mapScale = 1f;       // your x2 / /2 on top of the file's own size
    const string kPrefFiles = "delirium.filesDir", kPrefHeroModel = "delirium.heroModel", kPrefNpcModel = "delirium.npcModel", kPrefMap = "delirium.map";
    GameObject heroTemplate, npcTemplate, mapRoot;
    string heroModelName = "", npcModelName = "", mapName = "";
    string filesDir, filesNote = "";
    string[] files = new string[0];
    Vector3 spawn;                 // where the men stand on the map
    Material matModel;

    Material ModelMaterial()
    {
        if (matModel == null)
        {
            var lit = Resources.Load<Material>("DeliriumLit"); // Delirium/Lit: vertex colours, cut-outs, two faces
            matModel = lit != null ? new Material(lit) : Mat(Color.white);
        }
        return matModel;
    }

    // A man's body: your model for his part if one is loaded, else the mannequin.
    Mannequin MakeBody(string name, Vector3 at, float yaw, bool heroPart)
    {
        var template = heroPart ? heroTemplate : npcTemplate;
        if (template != null)
        {
            try { return Mannequin.FromTemplate(template, name, at, yaw); }
            catch (Exception e) { filesNote = "The model didn't make a man: " + e.Message; }
        }
        if (matHero == null) matHero = Mat(new Color(0.25f, 0.55f, 0.95f));
        return Mannequin.Create(name, at, yaw, heroPart ? matHero : matBody);
    }

    void FilesInit()
    {
        filesDir = PlayerPrefs.GetString(kPrefFiles, Application.persistentDataPath);
        if (Application.isMobilePlatform)
        {
            // the phone: characters and maps in Delirium Sandbox/mods (any subfolders), apart from the game's files
            try { Directory.CreateDirectory(ModsDir()); } catch (Exception) { }
            filesDir = ModsDir();
        }
        RefreshFiles();
        // what was in use the last time (a file that fails now is forgotten, not tried at every start)
        mapPath = PlayerPrefs.GetString(kPrefMap, "");
        mapScale = PlayerPrefs.GetFloat(kPrefMapScale, 1f); // the last map at the scale it had
        string[] prefs = { kPrefMap, kPrefNpcModel, kPrefHeroModel };
        int[] kinds = { 2, 1, 0 };
        for (int i = 0; i < prefs.Length; i++)
        {
            string path = PlayerPrefs.GetString(prefs[i], "");
            if (path == "") continue;
            if (!File.Exists(path) || !Use(path, kinds[i])) PlayerPrefs.DeleteKey(prefs[i]);
        }
        filesNote = "";
    }

    static string ModsDir() => Path.Combine(Application.persistentDataPath, "mods");

    void RefreshFiles()
    {
        try
        {
            var found = new List<string>();
            if (Directory.Exists(filesDir))
            {
                // its subfolders too (mods/maps, mods/characters...)
                found.AddRange(Directory.GetFiles(filesDir, "*.glb", SearchOption.AllDirectories));
                found.AddRange(Directory.GetFiles(filesDir, "*.vrm", SearchOption.AllDirectories)); // VRM avatars are .glb inside
            }
            // on the phone: ones still loose next to the game's files
            if (Application.isMobilePlatform && Directory.Exists(Application.persistentDataPath))
            {
                found.AddRange(Directory.GetFiles(Application.persistentDataPath, "*.glb"));
                found.AddRange(Directory.GetFiles(Application.persistentDataPath, "*.vrm"));
            }
            files = found.ToArray();
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(filesDir)) filesNote = "No such folder.";
        }
        catch (Exception e)
        {
            files = new string[0];
            filesNote = e.Message;
        }
    }

    // A file put to use: 0 the hero's model, 1 everyone else's, 2 the map. False: it couldn't be (filesNote says why).
    bool Use(string path, int kind)
    {
        try
        {
            byte[] data = File.ReadAllBytes(path);
            string name = Path.GetFileNameWithoutExtension(path);
            var made = GlbScene.Load(data, name, ModelMaterial());
            if (kind == 2)
            {
                if (made.Triangles == 0)
                {
                    Destroy(made.Root);
                    filesNote = name + ": no meshes in the file.";
                    return false;
                }
                if (path != mapPath) mapScale = 1f; // another map: its own size
                mapPath = path;
                PlayerPrefs.SetFloat(kPrefMapScale, mapScale);
                SetMap(made.Root, name);
                mapTextures = made.Owned.FindAll(o => o is Texture2D).ConvertAll(o => (Texture2D)o);
                // pixel art (a Minecraft map): crisp texels unless you say otherwise
                int small = mapTextures.FindAll(t => Mathf.Max(t.width, t.height) <= 256).Count;
                pixelTextures = mapTextures.Count > 0 && small * 2 >= mapTextures.Count;
                Pixels();
                PlayerPrefs.SetString(kPrefMap, path);
                filesNote = "Map: " + name + " (" + made.Triangles + " triangles, " + made.Textures + " textures" +
                            (made.TexturesFailed > 0 ? ", " + made.TexturesFailed + " didn't load: " + made.TextureWhy : "") +
                            (made.SeeThrough > 0 ? ", " + made.SeeThrough + " see-through materials" : "") +
                            (made.Coloured > 0 ? ", shading in " + made.Coloured + " meshes" : "") + ").";
                Debug.Log("[Sandbox] " + filesNote + (Resources.Load<Material>("DeliriumLit") == null ? " (no Delirium/Lit material: the plain one)" : ""));
                return true;
            }
            var wrap = new GameObject("Template " + name);
            made.Root.transform.SetParent(wrap.transform, false);
            string wrong = Mannequin.Prepare(wrap);
            if (wrong != null)
            {
                Destroy(wrap);
                filesNote = name + ": " + wrong;
                return false;
            }
            // hair, breasts, skirts...: the file's VRM spring bones, or its extra bones picked by name
            string springs = "";
            try { springs = SpringBones.Setup(wrap, made); }
            catch (Exception e) { Debug.LogWarning("[Sandbox] physics bones: " + e.Message + "\n" + e.StackTrace); }
            wrap.SetActive(false);
            DontDestroyOnLoad(wrap);
            if (kind == 0)
            {
                if (heroTemplate != null) Destroy(heroTemplate);
                heroTemplate = wrap;
                heroModelName = name;
                PlayerPrefs.SetString(kPrefHeroModel, path);
            }
            else
            {
                if (npcTemplate != null) Destroy(npcTemplate);
                npcTemplate = wrap;
                npcModelName = name;
                PlayerPrefs.SetString(kPrefNpcModel, path);
            }
            Rebody(kind == 0);
            filesNote = (kind == 0 ? "The hero is " : "Everyone else is ") + name + " (" + made.Triangles + " triangles" +
                        (springs != "" ? ", " + springs : "") + ").";
            Debug.Log("[Sandbox] " + filesNote);
            return true;
        }
        catch (Exception e)
        {
            filesNote = Path.GetFileName(path) + ": " + e.Message;
            Debug.LogWarning("[Sandbox] " + filesNote + "\n" + e.StackTrace);
            return false;
        }
    }

    List<Texture2D> mapTextures = new List<Texture2D>();
    bool pixelTextures;

    // The map's textures sharp (each texel a square: pixel art) or smooth.
    void Pixels()
    {
        foreach (var t in mapTextures)
            if (t != null)
            {
                t.filterMode = pixelTextures ? FilterMode.Point : FilterMode.Trilinear;
                t.anisoLevel = pixelTextures ? 0 : 4;
            }
    }

    void DefaultModel(bool heroPart)
    {
        if (heroPart)
        {
            if (heroTemplate != null) Destroy(heroTemplate);
            heroTemplate = null;
            heroModelName = "";
            PlayerPrefs.DeleteKey(kPrefHeroModel);
        }
        else
        {
            if (npcTemplate != null) Destroy(npcTemplate);
            npcTemplate = null;
            npcModelName = "";
            PlayerPrefs.DeleteKey(kPrefNpcModel);
        }
        Rebody(heroPart);
        filesNote = (heroPart ? "The hero" : "Everyone else") + ": the mannequin again.";
    }

    // Every man of that part in a new body, where he stood.
    void Rebody(bool heroPart)
    {
        Drop();
        foreach (var m in men)
        {
            if (m.Hero != heroPart) continue;
            var old = m.Body;
            bool ragdolled = nm.State(old.transform).Ragdolled;
            Vector3 at = m.Rising || !ragdolled ? old.transform.position : Feet(m);
            float yaw = Yaw(m);
            if (ragdolled) nm.GetUp(old.transform, 0.001f);
            nm.ClearOverrides(old.transform);
            string name = old.name;
            old.gameObject.SetActive(false);
            Destroy(old.gameObject);
            m.Body = MakeBody(name, at, yaw, heroPart);
            m.Health = 200f;
            m.Down = m.FireLeft = 0f;
            m.Killed = m.Rising = m.Broken = m.Cut = m.Curled = false;
            m.Touch = null;
            foreach (var e in paramEdits)
            {
                int dot = e.Key.IndexOf('.');
                if (dot > 0) nm.SetOverride(m.Body.transform, e.Key.Substring(0, dot), e.Key.Substring(dot + 1), e.Value);
            }
            if (m.Hero)
            {
                // what hung on the old body
                gunModel = null;
                gunShown = -1;
                heroRb = null;
                HeroBones();
                HeroPlace(at, yaw);
            }
        }
    }

    // ------------------------------------------------------------------ the map
    void SetMap(GameObject root, string name)
    {
        ClearLevel();
        mapRoot = root;
        mapName = name;
        // a file in centimetres: a hundred times a town
        Bounds b = MapBounds();
        if (b.size.magnitude > 4000f) root.transform.localScale *= 0.01f;
        root.transform.localScale *= mapScale;
        foreach (var mf in root.GetComponentsInChildren<MeshFilter>())
        {
            if (mf.sharedMesh == null || mf.sharedMesh.GetIndexCount(0) < 3) continue;
            var col = mf.gameObject.AddComponent<MeshCollider>();
            col.sharedMesh = mf.sharedMesh;
            level.Add(col);
            DeliriumRagdolls.SandboxWarm(col); // its triangles ready for Delirium (no hitch at the first ragdoll)
        }
        // the map never moves: its meshes drawn as a few big batches (one per material) instead of one call each.
        // The same picture; a map of hundreds of pieces far cheaper to draw. (Its colliders keep their own meshes.)
        StaticBatchingUtility.Combine(root);
        PlaceOnMap();
    }

    Bounds MapBounds()
    {
        var b = new Bounds(Vector3.zero, Vector3.zero);
        bool any = false;
        if (mapRoot != null)
            foreach (var r in mapRoot.GetComponentsInChildren<Renderer>())
            {
                if (any) b.Encapsulate(r.bounds);
                else b = r.bounds;
                any = true;
            }
        return b;
    }

    // The men onto the map: at its node named "Spawn" if it has one, else on top of its middle.
    void PlaceOnMap()
    {
        Physics.SyncTransforms();
        Bounds b = MapBounds();
        // a floor under the whole map (nothing falls for ever), the level read only around each man
        DeliriumRagdolls.SandboxFloor = b.min.y - 1f;
        DeliriumRagdolls.SandboxFloorSurface = null;
        DeliriumRagdolls.SandboxLevelRadius = 14f; // read round each ragdoll, more as it moves (a ragdoll goes nowhere fast)
        ice = false;
        spawn = b.center;
        Transform mark = null;
        foreach (var t in mapRoot.GetComponentsInChildren<Transform>())
            if (t.name.ToLowerInvariant().Contains("spawn")) { mark = t; break; }
        if (mark != null) spawn = mark.position;
        else
        {
            // down from above the middle; if that misses (a hole there), from a few places round it
            bool found = false;
            for (int k = 0; k < 9 && !found; k++)
            {
                Vector3 off = k == 0 ? Vector3.zero : Quaternion.Euler(0f, k * 45f, 0f) * new Vector3(Mathf.Min(b.extents.x, b.extents.z) * 0.35f, 0f, 0f);
                if (Physics.Raycast(new Vector3(b.center.x, b.max.y + 5f, b.center.z) + off, Vector3.down, out var hit, b.size.y + 20f))
                {
                    spawn = hit.point;
                    found = true;
                }
            }
        }
        if (Physics.Raycast(spawn + Vector3.up * 2f, Vector3.down, out var under, 50f)) spawn = under.point;
        for (int i = 0; i < men.Count; i++)
        {
            men[i].Home = HomeOf(i);
            Restand(men[i], men[i].Home, men[i].Yaw);
        }
        camTarget = spawn + Vector3.up;
    }

    // The i-th man's place: the first at the spawn, the rest in a ring round it, each on the ground under him.
    Vector3 HomeOf(int i)
    {
        Vector3 home = spawn + (i == 0 ? Vector3.zero : Quaternion.Euler(0f, (i - 1) * 72f, 0f) * new Vector3(0f, 0f, 2.2f));
        // on a loaded map: the ground under him. The sandbox's own is flat (and its boxes, made this very frame, are
        // still unit cubes at the origin to the physics: a ray down found one's top, half a metre up)
        if (mapRoot == null) return home;
        Physics.SyncTransforms();
        if (Physics.Raycast(home + Vector3.up * 2.5f, Vector3.down, out var hit, 30f)) home.y = hit.point.y;
        return home;
    }

    void ClearLevel()
    {
        foreach (var c in level)
            if (c != null) Destroy(c.gameObject);
        level.Clear();
        if (mapRoot != null) Destroy(mapRoot);
        mapRoot = null;
        mapName = "";
    }

    void DefaultMap()
    {
        ClearLevel();
        PlayerPrefs.DeleteKey(kPrefMap);
        mapPath = "";
        mapScale = 1f;
        BuildLevel();
        DeliriumRagdolls.SandboxFloor = 0f;
        DeliriumRagdolls.SandboxFloorSurface = null;
        DeliriumRagdolls.SandboxLevelRadius = 20f;
        ice = false;
        spawn = Vector3.zero;
        Physics.SyncTransforms();
        for (int i = 0; i < men.Count; i++)
        {
            men[i].Home = HomeOf(i);
            Restand(men[i], men[i].Home, men[i].Yaw);
        }
        filesNote = "The sandbox's own map again.";
    }

    // A batched map can't be resized in place: it's read again from its file at the new size.
    void ScaleMap(float k)
    {
        if (mapRoot == null || mapPath == "" || !File.Exists(mapPath)) return;
        mapScale *= k;
        bool px = pixelTextures;
        if (!Use(mapPath, 2)) return;
        pixelTextures = px;
        Pixels();
        filesNote = "Map scale x" + mapScale.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------ the menu's tab
    void FilesTab()
    {
        Head("Your files");
        Info("Characters: a rigged human as .glb or .vrm: Mixamo, VRM / Unity humanoid, Unreal, Source, Biped, GTA V, Rigify skeletons " +
             "(a .fbx: in Blender File > Import > FBX, then File > Export > glTF 2.0, format glTF Binary). " +
             "Maps: any .glb; its meshes are the ground and walls. An object named Spawn marks where you start.");
        if (Application.isMobilePlatform)
            Info("Put them in the Files app: On My iPhone > Delirium Sandbox > mods (folders inside it are fine, e.g. mods/maps). Then Refresh.");
        else
        {
            Info("The folder they're in (its subfolders too):");
            // a fixed width: a long path would make the whole menu wider than the screen
            string dir = GUILayout.TextField(filesDir ?? "", txt, GUILayout.Height(26f), GUILayout.Width(kPanel - 64f));
            if (dir != filesDir) filesDir = dir;
        }
        if (GUILayout.Button("Refresh", btn, GUILayout.Height(32f)))
        {
            filesDir = filesDir.Trim().Trim('"');
            if (!Application.isMobilePlatform) PlayerPrefs.SetString(kPrefFiles, filesDir);
            filesNote = "";
            RefreshFiles();
        }
        if (filesNote != "") GUILayout.Label(filesNote, lab);

        Head("In use");
        InUse("Hero", heroModelName, () => DefaultModel(true));
        InUse("Everyone else", npcModelName, () => DefaultModel(false));
        InUse("Map", mapName, DefaultMap);
        if (mapRoot != null)
        {
            bool px = Tick(pixelTextures, "Pixel textures (sharp, for Minecraft-like maps)");
            if (px != pixelTextures)
            {
                pixelTextures = px;
                Pixels();
            }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Map 2x smaller", btn, GUILayout.Height(30f))) ScaleMap(0.5f);
            if (GUILayout.Button("Map 2x bigger", btn, GUILayout.Height(30f))) ScaleMap(2f);
            GUILayout.EndHorizontal();
        }

        Head(files.Length + " file" + (files.Length == 1 ? "" : "s"));
        foreach (string f in files)
        {
            long size = 0;
            try { size = new FileInfo(f).Length; } catch (Exception) { }
            GUILayout.Label(ShownName(f) + "  (" + (size / 1048576f).ToString("0.0") + " MB)", lab);
            GUILayout.BeginHorizontal();
            int pick = -1;
            if (GUILayout.Button("Hero", btn, GUILayout.Height(30f))) pick = 0;
            if (GUILayout.Button("Everyone else", btn, GUILayout.Height(30f))) pick = 1;
            if (GUILayout.Button("Map", btn, GUILayout.Height(30f))) pick = 2;
            GUILayout.EndHorizontal();
            if (pick >= 0)
            {
                Use(f, pick);
                GUIUtility.ExitGUI();
            }
        }
    }

    // where it is under mods (maps/city.glb); one outside it says so
    string ShownName(string f)
    {
        string root = (filesDir ?? "").TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
        if (f.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return f.Substring(root.Length).Replace('\\', '/');
        return Path.GetFileName(f) + (Application.isMobilePlatform ? " (not in mods)" : "");
    }

    void InUse(string what, string name, Action toDefault)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(what + ": " + (name == "" ? "the sandbox's own" : name), lab);
        if (name != "" && GUILayout.Button("Default", btn, GUILayout.Width(80f), GUILayout.Height(28f)))
        {
            toDefault();
            GUIUtility.ExitGUI();
        }
        GUILayout.EndHorizontal();
    }
}
