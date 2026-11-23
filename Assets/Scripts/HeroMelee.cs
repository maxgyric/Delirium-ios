// The hero's fists, GTA V's way (Grand Theft Gmod's melee: GtaMelee.cs reads its moves). Punch picks a blow for the
// man in reach as the addon does: by how far he is and which way (ahead, to a side, behind), his facing (a takedown
// from the front or the back, now and then), how hurt he is (a finisher), whether he's down (a stamp or a kick);
// none near, a swing at the air (standing, walking, running). Pressed again during a blow, the next one follows it
// (the blow's chain: GTA V's combos). The blow is its whole clip, moving him by its own way, homing in on the man
// till it strikes (turned onto him, closing or opening the distance at the blow's own pace); it lands where its
// fists or feet really are (the clip's strike points against his body's parts), and Delirium takes the man.
using System.Collections.Generic;
using UnityEngine;

public partial class Game
{
    GtaAnim.MeleeMove mel;          // the blow being thrown (null: none)
    Man melTarget;
    float melT, melYaw, melEnd, melStrikeFrom, melStrikeTo, melReach;
    bool melQueued, melWasFiring;
    readonly HashSet<Man> melHit = new HashSet<Man>();

    bool MeleeReady => GtaAnim.V && GtaAnim.Melee && heroGround && !heroNoclip && !climbing && !riding;

    // Punch pressed
    void MeleePress()
    {
        if (mel != null)
        {
            // the next blow (once this one's swing is under way)
            if (melT / mel.C.Dur > 0.15f) melQueued = true;
            return;
        }
        MeleeStart(null);
    }

    // a blow for the man in reach (`chain`: only from its list)
    bool MeleeStart(string[] chain)
    {
        var t = MeleeTarget(out float d, out float a, out float facing, out bool down);
        Vector3 flat = new Vector3(heroVel.x, 0f, heroVel.z);
        float speed = flat.magnitude;
        float walk = GtaAnim.Move[1].Ok ? GtaAnim.Move[1].Speed : 1.5f, run = GtaAnim.Move[2].Ok ? GtaAnim.Move[2].Speed : 3.6f;
        bool running = speed > walk + (run - walk) * 0.5f;
        GtaAnim.MeleeMove pick = null;
        if (t != null)
        {
            var list = new List<GtaAnim.MeleeMove>();
            if (down)
            {
                foreach (var m in GtaAnim.MeleeGround)
                    if (m.Ground == "low" && Fits(m, d, a, facing, running)) list.Add(m);
            }
            else if (chain != null)
            {
                foreach (var id in chain)
                {
                    var m = GtaAnim.MeleeById(id);
                    if (m != null && Fits(m, d, a, facing, running) && !TooHurtFor(m, t)) list.Add(m);
                }
            }
            else
            {
                bool rollTakedown = Random.value < 0.05f;
                foreach (var m in GtaAnim.MeleeTargeted)
                {
                    if (m.Heavy) continue;
                    if (m.Roll == "takedown" && !rollTakedown) continue;
                    if (m.Ko != null && t.Health > 60f) continue; // (a finisher: only on a man nearly done)
                    if (Fits(m, d, a, facing, running)) list.Add(m);
                }
            }
            pick = Choose(list);
            // nothing for exactly where he is: the nearest-angled blow, homing does the rest
            if (pick == null && chain == null && !down && d < 4.5f)
            {
                float best = float.MaxValue;
                foreach (var m in GtaAnim.MeleeTargeted)
                {
                    if (m.Heavy || m.Roll != null || m.Ko != null || m.Branch == null && m.Prio < 300) continue;
                    float off = a < m.AMin ? m.AMin - a : a > m.AMax ? a - m.AMax : 0f;
                    float far = d < m.DMin ? m.DMin - d : d > m.DMax ? d - m.DMax : 0f;
                    float score = off / 45f + far;
                    if (score < best) { best = score; pick = m; }
                }
            }
        }
        if (pick == null && chain != null) return false;
        if (pick == null)
        {
            // no one: a swing at the air, as he's going
            string k = speed < walk * 0.5f ? "still" : running ? "run" : "walk";
            if (!GtaAnim.MeleeNoTarget.TryGetValue(k, out pick)) return false;
            t = null;
        }
        mel = pick;
        melTarget = t;
        melT = 0f;
        melYaw = heroYaw;
        melQueued = false;
        melHit.Clear();
        float b = GtaAnim.Mark(pick.C, 3);
        melEnd = b > 0.2f ? b : 1f;
        var s = pick.C.Strike;
        float last = Mathf.Max(1f, pick.C.Frames - 1);
        melStrikeFrom = s != null ? s[0] / last : 0.3f;
        melStrikeTo = s != null ? (s[0] + s[1] - 1f) / last : 0.45f;
        melReach = MeleeReach(pick);
        heroNextFire = Time.time + 0.1f;
        act = null;
        Play(Sfx.Jump, heroPos + Vector3.up * 1.3f, 0.4f); // the swing's air
        return true;
    }

    // how far ahead of his feet the blow's fist (or foot) gets, at its strike's middle: he homes in to stand that far
    // (less the man's half-width) from the man
    static float MeleeReach(GtaAnim.MeleeMove m)
    {
        var s = m.C.Strike;
        if (s == null || m.Sb == null || !GtaAnim.MeleeStrikes.TryGetValue(m.Sb, out var set)) return m.Kill ? 0.75f : 0.8f;
        int n = (int)s[1];
        float reach = 0f;
        for (int f = 0; f < n; f++)
        {
            int at = 2 + f * 30;
            if (at + 30 > s.Length) break;
            foreach (var pr in set)
            {
                if (pr.point < 1 || pr.point > 10) continue;
                float x = s[at + (pr.point - 1) * 3];
                if (x > reach) reach = x;
            }
        }
        return Mathf.Clamp(reach, 0.3f, 1.6f);
    }

    static bool Fits(GtaAnim.MeleeMove m, float d, float a, float facing, bool running)
    {
        if (d < m.DMin || d > m.DMax || a < m.AMin || a > m.AMax) return false;
        if (m.NoRun && running) return false;
        if (m.Run && !running) return false;
        if (m.HasFacing && (facing < m.Facing.x || facing > m.Facing.y)) return false;
        return true;
    }

    static bool TooHurtFor(GtaAnim.MeleeMove m, Man t) => m.Ko != null && t.Health > 60f;

    // the highest priority among them; equals: by their bias
    static GtaAnim.MeleeMove Choose(List<GtaAnim.MeleeMove> list)
    {
        if (list.Count == 0) return null;
        int top = int.MinValue;
        foreach (var m in list) top = Mathf.Max(top, m.Prio);
        float sum = 0f;
        foreach (var m in list) if (m.Prio == top) sum += Mathf.Max(0.01f, m.Bias);
        float r = Random.value * sum;
        foreach (var m in list)
        {
            if (m.Prio != top) continue;
            r -= Mathf.Max(0.01f, m.Bias);
            if (r <= 0f) return m;
        }
        return list[list.Count - 1];
    }

    // the man to hit: in 4 m, the one nearest and most ahead (the addon's score); `a` the way to him from his facing
    // (degrees, left +), `facing` how far the man faces away from him (0 at him, 180 his back to him), `down` lying
    Man MeleeTarget(out float d, out float a, out float facing, out bool down)
    {
        d = a = facing = 0f;
        down = false;
        Man best = null;
        float bestScore = 0f;
        Vector3 fwd = Quaternion.Euler(0f, heroYaw, 0f) * Vector3.forward;
        Vector3 stick = Quaternion.Euler(0f, hCamYaw, 0f) * new Vector3(hStick.x, 0f, hStick.y);
        Vector3 refDir = stick.sqrMagnitude > 0.02f ? stick.normalized : fwd;
        foreach (var m in men)
        {
            if (m.Hero || m.Body == null || m.Rising) continue;
            var st = nm.State(m.Body.transform);
            bool lying = st.Ragdolled && st.Fallen;
            if (m.Killed && !lying) continue;
            Vector3 p = lying ? m.Body.Hips.position : m.Body.transform.position;
            Vector3 to = p - heroPos;
            if (Mathf.Abs(to.y) > (lying ? 1f : 1.4f)) continue;
            to.y = 0f;
            float dist = to.magnitude;
            if (dist > 4f) continue;
            float ang = Vector3.Angle(refDir, to);
            if (ang > 75f && !(lying && dist < 1.25f) && dist > 1.2f) continue;
            float score = (1f - dist / 4f) + 0.333f * (1f - ang / 180f);
            if (best == null || score > bestScore)
            {
                best = m;
                bestScore = score;
                d = dist;
                a = -Vector3.SignedAngle(fwd, to, Vector3.up); // (left +, as the addon measures)
                down = lying;
                Vector3 his = m.Body.transform.forward;
                facing = Vector3.Angle(new Vector3(his.x, 0f, his.z), -to);
            }
        }
        return best;
    }

    // ------------------------------------------------------------------ each frame (Walk, in place of the moves)
    bool MeleeAct(float dt)
    {
        if (mel == null) return false;
        if (!MeleeReady || hJumpReq) { mel = null; return false; }
        var c = mel.C;
        float ph0 = melT / c.Dur;
        melT += dt;
        float ph = melT / c.Dur;
        bool pushed = hStick.sqrMagnitude > 0.03f;
        // the next blow in the chain, once this one has struck; the stick after it: back to walking
        if (ph > melStrikeTo + 0.04f)
        {
            if (melQueued)
            {
                var chain = mel.Branch != null && GtaAnim.MeleeBranches.TryGetValue(mel.Branch, out var ids) ? ids : null;
                var was = mel;
                mel = null;
                if (!MeleeStart(chain) && !MeleeStart(null)) { mel = null; return false; }
                if (mel == was && chain == null) { } // (the same again: fine)
                return MeleeAct(0f);
            }
            if (pushed && ph > melStrikeTo + 0.15f) { mel = null; actNextAt = Time.time + 0.1f; return false; }
        }
        if (ph >= melEnd) { mel = null; actNextAt = Time.time + 0.2f; heroVel = Vector3.zero; return false; }
        // its own way, turned by the homing
        var m = GtaAnim.Motion(c, ph);
        var m0 = GtaAnim.Motion(c, ph0);
        Vector3 step = Quaternion.Euler(0f, melYaw, 0f) * new Vector3(-(m.y - m0.y), 0f, m.x - m0.x);
        if (melTarget != null && melTarget.Body != null && ph < melStrikeFrom)
        {
            // homing: turned onto him, and at the blow's distance from him when it strikes
            Vector3 to = (nm.State(melTarget.Body.transform).Ragdolled ? melTarget.Body.Hips.position : melTarget.Body.transform.position) - heroPos;
            to.y = 0f;
            float dist = to.magnitude;
            if (dist > 0.05f)
            {
                float wantYaw = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg;
                // (the clip's own turn from here to its strike: he faces the man as it lands)
                float turnAtStrike = GtaAnim.Motion(c, melStrikeFrom).z;
                melYaw = Mathf.MoveTowardsAngle(melYaw, wantYaw + turnAtStrike, 540f * dt);
                Vector3 own = GtaAnim.Motion(c, melStrikeFrom) - m;
                float ownFwd = own.x; // (its own way on to the strike, roughly toward him)
                // (his body's front: the fist lands just inside it)
                float extra = dist - ownFwd - (melReach + 0.08f);
                float left = Mathf.Max(dt, (melStrikeFrom - ph) * c.Dur);
                float move = Mathf.Clamp(extra * Mathf.Clamp01(dt / left), -mel.HomSpeed * dt, mel.HomSpeed * dt);
                move = Mathf.Clamp(move, -2.5f * dt, 5f * dt);
                step += to / dist * move;
            }
        }
        heroVel = dt > 1e-4f ? Vector3.ClampMagnitude(step / dt, 9f) : Vector3.zero;
        heroYaw = melYaw - m.z;
        heroYawVel = 0f;
        // the strike: its points, frame by frame through its window, against everyone's body
        if (ph >= melStrikeFrom - 0.01f && ph <= melStrikeTo + 0.02f) MeleeStrike(ph0, ph);
        return true;
    }

    void MeleeStrike(float ph0, float ph)
    {
        var c = mel.C;
        var s = c.Strike;
        if (s == null || mel.Sb == null && !mel.Kill) { MeleeStrikeNear(); return; }
        int f0 = (int)s[0], n = (int)s[1];
        float last = Mathf.Max(1f, c.Frames - 1);
        int a = Mathf.Clamp(Mathf.FloorToInt(ph0 * last) - f0, 0, n - 1), b = Mathf.Clamp(Mathf.CeilToInt(ph * last) - f0, 0, n - 1);
        (int point, float r)[] set;
        if (mel.Sb == null || !GtaAnim.MeleeStrikes.TryGetValue(mel.Sb, out set)) set = new[] { (1, 0.12f), (4, 0.12f) }; // (a takedown: either hand)
        var rot = Quaternion.Euler(0f, heroYaw, 0f);
        Vector3 fwd = rot * Vector3.forward, left = rot * Vector3.left;
        for (int f = a; f <= b; f++)
        {
            int at = 2 + f * 30;
            if (at + 30 > s.Length) break;
            foreach (var pr in set)
            {
                int point = pr.point;
                float r = pr.r;
                int k = at + (point - 1) * 3;
                if (point < 1 || point > 10) continue;
                Vector3 w = heroPos + fwd * s[k] + left * s[k + 1] + Vector3.up * s[k + 2];
                foreach (var m in men)
                {
                    if (m.Hero || m.Body == null || melHit.Contains(m)) continue;
                    if (MeleeTouches(m, w, r + 0.08f, out Vector3 on)) MeleeLands(m, on, fwd);
                }
            }
        }
    }

    // (a blow without its strike data) the man in front, once, at its middle
    void MeleeStrikeNear()
    {
        if (melTarget == null || melHit.Contains(melTarget) || melTarget.Body == null) return;
        Vector3 fwd = Quaternion.Euler(0f, heroYaw, 0f) * Vector3.forward;
        Vector3 to = melTarget.Body.Hips.position - heroPos;
        to.y = 0f;
        if (to.magnitude < mel.HomDist + 0.6f) MeleeLands(melTarget, melTarget.Body.Bone("Spine3")?.position ?? melTarget.Body.Hips.position, fwd);
    }

    // a point within r of a part of his (the parts as FxWounds' capsules)
    static bool MeleeTouches(Man m, Vector3 p, float r, out Vector3 on)
    {
        on = p;
        var body = m.Body;
        float best = float.MaxValue;
        foreach (var part in kParts)
        {
            Transform a = body.Bone(part.a), b = body.Bone(part.b);
            if (a == null || b == null) continue;
            Vector3 q = ClosestOnSegment(a.position, b.position, p);
            float d = (q - p).magnitude - part.r;
            if (d < best) { best = d; on = q + (p - q).normalized * part.r; }
        }
        var head = body.Bone("Head");
        if (head != null)
        {
            float d = (head.position + Vector3.up * 0.08f - p).magnitude - 0.11f;
            if (d < best) { best = d; on = head.position + Vector3.up * 0.08f; }
        }
        return best < r;
    }

    static Vector3 ClosestOnSegment(Vector3 a, Vector3 b, Vector3 p)
    {
        Vector3 u = b - a;
        float uu = Vector3.Dot(u, u);
        if (uu < 1e-8f) return a;
        return a + u * Mathf.Clamp01(Vector3.Dot(p - a, u) / uu);
    }

    // it lands: Delirium takes him (the blow's push, stronger for a finisher or a takedown), it hurts him
    void MeleeLands(Man m, Vector3 at, Vector3 fwd)
    {
        melHit.Add(m);
        var owner = m.Body.transform;
        bool big = mel.Ko != null || mel.Kill;
        float force = big ? 85f : 25f + mel.Dmg * 0.8f;
        Vector3 dir = (fwd + Vector3.up * (mel.Ground != null ? -0.2f : 0.15f)).normalized;
        nm.MeleeHit(owner, at, dir * force, hero.Body.Bone("Head").position);
        Attacked(m);
        Play(Sfx.Punch, at, big ? 1f : 0.8f);
        if (Random.value < (big ? 0.8f : 0.35f)) FxBlood(at, fwd, big ? 0.6f : 0.3f);
        if (!Immortal(m))
        {
            m.Health = Mathf.Max(0f, m.Health - (mel.Kill ? 1000f : mel.Dmg * 2f));
            if (m.Health <= 0f && !m.Killed)
            {
                nm.Kill(owner);
                m.Killed = true;
            }
        }
    }
}
