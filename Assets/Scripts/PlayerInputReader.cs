using UnityEngine;
using UnityEngine.InputSystem;

namespace Wanderer
{
    /// <summary>
    /// Thin adapter over the Input System. Nothing else in the game touches
    /// InputAction directly, so rebinding or swapping devices stays local to this file.
    /// </summary>
    [RequireComponent(typeof(PlayerInput))]
    public class PlayerInputReader : MonoBehaviour
    {
        public Vector2 Move { get; private set; }
        public Vector2 Look { get; private set; }
        public bool SprintHeld { get; private set; }

        /// <summary>Held state of fire (Attack). True while the button is down.</summary>
        public bool FireHeld { get; private set; }

        /// <summary>
        /// Monotonic count of fire (Attack) presses. A consumer stores the value it last acted
        /// on and fires again only when this grows — so exactly one shot per press, immune to
        /// frame ordering and to a "held" state whose release event got dropped.
        /// </summary>
        public int FirePressCount { get; private set; }

        /// <summary>Monotonic count of Interact (E) presses. Same one-per-press contract.</summary>
        public int InteractPressCount { get; private set; }

        /// <summary>True only on the frame jump was pressed. Consumed by the controller.</summary>
        public bool JumpQueued { get; private set; }

        [Tooltip("How long a jump press stays buffered, so a press slightly before landing still fires.")]
        [SerializeField] private float jumpBufferSeconds = 0.15f;

        private float jumpQueuedAt = float.NegativeInfinity;

        // Invoked by PlayerInput in "Send Messages" mode. These callbacks run outside Update,
        // but incrementing a counter is order-independent — no flag can be read-then-cleared
        // in the wrong sequence.
        private void OnMove(InputValue v) => Move = v.Get<Vector2>();
        private void OnLook(InputValue v) => Look = v.Get<Vector2>();
        private void OnSprint(InputValue v) => SprintHeld = v.isPressed;

        private void OnJump(InputValue v)
        {
            if (v.isPressed) jumpQueuedAt = Time.time;
        }

        private void OnAttack(InputValue v)
        {
            FireHeld = v.isPressed;
            if (v.isPressed) FirePressCount++;   // one increment per genuine press edge
        }

        private void OnInteract(InputValue v)
        {
            if (v.isPressed) InteractPressCount++;
        }

        private void Update()
        {
            JumpQueued = Time.time - jumpQueuedAt <= jumpBufferSeconds;
        }

        /// <summary>Call when a jump is actually spent, so it can't double-fire.</summary>
        public void ConsumeJump() => jumpQueuedAt = float.NegativeInfinity;
    }
}
