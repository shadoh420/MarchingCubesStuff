using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Runtime-created Canvas HUD for the terrain tool.
///
/// Creates an overlay canvas with:
///   - A center-screen crosshair (four thin arms with a gap + center dot).
///   - A mode label (bottom-center) showing the active <see cref="ToolMode"/>.
///
/// Requires a <see cref="TerrainTool"/> reference to read the active mode.
/// The reference is wired at runtime by <see cref="PlayerSpawnManager"/>.
/// </summary>
public class TerrainToolHUD : MonoBehaviour
{
    // ── References ───────────────────────────────────────────────────
    [Header("References")]
    [Tooltip("The terrain tool whose state drives this HUD.")]
    public TerrainTool tool;

    // ── Crosshair tuning ─────────────────────────────────────────────
    [Header("Crosshair")]
    [Tooltip("Length of each crosshair arm in pixels.")]
    public float armLength = 10f;

    [Tooltip("Thickness of each crosshair arm in pixels.")]
    public float armThickness = 2f;

    [Tooltip("Gap between the center and each arm in pixels.")]
    public float gap = 4f;

    [Tooltip("Crosshair colour.")]
    public Color crosshairColor = new Color(1f, 1f, 1f, 0.85f);

    // ── Mode label tuning ────────────────────────────────────────────
    [Header("Mode Label")]
    public int   fontSize   = 16;
    public Color digColor   = new Color(1f, 0.45f, 0.45f, 1f);
    public Color buildColor = new Color(0.45f, 0.7f, 1f, 1f);

    // ── Internals ────────────────────────────────────────────────────
    private Canvas   _canvas;
    private Text     _modeText;
    private Image    _centerDot;
    private Image[]  _arms = new Image[4];
    private ToolMode _lastMode = (ToolMode)(-1);

    // =================================================================
    //  Lifecycle
    // =================================================================

    private void Start()
    {
        CreateCanvas();
        CreateCrosshair();
        CreateModeLabel();
        RefreshModeLabel();
    }

    private void LateUpdate()
    {
        if (tool == null) return;

        if (tool.CurrentMode != _lastMode)
        {
            _lastMode = tool.CurrentMode;
            RefreshModeLabel();
        }
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
        GameObject go = new GameObject("TerrainToolCanvas");
        go.transform.SetParent(transform, false);

        _canvas = go.AddComponent<Canvas>();
        _canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 100;

        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);

        go.AddComponent<GraphicRaycaster>();
    }

    /// <summary>
    /// Builds four thin arms around screen center with a configurable gap,
    /// plus a small center dot.
    /// </summary>
    private void CreateCrosshair()
    {
        RectTransform parent = _canvas.GetComponent<RectTransform>();

        // Center dot
        _centerDot = CreateImage("CenterDot", parent);
        SetRect(_centerDot.rectTransform, Vector2.zero, new Vector2(2f, 2f));
        _centerDot.color = crosshairColor;

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
            _arms[i] = CreateImage($"CrosshairArm_{i}", parent);
            float offset = gap + armLength * 0.5f;
            Vector2 pos  = directions[i] * offset;
            SetRect(_arms[i].rectTransform, pos, sizes[i]);
            _arms[i].color = crosshairColor;
        }
    }

    /// <summary>
    /// Creates a Text element at the bottom-center of the screen showing
    /// the current tool mode name.
    /// </summary>
    private void CreateModeLabel()
    {
        GameObject go = new GameObject("ModeLabel");
        go.transform.SetParent(_canvas.transform, false);

        _modeText = go.AddComponent<Text>();

        // Unity 6 built-in font
        _modeText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (_modeText.font == null)
            _modeText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");

        _modeText.fontSize       = fontSize;
        _modeText.fontStyle      = FontStyle.Bold;
        _modeText.alignment      = TextAnchor.MiddleCenter;
        _modeText.raycastTarget  = false;

        // Add a subtle shadow for readability
        Shadow shadow = go.AddComponent<Shadow>();
        shadow.effectColor    = new Color(0f, 0f, 0f, 0.6f);
        shadow.effectDistance = new Vector2(1f, -1f);

        RectTransform rt  = go.GetComponent<RectTransform>();
        rt.anchorMin        = new Vector2(0.5f, 0f);
        rt.anchorMax        = new Vector2(0.5f, 0f);
        rt.pivot            = new Vector2(0.5f, 0f);
        rt.anchoredPosition = new Vector2(0f, 40f);
        rt.sizeDelta        = new Vector2(200f, 40f);
    }

    /// <summary>
    /// Updates the mode label text and colour based on the current tool mode.
    /// </summary>
    private void RefreshModeLabel()
    {
        if (_modeText == null || tool == null) return;

        switch (tool.CurrentMode)
        {
            case ToolMode.Dig:
                _modeText.text  = "DIG";
                _modeText.color = digColor;
                break;
            case ToolMode.Build:
                _modeText.text  = "BUILD";
                _modeText.color = buildColor;
                break;
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
