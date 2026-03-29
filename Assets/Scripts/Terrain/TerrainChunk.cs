using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Terrain;

/// <summary>
/// Represents one chunk of the volumetric terrain.
/// Owns a density field, schedules a Burst-compiled Marching Cubes job,
/// and assigns the resulting mesh to its MeshFilter and MeshCollider.
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

    // ── Shared look-up tables (allocated once, ref-counted) ──────────
    private static NativeArray<int> s_EdgeTable;
    private static NativeArray<int> s_TriTable;          // flattened 256×16
    private static NativeArray<int> s_CornerIndexA;
    private static NativeArray<int> s_CornerIndexB;
    private static int s_RefCount;

    // =================================================================
    //  Public API
    // =================================================================

    /// <summary>
    /// Called by TerrainManager after instantiation.
    /// Sets position, fills density, and generates the initial mesh.
    /// </summary>
    public void Initialize(Vector3Int coord, Material mat,
                           float noiseScale, float amplitude, float surfaceY)
    {
        ChunkCoord      = coord;
        this.noiseScale = noiseScale;
        this.amplitude  = amplitude;
        this.surfaceY   = surfaceY;

        // Cache required components
        meshFilter   = GetComponent<MeshFilter>();
        meshRenderer = GetComponent<MeshRenderer>();
        meshCollider = GetComponent<MeshCollider>();

        if (mat != null) meshRenderer.sharedMaterial = mat;

        // Mesh setup (UInt32 to support >65 535 verts)
        mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        meshFilter.sharedMesh = mesh;

        // Ensure shared look-up tables exist
        AllocateSharedTables();

        // Build density → mesh
        densityField = new float[TotalPoints];
        PopulateDensityField();
        GenerateMesh();
    }

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
    //  Mesh generation (schedules the Burst job)
    // =================================================================

    /// <summary>
    /// Runs the Marching Cubes job and rebuilds this chunk's mesh.
    /// Call this whenever the density field has been edited.
    /// </summary>
    public void GenerateMesh()
    {
        int maxVerts = TotalVoxels * 15;  // worst case: 5 tris × 3 verts

        // ── Allocate temp job arrays ─────────────────────────────────
        var nativeDensities   = new NativeArray<float>(densityField, Allocator.TempJob);
        var nativeVertices    = new NativeArray<float3>(maxVerts, Allocator.TempJob);
        var nativeVertCounts  = new NativeArray<int>(TotalVoxels, Allocator.TempJob);

        // ── Configure and schedule ───────────────────────────────────
        var job = new MarchingCubesJob
        {
            chunkSize           = ChunkSize,
            numPointsPerAxis    = NumPointsPerAxis,
            isoLevel            = IsoLevel,
            densities           = nativeDensities,
            edgeTable           = s_EdgeTable,
            triTable            = s_TriTable,
            cornerIndexAFromEdge = s_CornerIndexA,
            cornerIndexBFromEdge = s_CornerIndexB,
            vertices            = nativeVertices,
            vertexCountPerVoxel = nativeVertCounts
        };

        JobHandle handle = job.Schedule(TotalVoxels, 64);
        handle.Complete();

        // ── Compact job output into managed arrays ───────────────────
        int totalVerts = 0;
        for (int i = 0; i < TotalVoxels; i++)
            totalVerts += nativeVertCounts[i];

        var meshVerts = new Vector3[totalVerts];
        var meshTris  = new int[totalVerts];

        int vi = 0;
        for (int i = 0; i < TotalVoxels; i++)
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

        // ── Dispose temporaries ──────────────────────────────────────
        nativeDensities.Dispose();
        nativeVertices.Dispose();
        nativeVertCounts.Dispose();

        // ── Apply to Unity mesh ──────────────────────────────────────
        mesh.Clear();
        mesh.vertices  = meshVerts;
        mesh.triangles = meshTris;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        // Force-rebake the physics collider
        meshCollider.sharedMesh = null;
        meshCollider.sharedMesh = mesh;
    }

    // =================================================================
    //  Density field initialisation (3-D simplex noise)
    // =================================================================

    private void PopulateDensityField()
    {
        Vector3 worldOffset = new Vector3(
            ChunkCoord.x * ChunkSize,
            ChunkCoord.y * ChunkSize,
            ChunkCoord.z * ChunkSize);

        int npa = NumPointsPerAxis;

        for (int z = 0; z < npa; z++)
        {
            for (int y = 0; y < npa; y++)
            {
                for (int x = 0; x < npa; x++)
                {
                    float wx = x + worldOffset.x;
                    float wy = y + worldOffset.y;
                    float wz = z + worldOffset.z;

                    // Base gradient: solid below surfaceY, air above
                    float density = surfaceY - wy;

                    // 3-D simplex noise for organic variation
                    float3 np = new float3(wx, wy, wz) * noiseScale;
                    density += noise.snoise(np) * amplitude;

                    densityField[FlatIndex(x, y, z)] = density;
                }
            }
        }
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
        ReleaseSharedTables();
        if (mesh != null) Destroy(mesh);
    }
}
