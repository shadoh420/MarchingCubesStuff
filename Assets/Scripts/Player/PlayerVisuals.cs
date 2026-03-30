using UnityEngine;

/// <summary>
/// Attaches a visible capsule mesh to the player character.
///
/// The local player's mesh is hidden (first-person — you don't see your
/// own body), but it will be visible to other players in multiplayer
/// (Phase 12). Uses the same dimensions as the KCC motor capsule.
///
/// Place this component on the same GameObject as
/// <see cref="PlayerCharacterController"/>.
/// </summary>
public class PlayerVisuals : MonoBehaviour
{
    // ── Settings ─────────────────────────────────────────────────────
    [Header("Visuals")]
    [Tooltip("Colour of the player capsule.")]
    public Color PlayerColor = new Color(0.3f, 0.6f, 1f, 1f);

    [Tooltip("If true, the mesh is hidden for the local player (first-person). " +
             "Set to false if you want to see yourself for debugging.")]
    public bool HideForLocalPlayer = true;

    // ── References ───────────────────────────────────────────────────
    private GameObject _meshGO;
    private MeshRenderer _meshRenderer;
    private Material _material;

    // =================================================================
    //  Lifecycle
    // =================================================================

    private void Start()
    {
        CreateVisualMesh();

        if (HideForLocalPlayer)
        {
            // In single-player / local mode, hide the mesh.
            // Phase 12 (netcode) will set HideForLocalPlayer = false
            // for remote players so they're visible.
            SetVisible(false);
        }
    }

    private void OnDestroy()
    {
        if (_material != null) Destroy(_material);
        if (_meshGO != null) Destroy(_meshGO);
    }

    // =================================================================
    //  Public API
    // =================================================================

    /// <summary>
    /// Shows or hides the player mesh. Used by netcode to show remote
    /// players while keeping the local player invisible.
    /// </summary>
    public void SetVisible(bool visible)
    {
        if (_meshGO != null) _meshGO.SetActive(visible);
    }

    /// <summary>
    /// Changes the player capsule colour (e.g., for team colours).
    /// </summary>
    public void SetColor(Color color)
    {
        PlayerColor = color;
        if (_material != null)
        {
            _material.SetColor("_BaseColor", color);
        }
    }

    // =================================================================
    //  Mesh construction
    // =================================================================

    /// <summary>
    /// Creates a capsule primitive as a child of this transform, matching
    /// the character controller's standing capsule dimensions.
    /// </summary>
    private void CreateVisualMesh()
    {
        _meshGO = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        _meshGO.name = "PlayerMesh";
        _meshGO.transform.SetParent(transform, false);

        // Unity's capsule primitive is 2 units tall with 0.5 radius by default.
        // The KCC uses StandingCapsuleHeight=2, StandingCapsuleRadius=0.5,
        // StandingCapsuleYOffset=1. The primitive's pivot is at its center,
        // so we offset Y by the capsule Y offset (1 = half height).
        _meshGO.transform.localPosition = new Vector3(0f, 1f, 0f);
        _meshGO.transform.localScale = Vector3.one;

        // Remove the collider — the KCC has its own
        Collider col = _meshGO.GetComponent<Collider>();
        if (col != null) DestroyImmediate(col);

        // Apply material
        _meshRenderer = _meshGO.GetComponent<MeshRenderer>();
        _meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        _meshRenderer.receiveShadows = true;

        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");

        _material = new Material(shader);
        _material.SetColor("_BaseColor", PlayerColor);
        _meshRenderer.sharedMaterial = _material;
    }
}
