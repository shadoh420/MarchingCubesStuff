using System.Collections;
using UnityEngine;
using KinematicCharacterController;

/// <summary>
/// Server-side spawn utility for terrain surface finding.
///
/// PHASE 12 CHANGES:
///   This is no longer the central wiring point. Component wiring is now
///   handled by <see cref="NetworkPlayerSetup.OnNetworkSpawn"/>.
///   This component provides:
///     - <see cref="GenerateSpawnColumn"/>: sync-generates terrain chunks at spawn XZ.
///     - <see cref="FindTerrainSurface"/>: raycasts to find the surface.
///
///   These are called by <see cref="NetworkGameManager"/> before players spawn.
///
///   The component can still be used in single-player mode (without networking)
///   by keeping its Start() coroutine path, controlled by the
///   <see cref="SinglePlayerMode"/> toggle.
/// </summary>
[DefaultExecutionOrder(100)]
public class PlayerSpawnManager : MonoBehaviour
{
    // ── References ───────────────────────────────────────────────────
    [Header("References")]
    public TerrainManager             TerrainManager;
    public PlayerCharacterController  Character;
    public FirstPersonCamera          Camera;
    public PlayerInputManager         InputManager;
    public TerrainTool                  Tool;
    public TerrainToolHUD               HUD;
    public ProjectileLauncher           Launcher;
    public CombatHUD                    CombatHudComponent;
    public PlayerHealth                 Health;
    public PlayerVisuals                Visuals;
    public HealthHUD                    HealthHudComponent;

    [Header("Admin")]
    [Tooltip("When false, TerrainTool and TerrainToolHUD are disabled at spawn.")]
    public bool AdminToolsEnabled = false;

    // ── Spawn settings ──────────────────────────────────────────────
    [Header("Spawn Settings")]
    public Vector2 SpawnXZ = Vector2.zero;
    public float RaycastStartY = 200f;
    public float RaycastMaxDistance = 400f;
    public float SpawnHeightOffset = 2f;
    public int SpawnChunkColumnHeight = 3;

    [Header("Mode")]
    [Tooltip("Enable for non-networked single-player. Runs the legacy Start() wiring path.")]
    public bool SinglePlayerMode = false;

    // =================================================================
    //  Lifecycle
    // =================================================================

    private IEnumerator Start()
    {
        // Only run the legacy wiring path in single-player mode
        if (!SinglePlayerMode) yield break;

        yield return null;
        GenerateSpawnColumn();
        yield return null;
        yield return null;
        yield return new WaitForFixedUpdate();

        Vector3 spawnPos = FindTerrainSurface();

        if (Character != null && Character.Motor != null)
        {
            Character.Motor.SetPositionAndRotation(spawnPos, Quaternion.identity);
            Debug.Log($"[PlayerSpawnManager] Spawned player at {spawnPos}");
        }

        // Legacy single-player wiring (kept for backward compat)
        WireSinglePlayerReferences();
    }

    // =================================================================
    //  Public API (used by NetworkGameManager)
    // =================================================================

    /// <summary>
    /// Synchronously generates the chunk column at spawn XZ.
    /// </summary>
    public void GenerateSpawnColumn()
    {
        if (TerrainManager == null)
        {
            Debug.LogWarning("[PlayerSpawnManager] No TerrainManager assigned.");
            return;
        }

        Vector3 spawnWorld = new Vector3(SpawnXZ.x, 0f, SpawnXZ.y);
        Vector3Int center  = global::TerrainManager.WorldToChunkCoord(spawnWorld);
        int halfH = SpawnChunkColumnHeight / 2;

        for (int dy = -halfH; dy <= halfH; dy++)
        {
            Vector3Int coord = new Vector3Int(center.x, center.y + dy, center.z);
            if (TerrainManager.GetChunk(coord) != null) continue;
            TerrainManager.GenerateChunkImmediate(coord);
        }

        Debug.Log($"[PlayerSpawnManager] Generated {SpawnChunkColumnHeight} spawn chunks.");
    }

    /// <summary>
    /// Raycasts down from high altitude to find the terrain surface.
    /// </summary>
    public Vector3 FindTerrainSurface()
    {
        Vector3 rayOrigin = new Vector3(SpawnXZ.x, RaycastStartY, SpawnXZ.y);

        if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, RaycastMaxDistance))
        {
            return hit.point + Vector3.up * SpawnHeightOffset;
        }

        Debug.LogWarning("[PlayerSpawnManager] No terrain surface found — using fallback.");
        return new Vector3(SpawnXZ.x, TerrainManager != null ? TerrainManager.surfaceY + 5f : 20f, SpawnXZ.y);
    }

    // =================================================================
    //  Legacy single-player wiring (only used when SinglePlayerMode=true)
    // =================================================================

    private void WireSinglePlayerReferences()
    {
        if (Camera != null && Character != null)
            Camera.SetFollowPoint(Character.CameraFollowPoint);

        if (InputManager != null)
        {
            if (InputManager.Character == null) InputManager.Character = Character;
            if (InputManager.Camera    == null) InputManager.Camera    = Camera;
        }

        if (TerrainManager != null && Character != null)
            TerrainManager.player = Character.transform;

        if (Tool != null)
        {
            if (AdminToolsEnabled)
            {
                if (Tool.terrainManager == null) Tool.terrainManager = TerrainManager;
                if (Tool.cameraTransform == null && Camera != null)
                    Tool.cameraTransform = Camera.transform;
            }
            else
            {
                Tool.enabled = false;
            }
        }

        if (HUD != null)
        {
            if (AdminToolsEnabled && Tool != null)
            {
                if (HUD.tool == null) HUD.tool = Tool;
            }
            else
            {
                HUD.enabled = false;
            }
        }

        if (InputManager != null && Tool != null && AdminToolsEnabled)
        {
            if (InputManager.Tool == null) InputManager.Tool = Tool;
        }

        if (Launcher != null)
        {
            if (Launcher.terrainManager == null) Launcher.terrainManager = TerrainManager;
            if (Launcher.cameraTransform == null && Camera != null)
                Launcher.cameraTransform = Camera.transform;
            if (Launcher.playerObject == null && Character != null)
                Launcher.playerObject = Character.gameObject;
            if (Launcher.playerCollider == null && Character != null)
            {
                Collider col = Character.GetComponent<Collider>();
                if (col != null) Launcher.playerCollider = col;
            }
        }

        if (InputManager != null && Launcher != null)
        {
            if (InputManager.Launcher == null) InputManager.Launcher = Launcher;
        }

        if (CombatHudComponent != null && Launcher != null)
        {
            if (CombatHudComponent.launcher == null)
                CombatHudComponent.launcher = Launcher;
        }

        if (Health != null)
        {
            if (Health.Character == null) Health.Character = Character;
            if (Health.Camera == null) Health.Camera = Camera;
            if (Health.TerrainManager == null) Health.TerrainManager = TerrainManager;
            if (Health.Launcher == null) Health.Launcher = Launcher;
            if (Health.Visuals == null && Visuals != null) Health.Visuals = Visuals;
        }

        if (HealthHudComponent != null && Health != null)
        {
            if (HealthHudComponent.playerHealth == null)
                HealthHudComponent.playerHealth = Health;
        }
    }
}
