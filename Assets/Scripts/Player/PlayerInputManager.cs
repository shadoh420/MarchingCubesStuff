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

    private bool _cursorLocked = true;

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
        }
    }

    private void OnEnable()
    {
        _moveAction?.Enable();
        _lookAction?.Enable();
        _jumpAction?.Enable();
        _sprintAction?.Enable();
        _crouchAction?.Enable();

        LockCursor();
    }

    private void OnDisable()
    {
        _moveAction?.Disable();
        _lookAction?.Disable();
        _jumpAction?.Disable();
        _sprintAction?.Disable();
        _crouchAction?.Disable();

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
