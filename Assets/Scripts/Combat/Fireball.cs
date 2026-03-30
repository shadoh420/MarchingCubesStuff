using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Server-authoritative fireball projectile.
///
/// PHASE 12 CHANGES:
///   - <c>NetworkBehaviour</c> with <c>NetworkObject</c> + <c>NetworkTransform</c>.
///   - Rigidbody physics runs on the SERVER only (clients interpolate via NetworkTransform).
///   - <c>OnCollisionEnter</c> runs SERVER-ONLY → terrain edit + damage + VFX broadcast.
///   - VFX trail: each client instantiates locally in <see cref="OnNetworkSpawn"/>.
///   - VFX explosion: server broadcasts position via ClientRpc, each client instantiates.
///   - Destroy → <see cref="NetworkObject.Despawn"/> (server-only).
///
/// Self-damage is reduced by <see cref="SelfDamageMultiplier"/> (Quake-style).
/// </summary>
[RequireComponent(typeof(Rigidbody), typeof(SphereCollider))]
public class Fireball : NetworkBehaviour
{
    // ── Projectile settings ─────────────────────────────────────────
    [Header("Projectile")]
    public float Speed = 25f;
    public Vector3 Gravity = Vector3.zero;
    public float Lifetime = 8f;

    // ── Terrain deformation ─────────────────────────────────────────
    [Header("Crater")]
    public float CraterRadius = 1.5f;
    public float CraterDelta = 30f;

    // ── Damage ──────────────────────────────────────────────────────
    [Header("Damage")]
    public float DirectDamage = 40f;
    public float SplashDamage = 25f;
    public float SplashRadius = 4f;
    public float SelfDamageMultiplier = 0.5f;

    // ── VFX ─────────────────────────────────────────────────────────
    [Header("VFX")]
    public GameObject ExplosionPrefab;

    [Tooltip("Trail VFX prefab (PyroParticles fire trail). Assign on the prefab so all clients have it.")]
    public GameObject TrailVFXPrefab;

    // ── Runtime state (set by ProjectileLauncher on server) ─────────
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
        _rb.useGravity  = false;
        _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
    }

    public override void OnNetworkSpawn()
    {
        _spawnTime = Time.time;

        if (IsServer)
        {
            // Server drives physics
            _rb.isKinematic = false;
            _rb.linearVelocity = transform.forward * Speed;

            // Ignore shooter collision
            if (ShooterCollider != null)
            {
                Physics.IgnoreCollision(GetComponent<Collider>(), ShooterCollider, true);
            }
        }
        else
        {
            // Clients: disable physics (NetworkTransform handles position)
            _rb.isKinematic = true;
        }

        // All clients: attach trail VFX locally
        if (TrailVFXPrefab != null)
        {
            GameObject vfx = Instantiate(TrailVFXPrefab, transform.position, transform.rotation);
            vfx.transform.SetParent(transform, true);
            DisablePyroPhysics(vfx);
        }
    }

    private void FixedUpdate()
    {
        // Only server runs physics
        if (!IsServer) return;

        _rb.linearVelocity += Gravity * Time.fixedDeltaTime;

        if (Time.time - _spawnTime >= Lifetime)
        {
            DespawnProjectile();
        }
    }

    // =================================================================
    //  Collision — Server only
    // =================================================================

    private void OnCollisionEnter(Collision collision)
    {
        if (!IsServer || _hasCollided) return;
        _hasCollided = true;

        Vector3 impactPoint = collision.contactCount > 0
            ? collision.GetContact(0).point
            : transform.position;

        // ── Terrain deformation (via NetworkTerrainSync) ─────────────
        if (NetworkTerrainSync.Instance != null)
        {
            NetworkTerrainSync.Instance.ApplyTerrainEdit(impactPoint, CraterRadius, CraterDelta);
        }
        else if (TerrainManager != null)
        {
            // Fallback: apply locally only (non-networked)
            TerrainManager.EditTerrain(impactPoint, CraterRadius, CraterDelta);
        }

        // ── Splash damage (server-side) ──────────────────────────────
        ApplySplashDamage(impactPoint);

        // ── Explosion VFX (broadcast to all clients) ─────────────────
        SpawnExplosionRpc(impactPoint);

        // ── Despawn projectile ───────────────────────────────────────
        DespawnProjectile();
    }

    // =================================================================
    //  Damage — Server-only
    // =================================================================

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
    //  VFX — broadcast to all clients
    // =================================================================

    [Rpc(SendTo.Everyone)]
    private void SpawnExplosionRpc(Vector3 position)
    {
        if (ExplosionPrefab == null) return;

        GameObject explosion = Instantiate(ExplosionPrefab, position, Quaternion.identity);

        // Make all explosion audio sources 3D so volume falls off with distance
        foreach (AudioSource src in explosion.GetComponentsInChildren<AudioSource>(true))
        {
            src.spatialBlend = 1f;
            src.rolloffMode  = AudioRolloffMode.Logarithmic;
            src.minDistance   = 5f;
            src.maxDistance   = 100f;
        }

        Destroy(explosion, 6f);
    }

    // =================================================================
    //  Cleanup
    // =================================================================

    private void DespawnProjectile()
    {
        if (!IsServer) return;

        var netObj = GetComponent<NetworkObject>();
        if (netObj != null && netObj.IsSpawned)
        {
            netObj.Despawn();
        }
        else
        {
            Destroy(gameObject);
        }
    }

    // =================================================================
    //  Helpers
    // =================================================================

    private void DisablePyroPhysics(GameObject vfxRoot)
    {
        foreach (Rigidbody rb in vfxRoot.GetComponentsInChildren<Rigidbody>(true))
        {
            if (!rb.isKinematic)
            {
                rb.linearVelocity = Vector3.zero;
                rb.isKinematic = true;
            }
        }

        foreach (Collider col in vfxRoot.GetComponentsInChildren<Collider>(true))
        {
            col.enabled = false;
        }

        foreach (var script in vfxRoot.GetComponentsInChildren<DigitalRuby.PyroParticles.FireProjectileScript>(true))
        {
            script.enabled = false;
        }

        foreach (var fwd in vfxRoot.GetComponentsInChildren<DigitalRuby.PyroParticles.FireCollisionForwardScript>(true))
        {
            fwd.enabled = false;
        }
    }
}
