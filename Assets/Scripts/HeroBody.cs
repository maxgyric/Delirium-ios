// The hero's body against ragdolls (the iPhone's game): while he's on his feet his mover is also a box for
// Delirium, so a ragdoll he walks into is pushed (and one lying in his way is shoved aside, not climbed: his mover
// passes through the ragdolls' parts, as in VR). In VR (the Quest) only your hands meet people: not this.
using System;
using UnityEngine;

public partial class Game
{
    bool heroBoxOn;
    Transform heroBoxOf;
    Delirium.Ragdoll.DeliriumRagdolls heroBoxWith;

    void HeroBodyBox()
    {
        bool want = Application.platform == RuntimePlatform.IPhonePlayer && !vrOn && running && nm != null && heroOn && hero != null
            && heroMode == 0 && !hero.Rising && !hero.Broken && !riding && !heroNoclip && heroCC != null && heroCC.enabled;
        if (want && heroBoxOn && heroBoxWith == nm && heroBoxOf == heroCC.transform)
        {
            VrNotIntoPeople();
            return;
        }
        if (!want && !heroBoxOn) return;
        if (heroBoxOn && heroBoxWith != null && heroBoxOf != null)
            try { heroBoxWith.UnregisterCar(heroBoxOf); } catch (Exception) { }
        heroBoxOn = false;
        if (!want) return;
        try
        {
            // his size (a little narrower than his mover: he's pushing with his body, not his capsule's skin)
            nm.RegisterMover(heroCC.transform, Delirium.Ragdoll.DeliriumRagdolls.MoverKind.Object, new Vector3(0.42f, 1.7f, 0.42f), true, false, false, new Vector3(0f, 0.88f, 0f));
            heroBoxOn = true;
            heroBoxWith = nm;
            heroBoxOf = heroCC.transform;
        }
        catch (Exception e) { Debug.LogWarning("[Sandbox] the hero's body box: " + e.Message); }
    }
}
