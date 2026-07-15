using System.Collections;
using UnityEngine;

namespace Wanderer
{
    /// <summary>
    /// A wandering sheep the player hunts. It ambles between random points in the arena, pauses
    /// to graze, and bolts away when the player gets close (so it's a moving target, not a
    /// stationary one). A shot flops it onto its side; after a beat it vanishes and pops back in
    /// at a fresh spot somewhere else in the arena. Movement is a simple grounded steer — it
    /// samples the terrain height each frame, so no NavMesh bake is needed.
    /// </summary>
    [RequireComponent(typeof(CapsuleCollider))]
    public class Sheep : MonoBehaviour
    {
        [Header("Movement")]
        [SerializeField] private float walkSpeed = 2.2f;
        [SerializeField] private float fleeSpeed = 6.0f;
        [SerializeField] private float turnSpeed = 9f;
        [SerializeField] private float wanderHop = 8f;      // how far a graze-to-graze step reaches
        [SerializeField] private float arriveDist = 1.1f;

        [Header("Behaviour")]
        [SerializeField] private float fleeRadius = 9f;     // player closer than this = panic
        [SerializeField] private float respawnDelay = 1.5f;

        /// <summary>Set by the builder so death can tint the wool without touching other sheep.</summary>
        public Renderer bodyRenderer;

        private HuntManager hunt;
        private Transform player;
        private CapsuleCollider col;
        private Vector3 destination;
        private Vector3 homeScale;
        private float grazeUntil;
        private bool dead;

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private Color woolColor = Color.white;
        private MaterialPropertyBlock mpb;

        private void Start()
        {
            hunt = Object.FindFirstObjectByType<HuntManager>();
            col = GetComponent<CapsuleCollider>();
            homeScale = transform.localScale;
            mpb = new MaterialPropertyBlock();

            var pc = Object.FindFirstObjectByType<ThirdPersonController>();
            if (pc != null) player = pc.transform;
            if (bodyRenderer != null && bodyRenderer.sharedMaterial != null)
                woolColor = bodyRenderer.sharedMaterial.GetColor(BaseColorId);

            // Sheep are positioned at build time, before the rocks are scattered, so one may
            // start half-buried in scenery. Now that physics is live, nudge it to open ground.
            if (hunt != null)
            {
                col.enabled = false;    // don't detect our own body
                if (hunt.Blocked(transform.position))
                    transform.position = hunt.RandomPoint();
                col.enabled = true;
            }

            Ground();
            PickWanderTarget();
        }

        private void Update()
        {
            if (dead || hunt == null) return;

            bool fleeing = false;
            if (player != null)
            {
                Vector3 away = transform.position - player.position;
                away.y = 0f;
                if (away.sqrMagnitude < fleeRadius * fleeRadius && away.sqrMagnitude > 0.001f)
                {
                    // run directly away from the player, but stay inside the arena
                    destination = transform.position + away.normalized * wanderHop;
                    if (!hunt.InArena(destination))
                        destination = new Vector3(hunt.ArenaCenter.x, 0f, hunt.ArenaCenter.y);
                    fleeing = true;
                    grazeUntil = 0f;
                }
            }

            if (!fleeing && Time.time < grazeUntil)
            {
                Ground();               // stand and graze
                return;
            }

            MoveToward(destination, fleeing ? fleeSpeed : walkSpeed);

            Vector3 flat = destination - transform.position; flat.y = 0f;
            if (flat.magnitude < arriveDist)
            {
                if (fleeing) PickWanderTarget();          // keep bolting
                else { grazeUntil = Time.time + Random.Range(0.8f, 2.6f); PickWanderTarget(); }
            }
        }

        private void MoveToward(Vector3 target, float speed)
        {
            Vector3 dir = target - transform.position; dir.y = 0f;
            if (dir.sqrMagnitude > 0.0001f)
            {
                dir.Normalize();
                var look = Quaternion.LookRotation(dir, Vector3.up);
                transform.rotation = Quaternion.Slerp(transform.rotation, look, turnSpeed * Time.deltaTime);
                transform.position += dir * speed * Time.deltaTime;
            }
            Ground();
        }

        /// <summary>Pin the sheep to the terrain surface at its current XZ.</summary>
        private void Ground()
        {
            if (hunt == null) return;
            var p = transform.position;
            p.y = hunt.GroundHeight(p);
            transform.position = p;
        }

        private void PickWanderTarget()
        {
            for (int i = 0; i < 12; i++)
            {
                float a = Random.value * Mathf.PI * 2f;
                float r = Random.Range(wanderHop * 0.4f, wanderHop);
                var p = transform.position + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                if (hunt.InArena(p) && !hunt.Blocked(p))
                {
                    destination = p;
                    return;
                }
            }
            destination = hunt.RandomPoint();   // boxed in — teleport-quality point back toward open ground
        }

        // ---------------------------------------------------------------- getting shot

        /// <summary>
        /// Called by the weapon when a shot lands on this sheep. Returns true if it was a live
        /// hit (so the weapon can play its hit-marker); a sheep already flopped over ignores shots.
        /// </summary>
        public bool Hit(Vector3 worldPoint, Vector3 fromDirection)
        {
            if (dead || hunt == null) return false;
            dead = true;
            col.enabled = false;
            StartCoroutine(DieAndRespawn(fromDirection));
            return true;
        }

        private IEnumerator DieAndRespawn(Vector3 fromDirection)
        {
            // flop onto its side, away from the shot, and desaturate the wool
            float yaw = transform.eulerAngles.y;
            var upright = transform.rotation;
            var knocked = Quaternion.Euler(0f, yaw, Random.value < 0.5f ? 88f : -88f);
            SetWool(new Color(0.5f, 0.48f, 0.46f));

            float t = 0f;
            while (t < 0.22f)
            {
                t += Time.deltaTime;
                transform.rotation = Quaternion.Slerp(upright, knocked, t / 0.22f);
                yield return null;
            }
            transform.rotation = knocked;

            yield return new WaitForSeconds(respawnDelay);

            // shrink out
            t = 0f;
            while (t < 0.15f)
            {
                t += Time.deltaTime;
                transform.localScale = Vector3.Lerp(homeScale, Vector3.zero, t / 0.15f);
                yield return null;
            }

            // reappear somewhere fresh, upright, and restore the wool colour
            transform.position = hunt != null ? hunt.RandomPoint() : transform.position;
            transform.rotation = Quaternion.Euler(0f, Random.value * 360f, 0f);
            SetWool(woolColor);
            Ground();

            t = 0f;
            while (t < 0.2f)
            {
                t += Time.deltaTime;
                transform.localScale = Vector3.Lerp(Vector3.zero, homeScale, t / 0.2f);
                yield return null;
            }
            transform.localScale = homeScale;

            col.enabled = true;
            dead = false;
            grazeUntil = 0f;
            PickWanderTarget();
        }

        private void SetWool(Color c)
        {
            if (bodyRenderer == null) return;
            bodyRenderer.GetPropertyBlock(mpb);
            mpb.SetColor(BaseColorId, c);
            bodyRenderer.SetPropertyBlock(mpb);
        }
    }
}
