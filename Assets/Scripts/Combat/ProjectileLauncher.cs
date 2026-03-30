using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Server-authoritative projectile launcher.
///
/// PHASE 12 FLOW:
///   Client presses LMB → NetworkPlayerController sends FireHeld=true to server
///   → Server: ProjectileLauncher.SetFireInput(true) → Fire() → Instantiate
///     fireball → NetworkObject.Spawn() → all clients see it via NGO sync.
///
/// The server calculates the aim direction from the KCC's rotation
/// combined with the synced pitch from <see cref="NetworkPlayerController"/>.
///
/// Cooldown is enforced server-side — clients cannot cheat fire rate.
/// </summary>
public class ProjectileLauncher : NetworkBehaviour
{
    // ── References ───────────────────────────────────────────────────
    [Header("References")]
    [Tooltip("TerrainManager for crater deformation. Wired at runtime.")]
    public TerrainManager terrainManager;

    [Tooltip("Camera transform used for aim direction. Wired at runtime.")]
    public Transform cameraTransform;

    [Tooltip("The player's root GameObject (used for self-damage identification).")]
    public GameObject playerObject;

    [Tooltip("The player's main collider (ignored by spawned projectiles).")]
    public Collider playerCollider;

    // ── Prefabs ─────────────────────────────────────────────────────
    [Header("Prefabs")]
    [Tooltip("Fireball projectile prefab (must have Fireball + NetworkObject components).")]
    public GameObject FireballPrefab;

    [Tooltip("Fireball VFX prefab from PyroParticles (trail + glow).")]
    public GameObject FireballVFXPrefab;

    [Tooltip("Explosion VFX prefab from PyroParticles.")]
    public GameObject ExplosionVFXPrefab;

    // ── Audio ────────────────────────────────────────────────────────
    [Header("Audio")]
    [Tooltip("Sound played on each shot.")]
    public AudioClip LaunchSound;

    [Tooltip("Volume of the launch sound (0–1).")]
    [Range(0f, 1f)]
    public float LaunchVolume = 0.8f;

    // ── Fire settings ───────────────────────────────────────────────
    [Header("Fire Settings")]
    [Tooltip("Minimum seconds between shots.")]
    public float Cooldown = 1.25f;

    [Tooltip("Distance in front of the camera where the projectile spawns.")]
    public float SpawnOffset = 1.5f;

    // ── Projectile overrides ─────────────────────────────────────────
    [Header("Projectile Overrides")]
    public float SpeedOverride = 25f;
    public Vector3 GravityOverride = Vector3.zero;
    public float CraterRadius = 1.5f;
    public float CraterDelta = 30f;

    // ── Damage overrides ──────────────────────────────────────────
    [Header("Damage")]
    public float DirectDamage = 40f;
    public float SplashDamage = 25f;
    public float SplashRadius = 4f;
    [Range(0f, 1f)]
    public float SelfDamageMultiplier = 0.5f;

    // ── Public state (read by CombatHUD) ────────────────────────────
    public float CooldownProgress
    {
        get
        {
            if (Cooldown <= 0f) return 0f;
            float elapsed = Time.time - _lastFireTime;
            return Mathf.Clamp01(1f - elapsed / Cooldown);
        }
    }

    public bool IsReady => Time.time - _lastFireTime >= Cooldown;

    // ── Input state ─────────────────────────────────────────────────
    private bool _fireHeld;

    // ── Internals ───────────────────────────────────────────────────
    private float _lastFireTime = -999f;
    private AudioSource _audioSource;

    // =================================================================
    //  Public Input API (called by NetworkPlayerController on server)
    // =================================================================

    public void SetFireInput(bool held)
    {
        _fireHeld = held;
    }

    // =================================================================
    //  Lifecycle
    // =================================================================

    private void Awake()
    {
        _audioSource = gameObject.AddComponent<AudioSource>();
        _audioSource.playOnAwake = false;
        _audioSource.spatialBlend = 1f;           // full 3D
        _audioSource.rolloffMode  = AudioRolloffMode.Logarithmic;
        _audioSource.minDistance   = 5f;
        _audioSource.maxDistance   = 80f;
    }

    private void Update()
    {
        // Only the server processes fire logic
        if (!IsServer) return;

        if (_fireHeld && IsReady)
        {
            ServerFire();
        }
    }

    // =================================================================
    //  Server-authoritative fire
    // =================================================================

    private void ServerFire()
    {
        _lastFireTime = Time.time;

        // Calculate aim from the KCC rotation + synced pitch
        var netController = GetComponent<NetworkPlayerController>();
        float pitch = netController != null ? netController.SyncedPitch : 0f;
        Quaternion aimRot = Quaternion.Euler(pitch, transform.eulerAngles.y, 0f);
        Vector3 aimDir = aimRot * Vector3.forward;

        // Spawn position: in front of the character's eye height
        Vector3 eyePos = transform.position + Vector3.up * 1.6f; // approx eye height
        Vector3 spawnPos = eyePos + aimDir * SpawnOffset;
        Quaternion spawnRot = Quaternion.LookRotation(aimDir);

        // Instantiate the projectile
        GameObject projectileGO;
        if (FireballPrefab != null)
        {
            projectileGO = Instantiate(FireballPrefab, spawnPos, spawnRot);
        }
        else
        {
            projectileGO = CreateFallbackProjectile(spawnPos, spawnRot);
        }

        // Configure the Fireball component
        Fireball fireball = projectileGO.GetComponent<Fireball>();
        if (fireball == null)
            fireball = projectileGO.AddComponent<Fireball>();

        fireball.TerrainManager  = terrainManager;
        fireball.ShooterCollider = playerCollider;
        fireball.ShooterObject   = playerObject;

        if (SpeedOverride > 0f) fireball.Speed = SpeedOverride;
        fireball.Gravity      = GravityOverride;
        fireball.CraterRadius = CraterRadius;
        fireball.CraterDelta  = CraterDelta;
        fireball.DirectDamage        = DirectDamage;
        fireball.SplashDamage        = SplashDamage;
        fireball.SplashRadius        = SplashRadius;
        fireball.SelfDamageMultiplier = SelfDamageMultiplier;

        // NOTE: ExplosionPrefab and TrailVFXPrefab are now serialized on the
        // Fireball prefab itself.  Clients instantiate the prefab fresh from
        // NGO, so only inspector-serialized values survive across the network.
        // Do NOT override them here — the server's runtime writes would be
        // lost on every non-host client.

        // Spawn as NetworkObject — all clients will see it
        var netObj = projectileGO.GetComponent<NetworkObject>();
        if (netObj != null)
        {
            netObj.Spawn();
        }
        else
        {
            Debug.LogWarning("[ProjectileLauncher] Fireball prefab missing NetworkObject! " +
                             "Spawning locally only (not networked).");
        }

        // Play launch sound for all clients
        PlayLaunchSoundRpc(spawnPos);
    }

    [Rpc(SendTo.Everyone)]
    private void PlayLaunchSoundRpc(Vector3 position)
    {
        if (LaunchSound != null && _audioSource != null)
        {
            _audioSource.PlayOneShot(LaunchSound, LaunchVolume);
        }
    }

    // =================================================================
    //  Helpers
    // =================================================================

    private GameObject CreateFallbackProjectile(Vector3 pos, Quaternion rot)
    {
        GameObject go = new GameObject("Fireball");
        go.transform.position = pos;
        go.transform.rotation = rot;

        Rigidbody rb = go.AddComponent<Rigidbody>();
        rb.useGravity = false;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

        SphereCollider col = go.AddComponent<SphereCollider>();
        col.radius = 0.3f;

        // Visual
        GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        visual.transform.SetParent(go.transform, false);
        visual.transform.localScale = Vector3.one * 0.4f;

        Collider visualCol = visual.GetComponent<Collider>();
        if (visualCol != null) Object.DestroyImmediate(visualCol);

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
}
