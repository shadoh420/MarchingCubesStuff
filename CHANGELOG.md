# Changelog

## Phase 8 — Character Controller + First-Person Camera

### New Files

- **`Assets/Scripts/Player/PlayerCharacterController.cs`**
  First-person character controller implementing KCC's `ICharacterController` interface.
  Walk, sprint, jump, crouch, gravity, and slope handling on marching-cubes MeshCollider terrain.
  Capsule rotation locked to yaw only; pitch handled by the camera.

- **`Assets/Scripts/Player/FirstPersonCamera.cs`**
  Mouse-look camera with configurable sensitivity and vertical clamp (±89°).
  Follows a `CameraFollowPoint` transform on the character. Accumulates raw Input System deltas
  into pitch/yaw. Lives on the Camera GameObject, not on the character.

- **`Assets/Scripts/Player/PlayerInputManager.cs`**
  Bridges Unity Input System actions (Move, Look, Jump, Sprint, Crouch) to the character
  controller and camera. Manages cursor lock state (Escape to toggle).

- **`Assets/Scripts/Player/PlayerSpawnManager.cs`**
  Spawn system that synchronously generates the chunk column at the player's spawn XZ,
  raycasts down to find the terrain surface, and teleports the character onto solid ground.
  Wires up camera follow point, input manager, and TerrainManager player reference at runtime.

### Modified Files

- **`Assets/Scripts/Terrain/TerrainManager.cs`**
  - **`GenerateChunkImmediate(Vector3Int coord)`** — New public method for synchronous single-chunk
    generation with immediate physics bake. Used by the spawn system.
  - **Soft-cancel collision orphan fix** — `LoadTerrain()` now completes any in-flight physics bakes
    and orphaned chunk batches from a cancelled coroutine before starting a new pass. Previously,
    chunks could end up with visible meshes but no colliders when the generation counter advanced
    mid-load.
  - **`CompletePendingBatchOrphan()`** — New helper that finishes chunks left in a half-initialized
    state by a soft-cancelled coroutine.
  - **LOD0 forced for all collidered chunks** — `GetLodStep()` now returns step=1 for every chunk
    within collider distance, eliminating LOD-boundary vertex mismatches that created physical gaps
    in the terrain.
  - **Collider distance safety clamp** — `Start()` clamps `colliderDistanceXZ` up to
    `viewDistanceXZ` (and Y equivalents) at runtime, ensuring every visible chunk always receives
    a MeshCollider. Logs a warning if values were misconfigured.

- **`Assets/Scripts/Terrain/TerrainChunk.cs`**
  - **`_densityFieldPopulated` flag** — Tracks whether the managed `densityField[]` contains valid
    LOD0 data. LOD1/LOD2 chunks only fill the NativeArray at reduced resolution; the managed array
    stays stale until explicitly populated.
  - **`EnsureFullResolutionDensity()`** — New public method that synchronously runs a full-resolution
    DensityJob if the managed array is stale. Called before any terrain edit and as a safety net
    inside `GenerateMesh()`. Prevents the bug where editing LOD1/LOD2 chunks caused terrain to
    vanish (empty density → empty mesh).

### Bug Fixes Summary

| Bug | Root Cause | Fix |
|-----|-----------|-----|
| Terrain vanishes on edit near LOD chunks | LOD1/2 chunks never copied density to managed array; edits wrote into zeros | `EnsureFullResolutionDensity()` populates array on demand |
| Visible terrain with no collider (fall-through) | Soft-cancel orphaned in-flight physics bakes | Complete orphaned bakes at start of new load pass |
| LOD boundary gaps (fall-through at seams) | Adjacent chunks at different LOD levels produce mismatched edge vertices | Force LOD0 for all chunks within collider distance |
| Visual-only chunks with no collider | `colliderDistanceXZ < viewDistanceXZ` left outer ring walkable but uncollidered | Runtime clamp ensures collider distance ≥ view distance |
