using Unity.Netcode;
using UnityEngine;
using KinematicCharacterController;

/// <summary>
/// Configures the player prefab instance based on network ownership.
///
/// Placed on the player prefab root (same GO as <see cref="NetworkObject"/>).
/// On <see cref="OnNetworkSpawn"/>, enables/disables subsystems according to:
///
///   SERVER (for every player):
///     • KCC motor ENABLED — server simulates all player physics.
///     • ProjectileLauncher receives fire input from server-side NetworkPlayerController.
///
///   OWNER (local player on this client):
///     • PlayerInputManager ENABLED — reads keyboard/mouse.
///     • FirstPersonCamera ENABLED — follows this player.
///     • HUDs ENABLED — shows health, crosshair, etc.
///     • PlayerVisuals HIDDEN — first-person view.
///
///   REMOTE (other players on this client):
///     • KCC motor DISABLED — position from NetworkTransform.
///     • PlayerInputManager DISABLED.
///     • Camera/HUDs DISABLED.
///     • PlayerVisuals VISIBLE — see their capsule.
///
/// Also wires scene-level references (camera, terrain manager) at runtime,
/// replacing the old PlayerSpawnManager wiring pattern.
/// </summary>
public class NetworkPlayerSetup : NetworkBehaviour
{
    // ── Scene references (set by NetworkGameManager before spawn,
    //    or found automatically) ──────────────────────────────────────
    [Header("Scene References")]
    public TerrainManager  SceneTerrainManager;
    public FirstPersonCamera SceneCamera;

    // ── Component references on this prefab ──────────────────────────
    private PlayerCharacterController _character;
    private KinematicCharacterMotor   _motor;
    private PlayerInputManager        _inputManager;
    private NetworkPlayerController   _netController;
    private ProjectileLauncher        _launcher;
    private PlayerHealth              _health;
    private PlayerVisuals             _visuals;
    private TerrainTool               _terrainTool;
    private HealthHUD                 _healthHUD;
    private CombatHUD                 _combatHUD;
    private TerrainToolHUD            _terrainToolHUD;

    // =================================================================
    //  Network Lifecycle
    // =================================================================

    public override void OnNetworkSpawn()
    {
        CacheComponents();
        FindSceneReferences();

        if (IsServer)
        {
            ConfigureServer();
        }

        if (IsOwner)
        {
            ConfigureOwner();
        }
        else
        {
            ConfigureRemote();
        }
    }

    // =================================================================
    //  Component caching
    // =================================================================

    private void CacheComponents()
    {
        _character      = GetComponent<PlayerCharacterController>();
        _motor          = GetComponent<KinematicCharacterMotor>();
        _inputManager   = GetComponent<PlayerInputManager>();
        _netController  = GetComponent<NetworkPlayerController>();
        _launcher       = GetComponent<ProjectileLauncher>();
        _health         = GetComponent<PlayerHealth>();
        _visuals        = GetComponent<PlayerVisuals>();
        _terrainTool    = GetComponent<TerrainTool>();
        _healthHUD      = GetComponent<HealthHUD>();
        _combatHUD      = GetComponent<CombatHUD>();
        _terrainToolHUD = GetComponent<TerrainToolHUD>();
    }

    private void FindSceneReferences()
    {
        if (SceneTerrainManager == null)
            SceneTerrainManager = FindFirstObjectByType<TerrainManager>();

        if (SceneCamera == null)
            SceneCamera = FindFirstObjectByType<FirstPersonCamera>();
    }

    // =================================================================
    //  Server configuration (runs for EVERY player on the server)
    // =================================================================

    private void ConfigureServer()
    {
        // KCC motor runs on server for all players
        if (_motor != null)
        {
            _motor.enabled = true;

            // CRITICAL: NGO's ApprovalCallback sets transform.position, but
            // KCC maintains its own internal position (TransientPosition) and
            // ignores external transform writes.  We must explicitly tell the
            // motor where it is, otherwise it starts at the prefab origin and
            // the player falls through the world.
            _motor.SetPositionAndRotation(transform.position, transform.rotation);
        }

        // Wire launcher references for server-side fire
        if (_launcher != null)
        {
            _launcher.terrainManager = SceneTerrainManager;
            _launcher.playerObject   = gameObject;

            Collider col = GetComponent<Collider>();
            if (col != null) _launcher.playerCollider = col;
        }

        // Wire terrain tool
        if (_terrainTool != null)
        {
            _terrainTool.terrainManager = SceneTerrainManager;
        }

        // Wire health references
        if (_health != null)
        {
            _health.Character      = _character;
            _health.TerrainManager = SceneTerrainManager;
            _health.Launcher       = _launcher;
            _health.Visuals        = _visuals;
        }
    }

    // =================================================================
    //  Owner configuration (local player on this client)
    // =================================================================

    private void ConfigureOwner()
    {
        // Input system — only the owner reads keyboard/mouse
        if (_inputManager != null)
        {
            _inputManager.enabled = true;
            _inputManager.Character = _character;
        }

        // Camera — only the owner has the active FPS camera
        if (SceneCamera != null && _character != null)
        {
            SceneCamera.SetFollowPoint(_character.CameraFollowPoint);
            SceneCamera.enabled = true;

            if (_inputManager != null)
                _inputManager.Camera = SceneCamera;
        }

        // Point camera transform for launcher aim direction (server uses this too)
        if (_launcher != null && SceneCamera != null)
            _launcher.cameraTransform = SceneCamera.transform;

        // Terrain tool camera reference
        if (_terrainTool != null && SceneCamera != null)
            _terrainTool.cameraTransform = SceneCamera.transform;

        // Visuals — hidden for first-person
        if (_visuals != null)
            _visuals.SetVisible(false);

        // HUDs — active for owner only
        if (_healthHUD != null)
        {
            _healthHUD.enabled = true;
            if (_health != null) _healthHUD.playerHealth = _health;
        }

        if (_combatHUD != null)
        {
            _combatHUD.enabled = true;
            if (_launcher != null) _combatHUD.launcher = _launcher;
        }

        if (_terrainToolHUD != null && _terrainTool != null)
        {
            _terrainToolHUD.enabled = true;
            _terrainToolHUD.tool = _terrainTool;
        }

        // KCC motor: disabled on non-host owners (server drives position)
        // For the host (IsServer && IsOwner), it stays enabled from ConfigureServer()
        if (!IsServer && _motor != null)
            _motor.enabled = false;

        // Tell TerrainManager to follow the local player
        if (SceneTerrainManager != null)
            SceneTerrainManager.player = transform;

        // Lock cursor for gameplay
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible   = false;

        // ── Diagnostic dump ──────────────────────────────────────────
        Debug.Log($"[NetworkPlayerSetup] ConfigureOwner complete for client {OwnerClientId}:\n" +
                  $"  _inputManager       = {(_inputManager != null ? "OK" : "NULL")}\n" +
                  $"  _inputManager.enabled = {(_inputManager != null && _inputManager.enabled)}\n" +
                  $"  InputActions asset  = {(_inputManager != null && _inputManager.InputActions != null ? _inputManager.InputActions.name : "NULL")}\n" +
                  $"  _inputManager.Camera = {(_inputManager != null && _inputManager.Camera != null ? "OK" : "NULL")}\n" +
                  $"  _inputManager.Character = {(_inputManager != null && _inputManager.Character != null ? "OK" : "NULL")}\n" +
                  $"  SceneCamera         = {(SceneCamera != null ? "OK" : "NULL")}\n" +
                  $"  SceneCamera.FollowPoint = {(SceneCamera != null && SceneCamera.FollowPoint != null ? SceneCamera.FollowPoint.name : "NULL")}\n" +
                  $"  _motor              = {(_motor != null ? $"enabled={_motor.enabled}" : "NULL")}\n" +
                  $"  _netController      = {(_netController != null ? "OK" : "NULL")}\n" +
                  $"  CursorLocked        = {Cursor.lockState}");
    }

    // =================================================================
    //  Remote configuration (other players on this client)
    // =================================================================

    private void ConfigureRemote()
    {
        // No input for remote players
        if (_inputManager != null)
            _inputManager.enabled = false;

        // No KCC motor on non-server (position from NetworkTransform)
        if (!IsServer && _motor != null)
            _motor.enabled = false;

        // Visuals — visible so we can see other players
        if (_visuals != null)
            _visuals.SetVisible(true);

        // HUDs — disabled for remote players
        if (_healthHUD != null)      _healthHUD.enabled = false;
        if (_combatHUD != null)      _combatHUD.enabled = false;
        if (_terrainToolHUD != null) _terrainToolHUD.enabled = false;
    }
}
