// The Quest (VR): your body. In first person the hero's own body is drawn under you (his head and hands hidden: your
// eyes are his, and your hands are the ones on your controllers): his arms reach for your hands (two-bone IK), his
// hips go down when you crouch and his legs bend for it, his feet stay planted on the ground till they're left too
// far behind (a step), so walking about your room or turning on the spot he steps with you.
//
// All drawn only: after every LateUpdate (VrLate, order 300) the bones are put where they're drawn, and before the
// next frame's physics and clips (VrEarly) they're put back as the clips left them. Delirium never sees them.
//
// Also here: climbing (a fist on a wall, a ledge, anything of the map: your hand stays on it and your body goes the
// other way as you pull) and the hands' fingers (trigger: the index, grip: the other three, a thumb on a button or
// the stick: the thumb).
using System;
using System.Collections.Generic;
using UnityEngine;

public partial class Game
{
    // ------------------------------------------------------------------ before and after the frame
    [DefaultExecutionOrder(-30000)]
    public class VrEarly : MonoBehaviour
    {
        public Game G;
        void FixedUpdate() => G?.VrBodyRestore();
        void Update() => G?.VrBodyRestore();
    }

    [DefaultExecutionOrder(300)] // after Game (100) and SpringBones (200): the pose is final
    public class VrLate : MonoBehaviour
    {
        public Game G;
        void LateUpdate() => G?.VrBodyLate();
    }

    Mannequin vrPosed;            // the body whose bones are moved for the drawing (put back by VrBodyRestore)
    Vector3[] vrSavedPos, vrSavedScale;
    Quaternion[] vrSavedRot;
    Vector3 vrBodyRootPos, vrBodyRootScale = Vector3.one;
    Quaternion vrBodyRootRot;
    // your height (your eyes above the floor, stood straight: the menu's Height) and his (his eyes, standing): he's
    // drawn scaled to yours, so stood straight his legs are straight
    float vrEye, vrModelEye;
    const string kPrefEye = "delirium.vrEye";
    // the feet: where each is planted, and a step's progress (-1: planted)
    Vector3[] vrFoot = new Vector3[2];
    float[] vrStep = { -1f, -1f };
    Vector3[] vrStepFrom = new Vector3[2];
    bool vrFeetSet;

    void VrBodyRestore()
    {
        if (vrPosed == null) return;
        var b = vrPosed.Bones;
        if (vrSavedPos != null && b != null && vrSavedPos.Length == b.Length)
            for (int i = b.Length - 1; i >= 0; i--)
            {
                if (b[i] == null) continue;
                b[i].localPosition = vrSavedPos[i];
                b[i].localRotation = vrSavedRot[i];
                b[i].localScale = vrSavedScale[i];
            }
        vrPosed.transform.SetPositionAndRotation(vrBodyRootPos, vrBodyRootRot);
        vrPosed.transform.localScale = vrBodyRootScale;
        vrPosed = null;
    }

    bool VrBodyShown() => vrOn && !vrThird && heroOn && hero?.Body != null && !heroNoclip && !riding && heroMode == 0 && !hero.Rising;

    void VrBodyLate()
    {
        VrBodyRestore();
        if (!VrBodyShown() || cam == null) { vrFeetSet = false; return; }
        var body = hero.Body;
        var bones = body.Bones;
        if (bones == null || bones.Length == 0) return;
        Transform hips = body.Hips, head = body.Bone("Head");
        if (hips == null || head == null) return;
        // what the clips left: kept to put back
        if (vrSavedPos == null || vrSavedPos.Length != bones.Length)
        {
            vrSavedPos = new Vector3[bones.Length];
            vrSavedRot = new Quaternion[bones.Length];
            vrSavedScale = new Vector3[bones.Length];
        }
        for (int i = 0; i < bones.Length; i++)
        {
            if (bones[i] == null) continue;
            vrSavedPos[i] = bones[i].localPosition;
            vrSavedRot[i] = bones[i].localRotation;
            vrSavedScale[i] = bones[i].localScale;
        }
        vrBodyRootPos = body.transform.position;
        vrBodyRootRot = body.transform.rotation;
        vrBodyRootScale = body.transform.localScale;
        vrPosed = body;

        // his eyes' height as the clips stand him (learnt while he stands still); scaled to yours
        float eyeNow = head.position.y + 0.09f - body.transform.position.y;
        if (heroGround && heroVel.magnitude < 0.2f && !vrClimbNow && eyeNow > 0.5f)
            vrModelEye = vrModelEye <= 0f ? eyeNow : Mathf.Lerp(vrModelEye, eyeNow, 0.02f);
        float scale = vrEye > 0.5f && vrModelEye > 0.5f ? Mathf.Clamp(vrEye / vrModelEye, 0.6f, 1.6f) : 1f;
        if (Mathf.Abs(scale - 1f) > 0.005f) body.transform.localScale = vrBodyRootScale * scale;
        if (vrL != null) vrL.LookScale(scale);
        if (vrR != null) vrR.LookScale(scale);

        Vector3 fwd = body.transform.forward;
        Vector3 right = body.transform.right;
        // the feet as the clips have them, on the ground under them
        Transform[] up = { body.Bone("LeftUpLeg"), body.Bone("RightUpLeg") }, knee = { body.Bone("LeftLeg"), body.Bone("RightLeg") }, foot = { body.Bone("LeftFoot"), body.Bone("RightFoot") };
        var animFoot = new Vector3[2];
        var footRot = new Quaternion[2];
        for (int s = 0; s < 2; s++)
        {
            if (foot[s] == null) continue;
            animFoot[s] = foot[s].position;
            footRot[s] = foot[s].rotation;
            float lift = Mathf.Max(0f, animFoot[s].y - heroPos.y);
            if (heroGround && Physics.Raycast(animFoot[s] + Vector3.up * 0.5f, Vector3.down, out var g, 1.1f, VrWorldMask(), QueryTriggerInteraction.Ignore))
                animFoot[s].y = g.point.y + lift;
        }
        // planted, or a step to catch up (one foot at a time); on the move or in the air: the clips' own
        bool still = heroGround && new Vector3(heroVel.x, 0f, heroVel.z).magnitude < 0.25f && !climbing && !vrClimbNow;
        if (!vrFeetSet || !still)
        {
            vrFoot[0] = animFoot[0];
            vrFoot[1] = animFoot[1];
            vrStep[0] = vrStep[1] = -1f;
            vrFeetSet = true;
        }
        else
        {
            float dt = Time.deltaTime;
            for (int s = 0; s < 2; s++)
            {
                if (foot[s] == null) continue;
                if (vrStep[s] >= 0f)
                {
                    vrStep[s] += dt / 0.2f;
                    float t = Mathf.Clamp01(vrStep[s]);
                    vrFoot[s] = Vector3.Lerp(vrStepFrom[s], animFoot[s], t) + Vector3.up * Mathf.Sin(t * Mathf.PI) * 0.08f;
                    if (t >= 1f) { vrStep[s] = -1f; Play(Sfx.Step, vrFoot[s], 0.35f); }
                }
                else if (vrStep[1 - s] < 0f && (vrFoot[s] - animFoot[s]).magnitude > 0.22f)
                {
                    vrStep[s] = 0f;
                    vrStepFrom[s] = vrFoot[s];
                }
            }
        }

        // the head under your eyes (a little behind them: you see your chest, not inside it); down when you crouch
        Vector3 eye = head.position + Vector3.up * 0.09f * scale;
        Vector3 want = cam.transform.position - fwd * 0.1f;
        Vector3 shift = new Vector3(want.x - eye.x, Mathf.Clamp(want.y - eye.y, -0.75f, 0f), want.z - eye.z);
        if (shift.sqrMagnitude > 1.5f * 1.5f) shift = Vector3.zero; // (a teleport this frame)
        hips.position += shift;

        // the legs to the feet, the knees forward (hanging on a wall: knees up, feet under him against it)
        for (int s = 0; s < 2; s++)
        {
            if (up[s] == null || knee[s] == null || foot[s] == null) continue;
            Vector3 to = vrFoot[s];
            if (vrClimbNow || vrMantle >= 0f)
            {
                float leg = (knee[s].position - up[s].position).magnitude + (foot[s].position - knee[s].position).magnitude;
                to = up[s].position + (-Vector3.up * 0.75f + fwd * 0.25f).normalized * leg * 0.62f;
            }
            TwoBone(up[s], knee[s], foot[s], to, fwd + Vector3.up * 0.1f);
            foot[s].rotation = footRot[s];
        }

        // the arms to your hands, the elbows down and out
        for (int s = 0; s < 2; s++)
        {
            var h = s == 0 ? vrL : vrR;
            Transform a = body.Bone(s == 0 ? "LeftArm" : "RightArm"), f = body.Bone(s == 0 ? "LeftForeArm" : "RightForeArm"), hand = body.Bone(s == 0 ? "LeftHand" : "RightHand");
            if (h == null || a == null || f == null || hand == null || !h.isActiveAndEnabled) continue;
            var wrist = h.Wrist;
            Vector3 side = s == 0 ? -right : right;
            TwoBone(a, f, hand, wrist.position, -Vector3.up + side * 0.6f - fwd * 0.2f);
            hand.rotation = wrist.rotation;
            hand.localScale = Vector3.zero; // (your hand is drawn in its place)
        }
        head.localScale = Vector3.zero;
        VrGunHold();
    }

    // a, b, c: a limb's three joints; c is put at `target` (as near as it reaches), b bent toward `hint`
    static void TwoBone(Transform a, Transform b, Transform c, Vector3 target, Vector3 hint)
    {
        Vector3 pa = a.position, pb = b.position, pc = c.position;
        float la = (pb - pa).magnitude, lb = (pc - pb).magnitude;
        if (la < 1e-4f || lb < 1e-4f) return;
        Vector3 to = target - pa;
        float d = Mathf.Clamp(to.magnitude, Mathf.Abs(la - lb) + 1e-3f, la + lb - 1e-3f);
        if (to.sqrMagnitude < 1e-8f) return;
        Vector3 dir = to.normalized;
        Vector3 bend = Vector3.ProjectOnPlane(hint, dir);
        if (bend.sqrMagnitude < 1e-6f) bend = Vector3.ProjectOnPlane(pb - pa, dir);
        if (bend.sqrMagnitude < 1e-6f) return;
        bend.Normalize();
        float cosA = Mathf.Clamp((la * la + d * d - lb * lb) / (2f * la * d), -1f, 1f);
        Vector3 wantB = pa + dir * (la * cosA) + bend * (la * Mathf.Sqrt(1f - cosA * cosA));
        a.rotation = Quaternion.FromToRotation(pb - pa, wantB - pa) * a.rotation;
        pb = b.position;
        pc = c.position;
        Vector3 wantC = pa + dir * d;
        b.rotation = Quaternion.FromToRotation(pc - pb, wantC - pb) * b.rotation;
    }

    // what the feet stand on and the hands hold: the map and its things (not the hero's mover, the menu, the sky)
    static int VrWorldMask() => ~((1 << 2) | (1 << 29) | (1 << 30) | (1 << kUiLayer));

    // ------------------------------------------------------------------ climbing
    bool vrClimbNow;          // a hand holds the world this frame
    Vector3 vrClimbVel;       // the body's speed from the pulls (a throw over a ledge when you let go)

    // the hand's fist closed by a wall (or anything of the map): where it holds, in that thing's own frame
    bool VrClimbGrab(VrHand h, out Transform on, out Vector3 local)
    {
        on = null;
        local = Vector3.zero;
        Vector3 p = h.transform.position;
        float best = 0.09f;
        foreach (var c in Physics.OverlapSphere(p, 0.09f, VrWorldMask(), QueryTriggerInteraction.Ignore))
        {
            if (c == null || c.gameObject == h.gameObject) continue;
            var rb = c.attachedRigidbody;
            if (rb != null && (!rb.isKinematic || rb.GetComponent<VrHand>() != null)) continue; // (a loose thing, a hand)
            if (heroCC != null && c == heroCC) continue;
            if (c.transform.root.name.Contains("ragdoll")) continue; // (a person's body: that's a grab, not a climb)
            // (a map's mesh: the sphere is on its triangles already; ClosestPoint is only for convex shapes)
            float d = c is MeshCollider mc && !mc.convex ? 0.01f : (c.ClosestPoint(p) - p).magnitude;
            if (d < best) { best = d; on = c.transform; }
        }
        if (on == null) return false;
        local = on.InverseTransformPoint(p);
        return true;
    }

    // (VrUpdate, after the controllers are read) each holding hand's pull: the body goes the other way
    void VrClimb(float dt)
    {
        if (VrMantleStep(dt)) { vrClimbNow = true; vrClimbVel = Vector3.zero; return; }
        Vector3 pull = Vector3.zero;
        int n = 0;
        foreach (var h in new[] { vrL, vrR })
        {
            if (h == null || !h.Climbing) continue;
            if (h.ClimbOn == null) { h.LetGoWorld(); continue; }
            pull += h.ClimbOn.TransformPoint(h.ClimbLocal) - h.Target.position;
            n++;
        }
        bool was = vrClimbNow;
        vrClimbNow = n > 0;
        if (!vrClimbNow)
        {
            if (was && heroOn && heroMode == 0 && vrClimbVel.y > 1.2f)
            {
                // let go pulling up: a little throw (over the ledge)
                heroGround = false;
                heroVy = Mathf.Min(vrClimbVel.y, 4.5f);
                heroJumped = true;
                heroFallFrom = heroPos.y;
            }
            vrClimbVel = Vector3.zero;
            return;
        }
        pull /= n;
        if (pull.magnitude > 0.6f) pull = pull.normalized * 0.6f; // (a frame's pull: not a teleport)
        if (dt > 0f) vrClimbVel = Vector3.Lerp(vrClimbVel, pull / dt, 0.3f);
        if (heroOn && hero != null && heroMode == 0 && !riding)
        {
            var cc = HeroMover();
            PlaceMover();
            Vector3 at = cc.transform.position;
            cc.Move(pull);
            Vector3 moved = cc.transform.position - at;
            heroPos += moved;
            // your eyes go with him (else this frame's room-scale step took a sideways pull back)
            if (vrRig != null) vrRig.position += new Vector3(moved.x, 0f, moved.z);
            heroVy = 0f;
            heroGround = true;
            heroAir = 0f;
            heroFallFrom = heroPos.y;
            heroVel = Vector3.zero;
        }
        else if (!heroOn && vrRig != null) vrRig.position += pull;
        if (heroOn && heroMode == 0) VrMantleCheck();
    }

    // ------------------------------------------------------------------ people: your hands meet them, your body doesn't
    // (his mover would stop at a ragdoll's parts: they're let through, a few times a second, as they come near)
    readonly HashSet<Collider> vrPassed = new HashSet<Collider>();
    float vrPassAt;

    void VrNotIntoPeople()
    {
        if (heroCC == null || Time.time < vrPassAt) return;
        vrPassAt = Time.time + 0.2f;
        if (vrPassed.Count > 2000) vrPassed.RemoveWhere(c => c == null);
        foreach (var c in Physics.OverlapSphere(heroCC.transform.position + Vector3.up * 0.9f, 2.5f, ~0, QueryTriggerInteraction.Ignore))
        {
            if (c == null || c == heroCC || vrPassed.Contains(c)) continue;
            var rb = c.attachedRigidbody;
            if (rb == null || rb.GetComponent<VrHand>() != null) continue;
            if (!c.transform.root.name.Contains("ragdoll") && rb.GetComponentInParent<Mannequin>() == null) continue;
            Physics.IgnoreCollision(heroCC, c, true);
            vrPassed.Add(c);
        }
    }

    // ------------------------------------------------------------------ up onto a ledge
    // Hanging on and pulled up till your hand is at your chest or lower: if there's a top to stand on just past it,
    // he goes up onto it (a half second: up, then over), your hands let go.
    float vrMantle = -1f;
    Vector3 vrMantleFrom, vrMantleTo;

    void VrMantleCheck()
    {
        if (vrMantle >= 0f || cam == null) return;
        foreach (var h in new[] { vrL, vrR })
        {
            if (h == null || !h.Climbing || h.ClimbOn == null) continue;
            Vector3 a = h.ClimbOn.TransformPoint(h.ClimbLocal);
            if (a.y > cam.transform.position.y - 0.3f) continue;
            Vector3 way = new Vector3(a.x - heroPos.x, 0f, a.z - heroPos.z);
            if (way.sqrMagnitude < 0.01f) way = new Vector3(cam.transform.forward.x, 0f, cam.transform.forward.z);
            way.Normalize();
            foreach (float ahead in new[] { 0.3f, 0.5f, 0.15f })
            {
                Vector3 from = a + way * ahead + Vector3.up * 0.6f;
                if (!Physics.Raycast(from, Vector3.down, out var top, 1.2f, VrWorldMask(), QueryTriggerInteraction.Ignore)) continue;
                if (top.normal.y < 0.6f || top.point.y < heroPos.y + 0.2f || top.point.y > a.y + 0.35f) continue;
                if (top.collider.attachedRigidbody != null && !top.collider.attachedRigidbody.isKinematic) continue;
                // room for him up there
                Vector3 feet = top.point + Vector3.up * 0.05f;
                bool blocked = false;
                foreach (var c in Physics.OverlapCapsule(feet + Vector3.up * 0.3f, feet + Vector3.up * 1.5f, 0.22f, VrWorldMask(), QueryTriggerInteraction.Ignore))
                {
                    var rb = c.attachedRigidbody;
                    if (rb != null && (rb.GetComponent<VrHand>() != null || !rb.isKinematic || c.transform.root.name.Contains("ragdoll"))) continue;
                    if (heroCC != null && c == heroCC) continue;
                    blocked = true;
                    break;
                }
                if (blocked) continue;
                vrMantle = 0f;
                vrMantleFrom = heroPos;
                vrMantleTo = feet;
                if (vrL != null) vrL.LetGoWorld();
                if (vrR != null) vrR.LetGoWorld();
                Play(Sfx.Land, feet, 0.5f);
                return;
            }
        }
    }

    // (VrUpdate) the way up, while it lasts: true while he's on it
    bool VrMantleStep(float dt)
    {
        if (vrMantle < 0f) return false;
        if (!heroOn || heroMode != 0) { vrMantle = -1f; return false; }
        vrMantle += dt / 0.55f;
        float t = Mathf.Clamp01(vrMantle);
        float upT = Mathf.Clamp01(t / 0.55f), overT = Mathf.Clamp01((t - 0.35f) / 0.65f);
        Vector3 p = new Vector3(Mathf.Lerp(vrMantleFrom.x, vrMantleTo.x, Mathf.SmoothStep(0f, 1f, overT)),
            Mathf.Lerp(vrMantleFrom.y, vrMantleTo.y + 0.05f, Mathf.SmoothStep(0f, 1f, upT)),
            Mathf.Lerp(vrMantleFrom.z, vrMantleTo.z, Mathf.SmoothStep(0f, 1f, overT)));
        Vector3 moved = p - heroPos;
        heroPos = p;
        PlaceMover();
        if (vrRig != null) vrRig.position += new Vector3(moved.x, 0f, moved.z);
        heroVel = Vector3.zero;
        heroVy = 0f;
        heroGround = true;
        heroAir = 0f;
        heroFallFrom = heroPos.y;
        if (t >= 1f) { vrMantle = -1f; heroPos = vrMantleTo; PlaceMover(); return false; }
        return true;
    }

    // ------------------------------------------------------------------ the hands' fingers
    // A hand cut from the hero's model with its own copy of his finger bones (in their bind pose), so they bend.
    public class VrFingers
    {
        public SkinnedMeshRenderer Smr;
        public Transform[] Bone;
        public Quaternion[] Rest;
        public Vector3[] Axis;     // each bone's bend, in its own frame
        public int[] Group;        // 0 thumb, 1 index, 2 the other three
        public float[] Max;        // degrees
        public float Thumb, Index, Rest3;

        public void Pose(float thumb, float index, float rest, float dt)
        {
            Thumb = Mathf.MoveTowards(Thumb, thumb, dt * 9f);
            Index = Mathf.MoveTowards(Index, index, dt * 9f);
            Rest3 = Mathf.MoveTowards(Rest3, rest, dt * 9f);
            for (int i = 0; i < Bone.Length; i++)
            {
                if (Bone[i] == null || Group[i] < 0) continue;
                float k = Group[i] == 0 ? Thumb : Group[i] == 1 ? Index : Rest3;
                Bone[i].localRotation = Rest[i] * Quaternion.AngleAxis(Max[i] * k, Axis[i]);
            }
        }
    }

    static readonly System.Text.RegularExpressions.Regex kThumb = new System.Text.RegularExpressions.Regex(@"thumb|finger0(\d|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    static readonly System.Text.RegularExpressions.Regex kIndex = new System.Text.RegularExpressions.Regex(@"index|finger1(\d|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}
