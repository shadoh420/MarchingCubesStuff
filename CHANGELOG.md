# Changelog

## Phase 10 — Projectile System (Fireballs)

### New Files

- **`Assets/Scripts/Combat/Fireball.cs`**
  Physics-driven fireball projectile. Rigidbody with configurable speed (25 m/s), zero gravity
  (straight flight), and max lifetime (8s). On terrain collision: carves a crater via
  `TerrainManager.EditTerrain()` (radius=1.5, delta=30, tuneable in Inspector) and spawns
  PyroParticles `FireExplosion` VFX. Self-inflicted splash damage reduced to 50% (Quake-style
  rocket jumping). Splash damage uses `PlayerHealth.TakeDamage()` with distance falloff.

- **`Assets/Scripts/Combat/ProjectileLauncher.cs`**
  First-person projectile launcher. Fires from camera center with configurable cooldown (1.25s),
  spawn offset (1.5 units forward to clear player capsule). Exposes `CooldownProgress` (0–1)
  for HUD display. Creates fireball instances with PyroParticles trail VFX attached as child
  (Pyro physics disabled). Includes fallback orange-sphere visual when no prefab is assigned.
  Launch audio via configurable `AudioClip` (played from a 2D AudioSource on the launcher).
  Input via `SetFireInput()` from PlayerInputManager (same pattern as TerrainTool).

- **`Assets/Scripts/UI/CombatHUD.cs`**
  Runtime-created screen-space overlay Canvas (sort order 110, above TerrainToolHUD at 100).
  Center crosshair with four arms + dot, colour-lerps between ready (white) and cooldown (orange)
  states. CanvasScaler at 1920×1080 reference.

### Modified Files

- **`Assets/Scripts/Player/PlayerInputManager.cs`**
  - Added `public ProjectileLauncher Launcher` field.
  - Attack input routing is now mutually exclusive: admin mode sends LMB to TerrainTool and
    explicitly sends `false` to Launcher; combat mode sends LMB to Launcher only.

- **`Assets/Scripts/Player/PlayerSpawnManager.cs`**
  - Added `ProjectileLauncher Launcher`, `CombatHUD CombatHudComponent`, `PlayerHealth Health`,
    `PlayerVisuals Visuals`, `HealthHUD HealthHudComponent` references.
  - Added `bool AdminToolsEnabled` (default=false). When false, `TerrainTool` and
    `TerrainToolHUD` components are disabled at spawn.
  - Spawn sequence extended with steps 10–14.

---

## Phase 11 — Health & Game Loop (Partial)

### New Files

- **`Assets/Scripts/Player/PlayerHealth.cs`**
  Player health component: MaxHP (100), TakeDamage(), Heal(), Die(), Respawn(). Death disables
  KCC motor, launcher, and player visuals. After RespawnDelay (3s), raycasts down to find terrain
  surface and teleports the player back. Events: OnDamaged, OnDied, OnRespawned for HUD binding.

- **`Assets/Scripts/Player/PlayerVisuals.cs`**
  Creates a capsule mesh child matching KCC dimensions. Hidden for local first-person player
  (`HideForLocalPlayer` flag), visible for remote players in multiplayer. `SetColor()` API for
  team colours. `SetVisible()` called by PlayerHealth on death/respawn.

- **`Assets/Scripts/UI/HealthHUD.cs`**
  Runtime-created overlay Canvas (sort order 120). Bottom-left health bar with green→red colour
  at low HP, HP text overlay, full-screen red damage flash on hit, and "RESPAWNING..." death
  overlay. Event-driven via PlayerHealth subscriptions.

### Architecture Notes

- **Admin mode exclusive**: Admin tools (dig/build) and combat weapon are mutually exclusive.
  LMB goes to TerrainTool in admin mode, to ProjectileLauncher in combat mode.
- **Self-damage**: 50% splash damage on self, enabling Quake-style rocket jumping.
- **Death/Respawn**: Self-contained in PlayerHealth — no external coroutine needed.
  Respawn uses same terrain raycast logic as initial spawn.

---

## Phase 9 — Terrain Interaction & Tools

### New Files

- **`Assets/Scripts/Player/TerrainTool.cs`**
  First-person terrain editing tool. Raycasts from the camera center each frame and applies
  spherical density edits via `TerrainManager.EditTerrain()` while the Attack input (LMB) is held.
  Supports Dig mode (positive delta — remove material) and Build mode (negative delta — add material),
  cycled with the Previous/Next actions (1/2 keys). A runtime-created semi-transparent indicator
  sphere previews the edit radius at the hit point; colour changes per mode (red=dig, blue=build).

- **`Assets/Scripts/UI/TerrainToolHUD.cs`**
  Runtime-created screen-space overlay Canvas. Draws a center-screen crosshair (four thin arms
  with a gap + center dot) and a bottom-center mode label ("DIG" / "BUILD") with colour coding.
  CanvasScaler set to ScaleWithScreenSize (1920×1080 reference).

### Modified Files

- **`Assets/Scripts/Player/PlayerInputManager.cs`**
  - Added `TerrainTool Tool` public reference.
  - Cached and enabled/disabled `Attack`, `Previous`, `Next` actions from the Player map.
  - `Update()` now feeds tool inputs: attack (gated on cursor lock), mode cycling.
  - Added `IsCursorLocked` public property.

- **`Assets/Scripts/Player/PlayerSpawnManager.cs`**
  - Added `TerrainTool Tool` and `TerrainToolHUD HUD` public references.
  - Spawn sequence now wires `Tool.terrainManager`, `Tool.cameraTransform`, `HUD.tool`,
    and `InputManager.Tool` at runtime (steps 7–9).

### Deprecation Fixes

- **`Assets/Scripts/Jobs/PhysicsBakeJob.cs`**
  - Changed `meshInstanceId` field from `int` to `EntityId`.
  - Eliminates `CS0618` warning: `Physics.BakeMesh(int, bool)` is obsolete in Unity 6.

- **`Assets/Scripts/Terrain/TerrainChunk.cs`**
  - Replaced `mesh.GetInstanceID()` with `mesh.GetEntityId()` in both the async bake scheduling
    (`CompleteGeneration`) and the synchronous bake path (`GenerateMesh`).
  - Eliminates the matching `CS0618` warning.

---

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
