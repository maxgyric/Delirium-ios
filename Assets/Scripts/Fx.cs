// Effects: blood (a spray and a mist where a bullet or a hard punch goes in, drops that fall and leave splats, a
// splat on the wall behind), bullets' impacts (sparks, dust, a hole), tracers, and the muzzle (a flash, a light, a
// few sparks, smoke, a shell). All made here (textures drawn in code, Hidden/Delirium/Fx in Resources): one
// particle system each, emitted into by hand (each particle its own place, speed, size), and a pool of marks
// (quads on the walls and floors, the oldest reused).
using System;
using System.Collections.Generic;
using UnityEngine;

public partial class Game
{
    class FxSet
    {
        public ParticleSystem Mist, Drops, Sparks, Dust, Smoke, Flash, Tracer, Shell;
        public Material Splat, Hole, Pool, Run;
        public Light Light;
    }
    FxSet fx;
    Vector3 fxMuzzlePos;
    int fxMuzzleFrame = -1;
    readonly List<GameObject> fxMarks = new List<GameObject>();
    int fxMarkNext;
    Mesh fxQuad;
    float fxLightT;
    static bool OnQuest => Application.platform == RuntimePlatform.Android;
    int FxMaxMarks => OnQuest ? 90 : 160;

    // ------------------------------------------------------------------ made once
    void FxMake()
    {
        if (fx != null) return;
        var sh = Shader.Find("Hidden/Delirium/Fx");
        if (sh == null) { Debug.LogWarning("[Sandbox] effects: no Hidden/Delirium/Fx shader"); fx = new FxSet(); return; }
        var blob = FxTex(64, (x, y) => { float r = Mathf.Sqrt(x * x + y * y); return Mathf.Clamp01(1f - r) * Mathf.Clamp01(1f - r); }, false);
        var soft = FxTex(64, (x, y) => { float r = Mathf.Sqrt(x * x + y * y); float n = Mathf.PerlinNoise(x * 3f + 5f, y * 3f + 9f); return Mathf.Clamp01((1f - r) * (0.55f + 0.7f * n)) * Mathf.Clamp01(1f - r * r); }, false);
        var star = FxTex(64, (x, y) =>
        {
            float r = Mathf.Sqrt(x * x + y * y), a = Mathf.Atan2(y, x);
            float rays = Mathf.Pow(Mathf.Abs(Mathf.Cos(a * 2.5f)), 6f);
            return Mathf.Clamp01((1f - r) * (0.45f + rays * 0.8f) * 1.6f - r * 0.3f);
        }, false);
        var splat = FxSplatTex(128);
        var hole = FxTex(64, (x, y) =>
        {
            float r = Mathf.Sqrt(x * x + y * y);
            float n = Mathf.PerlinNoise(x * 6f + 2f, y * 6f + 4f);
            return r < 0.22f ? 1f : Mathf.Clamp01((0.75f + n * 0.25f - r) * 2.2f) * 0.65f;
        }, true);
        var root = new GameObject("Effects");
        DontDestroyOnLoad(root);
        fx = new FxSet
        {
            Mist = FxSystem(root, "Blood mist", FxMat(sh, soft, false), 0.15f, OnQuest ? 80 : 200, false, 0f),
            Drops = FxSystem(root, "Blood drops", FxMat(sh, blob, false), 1f, OnQuest ? 160 : 400, true, 0.03f),
            Sparks = FxSystem(root, "Sparks", FxMat(sh, blob, true), 0.9f, OnQuest ? 120 : 300, true, 0.006f),
            Dust = FxSystem(root, "Dust", FxMat(sh, soft, false), -0.05f, OnQuest ? 80 : 200, false, 0f),
            Smoke = FxSystem(root, "Gun smoke", FxMat(sh, soft, false), -0.04f, OnQuest ? 60 : 160, false, 0f),
            Flash = FxSystem(root, "Muzzle flash", FxMat(sh, star, true), 0f, 40, false, 0f),
            Tracer = FxSystem(root, "Tracers", FxMat(sh, blob, true), 0f, 60, true, 0.0035f),
            Shell = FxSystem(root, "Shells", FxMat(sh, blob, false), 1f, 40, true, 0.004f),
            Splat = FxMat(sh, splat, false, -2f),
            Hole = FxMat(sh, hole, false, -2f),
            Pool = FxMat(sh, FxPoolTex(96), false, -2f),
            Run = FxMat(sh, FxRunTex(32, 128), false, -3f),
        };
        // the drops and shells land; a drop leaves a splat where it does
        FxCollide(fx.Drops, 0.05f, 1f, true);
        FxSolid(fx.Drops, 1.3f);
        FxCollide(fx.Shell, 0.45f, 0f, false);
        var lgo = new GameObject("Muzzle light");
        lgo.transform.SetParent(root.transform, false);
        fx.Light = lgo.AddComponent<Light>();
        fx.Light.type = LightType.Point;
        fx.Light.color = new Color(1f, 0.75f, 0.4f);
        fx.Light.range = 5f;
        fx.Light.shadows = LightShadows.None;
        fx.Light.enabled = false;
        fxQuad = new Mesh { name = "mark" };
        fxQuad.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f) };
        fxQuad.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
        fxQuad.colors = new[] { Color.white, Color.white, Color.white, Color.white };
        fxQuad.triangles = new[] { 0, 2, 1, 0, 3, 2 };
        fxQuad.RecalculateBounds();
    }

    static Material FxMat(Shader sh, Texture2D t, bool add, float offset = 0f)
    {
        var m = new Material(sh) { mainTexture = t, renderQueue = 3000 };
        m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        m.SetFloat("_DstBlend", (float)(add ? UnityEngine.Rendering.BlendMode.One : UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha));
        m.SetFloat("_Offset", offset);
        return m;
    }

    static Texture2D FxTex(int n, Func<float, float, float> alpha, bool dark)
    {
        var t = new Texture2D(n, n, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        var px = new Color32[n * n];
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                float x = (i + 0.5f) / n * 2f - 1f, y = (j + 0.5f) / n * 2f - 1f;
                byte a = (byte)(Mathf.Clamp01(alpha(x, y)) * 255f);
                byte c = dark ? (byte)(Mathf.Sqrt(x * x + y * y) < 0.22f ? 8 : 40) : (byte)255;
                px[j * n + i] = new Color32(c, c, c, a);
            }
        t.SetPixels32(px);
        t.Apply(true, true);
        return t;
    }

    // a splat: a blob and drops flung round it, uneven edges (white: the mark's colour tints it)
    static Texture2D FxSplatTex(int n)
    {
        var rnd = new System.Random(7);
        var dots = new List<Vector3>(); // x, y, radius
        dots.Add(new Vector3(0f, 0f, 0.42f));
        for (int i = 0; i < 9; i++) { float a = (float)rnd.NextDouble() * 6.283f, d = 0.15f + (float)rnd.NextDouble() * 0.25f; dots.Add(new Vector3(Mathf.Cos(a) * d, Mathf.Sin(a) * d, 0.12f + (float)rnd.NextDouble() * 0.14f)); }
        for (int i = 0; i < 16; i++) { float a = (float)rnd.NextDouble() * 6.283f, d = 0.5f + (float)rnd.NextDouble() * 0.4f; dots.Add(new Vector3(Mathf.Cos(a) * d, Mathf.Sin(a) * d, 0.025f + (float)rnd.NextDouble() * 0.05f)); }
        return FxTex(n, (x, y) =>
        {
            float a = 0f;
            foreach (var d in dots)
            {
                float r = Mathf.Sqrt((x - d.x) * (x - d.x) + (y - d.y) * (y - d.y));
                a = Mathf.Max(a, Mathf.Clamp01((d.z - r) / Mathf.Max(0.01f, d.z * 0.25f)));
            }
            return a * (0.8f + 0.2f * Mathf.PerlinNoise(x * 9f, y * 9f));
        }, false);
    }

    static ParticleSystem FxSystem(GameObject root, string name, Material mat, float gravity, int max, bool stretch, float stretchBy)
    {
        var go = new GameObject(name);
        go.transform.SetParent(root.transform, false);
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = ps.main;
        main.playOnAwake = false;
        main.loop = true; // (always playing, emitting nothing by itself: Emit puts each particle in)
        main.duration = 1f;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = max;
        main.gravityModifier = gravity;
        main.scalingMode = ParticleSystemScalingMode.Hierarchy;
        var em = ps.emission;
        em.enabled = false;
        var shape = ps.shape;
        shape.enabled = false;
        // they fade out (and the puffs grow)
        var col = ps.colorOverLifetime;
        col.enabled = true;
        var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.8f, 0.5f), new GradientAlphaKey(0f, 1f) });
        col.color = g;
        if (!stretch)
        {
            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.6f, 1f, 1.6f));
        }
        var r = go.GetComponent<ParticleSystemRenderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        r.renderMode = stretch ? ParticleSystemRenderMode.Stretch : ParticleSystemRenderMode.Billboard;
        if (stretch) { r.velocityScale = stretchBy; r.lengthScale = 1f; }
        r.maxParticleSize = 2f;
        ps.Play();
        return ps;
    }

    // a particle that stays whole till it dies (a drop: it goes when it lands), drawn `stretch` times its trail
    static void FxSolid(ParticleSystem ps, float stretch)
    {
        var col = ps.colorOverLifetime;
        var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 0.85f), new GradientAlphaKey(0f, 1f) });
        col.color = g;
        var r = ps.GetComponent<ParticleSystemRenderer>();
        r.lengthScale = stretch;
        r.minParticleSize = 0f;
    }

    void FxCollide(ParticleSystem ps, float bounce, float lifeLoss, bool tellMe)
    {
        var c = ps.collision;
        c.enabled = true;
        c.type = ParticleSystemCollisionType.World;
        c.mode = ParticleSystemCollisionMode.Collision3D;
        c.quality = ParticleSystemCollisionQuality.Medium;
        c.collidesWith = VrWorldMask();
        c.bounce = bounce;
        c.dampen = 0.4f;
        c.lifetimeLoss = lifeLoss;
        c.radiusScale = 0.5f;
        c.sendCollisionMessages = tellMe;
        if (tellMe) ps.gameObject.AddComponent<FxDropLands>().G = this;
    }

    // ------------------------------------------------------------------ emitting
    static ParticleSystem.EmitParams P(Vector3 at, Vector3 v, float size, float life, Color c)
    {
        return new ParticleSystem.EmitParams
        {
            position = at, velocity = v, startSize = size, startLifetime = life, startColor = c,
            rotation = UnityEngine.Random.Range(0f, 360f), applyShapeToPosition = false,
        };
    }
    static void Emit(ParticleSystem ps, ParticleSystem.EmitParams p) { if (ps != null) ps.Emit(p, 1); }
    static Vector3 Rnd(float r) => UnityEngine.Random.insideUnitSphere * r;

    static readonly Color kBlood = new Color(0.62f, 0.03f, 0.03f, 1f), kBloodMist = new Color(0.55f, 0.04f, 0.04f, 0.45f);

    // where something goes into a man, `dir` its way; amount: 1 a pistol's bullet
    void FxBlood(Vector3 at, Vector3 dir, float amount)
    {
        FxMake();
        if (fx.Mist == null) return;
        dir = dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector3.up;
        amount = Mathf.Clamp(amount, 0.2f, 3f);
        int mist = Mathf.RoundToInt(3 + amount * 2), drops = Mathf.RoundToInt((OnQuest ? 6 : 10) * amount);
        for (int i = 0; i < mist; i++)
            Emit(fx.Mist, P(at + Rnd(0.03f), dir * UnityEngine.Random.Range(0.2f, 1.2f) - dir * 0.6f * (i % 2) + Rnd(0.35f), UnityEngine.Random.Range(0.08f, 0.18f) * Mathf.Sqrt(amount), UnityEngine.Random.Range(0.25f, 0.5f), kBloodMist));
        for (int i = 0; i < drops; i++)
        {
            // most out the far side, some back toward the shooter
            Vector3 way = (i % 3 == 0 ? -dir * 0.6f : dir) + Rnd(0.55f) + Vector3.up * 0.25f;
            Emit(fx.Drops, P(at + Rnd(0.02f), way * UnityEngine.Random.Range(1.2f, 4.5f), UnityEngine.Random.Range(0.022f, 0.054f), UnityEngine.Random.Range(0.6f, 1.4f), kBlood));
        }
        // the wall behind him
        if (amount >= 0.6f && Physics.Raycast(at + dir * 0.25f, dir, out var hit, 2.8f, VrWorldMask(), QueryTriggerInteraction.Ignore) && FxCanMark(hit.collider))
        {
            float size = UnityEngine.Random.Range(0.25f, 0.5f) * Mathf.Sqrt(amount);
            FxMark(fx.Splat, hit.point, hit.normal, size, new Color(0.38f, 0.02f, 0.02f, 0.92f), hit.collider);
            FxRunsFrom(hit.point, hit.normal, size, hit.collider);
        }
        // and a drip below
        if (amount >= 0.8f && Physics.Raycast(at, Vector3.down, out var floor, 2.5f, VrWorldMask(), QueryTriggerInteraction.Ignore) && FxCanMark(floor.collider))
            FxMark(fx.Pool, floor.point, floor.normal, UnityEngine.Random.Range(0.08f, 0.16f), kPoolTint, floor.collider);
    }

    // a bullet on the map: sparks, dust, a hole
    void FxImpact(Vector3 at, Vector3 normal, Vector3 dir, Collider on)
    {
        FxMake();
        if (fx.Sparks == null) return;
        Vector3 off = Vector3.Reflect(dir.normalized, normal);
        int n = OnQuest ? 5 : 9;
        for (int i = 0; i < n; i++)
            Emit(fx.Sparks, P(at + normal * 0.01f, (off * 0.6f + normal * 0.5f + Rnd(0.7f)) * UnityEngine.Random.Range(2.5f, 7f), UnityEngine.Random.Range(0.012f, 0.025f), UnityEngine.Random.Range(0.12f, 0.35f), new Color(1f, UnityEngine.Random.Range(0.55f, 0.85f), 0.3f, 1f)));
        var dust = new Color(0.55f, 0.5f, 0.44f, 0.6f);
        for (int i = 0; i < (OnQuest ? 3 : 5); i++)
            Emit(fx.Dust, P(at + normal * 0.03f, (normal * UnityEngine.Random.Range(0.4f, 1.4f) + Rnd(0.4f)), UnityEngine.Random.Range(0.08f, 0.2f), UnityEngine.Random.Range(0.6f, 1.3f), dust));
        if (FxCanMark(on)) FxMark(fx.Hole, at, normal, UnityEngine.Random.Range(0.05f, 0.08f), new Color(1f, 1f, 1f, 0.95f), on);
    }

    void FxTracer(Vector3 from, Vector3 to)
    {
        FxMake();
        if (fx.Tracer == null) return;
        Vector3 d = to - from;
        float dist = d.magnitude;
        if (dist < 1.5f) return;
        const float speed = 320f;
        Emit(fx.Tracer, P(from + d / dist * 0.6f, d / dist * speed, 0.025f, Mathf.Max(0.02f, (dist - 0.6f) / speed), new Color(1f, 0.85f, 0.55f, 0.85f)));
    }

    // the muzzle: `fwd` the barrel, `side` where the shells go
    void FxMuzzle(Vector3 at, Vector3 fwd, Vector3 side, Gun gun)
    {
        FxMake();
        if (fx.Flash == null) return;
        fxMuzzlePos = at;
        fxMuzzleFrame = Time.frameCount;
        fwd.Normalize();
        float big = gun.Long ? 1.35f : gun.Name == "deagle" ? 1.2f : 1f;
        var hot = new Color(1f, 0.8f, 0.45f, 1f);
        Emit(fx.Flash, P(at + fwd * 0.03f, fwd * 0.4f, 0.2f * big, 0.05f, hot));
        Emit(fx.Flash, P(at + fwd * 0.09f, fwd * 0.4f, 0.13f * big, 0.04f, hot));
        Emit(fx.Flash, P(at + fwd * 0.15f, fwd * 0.4f, 0.08f * big, 0.035f, hot));
        for (int i = 0; i < (OnQuest ? 2 : 4); i++)
            Emit(fx.Sparks, P(at, (fwd + Rnd(0.25f)) * UnityEngine.Random.Range(6f, 12f), 0.01f, UnityEngine.Random.Range(0.05f, 0.12f), hot));
        var smoke = new Color(0.75f, 0.75f, 0.75f, 0.35f);
        for (int i = 0; i < (gun.Pellets ? 4 : 2); i++)
            Emit(fx.Smoke, P(at + fwd * (0.05f + i * 0.06f), fwd * UnityEngine.Random.Range(0.3f, 0.9f) + Vector3.up * 0.15f + Rnd(0.1f), UnityEngine.Random.Range(0.05f, 0.1f) * big, UnityEngine.Random.Range(0.9f, 1.6f), smoke));
        if (!gun.Pellets || UnityEngine.Random.value < 0.5f)
            Emit(fx.Shell, P(at - fwd * (gun.Long ? 0.25f : 0.1f), (side.normalized * UnityEngine.Random.Range(1.2f, 2.2f) + Vector3.up * UnityEngine.Random.Range(1f, 1.8f) - fwd * 0.3f), gun.Long ? 0.022f : 0.016f, 1.6f, new Color(0.85f, 0.65f, 0.25f, 1f)));
        fx.Light.transform.position = at + fwd * 0.1f;
        fx.Light.intensity = 3f * big;
        fx.Light.enabled = true;
        fxLightT = 0.05f;
    }

    // (each frame) the muzzle's light goes out
    void FxUpdate()
    {
        FxRunsUpdate();
        WoundsUpdate();
        FallsUpdate();
        if (fx?.Light == null || !fx.Light.enabled) return;
        fxLightT -= Time.deltaTime;
        if (fxLightT <= 0f) fx.Light.enabled = false;
        else fx.Light.intensity *= 0.6f;
    }

    // ------------------------------------------------------------------ a bullet's way (every shot: Missed is called for each)
    // the man struck (his nearest part to the line), else the first thing of the map on it; the tracer from the
    // muzzle if one was just fired; a shotgun's pellets fanned out
    void FxBullet(Ray ray, Man struck)
    {
        var gun = kGuns[weapon % kGuns.Length];
        if (gun.Fists) return;
        bool muzzle = fxMuzzleFrame == Time.frameCount;
        int pellets = gun.Pellets ? (OnQuest ? 5 : 8) : 1;
        for (int p = 0; p < pellets; p++)
        {
            Vector3 dir = ray.direction;
            if (p > 0) dir = (Quaternion.AngleAxis(UnityEngine.Random.Range(0f, 360f), ray.direction) * Quaternion.AngleAxis(UnityEngine.Random.Range(1f, 4f), Vector3.Cross(ray.direction, Vector3.up).normalized + Vector3.up * 0.001f) * ray.direction).normalized;
            Vector3 end = ray.origin + dir * 120f;
            if (p == 0 && struck != null && struck.Body != null && FxShotMan(new Ray(ray.origin, dir), struck, gun, out Vector3 at)) end = at;
            else if (Physics.Raycast(ray.origin, dir, out var hit, 300f, VrWorldMask(), QueryTriggerInteraction.Ignore))
            {
                var rb = hit.collider.attachedRigidbody;
                if (rb != null && hit.collider.transform.root.name.Contains("ragdoll")) FxBlood(hit.point, dir, gun.Pellets ? 0.4f : gun.Damage / 40f);
                else if (rb == null || rb.GetComponent<VrHand>() == null) FxImpact(hit.point, hit.normal, dir, hit.collider);
                end = hit.point;
            }
            if (muzzle && (p == 0 || p % 2 == 1)) FxTracer(fxMuzzlePos, end);
        }
    }

    // the point on `ray` nearest a part of his (his bones' joints and their middles)
    static bool FxOnMan(Ray ray, Man m, out Vector3 at)
    {
        at = default;
        float best = 0.35f;
        bool any = false;
        foreach (var b in m.Body.Bones)
        {
            if (b == null) continue;
            for (int half = 0; half < 2; half++)
            {
                Vector3 p = half == 0 || b.parent == null ? b.position : (b.position + b.parent.position) * 0.5f;
                float along = Vector3.Dot(p - ray.origin, ray.direction);
                if (along <= 0f) continue;
                Vector3 q = ray.origin + ray.direction * along;
                float d = (q - p).magnitude;
                if (d < best) { best = d; at = q; any = true; }
            }
        }
        return any;
    }

    // ------------------------------------------------------------------ marks
    static bool FxCanMark(Collider c)
    {
        if (c == null) return false;
        var rb = c.attachedRigidbody;
        if (rb != null && (!rb.isKinematic || c.transform.root.name.Contains("ragdoll") || rb.GetComponent<VrHand>() != null)) return false;
        return c.gameObject.layer != kUiLayer && c.gameObject.layer != 2;
    }

    GameObject FxMark(Material mat, Vector3 at, Vector3 normal, float size, Color tint, Collider on)
    {
        if (mat == null || fxQuad == null) return null;
        GameObject g = null;
        if (fxMarks.Count < FxMaxMarks)
        {
            g = new GameObject("Mark");
            g.AddComponent<MeshFilter>().sharedMesh = fxQuad;
            var r = g.AddComponent<MeshRenderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            fxMarks.Add(g);
        }
        else
        {
            for (int tries = 0; tries < fxMarks.Count && g == null; tries++)
            {
                fxMarkNext = (fxMarkNext + 1) % fxMarks.Count;
                g = fxMarks[fxMarkNext];
                if (g == null) { fxMarks.RemoveAt(fxMarkNext); fxMarkNext = Mathf.Max(0, fxMarkNext - 1); }
            }
            if (g == null) return null;
        }
        fxMarkInfo[g] = new MarkInfo { Normal = normal, Size = size, Pool = mat == fx.Pool };
        var mr = g.GetComponent<MeshRenderer>();
        mr.sharedMaterial = mat;
        var mpb = new MaterialPropertyBlock();
        mpb.SetColor("_Color", tint);
        mr.SetPropertyBlock(mpb);
        // on what moves (a door, a lift): with it; else in the world
        Transform parent = on != null && on.attachedRigidbody != null && on.attachedRigidbody.isKinematic ? on.transform : null;
        g.transform.SetParent(null, false);
        g.transform.localScale = Vector3.one * size;
        g.transform.SetPositionAndRotation(at + normal * 0.006f, Quaternion.LookRotation(normal) * Quaternion.Euler(0f, 0f, UnityEngine.Random.Range(0f, 360f)));
        if (parent != null) g.transform.SetParent(parent, true);
        return g;
    }

    // (map change) the marks go with the old map
    void FxClearMarks()
    {
        foreach (var g in fxMarks) if (g != null) Destroy(g);
        fxMarks.Clear();
        fxMarkInfo.Clear();
        FxRunsClear();
        fxMarkNext = 0;
    }

    // a blood drop that landed: a small splat (a few a second at most)
    public class FxDropLands : MonoBehaviour
    {
        public Game G;
        readonly List<ParticleCollisionEvent> events = new List<ParticleCollisionEvent>();
        float budget;
        ParticleSystem ps;

        void Update() => budget = Mathf.Min(budget + Time.deltaTime * 25f, 10f);

        void OnParticleCollision(GameObject other)
        {
            if (G == null || budget < 1f) return;
            if (ps == null) ps = GetComponent<ParticleSystem>();
            var c = other != null ? other.GetComponent<Collider>() : null;
            if (!FxCanMark(c)) return;
            int n = ps.GetCollisionEvents(other, events);
            for (int i = 0; i < n && budget >= 1f; i++)
            {
                budget -= 1f;
                G.FxDropLanded(events[i].intersection, events[i].normal, c);
            }
        }
    }
}
