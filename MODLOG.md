# Delirium Sandbox: journal

Goal: a Unity game for iOS (iPhone 15 Pro, iOS 27) on Delirium's ragdoll, a sandbox with a Hero mode later. No Mac:
built by GitHub Actions (repo Delirium-ios, private), installed with Sideloadly / AltStore. Personal use; the game's
files (default.xex, the three exported files, the archives) are picked by the launcher and never in the repo.

## Stage 1: the sandbox on PC (Unity 6000.3.28f1, built-in render pipeline, old Input Manager, IMGUI)
- Delirium's Unity package 0.7.0 embedded in Packages/ (from delirium-physics-0.7.0/crates/delirium-sdk/bindings/unity)
  with two changes: `Native.Dll` is "__Internal" on iOS (a static library), FindObjectsOfType -> FindObjectsByType.
  delirium_physics.dll (x64) from delirium-unreal-plugin-0.7.0-windows; its 183 nm_ exports match Delirium.cs.
- The package wants a skinned mesh whose bones it knows by name, under an object with an Animator. Mannequin.cs makes
  one in code: Mixamo's names (checked against nm_rig_part_for_name on the Linux build: Hips 0 ... RightHand 20; the
  toes -1), identity bone rotations, T-pose bind, capsules weighted to one bone each (winding checked numerically:
  (B-A)x(C-A) outward = Unity's front).
- DeliriumRagdolls reads its fields in Awake: made on an inactive object, fields set, then activated. It disables
  itself when it can't start; the launcher shows the last error logged.
- No animations yet: a man stands because he's ragdolled with bodyBalance from the start. Down for 1.6 s and still
  (LyingFor > 0.5, or down 4.5 s and slow: get_lying didn't always go still in SA): nm.GetUp(owner, 0.001), then our
  own blend (1.25 s) of the hips' world pose and every bone's local rotation into the standing pose, then ragdolled
  again. GTA IV's get-up clips come with Hero (delirium_anim).
- Not compiled by Unity here (no editor): Game.cs and Mannequin.cs type-checked with mcs against hand-written stubs
  of the Unity calls they use, so no local functions or foreach deconstruction in them.

## Next
- Stage 2: GitHub Actions: cargo build --target aarch64-apple-ios (staticlib; crate-type needs "staticlib"), Unity
  iOS build (game-ci, a Unity licence in the repo's secrets), xcodebuild unsigned .ipa. The launcher on iOS: a
  document picker for default.xex and the three files, copied into the app's Documents.
- Stage 3: touch polish. Stage 4: Hero (delirium_anim for iOS, the game's anim archives on the phone).

## Stage 1b: the menu (after the Playroom's side panel)
- User: stage 1 works in the editor ("круто"); wants the Playroom's menu. Read delirium-room/src/main.rs: NAV = Scene,
  Character, Reactions, Parameters, Interact, Graphics, Audio/Video, Inspect, Help; its reaction buttons are console
  lines ("bodyBalance start=true", "upperBodyFlinch start=true pos=...") which the Unity package takes as
  nm.React(owner, line) (vectors in NM space: Unity's x, z, y).
- Menu.cs (partial class Game; IMGUI GUILayout panel on the right, "Menu" in the top bar): Scene (pause, speed,
  ice floor through the colliders' PhysicsMaterial + everyone re-taken, people), Character (next / reset here / reset
  pose, the 13 bodies via SetBody, strength, jump / dive / tase, sever with the limb's bone scaled to zero, get-up,
  unlimited health, weak muscles, head shots), Reactions (the Playroom's 20 + STOP all, step limit, fire reaction,
  dive when shot at, the 9 extra reactions as SetReactions bits), Parameters (DeliriumRagdolls.Messages(), search,
  changed only, SetOverride for everyone, reset), Interact (tool, push strength, weapons, blast, impact tests: pushes,
  3 m and 12 m drops, explosion), Inspect (State), Console (any message line, a log), Help.
- The man you control = the last one clicked (the camera follows him). Each ragdoll is a new NM body, so the
  menu's per-body settings are put on again (Outfit) every time he's taken.
- The Playroom's tunables for Drunk / Lean / Stay upright aren't in the SDK: leanAmount 0.12 / 0.3 here, defaults else.
- Not here yet: thrown objects, taxis, stages, graphics tabs, replay, lines tied to the map, the IMGUI panel's
  finger scrolling (stage 3).

## Stage 1c: Hero (GTA IV's clips), sticks, drunk
- User: "add hero" with GTA IV's animations, a Drunk button, sticks, Jump / Aim / Drunk buttons; in that mode only
  Reset and Menu stay (the gun is picked in the menu).
- GtaAnim.cs: P/Invoke of delirium_anim (the CS Delirium library, 64-bit build: build-anim.bat ->
  Assets/Plugins/x86_64). It is given the mannequin's skeleton under GTA's names (Hips = Pelvis, LeftUpLeg = "L Thigh",
  ...; its key() strips spaces) in its right-handed Z-up space (Unity's y and z swapped; rotations (-x, -z, -y, w)),
  and returns poses facing +X: on the bones through Euler(0, yaw - 90, 0). The mannequin got a Spine3 (GTA has four
  spine bones; Delirium maps Spine3 to part 10). Loaded on a thread from the exe's folder, else the Playroom's copy.
- Hero.cs: the CS hero's logic again: Gait() (walk / run blended by speed, strides synced), strafes in 8 sectors
  while aiming, jump (take off / in air / land / on the spot), the gun's aim (holster_2_aim's last frame) on the
  upper body bent with the camera's pitch, 0.2 s cross-fades. He moves kinematically (capsule cast + ground ray).
  Mode 0 clips, 1 knocked (Delirium till he's up; a fall faster than 8.5 m/s is a high fall), 2 drunk (a balancing
  ragdoll: lean + a push on the hips toward the stick up to 1.6 m/s, armsOutOnPush overrides off; nm.Jump).
- Getting up: GTA IV's get_up / get_up_back (get_up_fast, da_pose_root) for every man when the clips are loaded
  (the yaw and place fitted as in SA Delirium's StartClip), else the plain blend.
- Still unverified in Unity: all of the above was type-checked with mcs against stubs only.

## Stage 1d: the hero's first run (user's list of 8)
- delirium_anim built (after build-anim.bat learnt to find Visual Studio: vcvars64; the user's VS 2026 needed the C++
  workload). The hero walks on GTA IV's clips.
- Drunk did nothing: the package's SetOverride after ClearOverrides throws (Overridden[owner] is null, TryGetValue
  true, list.RemoveAll on null). Patched in our copy (|| list == null).
- "Aim leaves the camera in the air" and the guns "unnamed, not working": DeliriumRagdolls.WeaponNames() returns
  nulls (its Str() helper expects the length back; nm_weapon_get returns 0), so weapons[i].ToLowerInvariant() threw
  in HeroPose and LateUpdate never reached the camera. Now our own table (kGuns: pistol, deagle, smg, ak47, shotgun,
  sniper) loaded with DeliriumRagdolls.LoadWeapons, names and labels ours; the shotgun through nm.Shotgun.
- A man dropped from 12 m hung in the air: the package's Ground() casts 5 m down, else takes his lowest bone as the
  ground and adds a floor there. 500 m in our copy.
- Right (look) stick removed: the camera is a drag anywhere. Sprint button (hold; Left Shift): walk / run / sprint are
  three gaits each on its own clip (a light stick walks, a full one runs), no speed blend between them.
- A standing jump's landing: jump_on_spot now plays out from where it was held (it was cut at 30%: a jerk into idle).
- Reactions and console lines on the hero: he goes Drunk for them (a ragdoll you steer) instead of "knocked".
- Gun models: boxes parented to RightHand (gun space +X barrel, +Y top; on the hand rotated Euler(90,0,0): assumes
  the hand bone's +Z is up while aiming: to check on a screenshot). A muzzle flash; fire rate per gun.

## Stage 1e: the user's second list (9)
- Reactions / console lines as the hero go to every other man, never to him.
- Drunk on a stair's top walked on an invisible floor: the package always adds a 100 m floor at the ground under
  him. In our copy: only when no level triangles were read; the level is read within 80 m (was 15).
- Drunk in a jump threw him far: the push through the hips (heroVel * 35). Now the hero has a Rigidbody (kinematic,
  no gravity) made non-kinematic with his velocity just before nm.Ragdoll: the package starts the ragdoll with it.
- Long guns: move_rifle / jump_rifle sets while one is in hand; an aim clip per gun (handgun, deagle, mp5k, ak47,
  shotgun, rifle).
- The gun's barrel: set in the world every frame (LookRotation): at the camera's far point while aiming, along the
  forearm otherwise. No dependence on the hand bone's roll.
- Arms half out at rest (screenshot: ~23 degrees below level): the fitted body takes the bind pose as its own, and
  ours was a T. Mannequin.ArmDrop = the game man's own upper-arm angle (nm_build_part_frame 14 -> 15 of the loaded
  build), the arm's joints and capsules swung down by it in the bind pose. A guess to confirm on a screenshot.
- Fire: its button only (top left) or F; a touch on empty screen only turns the camera.
- Jumps: a standing jump is jump_on_spot from start to end with him on the ground (the clip lifts him itself; it
  was also lifted by our physics: a jerk at the landing). A moving jump is the in-air clip alone; the take-off and
  landing clips (they carry him along the ground: a slide and a snap back, and a forward landing while he backed
  away aiming) aren't played.

## Stage 1f: sounds, gun push, unlimited health, drunk in the air
- User: stage 1e all good. Then: Drunk pressed in a jump hit "something invisible" or fell through the ground; the
  shotgun threw a man "a kilometre"; unlimited health didn't hold for NPCs; wants sounds.
- Drunk in the air now waits for the landing (a ragdoll made mid-air starts bodyBalance on nothing). Through the
  ground: since 1e the level's triangles were the only ground (two huge ones for the 80 m box); the package got
  DeliriumRagdolls.SandboxFloor (0 here): a floor at that height under a read level, not at his feet.
- Shotgun: nm_agent_task_shotgun pushes with the table's push for EVERY pellet that strikes (16 x 90 N s). kGuns
  now carries Set / Push / the row's rest; pushes roughly halved (pistol 16, deagle 30, smg 10, ak47 20, sniper 55),
  the shotgun 5 a pellet; "Gun push" slider reloads the table. The camera's kick more than halved.
- Unlimited health: a head shot still killed (the SDK's own rule) and m.Killed followed State.Dead. With it on,
  nm_set_headshot_kills(0) and Shoot() never marks him killed.
- Sounds.cs: all synthesised (a pitch-sliding thump + low-passed noise burst per sound; AudioClip.Create), 12 flat
  AudioSources, volume by distance from the camera, lower pitch in slow motion. Hero: a step twice a gait cycle,
  jump, landing, his gun. Ragdolls: State.Contacts going 0 -> non-zero: a foot while up is a step, anything else an
  impact (louder with speed). Punch, blast, the sandbox's Shoot tool too. "Sound" slider in Scene.

## Stage 1g: fists, the rules made visible, a skin
- Body picker removed from the menu (Strength stays, under "Muscles").
- Ice on the ground did nothing: the SandboxFloor plane (1f) lay on the ground with the plain material. The package
  got SandboxFloorSurface (the ground's collider while ice is on): the floor is then a quad of that material.
- Fists: kGuns[0] (no table row, AimClip -1; the pistol is now index 1 and the default). Fire is "Punch" with them,
  no Aim. GtaAnim.FindPunches lists the game's melee dictionaries (da_dicts "melee", da_dict_clips) into the
  Console and takes up to 4 clips whose names hold punch / jab / cross / hook / uppercut; played on the upper body.
  None found: a procedural jab (right arm to straight ahead and back). The blow lands at 40% of the swing on the
  nearest man in front (nm.MeleeHit 85 N s), the hero turned to him.
- Rules / extra reactions "seemed not to work": the flags were set, but their triggers were missing. Now: shots
  call nm.NearMiss on everyone they pass (the flinch, and the dive with "Dive when shot at"); the hero is the
  attacker (nm.SetAttacker) of whoever he shoots or punches ("Look at who hit him"); "smartFall when dropped" sends
  smartFall on Drop him; "Curl up before getting up" is ours (bodyFoetal for a while, half the time; the SDK's own
  hangs on its get-up timer we don't use); toggling Step limit restarts the balance of everyone standing. Hints under
  each say how to see it.
- Ui.cs: one skin made in code (rounded stretchable textures): dark translucent buttons, accent for the chosen
  one, a panel, chips for the status, box switches instead of IMGUI's toggles, thick slider thumbs and scroll bar,
  the launcher on a card. The menu panel scrolls under a finger (a mostly vertical drag).

- 1g follow-up: a shot man pointed an arm at where the hero had stood: SetAttacker(armed: true) is the SDK's gun threat, and a snapshot. Now only with "Look at who hit him" on, unarmed, refreshed every 0.2 s for 2 s, then cleared (SetAttacker null).
- The pointing was still there: it was nm.NearMiss (1g), not the attacker: upperBodyFlinch + headLook alwaysLook at where the bullet passed, never stopped. Near misses now only with "Dive when shot at" on, and the flinch and look are stopped after 1 s.

## Stage 1h: your own characters and maps from .glb files
- User: models rigged at Mixamo for the hero and for the NPCs, and maps with the model's own collision, picked
  from files on the phone.
- No package for it: Glb.cs is our own reader (plain C#; JSON to dictionaries, accessors to arrays; tested here
  under mono on a hand-made skinned .glb: interleaved views, ubyte joints, normalized weights, mat4, an image).
  GlbScene.cs builds the objects (X mirrored: points (-x,y,z), rotations (x,-y,-z,w), matrices S M S, winding
  turned, v = 1 - v), skinned meshes with their bones and bind poses, base colour + texture on clones of the
  sandbox's own Standard material (a shader the build has; glTFast's shaders would need shipping).
  Not read: .gltf with outside files, Draco / meshopt, sparse accessors, KTX2; alpha is opaque.
- A model becomes a Mannequin (MannequinModel.cs): the same 23 bones found by name (mixamorig: prefix or none;
  Delirium's own nm_rig_part_for_name takes them too: checked), dummies for Spine3 and missing toes. Prepare():
  faced +Z (left leg on the left), scaled to a man if it isn't one (cm files), feet on the ground, limbs laid along
  the mannequin's own lines (the reference pose: arms at ArmDrop), every skinned mesh re-bound to that pose by
  skinning it by hand (Delirium fits its body to the bind pose). The clips, worked out for the mannequin, go on as
  world rotations: bone = turn * pose.R * Ref (Ref: the bone's rotation in the reference pose), hips' travel x Scale.
- Maps: every mesh gets a MeshCollider; the old boxes go; DeliriumRagdolls.SandboxFloor under the map's lowest point,
  SandboxLevelRadius 30 m (the package read 80 m round each new ragdoll); the men at a node named Spawn, else on top
  of the map's middle; x10 / /10 scale buttons (and /100 by itself for a map thousands of units big).
- Files: a folder (Application.persistentDataPath by default; on a PC any path typed), listed in the menu's Files
  tab with Hero / Everyone else / Map on each .glb; the last choice of each is loaded again at the start.
- Stage 2 must add to Info.plist: UIFileSharingEnabled and LSSupportsOpeningDocumentsInPlace, so the app's folder
  shows in Files ("On My iPhone") and the user can copy default.xex, the three data files, the anim archives and
  the .glb files there. No native file picker is needed.
- Not run in Unity: type-checked against stubs only. The parts most likely to need a look: the mirror of skinned
  meshes, Rebind, the clips on a model whose bones have their own axes.
- 1h bug: the first man stood half a metre up from the start: HomeOf cast a ray at boxes made in the same frame (to the physics still unit cubes at the origin). SyncTransforms after BuildLevel; HomeOf only casts on a loaded map.
- A model worked ("a bit crooked"): glossy (now _Glossiness 0.05, _Metallic 0), the gun grew (it was under the hand bone, which carries the model's scale: now under the root), and it "can't enter the ragdoll" (reason unknown: the Console isn't visible from here). Game.StartLog: sandbox-log.txt beside the project (persistentDataPath in a build) with every warning / error and the [Delirium] [NM] [Hero] [Sandbox] lines, to read it ourselves.
- sandbox-log.txt: a model isn't taken: "nm_build_fit: joints not finite, or a joint on its parent's" (a plain Mixamo skeleton). Suspect: the package's Bind() inverts the meshes' bind poses (every scale of the model's tree in them). The package got SandboxBind (a delegate): a loaded model answers with its reference pose (captured unscaled at FromTemplate). On failure the bind positions are logged (BindReport).
- Drunk across a big map fell through it away from where he became a ragdoll: the package reads the level once, round the ragdoll's first place (SandboxLevelRadius, 30 m on a map). Doll.SandboxFollow (the package; world no longer readonly): once the hips are half the radius from where its world was read, a new world is read round them (Level + the floor, factored out as Floor()), the old one let go (destroyed with its last ragdoll). Called each frame before the steps (synchronous: safe to swap).

## Stage 1i: maps that look like Sketchfab's
- User: a Minecraft village map looks good on Sketchfab, dull in the game (screenshots: darker, blurred texels,
  leaves solid, hard shadows). Asked whether Gemini's fixes (URP SSAO, Point filter, smoothness 0, alpha clipping,
  Linear + tonemapping) can be done on the phone: they're all ours to do in code, once, for every map.
- Assets/Shaders/DeliriumLit.shader (built-in surface shader, Lambert, addshadow): texture x colour x vertex colour,
  clip(a - _Cutoff), [_Cull]. Resources/DeliriumLit.mat made by ProjectSetup (ships the shader). GlbScene: COLOR_0
  read into mesh.colors (white without), alphaMode MASK at alphaCutoff / BLEND at 0.5 / OPAQUE 0, doubleSided ->
  _Cull 0.
- Pixel textures: Point filter, no aniso; on by itself when most of the map's textures are <= 256 px; a switch in Files.
- Light: Trilight ambient (sky 0.78/0.84/0.95, equator 0.66/0.68/0.66, ground 0.42/0.40/0.36), sun 1.05, shadow
  strength 0.55. PlayerSettings.colorSpace = Linear (ProjectSetup).
- Not done: SSAO / ACES (the Post Processing package; heavy on a phone): later, as a switch, if wanted.
- Shader not compiled here (no Unity): to check in the editor first.
- Ragdolled high in the air (a long fall on the clips), he flew and jumped about: SandboxFollow measured from the world's ground spot, so a ragdoll made 15+ m above the ground re-read its level every frame of the fall. Now from where its hips were when the level was read (Doll.readAt).
- Noclip for the hero (top bar, or N): flies through everything along the camera's look, Jump = Up and Drunk = Down while it's on (Space / Ctrl or C), Sprint x3; off: he falls from there. Getting ragdolled turns it off. (The fall-from-height bug isn't fixed: readAt didn't do it; still open.)
- Falling: any ragdoll (the hero drunk or knocked, a thrown NPC) dropping faster than 5.5 m/s with no ground within 2.5 m under the hips gets GTA IV's highFall once (nm.HighFall, player for the hero), till he stops coming down; the drunk steering pauses meanwhile.
- sandbox-log: on the 647k-triangle village map each ragdoll start and each "level read again" took ~1 s (the package copied and moved every vertex of the map's meshes at each read): hitches in which the falling ragdoll seemed to jump. Doll.Cached: a mesh collider's world triangles once (again if moved or rescaled), in 8 m cells (bigger ones in a list); Level reads only the cells within reach. Warmed as the map loads (SandboxWarm).
- 1i follow-up: Unity refused to compile: the cache's loop variable "tc" clashed with the terrain case's pattern variable "tc" in the same switch (CS0136; mono's parser can't read the package, so it wasn't caught here). Renamed. A model hero Delirium refuses is no longer asked again every frame of a fall (heroRefused).
- Still red: SandboxWarm called Doll.Warm, but inside DeliriumRagdolls "Doll" is a method (the package itself writes global::Delirium.Ragdoll.Doll there). Fixed. Assets/CompileLog (its own Editor-only asmdef): compiler errors to compile-log.txt beside the project, readable from here once it has loaded once.
- Models refused by Delirium ("a joint on its parent's"): our dummy Spine3, an empty child of Spine2 at its very place, which Delirium maps to its own spine part. No dummy now: Bones[Spine3] is Spine2 itself (the clips' Spine3 rotation lands on it after Spine2's); the aim bends it once.
- More skeletons (Rigs.cs): each skinned bone told by the words in its name (camelCase / _ / . / digits split; noise words like mixamorig, bip, skel, def, j, c dropped; l/left r/right the side), the highest one in the tree per Mixamo bone, the hips else the thighs' parent, the spine = the bones between the hips and the neck (or head): 1-3 of them Spine..Spine2, 4+ the first two and the last two. All renamed to Mixamo's names (which Delirium knows); any other bone Delirium would take for a part renamed auxN. Missing Spine1-3 = the one below, Neck = the head, collarbone = the upper arm. Checked here on 52 names: Mixamo, VRM, ValveBiped, GTA V SKEL_ + RB_/MH_ helpers, Rigify DEF-/ORG-, Unreal, Biped, Unity humanoid. .vrm files listed too.
- The jump on the spot (jump_on_spot, him held in place) removed at the user's ask: every jump leaves the ground, the in-air clip.
- No jump on the spot at all (the user): Jump does nothing unless the stick is pushed (or he is still moving faster than 1 m/s); drunk too.
- The bridge: a commit right after an edit sometimes writes the file as it was before the edit; commit, stage it back, compare, commit again if it differs.
- Lighter maps and models, the same picture: maps static-batched after their colliders (one draw a material; a batched map can't move, so x2 / /2 reads the file again at mapScale, kept in prefs); vertex colours as Color32; OptimizeIndexBuffers; a model's skins with a fixed 5 m box round the hips instead of updateWhenOffscreen (bounds from every vertex every frame); triangle counts by GetIndexCount, not a copy of the array.
- Video (frames looked at): a ragdoll made in a fall dropped through the village's ground and lay on the safety floor under the map; the log: 4 "level read again" in a second. Moving an agent into a new NM world mid-fall threw it about (it keeps the world's triangles in mind between steps). Now one world for good: SandboxFollow adds to it what it hasn't got (given[world]: (collider, cell) pairs; a box or terrain whole, once; a mesh's big triangles once; whole 8 m cells), every 12 m (0.4 R) moved.
- The trace (hips every 0.1 s after a ragdoll is made in a fall): NM speed small, but the hips sank 1-2 m under the village's ground. NM's collision drops a triangle wider than a limit across the ground (collision.rs add_chunk_faces: (hi - lo).truncate().max_element() > kv 73 -> skipped): the map's merged faces (a field as two triangles) weren't there. Level now splits triangles to 4 m across: cells' and boxes' whole (Split), the big list's only the pieces within reach, each piece once a world (SplitNear, givenPieces by its middle to 25 cm). compile-log.txt works (Unity compiled since).
- The user: ragdolls made in the air fall through ground on every map, and in Hello Neighbor (the same Unity package) and CS too, but never in SA. SA Delirium makes its agent at the feet's height now (SABody: feet - 0.08); the package made it at the ground under the hips (nm_agent_create at ground), metres below a falling body. Now at max(ground, the lowest bone).
- Fixed by making the agent at the feet: falls are right ("ideal"). Drunk in the air allowed again (no waiting for the landing). A long fall put him back on his clips at the ground, mid-air: NM still says balancing in free fall. Knocked -> clips and drunk off -> clips now need Landed(): ground within 1.5 m under the hips and not dropping faster than 1.5 m/s.
- Micro-hitches (ragdoll starts, falls): the level read was big (cells 8 m in a +-5 cell cube: 26-38k triangles a read) and made 4 small arrays a triangle. Now: map read radius 14 m (sandbox 20), read again every 0.4 R moved; 4 m cells within R + 12 m in a sphere, each triangle once a world (per-triangle, not per-cell); triangles over 12 m in the big list (pieces near only); boxes by pieces near, once each; Tri writes straight into the lists. Log lines without stack traces (Application.SetStackTraceLogType). The trace lines removed.
- Settings (a tab in the menu and a Settings button on the top bar, kept in prefs): Resolution 40-100% (the main camera into a RenderTexture at the scale, a second camera with cullingMask 0 blitting it in OnRenderImage; IMGUI stays at full size; rays from the screen scaled by ViewRay), Shadows reach 0-150 m (QualitySettings.shadowDistance; 0 turns the sun's shadows off), a frame cap 30 / 60 / 120 (vSync off), Show FPS, Volume (moved from Scene).
- As the hero, a new man (Add one / Add 4 / + Man) stands where the camera looks: the first non-man hit of its ray within 40 m (off a wall 0.5 m back), dropped onto the ground under it, facing the hero; nothing hit means 8 m ahead. Over a map's edge (no ground) he goes to the usual spawn ring.
- Sandbox shooting: an automatic (SMG, AK-47: Gun.Auto, Between < 0.2 s) keeps firing while the click / finger is held, if it started on a man; each shot goes where the pointer is now (a miss is a sound and near misses). Pistols, Deagle, shotgun, sniper: one shot a click as before.
- iPhone build: native/ = delirium-physics 0.7.0 (delirium, delirium-data, delirium-sdk as rlib) + CS-Delirium/anim (rlib) + ios/ (staticlib delirium_ios: ONE Rust static library, since two would each carry a std). blake3 with "pure" (no C, so it builds on Linux). Host build checked: all 193 nm_*/da_* the C# imports are in the archive. Workflow .github/workflows/ios.yml: native (ubuntu, aarch64-apple-ios) -> unity (game-ci v4, ubuntu, BuildIOS.Build, the .a in Assets/Plugins/iOS) -> ipa (macos-15, xcodebuild archive unsigned, Payload zip). BuildIOS: IL2CPP, stripping Minimal, device SDK, iOS 15, landscape; Info.plist: UIFileSharingEnabled, LSSupportsOpeningDocumentsInPlace, CADisableMinimumFrameDurationOnPhone (120 Hz), ITSAppUsesNonExemptEncryption NO. DeliriumRagdolls.cs taken from the PC (Unity's updater: body.linearVelocity).
- Licence: Unity 6 Hub writes only UnityEntitlementLicense.xml (no Unity_lic.ulf), which CI cannot use. The unity job now runs unityci/editor:ubuntu-6000.3.28f1-ios-3 by hand (ci/unity-build.sh): Unity.Licensing.Client --activate-all --include-personal with UNITY_EMAIL / UNITY_PASSWORD, the build, --deactivate-all on exit; cancel-in-progress off so a seat is never left taken.
- First iPhone build OK (run 43301066089, 12 Nov 2026): native ~2 min (aarch64-apple-ios on Linux), Unity ~5 min (Personal seat via the licensing client worked; BuildIOS: Succeeded, 0 errors), Xcode 16.4 on macos-15 ~1 min (ARCHIVE SUCCEEDED, all nm_*/da_* linked). DeliriumSandbox.ipa 13.3 MB. Not yet run on the phone.
- iPhone: "nm_unlock: couldn't get the key that opens default.xex": delirium-data fetches Xenia's xe_xex2_retail_key with `curl`, and an iOS app can't start processes. Now on iOS: config_dir = $HOME/Library/Application Support/delirium (writable), and the game (Game.NeedsKey / FetchKeyThenLaunch) fetches xex_module.cc once with UnityWebRequest, reads the 16 bytes (KeyOf, checked on the real page) and writes xex.key there before nm_unlock.
- UnityWebRequest needs the built-in module com.unity.modules.unitywebrequest (was not in Packages/manifest.json): added.
- Phone fixes: a second finger counted as the right mouse button (Unity's touch-to-mouse) and toggled Aim / orbited: the right button only with no touches. Settings > Buttons size 60-150% (uiZoom times the screen's own scale; applied when the finger lets go). Files: on the phone characters and maps live in Documents/mods (made at start, subfolders searched; loose ones in Documents still listed as "not in mods"); no path box on the phone, a fixed-width one on the PC (a long path widened the menu past the screen, hence the sideways scroll); the menu has no horizontal scroll bar.
- Phone: a Minecraft map came out white (no texture: _MainTex's default). Nothing in the glb/texture code differs between PC and phone, so: the map note now says how many textures loaded and why the first failed one did not (not in the .glb / mime / size / first bytes); a failed LoadImage is retried the plainest way. Also skinWeights Unlimited (the iPhone's Medium quality level bends skins by 2 bones: custom characters).
- Phone: no buttons at all (not even the top strip) — the lower-resolution path: the Present camera's OnRenderImage blit of the view landed over IMGUI on Metal. Present now only clears; OnGUI draws the view texture first (DrawView, Repaint only, identity matrix), the buttons after it.
- Phone: no buttons from the "phone fixes" build on — sandbox-log: every OnGUI "Instantiate: object is null" in BuildSkin (GUI.skin null), with "Could not produce class with ID 115" and "a scripted object has a different serialization layout (Read 40 bytes but expected 8488)" at start: the built-in GUISkin unreadable. The first good builds had no Library cache; the broken ones restored it. Workflow: no Library cache; BuildIOS: stripEngineCode off.
- 13 Nov 2026: confirmed on the iPhone 15 Pro — buttons back (no Library cache, engine code kept), everything works.
- Phone, 13 Nov: "No such file: .../<new container>/Documents/Data/Application/<old container>/Documents/default.xex": iOS gives a reinstalled app a new folder, the saved paths pointed into the old one. Rebase() moves a saved path under this install's Documents. Assets/Plugins/iOS/DeliriumPicker.mm: the Files app's picker (UIDocumentPickerViewController, public.folder / public.item, open mode): Choose folder... in the launcher (both paths to it; a bookmark in NSUserDefaults reopens it each launch); Select model... in Files: .glb / .vrm MOVED into Documents/mods (NSFileCoordinator move; copy + remove where a move is refused). Picker.cs: PathsAtStart, OnPickedFolder, OnPickedModels (UnitySendMessage to "Game").
- 14 Nov 2026: spring bones were unhooked by the folder-picker commit (Models.cs copied over from an older tree): SpringBones.Setup call put back.
- VRChat avatars imported on the phone (Files > Import VRChat avatar): .unitypackage (tar.gz) read by UnityPackage; the avatar = the prefab with a VRC Avatar Descriptor; its menu toggles' effects worked out by running every playable layer's state machines (VrcAvatar). Model: ufbx (Assets/Plugins/iOS/ufbx.c, MIT; fbxblob.c wraps it: meshes in the bind pose, glTF frame, MODIFY_GEOMETRY). Unity's model sub-asset fileIDs are hashed and the .meta lists none (MD4 "Type:Mesh->name0" didn't match): a renderer's mesh = the .fbx mesh named as its object, then by elimination. Skin: the .fbx's inverse bind matrices on the prefab's bones by name (frame differences cancel). Materials: .mat _MainTex/_Color, _Cull 0 = both faces, else Back (the Frenni package's "change Culling to Back"); a material missing from the package: a picture named for it. Humanoid bones renamed to Mixamo's from the .fbx .meta humanDescription. PhysBones -> VRMC_springBone. Writes mods/avatars/<name>.glb + <name>.vrc.json; VrcLook applies toggles per file (saved), parts can be hidden by hand. Tested in the container on Frenni (17 toggles, 346k triangles, 81 PhysBone chains; rendered with three.js: textures, skin, Hair Untied, mic stand right).
- 14 Nov evening: avatars in scenes and nested prefabs (PrefabGraph): the model's prefab made from the .fbx, overrides applied, stripped stand-ins resolved; a package with several avatars (PC / Quest) asks which. Unity's model-prefab GameObject/component fileIDs are NOT xxHash64("Type:X->name0") (only meshes are; tried paths, FBX UIDs, md4/md5/spooky/xxh3): overrides and objects added onto a model's bones (other than its root: GO 919132149155446097, TF -8679921383154817045, Animator 5866666021909216657) aren't matched. Hidden parts let out the body's "<OUTFIT> <piece> ON" tuck shapes. A missing material takes the picture its mesh's other materials use when it's the same size.
- Packages laid out as AssetRipper exports (Mesh/, Texture2D/, AnimationClip/ folders, meshes as .asset, path_0x... clip paths) are avatars extracted from VRChat's cache: not supported.
- 15 Nov: THE material bug: FBX UV v runs up, glTF's down; fbxblob wrote FBX's as is, so every import had its textures upside down on the layout (Ankha's hair took the body's yellow). Fixed (1 - v). Probe (fileids.yml, results on branch probe-results): Unity 6000.3 model prefab IDs for ufbx test models — Mesh = xxHash64("Type:Mesh->name0") confirmed; GameObject/Transform/renderer IDs fit none of: xxh64/xxh3/md4/md5/sha/spooky/murmur of type+name/path (any root prefix/separator), FBX UIDs, parent-ID chaining. A model with one top node: Unity makes it the root.
- 15 Nov: .blend import on the phone (BlendFile: DNA-driven reader, 2.6x-5.x, 4/8-byte pointers, gzip/zstd via Plugins/iOS/blendzstd.c + zstd's single-file decoder; Blender 5 "large BHead" files use made-up addresses unique only within an ID's data: looked up per owning ID block). BlendModel: meshes (5.x attribute storage / CustomData / MPoly-MLoop), UVs, vertex groups -> deform bones (BONE_NO_DEFORM = 1<<12), shape keys, Principled base colour texture (packed or found by name), Rigify DEF- bones re-parented through their ORG- twins / head-on-tail. Tested on generated 5.3 files (bpy 5.3 in a venv) incl. a Rigify human, and assimp's 2.69-2.79 samples (2.4x / 2.5x tessface-only files not read).

## .blend fixes (Phoenix, Hexra) + textures .zip
- DNA padding is counted from the DNA1 block's start, not the file's (Phoenix's DNA1 sat at an odd offset: "byteCount" crash).
- ListBase walk: a typed item whose Link is inside an embedded struct (ModifierData) - next = first pointer of the item. Before, only the first modifier was ever read.
- Mask modifiers (vertex-group mode, on in the viewport) applied: faces with a hidden vertex dropped. Edge-only meshes and pose bones' custom shapes (Auto-Rig Pro cs_*) skipped.
- Bone re-parenting order: own kept parent > head on a kept tail > walk up (ORG- / Auto-Rig Pro c_ twins) > constraint subtargets (Child Of head.x) > head on a kept head holding more children (ARP spine_01.x on root.x).
- Rigs.cs: ".x" is noise; "*twist"/"*stretch" sided halves read as the bone; no neck/head by name -> the spine bones above the shoulders' parent (Rigify DEF-spine.004..006).
- Materials: toon node set-ups (no Principled) - the picture scored by its name (_D, albedo vs _N, mask, ramp...) and where it goes (normal map, separate, ramp factor: data); a colour ramp driven by a data picture gives its mean colour; mix factors aren't followed.
- Picker: several files at once; .zips unpacked each into its own folder (nested .zips too); a .zip with no .blend kept as textures for the next model.

## GTA IV motorbike (stage 1)
- Your files: handling.dat from common.rpf (rpf key found by trial from default.xex), bikes' .xft/.xtd + vehshare.xtd
  from xbox360/models/cdimages/vehicles.img (IMG key = xbox360.rpf's). Bikes: pcj nrg900 sanchez faggio hellfury bobber
  zombieb; anim dicts veh@bike_spt/dirt/scooter/chopper (sit_drive, steer_l/r, steer_fwd/bwd, still, reverse, get_in_ds...).
- native/bike: tex.rs (Xbox fetch constant at header+0x1C: d0 bit31 tiled, pitch bits 22..30 *32; d1 &0x3F format
  18 DXT1 / 20 DXT5 / 6 ARGB, endian (d1>>6)&3, base d1&~0xFFF; d2 size-1; XGAddress2DTiledOffset in blocks), glb.rs
  (bones -> nodes, rigid meshes split by blend index into bone space, fragment children (wheels) in their bone's space,
  GTA (x,y,z) -> glTF (-x,z,y) -> Unity (x,z,y)), sim.rs (Transmission::process, Spring/force, tyre_force verbatim;
  balance: roll purely by a PD to atan(v*yaw/g)*(1+DesLean) - steer*StickLean, k 160 c 24 (gravity m g h / I ~ 45);
  wheel forces' roll torque removed; COM shift LeanFwd/BakCOM; wheelie/stoppie limits; InAirSteer; BrakeStabil yaw damp;
  drag a = drag * v^2), game.rs, lib.rs (db_* C API).
- Headless test (bike/examples/ride.rs, flat plane): settles at x=rest, 0-60 km/h ~3 s, 5 gears, ~126 km/h, lean held
  at 40 deg, wheelie ~55 deg, brakes, reverse when braking at a stop.
- anim: da_pose_seat (pose about the clip's root = the seat, mover added, not re-grounded).
- Bike.cs: spawn (top bar), Ride / Off / E, Gas = Jump's place, Brake = Sprint's, stick steer + lean; rays per wheel;
  push-out of what it hits, > 7 m/s into it or a spill -> rider on Delirium (Knock with the bike's speed), bike on a
  Unity Rigidbody; registered with Delirium as a Car mover. Get-on/off are 0.35 s blends (the clips not used yet).

## Bike stage 2 (user's 7 points)
- NaN: tyre_force divides by the load; a wheel just touching (a step's edge, spawning) had load 0 -> NaN pose ->
  Unity's physics broken for everything (collision gone, teleports, Reset useless). Now the game's own rule (no tyre
  below m g kv51), the step rolled back if anything goes non-finite, and Unity never takes a non-finite pose.
- Stairs launched it (ray on the next step: a huge bump-stop kick): past the travel it's a contact now (lifted out,
  inward speed removed), spring rate clamped to 3 m/s. Box push-outs with an upward normal are ground (no crash, no
  state reset: db_nudge moves pos/vel only).
- Load time: the archive key from the clips' opening (delirium_anim::KEY) - no second default.xex unpack; textures
  decoded only when a mesh uses them (vehshare.xtd has dozens); PNG deflate level 1. Open 4 ms, build ~45 ms on a PC.
- Riding controls: arrows (left/right) on the left, Gas / Brake / lean up / lean down on the right, Off top right; no
  stick. The gun hidden while riding.

## Bike stage 3
- "Knocked" + teleport on Off / crash: Delirium's Car mover puts anyone in its box (moving > 2 m/s) into NM - the rider
  too. The hero's body is now the bike's child while riding (Cars() skips who.IsChildOf(car)); Unseat() on every way off.
- Steering: arrows eased (2.5/s in, 5/s back), lock capped at atan(wheelbase * 0.7 g traction_max / v^2) - within grip,
  no sliding round when going left then right; lean target from the turn only.
- Wheels: a sphere cast (0.7 r) per wheel, the contact at the ball's bottom, edge normals leaned toward up: rolls over
  step edges instead of popping.
- Arrow icons: one texture a direction (GUIUtility.RotateAroundPivot misplaces under the scaled GUI matrix).
- Video (crash into a raised bed at 68 km/h): the ball wheel climbed the 0.6 m wall; ground hits higher than 0.85 r
  above the wheel's lowest point are skipped now (the body's box meets them). The rider flew back with the bike: the
  crash used the speed after the push-out; now velIn * 0.85 + up (over the bars), the bike tumbles nose-down.
- Video 2 (crash into the well at 50 km/h): the rider hung upright in the air beside it. Delirium's bridge Cars()
  returned at once when no car was left, so the worlds kept the last box set (the bike's, where it crashed): an
  invisible box. Cars() now clears the worlds' boxes (nm_world_set_boxes count 0) when the last car goes; the bike is
  unregistered before the rider is ragdolled.
- Video 3 (into the platform at 50 km/h): thrown ~30 m, upright in mid-air. The wreck (a dynamic Rigidbody) became a
  Delirium Object mover at once (MoversNear: loose bodies within 5 m), its box round his legs: NM shoved them out.
  Bridge: NotMovers (a set MoversNear skips); the wreck is in it 1.5 s after a crash. Throw: velIn*0.7 + up 1.5-3.5.
- Video 4: floating ragdoll by the bike lane after a crash; on the bridge, clinging outside the parapet; lying on the
  sea. A new ragdoll reads the level once (static colliders within R, non-kinematic bodies skipped): the bike's
  BoxCollider was still static at the crash (BikeFall came after Knock) and a parked bike was static too, so a copy
  stayed in the world where it was. Bridge: DeliriumRagdolls.SandboxNotLevel (never read as level; the bike's root,
  always); the crash makes the bike a loose body before the rider's ragdoll. The sea: maps' meshes all get colliders,
  water too: solid for ragdolls (left as is: passable water needs swimming or a respawn; asked the user).
- Still floating after a crash (the user: like the old fall-from-height bug). Same cause: Ground() is a ray down from
  the hips; thrown off a bike it hit the bike's box (the seat's height), the agent made at max(that, lowest bone) =
  half a metre above his feet's real ground. Ground() / GroundCollider() skip SandboxNotLevel (and triggers).

## Whole level in Delirium; faster start
- The user: make the ragdolls' collision the map itself (like SA), not reads round each ragdoll. Bridge:
  SandboxWholeLevel(level, floorY): the level's colliders gathered on the main thread (mesh caches, box corners,
  terrain at 2 m), one NM world a ragdoll size built on a ThreadPool thread (triangles split to kPiece, NM axes and
  winding as Level's Tri, the floor under it all as 4 m pieces 60 m past the map - nm add_floor asserts on its size),
  flushed to nm_world_add_mesh in 300k-triangle batches. Dolls of a size whose world is ready go in it (refcounted,
  no Level / Floor / SandboxFollow); before that, the old way. A new level (map, sandbox, ice) retires the worlds:
  freed with their last ragdoll. Called from BuildLevel, PlaceOnMap and the ice switch. NM's collision is a 2D cell
  grid (collision.rs): a whole map in one world only costs memory and the build.
- Start: da_open unpacked default.xex (LZX) and found the archive key by trial (an AES key schedule every 4 bytes
  of the program), then read all of xbox360.rpf for player.xft's skeleton - every start. Now
  <game>/delirium-anim.cache ("DAC1": xex size + mtime, the key, kv::export, the skeleton): the next start reads
  those and only anim.img's table. kv::export / import added to delirium-data. Round trip tested; not run on the
  real files (not here).

## Ragdoll off the bike: invisible steps, hovering, sinking (2026-11-16)
- The video: thrown off at ~50 km/h he went waist-deep into the red path at once, slid along it so, then lay hovering
  by the kerb. The whole-level world changed nothing because both ways of giving the level to NM cut big triangles the
  same broken way: each triangle quartered by its own width (Split / SplitNear), so a cut road triangle met an uncut
  or less-cut neighbour with corners in the middle of its edges (T-junctions). NM (collision.rs tuples) finds a
  triangle's neighbours by shared corners; an edge without one is an outside edge, whose contacts aren't filtered by
  the neighbour's normal (ragdoll.rs world_meet across_ok): sideways pushes all along the cut lines.
- Now Conform (DeliriumRagdolls.cs): an edge is halved iff it is longer than kPiece (x/z) on its own, at its exact
  middle (commutative sums: bit-identical from both sides); 1, 2 or 3 edges cut -> 2, 3 or 4 pieces, recursively.
  Test (scratchpad conf/t.cs, 200 random 5x5 grids of 0.5-40 m quads): every inner edge in exactly two pieces, the
  outer ones once, widest piece 4.0 m. Level's SplitNear prunes pieces out of reach but cuts the same.
- Bike.cs LiftOutOfGround: before the rider is ragdolled, the body lifted by its deepest bone's depth under the
  ground under it (+0.1 m): a leg started under a one-sided surface stays under it.
- Still the same on the phone (16 Nov, 14:18). Looked for why NM's ground could be wrong where the map's isn't:
  - NM resolves a limb sunk into a triangle along the triangle's face and edge normals only (gjk.rs Pen::Tri): a
    triangle facing down pushes it further down. The map's materials are often glTF doubleSided (drawn Cull Off), so
    a road made facing down looks right and was given to NM facing down. A mirrored node (negative scale) turns every
    face of its mesh round in world space too. Cached now orients (Facing): mirrored -> all turned; two-sided
    material -> faces pointing more than 60 degrees down turned up (walls left as made). Boxes in a mirrored space too.
  - SizeKey rounded the ragdoll's size to 0.05: a world built for 1.0 served a ragdoll of 1.02 (2% off, metres far
    from the origin). Now the exact size.
  - Menu > Physics view: NM's ground under a grid of rays round each ragdoll (green up / red down / yellow wall, drawn
    through everything) and its parts (blue, pink touching). New nm_world_raycast_face (the triangle's own face).
- The user: "a plain ragdoll is fine, only off the bike". The cause, at last: rig.rs make ties each bone to its NM
  part by their places in two poses, the rest (bind pose with its hips on the hips now) and the agent as made
  (nm_agent_create: standing, its lowest part's bottom at `pos`). The Doll made the agent at the lowest bone now
  (feetNow). Standing, that is where the bind pose's feet are; seated on the bike the feet are on the pegs ~0.5 m
  under the hips, not a leg's length: every tie got that gap, NM's body ~0.5 m over the drawn one for the whole
  ragdoll: the drawn body sank into the path (waist-deep), and stopped short of walls NM's body met. Now the agent is
  made at the bind pose's own lowest bone (At(b)), whatever pose he's in.

## GTA IV's sounds; bike handling; buttons; the thrown rider's legs (2026-11-16)
- The Xbox audio: audio.rpf (RPF2: config/sounds.dat15, game.dat16, curves...), xbox360/audio/sfx/*.rpf (RPF3, names
  hashed: <joaat(rpf)>/<joaat(bank)>; resident.rpf's 19 banks = joaat of AMB_RESIDENT, COLLISIONS, DOORS,
  ENGINE_RESIDENT, EXPLOSIONS, EXTRAS, FEET_RESIDENT, FRONTEND_GAME/MENU, HELICOPTER, HORNS, MELEE, RAIN,
  SCANNER_RESIDENT, SPEECH_RELATED, TRAIN, WATER, WEAPONS, WHEEL_LOOPS; waveslots.xml lists them). The RPF3s take
  audio.rpf's key (find_by_trial only knows RPF2's root).
- A bank (big-endian): u64 table, u64 ?, u32 count, u32 0, u32 data base; table entries {u64 info offset, u32 wave
  hash, u32 info size}; info {u64 data offset, u32 hash, u32 bytes, u32 samples, i32 loop start (-1 once), u16 rate,
  i16 headroom, then XMA: 0, 0, 56, loop bits.., packets, 0, seek table; or one word: 16-bit BE PCM}. XMA2 in
  2048-byte packets: ffmpeg's xma2 decodes them given XMA2WAVEFORMATEX extradata (34 bytes); counts exact.
- sounds.dat15 (Metadata, v15): 18470 objects with names; type byte, u32, u32 2-bit flags, u32, u16 field mask (bit0
  volume s16 in 1/100 dB, bit2 pitch s16 cents), ... Children by joaat(name), a wave by its hash. Types: 12 simple,
  13 multitrack, 14 randomized, 11 stereo pair, 10 crossfade (first = near), 6 wrapper, 2 envelope, 7 sequential...
  game.dat16 (v16, 2129 objects): W_GLOCK/W_UZI/W_AK47/W_PUMPSHOT/W_SHOTGUN/W_RIFLE (fire @10), PCJ/NRG900/SANCHEZ/
  FAGGIO/BOBBER... (@18 engine low, 22 high, 26/30 exhaust, 34 revs off, 146/150 idle, 206 start, 298 land, 310
  ignition), MELEE_COMBAT_JAB (@14 knuckle, 18 fist), FOOTSTEPS_CONCRETE_CLEAN (@32 LT heel, 40 scuff, 48 land).
- native/sound (delirium-sound, ds_* FFI); ci/xma.c + ci/ffmpeg-ios.sh: FFmpeg n7.1 with only the XMA decoders,
  built on the macOS runner (cached), merged into libdelirium_ios.a before xcodebuild. Checked here: the ffmpeg CLI
  standing in for the decoder (sound/examples/play.rs): 59 waves of 7 sounds decoded, sane levels and shapes.
- Phone files: audio.rpf next to default.xex; xbox360/audio/sfx/resident.rpf, streamed_vehicles.rpf (bike engines).
- Bike: turning at 100 km/h 13 -> 26 deg/s (grip limit 0.7 -> 1.0 g, bars 2.5 -> 4/s), a yaw toward the bars'
  bicycle rate and the sideways speed damped (sim.rs), the rear's spin taking gas off; ground normals eased (a seam's
  kick). Buttons: lean up/down up the right edge over Gas. Thrown rider: the plain knock (no high fall's pedalling).
- (17:17) Ragdoll sounds: a hit in mid-air, staggering heard as hits, too many while rolling. nm_agent_get_part_contact's
  flags count a part touching its own body (ragdoll.rs: self touches |= kv95); new nm_agent_get_part_ground = the
  game's `collided` (touch & kv95 == kv7: the world). Sounds.cs Contacts: per part, a foot coming down while upright
  (balancing / a stance) is a step; any other part that was off the world 0.2 s landing at > 2.2 m/s a hit; a step
  per 0.16 s, a hit per 0.25 s, loudness by the part's speed (DollState.PartSpeed).
- A long fall landed on its feet every time: highFall's pdStrength torque turns the pelvis to land feet first
  (nmhf.rs). Game.Tumble: "highFall start=true pdStrength=0 orientateBodyToFallDirection=false" and a starting spin
  (Doll.Spin: each part's mass times w x r about the centre of mass), 1.5-4.5 rad/s, mostly a somersault.
- (17:44) Health from the world's hits: Game.Impacts, a ragdoll's centre losing > 7 m/s in a frame while touching the
  world: (lost - 7)^1.4 * 12 + 4 off 200 (3 m drop starts hurting, 10 m about all, more kills). Hero's bar under his
  status (HeroGui HealthBar); Menu > Automatic > "Hero can't die" (heroImmortal; "Unlimited health" stays for all).
  Any death (hits, fire, a head shot, the Kill tool): DeliriumRagdolls.Die = dead + stopAllBehaviours + relax at
  stiffness 0 (relaxation 100: only joint limits), once (Man.Limp), till Reset.

## GTA V's movement from Grand Theft Gmod (2026-11-16, evening)
- D:\grandtheftgmod_0899: the addon after its exporter ran on the user's GTA V (clip data: `from = "move_m@generic.ycd"`,
  `exporterVersion = 3`). models/grandtheftgmod/anims/{male,female}_*.mdl (Source MDL v48, 65 bones: SKEL_ROOT at the
  hips, ValveBiped.Bip01_* with Spine..Spine2, Spine4, Neck1, Head1; inches, X forward Z up; all animations inline,
  long ones in 30-frame sections, no movement: in place), lua/grandtheftgmod/clips/lib_male_move*.lua (each clip's
  seq, dur, frames, move, speed in inches/s, a root-motion curve, foot/MoveEvent tags), sh_motion/sh_loco (the
  addon's GTA V locomotion: states IDLE/START/MOVE/STOP/TURN/AIR/LAND..., MoveBlend by mbr, gait, dir, slope).
- native/anim/src/mdl.rs: studiohdr bones (216 B), animdescs (100 B), seqdescs (212 B); mstudioanim_t lists: raw
  Quaternion48/64, Vector48 (half floats), or RLE euler/position tracks (ExtractAnimValue), sections (the last frame
  of a sectioned anim in a section of its own). Checked by plotting walk/run/sprint/idle: right.
- The anim library's second set (STATE_V, clip numbers from 1<<20; dv_open, dv_clip; da_clip_info/sync/pose take
  either): the Source skeleton as GTA IV's names (key(): valvebipedbip01 prefix, spine4->spine3, neck1->neck,
  head1->head), the clips' speeds from the lua (walk 1.74, run 4.74, sprint 7.06 m/s), sync from the way it faces.
  Onto the mannequin (examples/v.rs): stands at 0.97 m, feet on the ground, facing +X, walk/run plotted right.
- GtaAnim.LoadV after GTA IV's clips: Move[0..3] = gtgl_male_move_idle/walk/run/sprint, Jump[1] = fall_med, and a
  jump's take-off held by jump_launch_l (Hero PickClip, heroJumped). Next: starts/stops/turns from the clip data.
- Phone: male_move.mdl and lib_male_move*.lua beside default.xex (or the addon's folder as grandtheftgmod_0899).
- (19:50) GTA V's starts, stops, 180s and its own footsteps. The clip data parsed in full (lua_meta -> Meta: speed,
  dur, curve as x fwd / y left m / turn left deg samples evenly over the clip, heel marks L/R, the blend_out_*
  MoveEvent), dv_clip_data. Hero.VAct: from standing the stick's way picks wstart/rstart {l_0, l_m90, l_m180, r_0,
  r_90, r_180} (its turn bent to the exact angle over the clip), letting go wstop_{l,r}_0 / rstop_{l,r} (the foot
  after the last heel down), pulling back at a run run_turn_180_{l,r}; the clip moves him (its curve through the
  usual capsule slide) to its blend-out mark, then the walk/run loop starts on the same foot (the last heel mark,
  the loop's own heel mark for that side). Footsteps on GTA V clips' heel marks, not at phase 0 / 0.5.
- (21:15) Controls as GTA V's: the stick walks (lighter push slower), Sprint held jogs (the run), Sprint tapped
  sprints (each tap +0.4 to sprintPower, running down 0.55/s; sprint over 0.5, kept down to 0.2). Smoother: speed
  and heading apart (speed eased 4.5/3.2/2.4 m/s2 up by gait, 4-6 down; heading turning 480 deg/s at a walk down to
  150 at a sprint), the facing smoothed (SmoothDampAngle 0.12 s), GTA V's gaits blended by speed (Gait: walk/run/
  sprint pairs, one stride), clip cross-fades 0.3 s.
- (22:18) Shaking when turning hard at a jog: the run 180 was measured from the velocity but turned from the facing,
  so it could end still > 150 deg off and start again (turn after turn). Now from the facing (both > 150), and 0.35 s
  between two actions. Smoother: heading and speed on damped springs (SmoothDampAngle 0.12-0.3 s by speed, max
  520-200 deg/s; SmoothDamp speed 0.28/0.38/0.55 s up by gait, 0.22-0.35 down), facing 0.09 s. Landings: after
  > 0.3 s in the air, GTA V's jump_land_{l,r} running on (act kind 4: its curve, or on at the landing speed) or
  land_stop_{l,r} when stopped, then the loop on the same foot.
- (23:40) GTA V's climbs and vaults, aims and shots. The anim library: the addon's male_guns.mdl read beside
  male_move.mdl (Anim.extra; its tracks put on the first model's bones by name), lib_male_guns*.lua parsed too,
  zcurve (a climb's root height, inches -> m) as dv_clip_data what=2, pose mode 3 / da_pose_ground (the clip about
  the model's ground: no re-grounding, no centring; a climb's last frame then matches its exit's first exactly), a
  strafe's sync from its own way (curve). GtaAnim.Motion/Rise sample the clip data as the addon does (a sample every
  third frame). HeroClimb.cs: Jump in front of a ledge 0.48-2.95 m up runs Grand Theft Gmod's FindLedge (probes at
  0.5/0.91/1.37 m, range 0.61 or 1.12 m by speed, face within 50 deg, the top from above, depth to 1.02 m with a
  drop > 0.46 m: a vault, head room, room at the end) and pick_climb (low: standclimbup_20/80_low or close, at a run
  run_climbup_20/80_{l,r}[_close], vaults standvault_20/80; high: standclimbup_/runclimbup_ bands 80/140_low,
  140_high/180_low, 180_high/220, 220_high/295_low blended by height, the pair whose reach fits best). The climb:
  its curve plus the shift to the wall (smoothed over 30%), its zcurve scaled to the real height, the facing turned
  onto the wall; then clamberpose_stand/walk/run (room 1.0/2.0 m) or standclimbup_exit[_walk] / run_climbup_exit_*
  as act kind 5, or after a vault standvault_exit / run_vault_exit_* / clamberpose_fall* down the far side (its own
  drop, the vaulted top not ground). Aiming: GTA V's strafes (mstrafe_{walk,run}_* 8 ways, mstrafe_idle) for the legs,
  the gun's aim_{high,med,low}_loop blended by the camera's pitch (47 up / 66 down) on the upper body, the raise
  (idle_2_aim_fwd_*) when he starts, each shot its fire_* clip (an automatic's looping while held) plus a kick on a
  spring (pistol 7 deg ... sniper 15). Guns: pistol wpi, deagle wp5, SMG wsm, AK war, shotgun wpu, sniper wsr.
- Phone: male_guns.mdl beside male_move.mdl, and lib_male_guns*.lua beside lib_male_move*.lua.
- (23:27 user) Bugs: no steering in a landing or a start; falling through what he climbed or jumped against; turned
  away from walls (couldn't face one to climb); turning not GTA V-like; wants GTA V's camera. Fixes:
  VAct moves him by the clip's step each frame (not to an absolute track), turned by actOff, which the stick bends
  toward its way (starts, landings, run turns, climb exits that walk/run on; a start pushed > 110 deg back is let
  go). Heading = intent (velocity before the wall slide: heroIntent / heroVelSlid), facing = heading (0.05 s), the
  heading turning at a capped, eased rate (560 deg/s at a walk .. 230 at a sprint, accel-limited), speed off on hard
  turns (to 60% at 160 deg), a lean into turns about his feet (0.3 x the physical lean, max 12 deg). Ground: a 0.2 m
  sphere instead of a ray (a climb ends with his middle a few cm short of the edge: the ray missed the top and he
  dropped), the wall capsule down to 0.3 m in the air (his feet went into a box's top over its edge). Climb probes
  the stick's way. Camera: eased distance/side/height (aim 1.75 m over the shoulder, 3.3-4.1 m by speed), lagged
  pivot (more vertically), recentres behind him while running after 1.2 s untouched, sphere-cast pull-in at once and
  slow let-out, FOV +6 at a sprint, -6 aiming.
- (00:29 user, video) Still walks through the wall at the top of the stairs (Minecraft map) and falls out of the
  world. Cause: Physics casts ignore a collider they start touching, and the map is a few big MeshColliders: on
  the stairs his capsule touched the next step, so the whole map was missed (walls too), and the ground sphere,
  starting inside, missed the ground. Now a CharacterController ("Hero mover", Ignore Raycast layer, in
  SandboxNotLevel so Delirium's world skips it; r 0.25, h 1.7, step 0.5, slope 55): Move each frame (-0.04 down on
  the ground, a 0.55 m step-down probe), teleported (disable/enable) when heroPos is set elsewhere (climbs, resets),
  off while ragdolled / riding / noclip. The vault's drop stays kinematic. The body is drawn on a smoothed height on
  the ground (the controller steps up at once).
- (00:48 user) After climbing something small with a drop behind, the fall clip, a moment's freeze, then a ragdoll.
  The high fall was by speed (vy < -8.5): a vault's kinematic drop ended fast (run_vault_exit drops 3.6 m, its last
  samples ~8 m/s), and any 2.6 m drop did it; the freeze was the switch to Delirium. Now by height fallen (> 4.5 m
  from the top of the fall, heroFallFrom) and the vault's drop handed on at most 5 m/s.
- (02:39 user) Garry's Mod maps in the sandbox (no prop spawn menu). D:\Steam\steamapps\common\GarrysMod: maps
  gm_construct / gm_flatgrass (v20), content in sourceengine/*.vpk (CS:S, HL2) and garrysmod_*.vpk.
  Stage 1 (native/bsp, delirium-bsp in libdelirium_ios.a): the .bsp read (lumps: entities, planes, texdata +
  string table, vertices, nodes, texinfo, faces, leaves (32/56 B by lump version), edges, surfedges, models,
  dispinfo/dispverts), the world and brush entities (not triggers, areaportals, occluders, clips, env_*, rendermode
  10) at their origins, faces without sky/trigger/nodraw/hint/skip flags or tools/ materials (toolsblack kept),
  displacements as (2^p+1)^2 grids from the corner nearest startPosition (row c0->c1, column left->right), the 3D
  skybox left out (sky_camera's leaf's area: its leaves' box), triangles turned to face the plane's normal (glTF
  CCW). Written as a .glb (Source inches -> m, (x, z, -y)): a mesh per material, its reflectivity as the colour,
  UVs in 0..1 by the texture's size, water BLEND + "[nocollide]", glass see-through, a Spawn node at
  info_player_start. gm_construct: 29k triangles, 184 materials, 492 skybox faces out; renders right (pond, hills).
  Sandbox: .bsp in the files list (Map only), converted on use (dbsp_convert -> temporaryCachePath), a "GMod map
  (.bsp)..." button (the same picker, now taking .bsp), no colliders on [nocollide] meshes.
- Stage 2, textures: vpk.rs (v1/v2 dir trees, archives beside, preload bytes), content.rs (the pakfile zip:
  stored/deflate; packs: garrysmod first, then hl2, then the rest; loose folders with "materials"; VMT KeyValues:
  $basetexture, $translucent, $alphatest, "patch" includes; PNG writer: Sub filter, zlib level 3), vtf.rs (7.0-7.5,
  resources in 7.3+, the largest mip <= 512, DXT1/3/5 and 8-bit formats to RGBA). A picture once in the .glb however
  many materials (cubemap patches) use it. gm_construct with garrysmod + hl2_misc + hl2_textures: 181 of 184
  materials textured (glass and water's beneath have none), 16.5 MB, 0.75 s here; renders right. On the phone the
  .glb is cached (temporaryCachePath, keyed by the map's size/time and the packs in mods) and its line kept.
  Packs looked for under Documents/mods (any depth to 4), the map's folder and Documents.
- (03:11 user) GMA. gma.rs: GMAD v1-3 (required content list in v>1, name, description, author, version, the file
  table {index, path, i64 size, crc} till index 0, then the bytes), LZMA "alone" Workshop downloads unpacked
  (lzma-rs). A .gma's first maps/*.bsp converted, the addon's own files searched for textures (after the pakfile).
  Compressed BSP lumps ("LZMA" id, size, packed size, 5 properties) and zip LZMA entries (method 14) read too.
  User's samples: gm_stairs (The Ultimate Ragdoll Testing Map, plain .gma) and gm_falleritus8_2008 (an LZMA Workshop
  cache file): both convert and render right. Sandbox: .gma listed as a GMod map, the picker takes .gma.
- (10:40 user, screenshot of a voxel race map) GMod maps look flat/white, parts and textures missing. Causes: no
  baked light (Source maps are lit by their lightmaps), no material tint ($color: one white texture tinted per
  material on such maps), no static props (trees, flags, lights: the next stage). Done: lightmaps (lump 8, or HDR 53
  when that's all: ColorRGBExp32 -> gamma, half brightness), every face's (style 0; a bumped face's first of four)
  shelf-packed 2048 wide (to 4096 high) with a 1-texel border, faces without one on a grey (= white doubled) block;
  TEXCOORD_1 from the lightmap vectors (clamped inside the face's rect), the picture in the .glb and every material's
  extras.lightmap; GlbScene reads TEXCOORD_1 into uv2 and sets _LightMap/_LightMapOn; Delirium/Lit: emission =
  albedo x lightmap x 2 x 0.85, albedo x 0.2 for the sun and shadows. $color / $color2 ("[r g b]" or "{255 ...}") as
  the baseColorFactor. Checked: gm_stairs' lightmap (2048x511) renders with its baked shadow in place;
  gm_falleritus8 has none (a fullbright map).
- (10:47 user) An addon with several maps ([DESTRUCTION UPDATE] Natural Disaster Survival: 3 maps, 441 materials,
  180 MB): choose among them. gma.rs reads just an uncompressed addon's header and table (files read from it on
  demand, never all in memory); convert takes "x.gma#maps/y.bsp"; dgma_maps lists an addon's maps. Sandbox: each
  map of a multi-map addon is its own entry ("addon.gma › gm_y"), cached per map; removing one removes the addon.
  gm_natural_disaster_survival: 108k triangles, 193/193 materials textured from the addon itself, lightmap; renders
  in colour (red/white barriers, orange and blue buildings, trees are brushes here).
- (11:22 user) gm_nocheese.gma: "No meshes in the file". The map is all models (51 static props, every brush face
  a tool face). Stage 3, models: studio.rs reads a .mdl (v44-49: textures + material folders, skin families, body
  parts -> first model -> meshes), its .vvd (LOD 0 through the fixups) and .vtx (v7; LOD 0; strip groups 25 or 33
  bytes, by which fits; trilists) into triangles with their materials. bsp.rs: the game lump's static props
  ("sprp", flags bit 1 LZMA; v4-11 by their stride; origin, angles, model, skin, solid, v11 scale) and prop_*
  entities with a model; placed with Source's AngleMatrix, oriented by their vertex normals, smooth normals kept,
  non-solid ones "[nocollide]" (batch key flag 1<<24), lit by a block of the map's average luxel (x1.3, at most
  150) when the map has a lightmap. Model materials have 1x1 "size" (their UVs are already 0-1). gm_nocheese: 51
  placed, 78.6k triangles, 62/62 textured; renders right (Bikini Bottom, Krusty Krab). gm_stairs / falleritus
  name HL2/CS:S models (props_c17, de_inferno): found once those packs are in mods/gmod.
- (12:11 user, gm_scrapland) Each map's own skybox and look. sky.rs: worldspawn "skyname" -> skybox/<name>{rt,ft,lf,
  bk,up,dn} (VMT $basetexture or the .vtf), into a 2048x1024 equirectangular panorama in Source's directions. Face
  layout from HL2's sky_day01_01 itself (seams 0.3-1 of 255 apart against 7-106 otherwise): Quake 2's st_to_vec but
  lf = -X, bk = +Y (rt +X, ft -Y); sides half as tall as wide are their upper half, below the horizon the sides'
  last rows' average (and for a placeholder dn). env_fog_controller (fogenable: colour, start, end, max density),
  light_environment ("pitch" negative-down or the angles' pitch, yaw; _light colour and brightness/350; _ambient)
  into "<glb>.env" with "sky=<glb>.sky.png". Water: its $fogcolor as its colour. Sandbox: MapLook.cs applies the
  .env after a GMod map loads (Delirium/Sky panorama skybox from Resources, linear fog scaled with the map, far
  clip to the fog's end within 300..3000 m, sun colour/intensity/direction, trilight ambient) and puts the
  sandbox's own back for any other map. The lightmap texture is kept out of the pixel-art filter.
- D:\gm_scrapland.gma is over the 400 MB staging limit: the user tests it on the phone (or extracts its .bsp with
  gmad for me).
- (12:50 user) FPS drops a little on GMod maps; changing maps many times crashes (memory). Leak: a map's meshes,
  textures and materials (made at run time) were never destroyed with its objects, and Delirium's mesh cache held
  them too. ClearLevel now destroys the map's Owned objects, clears Delirium's mesh cache (SandboxForget), and calls
  Resources.UnloadUnusedAssets + GC.Collect (the static-batched meshes); a replaced character template unloads
  likewise. GMod maps' textures are compressed as they load (Texture2D.Compress: ETC2 on the phone, a quarter to an
  eighth of RGBA32; the lightmap left as is), and a lightmapped GMod map casts no sun shadows (it has its own baked).
- (13:01 user) "Some maps have a model only from one side"; a dusk map is black in the sandbox but lit in GMod.
  3D skybox: its faces and models (inside the sky_camera's area) now drawn round the map at the camera's scale
  ((p - sky_camera) x scale, "scale" or 16), not stood on, lightmapped; gm_construct gets its ring of hills and its
  mast back. The floating building bits were 3D-skybox props placed raw. Exposure: the baked light's average (every
  5th luxel's luminance) brought up to 0.22 (x1-6, never darker), as GMod's HDR eye does; the sun and ambient follow
  (ambient at least 0.45). $nocull materials double-sided. (Faces a map's builder never made, the backs of facades
  you can't reach, stay missing: GMod draws nothing there either.)

## GMod maps: gm_bigcity_dusk fixes, map-change memory (2026-11-17)
- HDR-only maps (no LDR lightmap, lump 8 empty): lightmap offsets come from the HDR faces lump (58), not lump 7.
  Before, bigcity_dusk's faces read luxels from the wrong places (wrong, patchy light).
- Exposure clamp raised 6 -> 12 (bigcity_dusk: mean luxel 0.019, now x11.4).
- Lightmap atlas packed tallest-first (2048x3380 -> 2048x1014 for bigcity_dusk; fewer faces lost to the 4096 cap).
- Scale bug: the 3D skybox (drawn x16 round the town) made MapBounds > 4000 m, so SetMap shrank the map x0.01.
  GMod maps are already in metres: no auto-shrink for them; MapBounds ignores [nocollide] (sky, water).
- Memory: the pakfile is indexed, entries unpacked on demand (not all at once); the .gma's map not copied twice.
  Native peak for bigcity_dusk 328 -> 210 MB.
- Map change: the old map is freed with DestroyImmediate (objects, owned meshes/textures/materials, static-batch
  meshes) BEFORE converting/loading the next. Before, Destroy was deferred and UnloadUnusedAssets async, so both
  maps were in memory at once. A failed load falls back to the default map.
- Log: sandbox-log-previous.txt keeps the run before (the crash's run); map loads log memory; Application.lowMemory
  is logged and unloads unused assets.

## GMod maps: 3D skybox textured and drawn by its own camera (2026-11-17, later)
- Bug: to_glb looked textures up by the material index with NOCOLLIDE stripped, but convert stored them under the
  full key, so every [nocollide] batch (3D skybox, water, non-solid props) lost its texture (flat sun-coloured
  boxes in bigcity_dusk). Now looked up by the full key first.
- 3D skybox batches get SKY3D (1<<25): mesh name "[sky3d] [nocollide]". Unity puts them on layer 29 and a child
  camera ("3D skybox camera", depth -1, far 20000*k) draws only that layer over the 2D sky; the main camera then
  clears depth only and skips layer 29 (Source's order). Before, the main far plane (fog end, ~640 m) cut the
  skybox town (out to 1250 m in bigcity_dusk) and its buildings could cut into the map.
- .env "skyfog=": the sky_camera's fog (distances x its scale). Source's fog caps at fogmaxdensity; Unity's linear
  can't, so the sky camera gets a line through Source's curve at 30% and 100% of the skybox's farthest distance.
  SkyCamera swaps RenderSettings fog in OnPreRender/OnPostRender and turns shadows off while it draws.
- examples/finddbg.rs: material/texture lookup from a .gma's map pakfile (debugging).

## GMod maps: a third of the faces were inside out (2026-11-17, 15:20)
- User: walls of a house missing from outside, there from inside; walks through them. Cause: dface_t.side is just
  planenum & 1 (planes come in pairs, n and its flip n^1; a face's planenum is already its own facing plane).
  The converter flipped the normal again when side == 1, so every face on an odd plane (~32% in bigcity_dusk:
  9468 of 29583) was wound backwards: culled from the front, drawn from behind, and the CharacterController
  passed through it from the front. Normal now taken from planes[planenum] as is. Verified by a Workbench render
  with backface culling at bigcity_dusk's spawn: the missing building walls and curb sides are back.
- The earlier "two-sided colliders" commit (wrong diagnosis) reverted.

## GMod Workshop player models (2026-11-17, 16:10)
- native/bsp/src/player.rs: models(gma) lists an addon's player models from its Lua
  (player_manager.AddValidModel("Name", "path")), else any .mdl with a pelvis and a head that isn't a weapon,
  hands or a prop. convert("addon.gma#models/x.mdl") -> a skinned .glb: studio::Model (now with bone parents and
  poseToBone at mstudiobone_t+96), bind pose = poseToBone inverted (orthonormalized), mesh = LOD 0, first model
  of each body part, default skin; VVD's 3 weights/vertex -> JOINTS_0 (u16) / WEIGHTS_0; textures from the addon
  then the packs ($basetexture, or $iris for eyes). Axes: rows (his left, up, forward) with left = L thigh - R
  thigh levelled, so he faces glTF +Z whatever way the model was built (Prepare only turns him 180).
- FFI: dgma_models (path|name lines), dmdl_convert (dbsp_error for the line). Example: examples/pm.rs.
- Models.cs: an addon's maps AND player models are listed one entry each ("addon.gma#maps/x.bsp",
  "addon.gma#models/y.mdl"), a single-map addon too, shown by their own names ("gm_city  (temp.gma)",
  "Buddy  (player model, x.gma)"). Player models get Hero / Everyone else; cached as <addon>-<model>_*.mdl.glb.
- Verified (Kick the Buddy addon): 6055 tris, 42 bones, 3 textures; Blender: faces +Z, textured, a raised arm
  and a bent knee deform the mesh right.

## Player models: Delirium refused Kick the Buddy (2026-11-17, 16:30)
- Log: "can't ragdoll Hero: nm_build_fit: joints not finite, or a joint on its parent's". Buddy's Spine, Spine1,
  Spine2, Spine4 and Neck1 are all at one point (and each clavicle on its upper arm); nm_build_fit refuses two
  of its 21 joints closer than 1 mm. Rigs.LeaveOutStacked: a spine/neck bone within 3% of the hips-head length
  of the last kept one, a shoulder on its arm, a toe on its foot are left unmapped (renamed aux by part_for_name),
  so nm_rig_joints interpolates the spine and neck between Spine and Head and puts the clavicles 30% from the neck.
  A "Spine" on the hips is replaced by the next spine bone up that's apart.
- DeliriumRagdolls.LateUpdate: a doll whose character was destroyed this frame is skipped (it threw a
  NullReferenceException in Doll.Pose and stopped the other dolls' posing; Prune frees it next frame).

## Models browser (2026-11-17, 17:00)
- Browser.cs: a "Models" button (top bar, and in Files) opens a full-screen browser, GMod spawn-menu style: tabs
  Player models / Maps, search, a grid of pictures with their names (and Hero / Everyone / Map on the ones in
  use), the one touched on the right (a character turning in 3D, drag to turn; a map's picture), with
  Be him (hero) / Everyone else / Load map / Remove.
- Kind: .gma#*.mdl and .vrm characters, .bsp / .gma#maps maps, a .glb by its JSON ("skins": a character).
- Pictures cached in temporaryCachePath/icons/<FNV of entry|size|date>.png. Characters: loaded one at a time
  (0.25 s apart, while the Player models tab is open), Mannequin.Prepare'd, photographed on layer 30
  (PreviewLayer, at y -3000, main camera excludes it) by a disabled camera into an ARGB RenderTexture (clear
  alpha 0), fog off for the shot. Maps: the addon's maps/thumb/<map>.png (native dgma_file), a png beside a
  .bsp, else the main view (sky camera first) 0.8 s after the map first loads (MapIconLater, from UseFile).
- (17:30) The turning 3D preview on the right removed (its buttons didn't fit): name, file, in use, and the
  buttons only. Maps tab froze ~5 s: each map's thumbnail opened its addon (dgma_file; a Workshop .gma is
  LZMA-packed, unpacked whole on open). Now dgma_thumbs unpacks all of an addon's maps/thumb/*.png at once into
  icons/thumbs-<hash>/, on a Task; tiles show "no picture" until it's done, then pick it up.

## Your GMod addons' folder; addons read in the background (2026-11-17, 18:10)
- User: ModShelf downloads GMod mods (.gma) into its folder inside LiveContainer, many subfolders; moving them by
  hand is a pain. "Pick GMod folder..." (Files tab, and the browser's header): a second security-scoped folder
  bookmark (DeliriumPicker.mm: DP_PickGmodFolder / DP_RestoreGmodFolder / DP_ForgetGmodFolder, bookmarks kept per
  key); RefreshFiles adds every .gma and .bsp under it (all subfolders). Shown as "GMod folder/...", never
  removed (Remove says to do it there).
- Addons.cs: .gma listing moved off the main thread: native dgma_list (one open: "M maps/x.bsp" and
  "P models/y.mdl|Name" lines, the maps' thumbnails written on the way) on a worker thread, one addon at a time;
  results cached in temporaryCachePath/addons.txt by path|size|date (next launch: instant). Not-yet-read addons
  aren't listed; the list is rebuilt when all are done (or every 4 s while reading). dgma_maps/dgma_models no
  longer called (kept in the library).

## GMod maps' working parts (entities) (2026-11-17, 21:40)
- Converter: Map.entities_kv (all keys in order, outputs repeated), model_bounds (lump 14). Brush entities that
  move or are told to (is_mover: func_door*, func_button, func_rot_button, func_movelinear, func_tracktrain,
  func_train, func_rotating, func_brush, func_wall_toggle, func_illusionary, func_breakable, func_physbox...) and
  prop_door_rotating / named prop_dynamic get their own batches: key | ent_key(i) ((i+1)<<32; MAT_MASK 0xffffff),
  mesh "... [ent i]". Beside the glb: ".ents" (key\tvalue lines, "#mins"/"#maxs" brush box in Source units
  with origin, "--" after each; i = order) and ".sounds/<path>" (every .wav/.mp3/.ogg value found via content).
  Cache name now *_e1.bsp.glb (old ones re-converted).
- MapEnts.cs: ents read; "[ent i]" meshes grouped under "ent i ..." (pivot at origin), kinematic Rigidbody, their
  colliders not in Delirium's level, not static-batched. I/O queue (delays, times, !self/!activator/wildcards,
  AddOutput). Classes: func_door / func_water_analog / func_button / func_movelinear (move along movedir by
  |size·dir| - lip; wait; toggle; locked), func_door_rotating / prop_door_rotating / func_rot_button (axis flags,
  distance; prop doors open away from the hero), func_rotating, func_tracktrain / func_train + path_track /
  path_corner (OnPass, speed, wait), func_brush / func_wall_toggle (Enable/Disable/Toggle, StartDisabled,
  Solidity), func_illusionary (not solid), ambient_generic (PCM .wav read by hand -> AudioClip; flags 1 everywhere,
  16 start silent, 32 not looped; radius; PlaySound/StopSound/ToggleSound/Volume), logic_auto, logic_relay,
  logic_timer, logic_case (InValue, PickRandom), logic_branch, math_counter, point_teleport, trigger_once /
  multiple / teleport (to its target's place and yaw) / push / hurt (>=100 kills); Kill, FireUserN.
- The hero: a lift or train under him carries him (its matrix change applied to heroPos/yaw); "Use" button
  (yellow, beside Aim; E on a keyboard) when a usable button/door is within 1.6 m of his chest and not behind
  the camera; touch-opening doors and touch buttons on entering their box.
- Not yet: particles (info_particle_system .pcf), env_sprite glows, NPC/vehicle entities, soundscripts
  (named sounds, not files), momentary_rot_button's position, func_physbox physics.
- (22:30) gm_stairs (user's ragdoll test map): func_tanktrain (= tracktrain, added to is_mover; cache _e2) with
  invisible brushes and prop_dynamic cars / a train001 and trigger_hurt parented to it. EntsStart now parents
  first (an entity without meshes gets an empty pivot "ent i ..."), then records home pose and each box in its
  own T space (LocalBox: triggers on trains move and turn with them), then runs Setup (trains jump to their first
  path_track with their children). Tracktrains: placed at node + "height", facing the next node at once, then
  turning toward it at 120 deg/s (orientationtype 0 / flag 16: fixed); facing = R(dir) R(src +x)^-1 home.
  phys_motor: turns attach1 (a func_physbox) about the line origin->"axis" at "speed" deg/s (flag 1 on;
  TurnOn/TurnOff/SetSpeed). trigger_push speed capped at 40 m/s.
