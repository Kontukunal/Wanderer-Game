using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Wanderer
{
    /// <summary>
    /// The player's gun handling: walk up to the gun on the table, press E to pick it up
    /// (it parents to the right hand), then hold/click fire to shoot a hitscan ray. Hits on a
    /// <see cref="ShootingTarget"/> make it react; hits on anything else spawn a spark.
    ///
    /// Firing is a raycast from the camera through screen centre — the standard TPS model, so
    /// you shoot exactly where the camera looks, not where the barrel happens to point.
    /// The gun and arm animation follow; the ray is the source of truth.
    /// </summary>
    [RequireComponent(typeof(PlayerInputReader))]
    public class WeaponController : MonoBehaviour
    {
        [Header("Firing")]
        [SerializeField] private float range = 300f;
        [SerializeField] private LayerMask hitMask = ~0;
        [Tooltip("Seconds the muzzle flash + tracer stay visible.")]
        [SerializeField] private float flashTime = 0.04f;

        [Header("Pickup")]
        [Tooltip("How close the player must be to a gun to pick it up with Interact.")]
        [SerializeField] private float pickupRadius = 3.5f;

        [Header("Aim / Scope (hold right mouse or left trigger)")]
        [Tooltip("Camera field of view when scoped in. Lower = more zoom.")]
        [SerializeField] private float aimFieldOfView = 28f;
        [Tooltip("How fast the scope zooms in/out. Higher = snappier.")]
        [SerializeField] private float aimLerpSpeed = 12f;
        [Tooltip("Look sensitivity multiplier while scoped, for finer aim.")]
        [SerializeField, Range(0.1f, 1f)] private float aimSensitivityScale = 0.5f;

        [Header("References")]
        [SerializeField] private Transform cameraTransform;

        public bool HasGun { get; private set; }

        /// <summary>True while the player is holding the aim button (scoped in).</summary>
        public bool IsAiming { get; private set; }

        /// <summary>0 at the hip, 1 fully scoped — drives the HUD reticle + sensitivity blend.</summary>
        public float AimBlend { get; private set; }

        /// <summary>Look sensitivity multiplier the CameraRig should apply this frame (1 = normal).</summary>
        public float LookSensitivityScale => Mathf.Lerp(1f, aimSensitivityScale, AimBlend);

        /// <summary>True when an un-picked gun is within pickupRadius (drives the HUD prompt).</summary>
        public bool GunInRange { get; private set; }

        /// <summary>Total shots that have landed on the practice target.</summary>
        public int TargetHits { get; private set; }

        /// <summary>Time of the last confirmed hit on a sheep — drives the HUD hit-marker flash.</summary>
        public float LastSheepHitTime { get; private set; } = -99f;

        // Exposed for the HUD's debug readout.
        public float DistanceToGun { get; private set; } = -1f;
        public int ShotsFired { get; private set; }

        private int lastFireCount;
        private int lastInteractCount;

        private PlayerInputReader input;
        private Animator animator;
        private Transform handBone;
        private GunPickup equippedGun;
        private GunPickup nearestGun;
        private Transform muzzle;
        private LineRenderer tracer;
        private Light muzzleFlash;
        private float flashOffAt;

        // Scope state: we drive the live vcam's lens FOV and calm its handheld shake while aiming.
        private CinemachineCamera vcam;
        private CinemachineBasicMultiChannelPerlin vcamNoise;
        private float hipFieldOfView;
        private float baseNoiseAmplitude;

        private static readonly int AimingHash = Animator.StringToHash("Aiming");
        private static readonly int ShootHash = Animator.StringToHash("Shoot");

        private void Awake()
        {
            input = GetComponent<PlayerInputReader>();
            animator = GetComponentInChildren<Animator>();
            if (cameraTransform == null && Camera.main != null)
                cameraTransform = Camera.main.transform;

            if (animator != null)
                handBone = animator.GetBoneTransform(HumanBodyBones.RightHand);

            vcam = Object.FindFirstObjectByType<CinemachineCamera>();
            if (vcam != null)
            {
                hipFieldOfView = vcam.Lens.FieldOfView;
                vcamNoise = vcam.GetComponent<CinemachineBasicMultiChannelPerlin>();
                if (vcamNoise != null) baseNoiseAmplitude = vcamNoise.AmplitudeGain;
            }
        }

        private void Start()
        {
            // Ignore any presses that happened before the game was ready (e.g. the click that
            // grabbed the Game view's focus) so nothing auto-fires or auto-picks-up at spawn.
            lastFireCount = input.FirePressCount;
            lastInteractCount = input.InteractPressCount;
        }

        private void Update()
        {
            if (!HasGun)
            {
                ScanForGun();                                  // updates GunInRange + nearestGun

                // E picks up, once per press, only when in range.
                if (input.InteractPressCount != lastInteractCount)
                {
                    lastInteractCount = input.InteractPressCount;
                    if (GunInRange) Equip(nearestGun);
                }
            }
            else
            {
                HandleFiring();                                // left click: one shot per press
            }

            UpdateAim();                                       // right click: zoom the scope

            if (muzzleFlash != null && Time.time >= flashOffAt)
            {
                muzzleFlash.enabled = false;
                if (tracer != null) tracer.enabled = false;
            }
        }

        // ---------------------------------------------------------------- aim / scope

        /// <summary>
        /// Blend the scope in while the aim button is held: zoom the camera's field of view,
        /// steady the handheld shake, and raise <see cref="AimBlend"/> (which also scales look
        /// sensitivity and drives the HUD reticle). Smoothing is exponential so it's framerate
        /// independent and eases rather than snaps.
        /// </summary>
        private void UpdateAim()
        {
            IsAiming = HasGun && AimInputHeld();

            float target = IsAiming ? 1f : 0f;
            AimBlend = Mathf.Lerp(AimBlend, target, 1f - Mathf.Exp(-aimLerpSpeed * Time.deltaTime));
            if (AimBlend < 0.001f) AimBlend = 0f;

            if (vcam != null)
                vcam.Lens.FieldOfView = Mathf.Lerp(hipFieldOfView, aimFieldOfView, AimBlend);
            if (vcamNoise != null)
                vcamNoise.AmplitudeGain = baseNoiseAmplitude * (1f - 0.85f * AimBlend);
        }

        /// <summary>Right mouse (or a gamepad's left trigger) holds the scope open.</summary>
        private static bool AimInputHeld()
        {
            var mouse = Mouse.current;
            if (mouse != null && mouse.rightButton.isPressed) return true;
            var pad = Gamepad.current;
            if (pad != null && pad.leftTrigger.ReadValue() > 0.5f) return true;
            return false;
        }

        // ---------------------------------------------------------------- pickup

        /// <summary>
        /// Every frame, find the nearest gun within reach. A plain distance test — no reliance
        /// on trigger events, which a CharacterController fires unreliably. GunInRange drives
        /// the on-screen prompt so the player always knows when a pickup is possible.
        /// </summary>
        private void ScanForGun()
        {
            nearestGun = null;
            float best = pickupRadius * pickupRadius;
            float closest = float.MaxValue;
            foreach (var g in Object.FindObjectsByType<GunPickup>(FindObjectsSortMode.None))
            {
                float d = (g.transform.position - transform.position).sqrMagnitude;
                closest = Mathf.Min(closest, d);
                if (d < best) { best = d; nearestGun = g; }
            }
            DistanceToGun = closest == float.MaxValue ? -1f : Mathf.Sqrt(closest);
            GunInRange = nearestGun != null;
        }

        private void Equip(GunPickup gun)
        {
            equippedGun = gun;
            HasGun = true;

            // stop it being a pickup and remove its trigger
            var trig = gun.GetComponent<Collider>();
            if (trig != null) Destroy(trig);

            var t = gun.transform;
            if (handBone != null)
            {
                t.SetParent(handBone, false);
                t.localPosition = gun.heldPositionOffset;
                t.localRotation = Quaternion.Euler(gun.heldEulerOffset);
            }

            BuildMuzzle(gun);
        }

        /// <summary>Create the muzzle transform, flash light and tracer line on the equipped gun.</summary>
        private void BuildMuzzle(GunPickup gun)
        {
            var mObj = new GameObject("Muzzle");
            muzzle = mObj.transform;
            muzzle.SetParent(gun.transform, false);
            muzzle.localPosition = gun.muzzleLocal;

            var flashObj = new GameObject("MuzzleFlash");
            flashObj.transform.SetParent(muzzle, false);
            muzzleFlash = flashObj.AddComponent<Light>();
            muzzleFlash.type = LightType.Point;
            muzzleFlash.color = new Color(1f, 0.85f, 0.45f);
            muzzleFlash.intensity = 6f;
            muzzleFlash.range = 4f;
            muzzleFlash.enabled = false;

            var tracerObj = new GameObject("Tracer");
            tracerObj.transform.SetParent(muzzle, false);
            tracer = tracerObj.AddComponent<LineRenderer>();
            tracer.material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            tracer.material.color = new Color(1f, 0.9f, 0.5f);
            tracer.startWidth = 0.03f;
            tracer.endWidth = 0.01f;
            tracer.positionCount = 2;
            tracer.enabled = false;
            tracer.useWorldSpace = true;
        }

        // ---------------------------------------------------------------- firing

        private void HandleFiring()
        {
            // Holding a gun = always in the ready/aim pose. Drives the Animator's pistol layer.
            if (animator != null) animator.SetBool(AimingHash, true);

            // ONE shot per press. We fire only when the press counter grows — so holding the
            // button does nothing (no new press), and a dropped mouse-up can't cause auto-fire.
            if (input.FirePressCount != lastFireCount)
            {
                lastFireCount = input.FirePressCount;
                Fire();
            }
        }

        private void Fire()
        {
            if (cameraTransform == null) return;

            ShotsFired++;
            if (animator != null) animator.SetTrigger(ShootHash);

            // Ray from camera through screen centre — you hit what you're looking at.
            var origin = cameraTransform.position;
            var dir = cameraTransform.forward;
            Vector3 endPoint = origin + dir * range;

            if (Physics.Raycast(origin, dir, out RaycastHit hit, range, hitMask, QueryTriggerInteraction.Ignore))
            {
                endPoint = hit.point;

                var target = hit.collider.GetComponentInParent<ShootingTarget>();
                if (target != null) { target.Hit(hit.point, dir); TargetHits++; }
                else
                {
                    var sheep = hit.collider.GetComponentInParent<Sheep>();
                    if (sheep != null && sheep.Hit(hit.point, dir)) LastSheepHitTime = Time.time;
                    else SpawnSpark(hit.point, hit.normal);
                }
            }

            ShowMuzzleEffects(endPoint);
        }

        private void ShowMuzzleEffects(Vector3 endPoint)
        {
            flashOffAt = Time.time + flashTime;
            if (muzzleFlash != null) muzzleFlash.enabled = true;
            if (tracer != null && muzzle != null)
            {
                tracer.enabled = true;
                tracer.SetPosition(0, muzzle.position);
                tracer.SetPosition(1, endPoint);
            }
        }

        private void SpawnSpark(Vector3 point, Vector3 normal)
        {
            var spark = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            spark.transform.position = point + normal * 0.02f;
            spark.transform.localScale = Vector3.one * 0.06f;
            Destroy(spark.GetComponent<Collider>());
            var r = spark.GetComponent<Renderer>();
            r.material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            r.material.color = new Color(1f, 0.8f, 0.4f);
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            Destroy(spark, 0.4f);
        }
    }
}
