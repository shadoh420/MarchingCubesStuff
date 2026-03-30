# MCProject — Volumetric Voxel Terrain Engine

A high-performance volumetric terrain engine built in **Unity 6** (6000.3.11f1), inspired by games like *Deep Rock Galactic*. Fully destructible/constructible terrain powered by Marching Cubes, Burst-compiled jobs, and an async generation pipeline. Currently evolving into a networked multiplayer arena game with terrain-deforming projectiles.

## Features

### Core Terrain System
- **Marching Cubes** isosurface extraction on a 3D density field
- **Burst-compiled parallel jobs** — density sampling and mesh generation run entirely on worker threads (zero main-thread computation)
- **Object-pooled infinite terrain** — chunks are recycled, never destroyed; NativeArrays allocated once and reused
- **Async two-phase pipeline** — `BeginGeneration()` schedules jobs on Frame N, `CompleteGeneration()` applies the mesh on Frame N+1
- **Soft-cancel via generation counter** — no `StopCoroutine` overhead when the player moves
- **Load hysteresis** — 2-chunk buffer prevents thrashing at chunk boundaries
- **Runtime terrain editing** — spherical brush with smooth quadratic falloff; automatically propagates across chunk borders

### Phase 7: Advanced Rendering

- **Distance-prioritized loading** — chunk coordinates sorted by squared distance from the player before generation begins; nearby chunks load first
- **Level of Detail (LOD) via voxel striding** — three LOD tiers (step 1/2/4) reduce triangle count for distant chunks without reallocating NativeArrays
- **True async physics baking** — `Physics.BakeMesh()` runs on a worker thread via `PhysicsBakeJob`; collider assignment is a cheap pointer swap one frame later
- **Collider distance culling** — chunks beyond a configurable radius skip physics entirely

### Phase 8: Character Controller + First-Person Camera
- **Kinematic Character Controller** — KCC-based first-person character with walk, sprint, jump, crouch, and gravity
- **Mouse-look camera** — configurable sensitivity, ±89° vertical clamp, follows character head
- **Input System bridge** — all input routed through Unity Input System Player action map
- **Spawn system** — synchronous chunk generation at spawn point, surface raycast, automatic reference wiring

### Phase 9: Terrain Interaction & Tools
- **Dig/build tool** — raycasts from camera center, calls `EditTerrain()` with configurable radius and power; LMB to dig or build
- **Tool switching** — press 1/2 to cycle between Dig mode (remove material) and Build mode (add material)
- **Visual feedback** — center-screen crosshair HUD, semi-transparent indicator sphere at hit point previewing edit radius, colour-coded by mode (red=dig, blue=build)
- **Admin-gated** — dig/build tools disabled by default (`AdminToolsEnabled` flag on PlayerSpawnManager)

### Phase 10: Combat — Fireball Projectile System
- **Fireball projectile** — physics-driven Rigidbody with configurable speed (25 m/s), gravity, and lifetime
- **Terrain deformation on impact** — carves craters via `TerrainManager.EditTerrain()` with configurable radius and delta
- **PyroParticles VFX** — trail/glow from Fireball prefab, explosion VFX on impact (physics disabled, visual only)
- **Launch audio** — configurable AudioClip played from a 2D AudioSource on the launcher
- **Cooldown system** — configurable fire rate (1.25s default), exposed as `CooldownProgress` for HUD
- **Self-damage** — 50% splash damage on self (Quake-style rocket jumping)
- **Combat HUD** — center crosshair with cooldown colour feedback (white→orange)
- **Mutually exclusive modes** — admin tools (dig/build) and combat weapon use the same input; never both

### Phase 11: Health & Game Loop
- **Player health** — HP system (100 max) with `TakeDamage()`, distance-based splash falloff, death, and timed respawn (3s)
- **Death/respawn cycle** — death disables KCC motor, weapon, and visuals; respawn raycasts terrain surface and teleports
- **Health HUD** — bottom-left health bar (green→red at low HP), damage flash overlay, "RESPAWNING..." death text
- **Player visuals** — capsule mesh matching KCC dimensions, hidden for local first-person, ready for multiplayer visibility
- **All damage tuneable from Inspector** — DirectDamage, SplashDamage, SplashRadius, SelfDamageMultiplier on ProjectileLauncher

## Project Structure

```
Assets/
  Scripts/
    Combat/
      Fireball.cs                # Projectile: physics, terrain deformation, splash damage
      ProjectileLauncher.cs      # Fire from camera center, cooldown, audio, VFX attachment
    Jobs/
      DensityJob.cs              # Burst job: 3D simplex noise density sampling
      MarchingCubesJob.cs        # Burst job: isosurface extraction
      PhysicsBakeJob.cs          # IJob: async Physics.BakeMesh on worker thread
    Player/
      PlayerCharacterController.cs  # KCC first-person character controller
      FirstPersonCamera.cs          # Mouse-look camera
      PlayerHealth.cs               # HP, damage, death, respawn
      PlayerInputManager.cs         # Input System bridge (combat + admin routing)
      PlayerSpawnManager.cs         # Spawn system, component wiring, admin toggle
      PlayerVisuals.cs              # Capsule mesh for multiplayer visibility
      TerrainTool.cs                # Dig/build terrain tool (admin only)
    Terrain/
      TerrainManager.cs          # Infinite terrain orchestrator, object pool, LOD tiers
      TerrainChunk.cs            # Per-chunk lifecycle, NativeArray management, job scheduling
    UI/
      CombatHUD.cs               # Crosshair + cooldown indicator
      HealthHUD.cs               # Health bar, damage flash, death overlay
      TerrainToolHUD.cs          # Crosshair + mode label (admin only)
    MarchingCubesTables.cs       # Static Marching Cubes lookup tables
  PyroParticles/                 # Third-party fire/explosion VFX asset
  Materials/                     # Terrain materials (triplanar dirt, grass, stone, etc.)
  Scenes/
    SampleScene.unity            # Main scene
```

## Architecture Overview

```
TerrainManager (MonoBehaviour)
  |
  |-- Update(): detect player movement, bump generation counter
  |-- LoadTerrain() coroutine:
  |     Phase 1: Unload far chunks (batched)
  |     Phase 2: Collect + sort missing coords by distance
  |     Phase 3: For each batch:
  |       Frame N  : BeginGeneration() → schedule DensityJob + MarchingCubesJob
  |       Frame N+1: CompleteGeneration() → apply mesh + schedule PhysicsBakeJob
  |       Frame N+2: CompletePhysicsBake() → assign MeshCollider (pointer swap)
  |
  |-- Object Pool: Queue<TerrainChunk>, warm-started at boot
  |-- EditTerrain(): runtime density modification with auto-remesh

TerrainChunk (MonoBehaviour, pooled)
  |-- Awake(): one-time allocation of Mesh, density[], NativeArrays
  |-- BeginGeneration(lodStep): schedule chained Burst jobs
  |-- CompleteGeneration(): complete jobs, apply mesh, schedule async bake
  |-- CompletePhysicsBake(): finalize collider
  |-- GenerateMesh(): synchronous path for terrain edits

ProjectileLauncher → Fireball → TerrainManager.EditTerrain()
  |-- On fire: spawn Fireball + PyroParticles VFX child
  |-- On collision: EditTerrain() + SpawnExplosion() + ApplySplashDamage()
  |-- PlayerHealth.TakeDamage() with distance falloff + self-damage reduction
```

## LOD System

| LOD | Step | Voxels/Axis | Points/Axis | Density Samples | Max Vertices |
|-----|------|-------------|-------------|-----------------|--------------|
| 0   | 1    | 16          | 17          | 4,913           | 61,440       |
| 1   | 2    | 8           | 9           | 729             | 7,680        |
| 2   | 4    | 4           | 5           | 125             | 960          |

NativeArrays are always allocated at LOD0 capacity. Lower LODs write to a smaller prefix of the same arrays — no reallocation, no out-of-bounds risk.

## Requirements

- **Unity 6** (6000.3.x or later)
- **Universal Render Pipeline** (URP 17.x)
- **Burst** + **Collections** + **Mathematics** packages (pulled in by default)
- **Input System** 1.19+
- **Kinematic Character Controller** asset (Unity Asset Store)
- **PyroParticles** asset (Unity Asset Store) — fire/explosion VFX

## Configuration

All tuning is exposed on the `TerrainManager` Inspector:

| Parameter | Default | Description |
|-----------|---------|-------------|
| View Distance XZ | 4 | Horizontal chunk radius |
| View Distance Y | 1 | Vertical chunk radius |
| Chunks Per Frame | 4 | Batch size for async pipeline |
| Load Hysteresis | 2 | Movement threshold before reload |
| Collider Distance XZ | 4 | Physics bake radius |
| LOD0 Distance XZ | 2 | Full-resolution radius |
| LOD1 Distance XZ | 4 | Half-resolution radius (beyond = LOD2) |

Combat tuning is on the `ProjectileLauncher` Inspector:

| Parameter | Default | Description |
|-----------|---------|-------------|
| Cooldown | 1.25s | Seconds between shots |
| Speed Override | 25 | Fireball speed (m/s) |
| Gravity Override | (0,0,0) | Custom gravity vector |
| Crater Radius | 1.5 | Terrain deformation radius |
| Crater Delta | 30 | Density edit strength |
| Direct Damage | 40 | Damage on direct hit |
| Splash Damage | 25 | Max splash damage at center |
| Splash Radius | 4 | Splash damage falloff radius |
| Self Damage Multiplier | 0.5 | Self-splash reduction |

## License

Private repository. All rights reserved.

