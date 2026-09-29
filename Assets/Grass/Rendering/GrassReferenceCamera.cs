using UnityEngine;

/// <summary>
/// Provides the camera used for grass culling / LOD selection. Grass is evaluated
/// relative to this camera instead of whichever camera is currently rendering
/// (game view, scene view, preview cameras, ...).
///
/// Renderer features live in assets and cannot reference scene objects, so this
/// component publishes its camera through a static registry. Runs in edit mode too
/// so culling also follows it in the scene view.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
public class GrassReferenceCamera : MonoBehaviour
{
    public static class Registry
    {
        static Camera s_ReferenceCamera;

        /// <summary>
        /// The assigned reference camera, or <see cref="Camera.main"/> when none is set.
        /// </summary>
        public static Camera referenceCamera
        {
            get => s_ReferenceCamera != null ? s_ReferenceCamera : Camera.main;
            set => s_ReferenceCamera = value;
        }
    }

    [Tooltip("Camera used for grass culling / LOD. Falls back to Camera.main when empty.")]
    public Camera referenceCamera;

    void OnEnable()
    {
        Registry.referenceCamera = referenceCamera;
    }

    void OnDisable()
    {
        // Only clear when this component currently owns the registry slot.
        if (Registry.referenceCamera == referenceCamera)
            Registry.referenceCamera = null;
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        if (isActiveAndEnabled)
            Registry.referenceCamera = referenceCamera;
    }
#endif
}
