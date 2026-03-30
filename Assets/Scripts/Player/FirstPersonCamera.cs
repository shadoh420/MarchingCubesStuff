using UnityEngine;

/// <summary>
/// First-person camera that follows the character's
/// <see cref="PlayerCharacterController.CameraFollowPoint"/> and applies
/// mouse-look rotation (yaw + pitch).
///
/// Yaw rotates the character (via input fed to the controller).
/// Pitch is applied locally to this transform so the camera can look
/// up/down without tilting the capsule.
///
/// This component lives on the Camera GameObject, NOT on the character.
/// </summary>
public class FirstPersonCamera : MonoBehaviour
{
    // ── References ───────────────────────────────────────────────────
    [Header("References")]
    [Tooltip("The transform this camera follows (CameraFollowPoint on the character).")]
    public Transform FollowPoint;

    // ── Sensitivity ─────────────────────────────────────────────────
    [Header("Look Settings")]
    [Tooltip("Mouse sensitivity multiplier.")]
    public float Sensitivity = 0.1f;

    [Tooltip("Minimum vertical look angle (looking down).")]
    public float MinVerticalAngle = -89f;

    [Tooltip("Maximum vertical look angle (looking up).")]
    public float MaxVerticalAngle = 89f;

    [Tooltip("Sharpness for position following. Very high = instant.")]
    public float FollowSharpness = 10000f;

    // ── State ───────────────────────────────────────────────────────
    private float _targetPitch;
    private float _targetYaw;

    /// <summary>Current pitch angle (degrees). Read by external systems if needed.</summary>
    public float Pitch => _targetPitch;

    /// <summary>Current yaw angle (degrees).</summary>
    public float Yaw => _targetYaw;

    /// <summary>The camera's full rotation (includes pitch).</summary>
    public Quaternion Rotation => Quaternion.Euler(_targetPitch, _targetYaw, 0f);

    // =================================================================
    //  Public API
    // =================================================================

    /// <summary>
    /// Called by <see cref="PlayerInputManager"/> every LateUpdate with
    /// the raw mouse delta from the Input System.
    /// </summary>
    public void UpdateWithInput(float deltaTime, Vector2 lookDelta)
    {
        if (FollowPoint == null) return;

        // Accumulate yaw and pitch
        _targetYaw   += lookDelta.x * Sensitivity;
        _targetPitch -= lookDelta.y * Sensitivity;
        _targetPitch  = Mathf.Clamp(_targetPitch, MinVerticalAngle, MaxVerticalAngle);

        // Apply rotation (pitch + yaw)
        transform.rotation = Quaternion.Euler(_targetPitch, _targetYaw, 0f);

        // Follow position
        Vector3 targetPos = FollowPoint.position;
        transform.position = Vector3.Lerp(transform.position, targetPos,
            1f - Mathf.Exp(-FollowSharpness * deltaTime));
    }

    /// <summary>
    /// Sets the follow target at runtime (e.g., after spawn).
    /// Initialises yaw/pitch from the character's current facing.
    /// </summary>
    public void SetFollowPoint(Transform point)
    {
        FollowPoint = point;
        if (point != null)
        {
            Vector3 euler = point.rotation.eulerAngles;
            _targetYaw   = euler.y;
            _targetPitch = 0f;
            transform.position = point.position;
        }
    }
}
