// The models browser (Garry's Mod's spawn menu, more or less): your player models and maps as a grid of pictures,
// the one you touch shown big on the right (a character turning in 3D, a drag turns him yourself) with what it can
// be: the hero, everyone else, the map.
//
// Pictures: a character's is drawn once, the first time the browser shows it (loaded, stood up by
// Mannequin.Prepare, photographed from the front and a little to the side on a layer of its own far under the
// world), and kept as a .png in the app's cache. A map's is its addon's own (maps/thumb/<map>.png, what GMod
// shows), a picture beside a .bsp (<map>.png, thumb/<map>.png), or else the view the first time it's loaded.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public partial class Game
{
    public const int PreviewLayer = 30;
    const float kTileW = 124f, kTileH = 152f;

    bool browserOpen;
    int browserTab; // 0 characters, 1 maps
    string browserSel = "", browserSearch = "", browserAsk = "";
    Vector2 browserScroll;
    readonly Dictionary<string, Texture2D> icons = new Dictionary<string, Texture2D>();
    readonly HashSet<string> iconTried = new HashSet<string>();   // looked for (or made, or failed) this run
    readonly Dictionary<string, int> fileKinds = new Dictionary<string, int>();
    float iconWait;
    Camera previewCam;
    RenderTexture previewRT;
    GameObject previewGo;
    readonly List<UnityEngine.Object> previewOwned = new List<UnityEngine.Object>();
    string previewOf = "";
    float previewYaw = 20f;
    bool previewDragging;
    GUIStyle tileName, tileNone, tileUse;

    [System.Runtime.InteropServices.DllImport(BspDll)] static extern int dgma_file(byte[] gma, byte[] path, byte[] outPath);

    void OpenBrowser()
    {
        browserOpen = !browserOpen;
        if (browserOpen)
        {
            menuOpen = false;
            RefreshFiles();
            browserAsk = "";
        }
        else ClosePreview();
    }

    // ------------------------------------------------------------------ what a file is, its name, its picture
    // 1 a character, 2 a map
    int KindOf(string f)
    {
        if (IsMdl(f)) return 1;
        if (IsBsp(f)) return 2;
        if (f.EndsWith(".vrm", StringComparison.OrdinalIgnoreCase)) return 1;
        if (fileKinds.TryGetValue(f, out int k)) return k;
        k = 2;
        try
        {
            // a .glb with a skin is a character, without one a map
            using (var s = File.OpenRead(f))
            {
                var h = new byte[20];
                if (s.Read(h, 0, 20) == 20 && h[0] == (byte)'g' && h[1] == (byte)'l' && h[2] == (byte)'T' && h[3] == (byte)'F')
                {
                    int n = BitConverter.ToInt32(h, 12);
                    var j = new byte[Mathf.Clamp(n, 0, 64 << 20)];
                    int got = 0;
                    while (got < j.Length) { int r = s.Read(j, got, j.Length - got); if (r <= 0) break; got += r; }
                    if (System.Text.Encoding.UTF8.GetString(j, 0, got).Contains("\"skins\"")) k = 1;
                }
            }
        }
        catch (Exception) { }
        fileKinds[f] = k;
        return k;
    }

    string NameOf(string f)
    {
        if (IsMdl(f)) return modelNames.TryGetValue(f, out var n) ? n : Path.GetFileNameWithoutExtension(f);
        return MapOf(f) ?? Path.GetFileNameWithoutExtension(f);
    }

    static string IconDir() => Path.Combine(Application.temporaryCachePath, "icons");

    // the picture's file: the entry and its file's size and date (a new version of the file, a new picture)
    static string IconPath(string f)
    {
        long len = 0, ticks = 0;
        try { var fi = new FileInfo(FileOf(f)); len = fi.Length; ticks = fi.LastWriteTimeUtc.Ticks; } catch (Exception) { }
        ulong h = 1469598103934665603UL;
        foreach (char c in f + "|" + len + "|" + ticks) { h ^= c; h *= 1099511628211UL; }
        return Path.Combine(IconDir(), h.ToString("x16") + ".png");
    }

    Texture2D IconOf(string f)
    {
        if (icons.TryGetValue(f, out var t)) return t;
        string p = IconPath(f);
        if (!File.Exists(p))
        {
            // a map's own picture, when it has one
            if (KindOf(f) == 2 && iconTried.Add(f + "|thumb")) FindMapThumb(f, p);
            if (!File.Exists(p)) return null;
        }
        try
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "icon " + NameOf(f) };
            if (tex.LoadImage(File.ReadAllBytes(p), true)) { icons[f] = tex; return tex; }
            Destroy(tex);
        }
        catch (Exception) { }
        icons[f] = null;
        return null;
    }

    void FindMapThumb(string f, string dest)
    {
        try
        {
            Directory.CreateDirectory(IconDir());
            string map = MapOf(f) ?? Path.GetFileNameWithoutExtension(f);
            if (f.IndexOf(".gma", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                foreach (string inside in new[] { "maps/thumb/" + map + ".png", "maps/" + map + ".png", "maps/thumb/" + map + ".jpg" })
                    if (dgma_file(System.Text.Encoding.UTF8.GetBytes(FileOf(f) + "\0"), System.Text.Encoding.UTF8.GetBytes(inside + "\0"), System.Text.Encoding.UTF8.GetBytes(dest + "\0")) > 0) return;
                return;
            }
            string dir = Path.GetDirectoryName(f) ?? "";
            foreach (string beside in new[] { Path.Combine(dir, map + ".png"), Path.Combine(dir, "thumb", map + ".png"), Path.Combine(dir, map + ".jpg") })
                if (File.Exists(beside)) { File.Copy(beside, dest, true); return; }
        }
        catch (Exception) { }
    }

    // ------------------------------------------------------------------ pictures of characters, made one at a time
    void BrowserUpdate()
    {
        if (!browserOpen) return;
        // the turning preview
        if (previewGo != null)
        {
            if (!previewDragging) previewYaw += 25f * Time.unscaledDeltaTime;
            previewGo.transform.rotation = Quaternion.Euler(0f, previewYaw, 0f);
            Photograph(previewGo, previewRT, previewYaw);
        }
        // the next character without a picture (a few frames apart: each is a load)
        iconWait -= Time.unscaledDeltaTime;
        if (iconWait > 0f || browserTab != 0) return;
        foreach (string f in Shown())
        {
            if (icons.TryGetValue(f, out var have) && have != null) continue;
            if (File.Exists(IconPath(f)) || !iconTried.Add(f)) continue;
            iconWait = 0.25f;
            try { MakeCharacterIcon(f); }
            catch (Exception e) { Debug.LogWarning("[Sandbox] picture of " + NameOf(f) + ": " + e.Message); }
            icons.Remove(f); // (read again from the file)
            return;
        }
    }

    // A character loaded and stood up as the sandbox will have him: null, or why not
    GameObject LoadForPreview(string f, List<UnityEngine.Object> owned)
    {
        string note;
        byte[] data = IsMdl(f) ? MdlToGlb(f, out note) : File.ReadAllBytes(f);
        if (data == null) return null;
        var made = GlbScene.Load(data, NameOf(f), ModelMaterial());
        owned.AddRange(made.Owned);
        var wrap = new GameObject("Preview " + NameOf(f));
        made.Root.transform.SetParent(wrap.transform, false);
        try { Mannequin.Prepare(wrap); } catch (Exception) { }
        // (Prepare's meshes bound anew: freed with it)
        foreach (var smr in wrap.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (smr.sharedMesh != null && !owned.Contains(smr.sharedMesh)) owned.Add(smr.sharedMesh);
            smr.updateWhenOffscreen = true;
        }
        foreach (var t in wrap.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = PreviewLayer;
        foreach (var c in wrap.GetComponentsInChildren<Collider>(true)) c.enabled = false;
        foreach (var r in wrap.GetComponentsInChildren<Renderer>(true)) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        wrap.transform.position = new Vector3(0f, -3000f, 0f);
        return wrap;
    }

    void FreePreview(GameObject go, List<UnityEngine.Object> owned)
    {
        owned.Remove(matModel); // (the models' shared material stays)
        if (go != null) DestroyImmediate(go);
        foreach (var o in owned)
            if (o != null) DestroyImmediate(o);
        owned.Clear();
    }

    void MakeCharacterIcon(string f)
    {
        var owned = new List<UnityEngine.Object>();
        var go = LoadForPreview(f, owned);
        if (go == null) return;
        try
        {
            go.transform.rotation = Quaternion.Euler(0f, 20f, 0f);
            var rt = RenderTexture.GetTemporary(256, 256, 24, RenderTextureFormat.ARGB32);
            Photograph(go, rt, 20f);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(256, 256, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, 256, 256), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            Directory.CreateDirectory(IconDir());
            File.WriteAllBytes(IconPath(f), tex.EncodeToPNG());
            Destroy(tex);
        }
        finally { FreePreview(go, owned); }
    }

    // The preview layer seen from in front of him (he faces +Z; `yaw` how far he's turned), all of him in the frame
    void Photograph(GameObject go, RenderTexture rt, float yaw)
    {
        if (previewCam == null)
        {
            var c = new GameObject("Preview camera");
            DontDestroyOnLoad(c);
            previewCam = c.AddComponent<Camera>();
            previewCam.enabled = false;
            previewCam.cullingMask = 1 << PreviewLayer;
            previewCam.clearFlags = CameraClearFlags.SolidColor;
            previewCam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            previewCam.fieldOfView = 28f;
            previewCam.nearClipPlane = 0.05f;
            previewCam.farClipPlane = 200f;
        }
        var rs = go.GetComponentsInChildren<Renderer>();
        if (rs.Length == 0) return;
        // his size standing (unturned), so a turn doesn't zoom the picture in and out
        var b = rs[0].bounds;
        foreach (var r in rs) b.Encapsulate(r.bounds);
        float tall = Mathf.Max(b.size.y, 0.3f);
        var centre = b.center;
        float dist = tall * 0.56f / Mathf.Tan(previewCam.fieldOfView * 0.5f * Mathf.Deg2Rad) + 0.3f;
        previewCam.transform.position = centre + new Vector3(0f, tall * 0.06f, dist);
        previewCam.transform.LookAt(centre);
        previewCam.targetTexture = rt;
        bool fog = RenderSettings.fog;
        RenderSettings.fog = false;
        previewCam.Render();
        RenderSettings.fog = fog;
        previewCam.targetTexture = null;
    }

    void ShowPreview(string f)
    {
        if (previewOf == f) return;
        ClosePreview();
        previewOf = f;
        if (KindOf(f) != 1) return;
        try
        {
            previewGo = LoadForPreview(f, previewOwned);
            if (previewGo == null) return;
            if (previewRT == null) previewRT = new RenderTexture(512, 512, 24, RenderTextureFormat.ARGB32) { name = "Preview", antiAliasing = 2 };
            previewYaw = 20f;
        }
        catch (Exception e) { filesNote = NameOf(f) + ": " + e.Message; }
    }

    void ClosePreview()
    {
        FreePreview(previewGo, previewOwned);
        previewGo = null;
        previewOf = "";
    }

    // After a map loads without a picture: the view, a moment later, is its picture
    IEnumerator MapIconLater(string f)
    {
        yield return new WaitForSecondsRealtime(0.8f);
        string p = IconPath(f);
        if (File.Exists(p) || cam == null) yield break;
        FindMapThumb(f, p);
        if (File.Exists(p)) yield break;
        var rt = RenderTexture.GetTemporary(384, 216, 24, RenderTextureFormat.ARGB32);
        var was = cam.targetTexture;
        if (skyCam != null && skyCam.enabled)
        {
            var sw = skyCam.targetTexture;
            skyCam.targetTexture = rt;
            skyCam.Render();
            skyCam.targetTexture = sw;
        }
        cam.targetTexture = rt;
        cam.Render();
        cam.targetTexture = was;
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(384, 216, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, 384, 216), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);
        try { Directory.CreateDirectory(IconDir()); File.WriteAllBytes(p, tex.EncodeToPNG()); } catch (Exception) { }
        Destroy(tex);
        icons.Remove(f);
    }

    // ------------------------------------------------------------------ the screen
    List<string> Shown()
    {
        var list = new List<string>();
        string q = browserSearch.Trim().ToLowerInvariant();
        foreach (string f in files)
        {
            if (KindOf(f) != (browserTab == 0 ? 1 : 2)) continue;
            if (q != "" && !NameOf(f).ToLowerInvariant().Contains(q) && !Path.GetFileName(FileOf(f)).ToLowerInvariant().Contains(q)) continue;
            list.Add(f);
        }
        list.Sort((a, b) => string.Compare(NameOf(a), NameOf(b), StringComparison.OrdinalIgnoreCase));
        return list;
    }

    string InUseAs(string f)
    {
        var said = new List<string>();
        if (PlayerPrefs.GetString(kPrefHeroModel, "") == f && heroModelName != "") said.Add("Hero");
        if (PlayerPrefs.GetString(kPrefNpcModel, "") == f && npcModelName != "") said.Add("Everyone");
        if (mapPath == f && mapName != "") said.Add("Map");
        return string.Join(" · ", said.ToArray());
    }

    void BrowserGui(float w, float h)
    {
        MenuStyles();
        if (tileName == null)
        {
            tileName = new GUIStyle(lab) { fontSize = 12, alignment = TextAnchor.UpperCenter, wordWrap = true, clipping = TextClipping.Clip };
            tileNone = new GUIStyle(dim) { alignment = TextAnchor.MiddleCenter, wordWrap = true };
            tileUse = new GUIStyle(chip) { fontSize = 11, alignment = TextAnchor.MiddleCenter };
        }
        var area = new Rect(8f, 60f, w - 16f, h - 68f);
        uiRects.Add(area);
        GUI.Box(area, GUIContent.none, panel);
        float x = area.x + 12f, y = area.y + 10f;

        // the top: the two kinds, a search, files and close
        if (GUI.Button(new Rect(x, y, 150f, 36f), "Player models", browserTab == 0 ? btnOn : btn) && browserTab != 0) { browserTab = 0; browserScroll = Vector2.zero; }
        if (GUI.Button(new Rect(x + 156f, y, 110f, 36f), "Maps", browserTab == 1 ? btnOn : btn) && browserTab != 1) { browserTab = 1; browserScroll = Vector2.zero; }
        float right = area.xMax - 12f;
        if (GUI.Button(new Rect(right - 44f, y, 44f, 36f), "X", btn)) { OpenBrowser(); return; }
        if (GUI.Button(new Rect(right - 140f, y, 90f, 36f), "Refresh", btn)) { RefreshFiles(); icons.Clear(); iconTried.Clear(); fileKinds.Clear(); }
        if (CanPick && GUI.Button(new Rect(right - 266f, y, 120f, 36f), "Add files...", btn)) SelectModels();
        float sx = x + 280f, sw = Mathf.Max(80f, right - 280f - sx);
        GUI.Label(new Rect(sx, y + 8f, 60f, 24f), "Search", dim);
        browserSearch = GUI.TextField(new Rect(sx + 58f, y + 4f, Mathf.Max(60f, sw - 64f), 30f), browserSearch, txt);
        y += 46f;

        // the detail on the right, the grid on the left
        float detailW = Mathf.Min(330f, area.width * 0.36f);
        var grid = new Rect(x, y, area.width - detailW - 36f, area.yMax - y - 10f);
        var detail = new Rect(grid.xMax + 12f, y, detailW, grid.height);
        var list = Shown();
        int per = Mathf.Max(1, Mathf.FloorToInt((grid.width - 14f) / (kTileW + 8f)));
        float contentH = Mathf.Ceil(list.Count / (float)per) * (kTileH + 8f) + 8f;
        // a finger dragged over the grid scrolls it
        var e = Event.current;
        if (e.type == EventType.MouseDrag && grid.Contains(e.mousePosition) && Mathf.Abs(e.delta.y) > Mathf.Abs(e.delta.x))
        {
            browserScroll.y = Mathf.Clamp(browserScroll.y - e.delta.y, 0f, Mathf.Max(0f, contentH - grid.height));
            e.Use();
        }
        browserScroll = GUI.BeginScrollView(grid, browserScroll, new Rect(0f, 0f, grid.width - 16f, contentH), false, true, GUIStyle.none, GUI.skin.verticalScrollbar);
        for (int i = 0; i < list.Count; i++)
        {
            var r = new Rect((i % per) * (kTileW + 8f), (i / per) * (kTileH + 8f), kTileW, kTileH);
            if (r.yMax < browserScroll.y - 10f || r.y > browserScroll.y + grid.height + 10f) continue; // off the screen
            string f = list[i];
            if (GUI.Button(r, GUIContent.none, f == browserSel ? btnOn : btn)) { browserSel = f; browserAsk = ""; }
            var pic = new Rect(r.x + 6f, r.y + 6f, r.width - 12f, r.width - 12f);
            var icon = IconOf(f);
            if (icon != null) GUI.DrawTexture(pic, icon, ScaleMode.ScaleToFit, true);
            else GUI.Label(pic, browserTab == 0 ? "…" : "no picture", tileNone);
            GUI.Label(new Rect(r.x + 4f, r.y + r.width - 4f, r.width - 8f, 40f), NameOf(f), tileName);
            string use = InUseAs(f);
            if (use != "") GUI.Label(new Rect(r.x + 4f, r.y + 4f, r.width - 8f, 20f), use, tileUse);
        }
        GUI.EndScrollView();
        if (list.Count == 0)
            GUI.Label(new Rect(grid.x, grid.y + 20f, grid.width, 80f), browserTab == 0
                ? "No player models yet. Put Workshop addons (.gma) with player models, or rigged .glb / .vrm characters, in Delirium Sandbox > mods (Add files...)."
                : "No maps yet. Put GMod maps (.bsp, or Workshop addons .gma) or .glb maps in Delirium Sandbox > mods (Add files...).", dim);

        DetailGui(detail);
    }

    void DetailGui(Rect d)
    {
        GUI.Box(d, GUIContent.none, chip);
        if (browserSel == "" || Array.IndexOf(files, browserSel) < 0)
        {
            GUI.Label(new Rect(d.x + 12f, d.y + 12f, d.width - 24f, 80f), "Touch a picture to see it here.", dim);
            if (previewGo != null) ClosePreview();
            return;
        }
        string f = browserSel;
        int kind = KindOf(f);
        float y = d.y + 10f, x = d.x + 10f, wd = d.width - 20f;
        if (kind == 1)
        {
            ShowPreview(f);
            var pv = new Rect(x, y, wd, wd);
            if (previewGo != null && previewRT != null)
            {
                GUI.DrawTexture(pv, previewRT, ScaleMode.ScaleToFit, true);
                // a drag across him turns him
                var e = Event.current;
                if (e.type == EventType.MouseDown && pv.Contains(e.mousePosition)) previewDragging = true;
                if (e.type == EventType.MouseUp) previewDragging = false;
                if (previewDragging && e.type == EventType.MouseDrag) { previewYaw -= e.delta.x * 0.8f; e.Use(); }
            }
            else
            {
                var icon = IconOf(f);
                if (icon != null) GUI.DrawTexture(pv, icon, ScaleMode.ScaleToFit, true);
            }
            y += wd + 6f;
        }
        else
        {
            if (previewGo != null) ClosePreview();
            var icon = IconOf(f);
            float ph = wd * 0.5625f;
            if (icon != null) GUI.DrawTexture(new Rect(x, y, wd, ph), icon, ScaleMode.ScaleToFit, true);
            else GUI.Label(new Rect(x, y, wd, ph), "Its picture is made the first time you load it.", tileNone);
            y += ph + 6f;
        }
        GUI.Label(new Rect(x, y, wd, 30f), NameOf(f), new GUIStyle(label) { fontStyle = FontStyle.Bold });
        y += 28f;
        long size = 0;
        try { size = new FileInfo(FileOf(f)).Length; } catch (Exception) { }
        string from = Path.GetFileName(FileOf(f)) + "  ·  " + (size / 1048576f).ToString("0.0") + " MB" + (IsMdl(f) ? "  ·  GMod player model" : IsBsp(f) ? "  ·  GMod map" : "");
        GUI.Label(new Rect(x, y, wd, 40f), from, dim);
        y += 40f;
        string use = InUseAs(f);
        if (use != "") { GUI.Label(new Rect(x, y, wd, 22f), "In use: " + use, lab); y += 24f; }
        if (kind == 1)
        {
            if (GUI.Button(new Rect(x, y, wd, 40f), "Be him (hero)", btn)) { Use(f, 0); }
            y += 46f;
            if (GUI.Button(new Rect(x, y, wd, 40f), "Everyone else", btn)) { Use(f, 1); }
            y += 46f;
        }
        else
        {
            if (GUI.Button(new Rect(x, y, wd, 40f), "Load map", btn))
            {
                Use(f, 2);
            }
            y += 46f;
        }
        if (GUI.Button(new Rect(x, y, wd, 34f), browserAsk == f ? "Sure? Deletes " + Path.GetFileName(FileOf(f)) : "Remove", browserAsk == f ? btnOn : btn))
        {
            if (browserAsk == f) { ClosePreview(); RemoveFile(f); browserSel = ""; browserAsk = ""; GUIUtility.ExitGUI(); }
            else browserAsk = f;
        }
        y += 40f;
        if (filesNote != "") GUI.Label(new Rect(x, y, wd, d.yMax - y - 6f), filesNote, dim);
    }
}
