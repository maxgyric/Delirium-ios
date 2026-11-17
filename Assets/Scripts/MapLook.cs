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

    // A GMod map's 3D skybox (the town round the map, its pieces "[sky3d]" in the .glb, 16 times their size): drawn
    // by a camera of its own first, then the map over it with the depth cleared, the way Source does. Its buildings
    // never cut into the map or show through it, its own fog on them, and as far as they go (the map's far plane
    // and fog had cut them off and flattened them to one colour).
    public const int Sky3DLayer = 29;
    Camera skyCam;
    SkyCamera skyCamLook;

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
        if (skyCam != null) skyCam.enabled = false;
        cam.cullingMask = ~(1 << PreviewLayer);
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
        float[] skyFog = null;
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
            else if (key == "skyfog")
            {
                var f = Nums(val); // r g b start end maxdensity (m): the 3D skybox's
                if (f.Length >= 5) skyFog = f;
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
        Sky3D(skyFog, k);
    }

    void Sky3D(float[] fog, float k)
    {
        bool any = false;
        float far = 0f; // how far its farthest piece is
        if (mapRoot != null)
            foreach (var r in mapRoot.GetComponentsInChildren<Renderer>())
                if (r.gameObject.layer == Sky3DLayer)
                {
                    any = true;
                    var b = r.bounds;
                    far = Mathf.Max(far, new Vector2(Mathf.Max(Mathf.Abs(b.min.x), Mathf.Abs(b.max.x)), Mathf.Max(Mathf.Abs(b.min.z), Mathf.Abs(b.max.z))).magnitude);
                }
        if (!any) return;
        if (skyCam == null)
        {
            var go = new GameObject("3D skybox camera");
            go.transform.SetParent(cam.transform, false);
            skyCam = go.AddComponent<Camera>();
            skyCamLook = go.AddComponent<SkyCamera>();
        }
        skyCamLook.main = cam;
        skyCam.CopyFrom(cam);
        skyCam.transform.localPosition = Vector3.zero;
        skyCam.transform.localRotation = Quaternion.identity;
        skyCam.depth = cam.depth - 1f;
        skyCam.cullingMask = 1 << Sky3DLayer;
        skyCam.clearFlags = cam.clearFlags; // the 2D sky (or its colour) behind it
        skyCam.nearClipPlane = 0.5f * k;
        skyCam.farClipPlane = 20000f * k;
        skyCam.enabled = true;
        cam.cullingMask = ~((1 << Sky3DLayer) | (1 << PreviewLayer));
        cam.clearFlags = CameraClearFlags.Depth;
        skyCamLook.fog = fog != null;
        if (fog != null)
        {
            skyCamLook.fogColor = new Color(fog[0], fog[1], fog[2]);
            // Source's: from nothing at its start up to its most (often half) at its end, then no more. Unity's
            // linear fog has no most: a line through Source's at the skybox's nearer and farthest pieces
            float start = fog[3] * k, end = Mathf.Max(fog[4] * k, start + 1f), most = fog.Length >= 6 ? Mathf.Clamp(fog[5], 0.01f, 1f) : 1f;
            System.Func<float, float> src = d => most * Mathf.Clamp01((d - start) / (end - start));
            float d2 = Mathf.Max(far, 1f), d1 = d2 * 0.3f;
            float f1 = src(d1), f2 = src(d2);
            float slope = Mathf.Max((f2 - f1) / (d2 - d1), 1e-6f);
            float s0 = d1 - f1 / slope;
            skyCamLook.fogStart = s0;
            skyCamLook.fogEnd = s0 + 1f / slope;
        }
    }
}

// The 3D skybox's camera: the main one's view (its field of view, its picture), its own fog and no shadows of its
// own while it draws
public class SkyCamera : MonoBehaviour
{
    public Camera main;
    public bool fog;
    public Color fogColor;
    public float fogStart, fogEnd;
    Camera self;
    bool wasFog;
    Color wasColor;
    float wasStart, wasEnd, wasShadows;
    FogMode wasMode;

    void LateUpdate()
    {
        if (self == null) self = GetComponent<Camera>();
        if (main != null && self.targetTexture != main.targetTexture) self.targetTexture = main.targetTexture;
    }

    void OnPreCull()
    {
        if (self == null) self = GetComponent<Camera>();
        if (main != null) self.fieldOfView = main.fieldOfView;
        wasShadows = QualitySettings.shadowDistance;
        QualitySettings.shadowDistance = 0f;
    }

    void OnPreRender()
    {
        wasFog = RenderSettings.fog;
        wasColor = RenderSettings.fogColor;
        wasStart = RenderSettings.fogStartDistance;
        wasEnd = RenderSettings.fogEndDistance;
        wasMode = RenderSettings.fogMode;
        RenderSettings.fog = fog;
        if (fog)
        {
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = fogColor;
            RenderSettings.fogStartDistance = fogStart;
            RenderSettings.fogEndDistance = fogEnd;
        }
    }

    void OnPostRender()
    {
        RenderSettings.fog = wasFog;
        RenderSettings.fogColor = wasColor;
        RenderSettings.fogStartDistance = wasStart;
        RenderSettings.fogEndDistance = wasEnd;
        RenderSettings.fogMode = wasMode;
        QualitySettings.shadowDistance = wasShadows;
    }
}
