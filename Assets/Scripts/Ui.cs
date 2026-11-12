// The interface's look: one dark, rounded skin made in code (no image files) for the launcher, the bars, the menu
// and the hero's pad. Every style the rest of the game draws with is made here.
using UnityEngine;

public partial class Game
{
    GUISkin skin;
    GUIStyle panel, chip, chk, chkOn, title;
    static readonly Color kAccent = new Color(0.16f, 0.52f, 0.96f, 1f);
    static readonly Color kInk = new Color(0.93f, 0.95f, 0.98f, 1f);
    static readonly Color kDim = new Color(0.66f, 0.70f, 0.78f, 1f);

    // A rounded box as a stretchable texture: `fill`, with a 1-pixel `edge` when it has one.
    static Texture2D Round(Color fill, int radius, Color edge)
    {
        int n = radius * 2 + 6;
        var t = new Texture2D(n, n, TextureFormat.RGBA32, false);
        t.hideFlags = HideFlags.HideAndDontSave;
        t.wrapMode = TextureWrapMode.Clamp;
        var px = new Color[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                // how far inside the rounded box this pixel is (negative: outside)
                float cx = Mathf.Clamp(x + 0.5f, radius, n - radius), cy = Mathf.Clamp(y + 0.5f, radius, n - radius);
                float d = radius - Mathf.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
                Color c = edge.a > 0f && d < 1.3f ? edge : fill;
                c.a *= Mathf.Clamp01(d + 0.5f);
                px[y * n + x] = c;
            }
        t.SetPixels(px);
        t.Apply();
        return t;
    }

    static GUIStyle Boxed(GUIStyle from, Texture2D normal, Texture2D hover, Texture2D active, Texture2D on, int radius)
    {
        var s = new GUIStyle(from);
        s.normal.background = normal;
        s.hover.background = hover;
        s.active.background = active;
        s.focused.background = normal;
        s.onNormal.background = on;
        s.onHover.background = on;
        s.onActive.background = on;
        s.onFocused.background = on;
        foreach (var st in new[] { s.normal, s.hover, s.active, s.focused, s.onNormal, s.onHover, s.onActive, s.onFocused }) st.textColor = kInk;
        s.border = new RectOffset(radius + 2, radius + 2, radius + 2, radius + 2);
        s.margin = new RectOffset(3, 3, 3, 3);
        s.padding = new RectOffset(10, 10, 5, 5);
        s.overflow = new RectOffset(0, 0, 0, 0);
        s.alignment = TextAnchor.MiddleCenter;
        return s;
    }

    void BuildSkin()
    {
        if (skin != null) return;
        skin = Instantiate(GUI.skin);
        var none = new Color(0f, 0f, 0f, 0f);
        var edge = new Color(1f, 1f, 1f, 0.10f);
        var bNormal = Round(new Color(0.15f, 0.17f, 0.22f, 0.92f), 9, edge);
        var bHover = Round(new Color(0.21f, 0.24f, 0.31f, 0.96f), 9, edge);
        var bActive = Round(new Color(0.10f, 0.36f, 0.72f, 1f), 9, edge);
        var bOn = Round(kAccent, 9, new Color(1f, 1f, 1f, 0.25f));

        big = Boxed(GUI.skin.button, bNormal, bHover, bActive, bOn, 9);
        big.fontSize = 15;
        big.fontStyle = FontStyle.Bold;
        btn = Boxed(GUI.skin.button, bNormal, bHover, bActive, bOn, 9);
        btn.fontSize = 13;
        btn.wordWrap = true;
        btn.padding = new RectOffset(6, 6, 4, 4);
        btnOn = new GUIStyle(btn);
        btnOn.normal.background = bOn;
        btnOn.hover.background = bOn;
        btnOn.fontStyle = FontStyle.Bold;

        var fNormal = Round(new Color(0.04f, 0.05f, 0.07f, 0.95f), 7, new Color(1f, 1f, 1f, 0.18f));
        var fFocus = Round(new Color(0.04f, 0.05f, 0.07f, 1f), 7, kAccent);
        field = Boxed(GUI.skin.textField, fNormal, fNormal, fFocus, fFocus, 7);
        field.focused.background = fFocus;
        field.alignment = TextAnchor.MiddleLeft;
        field.fontSize = 15;
        txt = new GUIStyle(field) { fontSize = 13 };
        txt.padding = new RectOffset(7, 7, 4, 4);

        label = new GUIStyle(GUI.skin.label) { fontSize = 15, wordWrap = true };
        label.normal.textColor = kInk;
        lab = new GUIStyle(label) { fontSize = 13 };
        dim = new GUIStyle(lab);
        dim.normal.textColor = kDim;
        head = new GUIStyle(lab) { fontSize = 12, fontStyle = FontStyle.Bold };
        head.normal.textColor = new Color(0.45f, 0.72f, 1f, 1f);
        title = new GUIStyle(label) { fontSize = 26, fontStyle = FontStyle.Bold };
        tog = new GUIStyle(lab) { alignment = TextAnchor.MiddleLeft };
        padText = new GUIStyle(lab) { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = false };

        panel = new GUIStyle();
        panel.normal.background = Round(new Color(0.06f, 0.07f, 0.10f, 0.90f), 14, new Color(1f, 1f, 1f, 0.08f));
        panel.border = new RectOffset(16, 16, 16, 16);
        chip = new GUIStyle(lab) { alignment = TextAnchor.MiddleLeft };
        chip.normal.background = Round(new Color(0.06f, 0.07f, 0.10f, 0.72f), 9, none);
        chip.border = new RectOffset(11, 11, 11, 11);
        chip.padding = new RectOffset(10, 10, 3, 3);
        chip.wordWrap = false;

        chk = new GUIStyle();
        chk.normal.background = Round(new Color(0.04f, 0.05f, 0.07f, 0.95f), 6, new Color(1f, 1f, 1f, 0.3f));
        chk.border = new RectOffset(8, 8, 8, 8);
        chk.margin = new RectOffset(3, 6, 4, 4);
        chkOn = new GUIStyle(chk);
        chkOn.normal.background = Round(kAccent, 6, new Color(1f, 1f, 1f, 0.5f));

        // the sliders and the scroll bar: thick enough for a finger
        var track = Round(new Color(1f, 1f, 1f, 0.16f), 4, none);
        var thumb = Round(kAccent, 10, new Color(1f, 1f, 1f, 0.5f));
        skin.horizontalSlider.normal.background = track;
        skin.horizontalSlider.border = new RectOffset(6, 6, 6, 6);
        skin.horizontalSlider.fixedHeight = 8f;
        skin.horizontalSlider.margin = new RectOffset(6, 10, 10, 10);
        skin.horizontalSliderThumb.normal.background = thumb;
        skin.horizontalSliderThumb.hover.background = thumb;
        skin.horizontalSliderThumb.active.background = thumb;
        skin.horizontalSliderThumb.border = new RectOffset(12, 12, 12, 12);
        skin.horizontalSliderThumb.fixedWidth = 22f;
        skin.horizontalSliderThumb.fixedHeight = 22f;
        skin.horizontalSliderThumb.overflow = new RectOffset(0, 0, 7, 7);
        skin.verticalScrollbar.normal.background = Round(new Color(1f, 1f, 1f, 0.07f), 5, none);
        skin.verticalScrollbar.border = new RectOffset(7, 7, 7, 7);
        skin.verticalScrollbar.fixedWidth = 12f;
        var grip = Round(new Color(1f, 1f, 1f, 0.35f), 5, none);
        skin.verticalScrollbarThumb.normal.background = grip;
        skin.verticalScrollbarThumb.hover.background = grip;
        skin.verticalScrollbarThumb.active.background = grip;
        skin.verticalScrollbarThumb.border = new RectOffset(7, 7, 7, 7);
        skin.verticalScrollbarThumb.fixedWidth = 12f;
        skin.verticalScrollbarUpButton.fixedHeight = 0f;
        skin.verticalScrollbarDownButton.fixedHeight = 0f;
        skin.scrollView.normal.background = null;
    }
}
