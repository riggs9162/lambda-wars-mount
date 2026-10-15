# Original Lambda Wars content mount

Native Source 1 resource readers adapted from Facepunch's MIT-licensed `Sandbox.Mounting.Source1` implementation in [sbox-public](https://github.com/Facepunch/sbox-public). The accompanying license applies to the upstream readers and shaders. `LambdaWars*` files add installation discovery, map metadata and BSP scene reconstruction.

Original game assets are read from the installed game and are never copied by this project. Settings provides a manual content path accepting either the installation root or its `lambdawars` directory. Applying stages a new file index before refreshing the mount in the menu or lobby; clearing the path restores automatic Steam discovery. The engine needs one restart when installing a new mount DLL, but content paths and map selection do not.

Steam's registered installation is preferred by default. An explicitly saved manual path overrides discovery; `LAMBDA_WARS_GAME_PATH` remains a fallback when no valid installation is otherwise available. Manual configuration is stored locally in `%LOCALAPPDATA%/LambdaWarsSbox/content.json` and is never replicated to other players. Both loose installations with `.dx90.vtx` model companions and Steam VPKs with `.vtx` companions are supported; the original model and texture identifiers stay unchanged.

On Windows, automatic discovery also checks Steam's registry entries read-only, then follows `steamapps/libraryfolders.vdf` and `appmanifest_270370.acf` to the registered game directory. It checks both machine registry views and the current user's Steam path; no registry values are modified.

The mount is a separately installed engine extension. Its direct file and registry access is not part of the sandboxed game assembly; game-side settings use `FileSystem.Data` and registered mount resources. The [API whitelist](https://sbox.game/dev/doc/code/code-basics/api-whitelist) blocks ordinary game code from unrestricted .NET file access. Editor validation does not establish platform distribution support. Keep the game whitelist enabled: a clean non-editor run with external content and a supported mount deployment remain release gates, and standalone-only mode must not be enabled automatically.

Build the production mount project with .NET 10 against your local engine:

```powershell
dotnet build Libraries/Source1/LambdaWars.Source1Mount.csproj -m:1 -nr:false -p:UseSharedCompilation=false "-p:EnginePath=<sbox installation>"
```

With the editor and game closed, copy the resulting `LambdaWars.Source1Mount.dll` into `<sbox installation>/mount/lambda_wars/lambda_wars.dll`. The engine must discover the trusted mount on startup. The game does not bypass its code sandbox. Standard platform delivery is not established by local DLL installation.

## Current import limits

- Map metadata includes CPU-only static-prop version 9 checks and prop/model counts. Village contains 637 props using 88 model entries; Chemical Plant contains 1,787 props using 100 entries. This preflight does not enable static-prop rendering or establish playable maps.
- Map conversion retains a 512 MB process-memory growth budget and a 20-second work budget. Windows physical-memory checks require at least 2 GB or 10 percent of total RAM, whichever is larger, to remain free. The editor's existing memory use does not count as new import growth.
- Original model rendering is enabled by default after CPU content checks. Authored texture mip levels use individual uploads and GPU copies to avoid the failing bulk upload path. The basic 16-model roster has been rendered in the Steam editor; this does not establish complete map or standalone support. `LAMBDA_WARS_ENABLE_3D_IMPORT=0` disables models for troubleshooting. Map loading is enabled by default; `LAMBDA_WARS_ENABLE_MAP_IMPORT=0` disables it for troubleshooting. Model loading alone never prepares a BSP package.
- The content screen reports the installation path, indexed file count, map count and a model companion-file diagnostic. It stays open after indexing so failures can be read.
- Loading original content also checks the basic roster sequentially without creating GPU resources. Model sidecars at `mount://lambda_wars/<original model path>.content.json` report geometry, skeletons, LODs, materials and texture dependencies. The content screen reports individual failures and the number of checked textures.
- Both models and textures are checked before GPU conversion. Skeleton transforms, triangle indices, vertex/tangent values and bone weights are validated. Texture dimensions and formats are checked before pixel allocation, including all animation frames. Parsed model data is cached for the mount's lifetime.
- Facial morph conversion is disabled by default after a reproducible infantry-preview GPU fault. `LAMBDA_WARS_ENABLE_FACIAL_MORPHS=1` is reserved for isolated diagnostics. This gate preserves base geometry, skeletons and textures; the fault's exact cause is not yet established.
- `LambdaWarsContentAliases.cs` is the explicit table repairing the Combine HQ's outdated shield and fence material paths. It points to existing installed materials and leaves all source files untouched.
- Lambda Wars uses the Alien Swarm VTX strip layout with 33-byte strip groups and 35-byte strips. The reader selects that layout for this mount while retaining the standard Source layout for other mounts.
- Missing loose model companion index entries can be recovered read-only beside the indexed original model. Archive files keep their original archive lookup.
- Texture conversion rejects excessive dimensions, frame counts and aggregate pixels; mesh conversion checks triangle indices before allocating GPU buffers. These checks do not protect against every native engine or driver fault.
- Original content loads only after the player selects Settings > Load Original Game Content.
- Map catalog reads retain entity/model metadata and stream SHA-256 fingerprints without retaining entire BSP files.
- A selected map gets a fresh installation index with its embedded resources taking precedence. No BSP package is loaded at startup unless `LAMBDA_WARS_IMPORT_MAP` explicitly selects one.
- Embedded packages are limited to 64 MiB uncompressed and 4,096 entries. Unsafe paths and case-insensitive duplicates reject the entire package before it changes the index.
- Scene loading estimates a bounded memory-growth allowance from validated texture payloads, with a 2 GB growth ceiling and a 20-second work budget. The system must retain at least 2 GB or 10 percent of physical RAM, whichever is larger. These checks cannot interrupt an individual native resource conversion or shader compilation already in progress.
- Before changing scenes, every human connection and spectator prepares its selected map and acknowledges its fingerprint. The host waits for these acknowledgements, then waits separately for collision, navigation and roster validation. Content path changes are rejected during a battlefield.
- The original maps remain validation-gated. Loading metadata is not proof of a playable map.

Source shaders under `Assets/shaders/source1` are engine implementation code rather than original-game artwork. Do not run concurrent shader compilation, map loading or game instances during validation.

## Original animation channels

All skeletal Lambda Wars models now import their local and recursively included sequence catalogs, rather than only the four initial roster models and selected activities. Unique source animation tracks are decoded once, retain every authored frame, FPS and additive flag, and are shared by blend-grid entries. Single-track sequences also expose their original sequence name; duplicate names retain their source model/index in the catalog. Existing evaluated roster clips take precedence over raw aliases.

`SourceSequenceCatalog` schema 2 is available as model game data in the mount and game assemblies. It retains the original skeleton, pose-parameter grids, bone weights, auto layers, track timing and motion distance, and lossless IK records. `PendingOperations` describes the original track requirements; the game controller evaluates the operations used by the current roster. Unsupported external IK targets remain explicit errors rather than silently selecting another animation. Input caching is capped at 128 MiB and decoded poses at 256 MiB per model.

`SourceSequencePlayer` evaluates original triangular pose blends and weighted additive layers through native animation channels. `SourceSequenceContacts` resolves mapped self targets and persistent ground contacts against scene collision. Gameplay movement remains authoritative; authored movement distances set gait playback speed without applying root motion a second time. Static pose layers with unused NaN timing envelopes remain full-weight pose grids, while timed layers still require finite intervals.

`WarsUnitAnimation` selects the current roster's idle, run/walk, weapon attack and construction activities. Weapon shots carry a host-owned serial and simulation timestamp in the match snapshot, so clients replay actual shots and return to idle instead of continuously looping a firing sequence merely because an attack order exists. Weapon bone merging runs after the final character pose.

`WarsBuildingAnimation` applies original construction progress, finished/idle activities, powered production work activities and production pose parameters for all current building definitions. The billet uses `ACT_FINISHED`; structures without authored construction or work activities retain their original idle pose. The source animation contracts follow [Valve's bone setup implementation](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/public/bone_setup.cpp) and the installed Lambda Wars unit/building definitions. These controllers cover the states exposed by the current match simulation; they do not add new unit types, abilities, death persistence or destruction effects.
