# MCProject — Volumetric Voxel Terrain Engine

A high-performance volumetric terrain engine built in **Unity 6** (6000.3.11f1), inspired by games like *Deep Rock Galactic*. Fully destructible/constructible terrain powered by Marching Cubes, Burst-compiled jobs, and an async generation pipeline.

## Features

### Core Terrain System
- **Marching Cubes** isosurface extraction on a 3D density field
- **Burst-compiled parallel jobs** — density sampling and mesh generation run entirely on worker threads (zero main-thread computation)
- **Object-pooled infinite terrain** — chunks are recycled, never destroyed; NativeArrays allocated once and reused
- **Async two-phase pipeline** — `BeginGeneration()` schedules jobs on Frame N, `CompleteGeneration()` applies the mesh on Frame N+1
- **Soft-cancel via generation counter** — no `StopCoroutine` overhead when the player moves
- **Load hysteresis** — 2-chunk buffer prevents thrashing at chunk boundaries
- **Runtime terrain editing** — spherical brush with smooth quadratic falloff; automatically propagates across chunk borders

### Phase 7: Advanced Rendering Optimizations
- **Distance-prioritized loading** — chunk coordinates sorted by squared distance from the player before generation begins; nearby chunks load first
- **Level of Detail (LOD) via voxel striding** — three LOD tiers (step 1/2/4) reduce triangle count for distant chunks without reallocating NativeArrays
- **True async physics baking** — `Physics.BakeMesh()` runs on a worker thread via `PhysicsBakeJob`; collider assignment is a cheap pointer swap one frame later
- **Collider distance culling** — chunks beyond a configurable radius skip physics entirely

## Project Structure

```
Assets/
  Scripts/
    Jobs/
      DensityJob.cs          # Burst job: 3D simplex noise density sampling
      MarchingCubesJob.cs    # Burst job: isosurface extraction
      PhysicsBakeJob.cs      # IJob: async Physics.BakeMesh on worker thread
    Terrain/
      TerrainManager.cs      # Infinite terrain orchestrator, object pool, LOD tiers
      TerrainChunk.cs        # Per-chunk lifecycle, NativeArray management, job scheduling
    MarchingCubesTables.cs   # Static Marching Cubes lookup tables
  Materials/                 # Terrain materials (triplanar dirt, grass, stone, etc.)
  Scenes/
    SampleScene.unity        # Main scene
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

## License

Private repository. All rights reserved.
