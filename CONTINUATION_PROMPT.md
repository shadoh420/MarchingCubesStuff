# Continuation Prompt — Paste this into a new conversation

---

You are an expert Unity Engine architect and C# performance optimization specialist. I am building a high-performance, volumetric voxel terrain engine (similar to Deep Rock Galactic) in Unity 6 (6000.3.11f1) with URP 17.

**Repository:** https://github.com/shadoh420/MarchingCubesStuff

## What's Already Built (read the codebase first)

The core terrain system is complete through **Phase 9**:

### Terrain System (Phases 1–7)
- **`Assets/Scripts/Terrain/TerrainManager.cs`** — Infinite terrain orchestrator with object pooling, distance-prioritized chunk loading (sorted by SqrMagnitude), LOD tier selection (step 1/2/4), and a pipelined async generation coroutine. Contains `EditTerrain()` for spherical brush edits with cross-chunk propagation. `GenerateChunkImmediate()` for synchronous single-chunk generation. Soft-cancel orphan fix ensures in-flight bakes are completed across generation changes. LOD0 is forced for all chunks within collider distance. Runtime safety clamp ensures `colliderDistanceXZ >= viewDistanceXZ`.
- **`Assets/Scripts/Terrain/TerrainChunk.cs`** — Per-chunk lifecycle with persistent NativeArrays (allocated once, never reallocated). Three-phase async pipeline: Frame N schedules Burst jobs, Frame N+1 applies mesh + schedules PhysicsBakeJob, Frame N+2 completes bake + assigns MeshCollider. `EnsureFullResolutionDensity()` synchronously regenerates LOD0 density on demand for LOD1/2 chunks before edits.
- **`Assets/Scripts/Jobs/DensityJob.cs`** + **`MarchingCubesJob.cs`** — Burst-compiled parallel jobs with vertexStep stride for LOD. Zero main-thread computation.
- **`Assets/Scripts/Jobs/PhysicsBakeJob.cs`** — IJob wrapping `Physics.BakeMesh()` for async collision baking on worker threads.
- **`Assets/Scripts/MarchingCubesTables.cs`** — Static lookup tables for Marching Cubes.
- **`Assets/Scripts/Terrain/TerrainEditor.cs`** — Legacy mouse-based terrain editing (raycast + `EditTerrain()`). Superseded by TerrainTool in Phase 9; kept for dev testing.
- **`Assets/Scripts/Terrain/FlyCameraController.cs`** — Dev fly camera (disabled in favour of first-person camera).
- **`Assets/Scripts/Terrain/TriplanarTerrainMaterialSetup.cs`** — Editor utility for terrain material setup.

### Character Controller + First-Person Camera (Phase 8)
- **`Assets/Scripts/Player/PlayerCharacterController.cs`** — KCC `ICharacterController` implementation: walk (6 m/s), sprint (10 m/s), jump, crouch, gravity (-30 Y), slope handling. Capsule rotation locked to yaw; pitch handled by camera.
- **`Assets/Scripts/Player/FirstPersonCamera.cs`** — Mouse-look camera with ±89° vertical clamp, configurable sensitivity (0.1), follows CameraFollowPoint on the character.
- **`Assets/Scripts/Player/PlayerInputManager.cs`** — Bridges Unity Input System Player action map (Move, Look, Jump, Sprint, Crouch, Attack, Previous, Next) to character + camera + terrain tool. Cursor lock management (Escape toggles). Gates tool attack input on cursor lock state.
- **`Assets/Scripts/Player/PlayerSpawnManager.cs`** — Synchronously generates chunk column at spawn XZ, raycasts down for surface, teleports character. Wires camera, input, TerrainManager.player, TerrainTool, and TerrainToolHUD at runtime.

### Terrain Interaction & Tools (Phase 9)
- **`Assets/Scripts/Player/TerrainTool.cs`** — First-person terrain tool. Raycasts from camera center, calls `TerrainManager.EditTerrain()` with configurable radius (2.5) and power (15). `ToolMode` enum: Dig (positive delta = remove material) / Build (negative delta = add material). Semi-transparent indicator sphere at hit point previews edit radius; colour changes per mode (red=dig, blue=build). Input fed by PlayerInputManager via `SetAttackInput()` and `CycleMode()`.
- **`Assets/Scripts/UI/TerrainToolHUD.cs`** — Runtime-created screen-space overlay canvas. Center-screen crosshair (4 arms + gap + center dot). Bottom-center mode label ("DIG" / "BUILD") with colour coding. CanvasScaler set to ScaleWithScreenSize (1920×1080 reference).

### Input System
- **`Assets/InputSystem_Actions.inputactions`** — Player map: Move (WASD/stick), Look (mouse/stick), Jump (Space), Sprint (L-Shift), Crouch (C), Attack (LMB), Interact (E), Previous (1/DPad-Left), Next (2/DPad-Right).

### Kinematic Character Controller Asset
- **`Assets/KinematicCharacterController/`** — Imported KCC asset by Philippe St-Amand. Provides `KinematicCharacterMotor`, `ICharacterController` interface, `KinematicCharacterSystem`.

## Current Inspector Settings (user-configured)
- `viewDistanceXZ = 16`, `viewDistanceY = 4`
- `colliderDistanceXZ` auto-clamped to match viewDistanceXZ at runtime
- All visible chunks forced to LOD0 with colliders (no LOD transitions within visible range)
- `chunksPerFrame = 4`, `unloadsPerFrame = 32`, `poolWarmupCount = 16`, `loadHysteresis = 2`
- `noiseScale = 0.05`, `amplitude = 10`, `surfaceY = 8`

## Scene Setup Required for Phase 9
After pulling, add the following components to your scene:
1. **TerrainTool** component on the Player GameObject (or any persistent GO). Inspector fields: `terrainManager`, `cameraTransform` (auto-wired by PlayerSpawnManager if left null).
2. **TerrainToolHUD** component on any persistent GO. Inspector field: `tool` (auto-wired by PlayerSpawnManager if left null).
3. Drag references into **PlayerSpawnManager**: `Tool` → TerrainTool, `HUD` → TerrainToolHUD.
4. Optionally disable the legacy **TerrainEditor** component if still active.

## What to Implement Next

### Phase 10: Chunk Border Stitching
- Address visible seams between chunks at different LOD levels (only relevant if `viewDistanceXZ > colliderDistanceXZ` in the future). Currently LOD is effectively disabled within visible range.
- Propose and implement a solution (shared-edge density sampling, transition meshes, or skirt geometry).

## System Constraints (Do NOT break these)
- Soft-cancel via generation counter (no StopCoroutine overhead)
- Load hysteresis (2-chunk buffer)
- NativeArrays allocated exactly ONCE and reused across pool cycles
- Collider distance culling (auto-clamped to >= view distance)
- Distance-prioritized loading order
- LOD via voxel striding with step 1/2/4 (currently forced to LOD0 for all collidered chunks)
- Async physics baking pipeline
- `EnsureFullResolutionDensity()` before any density array reads/writes on LOD1/2 chunks
- Orphaned bake/batch completion at the start of each new `LoadTerrain()` coroutine

## Instructions
- Read all existing scripts in the workspace before writing any code.
- Implement one phase at a time, starting with Phase 10.
- Commit and push to origin/master after each phase.
- Use the Input System (not legacy `Input.GetAxis`).
- Follow the existing code style (XML doc comments, region separators, consistent naming).
- Ask before proceeding to the next phase.
