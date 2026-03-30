using UnityEngine;
using KinematicCharacterController;

/// <summary>
/// First-person character controller built on KCC's ICharacterController.
///
/// Provides walk, sprint, jump, crouch, gravity, and slope handling on
/// marching-cubes MeshCollider terrain. The motor's capsule rides the
/// surface; rotation is locked to the Y axis (yaw only — pitch is
/// handled by <see cref="FirstPersonCamera"/>).
///
/// Input is fed each frame via <see cref="SetInputs"/> from
/// <see cref="PlayerInputManager"/>.
/// </summary>
[RequireComponent(typeof(KinematicCharacterMotor))]
public class PlayerCharacterController : MonoBehaviour, ICharacterController
{
    // ── References ───────────────────────────────────────────────────
    [Header("References")]
    [Tooltip("Assigned automatically if left null.")]
    public KinematicCharacterMotor Motor;

    [Tooltip("Child transform the camera pivots around (eye height).")]
    public Transform CameraFollowPoint;

    // ── Stable (ground) movement ────────────────────────────────────
    [Header("Stable Movement")]
    [Tooltip("Walk speed (m/s).")]
    public float WalkSpeed = 6f;

    [Tooltip("Sprint speed (m/s).")]
    public float SprintSpeed = 10f;

    [Tooltip("How fast the character reaches target speed on the ground.")]
    public float StableMovementSharpness = 15f;

    // ── Air movement ────────────────────────────────────────────────
    [Header("Air Movement")]
    public float MaxAirMoveSpeed = 6f;
    public float AirAccelerationSpeed = 15f;
    public float Drag = 0.1f;

    // ── Jumping ─────────────────────────────────────────────────────
    [Header("Jumping")]
    public float JumpUpSpeed = 10f;
    public float JumpScalableForwardSpeed = 2f;

    [Tooltip("Coyote time: seconds after leaving ground where jump is still allowed.")]
    public float JumpPostGroundingGraceTime = 0.1f;

    [Tooltip("Buffer time: seconds before landing where a jump press is remembered.")]
    public float JumpPreGroundingGraceTime = 0.1f;

    public bool AllowJumpingWhenSliding = false;

    // ── Crouching ───────────────────────────────────────────────────
    [Header("Crouching")]
    public float CrouchedCapsuleHeight = 1f;
    public float CrouchSpeed = 4f;

    [Tooltip("Standing capsule radius (default KCC value).")]
    public float StandingCapsuleRadius = 0.5f;

    [Tooltip("Standing capsule height.")]
    public float StandingCapsuleHeight = 2f;

    [Tooltip("Standing capsule Y offset.")]
    public float StandingCapsuleYOffset = 1f;

    // ── Gravity ─────────────────────────────────────────────────────
    [Header("Gravity")]
    public Vector3 Gravity = new Vector3(0f, -30f, 0f);

    // ── State ───────────────────────────────────────────────────────
    /// <summary>True while the character is in the crouched stance.</summary>
    public bool IsCrouching { get; private set; }

    // ── Input cache (set each frame by PlayerInputManager) ──────────
    private Vector3 _moveInputVector;
    private Vector3 _lookInputVector;
    private bool    _jumpRequested;
    private bool    _jumpConsumed;
    private bool    _jumpedThisFrame;
    private float   _timeSinceJumpRequested = Mathf.Infinity;
    private float   _timeSinceLastAbleToJump;
    private bool    _shouldBeCrouching;
    private bool    _sprintRequested;

    private Collider[] _probedColliders = new Collider[8];

    // =================================================================
    //  Unity Lifecycle
    // =================================================================

    private void Awake()
    {
        if (Motor == null)
            Motor = GetComponent<KinematicCharacterMotor>();

        Motor.CharacterController = this;
    }

    // =================================================================
    //  Public Input API
    // =================================================================

    /// <summary>
    /// Struct fed by <see cref="PlayerInputManager"/> every frame.
    /// </summary>
    public struct CharacterInputs
    {
        public float   MoveAxisForward;
        public float   MoveAxisRight;
        public Quaternion CameraRotation;
        public bool    JumpDown;
        public bool    CrouchDown;
        public bool    CrouchUp;
        public bool    SprintHeld;
    }

    /// <summary>
    /// Called every frame by <see cref="PlayerInputManager"/>.
    /// Translates raw input into the vectors the motor callbacks consume.
    /// </summary>
    public void SetInputs(ref CharacterInputs inputs)
    {
        // Build world-space move vector relative to camera
        Vector3 moveInput = Vector3.ClampMagnitude(
            new Vector3(inputs.MoveAxisRight, 0f, inputs.MoveAxisForward), 1f);

        Vector3 cameraPlanarDir = Vector3.ProjectOnPlane(
            inputs.CameraRotation * Vector3.forward, Motor.CharacterUp).normalized;
        if (cameraPlanarDir.sqrMagnitude == 0f)
            cameraPlanarDir = Vector3.ProjectOnPlane(
                inputs.CameraRotation * Vector3.up, Motor.CharacterUp).normalized;

        Quaternion cameraPlanarRot = Quaternion.LookRotation(cameraPlanarDir, Motor.CharacterUp);
        _moveInputVector = cameraPlanarRot * moveInput;
        _lookInputVector = cameraPlanarDir;

        _sprintRequested = inputs.SprintHeld;

        // Jump
        if (inputs.JumpDown)
        {
            _timeSinceJumpRequested = 0f;
            _jumpRequested = true;
        }

        // Crouch
        if (inputs.CrouchDown)
        {
            _shouldBeCrouching = true;
            if (!IsCrouching)
            {
                IsCrouching = true;
                Motor.SetCapsuleDimensions(
                    StandingCapsuleRadius,
                    CrouchedCapsuleHeight,
                    CrouchedCapsuleHeight * 0.5f);
            }
        }
        else if (inputs.CrouchUp)
        {
            _shouldBeCrouching = false;
        }
    }

    // =================================================================
    //  ICharacterController — Motor callbacks
    // =================================================================

    #region ICharacterController

    /// <summary>
    /// Lock the capsule to face the camera yaw. Pitch is visual only
    /// (handled by <see cref="FirstPersonCamera"/>).
    /// </summary>
    public void UpdateRotation(ref Quaternion currentRotation, float deltaTime)
    {
        if (_lookInputVector.sqrMagnitude > 0f)
        {
            currentRotation = Quaternion.LookRotation(_lookInputVector, Motor.CharacterUp);
        }

        // Keep character upright
        Vector3 currentUp = currentRotation * Vector3.up;
        Vector3 targetUp  = -Gravity.normalized;
        Vector3 smoothedUp = Vector3.Slerp(currentUp, targetUp,
            1f - Mathf.Exp(-10f * deltaTime));
        currentRotation = Quaternion.FromToRotation(currentUp, smoothedUp) * currentRotation;
    }

    /// <summary>
    /// Ground movement, air movement, gravity, jumping.
    /// </summary>
    public void UpdateVelocity(ref Vector3 currentVelocity, float deltaTime)
    {
        if (Motor.GroundingStatus.IsStableOnGround)
        {
            // ── Grounded ────────────────────────────────────────────
            float currentSpeed = currentVelocity.magnitude;
            Vector3 groundNormal = Motor.GroundingStatus.GroundNormal;

            // Reorient velocity on slope
            currentVelocity = Motor.GetDirectionTangentToSurface(currentVelocity, groundNormal) * currentSpeed;

            // Target speed: sprint > walk > crouch
            float targetSpeed = WalkSpeed;
            if (_sprintRequested && !IsCrouching) targetSpeed = SprintSpeed;
            else if (IsCrouching) targetSpeed = CrouchSpeed;

            // Reorient input onto slope
            Vector3 inputRight = Vector3.Cross(_moveInputVector, Motor.CharacterUp);
            Vector3 reorientedInput = Vector3.Cross(groundNormal, inputRight).normalized
                                      * _moveInputVector.magnitude;
            Vector3 targetVelocity = reorientedInput * targetSpeed;

            currentVelocity = Vector3.Lerp(currentVelocity, targetVelocity,
                1f - Mathf.Exp(-StableMovementSharpness * deltaTime));
        }
        else
        {
            // ── Airborne ────────────────────────────────────────────
            if (_moveInputVector.sqrMagnitude > 0f)
            {
                Vector3 addedVelocity = _moveInputVector * AirAccelerationSpeed * deltaTime;
                Vector3 velOnPlane = Vector3.ProjectOnPlane(currentVelocity, Motor.CharacterUp);

                if (velOnPlane.magnitude < MaxAirMoveSpeed)
                {
                    Vector3 newTotal = Vector3.ClampMagnitude(velOnPlane + addedVelocity, MaxAirMoveSpeed);
                    addedVelocity = newTotal - velOnPlane;
                }
                else
                {
                    if (Vector3.Dot(velOnPlane, addedVelocity) > 0f)
                        addedVelocity = Vector3.ProjectOnPlane(addedVelocity, velOnPlane.normalized);
                }

                // Prevent air-climbing slopes
                if (Motor.GroundingStatus.FoundAnyGround)
                {
                    if (Vector3.Dot(currentVelocity + addedVelocity, addedVelocity) > 0f)
                    {
                        Vector3 obstructNormal = Vector3.Cross(
                            Vector3.Cross(Motor.CharacterUp, Motor.GroundingStatus.GroundNormal),
                            Motor.CharacterUp).normalized;
                        addedVelocity = Vector3.ProjectOnPlane(addedVelocity, obstructNormal);
                    }
                }

                currentVelocity += addedVelocity;
            }

            // Gravity
            currentVelocity += Gravity * deltaTime;

            // Drag
            currentVelocity *= (1f / (1f + Drag * deltaTime));
        }

        // ── Jump ────────────────────────────────────────────────────
        _jumpedThisFrame = false;
        _timeSinceJumpRequested += deltaTime;

        if (_jumpRequested)
        {
            bool canJump = !_jumpConsumed &&
                ((AllowJumpingWhenSliding
                      ? Motor.GroundingStatus.FoundAnyGround
                      : Motor.GroundingStatus.IsStableOnGround)
                 || _timeSinceLastAbleToJump <= JumpPostGroundingGraceTime);

            if (canJump)
            {
                Vector3 jumpDir = Motor.CharacterUp;
                if (Motor.GroundingStatus.FoundAnyGround && !Motor.GroundingStatus.IsStableOnGround)
                    jumpDir = Motor.GroundingStatus.GroundNormal;

                Motor.ForceUnground();

                currentVelocity += (jumpDir * JumpUpSpeed) - Vector3.Project(currentVelocity, Motor.CharacterUp);
                currentVelocity += _moveInputVector * JumpScalableForwardSpeed;

                _jumpRequested  = false;
                _jumpConsumed   = true;
                _jumpedThisFrame = true;
            }
        }
    }

    public void BeforeCharacterUpdate(float deltaTime) { }

    public void PostGroundingUpdate(float deltaTime)
    {
        // Landing / leaving ground hooks (for future SFX)
    }

    public void AfterCharacterUpdate(float deltaTime)
    {
        // Jump timers
        if (_jumpRequested && _timeSinceJumpRequested > JumpPreGroundingGraceTime)
            _jumpRequested = false;

        if (AllowJumpingWhenSliding
                ? Motor.GroundingStatus.FoundAnyGround
                : Motor.GroundingStatus.IsStableOnGround)
        {
            if (!_jumpedThisFrame)
                _jumpConsumed = false;
            _timeSinceLastAbleToJump = 0f;
        }
        else
        {
            _timeSinceLastAbleToJump += deltaTime;
        }

        // Un-crouch when input released and space is clear
        if (IsCrouching && !_shouldBeCrouching)
        {
            Motor.SetCapsuleDimensions(StandingCapsuleRadius, StandingCapsuleHeight, StandingCapsuleYOffset);
            if (Motor.CharacterOverlap(
                    Motor.TransientPosition, Motor.TransientRotation,
                    _probedColliders, Motor.CollidableLayers,
                    QueryTriggerInteraction.Ignore) > 0)
            {
                Motor.SetCapsuleDimensions(StandingCapsuleRadius, CrouchedCapsuleHeight, CrouchedCapsuleHeight * 0.5f);
            }
            else
            {
                IsCrouching = false;
            }
        }
    }

    public bool IsColliderValidForCollisions(Collider coll)
    {
        return true;
    }

    public void OnGroundHit(Collider hitCollider, Vector3 hitNormal, Vector3 hitPoint,
        ref HitStabilityReport hitStabilityReport) { }

    public void OnMovementHit(Collider hitCollider, Vector3 hitNormal, Vector3 hitPoint,
        ref HitStabilityReport hitStabilityReport) { }

    public void ProcessHitStabilityReport(Collider hitCollider, Vector3 hitNormal,
        Vector3 hitPoint, Vector3 atCharacterPosition, Quaternion atCharacterRotation,
        ref HitStabilityReport hitStabilityReport) { }

    public void OnDiscreteCollisionDetected(Collider hitCollider) { }

    #endregion
}
