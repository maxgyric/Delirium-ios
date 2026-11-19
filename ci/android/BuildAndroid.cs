// The Quest build (run by GitHub Actions, .github/workflows/quest.yml: copied into Assets/Editor there, with the
// OpenXR package added to the manifest, so the iPhone build never sees either). Turns OpenXR on for Android with
// Meta Quest support and the Touch controllers, sets the player up for the headset (IL2CPP, ARM64, Android 10+,
// OpenGL ES 3 with multi-pass stereo: the game's own shaders need nothing for it) and builds an .apk to sideload.
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;

public static class BuildAndroid
{
    public static void Build()
    {
        string[] args = Environment.GetCommandLineArgs();
        int at = Array.IndexOf(args, "-customBuildPath");
        string dir = at >= 0 && at + 1 < args.Length ? args[at + 1] : "Build/Android";
        Directory.CreateDirectory(dir);

        if (!File.Exists("Assets/Plugins/Android/libs/arm64-v8a/libdelirium.so"))
            throw new Exception("Assets/Plugins/Android/libs/arm64-v8a/libdelirium.so is missing (the workflow's native step builds it)");

        EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
        var a = NamedBuildTarget.Android;
        PlayerSettings.SetScriptingBackend(a, ScriptingImplementation.IL2CPP);
        PlayerSettings.SetManagedStrippingLevel(a, ManagedStrippingLevel.Minimal);
        PlayerSettings.stripEngineCode = false;
        PlayerSettings.SetApplicationIdentifier(a, "com.delirium.sandbox");
        PlayerSettings.productName = "Delirium Sandbox VR";
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;
        PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { UnityEngine.Rendering.GraphicsDeviceType.OpenGLES3 });
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
        EditorUserBuildSettings.buildAppBundle = false;
        Xr();

        var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        if (scenes.Length == 0) scenes = new[] { "Assets/Scenes/Main.unity" };
        string apk = Path.Combine(dir, "DeliriumSandboxVR.apk");
        Debug.Log("BuildAndroid: " + string.Join(", ", scenes) + " -> " + apk);
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = apk,
            target = BuildTarget.Android,
            options = BuildOptions.None,
        });
        Debug.Log("BuildAndroid: " + report.summary.result + ", " + report.summary.totalErrors + " errors");
        if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
            EditorApplication.Exit(1);
    }

    // XR Plug-in Management for Android: OpenXR, started with the app; its Meta Quest feature and Touch controllers
    static void Xr()
    {
        Directory.CreateDirectory("Assets/XR");
        if (!EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey, out XRGeneralSettingsPerBuildTarget perTarget) || perTarget == null)
        {
            perTarget = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
            AssetDatabase.CreateAsset(perTarget, "Assets/XR/XRGeneralSettingsPerBuildTarget.asset");
            EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, perTarget, true);
        }
        var settings = perTarget.SettingsForBuildTarget(BuildTargetGroup.Android);
        if (settings == null)
        {
            settings = ScriptableObject.CreateInstance<XRGeneralSettings>();
            settings.name = "Android Settings";
            AssetDatabase.AddObjectToAsset(settings, perTarget);
            perTarget.SetSettingsForBuildTarget(BuildTargetGroup.Android, settings);
        }
        if (settings.AssignedSettings == null)
        {
            var manager = ScriptableObject.CreateInstance<XRManagerSettings>();
            manager.name = "Android Providers";
            AssetDatabase.AddObjectToAsset(manager, perTarget);
            settings.AssignedSettings = manager;
        }
        settings.InitManagerOnStart = true;
        bool loader = XRPackageMetadataStore.AssignLoader(settings.AssignedSettings, "UnityEngine.XR.OpenXR.OpenXRLoader", BuildTargetGroup.Android);
        EditorUtility.SetDirty(settings);
        EditorUtility.SetDirty(perTarget);
        AssetDatabase.SaveAssets();
        Debug.Log("BuildAndroid: OpenXR loader " + (loader ? "assigned" : "NOT assigned") + ", loaders: " + string.Join(", ", settings.AssignedSettings.activeLoaders.Select(l => l.name)));

        var ox = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
        if (ox == null) throw new Exception("no OpenXR settings for Android");
        ox.renderMode = OpenXRSettings.RenderMode.MultiPass;
        var on = new[] { "MetaQuestFeature", "OculusTouchControllerProfile", "MetaQuestTouchPlusControllerProfile", "MetaQuestTouchProControllerProfile" };
        foreach (var f in ox.GetFeatures<OpenXRFeature>())
        {
            if (on.Contains(f.GetType().Name)) f.enabled = true;
            EditorUtility.SetDirty(f);
        }
        EditorUtility.SetDirty(ox);
        AssetDatabase.SaveAssets();
        Debug.Log("BuildAndroid: OpenXR features on: " + string.Join(", ", ox.GetFeatures<OpenXRFeature>().Where(f => f.enabled).Select(f => f.GetType().Name)));
    }
}
