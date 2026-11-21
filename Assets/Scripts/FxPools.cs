// Blood that lies: a drop landing on the floor near blood already there joins it (that mark grows, as a pool
// does: area added each drop) rather than making a speck of its own; on a wall it runs: a streak down the wall
// from where it hit, long at first and slower as it goes, stopping.
using System.Collections.Generic;
using UnityEngine;

public partial class Game
{
    class MarkInfo { public Vector3 Normal; public float Size; public bool Pool; }
    readonly Dictionary<GameObject, MarkInfo> fxMarkInfo = new Dictionary<GameObject, MarkInfo>();
    static readonly Color kPoolTint = new Color(0.34f, 0.015f, 0.015f, 0.93f);
    const float kPoolMax = 1.3f;

    // ------------------------------------------------------------------ textures
    // a pool: round, its edge uneven and a little soft
    static Texture2D FxPoolTex(int n)
    {
        return FxTex(n, (x, y) =>
        {
            float r = Mathf.Sqrt(x * x + y * y), a = Mathf.Atan2(y, x);
            float edge = 0.78f + 0.1f * Mathf.Sin(a * 3f + 1f) + 0.06f * Mathf.Sin(a * 7f + 2f) + 0.05f * (Mathf.PerlinNoise(x * 4f + 3f, y * 4f + 7f) - 0.5f);
            return Mathf.Clamp01((edge - r) * 14f);
        }, false);
    }

    // a run: a thin streak (top: v = 1) ending in a drop
    static Texture2D FxRunTex(int w, int h)
    {
        var t = new Texture2D(w, h, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        var px = new Color32[w * h];
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                float x = (i + 0.5f) / w * 2f - 1f, v = (j + 0.5f) / h;
                float half = v < 0.08f ? 0.7f * Mathf.Sqrt(Mathf.Clamp01(v / 0.08f)) : Mathf.Lerp(0.45f, 0.3f, (v - 0.08f) / 0.92f);
                if (v > 0.9f) half *= Mathf.Clamp01((1f - v) / 0.1f) * 0.5f + 0.5f;
                float a = Mathf.Clamp01((half - Mathf.Abs(x)) / Mathf.Max(0.05f, half * 0.35f));
                px[j * w + i] = new Color32(255, 255, 255, (byte)(a * 255f));
            }
        t.SetPixels32(px);
        t.Apply(true, true);
        return t;
    }

    // ------------------------------------------------------------------ a drop lands
    void FxDropLanded(Vector3 at, Vector3 normal, Collider on)
    {
        if (fx?.Pool == null) return;
        bool floor = normal.y > 0.6f;
        // blood near it already, lying the same way: that grows
        GameObject best = null;
        MarkInfo bestInfo = null;
        float bestD = float.MaxValue;
        foreach (var kv in fxMarkInfo)
        {
            var g = kv.Key;
            var mi = kv.Value;
            if (g == null || !g.activeInHierarchy || mi.Size >= kPoolMax || Vector3.Dot(mi.Normal, normal) < 0.85f) continue;
            if (!mi.Pool && floor) continue; // (a bullet hole, a spatter: a pool of its own beside it)
            Vector3 off = Vector3.ProjectOnPlane(g.transform.position - at, normal);
            float d = off.magnitude;
            if (d < mi.Size * 0.42f + 0.03f && Mathf.Abs(Vector3.Dot(g.transform.position - at, normal)) < 0.05f && d < bestD) { bestD = d; best = g; bestInfo = mi; }
        }
        if (best != null && bestInfo.Pool)
        {
            // a drop's worth of area (about a 5 cm disc), and the pool's middle a little toward it
            float grown = Mathf.Min(kPoolMax, Mathf.Sqrt(bestInfo.Size * bestInfo.Size + 0.0028f));
            float k = grown / Mathf.Max(bestInfo.Size, 1e-3f);
            bestInfo.Size = grown;
            var tr = best.transform;
            tr.localScale *= k;
            tr.position = Vector3.Lerp(tr.position, at + normal * 0.006f, 0.05f);
            if (!floor && Random.value < 0.25f) FxRun(at, normal, on, 0.012f + 0.01f * Random.value, 0.15f + 0.3f * Random.value);
            return;
        }
        var made = FxMark(fx.Pool, at, normal, Random.Range(0.045f, 0.08f), kPoolTint, on);
        if (made != null && !floor && Random.value < 0.4f) FxRun(at, normal, on, 0.01f + 0.012f * Random.value, 0.1f + 0.35f * Random.value);
    }

    // a spatter on a wall: a few runs down from it
    void FxRunsFrom(Vector3 at, Vector3 normal, float size, Collider on)
    {
        if (normal.y > 0.6f || normal.y < -0.6f) return; // (a floor, a ceiling)
        int n = Random.Range(1, OnQuest ? 3 : 5);
        Vector3 side = Vector3.Cross(normal, Vector3.up);
        if (side.sqrMagnitude < 1e-4f) return;
        side.Normalize();
        for (int i = 0; i < n; i++)
            FxRun(at + side * Random.Range(-0.35f, 0.35f) * size - Vector3.up * size * 0.1f, normal, on, 0.012f + 0.02f * Random.value * size, (0.3f + 0.9f * Random.value) * Mathf.Clamp(size * 2f, 0.4f, 1.5f));
    }

    // ------------------------------------------------------------------ runs down walls
    class Run { public GameObject G; public float Born, Length, Width, Tau; public bool Done; }
    readonly List<Run> fxRuns = new List<Run>();
    int fxRunNext;
    Mesh fxRunMesh;
    int MaxRuns => OnQuest ? 24 : 60;

    void FxRun(Vector3 at, Vector3 normal, Collider on, float width, float length)
    {
        if (fx?.Run == null) return;
        Vector3 down = Vector3.ProjectOnPlane(Vector3.down, normal);
        if (down.sqrMagnitude < 0.04f) return; // (a floor: nothing runs)
        down.Normalize();
        if (fxRunMesh == null)
        {
            // pivot at its top: it grows down
            fxRunMesh = new Mesh { name = "run" };
            fxRunMesh.vertices = new[] { new Vector3(-0.5f, -1f, 0f), new Vector3(0.5f, -1f, 0f), new Vector3(0.5f, 0f, 0f), new Vector3(-0.5f, 0f, 0f) };
            fxRunMesh.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
            fxRunMesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            fxRunMesh.RecalculateBounds();
        }
        Run r = null;
        fxRuns.RemoveAll(x => x.G == null);
        if (fxRuns.Count < MaxRuns)
        {
            var g = new GameObject("Run");
            g.AddComponent<MeshFilter>().sharedMesh = fxRunMesh;
            var mr = g.AddComponent<MeshRenderer>();
            mr.sharedMaterial = fx.Run;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            var mpb = new MaterialPropertyBlock();
            mpb.SetColor("_Color", new Color(0.36f, 0.015f, 0.015f, 0.92f));
            mr.SetPropertyBlock(mpb);
            r = new Run { G = g };
            fxRuns.Add(r);
        }
        else
        {
            fxRunNext = (fxRunNext + 1) % fxRuns.Count;
            r = fxRuns[fxRunNext];
        }
        r.Born = Time.time;
        r.Length = length;
        r.Width = width;
        r.Tau = Random.Range(2.5f, 6f);
        r.Done = false;
        var t = r.G.transform;
        t.SetParent(null, false);
        t.SetPositionAndRotation(at + normal * 0.007f, Quaternion.LookRotation(normal, -down));
        t.localScale = new Vector3(width, 0.001f, 1f);
        Transform parent = on != null && on.attachedRigidbody != null && on.attachedRigidbody.isKinematic ? on.transform : null;
        if (parent != null) t.SetParent(parent, true);
    }

    // (each frame) the runs that are still running: longer, slower as they go
    void FxRunsUpdate()
    {
        float now = Time.time;
        foreach (var r in fxRuns)
        {
            if (r.Done || r.G == null) continue;
            float t = now - r.Born;
            float len = r.Length * (1f - Mathf.Exp(-t / r.Tau));
            var tr = r.G.transform;
            // (under a mover: its scale is the parent's too; set as a world size)
            Vector3 world = new Vector3(r.Width, Mathf.Max(0.001f, len), 1f);
            Vector3 ps = tr.parent != null ? tr.parent.lossyScale : Vector3.one;
            tr.localScale = new Vector3(world.x / Mathf.Max(1e-4f, Mathf.Abs(ps.x)), world.y / Mathf.Max(1e-4f, Mathf.Abs(ps.y)), 1f);
            if (t > r.Tau * 4f) r.Done = true;
        }
    }

    void FxRunsClear()
    {
        foreach (var r in fxRuns) if (r.G != null) Destroy(r.G);
        fxRuns.Clear();
        fxRunNext = 0;
    }
}
