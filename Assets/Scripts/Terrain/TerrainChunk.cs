using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Terrain;

/// <summary>
/// Represents one chunk of the volumetric terrain.
/// Owns a density field, schedules a Burst-compiled Marching Cubes job,
/// and assigns the resulting mesh to its MeshFilter and MeshCollider.
///
/// POOL-FRIENDLY LIFECYCLE:
///   Awake()              → allocates components, mesh, density array,
///                          and persistent NativeArrays ONCE.
///   BeginGeneration()    → fills density, copies to native, schedules the
///                          Marching Cubes job without blocking.
///   CompleteGeneration() → completes the job on a later frame, applies mesh.
///   GenerateMesh()       → synchronous convenience (for TerrainEditor).
///   OnDestroy()          → completes pending jobs, disposes all NativeArrays.
///
/// ASYNC PIPELINE:
///   Frame N  : BeginGeneration() — schedules Burst job on worker threads.
///   Frame N+1: CompleteGeneration() — extracts mesh, optionally bakes collider.
///   This lets the Burst job run across the entire frame gap instead of
///   blocking the main thread with handle.Complete().
///
/// Persistent NativeArrays (densities, vertices, vertexCounts) are allocated
/// once in Awake() and reused every generation cycle — zero alloc per frame.
/// </summary>
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider))]
public class TerrainChunk : MonoBehaviour
{
    // ── Tuning constants ─────────────────────────────────────────────
    public const int ChunkSize = 16;            // voxels per axis
    public const float IsoLevel = 0f;           // surface threshold

    // ── Noise parameters (exposed for the TerrainManager) ────────────
    [HideInInspector] public float noiseScale  = 0.05f;
    [HideInInspector] public float amplitude   = 10f;
    [HideInInspector] public float surfaceY    = 8f;

    // ── Derived ──────────────────────────────────────────────────────
    public static int NumPointsPerAxis => ChunkSize + 1;
    public static int TotalPoints      => NumPointsPerAxis * NumPointsPerAxis * NumPointsPerAxis;
    public static int TotalVoxels      => ChunkSize * ChunkSize * ChunkSize;

    // ── Per-chunk state ──────────────────────────────────────────────
    public Vector3Int ChunkCoord { get; private set; }
    private float[] densityField;

    private MeshFilter   meshFilter;
    private MeshRenderer meshRenderer;
    private MeshCollider meshCollider;
    private Mesh         mesh;

    // ── Persistent job I/O (allocated once in Awake, reused) ─────────
    private NativeArray<float>  nativeDensities;
    private NativeArray<float3> nativeVertices;
    private NativeArray<int>    nativeVertCounts;
    private JobHandle           pendingJobHandle;
    private bool                hasPendingJob;

    // ── LOD state (set each generation cycle) ────────────────────────
    private int currentLodStep   = 1;
    private int lodPointsPerAxis = ChunkSize + 1;
    private int lodVoxelsPerAxis = ChunkSize;
    private int lodVoxelCount    = ChunkSize * ChunkSize * ChunkSize;

    // ── Async physics bake ───────────────────────────────────────────
    private JobHandle pendingBakeHandle;
    private bool      hasPendingBake;

    // ── Density field validity ──────────────────────────────────────
    /// <summary>
    /// True when the managed densityField[] contains valid full-resolution
    /// (LOD0) data.  LOD1/LOD2 chunks only fill the NativeArray at reduced
    /// resolution, leaving the managed array stale.  Any code that reads or
    /// writes densityField (e.g. terrain editing) must call
    /// <see cref="EnsureFullResolutionDensity"/> first.
    /// </summary>
    private bool _densityFieldPopulated;

    // ── Shared look-up tables (allocated once, ref-counted) ──────────
    private static NativeArray<int> s_EdgeTable;
    private static NativeArray<int> s_TriTable;          // flattened 256×16
    private static NativeArray<int> s_CornerIndexA;
    private static NativeArray<int> s_CornerIndexB;
    private static int s_RefCount;

    // =================================================================
    //  Unity Lifecycle — one-time setup
    // =================================================================

    /// <summary>
    /// Called exactly once per GameObject.
    /// Allocates the density array, mesh, and caches components.
    /// After this, Initialize() can be called any number of times
    /// without triggering new allocations.
    /// </summary>
    private void Awake()
    {
        meshFilter   = GetComponent<MeshFilter>();
        meshRenderer = GetComponent<MeshRenderer>();
        meshCollider = GetComponent<MeshCollider>();

        mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        meshFilter.sharedMesh = mesh;

        densityField = new float[TotalPoints];

        int maxVerts = TotalVoxels * 15;
        nativeDensities  = new NativeArray<float>(TotalPoints, Allocator.Persistent);
        nativeVertices   = new NativeArray<float3>(maxVerts, Allocator.Persistent);
        nativeVertCounts = new NativeArray<int>(TotalVoxels, Allocator.Persistent);

        AllocateSharedTables();
    }

    // =================================================================
    //  Public API
    // =================================================================

    /// <summary>
    /// Returns the raw density array so callers (e.g. TerrainEditor,
    /// TerrainManager border-stitching) can read or modify it.
    /// </summary>
    public float[] GetDensityField() => densityField;

    /// <summary>
    /// Replace the density array wholesale (used for border stitching).
    /// Does NOT regenerate the mesh automatically — call GenerateMesh().
    /// </summary>
    public void SetDensityField(float[] field)
    {
        densityField = field;
    }

    /// <summary>
    /// Modify a single density sample by index.
    /// </summary>
    public void SetDensity(int flatIndex, float value)
    {
        densityField[flatIndex] = value;
    }

    /// <summary>
    /// Convert local (x,y,z) density-grid coords to a flat array index.
    /// </summary>
    public static int FlatIndex(int x, int y, int z)
    {
        return x + y * NumPointsPerAxis + z * NumPointsPerAxis * NumPointsPerAxis;
    }

    // =================================================================
    //  Async two-phase mesh generation
    // =================================================================

    /// <summary>
    /// PHASE 1 — Called by TerrainManager's loading coroutine.
    /// Sets chunk params, then schedules a chained DensityJob → MCJob
    /// pipeline that runs ENTIRELY on worker threads.
    /// The main thread only records the schedule (microseconds) and returns.
    /// Call <see cref="CompleteGeneration"/> on a later frame.
    /// </summary>
    public void BeginGeneration(Vector3Int coord, Material mat,
                                float noiseScale, float amplitude, float surfaceY,
                                int lodStep = 1)
    {
        ForceCompletePendingJob();

        ChunkCoord      = coord;
        this.noiseScale = noiseScale;
        this.amplitude  = amplitude;
        this.surfaceY   = surfaceY;

        // Mark density as stale — will be set true in CompleteGeneration
        // only for LOD0, or by EnsureFullResolutionDensity on demand.
        _densityFieldPopulated = false;

        // Compute LOD-derived sizes (arrays stay at max LOD0 capacity)
        currentLodStep   = lodStep;
        lodVoxelsPerAxis = ChunkSize / lodStep;
        lodPointsPerAxis = lodVoxelsPerAxis + 1;
        lodVoxelCount    = lodVoxelsPerAxis * lodVoxelsPerAxis * lodVoxelsPerAxis;

        if (mat != null) meshRenderer.sharedMaterial = mat;

        transform.position = new Vector3(
            coord.x * ChunkSize,
            coord.y * ChunkSize,
            coord.z * ChunkSize);

        ScheduleDensityAndMCJobs();
    }

    /// <summary>
    /// PHASE 2 — Completes the pending Burst job, compacts the output
    /// into a Unity Mesh, and optionally bakes the MeshCollider.
    /// </summary>
    /// <param name="bakeCollider">
    /// If false the MeshCollider is nulled out (saves a costly bake for
    /// chunks far from the player that don't need physics).
    /// </param>
    public void CompleteGeneration(bool bakeCollider = true)
    {
        if (!hasPendingJob) return;

        pendingJobHandle.Complete();
        hasPendingJob = false;

        // Sync density back for LOD0 so the managed array is ready for
        // terrain edits.  LOD1/LOD2 use fewer samples so we cannot copy
        // directly — EnsureFullResolutionDensity() handles that on demand.
        if (currentLodStep == 1)
        {
            nativeDensities.CopyTo(densityField);
            _densityFieldPopulated = true;
        }

        int vertCount = ApplyMeshFromNativeArrays();

        // Clear any previous collider
        meshCollider.sharedMesh = null;

        if (bakeCollider && vertCount > 0)
        {
            // Schedule async physics bake on a worker thread
            var bakeJob = new PhysicsBakeJob
            {
                meshInstanceId = mesh.GetEntityId(),
                convex         = false
            };
            pendingBakeHandle = bakeJob.Schedule();
            hasPendingBake    = true;
        }
    }

    /// <summary>
    /// PHASE 3 — Completes the async physics bake and assigns the
    /// pre-baked mesh to the MeshCollider (cheap pointer swap).
    /// Call this after yielding a frame so the bake job has time to run.
    /// </summary>
    public void CompletePhysicsBake()
    {
        if (!hasPendingBake) return;
        pendingBakeHandle.Complete();
        hasPendingBake = false;
        meshCollider.sharedMesh = mesh;
    }

    // =================================================================
    //  Density field guarantee
    // =================================================================

    /// <summary>
    /// Guarantees that the managed <c>densityField[]</c> contains valid
    /// full-resolution (LOD0) density data.  If the chunk was loaded at
    /// LOD1/LOD2 the managed array is still all-zeros; this method
    /// synchronously runs a DensityJob at step=1 to populate it.
    ///
    /// Call this before any code that reads or writes <c>densityField</c>
    /// (e.g. <see cref="TerrainManager.EditTerrain"/>).
    /// No-op if density is already valid.
    /// </summary>
    public void EnsureFullResolutionDensity()
    {
        if (_densityFieldPopulated) return;

        int fullPoints = NumPointsPerAxis * NumPointsPerAxis * NumPointsPerAxis;

        float3 worldOff = new float3(
            ChunkCoord.x * ChunkSize,
            ChunkCoord.y * ChunkSize,
            ChunkCoord.z * ChunkSize);

        var densityJob = new DensityJob
        {
            numPointsPerAxis = NumPointsPerAxis,
            worldOffset      = worldOff,
            vertexStep       = 1,
            noiseScale       = noiseScale,
            amplitude        = amplitude,
            surfaceY         = surfaceY,
            densities        = nativeDensities
        };

        densityJob.Schedule(fullPoints, 64).Complete();
        nativeDensities.CopyTo(densityField);
        _densityFieldPopulated = true;
    }

    // =================================================================
    //  Synchronous mesh generation (for TerrainEditor edits)
    // =================================================================

    /// <summary>
    /// Synchronous path: copies the managed density array into the
    /// persistent NativeArray and immediately runs the MC job.
    /// Use this after a density edit (TerrainEditor). Always bakes collider.
    /// </summary>
    public void GenerateMesh()
    {
        ForceCompletePendingJob();

        // Safety: if this chunk was loaded at LOD1/LOD2, its managed
        // densityField is stale. Regenerate it at full resolution first.
        EnsureFullResolutionDensity();

        // Sync path always uses full LOD0 resolution
        currentLodStep   = 1;
        lodVoxelsPerAxis = ChunkSize;
        lodPointsPerAxis = NumPointsPerAxis;
        lodVoxelCount    = TotalVoxels;

        ScheduleMCJobOnly();
        pendingJobHandle.Complete();
        hasPendingJob = false;

        int vertCount = ApplyMeshFromNativeArrays();

        // Blocking bake is acceptable for interactive edits
        meshCollider.sharedMesh = null;
        if (vertCount > 0)
        {
            Physics.BakeMesh(mesh.GetEntityId(), false);
            meshCollider.sharedMesh = mesh;
        }
    }

    // =================================================================
    //  Internal helpers
    // =================================================================

    /// <summary>
    /// Schedules DensityJob → MarchingCubesJob as a chained dependency.
    /// Both run entirely on worker threads. Used by BeginGeneration().
    /// </summary>
    private void ScheduleDensityAndMCJobs()
    {
        int lodTotalPoints = lodPointsPerAxis * lodPointsPerAxis * lodPointsPerAxis;

        float3 worldOff = new float3(
            ChunkCoord.x * ChunkSize,
            ChunkCoord.y * ChunkSize,
            ChunkCoord.z * ChunkSize);

        var densityJob = new DensityJob
        {
            numPointsPerAxis = lodPointsPerAxis,
            worldOffset      = worldOff,
            vertexStep       = currentLodStep,
            noiseScale       = noiseScale,
            amplitude        = amplitude,
            surfaceY         = surfaceY,
            densities        = nativeDensities
        };

        JobHandle densityHandle = densityJob.Schedule(lodTotalPoints, 64);

        var mcJob = new MarchingCubesJob
        {
            chunkSize            = lodVoxelsPerAxis,
            numPointsPerAxis     = lodPointsPerAxis,
            isoLevel             = IsoLevel,
            vertexStep           = currentLodStep,
            densities            = nativeDensities,
            edgeTable            = s_EdgeTable,
            triTable             = s_TriTable,
            cornerIndexAFromEdge = s_CornerIndexA,
            cornerIndexBFromEdge = s_CornerIndexB,
            vertices             = nativeVertices,
            vertexCountPerVoxel  = nativeVertCounts
        };

        // MC job depends on density — won't start until density finishes
        pendingJobHandle = mcJob.Schedule(lodVoxelCount, 64, densityHandle);
        hasPendingJob = true;
    }

    /// <summary>
    /// Copies managed density[] → native, then schedules only the MC job.
    /// Used by GenerateMesh() after terrain edits (density already modified
    /// in the managed array by TerrainEditor).
    /// </summary>
    private void ScheduleMCJobOnly()
    {
        nativeDensities.CopyFrom(densityField);

        var mcJob = new MarchingCubesJob
        {
            chunkSize            = lodVoxelsPerAxis,
            numPointsPerAxis     = lodPointsPerAxis,
            isoLevel             = IsoLevel,
            vertexStep           = currentLodStep,
            densities            = nativeDensities,
            edgeTable            = s_EdgeTable,
            triTable             = s_TriTable,
            cornerIndexAFromEdge = s_CornerIndexA,
            cornerIndexBFromEdge = s_CornerIndexB,
            vertices             = nativeVertices,
            vertexCountPerVoxel  = nativeVertCounts
        };

        pendingJobHandle = mcJob.Schedule(lodVoxelCount, 64);
        hasPendingJob = true;
    }

    /// <summary>
    /// Reads the completed job output from the persistent NativeArrays,
    /// compacts it into a Unity Mesh, and optionally assigns the collider.
    /// Must only be called after the job handle has been completed.
    /// </summary>
    private int ApplyMeshFromNativeArrays()
    {
        int totalVerts = 0;
        for (int i = 0; i < lodVoxelCount; i++)
            totalVerts += nativeVertCounts[i];

        var meshVerts = new Vector3[totalVerts];
        var meshTris  = new int[totalVerts];

        int vi = 0;
        for (int i = 0; i < lodVoxelCount; i++)
        {
            int count = nativeVertCounts[i];
            int baseIdx = i * 15;
            for (int j = 0; j < count; j++)
            {
                float3 v = nativeVertices[baseIdx + j];
                meshVerts[vi] = new Vector3(v.x, v.y, v.z);
                meshTris[vi]  = vi;
                vi++;
            }
        }

        mesh.Clear();
        mesh.vertices  = meshVerts;
        mesh.triangles = meshTris;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        return totalVerts;
    }

    /// <summary>
    /// Safety net: if a job is still in flight (e.g. the chunk is being
    /// recycled or destroyed before the coroutine could complete it),
    /// force-complete it now so the NativeArrays are safe to reuse.
    /// </summary>
    private void ForceCompletePendingJob()
    {
        if (hasPendingBake)
        {
            pendingBakeHandle.Complete();
            hasPendingBake = false;
        }
        if (!hasPendingJob) return;
        pendingJobHandle.Complete();
        hasPendingJob = false;
    }

    // =================================================================
    //  Shared look-up table management
    // =================================================================

    private static void AllocateSharedTables()
    {
        s_RefCount++;
        if (s_RefCount > 1) return;   // already allocated

        s_EdgeTable   = new NativeArray<int>(MarchingCubesTables.EdgeTable,          Allocator.Persistent);
        s_CornerIndexA = new NativeArray<int>(MarchingCubesTables.CornerIndexAFromEdge, Allocator.Persistent);
        s_CornerIndexB = new NativeArray<int>(MarchingCubesTables.CornerIndexBFromEdge, Allocator.Persistent);

        // Flatten the 2-D TriTable[256,16] → 1-D array of length 4096
        int[] flat = new int[256 * 16];
        for (int i = 0; i < 256; i++)
            for (int j = 0; j < 16; j++)
                flat[i * 16 + j] = MarchingCubesTables.TriTable[i, j];

        s_TriTable = new NativeArray<int>(flat, Allocator.Persistent);
    }

    private static void ReleaseSharedTables()
    {
        s_RefCount--;
        if (s_RefCount > 0) return;

        if (s_EdgeTable.IsCreated)   s_EdgeTable.Dispose();
        if (s_TriTable.IsCreated)    s_TriTable.Dispose();
        if (s_CornerIndexA.IsCreated) s_CornerIndexA.Dispose();
        if (s_CornerIndexB.IsCreated) s_CornerIndexB.Dispose();
        s_RefCount = 0;
    }

    // =================================================================
    //  Lifecycle
    // =================================================================

    private void OnDestroy()
    {
        ForceCompletePendingJob();

        if (nativeDensities.IsCreated)  nativeDensities.Dispose();
        if (nativeVertices.IsCreated)   nativeVertices.Dispose();
        if (nativeVertCounts.IsCreated) nativeVertCounts.Dispose();

        ReleaseSharedTables();
        if (mesh != null) Destroy(mesh);
    }
}
