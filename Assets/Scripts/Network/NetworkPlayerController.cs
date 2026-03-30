using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Bridges client input to server-side KCC simulation.
///
/// SERVER-AUTHORITATIVE FLOW:
///   Owner client:  PlayerInputManager reads input → this component
///                  packages it into <see cref="NetworkInputData"/> and
///                  sends it to the server via <see cref="SendInputRpc"/>.
///   Host shortcut: If we are the server AND the owner, input is applied
///                  directly to the KCC with zero latency (no RPC needed).
///   Server:        Receives input, stores it, and applies it to the
///                  <see cref="PlayerCharacterController"/> each frame.
///                  The KCC motor produces authoritative position which
///                  <c>NetworkTransform</c> syncs to all clients.
///
/// Also handles server-side projectile firing and terrain tool routing
/// based on the received input data.
/// </summary>
[RequireComponent(typeof(PlayerCharacterController))]
public class NetworkPlayerController : NetworkBehaviour
{
    // ── References (cached in OnNetworkSpawn) ─────────────────────────
    private PlayerCharacterController _character;
    private ProjectileLauncher        _launcher;
    private TerrainTool               _terrainTool;
    private PlayerInputManager        _inputManager;

    // ── Latest input from the owning client ──────────────────────────
    private NetworkInputData _latestInput;
    private bool             _hasInput;

    // ── Pitch sync for remote aiming display (not gameplay-critical) ──
    private NetworkVariable<float> _syncedPitch = new NetworkVariable<float>(
        0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// <summary>Server-synced pitch for projectile aim direction.</summary>
    public float SyncedPitch => _syncedPitch.Value;

    // =================================================================
    //  Lifecycle
    // =================================================================

    public override void OnNetworkSpawn()
    {
        _character   = GetComponent<PlayerCharacterController>();
        _launcher    = GetComponent<ProjectileLauncher>();
        _terrainTool = GetComponent<TerrainTool>();
        _inputManager = GetComponent<PlayerInputManager>();
    }

    private void Update()
    {
        if (IsOwner)
        {
            CollectAndSendInput();
        }

        if (IsServer && _hasInput)
        {
            ApplyInputToCharacter(_latestInput);
            HandleCombatInput(_latestInput);
        }
    }

    // =================================================================
    //  Owner → Server input pipeline
    // =================================================================

    // One-shot diagnostic flag
    private bool _diagLogged;

    /// <summary>
    /// Reads latched input from <see cref="PlayerInputManager"/> and
    /// sends it to the server. For the host player, applies directly.
    /// </summary>
    private void CollectAndSendInput()
    {
        if (_inputManager == null)
        {
            if (!_diagLogged) { Debug.LogWarning("[NetworkPlayerController] _inputManager is NULL — no input will be collected."); _diagLogged = true; }
            return;
        }

        if (!_diagLogged)
        {
            _diagLogged = true;
            Debug.Log($"[NetworkPlayerController] First input tick — " +
                      $"inputManager.enabled={_inputManager.enabled}, " +
                      $"InputActions={((_inputManager.InputActions != null) ? _inputManager.InputActions.name : "NULL")}, " +
                      $"Move={_inputManager.LatestMoveInput}, Yaw={_inputManager.LatestYaw:F1}, Pitch={_inputManager.LatestPitch:F1}");
        }

        var data = new NetworkInputData
        {
            MoveX          = _inputManager.LatestMoveInput.x,
            MoveZ          = _inputManager.LatestMoveInput.y,
            YawDegrees     = _inputManager.LatestYaw,
            PitchDegrees   = _inputManager.LatestPitch,
            JumpPressed    = _inputManager.ConsumeJumpLatch(),
            CrouchPressed  = _inputManager.ConsumeCrouchPressLatch(),
            CrouchReleased = _inputManager.ConsumeCrouchReleaseLatch(),
            SprintHeld     = _inputManager.LatestSprintHeld,
            FireHeld       = _inputManager.LatestFireHeld,
        };

        if (IsServer)
        {
            // Host shortcut: apply directly, no RPC overhead
            _latestInput = data;
            _hasInput = true;
            _syncedPitch.Value = data.PitchDegrees;
        }
        else
        {
            SendInputRpc(data);
        }
    }

    [Rpc(SendTo.Server)]
    private void SendInputRpc(NetworkInputData data)
    {
        _latestInput = data;
        _hasInput = true;
        _syncedPitch.Value = data.PitchDegrees;
    }

    // =================================================================
    //  Server: apply input to KCC
    // =================================================================

    /// <summary>
    /// Reconstructs a <see cref="PlayerCharacterController.CharacterInputs"/>
    /// struct from network data and feeds it to the KCC motor.
    /// </summary>
    private void ApplyInputToCharacter(NetworkInputData data)
    {
        if (_character == null) return;

        // Reconstruct the camera rotation quaternion from yaw + pitch
        // (matches FirstPersonCamera.Rotation)
        Quaternion camRot = Quaternion.Euler(data.PitchDegrees, data.YawDegrees, 0f);

        var inputs = new PlayerCharacterController.CharacterInputs
        {
            MoveAxisForward = data.MoveZ,
            MoveAxisRight   = data.MoveX,
            CameraRotation  = camRot,
            JumpDown        = data.JumpPressed,
            CrouchDown      = data.CrouchPressed,
            CrouchUp        = data.CrouchReleased,
            SprintHeld      = data.SprintHeld,
        };

        _character.SetInputs(ref inputs);

        // Consume one-frame signals so they don't repeat
        _latestInput.JumpPressed    = false;
        _latestInput.CrouchPressed  = false;
        _latestInput.CrouchReleased = false;
    }

    // =================================================================
    //  Server: combat input
    // =================================================================

    /// <summary>
    /// Handles fire/tool input on the server.
    /// </summary>
    private void HandleCombatInput(NetworkInputData data)
    {
        bool adminToolActive = _terrainTool != null && _terrainTool.isActiveAndEnabled;

        if (adminToolActive)
        {
            _terrainTool.SetAttackInput(data.FireHeld);
            if (_launcher != null) _launcher.SetFireInput(false);
        }
        else
        {
            if (_launcher != null) _launcher.SetFireInput(data.FireHeld);
        }
    }
}
