using System.Collections;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Bootstrap component for networked multiplayer.
///
/// Responsibilities:
///   - Creates/configures <see cref="NetworkManager"/> at runtime if none exists.
///   - Provides Host / Join flow via temporary IMGUI buttons.
///   - Ensures terrain is generated at the spawn point before players spawn.
///   - Uses <see cref="ConnectionApprovalCallback"/> to place connecting
///     players at the terrain surface.
///
/// Future phases will replace the IMGUI with a proper lobby UI.
/// </summary>
public class NetworkGameManager : MonoBehaviour
{
    // ── References ───────────────────────────────────────────────────
    [Header("References")]
    [Tooltip("Player prefab with NetworkObject. Must be registered as a NetworkPrefab.")]
    public GameObject PlayerPrefab;

    [Tooltip("TerrainManager in the scene.")]
    public TerrainManager TerrainManager;

    // ── Spawn settings (mirrors PlayerSpawnManager) ──────────────────
    [Header("Spawn Settings")]
    public Vector2 SpawnXZ             = Vector2.zero;
    public float   RaycastStartY       = 200f;
    public float   RaycastMaxDistance   = 400f;
    public float   SpawnHeightOffset   = 2f;
    public int     SpawnChunkColumnHeight = 3;

    // ── Network settings ─────────────────────────────────────────────
    [Header("Network")]
    [Tooltip("Port for hosting/joining.")]
    public ushort Port = 7777;

    // ── State ────────────────────────────────────────────────────────
    private bool   _isStarted;
    private string _joinIP = "127.0.0.1";
    private bool   _isPaused;

    // =================================================================
    //  Lifecycle
    // =================================================================

    private void Start()
    {
        EnsureNetworkManager();
    }

    private void Update()
    {
        // Pause menu toggle (only when in-game)
        if (_isStarted && Keyboard.current != null &&
            Keyboard.current[Key.Escape].wasPressedThisFrame)
        {
            SetPaused(!_isPaused);
        }
    }

    private void SetPaused(bool paused)
    {
        _isPaused = paused;

        if (paused)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible   = true;

            // Tell local player's input manager to stop reading input
            var localInput = FindLocalPlayerInputManager();
            if (localInput != null) localInput.UnlockCursor();
        }
        else
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible   = false;

            var localInput = FindLocalPlayerInputManager();
            if (localInput != null) localInput.LockCursor();
        }
    }

    private PlayerInputManager FindLocalPlayerInputManager()
    {
        foreach (var pim in FindObjectsByType<PlayerInputManager>(FindObjectsSortMode.None))
        {
            if (pim.enabled) return pim;
        }
        return null;
    }

    private PlayerHealth FindLocalPlayerHealth()
    {
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsClient)
            return null;

        ulong localId = NetworkManager.Singleton.LocalClientId;
        foreach (var ph in FindObjectsByType<PlayerHealth>(FindObjectsSortMode.None))
        {
            if (ph.OwnerClientId == localId) return ph;
        }
        return null;
    }

    private void OnDestroy()
    {
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.ConnectionApprovalCallback -= ApprovalCallback;
            NetworkManager.Singleton.OnClientConnectedCallback  -= OnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
        }
    }

    // =================================================================
    //  Network Manager setup
    // =================================================================

    private void EnsureNetworkManager()
    {
        NetworkManager nm = NetworkManager.Singleton;

        if (nm == null)
        {
            GameObject go = new GameObject("NetworkManager");
            nm = go.AddComponent<NetworkManager>();
            go.AddComponent<UnityTransport>();
        }

        // Ensure transport component exists
        UnityTransport transport = nm.GetComponent<UnityTransport>();
        if (transport == null)
            transport = nm.gameObject.AddComponent<UnityTransport>();

        // CRITICAL: wire transport into NetworkConfig so NGO knows about it
        nm.NetworkConfig.NetworkTransport = transport;

        // Player prefab registration
        if (PlayerPrefab != null)
        {
            nm.NetworkConfig.PlayerPrefab = PlayerPrefab;
        }

        // Enable connection approval so we can set spawn position
        nm.NetworkConfig.ConnectionApproval = true;
        nm.ConnectionApprovalCallback += ApprovalCallback;

        nm.OnClientConnectedCallback  += OnClientConnected;
        nm.OnClientDisconnectCallback += OnClientDisconnected;
    }

    // =================================================================
    //  Host / Client start
    // =================================================================

    /// <summary>Start as host (server + client).</summary>
    public void StartHost()
    {
        if (_isStarted) return;
        _isStarted = true;   // block double-clicks immediately

        StartCoroutine(StartHostCoroutine());
    }

    /// <summary>
    /// Generates spawn terrain, waits for physics to register the new
    /// mesh colliders (mirrors the proven multi-frame wait from
    /// <see cref="PlayerSpawnManager"/>), then starts the host.
    /// </summary>
    private IEnumerator StartHostCoroutine()
    {
        GenerateSpawnTerrain();

        // Newly created MeshColliders are not queryable until the physics
        // engine processes them.  Physics.SyncTransforms() flushes
        // transform positions but does NOT register new colliders.
        // We must wait for at least one FixedUpdate (simulation step)
        // for the colliders to appear in raycasts.
        yield return null;                      // let engine process
        yield return new WaitForFixedUpdate();   // physics simulation step

        var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        transport.SetConnectionData("0.0.0.0", Port);

        NetworkManager.Singleton.StartHost();

        Debug.Log($"[NetworkGameManager] Host started on port {Port}.");
    }

    /// <summary>Join an existing host.</summary>
    public void StartClient(string ip)
    {
        if (_isStarted) return;

        var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        transport.SetConnectionData(ip, Port);

        NetworkManager.Singleton.StartClient();
        _isStarted = true;

        Debug.Log($"[NetworkGameManager] Connecting to {ip}:{Port}...");
    }

    // =================================================================
    //  Connection approval — set spawn position
    // =================================================================

    private void ApprovalCallback(
        NetworkManager.ConnectionApprovalRequest request,
        NetworkManager.ConnectionApprovalResponse response)
    {
        response.Approved           = true;
        response.CreatePlayerObject = true;
        response.Position           = FindTerrainSurface();
        response.Rotation           = Quaternion.identity;
    }

    // =================================================================
    //  Connection events
    // =================================================================

    private void OnClientConnected(ulong clientId)
    {
        Debug.Log($"[NetworkGameManager] Client {clientId} connected.");
    }

    private void OnClientDisconnected(ulong clientId)
    {
        Debug.Log($"[NetworkGameManager] Client {clientId} disconnected.");
    }

    // =================================================================
    //  Terrain helpers
    // =================================================================

    /// <summary>
    /// Synchronously generates the chunk column at the spawn point
    /// so colliders exist before any player is placed.
    /// </summary>
    private void GenerateSpawnTerrain()
    {
        if (TerrainManager == null)
        {
            Debug.LogWarning("[NetworkGameManager] No TerrainManager assigned.");
            return;
        }

        Vector3 spawnWorld = new Vector3(SpawnXZ.x, 0f, SpawnXZ.y);
        Vector3Int center  = global::TerrainManager.WorldToChunkCoord(spawnWorld);

        int halfH = SpawnChunkColumnHeight / 2;
        int generated = 0;
        for (int dy = -halfH; dy <= halfH; dy++)
        {
            Vector3Int coord = new Vector3Int(center.x, center.y + dy, center.z);
            if (TerrainManager.GetChunk(coord) != null) continue;
            TerrainManager.GenerateChunkImmediate(coord);
            generated++;
        }

        Debug.Log($"[NetworkGameManager] Generated {generated} spawn chunks (column height {SpawnChunkColumnHeight}, center {center}).");
    }

    /// <summary>
    /// Raycasts down from high altitude at the spawn XZ to find the
    /// terrain surface. Used as the spawn position for new players.
    /// </summary>
    private Vector3 FindTerrainSurface()
    {
        Vector3 rayOrigin = new Vector3(SpawnXZ.x, RaycastStartY, SpawnXZ.y);

        if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, RaycastMaxDistance))
        {
            Debug.Log($"[NetworkGameManager] Raycast hit '{hit.collider.name}' at {hit.point}. Spawn Y = {hit.point.y + SpawnHeightOffset:F2}");
            return hit.point + Vector3.up * SpawnHeightOffset;
        }

        float surfY = TerrainManager != null ? TerrainManager.surfaceY + 5f : 20f;
        Debug.LogWarning($"[NetworkGameManager] Raycast MISSED — using fallback Y={surfY}. " +
                         $"Origin={rayOrigin}, MaxDist={RaycastMaxDistance}");
        return new Vector3(SpawnXZ.x, surfY, SpawnXZ.y);
    }

    // =================================================================
    //  IMGUI — Main menu & Pause menu
    // =================================================================

    private void OnGUI()
    {
        if (!_isStarted)
        {
            DrawMainMenu();
            return;
        }

        if (_isPaused)
        {
            DrawPauseMenu();
        }
    }

    private void DrawMainMenu()
    {
        float w = 260f, h = 170f;
        float x = (Screen.width - w) * 0.5f;
        float y = (Screen.height - h) * 0.5f;

        GUILayout.BeginArea(new Rect(x, y, w, h), GUI.skin.box);

        GUILayout.Label("MULTIPLAYER", new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize  = 18,
            fontStyle = FontStyle.Bold,
        });

        GUILayout.Space(10f);

        if (GUILayout.Button("Host Game", GUILayout.Height(35f)))
        {
            StartHost();
        }

        GUILayout.Space(10f);
        GUILayout.Label("Join IP:");
        _joinIP = GUILayout.TextField(_joinIP);

        if (GUILayout.Button("Join Game", GUILayout.Height(35f)))
        {
            StartClient(_joinIP);
        }

        GUILayout.EndArea();
    }

    private void DrawPauseMenu()
    {
        // Semi-transparent overlay
        GUI.color = new Color(0f, 0f, 0f, 0.5f);
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
        GUI.color = Color.white;

        float w = 260f, h = 230f;
        float x = (Screen.width - w) * 0.5f;
        float y = (Screen.height - h) * 0.5f;

        GUILayout.BeginArea(new Rect(x, y, w, h), GUI.skin.box);

        GUILayout.Label("PAUSED", new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize  = 20,
            fontStyle = FontStyle.Bold,
        });

        GUILayout.Space(12f);

        if (GUILayout.Button("Resume", GUILayout.Height(32f)))
        {
            SetPaused(false);
        }

        GUILayout.Space(6f);

        if (GUILayout.Button("Respawn", GUILayout.Height(32f)))
        {
            var health = FindLocalPlayerHealth();
            if (health != null)
            {
                health.RequestRespawnServerRpc();
            }
            SetPaused(false);
        }

        GUILayout.Space(6f);

        if (GUILayout.Button("Disconnect", GUILayout.Height(32f)))
        {
            SetPaused(false);
            if (NetworkManager.Singleton != null)
                NetworkManager.Singleton.Shutdown();
            _isStarted = false;
        }

        GUILayout.Space(6f);

        if (GUILayout.Button("Quit Game", GUILayout.Height(32f)))
        {
            Debug.Log("[NetworkGameManager] Quit Game requested.");
            Application.Quit();
            #if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
            #endif
        }

        GUILayout.EndArea();
    }
}
