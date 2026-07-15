using UnityEngine;

namespace Wanderer
{
    /// <summary>
    /// Owns the sheep arena: a centre + radius that the flock wanders inside and respawns into.
    /// Hands out random grounded points, clear of scenery, so a shot sheep pops back in at a
    /// fresh spot somewhere else. One lives in the scene; the sheep find it with
    /// <c>FindFirstObjectByType</c>.
    /// </summary>
    public class HuntManager : MonoBehaviour
    {
        [Header("Arena (world XZ)")]
        [SerializeField] private Vector2 arenaCenter = new Vector2(20f, -55f);
        [SerializeField] private float arenaRadius = 26f;

        [SerializeField] private Terrain terrain;

        public Vector2 ArenaCenter => arenaCenter;
        public float ArenaRadius => arenaRadius;

        private void Awake()
        {
            if (terrain == null) terrain = Terrain.activeTerrain;
        }

        /// <summary>A random grounded point in the arena that isn't already blocked by geometry.</summary>
        public Vector3 RandomPoint()
        {
            for (int i = 0; i < 24; i++)
            {
                float a = Random.value * Mathf.PI * 2f;
                float r = arenaRadius * Mathf.Sqrt(Random.value);   // uniform over the disc
                var p = new Vector3(arenaCenter.x + Mathf.Cos(a) * r, 0f, arenaCenter.y + Mathf.Sin(a) * r);
                p.y = GroundHeight(p);
                if (!Blocked(p))
                    return p;
            }
            var fallback = new Vector3(arenaCenter.x, 0f, arenaCenter.y);
            fallback.y = GroundHeight(fallback);
            return fallback;
        }

        /// <summary>
        /// Is this ground point already occupied by a rock, tree, the table or another sheep?
        /// The probe sits well ABOVE the surface (1.1m up, 0.55m radius) so the terrain itself is
        /// never counted as an obstacle — otherwise every candidate reads as blocked and the whole
        /// flock ends up piling onto the single fallback spot.
        /// </summary>
        public bool Blocked(Vector3 groundPoint) =>
            Physics.CheckSphere(groundPoint + Vector3.up * 1.1f, 0.55f, ~0, QueryTriggerInteraction.Ignore);

        public float GroundHeight(Vector3 worldPos) =>
            terrain != null ? terrain.SampleHeight(worldPos) : worldPos.y;

        public bool InArena(Vector3 worldPos)
        {
            float dx = worldPos.x - arenaCenter.x, dz = worldPos.z - arenaCenter.y;
            return dx * dx + dz * dz <= arenaRadius * arenaRadius;
        }
    }
}
