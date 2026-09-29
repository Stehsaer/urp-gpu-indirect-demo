using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Mouse driven orbit camera. The camera always looks at a fixed pivot and its
/// orientation is stored as euler <see cref="pitch"/> / <see cref="yaw"/>.
///
///   Left drag  : rotate the camera around the pivot (yaw + pitch).
///   Right drag : move the pivot on the plane through it that is parallel to the
///                near plane (i.e. along the camera's right / up axes).
///   Scroll     : scale the distance between the camera and the pivot.
///
/// The pivot / orientation can be seeded from the camera's current transform so
/// the view does not jump when the component is enabled at runtime.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Camera))]
[AddComponentMenu("Grass/Orbit Camera Controller")]
public class OrbitCameraController : MonoBehaviour
{
    [Header("Orbit center")]
    [Tooltip("Fixed point the camera orbits around (world space).")]
    [SerializeField] Vector3 pivot = Vector3.zero;

    [Header("Orbit angles")]
    [Tooltip("Rotation around the world Y axis, in degrees.")]
    [SerializeField] float yaw;
    [Tooltip("Rotation around the local X axis, in degrees. Clamped to avoid flipping.")]
    [SerializeField] float pitch = 20f;
    [SerializeField] float minPitch = -89f;
    [SerializeField] float maxPitch = 89f;

    [Header("Distance")]
    [Tooltip("Distance between the camera and the pivot.")]
    [SerializeField] float distance = 10f;
    [SerializeField] float minDistance = 0.5f;
    [SerializeField] float maxDistance = 500f;

    [Header("Input")]
    [Tooltip("Degrees of rotation per pixel of mouse movement.")]
    [SerializeField] float rotateSensitivity = 0.25f;
    [Tooltip("World units of pivot movement per pixel, per unit of distance.")]
    [SerializeField] float panSensitivity = 0.0015f;
    [Tooltip("Fraction of the distance removed per scroll notch.")]
    [SerializeField] float zoomSensitivity = 0.1f;

    [Header("Initialization")]
    [Tooltip("Seed pivot / yaw / pitch / distance from the camera's transform on play.")]
    [SerializeField] bool initializeFromTransform = true;

#if !ENABLE_INPUT_SYSTEM && ENABLE_LEGACY_INPUT_MANAGER
    Vector2 m_LastMousePosition;
#endif

    void Start()
    {
        if (initializeFromTransform)
            InitializeFromTransform();

        Apply();
    }

    void LateUpdate()
    {
        ReadInput();
        Apply();
    }

    void OnValidate()
    {
        pitch = Mathf.Clamp(pitch, minPitch, maxPitch);
        distance = Mathf.Clamp(distance, minDistance, maxDistance);

        if (Application.isPlaying && isActiveAndEnabled)
            Apply();
    }

    /// <summary>
    /// Derives the pivot, yaw, pitch and distance from the current transform so the
    /// camera keeps looking in the same direction with the same framing.
    /// </summary>
    public void InitializeFromTransform()
    {
        Vector3 forward = transform.forward;
        Vector3 euler = transform.eulerAngles;

        pitch = NormalizeAngle(euler.x);
        yaw = euler.y;
        pivot = transform.position + forward * distance;
    }

    void ReadInput()
    {
#if ENABLE_INPUT_SYSTEM
        var mouse = Mouse.current;
        if (mouse == null)
            return;

        Vector2 delta = mouse.delta.ReadValue();

        if (mouse.leftButton.isPressed)
            Rotate(delta);

        if (mouse.rightButton.isPressed)
            Pan(delta);

        float scroll = mouse.scroll.ReadValue().y;
        if (Mathf.Abs(scroll) > 0.0001f)
        {
            // Windows reports +/-120 per notch, other platforms +/-1.
            float notches = Mathf.Abs(scroll) > 1f ? scroll / 120f : scroll;
            Zoom(notches);
        }
#elif ENABLE_LEGACY_INPUT_MANAGER
        Vector2 mousePosition = Input.mousePosition;
        bool dragging = Input.GetMouseButton(0) || Input.GetMouseButton(1);
        Vector2 delta = dragging ? mousePosition - m_LastMousePosition : Vector2.zero;
        m_LastMousePosition = mousePosition;

        if (Input.GetMouseButton(0))
            Rotate(delta * 10f);

        if (Input.GetMouseButton(1))
            Pan(delta * 10f);

        if (Mathf.Abs(Input.mouseScrollDelta.y) > 0.0001f)
            Zoom(Input.mouseScrollDelta.y);
#endif
    }

    void Rotate(Vector2 delta)
    {
        yaw += delta.x * rotateSensitivity;
        pitch = Mathf.Clamp(pitch - delta.y * rotateSensitivity, minPitch, maxPitch);
    }

    void Pan(Vector2 delta)
    {
        Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
        Vector3 right = rotation * Vector3.right;
        Vector3 up = rotation * Vector3.up;

        // Move the pivot opposite to the cursor so the world follows the drag,
        // staying on the plane through the pivot parallel to the near plane.
        float scale = panSensitivity * distance;
        pivot -= (right * delta.x + up * delta.y) * scale;
    }

    void Zoom(float notches)
    {
        distance *= Mathf.Exp(-notches * zoomSensitivity);
        distance = Mathf.Clamp(distance, minDistance, maxDistance);
    }

    void Apply()
    {
        Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
        transform.rotation = rotation;
        transform.position = pivot - rotation * Vector3.forward * distance;
    }

    static float NormalizeAngle(float angle)
    {
        angle %= 360f;
        if (angle > 180f)
            angle -= 360f;
        return angle;
    }
}
