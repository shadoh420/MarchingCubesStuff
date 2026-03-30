using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Manages an infinite volumetric terrain around the player.
///
/// ARCHITECTURE:
///   - Tracks active chunks in a Dictionary&lt;Vector3Int, TerrainChunk&gt;.
///   - Maintains a Queue-based Object Pool of deactivated TerrainChunk
///     GameObjects so we never destroy/re-allocate them.
///   - Every frame, checks if the player has moved far enough from the
///     last load center. When they do, a coroutine evaluates which chunks
///     to load/unload using a two-phase async pipeline.
///
/// ASYNC PIPELINE (per batch of chunks):
///   Frame N  : BeginGeneration() — fills density + schedules Burst job.
///   Frame N+1: CompleteGeneration() — applies mesh + optionally bakes collider.
///   This lets Burst jobs run on worker threads during the frame gap.
///
/// MEMORY CONTRACT:
///   - TerrainChunk.Awake() allocates density[], Mesh, and persistent
///     NativeArrays ONCE. All are reused across pool cycles.
///   - Shared look-up tables are ref-counted and disposed on last OnDestroy.
/// </summary>
public class TerrainManager : MonoBehaviour
{
    // ── Player reference ─────────────────────────────────────────────
    [Header("Player")]
    [Tooltip("The Transform the terrain follows. If null, searches for 'Player' tag at Start.")]
    public Transform player;

    // ── View distance ────────────────────────────────────────────────
    [Header("View Distance (chunks)")]
    [Tooltip("Horizontal radius in chunk coordinates around the player.")]
    public int viewDistanceXZ = 4;
    [Tooltip("Vertical radius in chunk coordinates around the player.")]
    public int viewDistanceY  = 1;

    // ── Performance ──────────────────────────────────────────────────
    [Header("Performance")]
    [Tooltip("Max chunks to schedule per batch. Jobs run on workers during the yield, then complete next frame.")]
    public int chunksPerFrame = 4;
    [Tooltip("Max chunks to deactivate per frame during the unload phase.")]
    public int unloadsPerFrame = 32;
    [Tooltip("Number of inactive chunks to pre-instantiate in the pool at Start.")]
    public int poolWarmupCount = 16;
    [Tooltip("Player must move this many chunks from the last load center before a new load pass triggers. Prevents constant reloads while moving.")]
    public int loadHysteresis = 2;

    [Header("Collider Distance (chunks)")]
    [Tooltip("Horizontal radius within which MeshColliders are baked. Beyond this, chunks are visual-only (huge perf win).")]
    public int colliderDistanceXZ = 4;
    [Tooltip("Vertical radius within which MeshColliders are baked.")]
    public int colliderDistanceY  = 2;

    [Header("LOD Distance (chunks)")]
    [Tooltip("Chunks within this Chebyshev radius use LOD0 (step=1, full resolution).")]
    public int lod0DistanceXZ = 2;
    [Tooltip("Chunks within this radius use LOD1 (step=2, half resolution). Beyond this uses LOD2 (step=4). Only applies beyond collider distance.")]
    public int lod1DistanceXZ = 4;

    // ── Visuals ──────────────────────────────────────────────────────
    [Header("Material")]
    public Material terrainMaterial;

    // ── Noise parameters forwarded to every chunk ────────────────────
    [Header("Noise Settings")]
    public float noiseScale = 0.05f;
    public float amplitude  = 10f;
    public float surfaceY   = 8f;

    // ── Internal state ───────────────────────────────────────────────
    private readonly Dictionary<Vector3Int, TerrainChunk> activeChunks =
        new Dictionary<Vector3Int, TerrainChunk>();

    private readonly Queue<TerrainChunk> chunkPool =
        new Queue<TerrainChunk>();

    private Vector3Int lastLoadCenter = new Vector3Int(int.MinValue, 0, 0);
    private Coroutine  loadingRoutine;
    private int        loadGeneration;

    // Reusable lists to avoid GC alloc every update
    private readonly List<Vector3Int> coordsToRemove = new List<Vector3Int>();
    private readonly List<TerrainChunk> pendingChunks = new List<TerrainChunk>();
    private readonly List<Vector3Int> sortedCoords = new List<Vector3Int>();
    private readonly List<TerrainChunk> pendingBakeChunks = new List<TerrainChunk>();

    // Distance-sort state (avoids lambda allocation on every sort)
    private Vector3Int sortCenter;
    private System.Comparison<Vector3Int> cachedDistanceComparer;

    // =================================================================
    //  Lifecycle
    // =================================================================

    private void Start()
    {
        if (player == null)
        {
            GameObject go = GameObject.FindWithTag("Player");
            if (go != null) player = go.transform;
        }

        if (player == null)
        {
            Debug.LogWarning("[TerrainManager] No player assigned and none found with 'Player' tag. " +
                             "Using this Transform as the center.");
            player = transform;
        }

        // Safety: collider distance must cover the full view distance so
        // every visible chunk is walkable.  Clamp up if misconfigured.
        if (colliderDistanceXZ < viewDistanceXZ)
        {
            Debug.LogWarning($"[TerrainManager] colliderDistanceXZ ({colliderDistanceXZ}) " +
                             $"< viewDistanceXZ ({viewDistanceXZ}). Clamping up to avoid " +
                             "visible terrain with no colliders.");
            colliderDistanceXZ = viewDistanceXZ;
        }
        if (colliderDistanceY < viewDistanceY)
            colliderDistanceY = viewDistanceY;

        cachedDistanceComparer = CompareByDistance;

        WarmUpPool();

        // Force an immediate terrain load around the starting position
        lastLoadCenter = WorldToChunkCoord(player.position);
        loadGeneration++;
        loadingRoutine = StartCoroutine(LoadTerrain(lastLoadCenter, loadGeneration));
    }

    private void Update()
    {
        if (player == null) return;

        Vector3Int currentCoord = WorldToChunkCoord(player.position);

        // Only trigger a new load pass when the player has moved far enough
        // from where we last centered the terrain. This prevents constant
        // coroutine restarts while moving continuously.
        int dx = Mathf.Abs(currentCoord.x - lastLoadCenter.x);
        int dy = Mathf.Abs(currentCoord.y - lastLoadCenter.y);
        int dz = Mathf.Abs(currentCoord.z - lastLoadCenter.z);

        if (dx >= loadHysteresis || dy >= loadHysteresis || dz >= loadHysteresis)
        {
            lastLoadCenter = currentCoord;

            // Bump generation — any in-flight coroutine will see the mismatch
            // at its next yield point and exit cleanly (no hard StopCoroutine).
            loadGeneration++;
            loadingRoutine = StartCoroutine(LoadTerrain(currentCoord, loadGeneration));
        }
    }

    // =================================================================
    //  Terrain loading coroutine
    // =================================================================

    /// <summary>
    /// 1. Deactivate and pool any active chunks that fell outside the
    ///    view distance (batched across frames).
    /// 2. Collect ALL missing chunk coordinates, sort by squared distance
    ///    from the player so nearby chunks load first.
    /// 3. Process sorted list in batches with a three-phase pipeline:
    ///      Frame N  : BeginGeneration() — schedule Density + MC Burst jobs.
    ///      Frame N+1: CompleteGeneration() — apply mesh + schedule async bake.
    ///      Frame N+2: CompletePhysicsBake() — assign collider (cheap swap).
    ///    Bake jobs from batch N overlap with MC jobs from batch N+1.
    /// </summary>
    private IEnumerator LoadTerrain(Vector3Int center, int generation)
    {
        // ── Safety: complete any in-flight work from a cancelled coroutine ──
        // Without this, chunks that had physics bakes scheduled but never
        // completed would end up visible but with NO collider.
        CompletePendingBakes();
        CompletePendingBatchOrphan();

        // ── Phase 1: Unload far-away chunks (batched) ────────────────
        coordsToRemove.Clear();

        foreach (var kvp in activeChunks)
        {
            Vector3Int coord = kvp.Key;
            int dx = Mathf.Abs(coord.x - center.x);
            int dy = Mathf.Abs(coord.y - center.y);
            int dz = Mathf.Abs(coord.z - center.z);

            if (dx > viewDistanceXZ || dy > viewDistanceY || dz > viewDistanceXZ)
                coordsToRemove.Add(coord);
        }

        int unloaded = 0;
        for (int i = 0; i < coordsToRemove.Count; i++)
        {
            ReturnChunkToPool(coordsToRemove[i]);
            unloaded++;

            if (unloaded >= unloadsPerFrame)
            {
                unloaded = 0;
                if (generation != loadGeneration) yield break;
                yield return null;
            }
        }

        // ── Phase 2: Collect + sort missing coords by distance ───────
        sortedCoords.Clear();
        sortCenter = center;

        for (int x = -viewDistanceXZ; x <= viewDistanceXZ; x++)
        for (int y = -viewDistanceY;  y <= viewDistanceY;  y++)
        for (int z = -viewDistanceXZ; z <= viewDistanceXZ; z++)
        {
            Vector3Int coord = new Vector3Int(center.x + x, center.y + y, center.z + z);
            if (!activeChunks.ContainsKey(coord))
                sortedCoords.Add(coord);
        }

        sortedCoords.Sort(cachedDistanceComparer);

        // ── Phase 3: Load sorted chunks (schedule → yield → complete) ──
        pendingChunks.Clear();
        pendingBakeChunks.Clear();

        for (int idx = 0; idx < sortedCoords.Count; idx++)
        {
            Vector3Int coord = sortedCoords[idx];

            // ── Skip chunks guaranteed to produce zero geometry ─────────
            float chunkWorldYMin = coord.y * TerrainChunk.ChunkSize;
            float chunkWorldYMax = chunkWorldYMin + TerrainChunk.ChunkSize;
            if (chunkWorldYMin > surfaceY + amplitude ||
                chunkWorldYMax < surfaceY - amplitude)
            {
                activeChunks[coord] = null;
                continue;
            }

            // ── Schedule the Burst job with LOD based on distance ──────
            int lodStep = GetLodStep(coord, center);
            TerrainChunk chunk = GetChunkFromPool();
            chunk.gameObject.name = $"Chunk_{coord.x}_{coord.y}_{coord.z}";
            chunk.BeginGeneration(coord, terrainMaterial, noiseScale, amplitude, surfaceY, lodStep);

            activeChunks[coord] = chunk;
            pendingChunks.Add(chunk);

            // When we've filled a batch, yield so the Burst jobs can
            // execute on worker threads across the frame boundary.
            if (pendingChunks.Count >= chunksPerFrame)
            {
                yield return null;  // ← MC jobs + previous bake jobs run

                if (generation != loadGeneration) yield break;

                // Complete previous batch's async physics bakes
                CompletePendingBakes();

                // Complete current batch: MC → mesh → schedule new bakes
                CompletePendingBatch(center);
            }
        }

        // Complete any remaining partial batch
        if (pendingChunks.Count > 0)
        {
            yield return null;
            if (generation != loadGeneration) yield break;
            CompletePendingBakes();
            CompletePendingBatch(center);
        }

        // Final yield to let the last batch's physics bakes finish
        if (pendingBakeChunks.Count > 0)
        {
            yield return null;
            if (generation != loadGeneration) yield break;
            CompletePendingBakes();
        }

        loadingRoutine = null;
    }

    /// <summary>
    /// Completes all pending Burst jobs, applies their meshes, activates
    /// the GameObjects, schedules async physics bakes for nearby chunks,
    /// and tracks those chunks in pendingBakeChunks for later completion.
    /// </summary>
    private void CompletePendingBatch(Vector3Int center)
    {
        for (int i = 0; i < pendingChunks.Count; i++)
        {
            TerrainChunk chunk = pendingChunks[i];
            Vector3Int c = chunk.ChunkCoord;

            bool nearPlayer = Mathf.Abs(c.x - center.x) <= colliderDistanceXZ &&
                              Mathf.Abs(c.y - center.y) <= colliderDistanceY  &&
                              Mathf.Abs(c.z - center.z) <= colliderDistanceXZ;

            chunk.CompleteGeneration(nearPlayer);
            chunk.gameObject.SetActive(true);

            // Track chunks that may have async bakes in flight
            if (nearPlayer)
                pendingBakeChunks.Add(chunk);
        }
        pendingChunks.Clear();
    }

    /// <summary>
    /// Completes all pending async physics bakes from the previous batch.
    /// Each chunk's CompletePhysicsBake() is a no-op if no bake was scheduled.
    /// </summary>
    private void CompletePendingBakes()
    {
        for (int i = 0; i < pendingBakeChunks.Count; i++)
            pendingBakeChunks[i].CompletePhysicsBake();
        pendingBakeChunks.Clear();
    }

    /// <summary>
    /// Completes any orphaned chunks that had BeginGeneration() called
    /// but were never completed due to a soft-cancel (generation mismatch).
    /// Without this, the chunks stay in activeChunks with no mesh and no collider.
    /// </summary>
    private void CompletePendingBatchOrphan()
    {
        if (pendingChunks.Count == 0) return;

        for (int i = 0; i < pendingChunks.Count; i++)
        {
            TerrainChunk chunk = pendingChunks[i];
            if (chunk == null) continue;

            Vector3Int c = chunk.ChunkCoord;
            bool nearPlayer = Mathf.Abs(c.x - lastLoadCenter.x) <= colliderDistanceXZ &&
                              Mathf.Abs(c.y - lastLoadCenter.y) <= colliderDistanceY  &&
                              Mathf.Abs(c.z - lastLoadCenter.z) <= colliderDistanceXZ;

            chunk.CompleteGeneration(nearPlayer);
            chunk.gameObject.SetActive(true);

            if (nearPlayer)
                pendingBakeChunks.Add(chunk);
        }
        pendingChunks.Clear();
    }

    /// <summary>
    /// Returns the LOD vertex step for a chunk based on its distance
    /// from the load center.  All chunks within collider distance are
    /// forced to LOD0 (step=1) so that neighbouring full-resolution
    /// meshes share identical edge vertices — eliminating the physical
    /// gaps that let the player fall through LOD-boundary seams.
    /// LOD1/LOD2 only apply to visual-only chunks beyond collider range.
    /// </summary>
    private int GetLodStep(Vector3Int coord, Vector3Int center)
    {
        int dx = Mathf.Abs(coord.x - center.x);
        int dy = Mathf.Abs(coord.y - center.y);
        int dz = Mathf.Abs(coord.z - center.z);

        // Force LOD0 for every chunk that will receive a collider.
        // This guarantees matching vertices at chunk borders so the
        // player cannot fall through LOD-boundary gaps.
        if (dx <= colliderDistanceXZ && dy <= colliderDistanceY && dz <= colliderDistanceXZ)
            return 1;

        int distXZ = Mathf.Max(dx, dz);
        if (distXZ <= lod1DistanceXZ) return 2;
        return 4;
    }

    /// <summary>
    /// Cached comparison delegate for sorting chunk coordinates by
    /// squared distance from sortCenter. Zero GC alloc per sort.
    /// </summary>
    private int CompareByDistance(Vector3Int a, Vector3Int b)
    {
        int ax = a.x - sortCenter.x, ay = a.y - sortCenter.y, az = a.z - sortCenter.z;
        int bx = b.x - sortCenter.x, by = b.y - sortCenter.y, bz = b.z - sortCenter.z;
        return (ax * ax + ay * ay + az * az).CompareTo(bx * bx + by * by + bz * bz);
    }

    // =================================================================
    //  Object Pool
    // =================================================================

    /// <summary>
    /// Pre-instantiate a batch of inactive chunk GameObjects so the first
    /// loading pass doesn't need to call Instantiate for every chunk.
    /// </summary>
    private void WarmUpPool()
    {
        for (int i = 0; i < poolWarmupCount; i++)
        {
            TerrainChunk chunk = CreateChunkGameObject();
            chunk.gameObject.SetActive(false);
            chunkPool.Enqueue(chunk);
        }

        Debug.Log($"[TerrainManager] Pool warmed up with {poolWarmupCount} chunks.");
    }

    /// <summary>
    /// Returns an idle TerrainChunk from the pool, or instantiates a
    /// new one if the pool is empty.
    /// </summary>
    private TerrainChunk GetChunkFromPool()
    {
        if (chunkPool.Count > 0)
            return chunkPool.Dequeue();

        return CreateChunkGameObject();
    }

    /// <summary>
    /// Deactivates a chunk, removes it from activeChunks, and pushes
    /// it back into the pool for later reuse.
    /// </summary>
    private void ReturnChunkToPool(Vector3Int coord)
    {
        if (!activeChunks.TryGetValue(coord, out TerrainChunk chunk))
            return;

        activeChunks.Remove(coord);

        // Null entries are empty-sky/solid markers — no pooled chunk to return
        if (chunk == null) return;

        chunk.gameObject.SetActive(false);
        chunkPool.Enqueue(chunk);
    }

    /// <summary>
    /// Low-level factory — creates the GameObject + TerrainChunk component.
    /// Awake() on TerrainChunk handles all one-time allocations.
    /// </summary>
    private TerrainChunk CreateChunkGameObject()
    {
        GameObject go = new GameObject("PooledChunk");
        go.transform.SetParent(transform, false);
        TerrainChunk chunk = go.AddComponent<TerrainChunk>();
        return chunk;
    }

    // =================================================================
    //  Public API
    // =================================================================

    /// <summary>
    /// Sets the player transform that terrain loading follows.
    /// Called by <see cref="NetworkPlayerSetup"/> when the local player spawns.
    /// </summary>
    public void SetPlayerTransform(Transform t)
    {
        player = t;
    }

    /// <summary>
    /// Returns the chunk at the given grid coordinate, or null.
    /// </summary>
    public TerrainChunk GetChunk(Vector3Int coord)
    {
        activeChunks.TryGetValue(coord, out TerrainChunk chunk);
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
    /// Synchronously generates a single chunk at the given coordinate.
    /// Used by <see cref="PlayerSpawnManager"/> to guarantee colliders
    /// exist before the player is placed.  Uses LOD0 (step=1) and
    /// always bakes a MeshCollider.
    /// </summary>
    public void GenerateChunkImmediate(Vector3Int coord)
    {
        // Skip if already active
        if (activeChunks.ContainsKey(coord)) return;

        // Skip empty-sky chunks
        float chunkWorldYMin = coord.y * TerrainChunk.ChunkSize;
        float chunkWorldYMax = chunkWorldYMin + TerrainChunk.ChunkSize;
        if (chunkWorldYMin > surfaceY + amplitude ||
            chunkWorldYMax < surfaceY - amplitude)
        {
            activeChunks[coord] = null;
            return;
        }

        TerrainChunk chunk = GetChunkFromPool();
        chunk.gameObject.name = $"Chunk_{coord.x}_{coord.y}_{coord.z}";

        // Full-resolution synchronous generation
        chunk.BeginGeneration(coord, terrainMaterial, noiseScale, amplitude, surfaceY, 1);
        chunk.CompleteGeneration(true);          // mesh + schedule bake
        chunk.CompletePhysicsBake();             // finish bake immediately
        chunk.gameObject.SetActive(true);

        activeChunks[coord] = chunk;
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

            // Guarantee the managed density array has valid LOD0 data
            // before we modify it.  No-op for chunks already at LOD0.
            chunk.EnsureFullResolutionDensity();

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
