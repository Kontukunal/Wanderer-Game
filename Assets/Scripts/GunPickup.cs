using UnityEngine;

namespace Wanderer
{
    /// <summary>
    /// Marks a world object as a pickup-able gun. It sits on the table until the player is
    /// close (its trigger overlaps the player) and presses Interact; the WeaponController then
    /// equips it. This component only advertises "I am a gun on the ground" — the equipping
    /// logic lives on the player so the gun object stays dumb.
    /// </summary>
    public class GunPickup : MonoBehaviour
    {
        [Tooltip("Local offset/rotation to apply when this gun is held in the hand. Tune per model.")]
        public Vector3 heldPositionOffset = new Vector3(0f, 0f, 0f);
        public Vector3 heldEulerOffset = new Vector3(0f, 90f, 0f);

        [Tooltip("Where the muzzle is, in the gun's local space, so shots start from the barrel tip.")]
        public Vector3 muzzleLocal = new Vector3(0f, 0.05f, 0.35f);

        public bool PlayerInRange { get; private set; }

        private void OnTriggerEnter(Collider other)
        {
            if (other.CompareTag("Player")) PlayerInRange = true;
        }

        private void OnTriggerExit(Collider other)
        {
            if (other.CompareTag("Player")) PlayerInRange = false;
        }
    }
}
