// The Files app's picker on the iPhone (Assets/Plugins/iOS/DeliriumPicker.mm): the folder with your game's files
// (kept across launches), and models / maps moved into Documents/mods. Also: paths saved by an earlier install
// pointed into its old app folder (iOS gives the app a new one on each reinstall): moved over to the current one.
using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

public partial class Game
{
#if UNITY_IOS && !UNITY_EDITOR
    [DllImport("__Internal")] static extern void DP_PickFolder(string obj, string method);
    [DllImport("__Internal")] static extern void DP_PickModels(string obj, string method);
    [DllImport("__Internal")] static extern string DP_RestoreFolder();
    static bool CanPick => true;
#else
    static void DP_PickFolder(string obj, string method) { }
    static void DP_PickModels(string obj, string method) { }
    static string DP_RestoreFolder() => "";
    static bool CanPick => false;
#endif

    // a path from an earlier install: the same place under this install's Documents, if it's there
    static string Rebase(string p)
    {
        if (!Application.isMobilePlatform || string.IsNullOrEmpty(p) || File.Exists(p) || Directory.Exists(p)) return p;
        const string docs = "/Documents";
        int i = p.LastIndexOf(docs + "/", StringComparison.Ordinal);
        string rest = i >= 0 ? p.Substring(i + docs.Length + 1) : p.EndsWith(docs, StringComparison.Ordinal) ? "" : null;
        if (rest == null) return p;
        string q = rest == "" ? Application.persistentDataPath : Path.Combine(Application.persistentDataPath, rest);
        return File.Exists(q) || Directory.Exists(q) ? q : p;
    }

    // At start: the saved paths, moved to this install's folder; else the folder you picked; else Documents
    void PathsAtStart()
    {
        if (!Application.isMobilePlatform) return;
        gameExe = Rebase(gameExe);
        dataDir = Rebase(dataDir);
        string picked = "";
        try { picked = DP_RestoreFolder() ?? ""; } catch (Exception e) { Debug.LogWarning("[Sandbox] folder bookmark: " + e.Message); }
        bool exeOk = File.Exists(gameExe) || Directory.Exists(gameExe), dataOk = File.Exists(Path.Combine(dataDir ?? "", "character.xrt"));
        if (picked != "" && Directory.Exists(picked))
        {
            if (!exeOk) gameExe = picked;
            if (!dataOk) dataDir = picked;
        }
        if (!File.Exists(gameExe) && !Directory.Exists(gameExe) && File.Exists(DefaultExe())) gameExe = DefaultExe();
        if (!File.Exists(Path.Combine(dataDir ?? "", "character.xrt")) && File.Exists(Path.Combine(DefaultData(), "character.xrt"))) dataDir = DefaultData();
        Debug.Log("[Sandbox] game: " + gameExe + " | files: " + dataDir + (picked != "" ? " | picked folder: " + picked : ""));
    }

    void ChooseFolder() => DP_PickFolder(gameObject.name, nameof(OnPickedFolder));
    void SelectModels() => DP_PickModels(gameObject.name, nameof(OnPickedModels));

    // DeliriumPicker.mm: the folder (or "" when cancelled). Your game and the three exported files are looked for in it.
    void OnPickedFolder(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        gameExe = path;
        dataDir = path;
        error = File.Exists(Path.Combine(path, "default.xex")) || File.Exists(Path.Combine(path, "GTAIV.exe")) ? "" : "No default.xex in " + path;
        if (error == "" && !File.Exists(Path.Combine(path, "character.xrt"))) error = "No character.xrt in " + path;
        PlayerPrefs.SetString(kPrefExe, gameExe);
        PlayerPrefs.SetString(kPrefData, dataDir);
        PlayerPrefs.Save();
        Debug.Log("[Sandbox] picked folder: " + path);
    }

    // DeliriumPicker.mm: "moved|notes" (-1: cancelled)
    void OnPickedModels(string msg)
    {
        string[] parts = (msg ?? "").Split(new[] { '|' }, 2);
        int moved = int.TryParse(parts[0], out int n) ? n : 0;
        string notes = parts.Length > 1 ? parts[1] : "";
        if (moved < 0) return;
        filesNote = (moved == 1 ? "1 file moved into mods." : moved + " files moved into mods.") + (notes != "" ? " " + notes : "");
        Debug.Log("[Sandbox] " + filesNote);
        RefreshFiles();
    }
}
