using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Simple fly camera for development.
/// Attach to the Main Camera alongside TerrainEditor.
/// 
/// Controls:
///   WASD      — move horizontally
///   Space / E — ascend
///   Ctrl / Q  — descend
///   Shift     — boost speed
///   Right-click + drag — look around (frees cursor when released)
/// 
/// Left-click is deliberately left free for TerrainEditor digging.
/// </summary>
public class FlyCameraController : MonoBehaviour
{
    [Header("Movement")]
    public float moveSpeed   = 20f;
    public float boostFactor = 3f;

    [Header("Look")]
    public float lookSensitivity = 0.15f;

    // ── Internal state ───────────────────────────────────────────────
    private float pitch;
    private float yaw;

    private void Start()
    {
        Vector3 euler = transform.eulerAngles;
        yaw   = euler.y;
        pitch = euler.x;

        // Start with cursor visible and unlocked
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible   = true;
    }

    private void Update()
    {
        Keyboard kb    = Keyboard.current;
        Mouse    mouse = Mouse.current;
        if (kb == null || mouse == null) return;

        // ── Look (hold RMB to freelook) ──────────────────────────────
        if (mouse.rightButton.isPressed)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible   = false;

            Vector2 delta = mouse.delta.ReadValue();
            yaw   += delta.x * lookSensitivity;
            pitch -= delta.y * lookSensitivity;
            pitch  = Mathf.Clamp(pitch, -89f, 89f);

            transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
        }
        else
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible   = true;
        }

        // ── Movement ─────────────────────────────────────────────────
        Vector3 dir = Vector3.zero;

        if (kb.wKey.isPressed) dir += transform.forward;
        if (kb.sKey.isPressed) dir -= transform.forward;
        if (kb.dKey.isPressed) dir += transform.right;
        if (kb.aKey.isPressed) dir -= transform.right;

        if (kb.spaceKey.isPressed || kb.eKey.isPressed) dir += Vector3.up;
        if (kb.leftCtrlKey.isPressed || kb.qKey.isPressed) dir -= Vector3.up;

        float speed = moveSpeed;
        if (kb.leftShiftKey.isPressed) speed *= boostFactor;

        transform.position += dir.normalized * speed * Time.deltaTime;
    }
}
