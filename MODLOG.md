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
