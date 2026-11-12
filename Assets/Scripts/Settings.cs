// Settings: the picture's resolution (the 3D view drawn smaller and stretched to the screen; the buttons stay sharp),
// how far shadows reach, a frame-rate cap and an FPS counter. Kept between runs.
using UnityEngine;

public partial class Game
{
    const string kPrefScale = "delirium.renderScale", kPrefShadows = "delirium.shadowDistance", kPrefFps = "delirium.showFps", kPrefCap = "delirium.fpsCap";
    float renderScale = 1f, shadowDistance = 60f;
    bool showFps;
    int fpsCap = 60;
    float fpsSmooth;
    RenderTexture view;          // the 3D view at renderScale
    Camera present;              // puts it on the screen

    // The view as a camera ray: a point on the screen (pixels) in the view's own pixels.
    Ray ViewRay(Vector2 screen)
    {
        float k = cam.targetTexture != null ? (float)cam.targetTexture.width / Mathf.Max(Screen.width, 1) : 1f;
        return cam.ScreenPointToRay(new Vector3(screen.x * k, screen.y * k, 0f));
    }

    void SettingsLoad()
    {
        renderScale = Mathf.Clamp(PlayerPrefs.GetFloat(kPrefScale, 1f), 0.4f, 1f);
        shadowDistance = Mathf.Clamp(PlayerPrefs.GetFloat(kPrefShadows, 60f), 0f, 150f);
        showFps = PlayerPrefs.GetInt(kPrefFps, 0) == 1;
        fpsCap = PlayerPrefs.GetInt(kPrefCap, 60);
        SettingsApply();
    }

    void SettingsSave()
    {
        PlayerPrefs.SetFloat(kPrefScale, renderScale);
        PlayerPrefs.SetFloat(kPrefShadows, shadowDistance);
        PlayerPrefs.SetInt(kPrefFps, showFps ? 1 : 0);
        PlayerPrefs.SetInt(kPrefCap, fpsCap);
        PlayerPrefs.Save();
    }

    void SettingsApply()
    {
        Application.targetFrameRate = fpsCap;
        QualitySettings.vSyncCount = 0; // the cap, not the screen's refresh, sets the pace
        QualitySettings.shadowDistance = Mathf.Max(shadowDistance, 1f);
        var sun = FindFirstObjectByType<Light>();
        if (sun != null && sun.type == LightType.Directional) sun.shadows = shadowDistance > 0.5f ? LightShadows.Soft : LightShadows.None;
        View();
    }

    // The 3D view into a smaller texture when the scale is below 100%, a second camera stretching it over the screen.
    void View()
    {
        if (cam == null) return;
        if (renderScale > 0.99f)
        {
            cam.targetTexture = null;
            if (present != null) present.enabled = false;
            if (view != null) { view.Release(); Destroy(view); view = null; }
            return;
        }
        int w = Mathf.Max(64, Mathf.RoundToInt(Screen.width * renderScale)), h = Mathf.Max(64, Mathf.RoundToInt(Screen.height * renderScale));
        if (view == null || view.width != w || view.height != h)
        {
            cam.targetTexture = null;
            if (view != null) { view.Release(); Destroy(view); }
            view = new RenderTexture(w, h, 24, RenderTextureFormat.Default) { name = "View", filterMode = FilterMode.Bilinear };
            view.antiAliasing = Mathf.Max(1, QualitySettings.antiAliasing); // the same smoothing the screen had
            view.Create();
        }
        cam.targetTexture = view;
        if (present == null)
        {
            var go = new GameObject("Present");
            DontDestroyOnLoad(go);
            present = go.AddComponent<Camera>();
            present.cullingMask = 0;               // draws nothing of its own
            present.clearFlags = CameraClearFlags.SolidColor;
            present.backgroundColor = Color.black;
            present.depth = cam.depth + 10;
            go.AddComponent<Present>().game = this;
        }
        present.enabled = true;
    }

    internal RenderTexture ViewTexture => view;

    void SettingsUpdate()
    {
        float dt = Time.unscaledDeltaTime;
        if (dt > 0f) fpsSmooth = fpsSmooth <= 0f ? 1f / dt : Mathf.Lerp(fpsSmooth, 1f / dt, 0.05f);
        // a screen turned or resized: the view's texture follows
        if (renderScale <= 0.99f && view != null && (Mathf.Abs(view.width - Screen.width * renderScale) > 2f || Mathf.Abs(view.height - Screen.height * renderScale) > 2f)) View();
    }

    void FpsGui(float w)
    {
        if (!showFps) return;
        GUI.Label(new Rect(w - 96f, 60f, 88f, 24f), Mathf.RoundToInt(fpsSmooth) + " FPS", new GUIStyle(chip) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold });
    }

    void SettingsTab()
    {
        Head("Picture");
        float s = Slide("Resolution", Mathf.Round(renderScale * 100f), 40f, 100f, "%") / 100f;
        if (Mathf.Abs(s - renderScale) > 0.004f) { renderScale = s; View(); SettingsSave(); }
        Info("Below 100% the 3D view is drawn smaller and stretched to the screen (the buttons stay sharp). On a phone 70% hardly shows and is much lighter.");
        float d = Slide("Shadows reach", Mathf.Round(shadowDistance), 0f, 150f, shadowDistance < 0.5f ? " (off)" : " m");
        if (Mathf.Abs(d - shadowDistance) > 0.5f) { shadowDistance = d; SettingsApply(); SettingsSave(); }
        Info("How far away shadows are still drawn. Less is lighter; 0 turns them off.");

        Head("Frame rate");
        int[] caps = { 30, 60, 120 };
        int pick = Grid(new[] { "30", "60", "120" }, 3, System.Array.IndexOf(caps, fpsCap));
        if (pick >= 0) { fpsCap = caps[pick]; SettingsApply(); SettingsSave(); }
        Info("The most frames a second it draws. 120 only on a 120 Hz screen (an iPhone Pro); 30 keeps a phone cooler.");
        bool f = Tick(showFps, "Show FPS");
        if (f != showFps) { showFps = f; SettingsSave(); }

        Head("Sound");
        volume = Slide("Volume", volume, 0f, 1f);
    }
}

// Stretches the game's 3D view (rendered smaller) over the screen, under the buttons.
public class Present : MonoBehaviour
{
    internal Game game;

    void OnRenderImage(RenderTexture src, RenderTexture dst)
    {
        var view = game != null ? game.ViewTexture : null;
        if (view != null) Graphics.Blit(view, dst);
        else Graphics.Blit(src, dst);
    }
}
