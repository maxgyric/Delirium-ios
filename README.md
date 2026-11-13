# Delirium Sandbox (Unity 6000.3.28f1; Windows editor, iPhone build)

A sandbox on Delirium's ragdoll: mannequins that keep their balance, stagger, fall and get up. Push, shoot, punch,
blast, drag, burn and kill them.

**Nothing of the game is in this project.** Delirium reads its numbers from your own copy: the launcher asks where
your `default.xex` (or `GTAIV.exe`) is and where the three files exported from it are (`character.xrt`,
`build.txt`, `taskparams.txt`: `delirium-room --game "<your game>" --export mod-files`). Never commit them.

## Run it (Windows, Unity Editor)
1. Unity Hub > Add > this folder; open with 6000.3.28f1. The first time it sets itself up (a scene, a material).
2. Press Play. The launcher shows; check the two paths; Start.

## Controls
- Bottom bar: the tool. Click / tap a man to use it on him. Blast: also click the ground.
- Drag: hold a man and move the pointer.
- Empty space: drag to turn the camera (or right mouse button); wheel / pinch zooms.
- Top: Reset, + Man (up to 6), Slow-mo, Gun (with Shoot), Paths (the launcher again next time).

## What's where
- `Assets/Scripts/Game.cs`: the launcher and the sandbox. `Mannequin.cs`: the man, made in code. `SpringBones.cs`: physics bones (VRM spring bones: hair, breasts, skirts) on your own characters.
- `Assets/Editor/ProjectSetup.cs`: the first-open set-up.
- `Packages/com.delirium_physics.bridge`: Delirium's Unity package 0.7.0 (GPL-3.0) with `delirium_physics.dll` (x64).
- `MODLOG.md`: the journal.

## The iPhone build (GitHub Actions, no Mac)
`.github/workflows/ios.yml` runs on every push to `main` (or by hand: Actions > iOS build > Run workflow):
Rust builds `native/` (Delirium 0.7.0's source and delirium_anim, GPL-3.0) into `libdelirium_ios.a` for the phone,
Unity (its Docker image on Linux, `ci/unity-build.sh`) makes the Xcode project, a macOS runner makes an unsigned
`DeliriumSandbox.ipa` (the run's Artifacts). The repo needs two secrets, `UNITY_EMAIL` and `UNITY_PASSWORD`: the
build takes a Unity Personal seat with them and gives it back at the end (Unity 6's Hub makes no .ulf file to use).

Install: Sideloadly on the PC, your Apple ID, the .ipa, the iPhone on the cable. A free Apple ID's app runs 7 days,
then sign it again. On the phone: Settings > Privacy & Security > Developer Mode on (once), and trust your Apple ID
in Settings > General > VPN & Device Management.

The game's files on the phone (Files app > On My iPhone > Delirium Sandbox), the same names and folders:
`default.xex`, `xbox360.rpf`, `xbox360/anim/anim.img` (the Hero's GTA IV moves), and the three exported files
`character.xrt`, `build.txt`, `taskparams.txt`. Your .glb / .vrm characters and maps go in the same folder.
