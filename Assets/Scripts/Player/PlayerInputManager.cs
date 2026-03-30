using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Bridges Unity's Input System actions to the character controller and
/// first-person camera. Reads the "Player" action map from the project's
/// InputSystem_Actions asset.
///
/// PHASE 12 CHANGES:
///   - Exposes latched input state via public properties so
///     <see cref="NetworkPlayerController"/> can read and send it to the server.
///   - No longer calls <see cref="PlayerCharacterController.SetInputs"/>
///     directly — the server does that via NetworkPlayerController.
///   - Camera input (LateUpdate) is still local-only (owner camera).
///   - Cursor management is still local.
/// </summary>
public class PlayerInputManager : MonoBehaviour
{
    // ── References ───────────────────────────────────────────────────
    [Header("References")]
    public PlayerCharacterController Character;
    public FirstPersonCamera          Camera;
    public TerrainTool                Tool;
    public ProjectileLauncher         Launcher;

    // ── Input asset ─────────────────────────────────────────────────
    [Header("Input")]
    [Tooltip("Drag the InputSystem_Actions asset here, or leave null to create at runtime.")]
    public InputActionAsset InputActions;

    // ── Cursor ──────────────────────────────────────────────────────
    [Header("Cursor")]
    [Tooltip("Key that toggles cursor lock (e.g., pausing).")]
    public Key PauseKey = Key.Escape;

    // ── Cached actions ──────────────────────────────────────────────
    private InputAction _moveAction;
    private InputAction _lookAction;
    private InputAction _jumpAction;
    private InputAction _sprintAction;
    private InputAction _crouchAction;
    private InputAction _attackAction;
    private InputAction _previousAction;
    private InputAction _nextAction;

    private bool _cursorLocked = true;

    /// <summary>True when the cursor is locked for gameplay.</summary>
    public bool IsCursorLocked => _cursorLocked;

    // ── Latched input state (read by NetworkPlayerController) ────────
    /// <summary>Raw move input (x = right, y = forward).</summary>
    public Vector2 LatestMoveInput { get; private set; }

    /// <summary>Current yaw angle from the camera.</summary>
    public float LatestYaw => Camera != null ? Camera.Yaw : 0f;

    /// <summary>Current pitch angle from the camera.</summary>
    public float LatestPitch => Camera != null ? Camera.Pitch : 0f;

    /// <summary>True if sprint is held this frame.</summary>
    public bool LatestSprintHeld { get; private set; }

    /// <summary>True if fire/attack is held this frame.</summary>
    public bool LatestFireHeld { get; private set; }

    // ── One-frame latches (survive until consumed by NetworkPlayerController)
    private bool _jumpLatch;
    private bool _crouchPressLatch;
    private bool _crouchReleaseLatch;

    /// <summary>Consume the latched jump press. Returns true once, then resets.</summary>
    public bool ConsumeJumpLatch()
    {
        bool v = _jumpLatch;
        _jumpLatch = false;
        return v;
    }

    /// <summary>Consume the latched crouch press.</summary>
    public bool ConsumeCrouchPressLatch()
    {
        bool v = _crouchPressLatch;
        _crouchPressLatch = false;
        return v;
    }

    /// <summary>Consume the latched crouch release.</summary>
    public bool ConsumeCrouchReleaseLatch()
    {
        bool v = _crouchReleaseLatch;
        _crouchReleaseLatch = false;
        return v;
    }

    // =================================================================
    //  Lifecycle
    // =================================================================

    private void Awake()
    {
        // Resolve actions from the Player map.
        // CRITICAL: Clone the asset so each prefab instance owns independent
        // InputAction objects.  Without this, the second player's OnDisable()
        // disables the SAME actions the host player is reading.
        if (InputActions != null)
        {
            InputActions = Object.Instantiate(InputActions);
            var playerMap = InputActions.FindActionMap("Player", true);
            _moveAction   = playerMap.FindAction("Move",   true);
            _lookAction   = playerMap.FindAction("Look",   true);
            _jumpAction   = playerMap.FindAction("Jump",   true);
            _sprintAction = playerMap.FindAction("Sprint", true);
            _crouchAction = playerMap.FindAction("Crouch", true);
            _attackAction   = playerMap.FindAction("Attack",   true);
            _previousAction = playerMap.FindAction("Previous", true);
            _nextAction     = playerMap.FindAction("Next",     true);
        }
        else
        {
            Debug.LogError("[PlayerInputManager] InputActions asset is NULL! " +
                           "Assign the InputSystem_Actions asset on the Player Prefab's " +
                           "PlayerInputManager component. All input will be zero.");
        }
    }

    private void OnEnable()
    {
        _moveAction?.Enable();
        _lookAction?.Enable();
        _jumpAction?.Enable();
        _sprintAction?.Enable();
        _crouchAction?.Enable();
        _attackAction?.Enable();
        _previousAction?.Enable();
        _nextAction?.Enable();

        // NOTE: Cursor locking is handled by NetworkPlayerSetup.ConfigureOwner().
        // Do NOT lock here — OnEnable fires before OnNetworkSpawn, so we don't
        // yet know if this instance is the local owner.
    }

    private void OnDisable()
    {
        _moveAction?.Disable();
        _lookAction?.Disable();
        _jumpAction?.Disable();
        _sprintAction?.Disable();
        _crouchAction?.Disable();
        _attackAction?.Disable();
        _previousAction?.Disable();
        _nextAction?.Disable();

        // NOTE: Do NOT unlock cursor here.  When a remote player's
        // PlayerInputManager is disabled by ConfigureRemote(), calling
        // UnlockCursor() would free the host's cursor.
    }

    private void OnDestroy()
    {
        // Clean up the cloned InputActionAsset to prevent memory leaks.
        if (InputActions != null)
        {
            Destroy(InputActions);
            InputActions = null;
        }
    }

    // =================================================================
    //  Update — read inputs and expose them
    // =================================================================

    private void Update()
    {
        // NOTE: Escape / pause menu is handled by NetworkGameManager.
        // This component only reads gameplay input.

        // Read raw input ─────────────────────────────────────────────
        Vector2 move = _moveAction != null ? _moveAction.ReadValue<Vector2>() : Vector2.zero;
        LatestMoveInput = move;

        LatestSprintHeld = _sprintAction != null && _sprintAction.IsPressed();

        // Latch one-frame signals (persist until consumed)
        if (_jumpAction != null && _jumpAction.WasPressedThisFrame())
            _jumpLatch = true;

        if (_crouchAction != null && _crouchAction.WasPressedThisFrame())
            _crouchPressLatch = true;

        if (_crouchAction != null && _crouchAction.WasReleasedThisFrame())
            _crouchReleaseLatch = true;

        // Attack / fire
        bool attack = _cursorLocked && _attackAction != null && _attackAction.IsPressed();
        LatestFireHeld = attack;

        // ── Terrain tool mode cycling (local UI feedback) ────────────
        if (Tool != null && Tool.isActiveAndEnabled)
        {
            if (_previousAction != null && _previousAction.WasPressedThisFrame())
                Tool.CycleMode(-1);
            if (_nextAction != null && _nextAction.WasPressedThisFrame())
                Tool.CycleMode(1);
        }
    }

    // =================================================================
    //  LateUpdate — feed camera inputs (local only, always)
    // =================================================================

    private void LateUpdate()
    {
        if (Camera == null) return;

        Vector2 look = Vector2.zero;
        if (_cursorLocked && _lookAction != null)
            look = _lookAction.ReadValue<Vector2>();

        Camera.UpdateWithInput(Time.deltaTime, look);
    }

    // =================================================================
    //  Cursor management
    // =================================================================

    /// <summary>Locks and hides the cursor for gameplay.</summary>
    public void LockCursor()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible   = false;
        _cursorLocked    = true;
    }

    /// <summary>Unlocks and shows the cursor (pause / menu).</summary>
    public void UnlockCursor()
    {
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible   = true;
        _cursorLocked    = false;
    }
}
