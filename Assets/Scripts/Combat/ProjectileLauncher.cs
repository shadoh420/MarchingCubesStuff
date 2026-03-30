using UnityEngine;

/// <summary>
/// First-person projectile launcher. Fires <see cref="Fireball"/> projectiles
/// from slightly in front of the camera center on LMB press.
///
/// Input is fed each frame by <see cref="PlayerInputManager"/> through
/// <see cref="SetFireInput"/>, following the same pattern as
/// <see cref="TerrainTool.SetAttackInput"/>.
///
/// Configurable fire rate (cooldown), projectile prefab reference,
/// speed overrides, and crater parameters. All tuneable in Inspector.
/// </summary>
public class ProjectileLauncher : MonoBehaviour
{
    // ── References ───────────────────────────────────────────────────
    [Header("References")]
    [Tooltip("TerrainManager for crater deformation. Wired at runtime by PlayerSpawnManager.")]
    public TerrainManager terrainManager;

    [Tooltip("Camera transform used for aim direction. Wired at runtime by PlayerSpawnManager.")]
    public Transform cameraTransform;

    [Tooltip("The player's root GameObject (used for self-damage identification).")]
    public GameObject playerObject;

    [Tooltip("The player's main collider (ignored by spawned projectiles).")]
    public Collider playerCollider;

    // ── Prefabs ─────────────────────────────────────────────────────
    [Header("Prefabs")]
    [Tooltip("Fireball projectile prefab (must have Fireball component).")]
    public GameObject FireballPrefab;

    [Tooltip("Fireball VFX prefab from PyroParticles (trail + glow). Spawned as a child of the projectile.")]
    public GameObject FireballVFXPrefab;

    [Tooltip("Explosion VFX prefab from PyroParticles. Passed to the Fireball for impact.")]
    public GameObject ExplosionVFXPrefab;

    // ── Audio ────────────────────────────────────────────────────────
    [Header("Audio")]
    [Tooltip("Sound played on each shot. Drag a FireShoot clip from PyroParticles/Prefab/Audio.")]
    public AudioClip LaunchSound;

    [Tooltip("Volume of the launch sound (0–1).")]
    [Range(0f, 1f)]
    public float LaunchVolume = 0.8f;

    // ── Fire settings ───────────────────────────────────────────────
    [Header("Fire Settings")]
    [Tooltip("Minimum seconds between shots.")]
    public float Cooldown = 1.25f;

    [Tooltip("Distance in front of the camera where the projectile spawns (avoids self-collision).")]
    public float SpawnOffset = 1.5f;

    // ── Projectile overrides (applied to each spawned Fireball) ─────
    [Header("Projectile Overrides")]
    [Tooltip("Forward speed (m/s). 0 = use Fireball prefab default.")]
    public float SpeedOverride = 25f;

    [Tooltip("Custom gravity for the projectile.")]
    public Vector3 GravityOverride = Vector3.zero;

    [Tooltip("Crater radius on terrain impact.")]
    public float CraterRadius = 1.5f;

    [Tooltip("Crater density delta on terrain impact.")]
    public float CraterDelta = 30f;

    // ── Damage overrides ──────────────────────────────────────────
    [Header("Damage")]
    [Tooltip("Direct-hit damage dealt to a player.")]
    public float DirectDamage = 40f;

    [Tooltip("Maximum splash damage at the centre of the explosion.")]
    public float SplashDamage = 25f;

    [Tooltip("Splash damage radius (world units).")]
    public float SplashRadius = 4f;

    [Tooltip("Multiplier for self-inflicted splash damage (0.5 = Quake-style).")]
    [Range(0f, 1f)]
    public float SelfDamageMultiplier = 0.5f;

    // ── Public state (read by CombatHUD) ────────────────────────────
    /// <summary>Normalized cooldown progress: 0 = ready, 1 = just fired.</summary>
    public float CooldownProgress
    {
        get
        {
            if (Cooldown <= 0f) return 0f;
            float elapsed = Time.time - _lastFireTime;
            return Mathf.Clamp01(1f - elapsed / Cooldown);
        }
    }

    /// <summary>True when the launcher is ready to fire.</summary>
    public bool IsReady => Time.time - _lastFireTime >= Cooldown;

    // ── Input state ─────────────────────────────────────────────────
    private bool _fireHeld;
    private bool _firedThisPress;

    // ── Internals ───────────────────────────────────────────────────
    private float _lastFireTime = -999f;
    private AudioSource _audioSource;

    // =================================================================
    //  Public Input API
    // =================================================================

    /// <summary>
    /// Called by <see cref="PlayerInputManager"/> each frame with the
    /// current state of the Attack/Fire action.
    /// Fires on press (not hold-to-spam) to match typical shooter feel.
    /// </summary>
    public void SetFireInput(bool held)
    {
        // Detect rising edge (press, not hold)
        if (held && !_fireHeld)
        {
            _firedThisPress = false;
        }

        _fireHeld = held;
    }

    // =================================================================
    //  Lifecycle
    // =================================================================

    private void Awake()
    {
        // Create a dedicated AudioSource on this GameObject for launch sounds
        _audioSource = gameObject.AddComponent<AudioSource>();
        _audioSource.playOnAwake = false;
        _audioSource.spatialBlend = 0f; // 2D sound for the local player
    }

    private void Update()
    {
        if (cameraTransform == null) return;

        // Fire on press or allow hold-to-fire with cooldown
        if (_fireHeld && IsReady)
        {
            Fire();
        }
    }

    // =================================================================
    //  Fire
    // =================================================================

    /// <summary>
    /// Spawns a fireball projectile at the camera spawn point, aimed
    /// along the camera forward direction.
    /// </summary>
    private void Fire()
    {
        _lastFireTime = Time.time;

        // Play launch sound
        if (LaunchSound != null && _audioSource != null)
        {
            _audioSource.PlayOneShot(LaunchSound, LaunchVolume);
        }

        // Spawn position: slightly in front of camera to clear the player capsule
        Vector3 spawnPos = cameraTransform.position + cameraTransform.forward * SpawnOffset;
        Quaternion spawnRot = cameraTransform.rotation;

        // Instantiate the projectile
        GameObject projectileGO;
        if (FireballPrefab != null)
        {
            projectileGO = Instantiate(FireballPrefab, spawnPos, spawnRot);
        }
        else
        {
            // Fallback: create a bare projectile if no prefab is assigned
            projectileGO = CreateFallbackProjectile(spawnPos, spawnRot);
        }

        // Configure the Fireball component
        Fireball fireball = projectileGO.GetComponent<Fireball>();
        if (fireball == null)
            fireball = projectileGO.AddComponent<Fireball>();

        fireball.TerrainManager  = terrainManager;
        fireball.ShooterCollider = playerCollider;
        fireball.ShooterObject   = playerObject;

        // Apply overrides
        if (SpeedOverride > 0f) fireball.Speed = SpeedOverride;
        fireball.Gravity      = GravityOverride;
        fireball.CraterRadius = CraterRadius;
        fireball.CraterDelta  = CraterDelta;

        // Apply damage overrides
        fireball.DirectDamage        = DirectDamage;
        fireball.SplashDamage        = SplashDamage;
        fireball.SplashRadius        = SplashRadius;
        fireball.SelfDamageMultiplier = SelfDamageMultiplier;

        // Pass explosion VFX prefab
        if (ExplosionVFXPrefab != null)
            fireball.ExplosionPrefab = ExplosionVFXPrefab;

        // Attach trail VFX as a child (PyroParticles visual only)
        if (FireballVFXPrefab != null)
        {
            GameObject vfx = Instantiate(FireballVFXPrefab, spawnPos, spawnRot);
            vfx.transform.SetParent(projectileGO.transform, true);

            // Disable the PyroParticles physics (we have our own)
            DisablePyroPhysics(vfx);
        }
    }

    // =================================================================
    //  Helpers
    // =================================================================

    /// <summary>
    /// Creates a minimal projectile GameObject when no prefab is assigned.
    /// Useful for testing without prefabs configured.
    /// </summary>
    private GameObject CreateFallbackProjectile(Vector3 pos, Quaternion rot)
    {
        GameObject go = new GameObject("Fireball");
        go.transform.position = pos;
        go.transform.rotation = rot;

        // Rigidbody
        Rigidbody rb = go.AddComponent<Rigidbody>();
        rb.useGravity = false;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

        // Small sphere collider
        SphereCollider col = go.AddComponent<SphereCollider>();
        col.radius = 0.3f;

        // Visual: tiny sphere so we can at least see something
        GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        visual.transform.SetParent(go.transform, false);
        visual.transform.localScale = Vector3.one * 0.4f;

        // Remove the primitive's collider (our parent has the real one)
        Collider visualCol = visual.GetComponent<Collider>();
        if (visualCol != null) Object.DestroyImmediate(visualCol);

        // Orange emissive material
        Renderer rend = visual.GetComponent<Renderer>();
        if (rend != null)
        {
            Material mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            mat.SetColor("_BaseColor", new Color(1f, 0.5f, 0.1f));
            mat.SetColor("_EmissionColor", new Color(2f, 1f, 0.2f));
            mat.EnableKeyword("_EMISSION");
            rend.sharedMaterial = mat;
        }

        return go;
    }

    /// <summary>
    /// Disables Rigidbody and Collider components on the PyroParticles
    /// VFX instance so it doesn't interfere with our own projectile
    /// physics. The VFX is purely visual.
    /// </summary>
    private void DisablePyroPhysics(GameObject vfxRoot)
    {
        // Disable all rigidbodies (set velocity BEFORE kinematic to avoid warning)
        foreach (Rigidbody rb in vfxRoot.GetComponentsInChildren<Rigidbody>(true))
        {
            if (!rb.isKinematic)
            {
                rb.linearVelocity = Vector3.zero;
                rb.isKinematic = true;
            }
        }

        // Disable all colliders
        foreach (Collider col in vfxRoot.GetComponentsInChildren<Collider>(true))
        {
            col.enabled = false;
        }

        // Disable PyroParticles scripts that interfere with our physics/collision
        foreach (var script in vfxRoot.GetComponentsInChildren<DigitalRuby.PyroParticles.FireProjectileScript>(true))
        {
            script.enabled = false;
        }

        // Disable collision forwarders — their CollisionHandler is null since
        // we're not using PyroParticles' collision system, causing NullRefs.
        foreach (var fwd in vfxRoot.GetComponentsInChildren<DigitalRuby.PyroParticles.FireCollisionForwardScript>(true))
        {
            fwd.enabled = false;
        }
    }
}
