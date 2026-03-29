using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Spawns and manages a grid of TerrainChunk objects.
/// Provides world-space density editing that automatically handles
/// border consistency across adjacent chunks.
/// </summary>
public class TerrainManager : MonoBehaviour
{
    // ── Grid dimensions (in chunks) ──────────────────────────────────
    [Header("Grid Size (chunks)")]
    public int gridSizeX = 4;
    public int gridSizeY = 1;
    public int gridSizeZ = 4;

    // ── Visuals ──────────────────────────────────────────────────────
    [Header("Material")]
    public Material terrainMaterial;

    // ── Noise parameters forwarded to every chunk ────────────────────
    [Header("Noise Settings")]
    public float noiseScale = 0.05f;
    public float amplitude  = 10f;
    public float surfaceY   = 8f;

    // ── Internal state ───────────────────────────────────────────────
    private readonly Dictionary<Vector3Int, TerrainChunk> chunks =
        new Dictionary<Vector3Int, TerrainChunk>();

    // =================================================================
    //  Lifecycle
    // =================================================================

    private void Start()
    {
        GenerateGrid();
    }

    // =================================================================
    //  Grid generation
    // =================================================================

    private void GenerateGrid()
    {
        for (int x = 0; x < gridSizeX; x++)
        for (int y = 0; y < gridSizeY; y++)
        for (int z = 0; z < gridSizeZ; z++)
        {
            SpawnChunk(new Vector3Int(x, y, z));
        }

        Debug.Log($"[TerrainManager] Spawned {chunks.Count} chunks " +
                  $"({gridSizeX}×{gridSizeY}×{gridSizeZ}).");
    }

    private void SpawnChunk(Vector3Int coord)
    {
        GameObject go = new GameObject($"Chunk_{coord.x}_{coord.y}_{coord.z}");
        go.transform.SetParent(transform, false);
        go.transform.position = new Vector3(
            coord.x * TerrainChunk.ChunkSize,
            coord.y * TerrainChunk.ChunkSize,
            coord.z * TerrainChunk.ChunkSize);

        TerrainChunk chunk = go.AddComponent<TerrainChunk>();
        chunk.Initialize(coord, terrainMaterial, noiseScale, amplitude, surfaceY);
        chunks[coord] = chunk;
    }

    // =================================================================
    //  Public API
    // =================================================================

    /// <summary>
    /// Returns the chunk at the given grid coordinate, or null.
    /// </summary>
    public TerrainChunk GetChunk(Vector3Int coord)
    {
        chunks.TryGetValue(coord, out TerrainChunk chunk);
        return chunk;
    }

    /// <summary>
    /// Converts a world-space position to the chunk coordinate that contains it.
    /// </summary>
    public static Vector3Int WorldToChunkCoord(Vector3 worldPos)
    {
        int cs = TerrainChunk.ChunkSize;
        return new Vector3Int(
            Mathf.FloorToInt(worldPos.x / cs),
            Mathf.FloorToInt(worldPos.y / cs),
            Mathf.FloorToInt(worldPos.z / cs));
    }

    /// <summary>
    /// Modifies density in a sphere around <paramref name="worldCenter"/>.
    /// Positive <paramref name="delta"/> subtracts density (dig / remove material).
    /// Negative <paramref name="delta"/> adds density (build / fill material).
    /// Automatically affects all overlapping chunks and keeps border
    /// density in sync because the same world-space point is written
    /// identically in every chunk that contains it.
    /// </summary>
    public void EditTerrain(Vector3 worldCenter, float radius, float delta)
    {
        int cs = TerrainChunk.ChunkSize;

        // Chunk-coordinate range the sphere can touch
        int minCX = Mathf.FloorToInt((worldCenter.x - radius) / cs);
        int minCY = Mathf.FloorToInt((worldCenter.y - radius) / cs);
        int minCZ = Mathf.FloorToInt((worldCenter.z - radius) / cs);
        int maxCX = Mathf.FloorToInt((worldCenter.x + radius) / cs);
        int maxCY = Mathf.FloorToInt((worldCenter.y + radius) / cs);
        int maxCZ = Mathf.FloorToInt((worldCenter.z + radius) / cs);

        List<TerrainChunk> dirty = new List<TerrainChunk>();

        for (int cx = minCX; cx <= maxCX; cx++)
        for (int cy = minCY; cy <= maxCY; cy++)
        for (int cz = minCZ; cz <= maxCZ; cz++)
        {
            Vector3Int coord = new Vector3Int(cx, cy, cz);
            TerrainChunk chunk = GetChunk(coord);
            if (chunk == null) continue;

            if (EditChunkDensity(chunk, worldCenter, radius, delta))
                dirty.Add(chunk);
        }

        // Regenerate only the chunks that were actually modified
        for (int i = 0; i < dirty.Count; i++)
            dirty[i].GenerateMesh();
    }

    // =================================================================
    //  Per-chunk sphere edit (private helper)
    // =================================================================

    /// <summary>
    /// Applies a spherical density edit to a single chunk.
    /// Returns true if any sample was changed.
    /// </summary>
    private static bool EditChunkDensity(TerrainChunk chunk,
        Vector3 worldCenter, float radius, float delta)
    {
        int cs  = TerrainChunk.ChunkSize;
        int npa = TerrainChunk.NumPointsPerAxis;
        float[] field = chunk.GetDensityField();

        // Chunk world-space origin (matches transform.position)
        Vector3 origin = new Vector3(
            chunk.ChunkCoord.x * cs,
            chunk.ChunkCoord.y * cs,
            chunk.ChunkCoord.z * cs);

        // Edit center in local density-grid coordinates
        Vector3 local = worldCenter - origin;

        // Tight AABB in grid coords so we only touch relevant samples
        int xMin = Mathf.Max(0,       Mathf.FloorToInt(local.x - radius));
        int yMin = Mathf.Max(0,       Mathf.FloorToInt(local.y - radius));
        int zMin = Mathf.Max(0,       Mathf.FloorToInt(local.z - radius));
        int xMax = Mathf.Min(npa - 1, Mathf.CeilToInt(local.x + radius));
        int yMax = Mathf.Min(npa - 1, Mathf.CeilToInt(local.y + radius));
        int zMax = Mathf.Min(npa - 1, Mathf.CeilToInt(local.z + radius));

        float radiusSqr = radius * radius;
        bool modified = false;

        for (int z = zMin; z <= zMax; z++)
        for (int y = yMin; y <= yMax; y++)
        for (int x = xMin; x <= xMax; x++)
        {
            float dx = x - local.x;
            float dy = y - local.y;
            float dz = z - local.z;
            float distSqr = dx * dx + dy * dy + dz * dz;

            if (distSqr > radiusSqr) continue;

            // Smooth quadratic falloff: full strength at center, zero at edge
            float t = 1f - (distSqr / radiusSqr);
            int idx = TerrainChunk.FlatIndex(x, y, z);
            field[idx] -= delta * t;
            modified = true;
        }

        return modified;
    }
}
