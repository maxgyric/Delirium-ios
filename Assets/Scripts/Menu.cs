// The menu, after the Delirium Playroom's side panel: Scene, Character, Reactions, Parameters, Interact, Inspect,
// Console, Help. Everything acts on the man you control (the one you last clicked; the camera follows him).
using System;
using System.Collections.Generic;
using System.Globalization;
using Delirium.Ragdoll;
using UnityEngine;

public partial class Game
{
    // ------------------------------------------------------------------ what the menu sets
    int sel;                       // the man you control
    bool menuOpen;
    int tab = 2;
    Vector2 scroll;
    bool god, weak, autoGetUp = true, ice, stepLimit = true, fireReact = true, headshots = true, dive;
    bool paused;
    float gunPush = 1f;
    float speed = 1f, strength = 1f, diveGap = 2f, pokeForce = 110f, blastRadius = 6f, blastSpeed = 15f;
    int extra;                     // the extra reactions' bits (DeliriumRagdolls.Reactions)
    string bodyName = "Fitted";
    // parameters
    List<DeliriumRagdolls.Message> messages;
    string paramSearch = "", openMessage = "";
    bool changedOnly;
    readonly Dictionary<string, float> paramEdits = new Dictionary<string, float>();
    readonly Dictionary<string, string> paramText = new Dictionary<string, string>();
    // console
    string consoleLine = "";
    readonly List<string> consoleLog = new List<string>();

    static readonly string[] kTabs = { "Scene", "Character", "Reactions", "Parameters", "Interact", "Inspect", "Console", "Files", "Settings", "Help" };
    static readonly string[] kBodies = { "Fitted", "Small", "Average", "Large", "Lanky", "Fat", "Stocky", "Waddler", "Guard", "Brute", "Brawler", "Bruiser", "Random" };
    static readonly string[] kReactions =
    {
        "Balance", "Drunk", "Windmill arms", "Pedal legs", "Writhe", "Roll up", "Catch a fall", "High fall", "Tumble on stairs",
        "Shot reaction", "Flinch", "Brace for impact", "Look around", "Point", "Lean", "Relax", "Stay upright", "Foetal curl",
        "Grab", "Climb over wall",
    };
    static readonly string[] kExtra =
    {
        "Windmill arms", "Curl up before getting up", "Curl up when shot at lying down", "Look at who hit him", "Tumble off a ledge",
        "smartFall when dropped", "Dodge from above", "Brace on a wall", "Arms out early when bracing",
    };
    static readonly (string label, string bone)[] kCuts =
    {
        ("Left arm", "LeftForeArm"), ("Right arm", "RightForeArm"), ("Left leg", "LeftLeg"), ("Right leg", "RightLeg"), ("Head", "Head"),
    };
    static readonly float[] kSpeeds = { 0.125f, 0.25f, 0.5f, 1f, 2f };

    Man Sel => men.Count == 0 ? null : men[Mathf.Clamp(sel, 0, men.Count - 1)];

    void SetSpeed(float s)
    {
        speed = s;
        Time.timeScale = paused ? 0f : speed;
    }

    // A man just taken by Delirium (each ragdoll is a new body): the menu's settings onto it.
    void Outfit(Man m)
    {
        var owner = m.Body.transform;
        if (extra != 0) nm.SetReactions(owner, (DeliriumRagdolls.Reactions)extra);
        if (dive) nm.SetDive(owner, diveGap);
        float k = strength * (weak ? 0.5f : 1f);
        if (Mathf.Abs(k - 1f) > 0.01f) nm.SetStrength(owner, k);
    }

    void OutfitAll()
    {
        foreach (var m in men)
        {
            if (m.Rising || !nm.State(m.Body.transform).Ragdolled) continue;
            var owner = m.Body.transform;
            nm.SetReactions(owner, (DeliriumRagdolls.Reactions)extra);
            nm.SetDive(owner, dive ? diveGap : 0f);
            nm.SetStrength(owner, strength * (weak ? 0.5f : 1f));
        }
    }

    // Stands him up at `at` (his feet) as new; Delirium takes him again on the next frame.
    void Restand(Man m, Vector3 at, float yaw)
    {
        if (held == m) Drop();
        if (nm.State(m.Body.transform).Ragdolled) nm.GetUp(m.Body.transform, 0.001f);
        m.Body.Stand(at, yaw);
        m.Health = 200f;
        m.Down = m.FireLeft = 0f;
        m.Killed = m.Rising = m.Broken = m.Cut = false;
        if (m.Hero) HeroPlace(at, yaw);
    }

    Vector3 Feet(Man m)
    {
        Vector3 p = m.Body.Hips.position;
        return new Vector3(p.x, Physics.Raycast(p, Vector3.down, out var hit, 30f) ? hit.point.y : 0f, p.z);
    }

    float Yaw(Man m)
    {
        Vector3 f = Vector3.ProjectOnPlane(m.Body.Hips.rotation * Vector3.forward, Vector3.up);
        return f.magnitude < 0.2f ? m.Body.transform.eulerAngles.y : Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
    }

    // Unity's point or direction as NM's text (Z up: y and z swap), for a message line.
    static string V(Vector3 u) => string.Format(CultureInfo.InvariantCulture, "{0:0.###},{1:0.###},{2:0.###}", u.x, u.z, u.y);

    void Say(string text)
    {
        consoleLog.Add(text);
        if (consoleLog.Count > 10) consoleLog.RemoveAt(0);
    }

    bool Send(Man m, string line)
    {
        bool ok = nm.React(m.Body.transform, line);
        Say((ok ? "> " : "refused: ") + line);
        return ok;
    }

    // Who a reaction goes to: the man you control; as the hero, everyone but him.
    List<Man> Targets(Man m)
    {
        var list = new List<Man>();
        if (!heroOn)
        {
            if (!m.Rising) list.Add(m);
            return list;
        }
        foreach (var x in men)
            if (!x.Hero && !x.Rising) list.Add(x);
        return list;
    }

    void React(Man m, string reaction)
    {
        var body = m.Body;
        Vector3 fwd = Vector3.ProjectOnPlane(body.Hips.rotation * Vector3.forward, Vector3.up).normalized;
        if (fwd.magnitude < 0.5f) fwd = body.transform.forward;
        Vector3 left = -Vector3.Cross(Vector3.up, fwd);
        Vector3 head = body.Bone("Head").position, chest = body.Bone("Spine2").position, hips = body.Hips.position;
        switch (reaction)
        {
            case "Balance": Send(m, "bodyBalance start=true"); break;
            case "Drunk":
                Send(m, "bodyBalance start=true");
                Send(m, "leanInDirection start=true leanAmount=0.12 dir=" + V(fwd));
                break;
            case "Windmill arms":
                Send(m, "bodyBalance start=true");
                Send(m, "armsWindmillAdaptive start=true");
                break;
            case "Pedal legs": Send(m, "pedalLegs start=true"); break;
            case "Writhe": Send(m, "bodyWrithe start=true"); break;
            case "Roll up": Send(m, "bodyRollUp start=true"); break;
            case "Catch a fall": Send(m, "catchFall start=true"); break;
            case "High fall": Send(m, "highFall start=true"); break;
            case "Tumble on stairs": Send(m, "rollDownStairs start=true"); break;
            case "Shot reaction":
                nm.Shoot(m.Body.transform, chest, -fwd, 40f, "POWERFUL_WEAPON", god ? 200f : m.Health);
                Say("> shot at the chest");
                break;
            case "Flinch": Send(m, "upperBodyFlinch start=true pos=" + V(head + fwd * 2f)); break;
            case "Brace for impact": Send(m, "braceForImpact start=true pos=" + V(chest + fwd) + " look=" + V(chest + fwd)); break;
            case "Look around": Send(m, "headLook start=true alwaysLook=true pos=" + V(head + fwd * 2f + left)); break;
            case "Point": Send(m, "pointArm start=true target=" + V(chest + fwd * 2f - left)); break;
            case "Lean":
                Send(m, "bodyBalance start=true");
                Send(m, "leanInDirection start=true leanAmount=0.3 dir=" + V(fwd));
                break;
            case "Relax": Send(m, "bodyRelax start=true"); break;
            case "Stay upright": Send(m, "stayUpright start=true"); break;
            case "Foetal curl": Send(m, "bodyFoetal start=true"); break;
            case "Grab":
                {
                    Vector3 at = chest + fwd * 0.45f;
                    Send(m, "grab start=true linegrab=true useLeft=true useRight=true dontLetGo=true pos1=" + V(at + left * 0.5f) + " pos2=" + V(at - left * 0.5f));
                }
                break;
            case "Climb over wall":
                {
                    Vector3 over = hips - fwd * 0.3f;
                    over.y = Feet(m).y + 0.9f;
                    Send(m, "fallOverWall start=true fallOverPoint=" + V(over) + " fallOverEdge=" + V(left));
                }
                break;
            case "STOP all": Send(m, "stopAllBehaviours"); break;
        }
    }

    // ------------------------------------------------------------------ the panel
    GUIStyle btn, btnOn, lab, dim, head, tog, txt;

    void MenuStyles()
    {
        BuildSkin(); // Ui.cs
    }

    // Buttons in rows of `per`; the one pressed (-1: none). `lit` marks one as chosen.
    int Grid(string[] labels, int per, int lit = -1, float height = 34f)
    {
        int hit = -1;
        for (int i = 0; i < labels.Length; i += per)
        {
            GUILayout.BeginHorizontal();
            for (int j = i; j < i + per; j++)
            {
                if (j >= labels.Length) { GUILayout.Label("", GUILayout.Width((kPanel - 40f) / per - 4f)); continue; }
                if (GUILayout.Button(labels[j], j == lit ? btnOn : btn, GUILayout.Height(height), GUILayout.Width((kPanel - 40f) / per - 4f))) hit = j;
            }
            GUILayout.EndHorizontal();
        }
        return hit;
    }

    float Slide(string label, float v, float min, float max, string unit = "")
    {
        GUILayout.Label(label + ": " + v.ToString("0.##", CultureInfo.InvariantCulture) + unit, lab);
        return GUILayout.HorizontalSlider(v, min, max, GUILayout.Height(22f));
    }

    // A switch: a box (lit when on) and its words; a touch on either flips it.
    bool Tick(bool on, string label)
    {
        GUILayout.BeginHorizontal();
        bool hit = GUILayout.Button(GUIContent.none, on ? chkOn : chk, GUILayout.Width(24f), GUILayout.Height(24f));
        hit |= GUILayout.Button(label, tog, GUILayout.MinHeight(30f));
        GUILayout.EndHorizontal();
        return hit ? !on : on;
    }
    void Head(string t) { GUILayout.Space(12f); GUILayout.Label(t.ToUpperInvariant(), head); }
    void Info(string t) { GUILayout.Label(t, dim); }

    const float kPanel = 360f;

    void MenuGui(float w, float h)
    {
        MenuStyles();
        var area = new Rect(w - kPanel - 8f, 60f, kPanel, h - 60f - (heroOn ? 12f : 68f));
        if (area.x < 8f) area.x = 8f; // a narrow screen: over the left edge rather than off the right
        uiRects.Add(area);
        GUI.Box(area, GUIContent.none, panel);
        // a finger dragged up or down the panel scrolls it (a sideways drag is a slider's)
        var e = Event.current;
        if (e.type == EventType.MouseDrag && area.Contains(e.mousePosition) && Mathf.Abs(e.delta.y) > Mathf.Abs(e.delta.x) * 1.5f)
            scroll.y = Mathf.Max(0f, scroll.y - e.delta.y);
        GUILayout.BeginArea(new Rect(area.x + 10f, area.y + 10f, area.width - 20f, area.height - 20f));
        int t = Grid(kTabs, 3, tab, 30f);
        if (t >= 0 && t != tab) { tab = t; scroll = Vector2.zero; }
        // no sideways scroll bar: the panel's content is made to fit its width
        scroll = GUILayout.BeginScrollView(scroll, false, true, GUIStyle.none, GUI.skin.verticalScrollbar);
        var m = Sel;
        if (m != null)
            switch (tab)
            {
                case 0: SceneTab(m); break;
                case 1: CharacterTab(m); break;
                case 2: ReactionsTab(m); break;
                case 3: ParametersTab(); break;
                case 4: InteractTab(m); break;
                case 5: InspectTab(m); break;
                case 6: ConsoleTab(m); break;
                case 7: FilesTab(); break;
                case 8: SettingsTab(); break;
                default: HelpTab(); break;
            }
        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    void OpenSettings()
    {
        bool there = menuOpen && tab == 8;
        menuOpen = !there;
        tab = 8;
        scroll = Vector2.zero;
    }

    void SceneTab(Man m)
    {
        Head("Time");
        GUILayout.BeginHorizontal();
        if (GUILayout.Button(paused ? "Resume" : "Pause", btn, GUILayout.Height(34f))) { paused = !paused; SetSpeed(speed); }
        int k = Array.IndexOf(kSpeeds, speed);
        if (GUILayout.Button("Slower", btn, GUILayout.Height(34f)) && k > 0) SetSpeed(kSpeeds[k - 1]);
        if (GUILayout.Button("1x", btn, GUILayout.Height(34f))) SetSpeed(1f);
        if (GUILayout.Button("Faster", btn, GUILayout.Height(34f)) && k >= 0 && k < kSpeeds.Length - 1) SetSpeed(kSpeeds[k + 1]);
        GUILayout.EndHorizontal();
        Info(speed + "x  " + (paused ? "paused" : speed == 1f ? "real time" : speed < 1f ? "slow motion" : "fast forward"));

        Head("Surface");
        if (GUILayout.Button("Ice floor: " + (ice ? "On" : "Off"), ice ? btnOn : btn, GUILayout.Height(34f)))
        {
            ice = !ice;
            // the colliders' material reaches Delirium when it reads the level: everyone is taken anew where he stands
            var slick = ice ? new PhysicsMaterial("Ice") { dynamicFriction = 0.03f, staticFriction = 0.03f, bounciness = 0f } : null;
            foreach (var c in level) c.sharedMaterial = slick;
            DeliriumRagdolls.SandboxFloorSurface = ice && level.Count > 0 ? level[0] : null; // the floor under the level too
            foreach (var x in men) Restand(x, Feet(x), Yaw(x));
        }

        Head("People");
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Add one", btn, GUILayout.Height(34f))) AddMan();
        if (GUILayout.Button("Add 4", btn, GUILayout.Height(34f))) for (int i = 0; i < 4; i++) AddMan();
        if (GUILayout.Button("Remove others", btn, GUILayout.Height(34f)))
        {
            Drop();
            var keep = m;
            foreach (var x in men)
                if (x != keep && !x.Hero) Destroy(x.Body.gameObject);
            men.Clear();
            men.Add(keep);
            if (hero != null && hero != keep) men.Add(hero);
            sel = 0;
        }
        GUILayout.EndHorizontal();
        Info(men.Count + " of " + kMaxMen + ". Each costs a physics step.");
        if (GUILayout.Button("Reset everyone", btn, GUILayout.Height(34f))) ResetAll();
    }

    void CharacterTab(Man m)
    {
        Head(m.Body.name + (men.Count > 1 && !heroOn ? "  (click another to control him)" : ""));
        if (GUILayout.Button("Play as Hero: " + (heroOn ? "On" : "Off"), heroOn ? btnOn : btn, GUILayout.Height(36f)))
        {
            if (heroOn) RemoveHero(); else AddHero();
            GUIUtility.ExitGUI();
        }
        GUILayout.BeginHorizontal();
        if (!heroOn && GUILayout.Button("Next", btn, GUILayout.Height(34f))) sel = (sel + 1) % men.Count;
        if (GUILayout.Button("Reset here", btn, GUILayout.Height(34f))) Restand(m, Feet(m), Yaw(m));
        if (GUILayout.Button("Reset pose", btn, GUILayout.Height(34f))) Restand(m, m.Home, m.Yaw);
        GUILayout.EndHorizontal();

        Head("Muscles");
        float s = Slide("Strength", strength, 0.25f, 3f, "x");
        if (Mathf.Abs(s - strength) > 0.001f) { strength = s; OutfitAll(); }

        Head("Moves");
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Jump", btn, GUILayout.Height(34f))) nm.Jump(m.Body.transform, false);
        if (GUILayout.Button("Dive", btn, GUILayout.Height(34f))) nm.Jump(m.Body.transform, true);
        if (GUILayout.Button("Tase", btn, GUILayout.Height(34f))) nm.Tase(m.Body.transform, 2.5f);
        GUILayout.EndHorizontal();

        Head("Sever");
        var cuts = new string[kCuts.Length];
        for (int i = 0; i < cuts.Length; i++) cuts[i] = kCuts[i].label;
        int c = Grid(cuts, 3);
        if (c >= 0 && !m.Rising)
        {
            var bone = m.Body.Bone(kCuts[c].bone);
            Vector3 at = bone.position;
            if (nm.Sever(m.Body.transform, at, Vector3.up * 20f))
            {
                m.Body.Hide(kCuts[c].bone);
                m.Cut = true;
            }
        }
        Info("A cut man doesn't get up by himself. Reset puts him back together.");

        Head("Automatic");
        autoGetUp = Tick(autoGetUp, "Get up after a fall");
        bool gd = Tick(god, "Unlimited health");
        if (gd != god)
        {
            god = gd;
            Delirium.Native.nm_set_headshot_kills(headshots && !god ? 1 : 0);
        }
        bool wk = Tick(weak, "Weaken muscles");
        if (wk != weak) { weak = wk; OutfitAll(); }
        bool hs = Tick(headshots, "A head shot kills");
        if (hs != headshots) { headshots = hs; Delirium.Native.nm_set_headshot_kills(hs && !god ? 1 : 0); }
    }

    void ReactionsTab(Man m)
    {
        Head("Reactions");
        Info("Start a reaction, then poke, shoot or pull him to see how he responds.");
        int r = Grid(kReactions, 3, -1, 40f);
        if (r >= 0)
            foreach (var x in Targets(m)) React(x, kReactions[r]);
        if (GUILayout.Button("STOP all", btn, GUILayout.Height(36f)))
            foreach (var x in Targets(m)) React(x, "STOP all");
        if (heroOn) Info("As the hero: the reactions are everyone else's, not his.");

        Head("Rules");
        bool sl = Tick(stepLimit, "Step limit (as the game: a stumble ends in a fall)");
        if (sl != stepLimit)
        {
            stepLimit = sl;
            Delirium.Native.nm_set_step_limit(sl ? 1 : 0);
            // the balance in play was started under the old rule: started again
            foreach (var x in men)
            {
                var xs = nm.State(x.Body.transform);
                if (!x.Rising && xs.Ragdolled && xs.Balancing && !xs.Fallen) nm.React(x.Body.transform, "bodyBalance start=true");
            }
        }
        Info("Step limit: push him and count his steps. On, he gives up and falls after a few; off, he keeps stepping.");
        bool fr = Tick(fireReact, "Fire reaction");
        if (fr != fireReact) { fireReact = fr; Delirium.Native.nm_set_fire_reaction(fr ? 1 : 0); }
        bool dv = Tick(dive, "Dive when shot at");
        float gap = diveGap;
        if (dive) gap = Slide("Dive if shot at again within", diveGap, 0.5f, 10f, " s");
        if (dv != dive || Mathf.Abs(gap - diveGap) > 0.001f) { dive = dv; diveGap = gap; OutfitAll(); }
        Info("Dive: shoot past a standing man (a near miss), then again within the time: he dives out of the line.");

        Head("Additional experimental reactions");
        int was = extra;
        for (int i = 0; i < kExtra.Length; i++)
        {
            bool on = Tick((extra & (1 << i)) != 0, kExtra[i]);
            extra = on ? extra | (1 << i) : extra & ~(1 << i);
        }
        if (extra != was) OutfitAll();
        Info("They happen by chance and only in their moment: windmill as he loses his balance, the curls once he's down, " +
             "the look after a hit (as the hero), the ledge and wall ones at a ledge or a wall, smartFall on Drop him.");
    }

    void ParametersTab()
    {
        Head("Parameters");
        Info("Every behaviour's numbers. A change holds for everyone, for every message from now on.");
        if (messages == null)
        {
            try { messages = DeliriumRagdolls.Messages(); }
            catch (Exception e) { messages = new List<DeliriumRagdolls.Message>(); Debug.LogWarning("Parameters: " + e.Message); }
        }
        GUILayout.BeginHorizontal();
        GUILayout.Label("Search", lab, GUILayout.Width(52f));
        paramSearch = GUILayout.TextField(paramSearch, txt, GUILayout.Height(26f));
        GUILayout.EndHorizontal();
        changedOnly = Tick(changedOnly, "Changed only (" + paramEdits.Count + ")");
        if (paramEdits.Count > 0 && GUILayout.Button("Reset all parameters", btn, GUILayout.Height(30f)))
        {
            paramEdits.Clear();
            paramText.Clear();
            foreach (var x in men) nm.ClearOverrides(x.Body.transform);
        }
        string find = paramSearch.Trim().ToLowerInvariant();
        foreach (var msg in messages)
        {
            bool nameHit = find == "" || msg.Name.ToLowerInvariant().Contains(find);
            bool any = false;
            foreach (var p in msg.Params)
            {
                if (p.kind > 2) continue;
                bool changed = paramEdits.ContainsKey(msg.Name + "." + p.name);
                if ((nameHit || p.name.ToLowerInvariant().Contains(find)) && (!changedOnly || changed)) { any = true; break; }
            }
            if (!any) continue;
            bool open = openMessage == msg.Name || (find != "" && !nameHit) || changedOnly;
            if (GUILayout.Button((open ? "- " : "+ ") + msg.Name, open ? btnOn : btn, GUILayout.Height(28f)))
                openMessage = openMessage == msg.Name ? "" : msg.Name;
            if (!open) continue;
            foreach (var p in msg.Params)
            {
                if (p.kind > 2) continue; // vectors and names: the Console
                string key = msg.Name + "." + p.name;
                bool changed = paramEdits.TryGetValue(key, out float now);
                if (changedOnly && !changed) continue;
                if (!nameHit && !p.name.ToLowerInvariant().Contains(find)) continue;
                float def = p.def is float ? (float)p.def : 0f;
                if (!changed) now = def;
                GUILayout.BeginHorizontal();
                if (p.kind == 2)
                {
                    bool on = GUILayout.Toggle(now != 0f, " " + p.name + (changed ? " *" : ""), tog, GUILayout.Height(24f));
                    if (on != (now != 0f)) SetParam(msg.Name, p.name, on ? 1f : 0f);
                }
                else
                {
                    GUILayout.Label(p.name + (changed ? " *" : ""), lab, GUILayout.Width(170f));
                    if (!paramText.TryGetValue(key, out string text)) text = now.ToString("0.####", CultureInfo.InvariantCulture);
                    string edited = GUILayout.TextField(text, txt, GUILayout.Width(70f), GUILayout.Height(24f));
                    if (edited != text) paramText[key] = edited;
                    if (GUILayout.Button("Set", btn, GUILayout.Width(44f), GUILayout.Height(24f)) &&
                        float.TryParse(edited.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                    {
                        SetParam(msg.Name, p.name, p.kind == 1 ? Mathf.Round(v) : v);
                        paramText.Remove(key);
                    }
                }
                GUILayout.EndHorizontal();
            }
        }
    }

    void SetParam(string message, string param, float value)
    {
        paramEdits[message + "." + param] = value;
        foreach (var x in men) nm.SetOverride(x.Body.transform, message, param, value);
    }

    void InteractTab(Man m)
    {
        Head("Click");
        int t = Grid(kToolNames, 3, (int)tool);
        if (t >= 0) tool = (Tool)t;
        pokeForce = Slide("Push strength", pokeForce, 10f, 300f, " N s");

        Head("Weapon");
        int wpn = Grid(weaponLabels, 3, weapon % weapons.Length);
        if (wpn >= 0) { weapon = wpn; tool = Tool.Shoot; }
        if (heroOn) Info("The hero holds it: Aim, then Fire. Fists: Punch.");

        float gp = Slide("Gun push", gunPush, 0.2f, 3f, "x");
        if (Mathf.Abs(gp - gunPush) > 0.01f) { gunPush = gp; LoadGuns(); }

        Head("Blast");
        blastRadius = Slide("Radius", blastRadius, 2f, 12f, " m");
        blastSpeed = Slide("Throw", blastSpeed, 4f, 40f, " m/s");

        Head("Impact tests");
        var owner = m.Body.transform;
        Vector3 fwd = Vector3.ProjectOnPlane(m.Body.Hips.rotation * Vector3.forward, Vector3.up).normalized;
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Push from front", btn, GUILayout.Height(36f)) && !m.Rising) nm.Ragdoll(owner, -fwd * pokeForce);
        if (GUILayout.Button("Push from behind", btn, GUILayout.Height(36f)) && !m.Rising) nm.Ragdoll(owner, fwd * pokeForce);
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Drop him (3 m)", btn, GUILayout.Height(36f))) Lift(m, 3f, false);
        if (GUILayout.Button("High drop (12 m)", btn, GUILayout.Height(36f))) Lift(m, 12f, true);
        GUILayout.EndHorizontal();
        if (GUILayout.Button("Explosion at his feet", btn, GUILayout.Height(36f))) Blast(Feet(m) + fwd * 1.2f + Vector3.up * 0.2f);
    }

    void Lift(Man m, float height, bool high)
    {
        Restand(m, Feet(m) + Vector3.up * height, Yaw(m));
        if (m.Hero) { heroGround = false; heroAir = 0f; heroJumpStill = false; return; } // he falls on his own
        var owner = m.Body.transform;
        if (nm.Ragdoll(owner, Vector3.zero))
        {
            Outfit(m);
            if ((extra & 32) != 0) nm.React(owner, "smartFall start=true"); // "smartFall when dropped"
            else if (high) nm.HighFall(owner, false);
        }
    }

    void InspectTab(Man m)
    {
        Head("Inspect: " + m.Body.name);
        var st = nm.State(m.Body.transform);
        if (!st.Ragdolled)
        {
            Info(m.Rising ? "Getting up (not on Delirium for a moment)." : "Not on Delirium.");
            return;
        }
        GUILayout.Label(
            "Health: " + (god ? "unlimited" : Mathf.RoundToInt(m.Health) + " / 200") +
            "\nBalancing: " + Yes(st.Balancing) + "    Fallen: " + Yes(st.Fallen) +
            "\nCatching fall: " + Yes(st.CatchingFall) + "    Rolled up: " + Yes(st.RolledUp) +
            "\nRelaxing: " + Yes(st.Relaxing) + "    Dead: " + Yes(st.Dead) +
            "\nBurning: " + Yes(st.Burning) + "    Tased: " + Yes(st.Tased) +
            "\nJumping: " + Yes(st.Jumping) + "    Task running: " + Yes(st.TaskRunning) +
            "\nSettled: " + Yes(st.Settled) + "    Resting: " + Yes(st.Resting) +
            "\nLying still: " + st.LyingFor.ToString("0.0") + " s" +
            "\nSpeed: " + st.Velocity.magnitude.ToString("0.00") + " m/s" +
            "\nMass: " + st.Mass.ToString("0.0") + " kg" +
            "\nCentre: " + st.Centre.ToString("0.00"), lab);
        if (st.Contacts != null)
        {
            int touching = 0;
            foreach (int c in st.Contacts) touching += c != 0 ? 1 : 0;
            GUILayout.Label("Parts touching something: " + touching + " of " + st.Contacts.Length, lab);
        }
    }

    static string Yes(bool b) => b ? "yes" : "no";

    void ConsoleTab(Man m)
    {
        Head("Console");
        Info("A message to " + m.Body.name + ", as the Playroom's console: its name, then name=value. Vectors are x,y,z in Delirium's space (Z up).");
        consoleLine = GUILayout.TextField(consoleLine, txt, GUILayout.Height(28f));
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Send", btn, GUILayout.Height(34f)) && consoleLine.Trim() != "")
            foreach (var x in Targets(m)) Send(x, consoleLine.Trim());
        if (GUILayout.Button("Clear", btn, GUILayout.Height(34f))) consoleLine = "";
        GUILayout.EndHorizontal();
        Info("Try:  bodyRollUp start=true   ·   bodyWrithe start=true armStiffness=12   ·   stopAllBehaviours");
        Head("Activity");
        for (int i = consoleLog.Count - 1; i >= 0; i--) GUILayout.Label(consoleLog[i], dim);
    }

    void HelpTab()
    {
        Head("Help");
        GUILayout.Label(
            "Click a man: the bottom bar's tool is used on him, and he becomes the one you control.\n\n" +
            "Push, Shoot, Punch, Kill: where you click him.\nBlast: on him or on the ground.\nDrag: hold him and move.\nFire: on, and again to put it out.\n\n" +
            "Empty space: drag to turn the camera (or the right mouse button). Wheel or pinch: zoom.\n\n" +
            "Reactions start a behaviour by hand; Parameters changes their numbers; Console sends any message.\n\n" +
            "Getting up is a plain blend for now: GTA IV's own get-up comes with Hero.", lab);
    }
}
