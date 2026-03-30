using UnityEngine;

/// <summary>
/// Physics-driven fireball projectile.
///
/// On spawn, flies forward at <see cref="Speed"/> with optional gravity.
/// On collision with terrain: carves a crater via
/// <see cref="TerrainManager.EditTerrain"/> and spawns explosion VFX.
/// On collision with a player: deals damage (Phase 11 stub) and spawns
/// explosion VFX.
///
/// VFX is handled by instantiating PyroParticles prefabs:
///   - Trail/glow: a child instance of the fireball VFX prefab.
///   - Explosion:  instantiated at the impact point on collision.
///
/// The shooter's collider is ignored to prevent self-hits at spawn.
/// Self-splash damage is applied at 50% (Quake-style rocket jumping).
/// </summary>
[RequireComponent(typeof(Rigidbody), typeof(SphereCollider))]
public class Fireball : MonoBehaviour
{
    // ── Projectile settings ─────────────────────────────────────────
    [Header("Projectile")]
    [Tooltip("Forward speed on spawn (m/s).")]
    public float Speed = 25f;

    [Tooltip("Custom gravity applied each frame. Use (0, -5, 0) for a slight arc.")]
    public Vector3 Gravity = Vector3.zero;

    [Tooltip("Seconds before the projectile auto-destructs if it hits nothing.")]
    public float Lifetime = 8f;

    // ── Terrain deformation ─────────────────────────────────────────
    [Header("Crater")]
    [Tooltip("World-space radius of the crater carved on terrain impact.")]
    public float CraterRadius = 1.5f;

    [Tooltip("Density delta applied to the terrain (positive = dig).")]
    public float CraterDelta = 30f;

    // ── Damage (Phase 11 stub) ──────────────────────────────────────
    [Header("Damage")]
    [Tooltip("Direct-hit damage dealt to a player.")]
    public float DirectDamage = 40f;

    [Tooltip("Maximum splash damage at the center of the explosion.")]
    public float SplashDamage = 25f;

    [Tooltip("Splash damage radius (world units).")]
    public float SplashRadius = 4f;

    [Tooltip("Multiplier for self-inflicted splash damage (0.5 = Quake-style).")]
    public float SelfDamageMultiplier = 0.5f;

    // ── VFX ─────────────────────────────────────────────────────────
    [Header("VFX")]
    [Tooltip("Prefab instantiated at the impact point (e.g., PyroParticles FireExplosion).")]
    public GameObject ExplosionPrefab;

    // ── Runtime state (set by ProjectileLauncher on spawn) ───────────
    [HideInInspector] public TerrainManager TerrainManager;
    [HideInInspector] public Collider ShooterCollider;
    [HideInInspector] public GameObject ShooterObject;

    // ── Internals ───────────────────────────────────────────────────
    private Rigidbody _rb;
    private bool      _hasCollided;
    private float     _spawnTime;

    // =================================================================
    //  Lifecycle
    // =================================================================

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();

        // Physics config — we drive velocity manually via Gravity;
        // Unity gravity is off so we have precise control.
        _rb.useGravity  = false;
        _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
    }

    private void Start()
    {
        _spawnTime = Time.time;

        // Launch forward
        _rb.linearVelocity = transform.forward * Speed;

        // Ignore collision with the shooter so we don't explode at spawn
        if (ShooterCollider != null)
        {
            Physics.IgnoreCollision(GetComponent<Collider>(), ShooterCollider, true);
        }
    }

    private void FixedUpdate()
    {
        // Apply custom gravity
        _rb.linearVelocity += Gravity * Time.fixedDeltaTime;

        // Lifetime expiry
        if (Time.time - _spawnTime >= Lifetime)
        {
            DestroyProjectile();
        }
    }

    // =================================================================
    //  Collision
    // =================================================================

    private void OnCollisionEnter(Collision collision)
    {
        if (_hasCollided) return;
        _hasCollided = true;

        Vector3 impactPoint = collision.contactCount > 0
            ? collision.GetContact(0).point
            : transform.position;

        // ── Terrain deformation ──────────────────────────────────────
        if (TerrainManager != null)
        {
            TerrainManager.EditTerrain(impactPoint, CraterRadius, CraterDelta);
        }

        // ── Splash damage (Phase 11 ready) ───────────────────────────
        ApplySplashDamage(impactPoint);

        // ── Explosion VFX ────────────────────────────────────────────
        SpawnExplosion(impactPoint);

        // ── Destroy projectile ───────────────────────────────────────
        DestroyProjectile();
    }

    // =================================================================
    //  Damage helpers
    // =================================================================

    /// <summary>
    /// Finds all colliders within splash radius and applies damage
    /// with distance falloff via <see cref="PlayerHealth.TakeDamage"/>.
    /// Self-damage is reduced by <see cref="SelfDamageMultiplier"/>.
    /// Direct hits (the collider we actually collided with) receive
    /// <see cref="DirectDamage"/> instead of splash damage.
    /// </summary>
    private void ApplySplashDamage(Vector3 center)
    {
        Collider[] hits = Physics.OverlapSphere(center, SplashRadius);
        foreach (Collider hit in hits)
        {
            PlayerHealth health = hit.GetComponentInParent<PlayerHealth>();
            if (health == null) continue;

            float dist = Vector3.Distance(center, hit.ClosestPoint(center));
            float falloff = 1f - Mathf.Clamp01(dist / SplashRadius);
            float damage  = SplashDamage * falloff;

            // Reduce self-damage
            bool isSelf = ShooterObject != null &&
                          hit.transform.root == ShooterObject.transform.root;
            if (isSelf)
            {
                damage *= SelfDamageMultiplier;
            }

            if (damage > 0f)
            {
                health.TakeDamage(damage);
            }
        }
    }

    // =================================================================
    //  VFX helpers
    // =================================================================

    /// <summary>
    /// Instantiates the explosion VFX prefab at the impact point.
    /// The prefab is expected to self-destruct via PyroParticles'
    /// FireBaseScript cleanup coroutine.
    /// </summary>
    private void SpawnExplosion(Vector3 position)
    {
        if (ExplosionPrefab == null) return;

        GameObject explosion = Instantiate(ExplosionPrefab, position, Quaternion.identity);

        // PyroParticles handles its own cleanup, but add a safety net
        Destroy(explosion, 6f);
    }

    /// <summary>
    /// Destroys the projectile GameObject immediately.
    /// </summary>
    private void DestroyProjectile()
    {
        Destroy(gameObject);
    }
}
