using UnityEngine;

/// <summary>
/// Available terrain tool modes.
/// </summary>
public enum ToolMode
{
    /// <summary>Remove material (positive density delta).</summary>
    Dig,
    /// <summary>Add material (negative density delta).</summary>
    Build
}

/// <summary>
/// First-person terrain editing tool. Raycasts from the camera center
/// and applies spherical density edits via
/// <see cref="TerrainManager.EditTerrain"/> while the attack input is held.
///
/// Input is fed each frame by <see cref="PlayerInputManager"/> through
/// <see cref="SetAttackInput"/> and <see cref="CycleMode"/>.
///
/// Displays a semi-transparent indicator sphere at the hit point to
/// preview the edit radius. Colour changes with the active
/// <see cref="ToolMode"/>.
/// </summary>
public class TerrainTool : MonoBehaviour
{
    // ── References ───────────────────────────────────────────────────
    [Header("References")]
    [Tooltip("Terrain manager for edit calls. Wired at runtime by PlayerSpawnManager.")]
    public TerrainManager terrainManager;

    [Tooltip("Camera transform used for the center-screen raycast.")]
    public Transform cameraTransform;

    // ── Edit settings ────────────────────────────────────────────────
    [Header("Edit Settings")]
    [Tooltip("World-space radius of the edit sphere.")]
    public float editRadius = 2.5f;

    [Tooltip("Edit strength per second (scaled by deltaTime).")]
    public float editPower = 15f;

    [Tooltip("Maximum raycast distance from camera.")]
    public float maxRayDistance = 100f;

    // ── Indicator visuals ────────────────────────────────────────────
    [Header("Indicator")]
    public Color indicatorColorDig   = new Color(1f, 0.3f, 0.3f, 0.25f);
    public Color indicatorColorBuild = new Color(0.3f, 0.6f, 1f, 0.25f);

    // ── Public state (read by TerrainToolHUD) ────────────────────────
    /// <summary>Current tool mode.</summary>
    public ToolMode CurrentMode { get; private set; } = ToolMode.Dig;

    /// <summary>True when the center-screen ray hits terrain.</summary>
    public bool HasHit { get; private set; }

    /// <summary>World-space hit point on the terrain surface.</summary>
    public Vector3 HitPoint { get; private set; }

    /// <summary>Surface normal at the hit point.</summary>
    public Vector3 HitNormal { get; private set; }

    // ── Input state (set by PlayerInputManager) ─────────────────────
    private bool _attackHeld;

    // ── Indicator internals ─────────────────────────────────────────
    private GameObject   _indicatorGO;
    private MeshRenderer _indicatorRenderer;
    private Material     _indicatorMaterial;

    // =================================================================
    //  Lifecycle
    // =================================================================

    private void Awake()
    {
        CreateIndicator();
    }

    private void OnDisable()
    {
        if (_indicatorGO != null)
            _indicatorGO.SetActive(false);
    }

    private void OnDestroy()
    {
        if (_indicatorMaterial != null) Destroy(_indicatorMaterial);
        if (_indicatorGO != null) Destroy(_indicatorGO);
    }

    private void Update()
    {
        UpdateRaycast();

        if (_attackHeld && HasHit && terrainManager != null)
        {
            float delta = CurrentMode == ToolMode.Dig ? editPower : -editPower;
            float scaledDelta = delta * Time.deltaTime;

            // Route through NetworkTerrainSync for multiplayer
            if (NetworkTerrainSync.Instance != null)
            {
                NetworkTerrainSync.Instance.RequestTerrainEditRpc(HitPoint, editRadius, scaledDelta);
            }
            else
            {
                terrainManager.EditTerrain(HitPoint, editRadius, scaledDelta);
            }
        }

        UpdateIndicator();
    }

    // =================================================================
    //  Public Input API
    // =================================================================

    /// <summary>
    /// Called by <see cref="PlayerInputManager"/> each frame with the
    /// current state of the Attack action.
    /// </summary>
    public void SetAttackInput(bool held)
    {
        _attackHeld = held;
    }

    /// <summary>
    /// Cycles the tool mode forward (+1) or backward (−1).
    /// Currently toggles between <see cref="ToolMode.Dig"/> and
    /// <see cref="ToolMode.Build"/>.
    /// </summary>
    public void CycleMode(int direction)
    {
        int count = System.Enum.GetValues(typeof(ToolMode)).Length;
        int next  = ((int)CurrentMode + direction % count + count) % count;
        CurrentMode = (ToolMode)next;
        UpdateIndicatorColor();
    }

    // =================================================================
    //  Raycast
    // =================================================================

    /// <summary>
    /// Fires a ray from the camera center and updates hit state.
    /// </summary>
    private void UpdateRaycast()
    {
        if (cameraTransform == null)
        {
            HasHit = false;
            return;
        }

        Ray ray = new Ray(cameraTransform.position, cameraTransform.forward);
        if (Physics.Raycast(ray, out RaycastHit hit, maxRayDistance))
        {
            HasHit    = true;
            HitPoint  = hit.point;
            HitNormal = hit.normal;
        }
        else
        {
            HasHit = false;
        }
    }

    // =================================================================
    //  Indicator sphere
    // =================================================================

    /// <summary>
    /// Creates a semi-transparent sphere primitive at runtime to
    /// visualise the edit radius at the hit point.
    /// </summary>
    private void CreateIndicator()
    {
        _indicatorGO = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        _indicatorGO.name = "ToolIndicator";

        // Remove the collider immediately — this is purely visual and
        // must never interfere with the tool raycast.
        Collider col = _indicatorGO.GetComponent<Collider>();
        if (col != null) DestroyImmediate(col);

        _indicatorRenderer = _indicatorGO.GetComponent<MeshRenderer>();
        _indicatorRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _indicatorRenderer.receiveShadows    = false;

        // ── Create transparent URP material ──────────────────────────
        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null)
            shader = Shader.Find("Sprites/Default");

        _indicatorMaterial = new Material(shader);

        // URP transparency keywords / properties
        _indicatorMaterial.SetFloat("_Surface", 1f);          // 1 = Transparent
        _indicatorMaterial.SetFloat("_Blend",   0f);          // 0 = Alpha
        _indicatorMaterial.SetInt("_SrcBlend",
            (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        _indicatorMaterial.SetInt("_DstBlend",
            (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        _indicatorMaterial.SetInt("_ZWrite", 0);
        _indicatorMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        _indicatorMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");

        _indicatorRenderer.sharedMaterial = _indicatorMaterial;
        UpdateIndicatorColor();

        _indicatorGO.SetActive(false);
    }

    /// <summary>
    /// Moves and scales the indicator sphere to the current hit point,
    /// or hides it when no hit is active.
    /// </summary>
    private void UpdateIndicator()
    {
        if (_indicatorGO == null) return;

        if (!HasHit)
        {
            if (_indicatorGO.activeSelf)
                _indicatorGO.SetActive(false);
            return;
        }

        if (!_indicatorGO.activeSelf)
            _indicatorGO.SetActive(true);

        _indicatorGO.transform.position = HitPoint;
        float diameter = editRadius * 2f;
        _indicatorGO.transform.localScale = new Vector3(diameter, diameter, diameter);
    }

    /// <summary>
    /// Syncs the indicator material colour to the active tool mode.
    /// </summary>
    private void UpdateIndicatorColor()
    {
        if (_indicatorMaterial == null) return;

        Color c = CurrentMode == ToolMode.Dig ? indicatorColorDig : indicatorColorBuild;
        _indicatorMaterial.SetColor("_BaseColor", c);
    }
}
