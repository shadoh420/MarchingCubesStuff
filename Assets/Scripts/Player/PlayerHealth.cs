using Unity.Netcode;
using UnityEngine;
using System;

/// <summary>
/// Server-authoritative player health component.
///
/// HP is stored as a <see cref="NetworkVariable{T}"/> (server-write, everyone-read).
/// Damage is applied server-side only. Death and respawn are server-driven
/// with <c>ClientRpc</c> broadcasts for visual/audio effects.
///
/// Local C# events (<see cref="OnDamaged"/>, <see cref="OnDied"/>,
/// <see cref="OnRespawned"/>) still fire on every client for HUD updates,
/// driven by the NetworkVariable's <c>OnValueChanged</c> callback.
/// </summary>
public class PlayerHealth : NetworkBehaviour
{
    // ── Health settings ─────────────────────────────────────────────
    [Header("Health")]
    [Tooltip("Maximum hit points.")]
    public float MaxHP = 100f;

    // ── Respawn settings ────────────────────────────────────────────
    [Header("Respawn")]
    [Tooltip("Seconds after death before respawning.")]
    public float RespawnDelay = 3f;

    [Tooltip("Height from which the surface raycast is cast downward on respawn.")]
    public float RespawnRaycastY = 200f;

    [Tooltip("Extra height above the surface to place the respawned player.")]
    public float RespawnHeightOffset = 2f;

    // ── References (wired by NetworkPlayerSetup) ─────────────────────
    [Header("References")]
    public PlayerCharacterController Character;
    public FirstPersonCamera Camera;
    public TerrainManager TerrainManager;
    public ProjectileLauncher Launcher;
    public PlayerVisuals Visuals;

    // ── Networked HP ────────────────────────────────────────────────
    private NetworkVariable<float> _networkHP = new NetworkVariable<float>(
        100f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    // ── Events (fire on ALL clients for HUD updates) ────────────────
    /// <summary>Fired when HP changes. Args: current HP, max HP.</summary>
    public event Action<float, float> OnDamaged;

    /// <summary>Fired on death.</summary>
    public event Action OnDied;

    /// <summary>Fired on respawn. Args: current HP, max HP.</summary>
    public event Action<float, float> OnRespawned;

    // ── Public state ────────────────────────────────────────────────
    /// <summary>Current HP (from NetworkVariable).</summary>
    public float CurrentHP => _networkHP.Value;

    /// <summary>True when the player is alive.</summary>
    public bool IsAlive => _networkHP.Value > 0f;

    /// <summary>Normalized health (0–1) for HUD display.</summary>
    public float HealthPercent => MaxHP > 0f ? Mathf.Clamp01(_networkHP.Value / MaxHP) : 0f;

    // ── Internals ───────────────────────────────────────────────────
    private bool _isDead;

    // =================================================================
    //  Network Lifecycle
    // =================================================================

    public override void OnNetworkSpawn()
    {
        _networkHP.OnValueChanged += OnHPChanged;

        if (IsServer)
        {
            _networkHP.Value = MaxHP;
        }

        // Initial HUD refresh
        OnDamaged?.Invoke(_networkHP.Value, MaxHP);
    }

    public override void OnNetworkDespawn()
    {
        _networkHP.OnValueChanged -= OnHPChanged;
    }

    // =================================================================
    //  NetworkVariable callback (fires on ALL clients)
    // =================================================================

    private void OnHPChanged(float oldVal, float newVal)
    {
        OnDamaged?.Invoke(newVal, MaxHP);

        if (newVal <= 0f && oldVal > 0f)
        {
            OnDied?.Invoke();
        }
    }

    // =================================================================
    //  Public API — Server-only
    // =================================================================

    /// <summary>
    /// Applies damage. SERVER-ONLY — called directly by Fireball collision
    /// on the server. Clients cannot call this.
    /// </summary>
    public void TakeDamage(float amount)
    {
        if (!IsServer || _isDead || amount <= 0f) return;

        _networkHP.Value = Mathf.Max(0f, _networkHP.Value - amount);

        Debug.Log($"[PlayerHealth] Player {OwnerClientId} took {amount:F1} damage. " +
                  $"HP: {_networkHP.Value:F1}/{MaxHP}");

        if (_networkHP.Value <= 0f)
        {
            Die();
        }
    }

    /// <summary>
    /// Restores HP. SERVER-ONLY.
    /// </summary>
    public void Heal(float amount)
    {
        if (!IsServer || _isDead || amount <= 0f) return;
        _networkHP.Value = Mathf.Min(MaxHP, _networkHP.Value + amount);
    }

    /// <summary>Fully restores HP. SERVER-ONLY.</summary>
    public void FullHeal()
    {
        if (!IsServer) return;
        _networkHP.Value = MaxHP;
    }

    // =================================================================
    //  Death & Respawn — Server-authoritative
    // =================================================================

    private void Die()
    {
        if (!IsServer || _isDead) return;
        _isDead = true;

        Debug.Log($"[PlayerHealth] Player {OwnerClientId} died!");

        // Server: disable KCC motor
        if (Character != null && Character.Motor != null)
            Character.Motor.enabled = false;

        // All clients: visual/audio death effects
        DieClientRpc();

        // Schedule respawn on server
        Invoke(nameof(ServerRespawn), RespawnDelay);
    }

    [Rpc(SendTo.Everyone)]
    private void DieClientRpc()
    {
        // Called on ALL clients (including host)
        OnDied?.Invoke();

        if (Launcher != null)
            Launcher.enabled = false;

        if (Visuals != null)
            Visuals.SetVisible(false);
    }

    private void ServerRespawn()
    {
        if (!IsServer) return;

        _isDead = false;

        // Find a terrain surface position
        Vector3 spawnPos = FindSpawnPosition();

        // Server: re-enable KCC motor and teleport
        if (Character != null && Character.Motor != null)
        {
            Character.Motor.enabled = true;
            Character.Motor.SetPositionAndRotation(spawnPos, Quaternion.identity);
        }

        // Restore HP (triggers OnValueChanged → HUD update)
        _networkHP.Value = MaxHP;

        // All clients: re-enable systems
        RespawnClientRpc();
    }

    [Rpc(SendTo.Everyone)]
    private void RespawnClientRpc()
    {
        if (Launcher != null)
            Launcher.enabled = true;

        if (Visuals != null)
        {
            // Owner: stay hidden (first-person). Remote: show capsule.
            bool isLocalPlayer = IsOwner;
            Visuals.SetVisible(!isLocalPlayer);
        }

        OnRespawned?.Invoke(_networkHP.Value, MaxHP);
    }

    // =================================================================
    //  Manual respawn (requested by client via pause menu)
    // =================================================================

    /// <summary>
    /// Client requests a voluntary respawn (e.g. fell into a hole).
    /// Server teleports the player to a safe spawn position.
    /// </summary>
    [Rpc(SendTo.Server)]
    public void RequestRespawnServerRpc()
    {
        if (_isDead) return; // already dead, auto-respawn will handle it

        Vector3 spawnPos = FindSafeSpawnPosition();

        if (Character != null && Character.Motor != null)
            Character.Motor.SetPositionAndRotation(spawnPos, Quaternion.identity);

        Debug.Log($"[PlayerHealth] Player {OwnerClientId} manual respawn to {spawnPos}");
    }

    // =================================================================
    //  Helpers
    // =================================================================

    private Vector3 FindSpawnPosition()
    {
        return FindSafeSpawnPosition();
    }

    /// <summary>
    /// Tries to find valid ground. First tries the player's current XZ,
    /// then falls back to the world spawn point (0,0) if no ground is
    /// found (e.g. the player fell into a bottomless hole).
    /// </summary>
    private Vector3 FindSafeSpawnPosition()
    {
        // Try current XZ first
        Vector3 currentPos = transform.position;
        Vector3 rayOrigin = new Vector3(currentPos.x, RespawnRaycastY, currentPos.z);

        if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, RespawnRaycastY * 2f))
        {
            return hit.point + Vector3.up * RespawnHeightOffset;
        }

        // Current position has no ground — try the world spawn point
        var gameManager = FindFirstObjectByType<NetworkGameManager>();
        if (gameManager != null)
        {
            Vector3 spawnOrigin = new Vector3(
                gameManager.SpawnXZ.x, RespawnRaycastY, gameManager.SpawnXZ.y);

            if (Physics.Raycast(spawnOrigin, Vector3.down, out RaycastHit spawnHit, RespawnRaycastY * 2f))
            {
                return spawnHit.point + Vector3.up * RespawnHeightOffset;
            }
        }

        // Last resort fallback
        float surfaceY = TerrainManager != null ? TerrainManager.surfaceY + 5f : 20f;
        return new Vector3(0f, surfaceY, 0f);
    }
}
