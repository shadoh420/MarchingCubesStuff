using UnityEngine;
using System;

/// <summary>
/// Player health component. Tracks HP, handles damage, death, and respawn.
///
/// Placed on the same GameObject as <see cref="PlayerCharacterController"/>.
/// Splash damage from <see cref="Fireball"/> calls <see cref="TakeDamage"/>.
///
/// Death disables the character motor and camera, waits for
/// <see cref="RespawnDelay"/>, then respawns on the terrain surface
/// using the same logic as <see cref="PlayerSpawnManager"/>.
/// </summary>
public class PlayerHealth : MonoBehaviour
{
    // ── Health settings ─────────────────────────────────────────────
    [Header("Health")]
    [Tooltip("Maximum hit points.")]
    public float MaxHP = 100f;

    [Tooltip("Current hit points (readonly in Inspector for debugging).")]
    [SerializeField]
    private float _currentHP;

    // ── Respawn settings ────────────────────────────────────────────
    [Header("Respawn")]
    [Tooltip("Seconds after death before respawning.")]
    public float RespawnDelay = 3f;

    [Tooltip("Height from which the surface raycast is cast downward on respawn.")]
    public float RespawnRaycastY = 200f;

    [Tooltip("Extra height above the surface to place the respawned player.")]
    public float RespawnHeightOffset = 2f;

    // ── References (wired by PlayerSpawnManager) ─────────────────────
    [Header("References")]
    [Tooltip("The KCC character controller.")]
    public PlayerCharacterController Character;

    [Tooltip("The first-person camera.")]
    public FirstPersonCamera Camera;

    [Tooltip("TerrainManager for surface finding on respawn.")]
    public TerrainManager TerrainManager;

    [Tooltip("The projectile launcher (disabled during death).")]
    public ProjectileLauncher Launcher;

    [Tooltip("Player visuals component (hidden during death).")]
    public PlayerVisuals Visuals;

    // ── Events ──────────────────────────────────────────────────────
    /// <summary>Fired when damage is taken. Args: current HP, max HP.</summary>
    public event Action<float, float> OnDamaged;

    /// <summary>Fired on death.</summary>
    public event Action OnDied;

    /// <summary>Fired on respawn. Args: current HP, max HP.</summary>
    public event Action<float, float> OnRespawned;

    // ── Public state ────────────────────────────────────────────────
    /// <summary>Current HP (readonly).</summary>
    public float CurrentHP => _currentHP;

    /// <summary>True when the player is alive.</summary>
    public bool IsAlive => _currentHP > 0f;

    /// <summary>Normalized health (0–1) for HUD display.</summary>
    public float HealthPercent => MaxHP > 0f ? Mathf.Clamp01(_currentHP / MaxHP) : 0f;

    // ── Internals ───────────────────────────────────────────────────
    private bool _isDead;

    // =================================================================
    //  Lifecycle
    // =================================================================

    private void Awake()
    {
        _currentHP = MaxHP;
    }

    // =================================================================
    //  Public API
    // =================================================================

    /// <summary>
    /// Applies damage to the player. Clamps HP to zero and triggers
    /// death if HP reaches zero.
    /// </summary>
    /// <param name="amount">Positive damage amount.</param>
    public void TakeDamage(float amount)
    {
        if (_isDead || amount <= 0f) return;

        _currentHP = Mathf.Max(0f, _currentHP - amount);
        OnDamaged?.Invoke(_currentHP, MaxHP);

        Debug.Log($"[PlayerHealth] Took {amount:F1} damage. HP: {_currentHP:F1}/{MaxHP}");

        if (_currentHP <= 0f)
        {
            Die();
        }
    }

    /// <summary>
    /// Restores HP by the specified amount, clamped to MaxHP.
    /// </summary>
    public void Heal(float amount)
    {
        if (_isDead || amount <= 0f) return;

        _currentHP = Mathf.Min(MaxHP, _currentHP + amount);
        OnDamaged?.Invoke(_currentHP, MaxHP);
    }

    /// <summary>
    /// Fully restores HP to MaxHP.
    /// </summary>
    public void FullHeal()
    {
        _currentHP = MaxHP;
        OnDamaged?.Invoke(_currentHP, MaxHP);
    }

    // =================================================================
    //  Death & Respawn
    // =================================================================

    /// <summary>
    /// Handles player death: disables movement, camera input, and weapon.
    /// Starts the respawn timer.
    /// </summary>
    private void Die()
    {
        if (_isDead) return;
        _isDead = true;

        Debug.Log("[PlayerHealth] Player died!");
        OnDied?.Invoke();

        // Disable gameplay systems
        if (Character != null && Character.Motor != null)
            Character.Motor.enabled = false;

        if (Launcher != null)
            Launcher.enabled = false;

        if (Visuals != null)
            Visuals.SetVisible(false);

        // Start respawn timer
        Invoke(nameof(Respawn), RespawnDelay);
    }

    /// <summary>
    /// Respawns the player on the terrain surface, restores HP,
    /// and re-enables all gameplay systems.
    /// </summary>
    private void Respawn()
    {
        _isDead = false;
        _currentHP = MaxHP;

        // Find a spawn position on the terrain surface
        Vector3 spawnPos = FindSpawnPosition();

        // Teleport the character
        if (Character != null && Character.Motor != null)
        {
            Character.Motor.enabled = true;
            Character.Motor.SetPositionAndRotation(spawnPos, Quaternion.identity);
        }

        // Re-enable weapon
        if (Launcher != null)
            Launcher.enabled = true;

        // Re-show mesh
        if (Visuals != null)
            Visuals.SetVisible(!Visuals.HideForLocalPlayer);

        Debug.Log($"[PlayerHealth] Respawned at {spawnPos}");
        OnRespawned?.Invoke(_currentHP, MaxHP);
    }

    /// <summary>
    /// Finds a suitable respawn position by raycasting down from high
    /// altitude at the current XZ position. Falls back to a default
    /// height if no surface is found.
    /// </summary>
    private Vector3 FindSpawnPosition()
    {
        // Use current XZ or a random offset for variety
        Vector3 currentPos = transform.position;
        Vector3 rayOrigin = new Vector3(currentPos.x, RespawnRaycastY, currentPos.z);

        if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, RespawnRaycastY * 2f))
        {
            return hit.point + Vector3.up * RespawnHeightOffset;
        }

        // Fallback
        float surfaceY = TerrainManager != null ? TerrainManager.surfaceY + 5f : 20f;
        return new Vector3(currentPos.x, surfaceY, currentPos.z);
    }
}
