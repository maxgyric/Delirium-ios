// A GMod map's own look: its 2D skybox (a panorama the native library made of its six pictures), its fog, its sun and
// its ambient light, read from the ".env" beside its converted .glb (MapLook(path)); the sandbox's own sky, light and
// no fog again for any other map (MapLook(null)). Distances scale with the map.
using System;
using System.Globalization;
using System.IO;
using UnityEngine;

public partial class Game
{
    // the sandbox's own, kept the first time a map changes them
    bool lookSaved;
    CameraClearFlags lookClear;
    Color lookBack, lookSunColor, lookAmbSky, lookAmbEq, lookAmbGround;
    float lookSunIntensity, lookFar;
    Quaternion lookSunRot;
    UnityEngine.Rendering.AmbientMode lookAmbMode;
    Material skyMat;
    Texture2D skyTex;

    static float[] Nums(string s)
    {
        var parts = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var v = new float[parts.Length];
        for (int i = 0; i < parts.Length; i++) float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]);
        return v;
    }

    void MapLook(string env)
    {
        var sun = FindFirstObjectByType<Light>();
        if (!lookSaved)
        {
            lookSaved = true;
            lookClear = cam.clearFlags;
            lookBack = cam.backgroundColor;
            lookFar = cam.farClipPlane;
            lookAmbMode = RenderSettings.ambientMode;
            lookAmbSky = RenderSettings.ambientSkyColor;
            lookAmbEq = RenderSettings.ambientEquatorColor;
            lookAmbGround = RenderSettings.ambientGroundColor;
            if (sun != null) { lookSunColor = sun.color; lookSunIntensity = sun.intensity; lookSunRot = sun.transform.rotation; }
        }
        // the sandbox's own first (whatever this map has, it starts from there)
        cam.clearFlags = lookClear;
        cam.backgroundColor = lookBack;
        cam.farClipPlane = lookFar;
        RenderSettings.fog = false;
        RenderSettings.skybox = null;
        RenderSettings.ambientMode = lookAmbMode;
        RenderSettings.ambientSkyColor = lookAmbSky;
        RenderSettings.ambientEquatorColor = lookAmbEq;
        RenderSettings.ambientGroundColor = lookAmbGround;
        if (sun != null) { sun.color = lookSunColor; sun.intensity = lookSunIntensity; sun.transform.rotation = lookSunRot; }
        if (skyTex != null) { Destroy(skyTex); skyTex = null; }
        if (env == null || !File.Exists(env)) return;

        float k = mapRoot != null ? mapRoot.transform.localScale.x : 1f; // the map's size (x2 / ÷2)
        cam.farClipPlane = Mathf.Max(lookFar, 1500f * k);
        string[] lines;
        try { lines = File.ReadAllLines(env); }
        catch (Exception) { return; }
        foreach (var line in lines)
        {
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            string key = line.Substring(0, eq), val = line.Substring(eq + 1);
            if (key == "sky")
            {
                try
                {
                    var t = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "map sky", wrapModeU = TextureWrapMode.Repeat, wrapModeV = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                    if (!t.LoadImage(File.ReadAllBytes(val), true)) { Destroy(t); continue; }
                    t.wrapModeU = TextureWrapMode.Repeat;
                    t.wrapModeV = TextureWrapMode.Clamp;
                    var sh = Resources.Load<Shader>("DeliriumSky");
                    if (sh == null) { Destroy(t); continue; }
                    if (skyMat == null) skyMat = new Material(sh) { name = "map sky" };
                    skyMat.mainTexture = t;
                    skyTex = t;
                    RenderSettings.skybox = skyMat;
                    cam.clearFlags = CameraClearFlags.Skybox;
                }
                catch (Exception e) { Debug.LogWarning("[Sandbox] map sky: " + e.Message); }
            }
            else if (key == "fog")
            {
                var f = Nums(val); // r g b start end maxdensity (m)
                if (f.Length < 5) continue;
                RenderSettings.fog = true;
                RenderSettings.fogMode = FogMode.Linear;
                RenderSettings.fogColor = new Color(f[0], f[1], f[2]);
                float start = f[3] * k, end = f[4] * k;
                // thinner than full at its farthest: as far again to get there
                if (f.Length >= 6 && f[5] > 0.05f && f[5] < 1f) end = start + (end - start) / f[5];
                RenderSettings.fogStartDistance = start;
                RenderSettings.fogEndDistance = Mathf.Max(end, start + 1f);
                cam.farClipPlane = Mathf.Clamp(end * 1.05f, lookFar, 3000f * k);
                if (RenderSettings.skybox == null) cam.backgroundColor = RenderSettings.fogColor;
            }
            else if (key == "sun" && sun != null)
            {
                var f = Nums(val); // r g b intensity, the way its light goes
                if (f.Length < 7) continue;
                sun.color = new Color(f[0], f[1], f[2]);
                sun.intensity = f[3];
                var d = new Vector3(f[4], f[5], f[6]);
                if (d.sqrMagnitude > 1e-4f) sun.transform.rotation = Quaternion.LookRotation(d.normalized);
            }
            else if (key == "ambient")
            {
                var f = Nums(val);
                if (f.Length < 3) continue;
                var a = new Color(f[0], f[1], f[2]);
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
                RenderSettings.ambientSkyColor = a * 1.1f;
                RenderSettings.ambientEquatorColor = a;
                RenderSettings.ambientGroundColor = a * 0.6f;
            }
        }
    }
}
