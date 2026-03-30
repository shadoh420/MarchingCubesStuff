using System.Collections;
using UnityEngine;
using KinematicCharacterController;

/// <summary>
/// Handles spawning the player on the terrain surface at game start.
///
/// Pipeline:
///   1. Asks <see cref="TerrainManager"/> to synchronously generate the
///      chunk column at the spawn XZ position (ensures colliders exist).
///   2. Raycasts downward from a high Y to find the terrain surface.
///   3. Teleports the character motor to the hit point + offset.
///   4. Wires up the <see cref="FirstPersonCamera"/> follow point.
///
/// Must execute AFTER TerrainManager.Start() finishes its initial load.
/// Use [DefaultExecutionOrder(100)] or place the spawn call in a coroutine
/// that waits one frame.
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
    [Tooltip("When false, TerrainTool and TerrainToolHUD are disabled at spawn (regular player). " +
             "Set to true for dev/admin access to dig/build tools.")]
    public bool AdminToolsEnabled = false;

    // ── Spawn settings ──────────────────────────────────────────────
    [Header("Spawn Settings")]
    [Tooltip("World-space XZ position for the initial spawn.")]
    public Vector2 SpawnXZ = Vector2.zero;

    [Tooltip("Height from which the surface raycast is cast downward.")]
    public float RaycastStartY = 200f;

    [Tooltip("Maximum ray distance for finding the terrain surface.")]
    public float RaycastMaxDistance = 400f;

    [Tooltip("Extra height above the surface hit to place the character.")]
    public float SpawnHeightOffset = 2f;

    [Tooltip("Vertical range of chunks to synchronously generate at spawn.")]
    public int SpawnChunkColumnHeight = 3;

    // =================================================================
    //  Lifecycle
    // =================================================================

    private IEnumerator Start()
    {
        // Wait one frame so TerrainManager.Start() triggers its initial load
        yield return null;

        // ── 1. Synchronously generate chunks in the spawn column ────
        GenerateSpawnColumn();

        // Wait a couple of frames for physics bakes to complete
        yield return null;
        yield return null;
        yield return new WaitForFixedUpdate();

        // ── 2. Raycast down to find the terrain surface ─────────────
        Vector3 spawnPos = FindTerrainSurface();

        // ── 3. Place the character ──────────────────────────────────
        if (Character != null && Character.Motor != null)
        {
            Character.Motor.SetPositionAndRotation(spawnPos, Quaternion.identity);
            Debug.Log($"[PlayerSpawnManager] Spawned player at {spawnPos}");
        }
        else
        {
            Debug.LogError("[PlayerSpawnManager] Character or Motor reference is missing.");
        }

        // ── 4. Wire up camera ───────────────────────────────────────
        if (Camera != null && Character != null)
        {
            Camera.SetFollowPoint(Character.CameraFollowPoint);
        }

        // ── 5. Wire up input manager ────────────────────────────────
        if (InputManager != null)
        {
            if (InputManager.Character == null) InputManager.Character = Character;
            if (InputManager.Camera    == null) InputManager.Camera    = Camera;
        }

        // ── 6. Point TerrainManager's player reference ──────────────
        if (TerrainManager != null && Character != null)
        {
            TerrainManager.player = Character.transform;
        }

        // ── 7. Wire up terrain tool (admin only) ──────────────────────
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
                Debug.Log("[PlayerSpawnManager] TerrainTool disabled (AdminToolsEnabled=false).");
            }
        }

        // ── 8. Wire up terrain tool HUD (admin only) ──────────────────
        if (HUD != null)
        {
            if (AdminToolsEnabled && Tool != null)
            {
                if (HUD.tool == null) HUD.tool = Tool;
            }
            else
            {
                HUD.enabled = false;
                Debug.Log("[PlayerSpawnManager] TerrainToolHUD disabled (AdminToolsEnabled=false).");
            }
        }

        // ── 9. Wire tool into input manager ──────────────────────────
        if (InputManager != null && Tool != null && AdminToolsEnabled)
        {
            if (InputManager.Tool == null) InputManager.Tool = Tool;
        }

        // ── 10. Wire up projectile launcher ─────────────────────────
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

        // ── 11. Wire launcher into input manager ────────────────────
        if (InputManager != null && Launcher != null)
        {
            if (InputManager.Launcher == null) InputManager.Launcher = Launcher;
        }

        // ── 12. Wire up combat HUD ───────────────────────────────
        if (CombatHudComponent != null && Launcher != null)
        {
            if (CombatHudComponent.launcher == null)
                CombatHudComponent.launcher = Launcher;
        }

        // ── 13. Wire up player health ────────────────────────────────
        if (Health != null)
        {
            if (Health.Character == null) Health.Character = Character;
            if (Health.Camera == null) Health.Camera = Camera;
            if (Health.TerrainManager == null) Health.TerrainManager = TerrainManager;
            if (Health.Launcher == null) Health.Launcher = Launcher;
            if (Health.Visuals == null && Visuals != null) Health.Visuals = Visuals;
        }

        // ── 14. Wire up health HUD ───────────────────────────────────
        if (HealthHudComponent != null && Health != null)
        {
            if (HealthHudComponent.playerHealth == null)
                HealthHudComponent.playerHealth = Health;
        }
    }

    // =================================================================
    //  Spawn helpers
    // =================================================================

    /// <summary>
    /// Synchronously generates the vertical column of chunks at the
    /// spawn XZ, so the player has colliders to stand on immediately.
    /// </summary>
    private void GenerateSpawnColumn()
    {
        if (TerrainManager == null)
        {
            Debug.LogWarning("[PlayerSpawnManager] No TerrainManager assigned.");
            return;
        }

        Vector3 spawnWorld = new Vector3(SpawnXZ.x, 0f, SpawnXZ.y);
        Vector3Int center  = TerrainManager.WorldToChunkCoord(spawnWorld);

        int halfH = SpawnChunkColumnHeight / 2;

        for (int dy = -halfH; dy <= halfH; dy++)
        {
            Vector3Int coord = new Vector3Int(center.x, center.y + dy, center.z);

            // Skip if already loaded
            if (TerrainManager.GetChunk(coord) != null)
                continue;

            TerrainManager.GenerateChunkImmediate(coord);
        }

        Debug.Log($"[PlayerSpawnManager] Synchronously generated {SpawnChunkColumnHeight} " +
                  $"chunks at column ({center.x}, {center.z}).");
    }

    /// <summary>
    /// Casts a ray straight down from a high altitude at the spawn XZ
    /// to find the terrain surface. Falls back to a default position.
    /// </summary>
    private Vector3 FindTerrainSurface()
    {
        Vector3 rayOrigin = new Vector3(SpawnXZ.x, RaycastStartY, SpawnXZ.y);

        if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, RaycastMaxDistance))
        {
            return hit.point + Vector3.up * SpawnHeightOffset;
        }

        Debug.LogWarning("[PlayerSpawnManager] No terrain surface found — using fallback position.");
        return new Vector3(SpawnXZ.x, TerrainManager != null ? TerrainManager.surfaceY + 5f : 20f, SpawnXZ.y);
    }
}
