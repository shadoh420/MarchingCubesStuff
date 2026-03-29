using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Attach to the Camera or Player object.
/// Raycasts from the mouse position into the terrain; on mouse-button hold,
/// applies a spherical density edit (dig with LMB, build with RMB).
/// Requires a <see cref="TerrainManager"/> reference to coordinate
/// multi-chunk edits and mesh regeneration.
/// </summary>
public class TerrainEditor : MonoBehaviour
{
    // ── References ───────────────────────────────────────────────────
    [Header("References")]
    [Tooltip("Drag in the TerrainManager from the scene.")]
    public TerrainManager terrainManager;

    // ── Edit tunables ────────────────────────────────────────────────
    [Header("Edit Settings")]
    [Tooltip("World-space radius of the edit sphere.")]
    public float editRadius = 2.5f;

    [Tooltip("Strength of the edit per second (scales with deltaTime).")]
    public float editPower = 15f;

    [Header("Raycast")]
    [Tooltip("Maximum ray distance for terrain hits.")]
    public float maxRayDistance = 200f;

    // =================================================================
    //  Update loop
    // =================================================================

    private void Update()
    {
        if (terrainManager == null) return;

        Mouse mouse = Mouse.current;
        if (mouse == null) return;

        // LMB = dig (subtract density → remove material)
        if (mouse.leftButton.isPressed)
        {
            TryEdit(editPower, mouse);
        }
        // RMB = build (add density → fill material)
        else if (mouse.rightButton.isPressed)
        {
            TryEdit(-editPower, mouse);
        }
    }

    // =================================================================
    //  Raycast → edit
    // =================================================================

    /// <summary>
    /// Casts a ray from the mouse position through the main camera.
    /// On hit, delegates to <see cref="TerrainManager.EditTerrain"/>.
    /// <paramref name="delta"/>: positive = dig, negative = build.
    /// </summary>
    private void TryEdit(float delta, Mouse mouse)
    {
        Camera cam = Camera.main;
        if (cam == null) return;

        Vector2 mousePos = mouse.position.ReadValue();
        Ray ray = cam.ScreenPointToRay(mousePos);

        if (Physics.Raycast(ray, out RaycastHit hit, maxRayDistance))
        {
            // Scale by deltaTime for framerate-independent editing
            float scaledDelta = delta * Time.deltaTime;
            terrainManager.EditTerrain(hit.point, editRadius, scaledDelta);
        }
    }
}
