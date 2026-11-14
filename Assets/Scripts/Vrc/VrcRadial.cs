// As the hero in an imported VRChat avatar: an Expressions button by the others, opening the avatar's own menu as
// VRChat draws it — a wheel of its items (icons and names), Back at the top, sub-menus inside it; a toggle lit
// while on; a radial a dial you turn with your finger.
using System.Collections.Generic;
using UnityEngine;

public partial class Game
{
    bool radialOpen;
    readonly List<VrcLook.Item> radialStack = new List<VrcLook.Item>();
    VrcLook.Item radialDial;          // a radial being turned
    int radialFinger = -99;
    readonly Dictionary<int, Texture2D> radialWheels = new Dictionary<int, Texture2D>();
    GUIStyle radialLabel, radialTitle;

    Vector2 BtnExpr(float w, float h) => new Vector2(w - 80f, h - 300f);

    // the hero's avatar's menu (null: his model isn't an imported VRChat avatar, or has no menu)
    VrcLook HeroLook()
    {
        if (heroTemplate == null) return null;
        var look = heroTemplate.GetComponentInChildren<VrcLook>(true);
        var d = look != null ? look.D : null;
        return d != null && d.Menu != null && d.Menu.Items.Count > 0 ? look : null;
    }

    VrcLook.Item RadialMenu(VrcLook look) => radialStack.Count > 0 ? radialStack[radialStack.Count - 1] : look.D.Menu;

    void RadialGeometry(float w, float h, out Vector2 c, out float r)
    {
        c = new Vector2(w * 0.5f, h * 0.52f);
        r = Mathf.Min(h * 0.4f, 230f);
    }

    /// A finger put down: true when the Expressions button or the open wheel takes it.
    bool RadialTap(Vector2 at, int finger, float w, float h)
    {
        var look = HeroLook();
        if (look == null) { radialOpen = false; return false; }
        if (Vector2.Distance(at, BtnExpr(w, h)) < kBtnR + 6f)
        {
            radialOpen = !radialOpen;
            radialStack.Clear();
            radialDial = null;
            return true;
        }
        if (!radialOpen) return false;
        RadialGeometry(w, h, out var c, out float r);
        float dist = Vector2.Distance(at, c);
        if (dist > r) return false; // outside: the camera, the stick...
        if (radialDial != null)
        {
            if (dist < r * 0.28f) radialDial = null; // the middle: done
            else { radialFinger = finger; RadialDrag(at, w, h); }
            return true;
        }
        var menu = RadialMenu(look);
        if (dist < r * 0.28f) { Back(); return true; }
        int n = menu.Items.Count + 1, k = Slot(at - c, n);
        if (k == 0) { Back(); return true; }
        var it = menu.Items[k - 1];
        var d = look.D;
        if (it.Kind == 103) radialStack.Add(it);
        else if (it.Kind == 203 && it.Toggle != null) { radialDial = it; radialFinger = finger; }
        else if (it.Toggle != null) VrcLook.Set(look.Key, it.Toggle, !VrcLook.IsOn(d, it.Toggle));
        return true;
    }

    void Back()
    {
        if (radialStack.Count > 0) radialStack.RemoveAt(radialStack.Count - 1);
        else radialOpen = false;
    }

    // the slot a point is in: 0 at the top, clockwise
    static int Slot(Vector2 d, int n)
    {
        float a = Mathf.Atan2(d.x, -d.y) * Mathf.Rad2Deg; // 0 up, clockwise (screen y runs down)
        if (a < 0f) a += 360f;
        return Mathf.RoundToInt(a / (360f / n)) % n;
    }

    /// A finger held on the dial: its angle from the top, clockwise, is the value.
    void RadialDrag(Vector2 at, float w, float h)
    {
        var look = HeroLook();
        if (look == null || radialDial == null || radialDial.Toggle == null) return;
        RadialGeometry(w, h, out var c, out float r);
        var d = at - c;
        if (d.magnitude < r * 0.15f) return;
        float a = Mathf.Atan2(d.x, -d.y) * Mathf.Rad2Deg;
        if (a < 0f) a += 360f;
        VrcLook.SetValue(look.Key, radialDial.Toggle.Param, a / 360f);
    }

    // The wheel's picture for n slots: a dark disc, its rim, a ring round the middle, lines between the slots.
    Texture2D Wheel(int n)
    {
        if (radialWheels.TryGetValue(n, out var t) && t != null) return t;
        const int S = 512;
        t = new Texture2D(S, S, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
        var px = new Color32[S * S];
        float step = 360f / n;
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float dx = x - S / 2f + 0.5f, dy = S / 2f - y - 0.5f;
                float rr = Mathf.Sqrt(dx * dx + dy * dy) / (S / 2f);
                float edge = Mathf.Clamp01((1f - rr) * S * 0.5f); // soft outer edge
                if (edge <= 0f) { px[y * S + x] = new Color32(0, 0, 0, 0); continue; }
                Color col = rr < 0.28f ? new Color(0.13f, 0.2f, 0.22f, 0.92f) : new Color(0.1f, 0.24f, 0.27f, 0.82f);
                // rims
                float line = 0f;
                line = Mathf.Max(line, 1f - Mathf.Abs(rr - 0.985f) * S * 0.5f / 2.5f);
                line = Mathf.Max(line, 1f - Mathf.Abs(rr - 0.28f) * S * 0.5f / 2f);
                line = Mathf.Max(line, 1f - Mathf.Abs(rr - 0.2f) * S * 0.5f / 1.5f);
                // between the slots
                if (rr > 0.28f && n > 1)
                {
                    float a = Mathf.Atan2(dx, dy) * Mathf.Rad2Deg; // 0 up, clockwise
                    if (a < 0f) a += 360f;
                    float off = Mathf.Repeat(a - step * 0.5f, step);
                    float px2 = Mathf.Min(off, step - off) * Mathf.Deg2Rad * rr * S * 0.5f;
                    line = Mathf.Max(line, 1f - px2 / 1.5f);
                }
                line = Mathf.Clamp01(line);
                col = Color.Lerp(col, new Color(0.35f, 0.62f, 0.66f, 0.95f), line * 0.8f);
                col.a *= edge;
                px[y * S + x] = col;
            }
        t.SetPixels32(px);
        t.Apply(false);
        radialWheels[n] = t;
        return t;
    }

    void RadialGui(float w, float h)
    {
        var look = HeroLook();
        if (look == null) return;
        Disc(BtnExpr(w, h), kBtnR, radialOpen ? new Color(0.35f, 0.75f, 1f, 0.6f) : new Color(1f, 1f, 1f, 0.27f), "Expr.");
        if (!radialOpen) return;
        if (radialLabel == null)
        {
            radialLabel = new GUIStyle(padText) { fontSize = 12, fontStyle = FontStyle.Normal, wordWrap = true, alignment = TextAnchor.UpperCenter };
            radialTitle = new GUIStyle(padText) { fontSize = 15 };
        }
        var d = look.D;
        RadialGeometry(w, h, out var c, out float r);
        var menu = RadialMenu(look);
        if (radialDial != null)
        {
            // the dial: the value round the rim from the top, its name and percent in the middle
            GUI.DrawTexture(new Rect(c.x - r, c.y - r, r * 2f, r * 2f), Wheel(1));
            float v = VrcLook.Value(d, radialDial.Toggle.Param);
            int dots = 72;
            for (int i = 0; i <= dots; i++)
            {
                float f = i / (float)dots;
                float a = f * Mathf.PI * 2f;
                var p = c + new Vector2(Mathf.Sin(a), -Mathf.Cos(a)) * r * 0.82f;
                Disc(p, 7f, f <= v ? new Color(0.35f, 0.85f, 0.95f, 0.95f) : new Color(1f, 1f, 1f, 0.12f));
            }
            float va = v * Mathf.PI * 2f;
            Disc(c + new Vector2(Mathf.Sin(va), -Mathf.Cos(va)) * r * 0.82f, 14f, new Color(1f, 1f, 1f, 0.95f));
            GUI.Label(new Rect(c.x - r * 0.6f, c.y - 30f, r * 1.2f, 24f), radialDial.Name, radialTitle);
            GUI.Label(new Rect(c.x - r * 0.6f, c.y - 4f, r * 1.2f, 24f), Mathf.RoundToInt(v * 100f) + "%", radialTitle);
            GUI.Label(new Rect(c.x - 120f, c.y + r + 6f, 240f, 22f), "turn it round the rim; tap the middle when done", radialLabel);
            return;
        }
        int n = menu.Items.Count + 1;
        GUI.DrawTexture(new Rect(c.x - r, c.y - r, r * 2f, r * 2f), Wheel(n));
        GUI.Label(new Rect(c.x - 150f, c.y - r - 30f, 300f, 24f), radialStack.Count == 0 ? "Expressions" : menu.Name, radialTitle);
        for (int k = 0; k < n; k++)
        {
            float a = k * (360f / n) * Mathf.Deg2Rad;
            var p = c + new Vector2(Mathf.Sin(a), -Mathf.Cos(a)) * r * 0.64f;
            string name;
            Texture2D icon = null;
            bool on = false;
            if (k == 0) name = radialStack.Count == 0 ? "Close" : "Back";
            else
            {
                var it = menu.Items[k - 1];
                name = it.Name;
                icon = d.Icon(it.Icon);
                if (it.Toggle != null && (it.Kind == 101 || it.Kind == 102)) on = VrcLook.IsOn(d, it.Toggle);
                if (it.Kind == 203 && it.Toggle != null) name += " " + Mathf.RoundToInt(VrcLook.Value(d, it.Toggle.Param) * 100f) + "%";
            }
            float sz = Mathf.Min(48f, r * 0.3f);
            if (on) Disc(p + new Vector2(0f, -8f), sz * 0.85f, new Color(0.35f, 0.85f, 0.95f, 0.55f));
            if (icon != null) GUI.DrawTexture(new Rect(p.x - sz * 0.5f, p.y - sz * 0.5f - 8f, sz, sz), icon, ScaleMode.ScaleToFit);
            else GUI.Label(new Rect(p.x - 30f, p.y - 22f, 60f, 24f), k == 0 ? (radialStack.Count == 0 ? "×" : "‹") : menu.Items[k - 1].Kind == 103 ? "…" : "●", radialTitle);
            float lw = Mathf.Min(120f, r * 2f * Mathf.PI / n * 0.9f);
            GUI.Label(new Rect(p.x - lw * 0.5f, p.y + sz * 0.5f - 6f, lw, 36f), name, radialLabel);
        }
    }
}
