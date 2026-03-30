using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Runtime-created Canvas HUD for player health.
///
/// Creates an overlay canvas with:
///   - A health bar (bottom-left) that fills/drains with HP changes.
///   - A full-screen damage flash (red vignette) on hit.
///   - A death overlay with "RESPAWNING..." text.
///
/// Subscribes to <see cref="PlayerHealth"/> events for updates.
/// Wired at runtime by <see cref="PlayerSpawnManager"/>.
/// </summary>
public class HealthHUD : MonoBehaviour
{
    // ── References ───────────────────────────────────────────────────
    [Header("References")]
    [Tooltip("The player health component to display.")]
    public PlayerHealth playerHealth;

    // ── Health bar tuning ────────────────────────────────────────────
    [Header("Health Bar")]
    public float barWidth    = 250f;
    public float barHeight   = 20f;
    public float barMarginX  = 30f;
    public float barMarginY  = 30f;
    public Color barBgColor  = new Color(0.15f, 0.15f, 0.15f, 0.7f);
    public Color barHPColor  = new Color(0.2f, 0.9f, 0.3f, 0.9f);
    public Color barLowColor = new Color(0.9f, 0.2f, 0.2f, 0.9f);

    [Tooltip("HP fraction below which the bar turns red.")]
    public float lowHealthThreshold = 0.3f;

    // ── Damage flash ────────────────────────────────────────────────
    [Header("Damage Flash")]
    public Color flashColor = new Color(0.8f, 0f, 0f, 0.3f);
    public float flashDuration = 0.25f;

    // ── Internals ────────────────────────────────────────────────────
    private Canvas    _canvas;
    private Image     _barBg;
    private Image     _barFill;
    private Text      _hpText;
    private Image     _damageFlash;
    private Text      _deathText;
    private float     _flashTimer;

    // =================================================================
    //  Lifecycle
    // =================================================================

    private bool _subscribed;

    private void Start()
    {
        CreateCanvas();
        CreateHealthBar();
        CreateDamageFlash();
        CreateDeathOverlay();

        // Try to subscribe immediately (may be null if wired late)
        TrySubscribe();
    }

    private void OnDestroy()
    {
        Unsubscribe();
    }

    private void Update()
    {
        // Deferred binding: PlayerSpawnManager wires playerHealth in a
        // coroutine that runs after our Start(). Keep trying until bound.
        if (!_subscribed && playerHealth != null)
        {
            TrySubscribe();
        }

        // Fade out damage flash
        if (_flashTimer > 0f)
        {
            _flashTimer -= Time.deltaTime;
            float alpha = Mathf.Clamp01(_flashTimer / flashDuration) * flashColor.a;
            if (_damageFlash != null)
            {
                Color c = flashColor;
                c.a = alpha;
                _damageFlash.color = c;
            }
        }
    }

    // =================================================================
    //  Subscription helpers
    // =================================================================

    private void TrySubscribe()
    {
        if (_subscribed || playerHealth == null) return;

        playerHealth.OnDamaged   += HandleDamaged;
        playerHealth.OnDied      += HandleDied;
        playerHealth.OnRespawned += HandleRespawned;
        RefreshBar(playerHealth.CurrentHP, playerHealth.MaxHP);
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed || playerHealth == null) return;

        playerHealth.OnDamaged   -= HandleDamaged;
        playerHealth.OnDied      -= HandleDied;
        playerHealth.OnRespawned -= HandleRespawned;
        _subscribed = false;
    }

    // =================================================================
    //  Event handlers
    // =================================================================

    private void HandleDamaged(float currentHP, float maxHP)
    {
        RefreshBar(currentHP, maxHP);

        // Trigger damage flash
        _flashTimer = flashDuration;
    }

    private void HandleDied()
    {
        if (_deathText != null) _deathText.gameObject.SetActive(true);
    }

    private void HandleRespawned(float currentHP, float maxHP)
    {
        RefreshBar(currentHP, maxHP);
        if (_deathText != null) _deathText.gameObject.SetActive(false);
    }

    // =================================================================
    //  Bar updates
    // =================================================================

    private void RefreshBar(float currentHP, float maxHP)
    {
        if (_barFill == null) return;

        float pct = maxHP > 0f ? Mathf.Clamp01(currentHP / maxHP) : 0f;
        _barFill.rectTransform.sizeDelta = new Vector2(barWidth * pct, barHeight);

        // Colour: green → red when low
        _barFill.color = pct <= lowHealthThreshold ? barLowColor : barHPColor;

        // Update text
        if (_hpText != null)
            _hpText.text = $"{Mathf.CeilToInt(currentHP)} / {Mathf.CeilToInt(maxHP)}";
    }

    // =================================================================
    //  UI construction
    // =================================================================

    private void CreateCanvas()
    {
        GameObject go = new GameObject("HealthHUDCanvas");
        go.transform.SetParent(transform, false);

        _canvas = go.AddComponent<Canvas>();
        _canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 120; // Above CombatHUD (110)

        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);

        go.AddComponent<GraphicRaycaster>();
    }

    private void CreateHealthBar()
    {
        RectTransform parent = _canvas.GetComponent<RectTransform>();

        // Background
        _barBg = CreateImage("HealthBarBg", parent);
        RectTransform bgRt = _barBg.rectTransform;
        bgRt.anchorMin        = new Vector2(0f, 0f);
        bgRt.anchorMax        = new Vector2(0f, 0f);
        bgRt.pivot            = new Vector2(0f, 0f);
        bgRt.anchoredPosition = new Vector2(barMarginX, barMarginY);
        bgRt.sizeDelta        = new Vector2(barWidth, barHeight);
        _barBg.color = barBgColor;

        // Fill bar (left-aligned, width changes with HP)
        _barFill = CreateImage("HealthBarFill", bgRt);
        RectTransform fillRt = _barFill.rectTransform;
        fillRt.anchorMin        = new Vector2(0f, 0f);
        fillRt.anchorMax        = new Vector2(0f, 0f);
        fillRt.pivot            = new Vector2(0f, 0f);
        fillRt.anchoredPosition = Vector2.zero;
        fillRt.sizeDelta        = new Vector2(barWidth, barHeight);
        _barFill.color = barHPColor;

        // HP text overlay
        GameObject textGO = new GameObject("HPText");
        textGO.transform.SetParent(bgRt, false);

        _hpText = textGO.AddComponent<Text>();
        _hpText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (_hpText.font == null)
            _hpText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");

        _hpText.fontSize      = 14;
        _hpText.fontStyle     = FontStyle.Bold;
        _hpText.alignment     = TextAnchor.MiddleCenter;
        _hpText.color         = Color.white;
        _hpText.raycastTarget = false;

        RectTransform textRt = textGO.GetComponent<RectTransform>();
        textRt.anchorMin = Vector2.zero;
        textRt.anchorMax = Vector2.one;
        textRt.offsetMin = Vector2.zero;
        textRt.offsetMax = Vector2.zero;

        // Shadow for readability
        Shadow shadow = textGO.AddComponent<Shadow>();
        shadow.effectColor    = new Color(0f, 0f, 0f, 0.7f);
        shadow.effectDistance = new Vector2(1f, -1f);
    }

    private void CreateDamageFlash()
    {
        RectTransform parent = _canvas.GetComponent<RectTransform>();

        _damageFlash = CreateImage("DamageFlash", parent);
        RectTransform rt = _damageFlash.rectTransform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        Color c = flashColor;
        c.a = 0f;
        _damageFlash.color = c;
        _damageFlash.raycastTarget = false;
    }

    private void CreateDeathOverlay()
    {
        RectTransform parent = _canvas.GetComponent<RectTransform>();

        GameObject go = new GameObject("DeathText");
        go.transform.SetParent(parent, false);

        _deathText = go.AddComponent<Text>();
        _deathText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (_deathText.font == null)
            _deathText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");

        _deathText.text         = "RESPAWNING...";
        _deathText.fontSize     = 36;
        _deathText.fontStyle    = FontStyle.Bold;
        _deathText.alignment    = TextAnchor.MiddleCenter;
        _deathText.color        = new Color(1f, 0.3f, 0.3f, 0.9f);
        _deathText.raycastTarget = false;

        RectTransform rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot     = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(0f, -60f);
        rt.sizeDelta = new Vector2(400f, 60f);

        Shadow shadow = go.AddComponent<Shadow>();
        shadow.effectColor    = new Color(0f, 0f, 0f, 0.8f);
        shadow.effectDistance = new Vector2(2f, -2f);

        go.SetActive(false); // Hidden until death
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
}
