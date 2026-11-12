// The iPhone build (run by GitHub Actions: game-ci with -executeMethod BuildIOS.Build -customBuildPath <dir>).
// Makes the Xcode project; the workflow turns it into an unsigned .ipa (signed on your PC by Sideloadly).
// The game's own native code (Delirium + delirium_anim) is Assets/Plugins/iOS/libdelirium_ios.a, built by the
// workflow from native/ just before this runs.
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Callbacks;
using UnityEngine;
#if UNITY_IOS
using UnityEditor.iOS.Xcode;
#endif

public static class BuildIOS
{
    public static void Build()
    {
        string[] args = Environment.GetCommandLineArgs();
        int at = Array.IndexOf(args, "-customBuildPath");
        string path = at >= 0 && at + 1 < args.Length ? args[at + 1] : "Build/iOS";

        if (!File.Exists("Assets/Plugins/iOS/libdelirium_ios.a"))
            throw new Exception("Assets/Plugins/iOS/libdelirium_ios.a is missing (the workflow's native step builds it)");

        var ios = NamedBuildTarget.iOS;
        PlayerSettings.SetScriptingBackend(ios, ScriptingImplementation.IL2CPP);
        PlayerSettings.SetManagedStrippingLevel(ios, ManagedStrippingLevel.Minimal);
        PlayerSettings.SetApplicationIdentifier(ios, "com.delirium.sandbox");
        PlayerSettings.iOS.sdkVersion = iOSSdkVersion.DeviceSDK;
        PlayerSettings.iOS.targetOSVersionString = "15.0";
        PlayerSettings.iOS.appleEnableAutomaticSigning = false;
        PlayerSettings.statusBarHidden = true;
        // played sideways (the sticks and the buttons are laid out for it)
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
        PlayerSettings.allowedAutorotateToPortrait = false;
        PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
        PlayerSettings.allowedAutorotateToLandscapeLeft = true;
        PlayerSettings.allowedAutorotateToLandscapeRight = true;

        var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        if (scenes.Length == 0) scenes = new[] { "Assets/Scenes/Main.unity" };
        Debug.Log("BuildIOS: " + string.Join(", ", scenes) + " -> " + path);

        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = path,
            target = BuildTarget.iOS,
            options = BuildOptions.None,
        });
        Debug.Log("BuildIOS: " + report.summary.result + ", " + report.summary.totalErrors + " errors");
        if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
            EditorApplication.Exit(1);
    }

#if UNITY_IOS
    // Info.plist: the app's folder shows in the Files app (put default.xex, the exported files, .glb files there),
    // 120 Hz on ProMotion screens, no export-compliance question.
    [PostProcessBuild(100)]
    public static void Plist(BuildTarget target, string path)
    {
        if (target != BuildTarget.iOS) return;
        string file = Path.Combine(path, "Info.plist");
        var plist = new PlistDocument();
        plist.ReadFromFile(file);
        var root = plist.root;
        root.SetBoolean("UIFileSharingEnabled", true);
        root.SetBoolean("LSSupportsOpeningDocumentsInPlace", true);
        root.SetBoolean("CADisableMinimumFrameDurationOnPhone", true);
        root.SetBoolean("ITSAppUsesNonExemptEncryption", false);
        plist.WriteToFile(file);
    }
#endif
}
