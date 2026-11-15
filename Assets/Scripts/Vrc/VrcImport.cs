// Files tab: a VRChat avatar's .unitypackage made into a character on the phone itself (no Unity editor, no VRChat
// SDK): read in the background (UnityPackage, Avatar, AvatarModel), its textures shrunk to 2048 here (Unity's image
// code), written as mods/avatars/<name>.glb with its toggles beside it (<name>.vrc.json), then made the hero.
// The toggles and the parts show under the character in the Files tab (VrcLook).
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;
using Vrc;

public partial class Game
{
#if UNITY_IOS && !UNITY_EDITOR
    [DllImport("__Internal")] static extern void DP_PickPackage(string obj, string method);
#else
    static void DP_PickPackage(string obj, string method) { }
#endif

    const int kMaxTexture = 2048;
    string vrcBusy = "";          // what the import is doing ("": nothing)
    float vrcProgress = -1f;
    List<Vrc.Avatar> vrcChoices;  // the package's avatars, while you pick one
    int vrcPick = -1;             // -1 not yet, -2 cancelled
    string[] packages = new string[0];

    void PickPackage() => DP_PickPackage(gameObject.name, nameof(OnPickedPackage));

    // DeliriumPicker.mm: a copy of the picked file in the app's tmp folder ("": cancelled)
    void OnPickedPackage(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".blend" || ext == ".zip") { StartCoroutine(ImportBlend(path, true)); return; }
        if (ext != ".unitypackage")
        {
            filesNote = Path.GetFileName(path) + ": a .unitypackage, a .blend or a .zip with a .blend in it, please";
            TryDelete(path);
            return;
        }
        StartCoroutine(ImportPackage(path, true));
    }

    List<string> blendChoices;   // a .zip's .blend files, while you pick one
    string blendRoot = "";

    // A Blender file (or a .zip with one and its textures) made into a character: mods/models/<name>.glb + its wheel.
    IEnumerator ImportBlend(string file, bool deleteAfter)
    {
        if (vrcBusy != "" || vrcChoices != null || blendChoices != null) yield break;
        vrcBusy = "Opening " + Path.GetFileName(file) + "...";
        vrcProgress = -1f;
        filesNote = "";
        string work = Path.Combine(Application.temporaryCachePath, "blendimport");
        string blend = file, texDir = Path.GetDirectoryName(file);
        Exception err = null;
        List<string> found = null;
        if (file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var th0 = new Thread(() =>
            {
                try
                {
                    if (Directory.Exists(work)) Directory.Delete(work, true);
                    System.IO.Compression.ZipFile.ExtractToDirectory(file, work);
                    found = new List<string>(Directory.GetFiles(work, "*.blend", SearchOption.AllDirectories));
                    found.Sort((a, b) => new FileInfo(b).Length.CompareTo(new FileInfo(a).Length));
                    if (found.Count == 0) throw new Exception("no .blend in it");
                }
                catch (Exception e) { err = e; }
            }) { IsBackground = true };
            th0.Start();
            while (th0.IsAlive) yield return null;
            if (err == null)
            {
                blend = found[0];
                if (found.Count > 1)
                {
                    blendRoot = work;
                    blendChoices = found;
                    vrcPick = -1;
                    vrcBusy = "";
                    while (vrcPick == -1) yield return null;
                    blendChoices = null;
                    if (vrcPick < 0) err = new Exception("cancelled");
                    else blend = found[vrcPick];
                }
                texDir = work;
            }
        }
        BlendModel model = null;
        if (err == null)
        {
            vrcBusy = "Reading " + Path.GetFileName(blend) + "...";
            var th = new Thread(() =>
            {
                try { model = BlendModel.Build(blend, texDir, st => vrcBusy = Path.GetFileName(blend) + ": " + st + "..."); }
                catch (Exception e) { err = e; }
            }) { IsBackground = true };
            th.Start();
            while (th.IsAlive) yield return null;
        }
        if (err == null)
            for (int i = 0; i < model.Textures.Count; i++)
            {
                vrcBusy = model.Name + ": textures " + (i + 1) + "/" + model.Textures.Count;
                try { Encode(model.Textures[i]); }
                catch (Exception e) { model.Notes.Add(model.Textures[i].Guid + ": " + e.Message); }
                yield return null;
            }
        string glb = "";
        if (err == null)
        {
            vrcBusy = model.Name + ": writing...";
            string dir = Path.Combine(ModsDir(), "models");
            string name = Safe(model.Name);
            glb = Path.Combine(dir, name + ".glb");
            var th = new Thread(() =>
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    File.WriteAllBytes(glb, model.Glb());
                    File.WriteAllText(Path.Combine(dir, name + ".vrc.json"), model.Sidecar());
                }
                catch (Exception e) { err = e; }
            }) { IsBackground = true };
            th.Start();
            while (th.IsAlive) yield return null;
        }
        try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (Exception) { }
        if (deleteAfter) TryDelete(file);
        vrcBusy = "";
        if (err != null)
        {
            filesNote = "The import failed: " + err.Message;
            Debug.LogWarning("[Sandbox] .blend import: " + err);
            yield break;
        }
        RefreshFiles();
        Debug.Log("[Sandbox] .blend import: " + glb + "\n  " + string.Join("\n  ", model.Notes.ToArray()));
        Use(glb, 0);
        filesNote = model.Name + " imported (" + model.Triangles + " triangles, " + model.Meshes + " meshes, " + model.Shapes + " shape keys): mods/models/" +
                    Path.GetFileName(glb) + ". " + (model.Notes.Count > 0 ? string.Join("; ", model.Notes.ToArray()) + ". " : "") + filesNote;
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
    }

    IEnumerator ImportPackage(string package, bool deleteAfter)
    {
        if (vrcBusy != "" || vrcChoices != null) yield break;
        vrcBusy = "Opening " + Path.GetFileName(package) + "...";
        vrcProgress = 0f;
        filesNote = "";
        string work = Path.Combine(Application.temporaryCachePath, "vrcimport");
        Vrc.Avatar av = null;
        AvatarModel model = null;
        Exception err = null;
        List<Vrc.Avatar> found = null;
        var th = new Thread(() =>
        {
            try
            {
                var assets = UnityPackage.Extract(package, work, f => vrcProgress = f);
                vrcProgress = -1f;
                vrcBusy = "Finding the avatar...";
                found = Vrc.Avatar.FindAll(assets);
                if (found.Count == 0) throw new Exception("no VRChat avatar in it (a prefab or scene with a VRC Avatar Descriptor)");
            }
            catch (Exception e) { err = e; }
        }) { IsBackground = true };
        th.Start();
        while (th.IsAlive) yield return null;
        // more than one (PC and Quest, several versions): yours to pick
        if (err == null)
        {
            av = found[0];
            if (found.Count > 1)
            {
                vrcChoices = found;
                vrcPick = -1;
                vrcBusy = "";
                while (vrcPick == -1) yield return null;
                vrcChoices = null;
                if (vrcPick < 0) err = new Exception("cancelled");
                else av = found[vrcPick];
            }
        }
        if (err == null)
        {
            vrcBusy = "Reading " + av.Name + "...";
            th = new Thread(() =>
            {
                try
                {
                    av.Make();
                    if (av.Root == null) throw new Exception("the avatar's object isn't found in " + av.File0);
                    model = AvatarModel.Build(av, s => vrcBusy = av.Name + ": " + s + "...");
                }
                catch (Exception e) { err = e; }
            }) { IsBackground = true };
            th.Start();
            while (th.IsAlive) yield return null;
        }
        if (err == null)
        {
            // the pictures, one a frame
            for (int i = 0; i < model.Textures.Count; i++)
            {
                vrcBusy = av.Name + ": textures " + (i + 1) + "/" + model.Textures.Count;
                vrcProgress = (float)i / model.Textures.Count;
                try { Encode(model.Textures[i]); }
                catch (Exception e) { model.Notes.Add(Path.GetFileName(model.Textures[i].File) + ": " + e.Message); }
                yield return null;
            }
            // the menu's icons
            foreach (var icon in model.Icons)
            {
                try { Encode(icon); }
                catch (Exception) { icon.Out = null; }
            }
            vrcProgress = -1f;
            vrcBusy = av.Name + ": writing...";
        }
        string glb = "";
        if (err == null)
        {
            string dir = Path.Combine(ModsDir(), "avatars");
            string name = Safe(av.Name);
            glb = Path.Combine(dir, name + ".glb");
            th = new Thread(() =>
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    File.WriteAllBytes(glb, model.Glb());
                    File.WriteAllText(Path.Combine(dir, name + ".vrc.json"), model.Sidecar());
                }
                catch (Exception e) { err = e; }
            }) { IsBackground = true };
            th.Start();
            while (th.IsAlive) yield return null;
        }
        try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (Exception) { }
        if (deleteAfter) TryDelete(package);
        vrcBusy = "";
        vrcProgress = -1f;
        if (err != null)
        {
            filesNote = "The import failed: " + err.Message;
            Debug.LogWarning("[Sandbox] VRChat import: " + err);
            yield break;
        }
        RefreshFiles();
        Debug.Log("[Sandbox] VRChat import: " + glb + "\n  " + string.Join("\n  ", model.Notes.ToArray()));
        // the hero in it now (its note says what it did)
        Use(glb, 0);
        filesNote = av.Name + " imported (" + model.Triangles + " triangles, " + av.Toggles.Count + " toggles" +
                    (model.Springs > 0 ? ", " + model.Springs + " PhysBone chains" : "") + "): mods/avatars/" + Path.GetFileName(glb) + ". " + filesNote;
    }

    static string Safe(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().Trim('.');
        return name == "" ? "avatar" : name;
    }

    // A texture for the .glb: as it is when it's a PNG / JPEG no bigger than kMaxTexture, else decoded, shrunk and
    // encoded again (PNG where its alpha cuts the material out, else JPEG).
    static void Encode(TexJob job)
    {
        byte[] bytes = job.Bytes ?? File.ReadAllBytes(job.File);
        bool png = bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 'P', jpg = bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8;
        int w = 0, h = 0;
        if (png && bytes.Length > 24) { w = Be(bytes, 16); h = Be(bytes, 20); }
        if ((png || jpg) && w > 0 && w <= job.Max && h <= job.Max)
        {
            job.Out = bytes;
            job.Mime = png ? "image/png" : "image/jpeg";
            return;
        }
        Texture2D tex = null, small = null;
        RenderTexture rt = null;
        try
        {
            if (png || jpg)
            {
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!tex.LoadImage(bytes, false)) throw new Exception("didn't decode");
            }
            else tex = Tga(bytes);
            int tw = tex.width, th = tex.height;
            while (tw > job.Max || th > job.Max) { tw = Mathf.Max(1, tw / 2); th = Mathf.Max(1, th / 2); }
            if (tw != tex.width || th != tex.height)
            {
                rt = RenderTexture.GetTemporary(tw, th, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                Graphics.Blit(tex, rt);
                var was = RenderTexture.active;
                RenderTexture.active = rt;
                small = new Texture2D(tw, th, TextureFormat.RGBA32, false);
                small.ReadPixels(new Rect(0, 0, tw, th), 0, 0);
                small.Apply(false);
                RenderTexture.active = was;
            }
            var src = small != null ? small : tex;
            if (job.Alpha) { job.Out = src.EncodeToPNG(); job.Mime = "image/png"; }
            else { job.Out = src.EncodeToJPG(92); job.Mime = "image/jpeg"; }
        }
        finally
        {
            if (rt != null) RenderTexture.ReleaseTemporary(rt);
            if (tex != null) Destroy(tex);
            if (small != null) Destroy(small);
        }
    }

    static int Be(byte[] b, int at) => (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3];

    // A .tga: true colour (24 / 32 bits) or grey, plain or run-length encoded.
    static Texture2D Tga(byte[] b)
    {
        int idLen = b[0], cmapType = b[1], type = b[2];
        int cmapLen = b[5] | (b[6] << 8), cmapBits = b[7];
        int w = b[12] | (b[13] << 8), h = b[14] | (b[15] << 8), bits = b[16], desc = b[17];
        bool rle = type == 10 || type == 11, grey = type == 3 || type == 11;
        if (!(type == 2 || type == 10 || grey) || cmapType != 0) throw new Exception("a TGA of a kind not read (type " + type + ")");
        int bpp = bits / 8;
        if (bpp < 1 || bpp > 4) throw new Exception("a " + bits + "-bit TGA isn't read");
        int at = 18 + idLen + (cmapType != 0 ? cmapLen * ((cmapBits + 7) / 8) : 0);
        var px = new Color32[w * h];
        Func<int, Color32> read = p =>
        {
            if (grey) return new Color32(b[p], b[p], b[p], bpp > 1 ? b[p + 1] : (byte)255);
            if (bpp == 2)
            {
                int v = b[p] | (b[p + 1] << 8);
                return new Color32((byte)(((v >> 10) & 31) * 255 / 31), (byte)(((v >> 5) & 31) * 255 / 31), (byte)((v & 31) * 255 / 31), 255);
            }
            return new Color32(b[p + 2], b[p + 1], b[p], bpp == 4 ? b[p + 3] : (byte)255);
        };
        int n = 0;
        while (n < px.Length && at < b.Length)
        {
            if (!rle) { px[n++] = read(at); at += bpp; continue; }
            int head = b[at++], count = (head & 127) + 1;
            if ((head & 128) != 0)
            {
                var c = read(at);
                at += bpp;
                for (int k = 0; k < count && n < px.Length; k++) px[n++] = c;
            }
            else
                for (int k = 0; k < count && n < px.Length; k++) { px[n++] = read(at); at += bpp; }
        }
        // Unity's rows go up from the bottom; a TGA's do too unless its descriptor says top-first
        if ((desc & 0x20) != 0)
            for (int y = 0; y < h / 2; y++)
                for (int x = 0; x < w; x++)
                {
                    var t = px[y * w + x];
                    px[y * w + x] = px[(h - 1 - y) * w + x];
                    px[(h - 1 - y) * w + x] = t;
                }
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        tex.SetPixels32(px);
        tex.Apply(false);
        return tex;
    }

    // ------------------------------------------------------------------ the Files tab's part
    void VrcImportUi()
    {
        Head("Import");
        Info("A VRChat avatar's .unitypackage (your own or one you bought) made into a character here: its meshes, " +
             "textures, PhysBones and menu toggles (objects and blend shapes; no gestures). Or a Blender file (.blend, " +
             "or a .zip with one and its textures): its meshes, armature, shape keys (on the Expr. wheel) and textures.");
        if (blendChoices != null)
        {
            Info("It has " + blendChoices.Count + " .blend files. Which one?");
            for (int i = 0; i < blendChoices.Count; i++)
                if (GUILayout.Button(blendChoices[i].Substring(blendRoot.Length).TrimStart('/', '\\'), btn, GUILayout.Height(34f))) vrcPick = i;
            if (GUILayout.Button("Cancel", btn, GUILayout.Height(30f))) vrcPick = -2;
            return;
        }
        if (vrcChoices != null)
        {
            Info("This package has " + vrcChoices.Count + " avatars. Which one?");
            for (int i = 0; i < vrcChoices.Count; i++)
            {
                var c = vrcChoices[i];
                string what = c.Label + "  (" + c.Toggles.Count + " toggles" + (c.Score < -1000 ? ", Quest / fallback" : "") + ")";
                if (GUILayout.Button(what, i == 0 ? btnOn : btn, GUILayout.Height(34f))) vrcPick = i;
            }
            if (GUILayout.Button("Cancel", btn, GUILayout.Height(30f))) vrcPick = -2;
            return;
        }
        if (vrcBusy != "")
        {
            GUILayout.Label(vrcBusy + (vrcProgress >= 0f ? " " + Mathf.RoundToInt(vrcProgress * 100f) + "%" : ""), lab);
            return;
        }
        if (CanPick && GUILayout.Button("Import model (.unitypackage, .blend, .zip)...", btn, GUILayout.Height(36f))) PickPackage();
        foreach (string p in packages)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Path.GetFileName(p), lab);
            if (GUILayout.Button("Import", btn, GUILayout.Width(80f), GUILayout.Height(28f))) StartCoroutine(ImportPackage(p, false));
            GUILayout.EndHorizontal();
        }
    }

    void VrcToggles(GameObject template, string who)
    {
        if (template == null) return;
        var look = template.GetComponentInChildren<VrcLook>(true);
        var d = look != null ? look.D : null;
        if (d == null) return;
        Head(who + ": " + d.Avatar);
        string menu = null;
        foreach (var t in d.Toggles)
        {
            if (t.Radial) continue;
            if (t.Menu != menu)
            {
                menu = t.Menu;
                if (menu != "") GUILayout.Label(menu, dim);
            }
            bool on = VrcLook.IsOn(d, t);
            bool now = Tick(on, t.Name);
            if (now != on) VrcLook.Set(look.Key, t, now);
        }
        if (d.Toggles.Count == 0) Info("No menu toggles that change anything.");
        if (GUILayout.Button("Reset toggles", btn, GUILayout.Height(30f))) VrcLook.Reset(look.Key);
    }
}
