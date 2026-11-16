// The sandbox's sounds, made in code (no sound files): steps, a jump, a landing, a body hitting something, each
// gun's shot, a punch, a blast. Played flat (2D) through a few sources, quieter with distance from the camera.
// GTA IV's own take their place once your game's audio files are read (GtaSounds.cs).
using System;
using Delirium.Ragdoll;
using UnityEngine;

public partial class Game
{
    enum Sfx { Step, Jump, Land, Impact, Punch, Boom, Pistol, Deagle, Smg, Ak47, Shotgun, Sniper, Count }

    AudioClip[] sfx;
    AudioSource[] voices;
    int voice;
    float volume = 1f;
    const int kRate = 44100;

    void SoundsInit()
    {
        if (sfx != null) return;
        var rng = new System.Random(7);
        sfx = new AudioClip[(int)Sfx.Count];
        //                         seconds  thump Hz  thump  noise  noise's decay  noise's cut  thump's decay
        sfx[(int)Sfx.Step] = Make("step", rng, 0.09f, 95f, 0.5f, 0.45f, 0.018f, 0.25f, 0.03f);
        sfx[(int)Sfx.Land] = Make("land", rng, 0.22f, 70f, 0.9f, 0.5f, 0.03f, 0.18f, 0.06f);
        sfx[(int)Sfx.Impact] = Make("impact", rng, 0.28f, 58f, 1f, 0.55f, 0.035f, 0.14f, 0.08f);
        sfx[(int)Sfx.Punch] = Make("punch", rng, 0.12f, 130f, 0.7f, 0.8f, 0.02f, 0.4f, 0.03f);
        sfx[(int)Sfx.Boom] = Make("boom", rng, 1.4f, 42f, 1f, 1f, 0.3f, 0.06f, 0.35f);
        sfx[(int)Sfx.Pistol] = Make("pistol", rng, 0.3f, 170f, 0.6f, 1f, 0.035f, 0.75f, 0.04f);
        sfx[(int)Sfx.Deagle] = Make("deagle", rng, 0.42f, 120f, 0.9f, 1f, 0.055f, 0.6f, 0.06f);
        sfx[(int)Sfx.Smg] = Make("smg", rng, 0.18f, 200f, 0.5f, 0.9f, 0.022f, 0.85f, 0.025f);
        sfx[(int)Sfx.Ak47] = Make("ak47", rng, 0.3f, 130f, 0.8f, 1f, 0.04f, 0.65f, 0.045f);
        sfx[(int)Sfx.Shotgun] = Make("shotgun", rng, 0.7f, 80f, 1f, 1f, 0.11f, 0.4f, 0.1f);
        sfx[(int)Sfx.Sniper] = Make("sniper", rng, 0.9f, 95f, 1f, 1f, 0.09f, 0.55f, 0.16f);
        sfx[(int)Sfx.Jump] = Whoosh("jump", rng, 0.2f);
        var go = new GameObject("Sounds");
        DontDestroyOnLoad(go);
        voices = new AudioSource[32]; // a GTA IV gunshot is half a dozen waves at once
        for (int i = 0; i < voices.Length; i++)
        {
            voices[i] = go.AddComponent<AudioSource>();
            voices[i].playOnAwake = false;
            voices[i].spatialBlend = 0f;
        }
    }

    // A knock: a low thump sliding down in pitch under a burst of noise (the noise's cut: 1 bright, 0.05 dull).
    static AudioClip Make(string name, System.Random rng, float seconds, float hz, float thump, float noise, float noiseDecay, float cut, float thumpDecay)
    {
        int n = (int)(seconds * kRate);
        var data = new float[n];
        float low = 0f, phase = 0f, peak = 1e-6f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)kRate;
            low += cut * ((float)(rng.NextDouble() * 2.0 - 1.0) - low); // one-pole low pass
            phase += 2f * Mathf.PI * hz * (0.45f + 0.55f * Mathf.Exp(-t / (thumpDecay * 1.5f))) / kRate;
            float v = thump * Mathf.Sin(phase) * Mathf.Exp(-t / thumpDecay) + noise * low * Mathf.Exp(-t / noiseDecay);
            v *= Mathf.Clamp01(i / 40f) * Mathf.Clamp01((n - i) / 400f); // no click at either end
            data[i] = v;
            peak = Mathf.Max(peak, Mathf.Abs(v));
        }
        for (int i = 0; i < n; i++) data[i] *= 0.9f / peak;
        var clip = AudioClip.Create(name, n, 1, kRate, false);
        clip.SetData(data, 0);
        return clip;
    }

    // Air: soft noise swelling and fading.
    static AudioClip Whoosh(string name, System.Random rng, float seconds)
    {
        int n = (int)(seconds * kRate);
        var data = new float[n];
        float low = 0f;
        for (int i = 0; i < n; i++)
        {
            float k = i / (float)n;
            low += (0.08f + 0.25f * k) * ((float)(rng.NextDouble() * 2.0 - 1.0) - low);
            data[i] = low * Mathf.Sin(Mathf.PI * k) * 1.6f;
        }
        var clip = AudioClip.Create(name, n, 1, kRate, false);
        clip.SetData(data, 0);
        return clip;
    }

    void Play(Sfx what, Vector3 at, float loud = 1f)
    {
        if (sfx == null || cam == null || volume <= 0f) return;
        float far = Vector3.Distance(cam.transform.position, at);
        float v = volume * loud / (1f + far * 0.09f);
        if (v < 0.01f) return;
        var s = voices[voice];
        voice = (voice + 1) % voices.Length;
        // a little off in pitch each time; lower in slow motion
        float pitch = UnityEngine.Random.Range(0.96f, 1.04f) * (Time.timeScale < 0.99f ? Mathf.Lerp(0.55f, 1f, Time.timeScale) : 1f);
        if (PlayGta(what, v, pitch)) return; // the game's own, when your GTA IV's audio files are there
        s.pitch = pitch * UnityEngine.Random.Range(0.96f, 1.04f);
        s.PlayOneShot(sfx[(int)what], Mathf.Clamp01(v));
    }

    void PlayGun(Vector3 at)
    {
        Sfx s;
        switch (kGuns[weapon % kGuns.Length].Name)
        {
            case "pistol": s = Sfx.Pistol; break;
            case "deagle": s = Sfx.Deagle; break;
            case "smg": s = Sfx.Smg; break;
            case "ak47": s = Sfx.Ak47; break;
            case "shotgun": s = Sfx.Shotgun; break;
            default: s = Sfx.Sniper; break;
        }
        Play(s, at, 1f);
    }

    // A ragdoll's body meeting the world (not its own parts: an arm swinging onto its chest in the air is no hit),
    // from its parts' own speeds: a step is a foot coming down while he's on his feet (staggering, balancing); a
    // hit is a part that was off the world a moment landing on it fast. Not every touch: rolling and sliding keep
    // parts flickering on and off the ground, so a part sounds only after a fifth of a second off it, and the body
    // at most every quarter second, louder the faster the part came in.
    void Contacts(Man m, DeliriumRagdolls.DollState st)
    {
        var now = st.Contacts;
        if (now == null || st.PartSpeed == null) return;
        if (m.Touch == null || m.Touch.Length != now.Length || m.TouchedAt == null)
        {
            m.Touch = (int[])now.Clone(); // the first look: nothing new
            m.TouchedAt = new float[now.Length];
            for (int k = 0; k < now.Length; k++) m.TouchedAt[k] = Time.time;
            return;
        }
        bool upright = !st.Fallen && (st.Balancing || st.Stance.HasValue);
        float hit = 0f;
        Vector3 at = m.Body.Hips.position;
        for (int k = 0; k < now.Length; k++)
        {
            bool on = now[k] != 0, was = m.Touch[k] != 0;
            float away = Time.time - m.TouchedAt[k];
            if (on) m.TouchedAt[k] = Time.time;
            m.Touch[k] = now[k];
            if (!on || was) continue;
            float speed = k < st.PartSpeed.Length ? st.PartSpeed[k] : 0f;
            bool foot = k == 3 || k == 6;
            if (foot && upright)
            {
                if (away > 0.12f && Time.time - m.StepAt > 0.16f && speed > 0.3f)
                {
                    Play(Sfx.Step, at, Mathf.Clamp(speed / 3f, 0.35f, 0.8f));
                    m.StepAt = Time.time;
                }
                continue;
            }
            if (away > 0.2f && speed > 2.2f) hit = Mathf.Max(hit, speed);
        }
        if (hit > 0f && Time.time - m.SfxAt > 0.25f)
        {
            Play(Sfx.Impact, at, Mathf.Clamp((hit - 1.5f) / 6f, 0.2f, 1f));
            m.SfxAt = Time.time;
        }
    }
}
