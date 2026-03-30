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

The MVP goal is a **networked multiplayer arena** where players shoot fireballs at each other,
deforming the voxel terrain on impact. Implement one phase at a time in order.

### Phase 10: Projectile System (Fireballs)
- **Fireball prefab** — physics-driven projectile with VFX (particle trail + glow). Configurable speed, gravity, lifetime.
- **Shooting mechanic** — fire from camera center. Either add a new tool mode to TerrainTool or a separate `ProjectileLauncher` component with its own input action (e.g., right-click or a dedicated "Fire" action).

USER CLARIFICATION: the dig/build tools and HUD are for testing and debugging purposes, they won't be available in the "arena" mode. So they need to be removed for regular players but remain available for admins or devs in the future. Also, I grabbed a free fireball pack from the asset store which is located in Assets\PyroParticles

- **Terrain deformation on impact** — on terrain collision, call `TerrainManager.EditTerrain()` at impact point with a large crater radius/delta. Destroy the projectile.
- **Player hit detection** — on player collision, apply damage (prepare for Phase 11 health system). Destroy the projectile.
- **Muzzle + explosion VFX** — simple particle effects for launch and impact.

### Phase 11: Health & Game Loop
- **Player health component** — `PlayerHealth.cs` with max HP, TakeDamage(), Die(), Respawn(). Networked later.
- **HUD** — health bar, damage flash, kill feed text.
- **Death & respawn** — on death, disable character + camera, wait, then respawn on terrain surface (reuse `PlayerSpawnManager` logic).
- **Basic round structure** — free-for-all deathmatch or simple timer-based rounds.

### Phase 12: Networked Multiplayer
- **Unity Netcode for GameObjects (NGO)** — add `com.unity.netcode.gameobjects` package.
- **NetworkManager + connection UI** — host/join screen, relay (Unity Relay or direct IP).
- **Networked player** — player prefab with `NetworkObject`, owner-authoritative movement via `ClientNetworkTransform` (or custom sync compatible with KCC).
- **Networked terrain edits** — client requests edit → `ServerRpc` → server calls `EditTerrain()` → `ClientRpc` broadcasts the edit (position, radius, delta) to all clients for local replay.
- **Networked projectiles** — server-authoritative spawn + hit detection. Clients see interpolated ghost.
- **Networked health/damage** — server-authoritative HP, damage RPCs, death/respawn sync.
- **Terrain state sync for late joiners** — delta log or full density snapshot for clients joining mid-game.

### Phase 13: Polish & Performance
- **Bandwidth optimization** — batch/compress terrain edit RPCs, delta encoding for density changes.
- **Lag compensation** — server-side rewind for projectile hit validation.
- **Visual polish** — explosion VFX, terrain dust particles, better UI, sound effects.
- **Stress testing** — multiple clients, profiling, frame budget analysis.

### Deferred / Removed
- ~~**Phase 10 (old): Chunk Border Stitching**~~ — LOD is forced to 0 for all visible chunks; seams are not an issue. Revisit only if LOD is re-enabled for distant chunks in the future.

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
- Implement one phase at a time, starting with **Phase 10 (Projectile System)**.
- Commit and push to origin/master after each phase.
- Use the Input System (not legacy `Input.GetAxis`).
- Follow the existing code style (XML doc comments, region separators, consistent naming).
- Ask before proceeding to the next phase.
- The MVP goal is a networked multiplayer fireball arena — keep all decisions oriented toward that.
