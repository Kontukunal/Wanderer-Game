using UnityEngine;

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

        [Header("References")]
        [SerializeField] private Transform cameraTransform;

        public bool HasGun { get; private set; }
        public bool IsAiming { get; private set; }

        /// <summary>True when an un-picked gun is within pickupRadius (drives the HUD prompt).</summary>
        public bool GunInRange { get; private set; }

        /// <summary>Total shots that have landed on a target.</summary>
        public int TargetHits { get; private set; }

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

            if (muzzleFlash != null && Time.time >= flashOffAt)
            {
                muzzleFlash.enabled = false;
                if (tracer != null) tracer.enabled = false;
            }
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
            IsAiming = true;
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
                else SpawnSpark(hit.point, hit.normal);
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
