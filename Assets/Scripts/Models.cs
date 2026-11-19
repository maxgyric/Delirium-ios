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
            if (!File.Exists(FileOf(path)) || !Use(path, kinds[i])) PlayerPrefs.DeleteKey(prefs[i]);
        }
        filesNote = "";
    }

    static string ModsDir() => Path.Combine(Application.persistentDataPath, "mods");

#if UNITY_IOS && !UNITY_EDITOR
    const string BspDll = "__Internal";
#elif UNITY_ANDROID && !UNITY_EDITOR
    const string BspDll = "delirium";
#else
    const string BspDll = "delirium_bsp";
#endif
    [System.Runtime.InteropServices.DllImport(BspDll)] static extern int dbsp_convert(byte[] bsp, byte[] glb, byte[] roots, int maxSide);
    [System.Runtime.InteropServices.DllImport(BspDll)] static extern int dbsp_error(byte[] text, int cap);

    // a GMod map: a .bsp, or a Workshop addon (.gma) with one in it; one of an addon's several: "x.gma#maps/y.bsp"
    // a Workshop addon's player model ("addon.gma#models/x.mdl")
    static bool IsMdl(string path) => path.IndexOf(".gma#", StringComparison.OrdinalIgnoreCase) >= 0 && path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase);
    static bool IsBsp(string path) => path.EndsWith(".bsp", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".gma", StringComparison.OrdinalIgnoreCase);
    static string FileOf(string path)
    {
        int k = path.IndexOf(".gma#", StringComparison.OrdinalIgnoreCase);
        return k < 0 ? path : path.Substring(0, k + 4);
    }
    // which of an addon's maps ("gm_x"), or null
    static string MapOf(string path)
    {
        int k = path.IndexOf(".gma#", StringComparison.OrdinalIgnoreCase);
        return k < 0 ? null : Path.GetFileNameWithoutExtension(path.Substring(k + 5));
    }

    [System.Runtime.InteropServices.DllImport(BspDll)] static extern int dgma_maps(byte[] gma, byte[] text, int cap);
    [System.Runtime.InteropServices.DllImport(BspDll)] static extern int dgma_models(byte[] gma, byte[] text, int cap);
    [System.Runtime.InteropServices.DllImport(BspDll)] static extern int dmdl_convert(byte[] spec, byte[] glb, byte[] roots, int maxSide);

    // A player model from an addon as a rigged .glb (cached like a map): its bytes, or null and why
    static byte[] MdlToGlb(string path, out string note)
    {
        note = "";
        var fi = new FileInfo(FileOf(path));
        string cacheName = Path.GetFileNameWithoutExtension(FileOf(path)) + "-" + Path.GetFileNameWithoutExtension(path);
        string glb = Path.Combine(Application.temporaryCachePath, cacheName + "_" + fi.Length + "_" + fi.LastWriteTimeUtc.Ticks + ".mdl.glb");
        string info = glb + ".txt";
        if (File.Exists(glb))
        {
            try { note = File.Exists(info) ? File.ReadAllText(info) : "(cached)"; } catch (Exception) { }
            return File.ReadAllBytes(glb);
        }
        try
        {
            foreach (var old in Directory.GetFiles(Application.temporaryCachePath, cacheName + "_*.mdl.glb*")) File.Delete(old);
        }
        catch (Exception) { }
        string roots = ModsDir() + "|" + Path.GetDirectoryName(FileOf(path)) + "|" + Application.persistentDataPath;
        int r;
        try
        {
            r = dmdl_convert(System.Text.Encoding.UTF8.GetBytes(path + "\0"), System.Text.Encoding.UTF8.GetBytes(glb + "\0"),
                System.Text.Encoding.UTF8.GetBytes(roots + "\0"), 1024);
        }
        catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
        {
            note = "this build can't read GMod player models";
            return null;
        }
        var buf = new byte[2048];
        int n = dbsp_error(buf, buf.Length);
        note = System.Text.Encoding.UTF8.GetString(buf, 0, Mathf.Max(0, n));
        if (r < 0) return null;
        try { File.WriteAllText(info, note); } catch (Exception) { }
        return File.ReadAllBytes(glb);
    }

    // A Garry's Mod (Source) map as a .glb (the native library reads the .bsp and finds its textures: in the map, in
    // the game's packs (*_dir.vpk with their _000.vpk...) and "materials" folders you put anywhere in mods): its bytes,
    // or null and why. Kept in the cache till the map or the packs change (its textures take a while to make).
    static string bspEnv; // the last converted map's sky, fog and sun (MapLook)

    static byte[] BspToGlb(string path, out string note)
    {
        bspEnv = null;
        bspEnts = null;
        note = "";
        string mods = ModsDir();
        long stamp = 0;
        try
        {
            if (Directory.Exists(mods))
                foreach (var f in Directory.GetFiles(mods, "*.vpk", SearchOption.AllDirectories)) stamp += new FileInfo(f).Length % 1000003;
        }
        catch (Exception) { }
        var fi = new FileInfo(FileOf(path));
        string cacheName = Path.GetFileNameWithoutExtension(FileOf(path)) + (MapOf(path) != null ? "-" + MapOf(path) : "");
        // (_e1: a map's entities beside it, its moving parts in meshes of their own)
        string glb = Path.Combine(Application.temporaryCachePath, cacheName + "_" + fi.Length + "_" + fi.LastWriteTimeUtc.Ticks + "_" + stamp + "_e2.bsp.glb");
        bspEnts = glb + ".ents";
        string info = glb + ".txt";
        if (File.Exists(glb))
        {
            try { note = File.Exists(info) ? File.ReadAllText(info) : "(cached)"; } catch (Exception) { }
            bspEnv = glb + ".env";
            return File.ReadAllBytes(glb);
        }
        // older conversions of this map: gone
        try
        {
            foreach (var old in Directory.GetFiles(Application.temporaryCachePath, cacheName + "_*.bsp.glb*")) File.Delete(old);
            foreach (var old in Directory.GetDirectories(Application.temporaryCachePath, cacheName + "_*.bsp.glb.sounds")) Directory.Delete(old, true);
        }
        catch (Exception) { }
        string roots = mods + "|" + Path.GetDirectoryName(FileOf(path)) + "|" + Application.persistentDataPath;
        int r;
        try
        {
            r = dbsp_convert(System.Text.Encoding.UTF8.GetBytes(path + "\0"), System.Text.Encoding.UTF8.GetBytes(glb + "\0"),
                System.Text.Encoding.UTF8.GetBytes(roots + "\0"), 512);
        }
        catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
        {
            note = "this build can't read GMod maps";
            return null;
        }
        var buf = new byte[2048];
        int n = dbsp_error(buf, buf.Length);
        note = System.Text.Encoding.UTF8.GetString(buf, 0, Mathf.Max(0, n));
        if (r < 0) return null;
        try { File.WriteAllText(info, note); } catch (Exception) { }
        bspEnv = glb + ".env";
        return File.ReadAllBytes(glb);
    }

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
                found.AddRange(Directory.GetFiles(filesDir, "*.bsp", SearchOption.AllDirectories)); // Garry's Mod / Source maps
                found.AddRange(Directory.GetFiles(filesDir, "*.gma", SearchOption.AllDirectories)); // Workshop addons with a map
            }
            // VRChat packages to import (put there by hand)
            var pk = new List<string>();
            foreach (string dir in new[] { filesDir, Application.persistentDataPath })
                if (Directory.Exists(dir))
                    foreach (string f in Directory.GetFiles(dir, "*.unitypackage", SearchOption.AllDirectories))
                        if (!pk.Contains(f)) pk.Add(f);
            packages = pk.ToArray();
            // on the phone: ones still loose next to the game's files
            if (Application.isMobilePlatform && Directory.Exists(Application.persistentDataPath))
            {
                found.AddRange(Directory.GetFiles(Application.persistentDataPath, "*.glb"));
                found.AddRange(Directory.GetFiles(Application.persistentDataPath, "*.vrm"));
                found.AddRange(Directory.GetFiles(Application.persistentDataPath, "*.bsp"));
                found.AddRange(Directory.GetFiles(Application.persistentDataPath, "*.gma"));
            }
            // your Garry's Mod addons' folder (ModShelf's...), read where it is: its addons and maps, every subfolder
            if (gmodDir != "" && Directory.Exists(gmodDir))
            {
                found.AddRange(Directory.GetFiles(gmodDir, "*.gma", SearchOption.AllDirectories));
                found.AddRange(Directory.GetFiles(gmodDir, "*.bsp", SearchOption.AllDirectories));
                // (on the Quest it's your mods folder anywhere: characters and .glb maps too)
                if (Application.platform == RuntimePlatform.Android)
                {
                    found.AddRange(Directory.GetFiles(gmodDir, "*.glb", SearchOption.AllDirectories));
                    found.AddRange(Directory.GetFiles(gmodDir, "*.vrm", SearchOption.AllDirectories));
                }
            }
            // an addon with several maps: each of them
            var listed = new List<string>();
            foreach (var f in found)
            {
                if (f.EndsWith(".gma", StringComparison.OrdinalIgnoreCase)) listed.AddRange(GmaEntries(f));
                else listed.Add(f);
            }
            var unique = new List<string>();
            var had = new HashSet<string>();
            foreach (var f in listed) if (had.Add(f)) unique.Add(f);
            files = unique.ToArray();
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
        if (kind != 2 || mapRoot == null) return UseFile(path, kind);
        // the map in use gone first, there and then: its meshes and textures and the next map's never in memory
        // together (map after map, a phone ran out of it and the game was closed)
        FreeMapNow();
        if (UseFile(path, kind)) return true;
        string why = filesNote;
        DefaultMap();
        filesNote = why;
        return false;
    }

    static string Mem() => (UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / 1048576) + " MB (reserved " + (UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong() / 1048576) + " MB)";

    void FreeMapNow()
    {
        Debug.Log("[Sandbox] map " + mapName + " freed (memory " + Mem() + ")");
        // the meshes its renderers draw: its own and the batches made of them (not freed with their objects)
        var meshes = new HashSet<Mesh>();
        foreach (var mf in mapRoot.GetComponentsInChildren<MeshFilter>(true))
            if (mf.sharedMesh != null) meshes.Add(mf.sharedMesh);
        DeliriumRagdolls.SandboxForget();
        EntsClear();
        DestroyImmediate(mapRoot);
        mapRoot = null;
        mapName = "";
        foreach (var c in level)
            if (c != null) DestroyImmediate(c.gameObject);
        level.Clear();
        if (mapOwned != null)
        {
            foreach (var o in mapOwned)
                if (o != null) DestroyImmediate(o);
            mapOwned = null;
        }
        foreach (var m in meshes)
            if (m != null) DestroyImmediate(m);
        mapTextures = new List<Texture2D>();
        DeliriumRagdolls.SandboxForget();
        GC.Collect();
        Resources.UnloadUnusedAssets();
    }

    bool UseFile(string path, int kind)
    {
        try
        {
            bool mdl = IsMdl(path);
            if (mdl && !modelNames.ContainsKey(path)) foreach (var _ in GmaEntries(FileOf(path))) { } // (its name)
            string name = mdl ? (modelNames.TryGetValue(path, out var mn) ? mn : Path.GetFileNameWithoutExtension(path)) : MapOf(path) ?? Path.GetFileNameWithoutExtension(path);
            bool source = IsBsp(path);
            if (source && kind != 2)
            {
                filesNote = name + ": a GMod map is a map, not a character.";
                return false;
            }
            if (mdl && kind == 2)
            {
                filesNote = name + ": a player model is a character, not a map.";
                return false;
            }
            string bspNote = "";
            if (kind == 2) Debug.Log("[Sandbox] map " + name + ": loading (memory " + Mem() + ")");
            byte[] data = source ? BspToGlb(path, out bspNote) : mdl ? MdlToGlb(path, out bspNote) : File.ReadAllBytes(path);
            if (mdl && data != null) Debug.Log("[Sandbox] GMod player model " + name + ": " + bspNote);
            if (kind == 2) Debug.Log("[Sandbox] map " + name + ": " + (data == null ? "not converted" : (data.Length / 1048576) + " MB file") + " (memory " + Mem() + ")");
            if (data == null)
            {
                filesNote = name + ": " + bspNote;
                return false;
            }
            GlbScene.CompressTextures = source;
            GlbScene.Made made;
            try { made = GlbScene.Load(data, name, ModelMaterial()); }
            finally { GlbScene.CompressTextures = false; }
            // an imported VRChat avatar: its toggles (on its own tree, before Prepare renames the bones)
            if (kind != 2)
                try { VrcLook.Attach(path, made); }
                catch (Exception e) { Debug.LogWarning("[Sandbox] toggles: " + e.Message); }
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
                SetMap(made.Root, name, source);
                mapOwned = made.Owned;
                // a GMod map lit by its own baked light: its shadows are in it, it casts none of the sun's (a big
                // map drawn again into the shadow map every frame cost a lot)
                if (source)
                    foreach (var r in made.Root.GetComponentsInChildren<Renderer>())
                        if (r.sharedMaterial != null && r.sharedMaterial.HasProperty("_LightMapOn") && r.sharedMaterial.GetFloat("_LightMapOn") > 0.5f)
                            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mapTextures = made.Owned.FindAll(o => o is Texture2D).ConvertAll(o => (Texture2D)o);
                // a GMod map's baked light stays smooth whatever the pixel look
                foreach (var r in made.Root.GetComponentsInChildren<Renderer>())
                    foreach (var m in r.sharedMaterials)
                        if (m != null && m.HasProperty("_LightMap") && m.GetTexture("_LightMap") is Texture2D lm) mapTextures.Remove(lm);
                // its sky, fog and sun (or the sandbox's own again)
                if (source) MapLook(bspEnv); else MapLook(null);
                StartCoroutine(MapIconLater(path)); // (its picture for the models browser, when it has none)
                // pixel art (a Minecraft map): crisp texels unless you say otherwise
                int small = mapTextures.FindAll(t => Mathf.Max(t.width, t.height) <= 256).Count;
                pixelTextures = mapTextures.Count > 0 && small * 2 >= mapTextures.Count;
                Pixels();
                PlayerPrefs.SetString(kPrefMap, path);
                if (source) Debug.Log("[Sandbox] GMod map " + name + ": " + bspNote);
                Debug.Log("[Sandbox] map " + name + " in (memory " + Mem() + ")");
                filesNote = "Map: " + name + (source ? " (GMod map: " + bspNote + ")" : "") + " (" + made.Triangles + " triangles, " + made.Textures + " textures" +
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
                if (heroTemplate != null) { Destroy(heroTemplate); Resources.UnloadUnusedAssets(); } // (its meshes and textures too)
                heroTemplate = wrap;
                heroModelName = name;
                PlayerPrefs.SetString(kPrefHeroModel, path);
            }
            else
            {
                if (npcTemplate != null) { Destroy(npcTemplate); Resources.UnloadUnusedAssets(); }
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
            m.Killed = m.Rising = m.Broken = m.Cut = m.Curled = m.Limp = m.VelSet = false;
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
    void SetMap(GameObject root, string name, bool metres = false)
    {
        ClearLevel();
        mapRoot = root;
        mapName = name;
        // a file in centimetres: a hundred times a town (a GMod map's already in metres: its 3D skybox, drawn 16 times
        // its size round the town, would have made it look like one)
        Bounds b = MapBounds();
        if (!metres && b.size.magnitude > 4000f) root.transform.localScale *= 0.01f;
        root.transform.localScale *= mapScale;
        // a GMod map's 3D skybox: its own camera draws it (MapLook)
        foreach (var r in root.GetComponentsInChildren<Renderer>())
            if (r.gameObject.name.Contains("[sky3d]")) r.gameObject.layer = Sky3DLayer;
        // a GMod map's working parts (buttons, doors, lifts...): their meshes gathered, one object each (MapEnts)
        if (metres) EntsBuild(root, bspEnts); else EntsClear();
        var still = new List<GameObject>();
        foreach (var mf in root.GetComponentsInChildren<MeshFilter>())
        {
            var partOf = ents.Count > 0 ? EntOfObject(mf.gameObject) : null;
            bool part = partOf != null;
            if (!part) still.Add(mf.gameObject);
            if (mf.sharedMesh == null || mf.sharedMesh.GetIndexCount(0) < 3) continue;
            if (mf.gameObject.name.Contains("[nocollide]")) continue; // a GMod map's water: drawn, not stood on
            var col = mf.gameObject.AddComponent<MeshCollider>();
            col.sharedMesh = mf.sharedMesh;
            if (EntMoves(partOf)) continue; // (one that moves: not in Delirium's still world; a moving box of its own)
            level.Add(col);
            DeliriumRagdolls.SandboxWarm(col); // its triangles ready for Delirium (no hitch at the first ragdoll)
        }
        // the map never moves: its meshes drawn as a few big batches (one per material) instead of one call each.
        // The same picture; a map of hundreds of pieces far cheaper to draw. (Its colliders keep their own meshes.)
        // Its working parts move: not batched.
        StaticBatchingUtility.Combine(still.ToArray(), root);
        PlaceOnMap();
        EntsStart();
    }

    Bounds MapBounds()
    {
        var b = new Bounds(Vector3.zero, Vector3.zero);
        bool any = false;
        if (mapRoot != null)
            foreach (var r in mapRoot.GetComponentsInChildren<Renderer>())
            {
                // a GMod map's sky (its 3D skybox round the town) and water: not the ground you play on
                if (r.gameObject.name.Contains("[nocollide]")) continue;
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
        DeliriumRagdolls.SandboxLevelRadius = 14f; // (while the whole map's world is being built: read round each ragdoll)
        DeliriumRagdolls.SandboxWholeLevel(level, b.min.y - 1f); // the whole map to Delirium at once, built in the background
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
        EntsClear();
        foreach (var c in level)
            if (c != null) Destroy(c.gameObject);
        level.Clear();
        if (mapRoot != null) Destroy(mapRoot);
        mapRoot = null;
        mapName = "";
        // its meshes, textures and materials too (made at run time: not freed with its objects; maps one after
        // another filled the memory till the game was closed), Delirium's copies of its triangles, then whatever's
        // left unused (the batched meshes)
        if (mapOwned != null)
        {
            foreach (var o in mapOwned)
                if (o != null) Destroy(o);
            mapOwned = null;
        }
        mapTextures = new List<Texture2D>();
        DeliriumRagdolls.SandboxForget();
        Resources.UnloadUnusedAssets();
        GC.Collect();
    }

    List<UnityEngine.Object> mapOwned; // the loaded map's meshes, textures, materials

    void DefaultMap()
    {
        ClearLevel();
        MapLook(null);
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
        if (mapRoot == null || mapPath == "" || !File.Exists(FileOf(mapPath))) return;
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
        if (GUILayout.Button("Models and maps (pictures)...", btnOn, GUILayout.Height(40f))) OpenBrowser();
        GmodFolderUi();
        Head("Your files");
        Info("Characters: a rigged human as .glb or .vrm: Mixamo, VRM / Unity humanoid, Unreal, Source, Biped, GTA V, Rigify skeletons " +
             "(a .fbx: in Blender File > Import > FBX, then File > Export > glTF 2.0, format glTF Binary). " +
             "Maps: any .glb; its meshes are the ground and walls. An object named Spawn marks where you start. " +
             "Garry's Mod / Source maps: a .bsp (garrysmod/maps) or a Workshop addon .gma with a map in it (steamapps/workshop/content/4000, garrysmod/cache/workshop). " +
             "Their textures: put GMod's .vpk packs (garrysmod_*.vpk, sourceengine/hl2_*.vpk...) in mods/gmod.");
        if (Application.isMobilePlatform)
            Info("Select model: pick .glb / .vrm files anywhere in the Files app: they're moved into Delirium Sandbox > mods (folders inside it are fine, e.g. mods/maps).");
        else
        {
            Info("The folder they're in (its subfolders too):");
            // a fixed width: a long path would make the whole menu wider than the screen
            string dir = GUILayout.TextField(filesDir ?? "", txt, GUILayout.Height(26f), GUILayout.Width(kPanel - 64f));
            if (dir != filesDir) filesDir = dir;
        }
        if (CanPick && GUILayout.Button("Select model...", btn, GUILayout.Height(36f))) SelectModels(); // moved into mods
        if (CanPick && GUILayout.Button("GMod map (.bsp / .gma)...", btn, GUILayout.Height(36f))) SelectModels(); // the same picker: they go to mods too
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

        if (HeroLook() != null) Info("The hero's VRChat menu: the Expr. button in hero mode.");
        VrcToggles(npcTemplate, "Everyone else");
        VrcImportUi();

        Head(files.Length + " file" + (files.Length == 1 ? "" : "s"));
        foreach (string f in files)
        {
            long size = 0;
            try { size = new FileInfo(FileOf(f)).Length; } catch (Exception) { }
            GUILayout.Label(ShownName(f) + "  (" + (size / 1048576f).ToString("0.0") + " MB)", lab);
            GUILayout.BeginHorizontal();
            int pick = -1;
            if (!IsBsp(f))
            {
                if (GUILayout.Button("Hero", btn, GUILayout.Height(30f))) pick = 0;
                if (GUILayout.Button("Everyone else", btn, GUILayout.Height(30f))) pick = 1;
            }
            if (!IsMdl(f) && GUILayout.Button(IsBsp(f) ? "Map (GMod)" : "Map", btn, GUILayout.Height(30f))) pick = 2;
            // a second tap deletes it
            if (GUILayout.Button(removeAsk == f ? "Sure?" : "Remove", removeAsk == f ? btnOn : btn, GUILayout.Width(80f), GUILayout.Height(30f)))
            {
                if (removeAsk == f) { RemoveFile(f); GUIUtility.ExitGUI(); }
                else removeAsk = f;
            }
            GUILayout.EndHorizontal();
            if (pick >= 0)
            {
                removeAsk = "";
                Use(f, pick);
                GUIUtility.ExitGUI();
            }
        }
    }

    string removeAsk = ""; // the file whose Remove was tapped once

    // A model or map deleted from the phone (with its VRChat toggles); in use: the default again first.
    void RemoveFile(string f)
    {
        removeAsk = "";
        if (InGmodDir(f)) { filesNote = "That one's in your GMod folder: remove it there (ModShelf...)."; return; }
        if (FileOf(PlayerPrefs.GetString(kPrefHeroModel, "")) == FileOf(f)) DefaultModel(true);
        if (FileOf(PlayerPrefs.GetString(kPrefNpcModel, "")) == FileOf(f)) DefaultModel(false);
        if (FileOf(mapPath) == FileOf(f)) DefaultMap();
        f = FileOf(f); // (an addon's map: the whole addon)
        try
        {
            File.Delete(f);
            string side = Path.Combine(Path.GetDirectoryName(f) ?? "", Path.GetFileNameWithoutExtension(f) + ".vrc.json");
            if (File.Exists(side)) File.Delete(side);
            PlayerPrefs.DeleteKey("delirium.vrc." + Path.GetFileNameWithoutExtension(f));
            filesNote = Path.GetFileName(f) + " removed.";
        }
        catch (Exception e) { filesNote = Path.GetFileName(f) + ": " + e.Message; }
        RefreshFiles();
    }

    // where it is under mods (maps/city.glb); one outside it says so
    string ShownName(string f)
    {
        // an addon's map or player model: its own name first (the file can be called anything: "temp.gma")
        if (IsMdl(f)) return (modelNames.TryGetValue(f, out var mn) ? mn : Path.GetFileNameWithoutExtension(f)) + "  (player model, " + ShownName(FileOf(f)) + ")";
        if (MapOf(f) != null) return MapOf(f) + "  (" + ShownName(FileOf(f)) + ")";
        if (InGmodDir(f)) return "GMod folder/" + f.Substring(gmodDir.TrimEnd('/', '\\').Length + 1).Replace('\\', '/');
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
