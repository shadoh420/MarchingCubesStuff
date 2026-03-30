using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Runtime-created Canvas HUD for combat mode.
///
/// Creates an overlay canvas with:
///   - A center-screen crosshair (four thin arms with a gap + center dot).
///   - A cooldown indicator that dims the crosshair when the weapon is
///     recharging.
///   - Prepared for Phase 11: health bar and kill feed slots.
///
/// Reads state from <see cref="ProjectileLauncher"/> for cooldown display.
/// The reference is wired at runtime by <see cref="PlayerSpawnManager"/>.
/// </summary>
public class CombatHUD : MonoBehaviour
{
    // ── References ───────────────────────────────────────────────────
    [Header("References")]
    [Tooltip("The projectile launcher whose state drives cooldown display.")]
    public ProjectileLauncher launcher;

    // ── Crosshair tuning ─────────────────────────────────────────────
    [Header("Crosshair")]
    [Tooltip("Length of each crosshair arm in pixels.")]
    public float armLength = 12f;

    [Tooltip("Thickness of each crosshair arm in pixels.")]
    public float armThickness = 2f;

    [Tooltip("Gap between the center and each arm in pixels.")]
    public float gap = 5f;

    [Tooltip("Crosshair colour when ready to fire.")]
    public Color crosshairReady = new Color(1f, 1f, 1f, 0.9f);

    [Tooltip("Crosshair colour during cooldown.")]
    public Color crosshairCooldown = new Color(1f, 0.4f, 0.2f, 0.4f);

    // ── Internals ────────────────────────────────────────────────────
    private Canvas  _canvas;
    private Image   _centerDot;
    private Image[] _arms = new Image[4];

    // =================================================================
    //  Lifecycle
    // =================================================================

    private void Start()
    {
        CreateCanvas();
        CreateCrosshair();
    }

    private void LateUpdate()
    {
        UpdateCrosshairColor();
    }

    // =================================================================
    //  Canvas construction
    // =================================================================

    /// <summary>
    /// Creates a screen-space overlay canvas at a high sort order
    /// so HUD elements draw on top of everything.
    /// </summary>
    private void CreateCanvas()
    {
        GameObject go = new GameObject("CombatHUDCanvas");
        go.transform.SetParent(transform, false);

        _canvas = go.AddComponent<Canvas>();
        _canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 110; // Above TerrainToolHUD (100)

        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);

        go.AddComponent<GraphicRaycaster>();
    }

    /// <summary>
    /// Builds four thin arms around screen center with a configurable gap,
    /// plus a small center dot. Same pattern as TerrainToolHUD but
    /// independent so both can coexist.
    /// </summary>
    private void CreateCrosshair()
    {
        RectTransform parent = _canvas.GetComponent<RectTransform>();

        // Center dot
        _centerDot = CreateImage("CombatCenterDot", parent);
        SetRect(_centerDot.rectTransform, Vector2.zero, new Vector2(3f, 3f));
        _centerDot.color = crosshairReady;

        // Arm directions: up, down, left, right
        Vector2[] directions = { Vector2.up, Vector2.down, Vector2.left, Vector2.right };
        Vector2[] sizes =
        {
            new Vector2(armThickness, armLength),   // up
            new Vector2(armThickness, armLength),   // down
            new Vector2(armLength, armThickness),   // left
            new Vector2(armLength, armThickness)    // right
        };

        for (int i = 0; i < 4; i++)
        {
            _arms[i] = CreateImage($"CombatArm_{i}", parent);
            float offset = gap + armLength * 0.5f;
            Vector2 pos  = directions[i] * offset;
            SetRect(_arms[i].rectTransform, pos, sizes[i]);
            _arms[i].color = crosshairReady;
        }
    }

    // =================================================================
    //  Crosshair updates
    // =================================================================

    /// <summary>
    /// Lerps crosshair colour between ready and cooldown states based on
    /// the launcher's cooldown progress.
    /// </summary>
    private void UpdateCrosshairColor()
    {
        if (launcher == null) return;

        float t = launcher.CooldownProgress; // 0 = ready, 1 = just fired
        Color c = Color.Lerp(crosshairReady, crosshairCooldown, t);

        if (_centerDot != null) _centerDot.color = c;
        for (int i = 0; i < _arms.Length; i++)
        {
            if (_arms[i] != null) _arms[i].color = c;
        }
    }

    // =================================================================
    //  UI helpers
    // =================================================================

    private Image CreateImage(string name, RectTransform parent)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        Image img = go.AddComponent<Image>();
        img.raycastTarget = false;
        return img;
    }

    private static void SetRect(RectTransform rt, Vector2 anchoredPos, Vector2 size)
    {
        rt.anchorMin        = new Vector2(0.5f, 0.5f);
        rt.anchorMax        = new Vector2(0.5f, 0.5f);
        rt.pivot            = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta        = size;
    }
}
