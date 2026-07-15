using System.Collections;
using UnityEngine;

namespace Wanderer
{
    /// <summary>
    /// A hittable target. When a shot's raycast lands on it, the weapon calls <see cref="Hit"/>,
    /// which gives visible, satisfying feedback: the target rocks back and flashes, and a
    /// running hit count is tracked. Deliberately simple — no health, it's a practice target.
    /// </summary>
    public class ShootingTarget : MonoBehaviour
    {
        [SerializeField] private float knockbackAngle = 18f;
        [SerializeField] private float recoverTime = 0.35f;

        public int HitCount { get; private set; }

        private Quaternion restRotation;
        private Coroutine reaction;

        private void Awake() => restRotation = transform.localRotation;

        /// <summary>Called by the weapon at the world point the ray struck.</summary>
        public void Hit(Vector3 worldPoint, Vector3 fromDirection)
        {
            HitCount++;
            if (reaction != null) StopCoroutine(reaction);
            reaction = StartCoroutine(React(fromDirection));
        }

        private IEnumerator React(Vector3 fromDirection)
        {
            // rock backward around the base, away from the shooter
            var localDir = transform.parent != null
                ? transform.parent.InverseTransformDirection(fromDirection)
                : fromDirection;
            var axis = Vector3.Cross(Vector3.up, new Vector3(localDir.x, 0f, localDir.z)).normalized;
            if (axis.sqrMagnitude < 0.001f) axis = Vector3.right;

            var knocked = restRotation * Quaternion.AngleAxis(knockbackAngle, axis);

            float t = 0f;
            // quick snap to the knocked pose
            while (t < 0.06f)
            {
                t += Time.deltaTime;
                transform.localRotation = Quaternion.Slerp(restRotation, knocked, t / 0.06f);
                yield return null;
            }
            // spring back to rest
            t = 0f;
            while (t < recoverTime)
            {
                t += Time.deltaTime;
                transform.localRotation = Quaternion.Slerp(knocked, restRotation, t / recoverTime);
                yield return null;
            }
            transform.localRotation = restRotation;
            reaction = null;
        }
    }
}
