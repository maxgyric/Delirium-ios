// Garry's Mod addons (.gma) listed: their maps and player models, an entry each ("addon.gma#maps/x.bsp",
// "addon.gma#models/y.mdl"), read by the native library on a thread of its own (a Workshop download is LZMA-packed:
// opening it unpacks the whole of it, seconds each, and a folder of them stood the game still for a minute) and
// remembered on disk by file, size and date (the next launch lists them at once). The maps' own pictures are
// unpacked on the same pass. An addon not read yet isn't listed; it shows up when it's done.
//
// Also your GMod addons' folder: one you pick anywhere the Files app reaches (ModShelf's downloads, in LiveContainer),
// read where it is, every subfolder; its files are never moved or deleted.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;

public partial class Game
{
    [System.Runtime.InteropServices.DllImport(BspDll)] static extern int dgma_list(byte[] gma, byte[] thumbDir, byte[] text, int cap);

    static readonly Dictionary<string, string> modelNames = new Dictionary<string, string>(); // "addon.gma#models/x.mdl" -> "Buddy"
    static Dictionary<string, string[]> addonLists;                 // "path|size|date" -> its lines ("M maps/x.bsp", "P models/y.mdl|Name")
    static readonly Queue<(string key, string gma, string thumbs)> addonQueue = new Queue<(string, string, string)>();
    static readonly Queue<(string key, string[] lines)> addonDone = new Queue<(string, string[])>();
    static readonly HashSet<string> addonAsked = new HashSet<string>();
    static readonly object addonLock = new object();
    static Thread addonWorker;
    static int addonsLeft; // being read (and waiting)

    static string AddonCacheFile() => Path.Combine(Application.temporaryCachePath, "addons.txt");

    static string AddonKey(string f)
    {
        var fi = new FileInfo(f);
        return f + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
    }

    static void LoadAddonLists()
    {
        if (addonLists != null) return;
        addonLists = new Dictionary<string, string[]>();
        try
        {
            if (!File.Exists(AddonCacheFile())) return;
            foreach (string line in File.ReadAllLines(AddonCacheFile()))
            {
                int tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                string rest = line.Substring(tab + 1);
                addonLists[line.Substring(0, tab)] = rest == "" ? new string[0] : rest.Split('\u001f');
            }
        }
        catch (Exception) { }
    }

    static void SaveAddonLists()
    {
        try
        {
            var lines = new List<string>();
            foreach (var kv in addonLists) lines.Add(kv.Key + "\t" + string.Join("\u001f", kv.Value));
            File.WriteAllLines(AddonCacheFile(), lines.ToArray());
        }
        catch (Exception) { }
    }

    // An addon's entries: at once when it's been read (this launch or an earlier one), else none yet (it's read in
    // the background; the list is made again when it's done)
    static IEnumerable<string> GmaEntries(string f)
    {
        LoadAddonLists();
        string key;
        try { key = AddonKey(f); } catch (Exception) { yield break; }
        if (!addonLists.TryGetValue(key, out var lines))
        {
            lock (addonLock)
                if (addonAsked.Add(key))
                {
                    addonQueue.Enqueue((key, f, ThumbDir(f)));
                    addonsLeft++;
                    if (addonWorker == null || !addonWorker.IsAlive)
                    {
                        addonWorker = new Thread(ReadAddons) { IsBackground = true, Name = "GMod addons" };
                        addonWorker.Start();
                    }
                }
            yield break;
        }
        bool any = false;
        foreach (string line in lines)
        {
            if (line.Length < 3) continue;
            string body = line.Substring(2);
            if (line[0] == 'M') { any = true; yield return f + "#" + body; }
            else if (line[0] == 'P')
            {
                int bar = body.IndexOf('|');
                string path = f + "#" + (bar < 0 ? body : body.Substring(0, bar));
                modelNames[path] = bar < 0 ? Path.GetFileNameWithoutExtension(body) : body.Substring(bar + 1);
                any = true;
                yield return path;
            }
        }
        if (!any) yield return f; // (nothing it knows in it: the file itself, which says so when it's used)
    }

    // the thread: one addon after another (not all at once: each can be hundreds of megabytes unpacked)
    static void ReadAddons()
    {
        while (true)
        {
            (string key, string gma, string thumbs) job;
            lock (addonLock)
            {
                if (addonQueue.Count == 0) return;
                job = addonQueue.Dequeue();
            }
            string[] lines = new string[0];
            try
            {
                var buf = new byte[1 << 17];
                int n = dgma_list(System.Text.Encoding.UTF8.GetBytes(job.gma + "\0"), System.Text.Encoding.UTF8.GetBytes(job.thumbs + "\0"), buf, buf.Length);
                if (n > 0)
                {
                    int end = Array.IndexOf(buf, (byte)0);
                    lines = System.Text.Encoding.UTF8.GetString(buf, 0, end < 0 ? buf.Length : end).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                }
            }
            catch (Exception) { }
            lock (addonLock)
            {
                addonDone.Enqueue((job.key, lines));
                addonsLeft--;
            }
        }
    }

    // (each frame) the addons read since: listed
    void AddonsUpdate()
    {
        bool any = false;
        lock (addonLock)
            while (addonDone.Count > 0)
            {
                var d = addonDone.Dequeue();
                LoadAddonLists();
                addonLists[d.key] = d.lines;
                any = true;
            }
        if (any) { SaveAddonLists(); pendingRefresh = true; }
        // listed again when they're all read, or every few seconds while a lot of them are
        if (pendingRefresh && (AddonsReading() <= 0 || Time.unscaledTime - lastRefresh > 4f))
        {
            pendingRefresh = false;
            lastRefresh = Time.unscaledTime;
            RefreshFiles();
        }
    }
    bool pendingRefresh;
    float lastRefresh;

    static int AddonsReading() { lock (addonLock) return addonsLeft; }

    // ------------------------------------------------------------------ your GMod addons' folder
    string gmodDir = "";
    const string kPrefGmod = "delirium.gmodDir";

#if UNITY_IOS && !UNITY_EDITOR
    [System.Runtime.InteropServices.DllImport("__Internal")] static extern void DP_PickGmodFolder(string obj, string method);
    [System.Runtime.InteropServices.DllImport("__Internal")] static extern string DP_RestoreGmodFolder();
    [System.Runtime.InteropServices.DllImport("__Internal")] static extern void DP_ForgetGmodFolder();
#else
    static void DP_PickGmodFolder(string obj, string method) { }
    static string DP_RestoreGmodFolder() => "";
    static void DP_ForgetGmodFolder() { }
#endif

    void GmodFolderAtStart()
    {
        try { gmodDir = Application.platform == RuntimePlatform.IPhonePlayer ? DP_RestoreGmodFolder() ?? "" : PlayerPrefs.GetString(kPrefGmod, ""); }
        catch (Exception e) { Debug.LogWarning("[Sandbox] GMod folder bookmark: " + e.Message); gmodDir = ""; }
        if (gmodDir != "") Debug.Log("[Sandbox] GMod folder: " + gmodDir);
    }

    bool InGmodDir(string f)
    {
        if (gmodDir == "") return false;
        string root = gmodDir.TrimEnd('/', '\\') + "/";
        return FileOf(f).Replace('\\', '/').StartsWith(root.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    // DeliriumPicker.mm: the folder picked ("" when cancelled)
    void OnPickedGmodFolder(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        gmodDir = path;
        PlayerPrefs.SetString(kPrefGmod, path);
        PlayerPrefs.Save();
        Debug.Log("[Sandbox] GMod folder: " + path);
        filesNote = "GMod folder: " + path + ". Its addons are being read (each once).";
        RefreshFiles();
    }

    void GmodFolderUi()
    {
        Head("Your GMod addons' folder");
        Info(gmodDir == ""
            ? "Pick the folder your GMod mods are downloaded to (ModShelf's, in LiveContainer...): its addons (.gma) and maps (.bsp), in every subfolder, are listed where they are. Nothing in it is moved or deleted."
            : gmodDir);
        GUILayout.BeginHorizontal();
        if (Application.isMobilePlatform)
        {
            if (GUILayout.Button(gmodDir == "" ? "Pick GMod folder..." : "Another folder...", btn, GUILayout.Height(34f))) DP_PickGmodFolder(gameObject.name, nameof(OnPickedGmodFolder));
        }
        else
        {
            string dir = GUILayout.TextField(gmodDir, txt, GUILayout.Height(30f), GUILayout.Width(kPanel - 140f));
            if (dir != gmodDir) { gmodDir = dir.Trim().Trim('"'); PlayerPrefs.SetString(kPrefGmod, gmodDir); }
        }
        if (gmodDir != "" && GUILayout.Button("Forget", btn, GUILayout.Width(80f), GUILayout.Height(34f)))
        {
            gmodDir = "";
            PlayerPrefs.DeleteKey(kPrefGmod);
            DP_ForgetGmodFolder();
            RefreshFiles();
        }
        GUILayout.EndHorizontal();
        int left = AddonsReading();
        if (left > 0) Info("Reading addons: " + left + " left (each once; they show up as they're done).");
    }
}
