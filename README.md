# Original Lambda Wars content mount

Native Source 1 resource readers adapted from Facepunch's MIT-licensed `Sandbox.Mounting.Source1` implementation in [sbox-public](https://github.com/Facepunch/sbox-public). The accompanying license applies to the upstream readers and shaders. `LambdaWars*` files add installation discovery, map metadata and BSP scene reconstruction.

This is an s&box engine mount: it reads models, materials, textures, sounds and maps directly from your own Steam copy of [Lambda Wars](https://store.steampowered.com/app/270370/Lambda_Wars/) at runtime. No original game files are included in this repository or its releases, and none are copied to disk.

## Install

1. Install Lambda Wars through Steam (app 270370).
2. Close s&box and the s&box editor.
3. Download `lambda-wars-mount-<version>.zip` from the [latest release](https://github.com/riggs9162/lambda-wars-mount/releases/latest).
4. Extract it into your s&box installation's `mount` folder so you end up with `<sbox>/mount/lambda_wars/lambda_wars.dll` and `<sbox>/mount/lambda_wars/Assets/`. With a default Steam library this is `C:/Program Files (x86)/Steam/steamapps/common/sbox/mount/`.
5. Start s&box. The mount is discovered on startup and appears as `lambda_wars`; its content is addressed as `mount://lambda_wars/<original path>`.

The mount finds the Steam installation automatically. If Lambda Wars lives somewhere Steam does not report, set the `LAMBDA_WARS_GAME_PATH` environment variable to the installation folder before starting s&box.

To update, close s&box and replace the `lambda_wars` folder with the one from the newer release. To uninstall, delete it.

Each release is built against the s&box engine version named in its release notes. An s&box update can change engine APIs; if the mount stops loading after an update, use a newer release or build it yourself.

The map catalog lists the original `hlw_` battlefields and `or_` Overrun maps. Overrun maps also expose their wave spawn points and the map logic that opens doors and enables spawn points. Every map exposes its `color_correction`, `color_correction_volume` and `fog_volume` entities with the logic entities that script them, and each original `.raw` color lookup table is available as a 32x32x32 volume texture at `mount://lambda_wars/<original path>.vtex`.

Original game assets are read from the installed game and are never copied by this project. Settings provides a manual content path accepting either the installation root or its `lambdawars` directory. Applying stages a new file index before refreshing the mount in the menu or lobby; clearing the path restores automatic Steam discovery. The engine needs one restart when installing a new mount DLL, but content paths and map selection do not.

Steam's registered installation is preferred by default. An explicitly saved manual path overrides discovery; `LAMBDA_WARS_GAME_PATH` remains a fallback when no valid installation is otherwise available. Manual configuration is stored locally in `%LOCALAPPDATA%/LambdaWarsSbox/content.json` and is never replicated to other players. Both loose installations with `.dx90.vtx` model companions and Steam VPKs with `.vtx` companions are supported; the original model and texture identifiers stay unchanged.

On Windows, automatic discovery also checks Steam's registry entries read-only, then follows `steamapps/libraryfolders.vdf` and `appmanifest_270370.acf` to the registered game directory. It checks both machine registry views and the current user's Steam path; no registry values are modified.

The mount is a separately installed engine extension. Its direct file and registry access is not part of the sandboxed game assembly; game-side settings use `FileSystem.Data` and registered mount resources. The [API whitelist](https://sbox.game/dev/doc/code/code-basics/api-whitelist) blocks ordinary game code from unrestricted .NET file access. Editor validation does not establish platform distribution support. Keep the game whitelist enabled: a clean non-editor run with external content and a supported mount deployment remain release gates, and standalone-only mode must not be enabled automatically.

## Build from source

Requires the .NET 10 SDK and a local s&box installation. The project references the engine assemblies in `<sbox>/bin/managed`, located through the `FACEPUNCH_ENGINE` environment variable or an explicit `EnginePath`.

Build the production mount project with .NET 10 against your local engine:

```powershell
dotnet build LambdaWars.Source1Mount.csproj -c Release -m:1 -nr:false -p:UseSharedCompilation=false "-p:EnginePath=<sbox installation>"
```

With the editor and game closed, copy the resulting `bin/Release/LambdaWars.Source1Mount.dll` to `<sbox installation>/mount/lambda_wars/lambda_wars.dll` and copy `Assets` to `<sbox installation>/mount/lambda_wars/Assets`. Release archives also contain compiled shaders; a source build relies on the engine compiling them. The engine must discover the trusted mount on startup. The game does not bypass its code sandbox. Standard platform delivery is not established by local DLL installation.

## Current import limits

- Map metadata includes CPU-only static-prop version 9 checks and prop/model counts. Village contains 637 props using 88 model entries; Chemical Plant contains 1,787 props using 100 entries. This preflight does not enable static-prop rendering or establish playable maps.
- Map conversion starts with a 512 MiB process-memory growth allowance and expands it from validated texture payloads up to a 3 GiB ceiling, with a 20-second work budget. Windows physical-memory checks require at least 2 GiB or 10 percent of total RAM, whichever is larger, to remain free. The editor's existing memory use does not count as new import growth. Selected-map preparation allows recently released resources to expire and imports the scene before acknowledging content readiness.
- Original model rendering is enabled by default after CPU content checks. Authored texture mip levels use individual uploads and GPU copies to avoid the failing bulk upload path. DXT1 and DXT5 textures are the exception: they stay compressed and upload as one chain, smallest mip first, without the mips narrower than one 4x4 block. The basic 16-model roster has been rendered in the Steam editor; this does not establish complete map or standalone support. `LAMBDA_WARS_ENABLE_3D_IMPORT=0` disables models for troubleshooting. Map loading is enabled by default; `LAMBDA_WARS_ENABLE_MAP_IMPORT=0` disables it for troubleshooting. Model loading alone never prepares a BSP package.
- The content screen reports the installation path, indexed file count, map count and a model companion-file diagnostic. It stays open after indexing so failures can be read.
- Loading original content also checks the basic roster sequentially without creating GPU resources. Model sidecars at `mount://lambda_wars/<original model path>.content.json` report geometry, skeletons, LODs, materials and texture dependencies. The content screen reports individual failures and the number of checked textures.
- Both models and textures are checked before GPU conversion. Skeleton transforms, triangle indices, vertex/tangent values and bone weights are validated. Texture dimensions and formats are checked before pixel allocation, including all animation frames. Parsed model data is reused only while something still uses it.
- Original animation frames are not stored in imported models. A model only records its path: the game requests the sequence catalog the first time it animates that model and each track the first time it plays it, so memory follows the tracks in use rather than every authored sequence. Game builds that expect baked clips need an earlier mount release.
- Facial morph conversion is disabled by default after a reproducible infantry-preview GPU fault. `LAMBDA_WARS_ENABLE_FACIAL_MORPHS=1` is reserved for isolated diagnostics. This gate preserves base geometry, skeletons and textures; the fault's exact cause is not yet established.
- `LambdaWarsContentAliases.cs` explicitly repairs moved references and known missing map resources using installed content. Missing normals use the installed flat normal, the missing grate environment uses the default cubemap, snow lightwarp uses the identity ramp, and white uses the platform white texture. Absent building/concrete surfaces use adjacent installed variants; missing underwater surfaces use installed water variants. These are approximations for absent files, not recovered originals. Existing loose or embedded originals take precedence, and source files stay untouched.
- Lambda Wars uses the Alien Swarm VTX strip layout with 33-byte strip groups and 35-byte strips. The reader selects that layout for this mount while retaining the standard Source layout for other mounts.
- Missing loose model companion index entries can be recovered read-only beside the indexed original model. Archive files keep their original archive lookup.
- Texture conversion rejects excessive dimensions, frame counts and aggregate pixels; mesh conversion checks triangle indices before allocating GPU buffers. These checks do not protect against every native engine or driver fault.
- Original content loads only after the player selects Settings > Load Original Game Content.
- Map catalog reads retain entity/model metadata and stream SHA-256 fingerprints without retaining entire BSP files.
- A selected map gets a fresh installation index with its embedded resources taking precedence. No BSP package is loaded at startup unless `LAMBDA_WARS_IMPORT_MAP` explicitly selects one.
- Embedded packages are limited to 64 MiB uncompressed and 4,096 entries. Unsafe paths and case-insensitive duplicates reject the entire package before it changes the index.
- Scene loading estimates a bounded memory-growth allowance from validated texture payloads, with a 3 GB growth ceiling and a 20-second work budget. The system must retain at least 2 GB or 10 percent of physical RAM, whichever is larger. These checks cannot interrupt an individual native resource conversion or shader compilation already in progress.
- Before changing scenes, every human connection and spectator prepares its selected map and acknowledges its fingerprint. The host waits for these acknowledgements, then waits separately for collision, navigation and roster validation. Content path changes are rejected during a battlefield.
- The original maps remain validation-gated. Loading metadata is not proof of a playable map.

Source shaders under `Assets/shaders/source1` are engine implementation code rather than original-game artwork. Do not run concurrent shader compilation, map loading or game instances during validation.

## Original animation channels

All skeletal Lambda Wars models now import their local and recursively included sequence catalogs, rather than only the four initial roster models and selected activities. Unique source animation tracks are decoded once, retain every authored frame, FPS and additive flag, and are shared by blend-grid entries. Single-track sequences also expose their original sequence name; duplicate names retain their source model/index in the catalog. Existing evaluated roster clips take precedence over raw aliases.

`SourceSequenceCatalog` schema 2 is available as model game data in the mount and game assemblies. It retains the original skeleton, pose-parameter grids, bone weights, auto layers, track timing and motion distance, and lossless IK records. `PendingOperations` describes the original track requirements; the game controller evaluates the operations used by the current roster. Unsupported external IK targets remain explicit errors rather than silently selecting another animation. Input caching is capped at 128 MiB and decoded poses at 256 MiB per model.

`SourceSequencePlayer` evaluates original triangular pose blends and weighted additive layers through native animation channels. `SourceSequenceContacts` resolves mapped self targets and persistent ground contacts against scene collision. Gameplay movement remains authoritative; authored movement distances set gait playback speed without applying root motion a second time. Static pose layers with unused NaN timing envelopes remain full-weight pose grids, while timed layers still require finite intervals.

`WarsUnitAnimation` selects the current roster's idle, run/walk, weapon attack and construction activities. Weapon shots carry a host-owned serial and simulation timestamp in the match snapshot, so clients replay actual shots and return to idle instead of continuously looping a firing sequence merely because an attack order exists. Weapon bone merging runs after the final character pose.

`WarsBuildingAnimation` applies original construction progress, finished/idle activities, powered production work activities and production pose parameters for all current building definitions. The billet uses `ACT_FINISHED`; structures without authored construction or work activities retain their original idle pose. The source animation contracts follow [Valve's bone setup implementation](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/public/bone_setup.cpp) and the installed Lambda Wars unit/building definitions. These controllers cover the states exposed by the current match simulation; they do not add new unit types, abilities, death persistence or destruction effects.


## Original gameplay audio

Logical events from the installed sound manifest are exposed lazily as `mount://lambda_wars/sounds/events/<lowercase escaped event name>.sound`. They retain available random wave variants, authored volume and pitch ranges, WAV loop markers and Source sound-level falloff. No original media is copied into the project. Distance-variant NPC gunshots decode their close-left and distant-right channels into separate mono resources; the match blends them over 1,200 world units with synchronized variant and pitch selection.

Soundscapes are read the way the original engine reads them: `scripts/soundscapes_manifest.txt` in order, then `scripts/soundscapes_<map>.txt`, with later names replacing earlier ones and empty sections left out. `mount://lambda_wars/soundscapes.json` publishes the result as ordered commands. Each voice a soundscape can start is a lazy event at `mount://lambda_wars/sounds/soundscapes/<wave>.<sound level>.sound`, with `.loop` before `.sound` for looping voices. Looping voices honour `smpl` loop markers as well as `cue` ones, since most original ambient loops only have the former. Each map's catalog entry lists its `Soundscapes` with named positions resolved to world points, its `SoundscapeTriggers`, and the `BoundaryBrushes` that set the listener's height.

The current five units and nine buildings use original selection/order responses, weapon fire, pain/death and destruction sounds. Footsteps and explicit sound events follow the evaluated animation cycles, including loop wraps. Barracks, junkyard and garrison production sounds follow completed, powered production; stalker beam loops follow actual attacks or nearby building work. Work loops stop on power loss, activity changes, fog, removal and match cleanup.

Host snapshots carry a bounded sound history and serial watermark. Each recipient receives only sounds permitted by world visibility or ownership; local playback consumes each serial once and skips old join history. Burst shots retain their source spacing. Selection and accepted command acknowledgements play once per group with the original response cooldowns. Faction announcements cover production, population upgrades, captures, attacks and population limits where their original cues exist.

The original rebel definition references an undefined `unit_rebel_attack` event, so the port uses its original movement acknowledgements when that event is absent. Missing base-game concrete/tile footsteps fall back to the installed NPC run-footstep events. The stalker's moved laser-flesh wave is repaired through the explicit content alias table. These fallbacks do not invent or redistribute audio.

Roster preparation also runs when gameplay scene services start, so a fresh proving-ground launch prepares its CPU model reports before allowing rendering. An empty report catalog must not leave every unit and building invisible until the manual content dialog is opened.

## Map catalog compatibility

The catalog exports enabled `func_map_boundary` volumes with their authored `bloat` inset. Camera movement and zoom keep both the focus and camera position inside one player region, including during smoothing and rotation. Camera focus height follows starting-ground elevations rather than the full BSP world's vertical center. Maps without authored player regions retain the world-bounds fallback.

Collision hulls clip authored brush faces against their half-spaces, including bevel planes, instead of enumerating every plane triple. Brushes with up to 1,024 sides are supported within the existing import time and memory budgets. This removes the 64-side rejection affecting Slums, Slums Winter, Abandoned, City 16, Dump, Jungle, Meltdown, Metro, and Waste. Their world collision models have been built in the engine; Slums also passed ground traces at both starting positions.

The installed 36-map catalog passes CPU validation of BSP geometry metadata, static and entity prop dependencies, materials, and texture payloads. VMT parsing accepts unquoted numeric bracket vectors separately from platform conditions. The largest validated map requires 2,496 MiB under the existing upload-headroom estimate, so scene conversion permits up to 3 GiB of growth while retaining the physical-memory reserve. On 2026-10-15, all 36 catalog maps passed individual private-lobby launches in one uninterrupted Steam editor session: correct scene, completed runtime readiness, running match, two starting workers and a camera position inside authored player boundaries. The sweep completed 8,450 readiness checks. Runtime readiness checks remain enabled; full matches, eight-human multiplayer and ordinary platform delivery are separate validation gates.

Displacement rows and columns follow the Source grid order, with matching render and collision winding. Starting workers use the original expanding-ring placement search. Capture checks use human-hull overlap with every valid authored trigger volume; empty trigger placeholders are ignored. Authored navigation gates are evaluated as connections between their adjacent walkable regions without changing their initial collision state. NPC clip brushes block unit movement and carve matching holes in the navigation mesh, except clip ramps with a walkable sloped side, such as those laid over stairs; units walk on those instead. Holes follow the shape of angled clip brushes rather than their bounding boxes, so a diagonal brush does not close the routes beside it. Grates and windows, including metal walkways, carry and stop units and feed navigation, while sight and weapon traces pass through them.

Multiblend terrain reads the BSP's packed four-layer weights, alpha controls and per-layer vertex colors, preserving the authored texture rotations, scales and texture-alpha transitions. The format and transition equations follow the [Alien Swarm displacement records](https://github.com/ReactiveDrop/reactivedrop_public_src/blob/master/src/public/bspfile.h) and [Multiblend shader](https://github.com/ReactiveDrop/reactivedrop_public_src/blob/master/src/materialsystem/stdshaders/multiblend_ps2x.fxc). WorldVertexTransition supports blend modulation, masked blending and a second normal map. Both shader source copies are kept in sync, and the production texture preflight includes terrain masks and specular maps.

Terrain uses the current renderer's lighting and specular response; original baked lightmaps and Source's global terrain-specular lighting are not reconstructed. Water uses a simplified shader (scrolling normal maps, scene refraction, depth fog and a Fresnel environment reflection); Source's planar reflection and refraction render targets, WaterLOD distance controls and cheap/expensive fallbacks are not reproduced. Successful map loading does not establish complete material or map I/O coverage.

Terrain rendering was checked in the editor on Village, Slums, Forest, Forest Winter and Base4 on 2026-10-15. A subsequent 36-map regression sweep stopped after 20 attempts: 14 matches passed without terrain-material fallbacks, five were blocked by the existing RAM headroom checks, and the editor connection closed on Mine Winter. The cause of that exit and the remaining map checks are unresolved; this terrain change has not passed a complete 36-map regression sweep.

## Original 3D skies

The importer separates the BSP area containing `sky_camera` into a render-only sky scene, retaining its authored origin and scale. Sky geometry and props do not contribute gameplay collision. Water in that scene uses a cubemap and the authored water fog color instead of the main scene's refraction buffer. The renderer does not yet reproduce Source's sky fog or its distinction between `SURF_SKY` and `SURF_SKY2D`.

On 2026-10-27, Village's distant trees and water were checked in the editor with the sky enabled and disabled. Forest loaded with its own sky scene. Canals loaded without a sky scene but remained blocked on its existing navigation readiness check. The BSP sky-area classifier also completed on 54 installed maps, including 31 with a sky camera.
