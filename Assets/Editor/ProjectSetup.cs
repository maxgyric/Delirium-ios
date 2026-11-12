// Sets the project up the first time Unity opens it (no hand-made settings files): a scene to build, the material
// the game clones, the app's name and id, the old Input Manager (the game reads the mouse and touches through it).
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;

[InitializeOnLoad]
public static class ProjectSetup
{
    const string kScene = "Assets/Scenes/Main.unity", kMat = "Assets/Resources/DeliriumMat.mat", kLit = "Assets/Resources/DeliriumLit.mat";

    static ProjectSetup()
    {
        EditorApplication.delayCall += Run;
    }

    [MenuItem("Delirium/Set the project up again")]
    static void Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        if (!File.Exists(kMat))
        {
            Directory.CreateDirectory("Assets/Resources");
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), kMat);
            AssetDatabase.SaveAssets();
        }
        // your models' and maps' material (its shader goes into the build with it)
        if (!File.Exists(kLit) && Shader.Find("Delirium/Lit") != null)
        {
            Directory.CreateDirectory("Assets/Resources");
            AssetDatabase.CreateAsset(new Material(Shader.Find("Delirium/Lit")), kLit);
            AssetDatabase.SaveAssets();
        }
        // colours as the files mean them (glTF's are linear): brighter, as Sketchfab shows them
        if (PlayerSettings.colorSpace != ColorSpace.Linear) PlayerSettings.colorSpace = ColorSpace.Linear;
        if (!File.Exists(kScene))
        {
            Directory.CreateDirectory("Assets/Scenes");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, kScene);
            AssetDatabase.Refresh();
        }
        else if (string.IsNullOrEmpty(EditorSceneManager.GetActiveScene().path))
            EditorSceneManager.OpenScene(kScene);
        if (EditorBuildSettings.scenes.Length == 0)
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(kScene, true) };

        if (PlayerSettings.productName != "Delirium Sandbox")
        {
            PlayerSettings.companyName = "Delirium";
            PlayerSettings.productName = "Delirium Sandbox";
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.iOS, "com.delirium.sandbox");
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Standalone, "com.delirium.sandbox");
        }

        // Active Input Handling: 0 the old Input Manager, 1 the Input System package, 2 both
        var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
        if (assets != null && assets.Length > 0)
        {
            var so = new SerializedObject(assets[0]);
            var p = so.FindProperty("activeInputHandler");
            if (p != null && p.intValue == 1)
            {
                p.intValue = 0;
                so.ApplyModifiedProperties();
                AssetDatabase.SaveAssets();
                Debug.LogWarning("[Delirium Sandbox] Input handling set to the old Input Manager: close Unity and open the project again.");
            }
        }
    }
}
