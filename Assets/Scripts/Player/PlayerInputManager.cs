using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Bridges Unity's Input System actions to the character controller and
/// first-person camera. Reads the "Player" action map from the project's
/// InputSystem_Actions asset.
///
/// Responsibilities:
///   - Polls Move, Look, Sprint, Jump, Crouch actions each frame.
///   - Feeds <see cref="PlayerCharacterController.SetInputs"/> in Update.
///   - Feeds <see cref="FirstPersonCamera.UpdateWithInput"/> in LateUpdate.
///   - Manages cursor lock state (locked during play, freed on Escape).
/// </summary>
public class PlayerInputManager : MonoBehaviour
{
    // ── References ───────────────────────────────────────────────────
    [Header("References")]
    public PlayerCharacterController Character;
    public FirstPersonCamera          Camera;
    public TerrainTool                Tool;

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

    // =================================================================
    //  Lifecycle
    // =================================================================

    private void Awake()
    {
        // Resolve actions from the Player map
        if (InputActions != null)
        {
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

        LockCursor();
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

        UnlockCursor();
    }

    // =================================================================
    //  Update — feed character inputs
    // =================================================================

    private void Update()
    {
        // Toggle cursor lock with Escape
        if (Keyboard.current != null && Keyboard.current[PauseKey].wasPressedThisFrame)
        {
            if (_cursorLocked) UnlockCursor();
            else               LockCursor();
        }

        if (Character == null) return;

        Vector2 move = _moveAction != null ? _moveAction.ReadValue<Vector2>() : Vector2.zero;

        var inputs = new PlayerCharacterController.CharacterInputs
        {
            MoveAxisForward = move.y,
            MoveAxisRight   = move.x,
            CameraRotation  = Camera != null ? Camera.Rotation : transform.rotation,
            JumpDown        = _jumpAction != null && _jumpAction.WasPressedThisFrame(),
            CrouchDown      = _crouchAction != null && _crouchAction.WasPressedThisFrame(),
            CrouchUp        = _crouchAction != null && _crouchAction.WasReleasedThisFrame(),
            SprintHeld      = _sprintAction != null && _sprintAction.IsPressed()
        };

        Character.SetInputs(ref inputs);

        // ── Feed terrain tool inputs ──────────────────────────────────
        if (Tool != null)
        {
            bool attack = _cursorLocked && _attackAction != null && _attackAction.IsPressed();
            Tool.SetAttackInput(attack);

            if (_previousAction != null && _previousAction.WasPressedThisFrame())
                Tool.CycleMode(-1);
            if (_nextAction != null && _nextAction.WasPressedThisFrame())
                Tool.CycleMode(1);
        }
    }

    // =================================================================
    //  LateUpdate — feed camera inputs
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
