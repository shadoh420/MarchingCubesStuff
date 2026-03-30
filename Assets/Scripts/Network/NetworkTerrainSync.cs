using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Singleton NetworkBehaviour that relays all terrain edits through the server.
///
/// Two entry points:
///   <see cref="RequestTerrainEditRpc"/> — called by clients (e.g., TerrainTool).
///   <see cref="ApplyTerrainEdit"/>      — called directly on the server (e.g., Fireball collision).
///
/// Both funnel into: server applies edit locally → broadcasts parameters
/// to all non-host clients via ClientRpc. Because <see cref="TerrainManager.EditTerrain"/>
/// is deterministic (same params → same result), all clients end up with
/// identical terrain state without syncing density arrays.
/// </summary>
public class NetworkTerrainSync : NetworkBehaviour
{
    // ── Singleton ────────────────────────────────────────────────────
    public static NetworkTerrainSync Instance { get; private set; }

    [Header("References")]
    [Tooltip("TerrainManager in the scene. Assigned automatically if null.")]
    public TerrainManager terrainManager;

    // =================================================================
    //  Lifecycle
    // =================================================================

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    public override void OnNetworkSpawn()
    {
        if (terrainManager == null)
            terrainManager = FindFirstObjectByType<TerrainManager>();
    }

    public override void OnDestroy()
    {
        if (Instance == this) Instance = null;
        base.OnDestroy();
    }

    // =================================================================
    //  Public API
    // =================================================================

    /// <summary>
    /// Called by client-side code (e.g., TerrainTool) to request an edit.
    /// Routes through the server.
    /// </summary>
    [Rpc(SendTo.Server, RequireOwnership = false)]
    public void RequestTerrainEditRpc(Vector3 center, float radius, float delta)
    {
        ApplyTerrainEdit(center, radius, delta);
    }

    /// <summary>
    /// Called directly when we are already on the server (e.g., Fireball
    /// collision). Applies the edit locally and broadcasts to all clients.
    /// </summary>
    public void ApplyTerrainEdit(Vector3 center, float radius, float delta)
    {
        if (!IsServer) return;

        // Apply on server
        if (terrainManager != null)
            terrainManager.EditTerrain(center, radius, delta);

        // Broadcast to all clients (host already applied above)
        BroadcastTerrainEditRpc(center, radius, delta);
    }

    // =================================================================
    //  ClientRpc — relay edit to all non-host clients
    // =================================================================

    [Rpc(SendTo.NotServer)]
    private void BroadcastTerrainEditRpc(Vector3 center, float radius, float delta)
    {
        if (terrainManager != null)
            terrainManager.EditTerrain(center, radius, delta);
    }
}
