# Original Lambda Wars content mount

Native Source 1 resource readers adapted from Facepunch's MIT-licensed `Sandbox.Mounting.Source1` implementation in [sbox-public](https://github.com/Facepunch/sbox-public). The accompanying license applies to the upstream readers and shaders. `LambdaWars*` files add installation discovery, map metadata and BSP scene reconstruction.

Original game assets are read from the Steam installation and are never copied by this project. Set `LAMBDA_WARS_GAME_PATH` to the original installation root or its `lambdawars` directory to override Steam discovery.

Build the production mount project with .NET 10 against your local engine:

```powershell
dotnet build Libraries/Source1/LambdaWars.Source1Mount.csproj -m:1 -nr:false -p:UseSharedCompilation=false "-p:EnginePath=<sbox installation>"
```

With the editor and game closed, copy the resulting `LambdaWars.Source1Mount.dll` into `<sbox installation>/mount/lambda_wars/lambda_wars.dll`. The engine must discover the trusted mount on startup. The game does not bypass its code sandbox. Standard platform delivery is not established by local DLL installation.

## Current import limits

- After the observed Vulkan device-loss crash, 3D model and scene conversion is disabled by default. Metadata, original menu artwork and HUD textures remain available. `LAMBDA_WARS_ENABLE_3D_IMPORT=1` enables controlled local GPU validation after restarting; it is not a claim that rendering is safe or complete.
- The content screen reports the installation path, indexed file count, map count and a model companion-file diagnostic. It stays open after indexing so failures can be read.
- Lambda Wars uses the Alien Swarm VTX strip layout with 33-byte strip groups and 35-byte strips. The reader selects that layout for this mount while retaining the standard Source layout for other mounts.
- Missing loose model companion index entries can be recovered read-only beside the indexed original model. Archive files keep their original archive lookup.
- Texture conversion rejects excessive dimensions, frame counts and aggregate pixels; mesh conversion checks triangle indices before allocating GPU buffers. These checks do not protect against every native engine or driver fault.
- Original content loads only after the player selects Settings > Load Original Game Content.
- Map catalog reads retain entity/model metadata and stream SHA-256 fingerprints without retaining entire BSP files.
- Only one map's embedded package is prepared. `LAMBDA_WARS_IMPORT_MAP` chooses its original basename; the default is `hlw_village`.
- Embedded packages are limited to 64 MB uncompressed and 4,096 entries.
- Scene loading checks resident memory between resources, allowing at most 512 MB growth and a 2 GB process ceiling, with a 20-second work budget. These checks cannot interrupt an individual native resource conversion or shader compilation already in progress.
- Changing original maps requires restarting until map-specific resource teardown is implemented.
- The original maps remain validation-gated. Loading metadata is not proof of a playable map.

Source shaders under `Assets/shaders/source1` are engine implementation code rather than original-game artwork. Do not run concurrent shader compilation, map loading or game instances during validation.
