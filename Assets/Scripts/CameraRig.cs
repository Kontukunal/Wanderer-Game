using Unity.Cinemachine;
using UnityEngine;

namespace Wanderer
{
    /// <summary>
    /// Runtime side of the third-person camera: locks the cursor so mouse movement becomes
    /// look input (as in any TPS), and applies sensitivity to the orbit axes.
    ///
    /// The orbiting itself is Cinemachine's — this only supplies the feel knobs and the
    /// cursor state, which Cinemachine deliberately doesn't touch.
    /// </summary>
    [RequireComponent(typeof(CinemachineInputAxisController))]
    [RequireComponent(typeof(CinemachineOrbitalFollow))]
    public class CameraRig : MonoBehaviour
    {
        [Header("Sensitivity")]
        [Tooltip("Degrees of yaw per unit of look input.")]
        [SerializeField] private float horizontalSensitivity = 220f;
        [Tooltip("Degrees of pitch per unit of look input. Usually lower than yaw.")]
        [SerializeField] private float verticalSensitivity = 130f;

        [Header("Cursor")]
        [SerializeField] private bool lockCursor = true;

        private CinemachineInputAxisController input;

        private void Awake()
        {
            input = GetComponent<CinemachineInputAxisController>();
            ApplySensitivity();
        }

        private void OnEnable()
        {
            if (lockCursor) SetCursor(true);
        }

        private void OnDisable() => SetCursor(false);

        private void Update()
        {
            // Escape releases the mouse so you can get back to the Editor; clicking re-grabs it.
            // This project is Input System-only, so the legacy UnityEngine.Input class throws.
            if (!lockCursor) return;

            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            var mouse = UnityEngine.InputSystem.Mouse.current;

            if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
                SetCursor(false);
            else if (mouse != null && Cursor.lockState != CursorLockMode.Locked
                     && mouse.leftButton.wasPressedThisFrame)
                SetCursor(true);
        }

        private void ApplySensitivity()
        {
            foreach (var c in input.Controllers)
            {
                if (!c.Enabled || c.Name.Contains("Scale")) continue;   // radial axis is the zoom; leave it off
                bool isPitch = c.Name.EndsWith("Y") || c.Name.Contains("Vertical");
                float sens = isPitch ? verticalSensitivity : horizontalSensitivity;
                // Preserve the sign — pitch is inverted at build time so up looks up.
                c.Input.Gain = Mathf.Sign(c.Input.Gain) * sens;
            }
        }

        private static void SetCursor(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        private void OnValidate()
        {
            if (Application.isPlaying && input != null) ApplySensitivity();
        }
    }
}
