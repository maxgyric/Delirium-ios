// The Quest (VR): the sandbox from inside. Your head is the camera; your hands are bodies of their own that follow
// your controllers and meet the world and the people (BONELAB's way): they can't go through a wall, they shove,
// slap and punch (Delirium feels them as moving boxes; a fast one hits like a punch), and the grip takes hold of a
// person (a leash from his nearest part to your hand) till you let go.
//
// You walk as the hero (his body hidden: you're inside it): the left stick moves him as on the phone (the way you
// look is forward; click it to run), A jumps, the right stick turns you in steps, B is "Drunk": he goes to his
// ragdoll and you see him from behind and a little above, steering him with the left stick, till B again. The
// right trigger uses a map's button or door in reach. Y opens the menu (maps, characters, people), pointed at with
// the right hand and picked with its trigger.
//
// Only when a headset runs (XRSettings.isDeviceActive: the Quest build, ci/android): the phone game is unchanged.
using System;
using System.Collections.Generic;
using System.IO;
using Delirium.Ragdoll;
using UnityEngine;
using UnityEngine.XR;

public partial class Game
{
    bool vrOn;
    Transform vrRig, vrLTarget, vrRTarget, vrMenu, vrRay;
    VrHand vrL, vrR;
    bool vrThird;
    float vrTpYaw, vrSnapReady = 1f;
    bool vrHeroAsked, vrTurning;
    Vector3 vrHeadWas;
    bool vrHeadWasSet;
    // the buttons' last state (a press is when one goes down)
    bool vrA, vrB, vrY, vrTrigR, vrTrigL;
    string vrPage = "main";
    int vrListAt;
    const int kUiLayer = 31;

    static bool XrActive()
    {
        try { return XRSettings.isDeviceActive; } catch (Exception) { return false; }
    }

    // (Game.Update, before anything else, running or not: the launcher's error shows in the headset)
    void VrUpdate()
    {
        if (!vrOn)
        {
            if (!XrActive()) return;
            VrSetup();
        }
        if (cam == null) return;
        VrHead();
        var L = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
        var R = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        VrPose(L, vrLTarget);
        VrPose(R, vrRTarget);
        L.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 ls);
        R.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 rs);
        bool a = Btn(R, CommonUsages.primaryButton), b = Btn(R, CommonUsages.secondaryButton), y = Btn(L, CommonUsages.secondaryButton);
        L.TryGetFeatureValue(CommonUsages.trigger, out float tl);
        R.TryGetFeatureValue(CommonUsages.trigger, out float tr);
        L.TryGetFeatureValue(CommonUsages.grip, out float gl);
        R.TryGetFeatureValue(CommonUsages.grip, out float gr);
        bool trigR = tr > 0.6f, trigL = tl > 0.6f;
        bool aDown = a && !vrA, bDown = b && !vrB, yDown = y && !vrY, trigRDown = trigR && !vrTrigR;
        (vrA, vrB, vrY, vrTrigR, vrTrigL) = (a, b, y, trigR, trigL);

        // the menu: Y; the right hand points, its trigger picks
        if (yDown) VrMenuToggle();
        bool menuUp = vrMenu != null && vrMenu.gameObject.activeSelf;
        VrButton aimed = menuUp ? VrAimed() : null;
        if (menuUp && trigRDown && aimed != null) { aimed.Click?.Invoke(); trigRDown = false; }
        VrMenuLabels();

        // turning in steps (the right stick), round your head
        if (Mathf.Abs(rs.x) < 0.3f) vrSnapReady = 1f;
        else if (vrSnapReady > 0f && Mathf.Abs(rs.x) > 0.7f)
        {
            vrRig.RotateAround(cam.transform.position, Vector3.up, Mathf.Sign(rs.x) * 30f);
            if (vrThird) vrTpYaw += Mathf.Sign(rs.x) * 30f;
            vrSnapReady = 0f;
        }

        // the hands' grips (a person, or the wall: climbing) and fingers
        if (vrL != null) { vrL.Grip(gl > 0.55f); vrL.FingersPose(tl, gl, VrThumbDown(L)); }
        if (vrR != null) { vrR.Grip(gr > 0.55f); vrR.FingersPose(tr, gr, VrThumbDown(R)); }
        // outside his body (a ragdoll seen from behind): no hands
        bool park = vrThird && heroOn;
        if (vrL != null) vrL.Park(park);
        if (vrR != null) vrR.Park(park);
        VrClimb(Time.deltaTime);

        if (!running || nm == null) return;
        if (!heroOn && !vrHeroAsked && GtaAnim.State == 2) { vrHeroAsked = true; AddHero(); }
        if (!heroOn)
        {
            // no hero (GTA IV's clips aren't there): walking about freely
            VrFreeWalk(ls, Time.deltaTime);
            return;
        }
        VrHandsLook();
        // the hero's controls (HeroInput reads these when vrOn)
        hStick = ls.magnitude > 0.15f ? ls : Vector2.zero;
        hCamYaw = cam.transform.eulerAngles.y;
        heroSprint = Btn(L, CommonUsages.primary2DAxisClick) || (heroSprint && ls.magnitude > 0.5f);
        if (aDown) hJumpReq = true;
        if (bDown) VrDrunk();
        if (trigRDown && useTarget != null) UseNow();
        // standing, he turns to face where you look (when you've looked well round)
        if (!vrThird && heroMode == 0 && hStick == Vector2.zero && heroVel.magnitude < 0.3f)
        {
            float off = Mathf.DeltaAngle(heroYaw, hCamYaw);
            if (Mathf.Abs(off) > 45f) vrTurning = true;
            if (vrTurning)
            {
                heroYaw = Mathf.MoveTowardsAngle(heroYaw, hCamYaw, 300f * Time.deltaTime);
                if (Mathf.Abs(Mathf.DeltaAngle(heroYaw, hCamYaw)) < 3f) vrTurning = false;
            }
        }
        else vrTurning = false;
        // your head walked (room scale): he walks with it (into nothing he couldn't walk into)
        if (!vrThird && heroMode == 0 && heroCC != null && heroCC.enabled && hero != null && !hero.Rising)
        {
            Vector3 d = cam.transform.position - heroPos;
            d.y = 0f;
            // (every frame, however little: a slow step in your room is a millimetre or two a frame)
            if (d.sqrMagnitude > 1e-8f && d.sqrMagnitude < 1f)
            {
                Vector3 was = heroCC.transform.position;
                heroCC.Move(d);
                heroPos += heroCC.transform.position - was;
            }
        }
    }

    static bool Btn(InputDevice d, InputFeatureUsage<bool> u) => d.isValid && d.TryGetFeatureValue(u, out bool v) && v;

    // a thumb on a button or the stick (the thumb curls in)
    static bool VrThumbDown(InputDevice d) => Btn(d, CommonUsages.primaryTouch) || Btn(d, CommonUsages.secondaryTouch) || Btn(d, CommonUsages.primary2DAxisTouch)
        || Btn(d, CommonUsages.primaryButton) || Btn(d, CommonUsages.secondaryButton);

    // B: drunk; again: back in your own eyes at once, lying or not (no getting up first)
    void VrDrunk()
    {
        if (hero == null || nm == null || riding || heroNoclip) { ToggleDrunk(); return; }
        var st = nm.State(hero.Body.transform);
        bool down = heroMode != 0 && (st.Ragdolled || hero.Rising);
        if (!down || hero.Killed) { ToggleDrunk(); return; }
        heroDrunk = false;
        heroDrunkPending = false;
        HeroOverrides(false);
        hero.Rising = false;
        // his feet: the ground under his hips (not his own body's parts)
        Vector3 hp = hero.Body.Hips.position;
        float y = float.MinValue;
        foreach (var h in Physics.RaycastAll(hp + Vector3.up * 0.3f, Vector3.down, 6f, VrWorldMask(), QueryTriggerInteraction.Ignore))
        {
            var rb = h.collider.attachedRigidbody;
            if (rb != null && (!rb.isKinematic || rb.GetComponent<VrHand>() != null || h.collider.transform.root.name.Contains("ragdoll"))) continue;
            y = Mathf.Max(y, h.point.y);
        }
        if (y == float.MinValue) y = hp.y - 0.1f;
        HeroTakeBack(Vector3.zero);
        heroPos = new Vector3(hp.x, y, hp.z);
        heroYaw = cam != null ? cam.transform.eulerAngles.y : heroYaw;
        PlaceMover();
    }

    // the controller's place (its own tracking space: the rig's): where your hand holds it (the grip's), turned the
    // way it points (the aim's: out of its front, by the trigger; the grip's own runs up through the ring)
    static readonly InputFeatureUsage<Quaternion> kAimRot = new InputFeatureUsage<Quaternion>("PointerRotation");
    static bool vrPoseSaid;

    static void VrPose(InputDevice d, Transform t)
    {
        if (t == null || !d.isValid) return;
        if (d.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 p)) t.localPosition = p;
        bool grip = d.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion q);
        bool aim = d.TryGetFeatureValue(kAimRot, out Quaternion a) && Mathf.Abs(a.x) + Mathf.Abs(a.y) + Mathf.Abs(a.z) + Mathf.Abs(a.w) > 0.5f;
        if (!vrPoseSaid && grip)
        {
            vrPoseSaid = true;
            var us = new List<InputFeatureUsage>();
            d.TryGetFeatureUsages(us);
            var names = new List<string>();
            foreach (var u in us) names.Add(u.name);
            Debug.Log("[Sandbox] VR controller " + d.name + ": " + string.Join(", ", names) + (aim ? " | aim vs grip: " + Quaternion.Angle(a, q).ToString("0") + " deg" : " | no aim pose: the grip's turned 55 deg"));
        }
        if (aim) t.localRotation = a;
        else if (grip) t.localRotation = q * Quaternion.Euler(55f, 0f, 0f);
    }

    void VrHead()
    {
        var h = InputDevices.GetDeviceAtXRNode(XRNode.CenterEye);
        if (!h.isValid) h = InputDevices.GetDeviceAtXRNode(XRNode.Head);
        if (!h.isValid) return;
        if (h.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 p)) cam.transform.localPosition = p;
        if (h.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion q)) cam.transform.localRotation = q;
    }

    void VrSetup()
    {
        vrOn = true;
        if (cam == null) cam = Camera.main;
        if (cam == null) return;
        Debug.Log("[Sandbox] VR: " + XRSettings.loadedDeviceName + ", " + XRSettings.eyeTextureWidth + "x" + XRSettings.eyeTextureHeight);
        // the floor is the play space's origin (your real height is your head's)
        var subs = new List<XRInputSubsystem>();
        SubsystemManager.GetSubsystems(subs);
        foreach (var s in subs) s.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor);
        var rig = new GameObject("VR rig");
        DontDestroyOnLoad(rig);
        vrRig = rig.transform;
        vrRig.position = new Vector3(cam.transform.position.x, 0f, cam.transform.position.z);
        cam.transform.SetParent(vrRig, false);
        cam.transform.localPosition = new Vector3(0f, 1.6f, 0f);
        cam.nearClipPlane = 0.03f;
        Application.onBeforeRender += VrHead;
        // the phone's view-scaling texture and frame cap: the headset has its own
        renderScale = 1f;
        View();
        Application.targetFrameRate = -1;
        QualitySettings.vSyncCount = 0;
        // (the Quest's GPU: no real-time shadows)
        shadowDistance = 0f;
        QualitySettings.shadowDistance = 0f;
        vrLTarget = new GameObject("Left controller").transform;
        vrRTarget = new GameObject("Right controller").transform;
        vrLTarget.SetParent(vrRig, false);
        vrRTarget.SetParent(vrRig, false);
        vrL = VrHand.Make(this, vrLTarget, true);
        vrR = VrHand.Make(this, vrRTarget, false);
        rig.AddComponent<VrEarly>().G = this;
        rig.AddComponent<VrLate>().G = this;
        // the pointer (shown with the menu)
        var ray = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Destroy(ray.GetComponent<Collider>());
        ray.name = "Pointer";
        ray.transform.SetParent(vrRTarget, false);
        ray.transform.localScale = new Vector3(0.004f, 0.004f, 2f);
        ray.transform.localPosition = new Vector3(0f, 0f, 1f);
        ray.GetComponent<Renderer>().sharedMaterial = Mat(new Color(1f, 0.9f, 0.4f));
        ray.SetActive(false);
        vrRay = ray.transform;
        // Delirium needs your GTA IV files: straight in (there's no launcher in a headset; its errors show in the menu)
        if (!running && !string.IsNullOrEmpty(gameExe)) Launch();
        if (!running) VrMenuToggle();
    }

    // (LateUpdate, in place of HeroCamera: the rig over him, or behind him when he's a ragdoll)
    void VrCamera()
    {
        if (vrRig == null || cam == null || hero == null) return;
        bool third = heroDrunk || heroMode != 0 || riding || (nm != null && nm.State(hero.Body.transform).Ragdolled);
        if (third != vrThird)
        {
            vrThird = third;
            vrTpYaw = cam.transform.eulerAngles.y;
            if (!third)
            {
                // back inside him at once
                Vector3 o = heroPos - cam.transform.position;
                vrRig.position += new Vector3(o.x, 0f, o.z);
            }
        }
        // his body: shown (in first person without his head and hands: VRBody.cs), not while you fly
        bool shown = vrThird || !heroNoclip;
        foreach (var r in hero.Body.GetComponentsInChildren<Renderer>()) if (r.enabled != shown) r.enabled = shown;
        if (gunModel != null && gunModel.activeSelf == !vrThird) gunModel.SetActive(vrThird);
        if (!vrThird)
        {
            Vector3 o = heroPos - cam.transform.position;
            vrRig.position = new Vector3(vrRig.position.x + o.x, heroPos.y, vrRig.position.z + o.z);
            return;
        }
        // behind him and above, the way you faced when he went (turned by the right stick)
        Vector3 at = hero.Body.Hips.position;
        Vector3 back = Quaternion.Euler(0f, vrTpYaw, 0f) * Vector3.back;
        Vector3 want = at + back * 2.6f + Vector3.up * 1.1f;
        Vector3 move = want - cam.transform.position;
        vrRig.position += move * (1f - Mathf.Exp(-4f * Time.deltaTime));
    }

    // no hero: the left stick moves you over the ground
    void VrFreeWalk(Vector2 ls, float dt)
    {
        if (ls.magnitude < 0.15f || cam == null) return;
        Vector3 f = cam.transform.forward;
        f.y = 0f;
        f.Normalize();
        Vector3 r = Vector3.Cross(Vector3.up, f);
        Vector3 step = (f * ls.y + r * ls.x) * 2.5f * dt;
        Vector3 head = cam.transform.position + step;
        if (Physics.Raycast(head, Vector3.down, out var hit, 4f, ~(1 << kUiLayer), QueryTriggerInteraction.Ignore))
            vrRig.position = new Vector3(vrRig.position.x + step.x, hit.point.y, vrRig.position.z + step.z);
        else vrRig.position += step;
    }

    // ------------------------------------------------------------------ the menu
    void VrMenuToggle()
    {
        if (vrMenu == null)
        {
            var go = new GameObject("VR menu");
            vrMenu = go.transform;
            vrMenu.SetParent(vrRig, false);
            go.SetActive(false);
        }
        bool show = !vrMenu.gameObject.activeSelf;
        vrMenu.gameObject.SetActive(show);
        if (vrRay != null) vrRay.gameObject.SetActive(show);
        if (!show) return;
        // in front of you, a little below your eyes, facing you
        Vector3 f = cam.transform.forward;
        f.y = 0f;
        f = f.sqrMagnitude > 1e-4f ? f.normalized : Vector3.forward;
        vrMenu.position = cam.transform.position + f * 0.65f - Vector3.up * 0.12f;
        vrMenu.rotation = Quaternion.LookRotation(f, Vector3.up);
        vrPage = "main";
        vrListAt = 0;
        VrMenuBuild();
    }

    Font vrFont;
    Material vrPanelMat, vrBtnMat, vrBtnLit;
    TextMesh vrNote;

    void VrMenuBuild()
    {
        for (int i = vrMenu.childCount - 1; i >= 0; i--) Destroy(vrMenu.GetChild(i).gameObject);
        if (vrFont == null) vrFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (vrPanelMat == null) { vrPanelMat = Mat(new Color(0.08f, 0.09f, 0.12f)); vrBtnMat = Mat(new Color(0.22f, 0.25f, 0.32f)); vrBtnLit = Mat(new Color(0.35f, 0.55f, 0.9f)); }
        const float W = 0.56f, H = 0.62f;
        var back = GameObject.CreatePrimitive(PrimitiveType.Quad);
        Destroy(back.GetComponent<Collider>());
        back.transform.SetParent(vrMenu, false);
        back.transform.localScale = new Vector3(W, H, 1f);
        back.transform.localRotation = Quaternion.Euler(0f, 180f, 0f); // (a quad faces -z: turned to face you)
        back.GetComponent<Renderer>().sharedMaterial = vrPanelMat;
        float top = H / 2f - 0.04f;
        VrText(vrPage == "main" ? "Delirium Sandbox" : vrPage == "maps" ? "Maps" : vrPage == "browse" ? (vrBrowseFor == 0 ? "Your GTA IV folder" : "Your mods folder") : "Characters", new Vector3(0f, top, -0.003f), 0.0032f, Color.white);
        vrNote = VrText("", new Vector3(0f, top - 0.045f, -0.003f), 0.0018f, new Color(0.8f, 0.85f, 0.9f));
        float y = top - 0.1f;
        if (vrPage == "main")
        {
            if (!running)
            {
                VrBtn("Choose the GTA IV folder", 0f, y, 0.44f, () => VrBrowse(0));
                y -= 0.07f;
                VrBtn("Start again", 0f, y, 0.44f, () => { Launch(); VrMenuBuild(); });
            }
            else
            {
                VrBtn("Maps", -0.13f, y, 0.24f, () => { vrPage = "maps"; vrListAt = 0; RefreshFiles(); VrMenuBuild(); });
                VrBtn("Characters", 0.13f, y, 0.24f, () => { vrPage = "chars"; vrListAt = 0; RefreshFiles(); VrMenuBuild(); });
                y -= 0.07f;
                VrBtn("+ Man", -0.13f, y, 0.24f, () => AddMan());
                VrBtn("Reset", 0.13f, y, 0.24f, () => ResetAll());
                y -= 0.07f;
                VrBtn("Drunk (B)", -0.13f, y, 0.24f, () => { if (heroOn) ToggleDrunk(); });
                VrBtn("Noclip", 0.13f, y, 0.24f, () => { if (heroOn) ToggleNoclip(); });
                y -= 0.07f;
                VrBtn("- Man", -0.13f, y, 0.24f, () => RemoveLastMan());
                VrBtn("Remove all", 0.13f, y, 0.24f, () => RemoveAllMen());
                y -= 0.07f;
                VrBtn("Slow-mo", -0.13f, y, 0.24f, () => SetSpeed(speed < 1f ? 1f : 0.25f));
                VrBtn("Default map", 0.13f, y, 0.24f, () => { DefaultMap(); PlayerPrefs.DeleteKey(kPrefMap); });
                y -= 0.07f;
                VrBtn("GTA IV folder", -0.13f, y, 0.24f, () => VrBrowse(0));
                VrBtn("Mods folder", 0.13f, y, 0.24f, () => VrBrowse(1));
            }
            y -= 0.09f;
            VrBtn("Close (Y)", 0f, y, 0.3f, VrMenuToggle);
            return;
        }
        if (vrPage == "browse") { VrBrowsePage(y, H); return; }
        // a list: maps, or characters (each with Hero / Everyone)
        var list = new List<string>();
        foreach (var f in files) if (KindOf(f) == (vrPage == "maps" ? 2 : 1)) list.Add(f);
        list.Sort((p, q) => string.Compare(NameOf(p), NameOf(q), StringComparison.OrdinalIgnoreCase));
        const int per = 6;
        vrListAt = Mathf.Clamp(vrListAt, 0, Mathf.Max(0, list.Count - 1));
        for (int i = vrListAt; i < Mathf.Min(list.Count, vrListAt + per); i++)
        {
            string f = list[i];
            string n = NameOf(f);
            if (n.Length > 22) n = n.Substring(0, 21) + "…";
            if (vrPage == "maps") VrBtn(n, 0f, y, 0.5f, () => { Use(f, 2); VrMenuBuild(); });
            else
            {
                VrText(n, new Vector3(-0.13f, y, -0.003f), 0.0019f, Color.white);
                VrBtn("Hero", 0.1f, y, 0.11f, () => { Use(f, 0); VrMenuBuild(); });
                VrBtn("All", 0.215f, y, 0.09f, () => { Use(f, 1); VrMenuBuild(); });
            }
            y -= 0.062f;
        }
        if (list.Count == 0) { VrText(vrPage == "maps" ? "No maps in mods" : "No characters in mods", new Vector3(0f, y, -0.003f), 0.002f, Color.white); y -= 0.062f; }
        float by = -H / 2f + 0.05f;
        VrBtn("<", -0.2f, by, 0.1f, () => { vrListAt = Mathf.Max(0, vrListAt - per); VrMenuBuild(); });
        VrBtn("Back", 0f, by, 0.24f, () => { vrPage = "main"; VrMenuBuild(); });
        VrBtn(">", 0.2f, by, 0.1f, () => { if (vrListAt + per < list.Count) vrListAt += per; VrMenuBuild(); });
    }

    // what it says now (a load's note, an error), every frame
    void VrMenuLabels()
    {
        if (vrNote == null || vrMenu == null || !vrMenu.gameObject.activeSelf) return;
        string s = !running ? (error != "" ? error : "Put default.xex, character.xrt, build.txt (your GTA IV and Delirium's files) in the game's folder") : note != "" ? note : filesNote;
        s = s ?? "";
        if (s.Length > 140) s = s.Substring(0, 139) + "…";
        // (wrapped by hand: a TextMesh doesn't)
        var words = s.Split(' ');
        var sb = new System.Text.StringBuilder();
        int line = 0;
        foreach (var w in words)
        {
            if (line + w.Length > 60) { sb.Append('\n'); line = 0; }
            sb.Append(w).Append(' ');
            line += w.Length + 1;
        }
        string t = sb.ToString().TrimEnd();
        if (vrNote.text != t) vrNote.text = t;
    }

    TextMesh VrText(string s, Vector3 at, float size, Color c)
    {
        var go = new GameObject("text");
        go.transform.SetParent(vrMenu, false);
        go.transform.localPosition = at;
        var tm = go.AddComponent<TextMesh>();
        tm.font = vrFont;
        tm.fontSize = 64;
        tm.characterSize = size;
        tm.anchor = TextAnchor.MiddleCenter;
        tm.alignment = TextAlignment.Center;
        tm.color = c;
        tm.text = s;
        go.GetComponent<MeshRenderer>().sharedMaterial = vrFont.material;
        return tm;
    }

    void VrBtn(string label, float x, float y, float w, Action click)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = "button " + label;
        go.layer = kUiLayer;
        go.transform.SetParent(vrMenu, false);
        go.transform.localPosition = new Vector3(x, y, 0f);
        go.transform.localScale = new Vector3(w, 0.05f, 0.01f);
        var col = go.GetComponent<BoxCollider>();
        col.isTrigger = true;
        var r = go.GetComponent<Renderer>();
        r.sharedMaterial = vrBtnMat;
        var b = go.AddComponent<VrButton>();
        b.Click = click;
        b.Back = r;
        var t = VrText(label, new Vector3(x, y, -0.008f), 0.0021f, Color.white);
        b.Label = t;
    }

    VrButton vrLit;

    VrButton VrAimed()
    {
        VrButton hitB = null;
        if (vrRTarget != null && Physics.Raycast(vrRTarget.position, vrRTarget.forward, out var hit, 3f, 1 << kUiLayer, QueryTriggerInteraction.Collide))
            hitB = hit.collider.GetComponent<VrButton>();
        if (hitB != vrLit)
        {
            if (vrLit != null && vrLit.Back != null) vrLit.Back.sharedMaterial = vrBtnMat;
            if (hitB != null) hitB.Back.sharedMaterial = vrBtnLit;
            vrLit = hitB;
        }
        return hitB;
    }

    // ------------------------------------------------------------------ folders anywhere on the headset
    // (Android: "All files access" lets the game read your folders where they are, as the iPhone's picker does)
    int vrBrowseFor; // 0 the GTA IV folder (default.xex, character.xrt, build.txt...), 1 your mods folder
    string vrBrowseDir = "";

    static bool AllFilesAccess()
    {
        if (Application.platform != RuntimePlatform.Android) return true;
        try { using (var env = new AndroidJavaClass("android.os.Environment")) return env.CallStatic<bool>("isExternalStorageManager"); }
        catch (Exception) { return true; }
    }

    static void AskAllFilesAccess()
    {
        if (Application.platform != RuntimePlatform.Android) return;
        try
        {
            using (var up = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var act = up.GetStatic<AndroidJavaObject>("currentActivity"))
            {
                AndroidJavaObject intent;
                try
                {
                    using (var uriC = new AndroidJavaClass("android.net.Uri"))
                    using (var uri = uriC.CallStatic<AndroidJavaObject>("parse", "package:" + Application.identifier))
                        intent = new AndroidJavaObject("android.content.Intent", "android.settings.MANAGE_APP_ALL_FILES_ACCESS_PERMISSION", uri);
                    act.Call("startActivity", intent);
                }
                catch (Exception)
                {
                    intent = new AndroidJavaObject("android.content.Intent", "android.settings.MANAGE_ALL_FILES_ACCESS_PERMISSION");
                    act.Call("startActivity", intent);
                }
            }
        }
        catch (Exception e) { Debug.LogWarning("[Sandbox] all files access: " + e.Message); }
    }

    void VrBrowse(int what)
    {
        vrBrowseFor = what;
        if (!AllFilesAccess()) AskAllFilesAccess();
        string start = what == 0 ? (Directory.Exists(gameExe) ? gameExe : Path.GetDirectoryName(gameExe ?? "")) : gmodDir;
        vrBrowseDir = !string.IsNullOrEmpty(start) && Directory.Exists(start) && !start.Contains("/Android/data/") ? start : "/storage/emulated/0";
        vrPage = "browse";
        vrListAt = 0;
        VrMenuBuild();
    }

    void VrBrowsePage(float y, float H)
    {
        string shown = vrBrowseDir.Replace("/storage/emulated/0", "Quest");
        if (shown.Length > 48) shown = "..." + shown.Substring(shown.Length - 47);
        VrText(shown, new Vector3(0f, y + 0.035f, -0.003f), 0.0017f, new Color(0.7f, 0.85f, 1f));
        if (!AllFilesAccess())
        {
            VrText("Allow \"All files access\" for the game (the window that opened), then reopen this", new Vector3(0f, y, -0.003f), 0.0016f, new Color(1f, 0.8f, 0.4f));
            y -= 0.03f;
            VrText("(no window? on a PC: adb shell appops set " + Application.identifier + " MANAGE_EXTERNAL_STORAGE allow)", new Vector3(0f, y, -0.003f), 0.0013f, new Color(0.8f, 0.8f, 0.8f));
            y -= 0.04f;
            VrBtn("Ask again", 0f, y, 0.3f, () => { AskAllFilesAccess(); VrMenuBuild(); });
            y -= 0.07f;
        }
        string[] dirs;
        try { dirs = Directory.GetDirectories(vrBrowseDir); Array.Sort(dirs, StringComparer.OrdinalIgnoreCase); }
        catch (Exception) { dirs = new string[0]; }
        const int per = 5;
        vrListAt = Mathf.Clamp(vrListAt, 0, Mathf.Max(0, dirs.Length - 1));
        for (int i = vrListAt; i < Mathf.Min(dirs.Length, vrListAt + per); i++)
        {
            string d = dirs[i];
            string n = Path.GetFileName(d);
            if (n.StartsWith(".")) continue;
            // (the folder with your GTA IV files: starred)
            bool gta = File.Exists(Path.Combine(d, "default.xex")) || File.Exists(Path.Combine(d, "character.xrt"));
            if (n.Length > 26) n = n.Substring(0, 25) + "...";
            VrBtn((gta ? "* " : "") + n, 0f, y, 0.5f, () => { vrBrowseDir = d; vrListAt = 0; VrMenuBuild(); });
            y -= 0.058f;
        }
        bool here = File.Exists(Path.Combine(vrBrowseDir, "default.xex")) && File.Exists(Path.Combine(vrBrowseDir, "character.xrt"));
        float by = -H / 2f + 0.11f;
        VrBtn("Up", -0.2f, by, 0.12f, () => { var p = Path.GetDirectoryName(vrBrowseDir); if (!string.IsNullOrEmpty(p) && p.Length >= "/storage".Length) vrBrowseDir = p; vrListAt = 0; VrMenuBuild(); });
        VrBtn(vrBrowseFor == 0 ? (here ? "Use this folder" : "Use (no default.xex)") : "Use this folder", 0.07f, by, 0.38f, () => VrUseFolder(vrBrowseDir));
        by -= 0.06f;
        VrBtn("<", -0.2f, by, 0.1f, () => { vrListAt = Mathf.Max(0, vrListAt - per); VrMenuBuild(); });
        VrBtn("Back", 0f, by, 0.24f, () => { vrPage = "main"; VrMenuBuild(); });
        VrBtn(">", 0.2f, by, 0.1f, () => { if (vrListAt + per < dirs.Length) vrListAt += per; VrMenuBuild(); });
    }

    void VrUseFolder(string dir)
    {
        if (vrBrowseFor == 0)
        {
            gameExe = dir;
            dataDir = dir;
            PlayerPrefs.SetString(kPrefExe, gameExe);
            PlayerPrefs.SetString(kPrefData, dataDir);
            PlayerPrefs.Save();
            Debug.Log("[Sandbox] GTA IV folder: " + dir);
            if (!running) Launch();
            else note = "GTA IV folder: " + dir + " (used the next time the game starts)";
        }
        else
        {
            gmodDir = dir;
            PlayerPrefs.SetString(kPrefGmod, dir);
            PlayerPrefs.Save();
            Debug.Log("[Sandbox] mods folder: " + dir);
            filesNote = "Mods folder: " + dir;
            if (running) RefreshFiles();
        }
        vrPage = "main";
        VrMenuBuild();
    }

    // ------------------------------------------------------------------ the hero's own hands on yours
    // His hands cut from his model (every triangle his hand or fingers move), in their bind pose, laid in your
    // controllers' frame: his fingers along its forward, the back of his hand outward, the wrist behind the grip.
    Mannequin vrHandsOf;

    void VrHandsLook()
    {
        if (hero?.Body == null || vrL == null || vrR == null || vrHandsOf == hero.Body) return;
        vrHandsOf = hero.Body;
        vrL.SetLook(VrHandMesh(true, out var fl), fl);
        vrR.SetLook(VrHandMesh(false, out var fr), fr);
    }

    static void AddWeight(Dictionary<int, float> acc, int[] remap, int bi, float wt)
    {
        if (wt <= 0f) return;
        int o = bi >= 0 && bi < remap.Length ? remap[bi] : 0;
        float x;
        acc[o] = (acc.TryGetValue(o, out x) ? x : 0f) + wt;
    }

    GameObject VrHandMesh(bool left, out VrFingers fingers)
    {
        fingers = null;
        try
        {
            var body = hero.Body;
            Transform hand = body.Bone(left ? "LeftHand" : "RightHand"), hips = body.Bone("Hips") ?? body.Hips, head = body.Bone("Head");
            if (hand == null) return null;
            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var uvs = new List<Vector2>();
            var weights = new List<BoneWeight>();
            var subs = new List<List<int>>();
            var mats = new List<Material>();
            // his hand's bones (the hand, its fingers), each once: their bind pose in the hand's space
            var boneList = new List<Transform>();
            var boneIn = new Dictionary<Transform, int>();
            var boneM = new List<Matrix4x4>(); // bone -> hand space
            Vector3 upH = Vector3.zero; // up (hips to head) in the hand's space at the bind pose
            float k = Mathf.Abs((hand.parent != null ? hand.parent : hand).lossyScale.x);
            if (k < 1e-6f) k = 1f;
            foreach (var smr in body.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = smr.sharedMesh;
                if (mesh == null || !mesh.isReadable) continue;
                var bones = smr.bones;
                var bind = mesh.bindposes;
                int hi = -1, hp = -1, hd = -1;
                var mine = new bool[bones.Length];
                for (int i = 0; i < bones.Length; i++)
                {
                    if (bones[i] == null) continue;
                    if (bones[i] == hand) hi = i;
                    if (bones[i] == hips) hp = i;
                    if (bones[i] == head) hd = i;
                    mine[i] = bones[i].IsChildOf(hand);
                }
                if (hi < 0 || hi >= bind.Length) continue;
                Matrix4x4 toHand = bind[hi];
                if (upH == Vector3.zero)
                {
                    Vector3 upMesh = hp >= 0 && hd >= 0 ? bind[hd].inverse.MultiplyPoint3x4(Vector3.zero) - bind[hp].inverse.MultiplyPoint3x4(Vector3.zero) : Vector3.up;
                    upH = toHand.MultiplyVector(upMesh).normalized;
                }
                // this mesh's bones -> ours (the hand first)
                var remap = new int[bones.Length];
                if (!boneIn.ContainsKey(hand)) { boneIn[hand] = boneList.Count; boneList.Add(hand); boneM.Add(Matrix4x4.identity); }
                for (int i = 0; i < bones.Length; i++)
                {
                    remap[i] = 0;
                    if (!mine[i] || i >= bind.Length) continue;
                    if (!boneIn.TryGetValue(bones[i], out int at))
                    {
                        at = boneList.Count;
                        boneList.Add(bones[i]);
                        boneM.Add(toHand * bind[i].inverse);
                        boneIn[bones[i]] = at;
                    }
                    remap[i] = at;
                }
                var bw = mesh.boneWeights;
                var mv = mesh.vertices;
                var mn = mesh.normals;
                var mu = mesh.uv;
                var map = new Dictionary<int, int>();
                for (int sm = 0; sm < mesh.subMeshCount; sm++)
                {
                    var tris = mesh.GetTriangles(sm);
                    List<int> outTris = null;
                    for (int t = 0; t + 2 < tris.Length; t += 3)
                    {
                        bool all = true;
                        for (int c = 0; c < 3 && all; c++)
                        {
                            int v = tris[t + c];
                            if (v >= bw.Length) { all = false; break; }
                            var w = bw[v];
                            float on = (w.boneIndex0 < mine.Length && mine[w.boneIndex0] ? w.weight0 : 0f) + (w.boneIndex1 < mine.Length && mine[w.boneIndex1] ? w.weight1 : 0f)
                                + (w.boneIndex2 < mine.Length && mine[w.boneIndex2] ? w.weight2 : 0f) + (w.boneIndex3 < mine.Length && mine[w.boneIndex3] ? w.weight3 : 0f);
                            all = on > 0.5f;
                        }
                        if (!all) continue;
                        if (outTris == null)
                        {
                            outTris = new List<int>();
                            subs.Add(outTris);
                            var sms = smr.sharedMaterials;
                            mats.Add(sm < sms.Length ? sms[sm] : (sms.Length > 0 ? sms[sms.Length - 1] : null));
                        }
                        for (int c = 0; c < 3; c++)
                        {
                            int v = tris[t + c];
                            if (!map.TryGetValue(v, out int nv))
                            {
                                nv = verts.Count;
                                map[v] = nv;
                                verts.Add(toHand.MultiplyPoint3x4(mv[v]));
                                norms.Add(v < mn.Length ? toHand.MultiplyVector(mn[v]).normalized : Vector3.up);
                                uvs.Add(v < mu.Length ? mu[v] : Vector2.zero);
                                // its weights on our bones (what was on the forearm: on the hand)
                                var w = bw[v];
                                var acc = new Dictionary<int, float>();
                                AddWeight(acc, remap, w.boneIndex0, w.weight0);
                                AddWeight(acc, remap, w.boneIndex1, w.weight1);
                                AddWeight(acc, remap, w.boneIndex2, w.weight2);
                                AddWeight(acc, remap, w.boneIndex3, w.weight3);
                                var list = new List<KeyValuePair<int, float>>(acc);
                                list.Sort((x, y) => y.Value.CompareTo(x.Value));
                                var o2 = new BoneWeight();
                                float sum = 0f;
                                for (int q = 0; q < list.Count && q < 4; q++) sum += list[q].Value;
                                if (sum <= 0f) { list.Clear(); list.Add(new KeyValuePair<int, float>(0, 1f)); sum = 1f; }
                                if (list.Count > 0) { o2.boneIndex0 = list[0].Key; o2.weight0 = list[0].Value / sum; }
                                if (list.Count > 1) { o2.boneIndex1 = list[1].Key; o2.weight1 = list[1].Value / sum; }
                                if (list.Count > 2) { o2.boneIndex2 = list[2].Key; o2.weight2 = list[2].Value / sum; }
                                if (list.Count > 3) { o2.boneIndex3 = list[3].Key; o2.weight3 = list[3].Value / sum; }
                                weights.Add(o2);
                            }
                            outTris.Add(nv);
                        }
                    }
                }
            }
            if (verts.Count < 12 || boneList.Count == 0 || boneList[0] != hand) return null;
            // the hand's way: wrist to its middle; the back of it: up, square to that
            Vector3 mid = Vector3.zero;
            foreach (var v in verts) mid += v;
            mid /= verts.Count;
            Vector3 f = mid.normalized;
            Vector3 u = Vector3.ProjectOnPlane(upH, f);
            if (u.sqrMagnitude < 1e-6f) u = Vector3.ProjectOnPlane(Vector3.up, f);
            u.Normalize();
            float side = left ? -1f : 1f;
            var frame = Quaternion.LookRotation(f, side * Vector3.Cross(f, u));
            var into = Quaternion.Inverse(frame);
            // the hand's bones: copies of his, in their bind pose (the hand at our origin)
            var go = new GameObject((left ? "Left" : "Right") + " hand look");
            var clones = new Transform[boneList.Count];
            clones[0] = go.transform;
            for (int i = 1; i < boneList.Count; i++)
            {
                clones[i] = new GameObject(boneList[i].name).transform;
                clones[i].SetParent(go.transform, false);
            }
            for (int i = 1; i < boneList.Count; i++)
            {
                // under its own parent's copy, if it's one of ours
                int pi = boneList[i].parent != null && boneIn.TryGetValue(boneList[i].parent, out int pp) ? pp : 0;
                Matrix4x4 local = boneM[pi].inverse * boneM[i];
                clones[i].SetParent(clones[pi], false);
                clones[i].localPosition = local.GetColumn(3);
                clones[i].localRotation = local.rotation;
            }
            var binds = new Matrix4x4[clones.Length];
            for (int i = 0; i < clones.Length; i++) binds[i] = clones[i].worldToLocalMatrix; // (the look at the origin)
            var m = new Mesh { name = (left ? "Left" : "Right") + " hand (hero)" };
            if (verts.Count > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.SetVertices(verts);
            m.SetNormals(norms);
            m.SetUVs(0, uvs);
            m.boneWeights = weights.ToArray();
            m.bindposes = binds;
            m.subMeshCount = subs.Count;
            for (int i = 0; i < subs.Count; i++) m.SetTriangles(subs[i], i);
            m.RecalculateBounds();
            var r = go.AddComponent<SkinnedMeshRenderer>();
            r.sharedMesh = m;
            r.bones = clones;
            r.rootBone = go.transform;
            r.sharedMaterials = mats.ToArray();
            r.updateWhenOffscreen = true;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            // the fingers' bends: each chain off the hand a thumb, an index or one of the rest (by name, else where it is)
            Vector3 palm = -u; // (the hand's space)
            var fing = new VrFingers { Smr = r, Bone = clones, Rest = new Quaternion[clones.Length], Axis = new Vector3[clones.Length], Group = new int[clones.Length], Max = new float[clones.Length] };
            var chainGroup = new Dictionary<int, int>();
            int thumbRoot = -1, indexRoot = -1;
            var roots = new List<int>();
            for (int i = 1; i < clones.Length; i++) if (clones[i].parent == clones[0] && clones[i].childCount > 0) roots.Add(i);
            foreach (int i in roots) { if (thumbRoot < 0 && kThumb.IsMatch(boneList[i].name)) thumbRoot = i; else if (indexRoot < 0 && kIndex.IsMatch(boneList[i].name)) indexRoot = i; }
            if (thumbRoot < 0 && roots.Count >= 4)
            {
                // nearest the wrist along the hand
                float bz = float.MaxValue;
                foreach (int i in roots) { float z = Vector3.Dot(clones[i].position, f); if (z < bz) { bz = z; thumbRoot = i; } }
            }
            if (indexRoot < 0 && roots.Count >= 4)
            {
                // the next to the thumb's side of the rest
                Vector3 thumbSide = Vector3.Cross(f, u) * side;
                float by = float.MinValue;
                foreach (int i in roots) { if (i == thumbRoot) continue; float y = Vector3.Dot(clones[i].position, thumbSide); if (y > by) { by = y; indexRoot = i; } }
            }
            for (int i = 0; i < clones.Length; i++)
            {
                fing.Rest[i] = clones[i].localRotation;
                fing.Group[i] = -1;
                if (i == 0) continue;
                // its chain's root
                int root = i;
                while (root > 0 && clones[root].parent != clones[0])
                {
                    int up = Array.IndexOf(clones, clones[root].parent);
                    if (up < 0) break;
                    root = up;
                }
                if (!roots.Contains(root)) continue;
                fing.Group[i] = root == thumbRoot ? 0 : root == indexRoot ? 1 : 2;
                Vector3 dir = clones[i].childCount > 0 ? clones[i].GetChild(0).position - clones[i].position : clones[i].position - clones[i].parent.position;
                if (dir.sqrMagnitude < 1e-10f) { fing.Group[i] = -1; continue; }
                Vector3 axis = Vector3.Cross(dir.normalized, palm);
                if (axis.sqrMagnitude < 1e-6f) { fing.Group[i] = -1; continue; }
                fing.Axis[i] = Quaternion.Inverse(clones[i].rotation) * axis.normalized;
                fing.Max[i] = fing.Group[i] == 0 ? 35f : clones[i].parent == clones[0] ? 65f : 80f;
            }
            fingers = fing;
            // laid in the controller's frame: fingers along its forward, the back of the hand out, the palm round the grip
            go.transform.localScale = Vector3.one * k;
            go.transform.localRotation = into;
            go.transform.localPosition = new Vector3(side * 0.02f, -0.01f, -mid.magnitude * k * 1.1f);
            Debug.Log("[Sandbox] VR " + (left ? "left" : "right") + " hand: " + verts.Count + " vertices, " + clones.Length + " bones (thumb " + (thumbRoot > 0 ? boneList[thumbRoot].name : "-") + ", index " + (indexRoot > 0 ? boneList[indexRoot].name : "-") + ")");
            return go;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[Sandbox] VR hands from the hero: " + e.Message);
            return null;
        }
    }

    // ------------------------------------------------------------------ the hands
    // a person's part near a point (not the hero's): the man, the place
    Man VrNearMan(Vector3 p, float within, out Vector3 at)
    {
        Man best = null;
        at = p;
        float bestD = within;
        foreach (var m in men)
        {
            if (m.Hero || m.Body == null) continue;
            foreach (var b in m.Body.Bones)
            {
                float d = Vector3.Distance(b.position, p);
                if (d < bestD) { bestD = d; best = m; at = b.position; }
            }
        }
        return best;
    }

    // a hand that hit someone fast: a punch (his reaction), and its sound
    void VrPunch(VrHand h, Vector3 vel)
    {
        var m = VrNearMan(h.transform.position, 0.16f, out var at);
        if (m == null || m.Rising) return;
        nm.MeleeHit(m.Body.transform, at, vel.normalized * Mathf.Clamp(vel.magnitude * 14f, 30f, 110f), cam.transform.position);
        Play(Sfx.Punch, at, Mathf.Clamp01(vel.magnitude / 6f));
        h.HapticPulse(Mathf.Clamp01(vel.magnitude / 5f), 0.08f);
    }

    // a hand that met a man on his feet, slower than a punch: he's pushed (Delirium's balance: a step, a stagger, a
    // fall if it was a shove). (Once he's a ragdoll Delirium's boxes meet him: the hands are movers to it.)
    bool VrTouch(VrHand h, Vector3 vel)
    {
        var m = VrNearMan(h.transform.position, 0.14f, out var at);
        if (m == null || m.Rising || m.Killed || m.Body == null) return false;
        if (nm.State(m.Body.transform).Ragdolled) return false;
        if (!nm.Ragdoll(m.Body.transform, vel.normalized * (10f + vel.magnitude * 10f))) return false;
        Outfit(m);
        h.HapticPulse(0.3f, 0.05f);
        return true;
    }

    int VrGrab(VrHand h, out Man who)
    {
        who = VrNearMan(h.transform.position, 0.2f, out var at);
        if (who == null || nm == null) return -1;
        return nm.Leash(who.Body.transform, at, h.transform.position, 0.05f);
    }

    void VrHold(VrHand h, Man who, int part)
    {
        if (who?.Body == null || nm == null) return;
        nm.LeashAnchor(who.Body.transform, part, h.transform.position, 0.05f);
    }

    void VrLetGo(Man who, int part)
    {
        if (who?.Body == null || nm == null) return;
        nm.Unleash(who.Body.transform, part);
    }

    // a hand: a body of its own pulled to the controller each physics step (never through a wall), a moving box
    // for Delirium; its grip holds a person, or the wall it's on (climbing)
    public class VrHand : MonoBehaviour
    {
        Game game;
        public Transform Target;
        Rigidbody rb;
        BoxCollider box;
        float punchReady, touchReady;
        bool holding;
        Man held;
        int heldPart = -1;
        Collider ignored;
        bool left;
        // holding the world: what, where (in its frame)
        public bool Climbing;
        public Transform ClimbOn;
        public Vector3 ClimbLocal;
        public VrFingers Fingers;

        public static VrHand Make(Game g, Transform target, bool left)
        {
            var go = new GameObject(left ? "Left hand" : "Right hand");
            go.transform.SetPositionAndRotation(target.position, target.rotation);
            var h = go.AddComponent<VrHand>();
            h.game = g;
            h.Target = target;
            h.left = left;
            h.rb = go.AddComponent<Rigidbody>();
            h.rb.useGravity = false;
            h.rb.mass = 2f;
            h.rb.interpolation = RigidbodyInterpolation.Interpolate;
            h.rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            h.box = go.AddComponent<BoxCollider>();
            h.box.size = new Vector3(0.085f, 0.045f, 0.17f);
            h.box.center = new Vector3(0f, -0.01f, -0.02f);
            // what you see: a mitten, till the hero's own hands are there (SetLook)
            var look = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(look.GetComponent<Collider>());
            look.transform.SetParent(go.transform, false);
            look.transform.localScale = h.box.size;
            look.transform.localPosition = h.box.center;
            look.GetComponent<Renderer>().sharedMaterial = g.Mat(new Color(0.85f, 0.68f, 0.55f));
            look.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            h.mitten = look;
            return h;
        }

        // the wrist (the hero's arm reaches for it): the hand's bone in the hero's look, else behind the mitten
        public Transform Wrist
        {
            get
            {
                if (own != null) return own.transform;
                if (wristMark == null)
                {
                    wristMark = new GameObject("wrist").transform;
                    wristMark.SetParent(transform, false);
                    wristMark.localPosition = new Vector3(0f, -0.01f, -0.11f);
                }
                return wristMark;
            }
        }
        Transform wristMark;

        void FixedUpdate()
        {
            if (Target == null) return;
            float dt = Time.fixedDeltaTime;
            // (not into the hero's own capsule)
            if (game.heroCC != null && ignored != game.heroCC) { Physics.IgnoreCollision(box, game.heroCC); ignored = game.heroCC; }
            if (Climbing && ClimbOn != null)
            {
                // on the wall: where it took hold, as the wall moves (a lift)
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.MovePosition(ClimbOn.TransformPoint(ClimbLocal));
                rb.MoveRotation(Target.rotation);
                return;
            }
            Vector3 to = Target.position - rb.position;
            // stuck far behind a wall (or after a teleport): there at once
            if (to.magnitude > 0.7f) { rb.position = Target.position; rb.rotation = Target.rotation; rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; return; }
            rb.linearVelocity = Vector3.ClampMagnitude(to / dt, 25f);
            var turn = Target.rotation * Quaternion.Inverse(rb.rotation);
            turn.ToAngleAxis(out float deg, out Vector3 axis);
            if (deg > 180f) deg -= 360f;
            if (!float.IsInfinity(axis.x) && Mathf.Abs(deg) > 0.01f) rb.angularVelocity = Vector3.ClampMagnitude(axis * (deg * Mathf.Deg2Rad / dt), 40f);
            else rb.angularVelocity = Vector3.zero;
        }

        void Update()
        {
            if (game.nm == null || !game.running) return;
            // the registration (Delirium may have come up after the hand, or been made again)
            if (!registered || registeredWith != game.nm)
            {
                try { game.nm.RegisterMover(transform, DeliriumRagdolls.MoverKind.Object, box.size, true, false, true, box.center); registered = true; registeredWith = game.nm; } catch (Exception) { }
            }
            punchReady -= Time.deltaTime;
            touchReady -= Time.deltaTime;
            Vector3 v = rb.linearVelocity;
            if (!holding && punchReady <= 0f && v.magnitude > 2.5f)
            {
                game.VrPunch(this, v);
                punchReady = 0.35f;
            }
            // a slower touch: a standing man feels it too (Delirium's balance takes him: he steps, staggers)
            else if (!holding && touchReady <= 0f && v.magnitude > 0.7f && game.VrTouch(this, v)) touchReady = 0.6f;
            if (holding && held != null && held.Body == null) { held = null; heldPart = -1; }
            if (holding && held != null) game.VrHold(this, held, heldPart);
        }
        bool registered;
        DeliriumRagdolls registeredWith;
        GameObject mitten, own;

        // the hero's hands in place of the mitten (null: the mitten again)
        public void SetLook(GameObject look, VrFingers fingers)
        {
            if (own != null)
            {
                var smr = own.GetComponent<SkinnedMeshRenderer>();
                if (smr != null && smr.sharedMesh != null) Destroy(smr.sharedMesh);
                Destroy(own);
            }
            own = look;
            Fingers = look != null ? fingers : null;
            if (mitten != null) mitten.SetActive(look == null);
            if (look == null) return;
            look.transform.SetParent(transform, false); // (its place in the hand: set where it was made)
        }

        // the fingers: the trigger's index, the grip's three, the thumb on a button or the stick
        public void FingersPose(float trigger, float grip, bool thumb)
        {
            if (Fingers == null) return;
            bool fist = Climbing || held != null;
            Fingers.Pose(fist || thumb ? 1f : 0f, fist ? 1f : trigger, fist ? 1f : grip, Time.deltaTime);
        }

        public void Grip(bool on)
        {
            if (on == holding) return;
            holding = on;
            if (on)
            {
                if (game.nm == null || !game.running || !isActiveAndEnabled) return;
                heldPart = game.VrGrab(this, out held);
                if (heldPart < 0) held = null;
                // no one there: the wall, if it's there
                if (held == null && game.VrClimbGrab(this, out var at, out var local))
                {
                    Climbing = true;
                    ClimbOn = at;
                    ClimbLocal = local;
                    rb.isKinematic = true;
                    game.Play(Sfx.Step, transform.position, 0.4f);
                }
            }
            else
            {
                if (held != null) game.VrLetGo(held, heldPart);
                held = null;
                heldPart = -1;
                LetGoWorld();
            }
        }

        public void LetGoWorld()
        {
            if (!Climbing) return;
            Climbing = false;
            ClimbOn = null;
            rb.isKinematic = false;
        }

        // put away (you're outside his body: a ragdoll seen from behind) or back
        public void Park(bool away)
        {
            if (away == !gameObject.activeSelf) return;
            if (away)
            {
                Grip(false);
                try { game?.nm?.UnregisterCar(transform); } catch (Exception) { }
                registered = false;
                gameObject.SetActive(false);
            }
            else
            {
                transform.SetPositionAndRotation(Target.position, Target.rotation);
                gameObject.SetActive(true);
                rb.position = Target.position;
                rb.rotation = Target.rotation;
                rb.linearVelocity = Vector3.zero;
            }
        }

        // a thing it bumps into: a knock
        void OnCollisionEnter(Collision c)
        {
            if (c.relativeVelocity.magnitude > 1.6f && Time.time > knockAt)
            {
                knockAt = Time.time + 0.15f;
                game.Play(Sfx.Impact, transform.position, Mathf.Clamp01(c.relativeVelocity.magnitude / 8f) * 0.6f);
                HapticPulse(Mathf.Clamp01(c.relativeVelocity.magnitude / 5f), 0.05f);
            }
        }
        float knockAt;

        public void HapticPulse(float amp, float sec)
        {
            try
            {
                var d = InputDevices.GetDeviceAtXRNode(left ? XRNode.LeftHand : XRNode.RightHand);
                if (d.isValid) d.SendHapticImpulse(0, Mathf.Clamp01(amp), sec);
            }
            catch (Exception) { }
        }

        void OnDestroy()
        {
            try { game?.nm?.UnregisterCar(transform); } catch (Exception) { }
        }
    }

    public class VrButton : MonoBehaviour
    {
        public Action Click;
        public Renderer Back;
        public TextMesh Label;
    }
}
