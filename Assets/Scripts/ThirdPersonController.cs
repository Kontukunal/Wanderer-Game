using UnityEngine;

namespace Wanderer
{
    /// <summary>
    /// Camera-relative third-person locomotion on a CharacterController.
    /// Walk / jog / sprint, gravity, jump with coyote time, and slope handling.
    ///
    /// Speeds here are in metres/second and are the same numbers used as the
    /// Animator blend-tree thresholds. Keeping the two in one unit is what keeps
    /// the feet planted: the clip that plays always corresponds to the real ground speed.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    [RequireComponent(typeof(PlayerInputReader))]
    public class ThirdPersonController : MonoBehaviour
    {
        // The hero is deliberately small (~1.2m) so the landscape reads as vast. A short
        // character has a short stride, so the speeds are scaled to match — otherwise the
        // feet skate. These numbers are the 1.8m-human values x 0.667.
        [Header("Speeds (m/s — must match Animator blend thresholds)")]
        [SerializeField] private float walkSpeed = 1.2f;
        [SerializeField] private float jogSpeed = 2.8f;
        [SerializeField] private float sprintSpeed = 4.4f;

        [Tooltip("Stick magnitude below this counts as a deliberate slow walk.")]
        [SerializeField, Range(0.1f, 0.9f)] private float walkStickThreshold = 0.5f;

        [Header("Feel")]
        [Tooltip("Seconds to reach target speed. Small = snappy, large = floaty.")]
        [SerializeField] private float acceleration = 0.12f;
        [SerializeField] private float deceleration = 0.10f;
        [Tooltip("Seconds to turn to face the movement direction.")]
        [SerializeField] private float turnSmoothTime = 0.10f;

        [Header("Jump & Gravity")]
        [SerializeField] private float jumpHeight = 0.95f;   // scaled to the smaller hero
        [SerializeField] private float gravity = -22f;
        [SerializeField] private float terminalVelocity = -40f;
        [Tooltip("Grace period after walking off a ledge where a jump still works.")]
        [SerializeField] private float coyoteTime = 0.12f;

        [Header("Ground Check")]
        [SerializeField] private LayerMask groundLayers = ~0;
        [SerializeField] private float groundCheckRadius = 0.19f;
        [Tooltip("How far below the capsule's base to probe. Small negative = slightly inside.")]
        [SerializeField] private float groundCheckOffset = 0.12f;

        [Header("Slopes")]
        [Tooltip("Above this angle the character slides instead of standing.")]
        [SerializeField] private float slideAngle = 46f;
        [SerializeField] private float slideSpeed = 6f;

        [Header("References")]
        [SerializeField] private Transform cameraTransform;

        private CharacterController controller;
        private PlayerInputReader input;
        private CharacterAnimator characterAnimator;

        private float currentSpeed;      // smoothed planar speed, m/s
        private float speedDampVelocity;
        private float turnDampVelocity;
        private float verticalVelocity;
        private float lastGroundedTime = float.NegativeInfinity;

        private Vector3 groundNormal = Vector3.up;
        private bool isGrounded;
        private bool isSliding;

        /// <summary>Planar speed in m/s. The Animator reads this.</summary>
        public float PlanarSpeed => currentSpeed;
        public bool IsGrounded => isGrounded;
        public float VerticalVelocity => verticalVelocity;

        private void Awake()
        {
            controller = GetComponent<CharacterController>();
            input = GetComponent<PlayerInputReader>();
            characterAnimator = GetComponentInChildren<CharacterAnimator>();

            if (cameraTransform == null && Camera.main != null)
                cameraTransform = Camera.main.transform;
        }

        private void Update()
        {
            ProbeGround();
            ApplyGravity();
            Move();

            if (characterAnimator != null)
                characterAnimator.Tick(currentSpeed, isGrounded, verticalVelocity);
        }

        /// <summary>
        /// Sphere-probe below the capsule. CharacterController.isGrounded alone is unreliable
        /// on slopes and at the crest of terrain, so we take either signal as grounded and
        /// keep the surface normal for slope work.
        /// </summary>
        private void ProbeGround()
        {
            Vector3 origin = transform.position + Vector3.up * (groundCheckRadius - groundCheckOffset);
            bool hitGround = Physics.SphereCast(
                origin, groundCheckRadius, Vector3.down, out RaycastHit hit,
                groundCheckOffset + 0.05f, groundLayers, QueryTriggerInteraction.Ignore);

            groundNormal = hitGround ? hit.normal : Vector3.up;
            isGrounded = controller.isGrounded || hitGround;

            float angle = Vector3.Angle(groundNormal, Vector3.up);
            isSliding = isGrounded && angle > slideAngle;

            if (isGrounded && !isSliding)
                lastGroundedTime = Time.time;
        }

        private void ApplyGravity()
        {
            // Hold the capsule against the ground so it doesn't hop down slopes or
            // flicker between grounded/airborne on terrain seams.
            if (isGrounded && verticalVelocity < 0f && !isSliding)
                verticalVelocity = -2f;

            bool canJump = Time.time - lastGroundedTime <= coyoteTime;
            if (input.JumpQueued && canJump && !isSliding)
            {
                // v = sqrt(2gh) — reach exactly jumpHeight at the apex.
                verticalVelocity = Mathf.Sqrt(-2f * gravity * jumpHeight);
                lastGroundedTime = float.NegativeInfinity;
                input.ConsumeJump();
                characterAnimator?.TriggerJump();
            }

            verticalVelocity += gravity * Time.deltaTime;
            verticalVelocity = Mathf.Max(verticalVelocity, terminalVelocity);
        }

        private void Move()
        {
            Vector2 stick = input.Move;
            float stickMag = Mathf.Clamp01(stick.magnitude);

            // Flatten the camera basis so looking up/down never changes ground direction.
            Vector3 forward = cameraTransform != null
                ? Vector3.ProjectOnPlane(cameraTransform.forward, Vector3.up).normalized
                : Vector3.forward;
            Vector3 right = cameraTransform != null
                ? Vector3.ProjectOnPlane(cameraTransform.right, Vector3.up).normalized
                : Vector3.right;

            Vector3 desiredDir = (forward * stick.y + right * stick.x);
            if (desiredDir.sqrMagnitude > 1f) desiredDir.Normalize();

            float targetSpeed = ResolveTargetSpeed(stickMag);

            float smooth = targetSpeed > currentSpeed ? acceleration : deceleration;
            currentSpeed = Mathf.SmoothDamp(currentSpeed, targetSpeed, ref speedDampVelocity, smooth);
            if (currentSpeed < 0.02f) currentSpeed = 0f;

            if (desiredDir.sqrMagnitude > 0.0001f)
            {
                float targetYaw = Mathf.Atan2(desiredDir.x, desiredDir.z) * Mathf.Rad2Deg;
                float yaw = Mathf.SmoothDampAngle(
                    transform.eulerAngles.y, targetYaw, ref turnDampVelocity, turnSmoothTime);
                transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            }

            Vector3 planar = transform.forward * currentSpeed;

            // On walkable ground, follow the surface instead of driving into it —
            // otherwise the controller stutters going uphill and skips going down.
            if (isGrounded && !isSliding && verticalVelocity <= 0f)
                planar = Vector3.ProjectOnPlane(planar, groundNormal);

            Vector3 velocity = planar + Vector3.up * verticalVelocity;

            // Too steep to stand: add downhill acceleration along the slope face.
            if (isSliding)
            {
                Vector3 downhill = Vector3.ProjectOnPlane(Vector3.down, groundNormal).normalized;
                velocity += downhill * slideSpeed;
            }

            controller.Move(velocity * Time.deltaTime);
        }

        private float ResolveTargetSpeed(float stickMag)
        {
            if (stickMag < 0.01f) return 0f;

            if (input.SprintHeld) return sprintSpeed;

            // Analog: a gently-held stick walks, a fully-pushed stick jogs.
            if (stickMag <= walkStickThreshold)
                return Mathf.Lerp(0f, walkSpeed, stickMag / walkStickThreshold);

            float t = Mathf.InverseLerp(walkStickThreshold, 1f, stickMag);
            return Mathf.Lerp(walkSpeed, jogSpeed, t);
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Application.isPlaying && isGrounded
                ? new Color(0.3f, 1f, 0.4f, 0.5f)
                : new Color(1f, 0.35f, 0.3f, 0.5f);
            Gizmos.DrawSphere(
                transform.position + Vector3.up * (groundCheckRadius - groundCheckOffset),
                groundCheckRadius);
        }
    }
}
