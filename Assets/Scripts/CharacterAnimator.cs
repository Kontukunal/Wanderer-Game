using UnityEngine;

namespace Wanderer
{
    /// <summary>
    /// Translates controller state into Animator parameters.
    /// Lives on the rig child so the Animator and the CharacterController stay separate.
    /// </summary>
    [RequireComponent(typeof(Animator))]
    public class CharacterAnimator : MonoBehaviour
    {
        private static readonly int SpeedHash = Animator.StringToHash("Speed");
        private static readonly int GroundedHash = Animator.StringToHash("Grounded");
        private static readonly int VerticalHash = Animator.StringToHash("VerticalSpeed");
        private static readonly int JumpHash = Animator.StringToHash("Jump");
        private static readonly int PlayRateHash = Animator.StringToHash("PlayRate");

        [Tooltip("Seconds of smoothing on the Speed parameter. Keeps the blend tree from popping.")]
        [SerializeField] private float speedDamp = 0.08f;

        [Tooltip("Speed the single-clip (Generic) animator treats as normal playback rate.")]
        [SerializeField] private float referenceSpeed = 2.8f;

        private Animator animator;
        private bool hasPlayRate;

        private void Awake()
        {
            animator = GetComponent<Animator>();

            // A Generic rig can't use the blend tree, so it drives one looping clip whose
            // playback rate follows ground speed instead. Humanoid rigs have no such
            // parameter — check, or Unity logs a warning every frame.
            foreach (var p in animator.parameters)
                if (p.nameHash == PlayRateHash) hasPlayRate = true;
        }

        /// <summary>Speed is in m/s and feeds the blend tree directly — same unit as the controller.</summary>
        public void Tick(float planarSpeed, bool grounded, float verticalVelocity)
        {
            animator.SetFloat(SpeedHash, planarSpeed, speedDamp, Time.deltaTime);
            animator.SetBool(GroundedHash, grounded);
            animator.SetFloat(VerticalHash, verticalVelocity);

            if (hasPlayRate)
            {
                // No floor: a floor makes the character walk on the spot while standing still.
                // At zero speed the clip stops, which is the honest behaviour for a rig that
                // has no idle animation.
                float rate = Mathf.Clamp(planarSpeed / referenceSpeed, 0f, 2f);
                animator.SetFloat(PlayRateHash, rate);
            }
        }

        public void TriggerJump() => animator.SetTrigger(JumpHash);
    }
}
