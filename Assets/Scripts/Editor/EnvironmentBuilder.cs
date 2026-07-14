using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Wanderer.EditorTools
{
    /// <summary>
    /// Builds the Milestone 1 environment: a sculpted headland with a ridge, a sheltered
    /// hollow, and a cliff outcrop as the focal point.
    ///
    /// The terrain is authored rather than random: a few hand-placed landform "features"
    /// are summed, then fractal noise roughens the surface. Doing it in code keeps the
    /// composition reproducible and tweakable, which a hand-sculpted heightmap isn't.
    /// </summary>
    public static class EnvironmentBuilder
    {
        private const int HeightmapRes = 513;      // must be 2^n + 1
        private const int AlphamapRes = 512;
        public const float TerrainSize = 400f;
        public const float TerrainHeight = 90f;

        private const string EnvDir = "Assets/_Project/Env";
        private const string TerrainAssetPath = EnvDir + "/Terrain/WandererTerrain.asset";

        /// <summary>
        /// A ridge: a raised line, not a blob. Radial bumps sum into a dome; lines sum into
        /// terrain that reads as geology. This is the single biggest lever on the composition.
        /// </summary>
        private struct Ridge
        {
            public Vector2 A, B;     // normalised endpoints of the crest line
            public float Width;      // normalised half-width
            public float Height;     // signed; negative carves a basin
            public float Sharp;      // >1 = knife-edge crest, <1 = rounded shoulder
        }

        // The composition, read as a plan view. North (v=1) is the high ground the player
        // looks toward; the player spawns low in the south-east basin.
        private static readonly Ridge[] Landforms =
        {
            // The main escarpment across the north — the horizon and the focal mass.
            new Ridge { A = new Vector2(0.05f, 0.80f), B = new Vector2(0.62f, 0.96f), Width = 0.20f, Height = 1.00f, Sharp = 1.7f },
            // A spur peeling off to the east, so the skyline isn't one flat wall.
            new Ridge { A = new Vector2(0.62f, 0.92f), B = new Vector2(0.95f, 0.66f), Width = 0.15f, Height = 0.78f, Sharp = 1.9f },
            // A lower buttress in front of the escarpment — gives the eye a middle distance.
            new Ridge { A = new Vector2(0.22f, 0.62f), B = new Vector2(0.52f, 0.70f), Width = 0.11f, Height = 0.42f, Sharp = 2.2f },
            // Western shoulder, framing the left of frame.
            new Ridge { A = new Vector2(0.02f, 0.42f), B = new Vector2(0.14f, 0.72f), Width = 0.13f, Height = 0.50f, Sharp = 1.8f },

            // The basin the player starts in: a carved hollow, not a dip in a dome.
            new Ridge { A = new Vector2(0.44f, 0.26f), B = new Vector2(0.62f, 0.34f), Width = 0.22f, Height = -0.30f, Sharp = 1.2f },

            // Gentle rolling ground in the south so the foreground isn't dead flat.
            new Ridge { A = new Vector2(0.14f, 0.14f), B = new Vector2(0.40f, 0.08f), Width = 0.14f, Height = 0.20f, Sharp = 1.4f },
            new Ridge { A = new Vector2(0.74f, 0.10f), B = new Vector2(0.94f, 0.24f), Width = 0.13f, Height = 0.24f, Sharp = 1.5f },
        };

        /// <summary>Perpendicular distance from p to segment AB, normalised by the ridge width.</summary>
        private static float DistToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            float len2 = ab.sqrMagnitude;
            if (len2 < 1e-6f) return Vector2.Distance(p, a);
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
            return Vector2.Distance(p, a + ab * t);
        }

        [MenuItem("Wanderer/Build Environment")]
        public static void Build()
        {
            var terrainData = BuildTerrainData();
            Debug.Log($"[Wanderer] Terrain built: {TerrainSize}m, max height {TerrainHeight}m");
            EditorUtility.SetDirty(terrainData);
            AssetDatabase.SaveAssets();
        }

        public static TerrainData BuildTerrainData()
        {
            Directory.CreateDirectory(EnvDir + "/Terrain");

            var data = AssetDatabase.LoadAssetAtPath<TerrainData>(TerrainAssetPath);
            if (data == null)
            {
                data = new TerrainData();
                AssetDatabase.CreateAsset(data, TerrainAssetPath);
            }

            data.heightmapResolution = HeightmapRes;
            data.size = new Vector3(TerrainSize, TerrainHeight, TerrainSize);

            data.SetHeights(0, 0, GenerateHeights());
            ApplyTerrainLayers(data);
            data.SetAlphamaps(0, 0, GenerateSplatmap(data));

            return data;
        }

        /// <summary>
        /// Ridged multifractal. Folding the noise about zero (1 - |n|) turns smooth hills into
        /// crests and gullies — it's the difference between "CG blob" and "eroded rock".
        /// Each octave is weighted by the one above it, so detail collects on the ridges.
        /// </summary>
        private static float RidgedNoise(float u, float v, int octaves, float freq, float seed)
        {
            float sum = 0f, amp = 0.5f, weight = 1f;
            for (int o = 0; o < octaves; o++)
            {
                float n = Mathf.PerlinNoise(u * freq + seed, v * freq + seed);
                n = 1f - Mathf.Abs(n * 2f - 1f);
                n *= n;
                n *= weight;
                weight = Mathf.Clamp01(n * 2f);
                sum += n * amp;
                amp *= 0.5f;
                freq *= 2.04f;
            }
            return sum;
        }

        private static float Fbm(float u, float v, int octaves, float freq, float seed)
        {
            float sum = 0f, amp = 0.5f;
            for (int o = 0; o < octaves; o++)
            {
                sum += (Mathf.PerlinNoise(u * freq + seed, v * freq + seed) - 0.5f) * amp;
                amp *= 0.5f;
                freq *= 2.03f;
            }
            return sum;
        }

        private static float[,] GenerateHeights()
        {
            var h = new float[HeightmapRes, HeightmapRes];

            for (int y = 0; y < HeightmapRes; y++)
            {
                for (int x = 0; x < HeightmapRes; x++)
                {
                    float u = x / (float)(HeightmapRes - 1);
                    float v = y / (float)(HeightmapRes - 1);
                    var p = new Vector2(u, v);

                    // 1. The authored landforms — ridges, not blobs.
                    float height = 0.14f;
                    foreach (var r in Landforms)
                    {
                        float d = DistToSegment(p, r.A, r.B) / r.Width;
                        if (d >= 1f) continue;
                        float w = Mathf.Cos(d * Mathf.PI * 0.5f);      // 1 on the crest -> 0 at the edge
                        w = Mathf.Pow(w, r.Sharp);
                        height += r.Height * w * 0.62f;
                    }

                    // 2. Warp the sample point before adding detail. Domain warping is what
                    //    stops noise looking like noise — it makes strata bend and flow.
                    float wx = u + Fbm(u, v, 3, 2.1f, 11.3f) * 0.18f;
                    float wy = v + Fbm(u, v, 3, 2.1f, 47.9f) * 0.18f;

                    // 3. Crests and gullies, scaled by altitude so the high ground is rugged
                    //    and the basin stays walkable.
                    float alt = Mathf.Clamp01((height - 0.16f) / 0.7f);
                    height += RidgedNoise(wx, wy, 6, 3.4f, 137.31f) * 0.30f * Mathf.SmoothStep(0f, 1f, alt);

                    // 4. Broad undulation everywhere, so nothing is ever truly flat.
                    height += Fbm(wx, wy, 5, 2.6f, 71.7f) * 0.10f;

                    // 5. Fine surface roughness — reads at walking distance.
                    height += Fbm(u, v, 3, 26f, 5.5f) * 0.012f;

                    h[y, x] = Mathf.Clamp01(height);
                }
            }
            return h;
        }

        private static void ApplyTerrainLayers(TerrainData data)
        {
            // grass -> forest floor -> rock -> sand/scree. Order matters: the splatmap indexes it.
            var specs = new (string folder, float tiling)[]
            {
                ("aerial_grass_rock",  14f),
                ("forrest_ground_01",  10f),
                ("rocky_terrain_02",   18f),
                ("coast_sand_rocks_02", 12f),
            };

            var layers = new List<TerrainLayer>();
            foreach (var (folder, tiling) in specs)
            {
                string layerPath = $"{EnvDir}/Terrain/TL_{folder}.terrainlayer";
                var layer = AssetDatabase.LoadAssetAtPath<TerrainLayer>(layerPath);
                if (layer == null)
                {
                    layer = new TerrainLayer();
                    AssetDatabase.CreateAsset(layer, layerPath);
                }

                string b = $"{EnvDir}/Terrain/{folder}/{folder}";
                layer.diffuseTexture = AssetDatabase.LoadAssetAtPath<Texture2D>($"{b}_Diffuse.jpg");
                layer.normalMapTexture = AssetDatabase.LoadAssetAtPath<Texture2D>($"{b}_nor_gl.jpg");
                layer.maskMapTexture = AssetDatabase.LoadAssetAtPath<Texture2D>($"{b}_Mask.png");
                layer.tileSize = new Vector2(tiling, tiling);
                layer.normalScale = 1f;
                layer.smoothness = 0.12f;   // wet-ish rock catches a little light; keeps it from reading flat
                layer.metallic = 0f;
                EditorUtility.SetDirty(layer);
                layers.Add(layer);
            }

            data.terrainLayers = layers.ToArray();
        }

        /// <summary>
        /// Paints by slope and altitude, the way real ground sorts itself:
        /// grass on gentle low ground, forest floor in the hollows, rock on steep faces,
        /// scree on the exposed tops.
        /// </summary>
        private static float[,,] GenerateSplatmap(TerrainData data)
        {
            int n = data.terrainLayers.Length;
            var map = new float[AlphamapRes, AlphamapRes, n];

            for (int y = 0; y < AlphamapRes; y++)
            {
                for (int x = 0; x < AlphamapRes; x++)
                {
                    float u = x / (float)(AlphamapRes - 1);
                    float v = y / (float)(AlphamapRes - 1);

                    float height = data.GetInterpolatedHeight(u, v) / TerrainHeight;
                    float steep = data.GetSteepness(u, v) / 90f;      // 0..1

                    // break up the boundaries so layers interlock instead of banding
                    float jitter = (Mathf.PerlinNoise(u * 26f, v * 26f) - 0.5f) * 0.22f;

                    float rock = Mathf.SmoothStep(0f, 1f, (steep - 0.34f + jitter) * 3.2f);
                    float scree = Mathf.SmoothStep(0f, 1f, (height - 0.62f + jitter) * 3.4f) * (1f - rock);
                    float forest = Mathf.SmoothStep(0f, 1f, (0.34f - height + jitter) * 3.6f) * (1f - rock);
                    float grass = Mathf.Max(0f, 1f - rock - scree - forest);

                    float sum = grass + forest + rock + scree;
                    if (sum <= 0.0001f) { grass = 1f; sum = 1f; }

                    map[y, x, 0] = grass / sum;
                    map[y, x, 1] = forest / sum;
                    map[y, x, 2] = rock / sum;
                    map[y, x, 3] = scree / sum;
                }
            }
            return map;
        }
    }
}
