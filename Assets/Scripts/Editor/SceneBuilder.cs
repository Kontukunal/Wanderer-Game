using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Cinemachine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace Wanderer.EditorTools
{
    /// <summary>
    /// Assembles the playable scene: terrain, dressing, lighting, post, camera, player.
    /// Re-running it rebuilds from scratch, so the scene is always reproducible from code.
    /// </summary>
    public static class SceneBuilder
    {
        private const string ScenePath = "Assets/_Project/Scenes/Wanderer.unity";

        // Which hero to spawn. Swap this one line to change character, then rebuild the scene.
        //   Player_Rogue.prefab — KayKit hooded rogue: Humanoid, full blend tree + jump.
        //   Player_Golu.prefab  — Golu: Generic rig, walk/run clip only, flat colours.
        private const string PlayerPrefabPath = "Assets/_Project/Prefabs/Player_Rogue.prefab";
        private const string EnvDir = "Assets/_Project/Env";
        private const string ProfilePath = EnvDir + "/PostProcessProfile.asset";
        private const string SkyMatPath = EnvDir + "/M_Sky_HDRI.mat";

        // The sun. Low angle is doing most of the work here: long shadows, warm rake
        // across the terrain, strong sense of time-of-day.
        private const float SunPitch = 14f;     // degrees above horizon
        private const float SunYaw = -34f;

        [MenuItem("Wanderer/Build Scene", priority = 0)]
        public static void BuildScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Directory.CreateDirectory("Assets/_Project/Scenes");

            var terrain = BuildTerrain();
            var sun = BuildLighting();
            BuildSky();
            BuildPostProcessing();
            BuildTrainingGround(terrain);   // flattens the centre before props/player land on it
            new GameObject("GameHUD").AddComponent<Wanderer.GameHUD>();
            var player = PlacePlayer(terrain);
            BuildCamera(player);
            DressScene(terrain);
            BuildLightProbes(terrain);

            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log("[Wanderer] Scene built -> " + ScenePath);
        }

        // ---------------------------------------------------------------- terrain

        private static Terrain BuildTerrain()
        {
            var data = EnvironmentBuilder.BuildTerrainData();
            var go = Terrain.CreateTerrainGameObject(data);
            go.name = "Terrain";
            go.transform.position = new Vector3(
                -EnvironmentBuilder.TerrainSize * 0.5f, 0f, -EnvironmentBuilder.TerrainSize * 0.5f);

            var t = go.GetComponent<Terrain>();
            t.heightmapPixelError = 3f;         // tighter than default: silhouette matters on a ridge
            t.basemapDistance = 200f;
            t.shadowCastingMode = ShadowCastingMode.On;
            t.materialTemplate = new Material(Shader.Find("Universal Render Pipeline/Terrain/Lit"));
            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.ContributeGI | StaticEditorFlags.OccluderStatic);
            return t;
        }

        // ---------------------------------------------------------------- lighting

        private static Light BuildLighting()
        {
            var go = new GameObject("Sun");
            var l = go.AddComponent<Light>();
            l.type = LightType.Directional;
            l.color = new Color(1f, 0.86f, 0.68f);      // warm low sun
            l.intensity = 2.6f;
            l.shadows = LightShadows.Soft;
            l.shadowStrength = 0.85f;
            l.shadowBias = 0.03f;
            l.shadowNormalBias = 0.25f;
            go.transform.rotation = Quaternion.Euler(SunPitch, SunYaw, 0f);

            var ld = l.GetUniversalAdditionalLightData();
            if (ld != null) ld.usePipelineSettings = true;

            l.lightmapBakeType = LightmapBakeType.Mixed;
            return l;
        }

        private static void BuildSky()
        {
            var hdri = AssetDatabase.LoadAssetAtPath<Cubemap>($"{EnvDir}/HDRI/belfast_sunset_puresky_4k.hdr")
                    ?? (Texture)AssetDatabase.LoadAssetAtPath<Texture2D>($"{EnvDir}/HDRI/belfast_sunset_puresky_4k.hdr") as Texture;

            var mat = AssetDatabase.LoadAssetAtPath<Material>(SkyMatPath);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Skybox/Panoramic"));
                AssetDatabase.CreateAsset(mat, SkyMatPath);
            }
            mat.SetTexture("_Tex", hdri);
            mat.SetFloat("_Exposure", 1.15f);
            mat.SetFloat("_Rotation", 205f);   // line the HDRI's sun up with our directional light
            EditorUtility.SetDirty(mat);

            RenderSettings.skybox = mat;
            RenderSettings.sun = Object.FindFirstObjectByType<Light>();

            // Ambient straight from the HDRI — this is what makes shadows read as sky-blue
            // rather than flat grey, and it's most of the "photoreal" feel.
            RenderSettings.ambientMode = AmbientMode.Skybox;
            RenderSettings.ambientIntensity = 1.0f;

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = new Color(0.72f, 0.76f, 0.82f);
            RenderSettings.fogDensity = 0.0022f;   // aerial perspective: distant ridges recede

            DynamicGI.UpdateEnvironment();
        }

        // ---------------------------------------------------------------- post

        private static void BuildPostProcessing()
        {
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfilePath);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                AssetDatabase.CreateAsset(profile, ProfilePath);
            }
            foreach (var c in profile.components.ToList()) Object.DestroyImmediate(c, true);
            profile.components.Clear();

            // ACES: filmic highlight rolloff. Without this, a bright sky just clips to white.
            var tone = profile.Add<Tonemapping>(true);
            tone.mode.overrideState = true;
            tone.mode.value = TonemappingMode.ACES;

            var expo = profile.Add<ColorAdjustments>(true);
            expo.postExposure.overrideState = true; expo.postExposure.value = 0.15f;
            expo.contrast.overrideState = true;     expo.contrast.value = 12f;
            expo.saturation.overrideState = true;   expo.saturation.value = 6f;
            expo.colorFilter.overrideState = true;  expo.colorFilter.value = new Color(1f, 0.98f, 0.95f);

            // Warm the highlights, cool the shadows — the classic golden-hour split.
            var grade = profile.Add<ShadowsMidtonesHighlights>(true);
            grade.shadows.overrideState = true;
            grade.shadows.value = new Vector4(0.92f, 0.97f, 1.12f, 0f);
            grade.highlights.overrideState = true;
            grade.highlights.value = new Vector4(1.08f, 1.02f, 0.92f, 0f);

            var bloom = profile.Add<Bloom>(true);
            bloom.threshold.overrideState = true; bloom.threshold.value = 1.05f;
            bloom.intensity.overrideState = true; bloom.intensity.value = 0.22f;
            bloom.scatter.overrideState = true;   bloom.scatter.value = 0.62f;

            var vig = profile.Add<Vignette>(true);
            vig.intensity.overrideState = true; vig.intensity.value = 0.26f;
            vig.smoothness.overrideState = true; vig.smoothness.value = 0.45f;

            var dof = profile.Add<DepthOfField>(true);
            dof.mode.overrideState = true; dof.mode.value = DepthOfFieldMode.Bokeh;
            dof.focusDistance.overrideState = true; dof.focusDistance.value = 9f;
            dof.aperture.overrideState = true; dof.aperture.value = 8f;   // gentle — just softens the far ridge
            dof.focalLength.overrideState = true; dof.focalLength.value = 42f;

            var grain = profile.Add<FilmGrain>(true);
            grain.type.overrideState = true; grain.type.value = FilmGrainLookup.Thin1;
            grain.intensity.overrideState = true; grain.intensity.value = 0.16f;

            var ca = profile.Add<ChromaticAberration>(true);
            ca.intensity.overrideState = true; ca.intensity.value = 0.08f;

            EditorUtility.SetDirty(profile);

            var go = new GameObject("Global Volume");
            var v = go.AddComponent<Volume>();
            v.isGlobal = true;
            v.priority = 1f;
            v.sharedProfile = profile;
        }

        // ---------------------------------------------------------------- player + camera

        private static GameObject PlacePlayer(Terrain terrain)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
            var player = (GameObject)PrefabUtility.InstantiatePrefab(prefab);

            // Drop the player in the hollow, facing the ridge (the focal point).
            var spawn = new Vector3(20f, 0f, -60f);
            spawn.y = terrain.SampleHeight(spawn) + 0.2f;
            player.transform.position = spawn;
            player.transform.rotation = Quaternion.Euler(0f, -20f, 0f);
            return player;
        }

        private static void BuildCamera(GameObject player)
        {
            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 900f;
            cam.fieldOfView = 50f;
            camGo.AddComponent<AudioListener>();
            camGo.AddComponent<CinemachineBrain>();

            var camData = camGo.GetComponent<UniversalAdditionalCameraData>();
            if (camData == null) camData = camGo.AddComponent<UniversalAdditionalCameraData>();
            camData.renderPostProcessing = true;
            camData.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            camData.antialiasingQuality = AntialiasingQuality.High;
            camData.renderShadows = true;

            // Chest height on a 1.2m hero.
            var target = new GameObject("CameraTarget");
            target.transform.SetParent(player.transform, false);
            target.transform.localPosition = new Vector3(0f, 0.95f, 0f);

            var vcamGo = new GameObject("CM FollowCam");
            var vcam = vcamGo.AddComponent<CinemachineCamera>();
            vcam.Follow = target.transform;
            vcam.LookAt = target.transform;
            vcam.Lens.FieldOfView = 50f;
            vcam.Lens.NearClipPlane = 0.1f;
            vcam.Lens.FarClipPlane = 900f;

            // PUBG-style third-person rig: the camera ORBITS the character on mouse /
            // right-stick, and the character turns to face where he's moving. The three-ring
            // orbit is what lets you swing the view round to the side and see past yourself:
            // looking up pulls the camera in tight and high, looking down drops it low.
            var follow = vcamGo.AddComponent<CinemachineOrbitalFollow>();
            follow.OrbitStyle = CinemachineOrbitalFollow.OrbitStyles.ThreeRing;
            follow.Orbits = new Cinemachine3OrbitRig.Settings
            {
                Top    = new Cinemachine3OrbitRig.Orbit { Height =  4.2f, Radius = 4.6f },
                Center = new Cinemachine3OrbitRig.Orbit { Height =  1.1f, Radius = 5.4f },
                Bottom = new Cinemachine3OrbitRig.Orbit { Height = -1.4f, Radius = 4.4f },
            };
            // Free 360 yaw; pitch clamped so you can't roll over the top or under the floor.
            follow.HorizontalAxis.Range = new Vector2(-180f, 180f);
            follow.HorizontalAxis.Wrap = true;
            follow.HorizontalAxis.Center = 0f;
            follow.VerticalAxis.Range = new Vector2(-35f, 65f);
            follow.VerticalAxis.Value = 12f;
            follow.VerticalAxis.Wrap = false;
            follow.RadialAxis.Range = new Vector2(1f, 1f);   // fixed distance; no zoom

            follow.TrackerSettings.PositionDamping = new Vector3(0.35f, 0.5f, 0.35f);
            follow.TrackerSettings.RotationDamping = new Vector3(0.3f, 0.3f, 0.3f);
            follow.TrackerSettings.QuaternionDamping = 0.3f;
            follow.TargetOffset = new Vector3(0.55f, 0f, 0f);   // over the right shoulder

            var aim = vcamGo.AddComponent<CinemachineRotationComposer>();
            aim.Composition.ScreenPosition = new Vector2(0.0f, 0.06f);
            aim.Composition.DeadZone.Enabled = true;
            aim.Composition.DeadZone.Size = new Vector2(0.12f, 0.12f);
            aim.Damping = new Vector2(0.25f, 0.25f);

            // Handheld drift: tiny, but it's the difference between "camera" and "operator".
            var noise = vcamGo.AddComponent<CinemachineBasicMultiChannelPerlin>();
            var noiseProfile = AssetDatabase.LoadAssetAtPath<NoiseSettings>(
                "Packages/com.unity.cinemachine/Presets/Noise/Handheld_normal_mild.asset");
            if (noiseProfile != null) noise.NoiseProfile = noiseProfile;
            noise.AmplitudeGain = 0.28f;
            noise.FrequencyGain = 0.22f;

            // Don't let the camera clip through cliffs.
            var decollider = vcamGo.AddComponent<CinemachineDeoccluder>();
            decollider.AvoidObstacles.Enabled = true;
            decollider.AvoidObstacles.DistanceLimit = 6f;
            decollider.AvoidObstacles.CameraRadius = 0.28f;
            decollider.AvoidObstacles.SmoothingTime = 0.35f;
            decollider.AvoidObstacles.Damping = 0.45f;

            // Bind the orbit axes to the Look action (mouse delta / right stick).
            // Without this the rig exists but nothing drives it, and the camera is rigid.
            var input = vcamGo.AddComponent<CinemachineInputAxisController>();
            input.AutoEnableInputs = true;

            var actions = AssetDatabase.LoadAssetAtPath<UnityEngine.InputSystem.InputActionAsset>(
                "Assets/InputSystem_Actions.inputactions");
            var lookAction = actions.FindAction("Player/Look");
            var lookRef = UnityEngine.InputSystem.InputActionReference.Create(lookAction);

            input.SynchronizeControllers();
            foreach (var c in input.Controllers)
            {
                // The radial ("Orbit Scale") axis is a zoom. Leaving it bound to Look means
                // the camera dollies in and out every time you move the mouse. Kill it.
                if (c.Name.Contains("Scale"))
                {
                    c.Input.InputAction = null;
                    c.Input.Gain = 0f;
                    c.Enabled = false;
                    continue;
                }

                c.Input.InputAction = lookRef;
                c.Enabled = true;
                // Look X is yaw, Look Y is pitch. Pitch is inverted so pushing up looks up.
                bool isPitch = c.Name.EndsWith("Y") || c.Name.Contains("Vertical");
                c.Input.Gain = isPitch ? -1f : 1f;
                c.Driver.AccelTime = 0.08f;
                c.Driver.DecelTime = 0.08f;
            }

            // Sensitivity lives on the axis itself.
            follow.HorizontalAxis.Recentering.Enabled = false;
            follow.VerticalAxis.Recentering.Enabled = false;

            vcamGo.AddComponent<CameraRig>();   // cursor lock + sensitivity

            // Wire the controller's camera reference to the real camera.
            var tpc = player.GetComponent<ThirdPersonController>();
            var so = new SerializedObject(tpc);
            so.FindProperty("cameraTransform").objectReferenceValue = camGo.transform;
            so.ApplyModifiedProperties();
        }

        // ---------------------------------------------------------------- dressing

        /// <summary>
        /// Scatters the photoscanned props. Not uniform noise — vegetation clusters on
        /// shelter and gentle ground, rocks collect on slope breaks, and the hero cliff
        /// is hand-placed on the ridge as the thing your eye goes to.
        /// </summary>
        /// <summary>
        /// Light probes across the playable area. The props aren't lightmapped, so this is
        /// what gives them grounded, position-varying ambient instead of one flat sky colour.
        /// </summary>
        private static void BuildLightProbes(Terrain terrain)
        {
            var go = new GameObject("Light Probe Group");
            var group = go.AddComponent<LightProbeGroup>();

            var positions = new List<Vector3>();
            const int grid = 7;
            float span = EnvironmentBuilder.TerrainSize * 0.45f;
            for (int x = 0; x < grid; x++)
            {
                for (int z = 0; z < grid; z++)
                {
                    float wx = Mathf.Lerp(-span, span, x / (float)(grid - 1));
                    float wz = Mathf.Lerp(-span, span, z / (float)(grid - 1));
                    float ground = terrain.SampleHeight(new Vector3(wx, 0f, wz));
                    // one near the ground, one at head height, one above — enough to catch
                    // the vertical gradient from shadowed ground to open sky
                    positions.Add(new Vector3(wx, ground + 0.5f, wz));
                    positions.Add(new Vector3(wx, ground + 3f, wz));
                    positions.Add(new Vector3(wx, ground + 9f, wz));
                }
            }
            group.probePositions = positions.ToArray();
        }

        /// <summary>
        /// Real measured sizes drive the scales here. The cliff is 87m long in its own
        /// right, so it needs a scale near 1 — not 2.4, which turned it into a 200m slab.
        /// Player spawns around (20, -60) looking north toward the ridge.
        /// </summary>
        private static void DressScene(Terrain terrain)
        {
            var root = new GameObject("Environment").transform;
            Random.InitState(20260713);

            // --- focal point: the escarpment crowning the north ridge (87m long, 11m tall) ---
            // bury is now in METRES, not a fraction of height.
            PlaceHero(root, terrain, "coastal_cliff_04", new Vector3(-30f, 0f, 105f), 1.35f, 3.0f, 8f);
            // A second outcrop offset to the east, so the skyline has two reads, not one.
            PlaceHero(root, terrain, "coastal_cliff_02", new Vector3(76f, 0f, 86f), 1.15f, 2.5f, -35f);
            // Low slab shelf on the mid-buttress — leads the eye up toward the cliff.
            PlaceHero(root, terrain, "coast_land_rocks_02", new Vector3(-4f, 0f, 46f), 1.6f, 0.6f, 22f);

            // --- trees: photoscans, so few and deliberate. These are the silhouettes. ---
            // These photoscans are ~1.6M triangles each, but they now carry generated LOD
            // chains that the GPU Resident Drawer actually honours, so distant ones collapse
            // to a fraction of that. Keep them as hero silhouettes rather than forest fill.
            // Exactly three trees — hand-placed as framing silhouettes, kept clear of the
            // central training ground so they never block the shooting sightline.
            PlaceHero(root, terrain, "island_tree_01", new Vector3(-48f, 0f, 30f),  1.7f, 0.05f, 40f);
            PlaceHero(root, terrain, "island_tree_02", new Vector3( 58f, 0f, -20f), 1.8f, 0.05f, 200f);
            PlaceHero(root, terrain, "island_tree_01", new Vector3( 30f, 0f, 62f),  1.6f, 0.05f, 120f);

            // --- rock: scattered widely, bedded into slope breaks ---
            // Boulders are cheap (66k tris) so they carry the density and do most of the work
            // of making the ground read as real. coast_land_rocks_02 is 1.14M tris EACH — a
            // handful only, or it alone eats the frame budget.
            ScatterCluster(root, terrain, "boulder_01",          new Vector2(  0f,  30f), 95f, 70, 0.6f, 3.0f, maxSlope: 44f);
            ScatterCluster(root, terrain, "boulder_01",          new Vector2( 15f, -55f), 55f, 30, 0.5f, 2.2f, maxSlope: 40f);
            ScatterCluster(root, terrain, "coast_land_rocks_02", new Vector2(-45f,  55f), 70f, 14, 0.6f, 1.5f, maxSlope: 46f);
            ScatterCluster(root, terrain, "dead_tree_trunk",     new Vector2(  5f, -30f), 60f,  8, 1.0f, 1.8f, maxSlope: 26f);

            // --- ground cover: cheap, so this is where density comes from. Heaviest
            //     around the player's basin, where it actually gets seen up close. ---
            ScatterCluster(root, terrain, "fern_02",         new Vector2( 18f, -58f), 45f, 260, 0.9f, 1.7f, maxSlope: 30f);
            ScatterCluster(root, terrain, "fern_02",         new Vector2(-35f, -20f), 50f, 180, 0.8f, 1.6f, maxSlope: 30f);
            ScatterCluster(root, terrain, "fern_02",         new Vector2( 55f,  15f), 45f, 140, 0.8f, 1.5f, maxSlope: 30f);
            ScatterCluster(root, terrain, "grass_medium_01", new Vector2( 20f, -55f), 60f, 420, 0.8f, 1.8f, maxSlope: 28f);
            ScatterCluster(root, terrain, "grass_medium_01", new Vector2(-30f,  15f), 75f, 380, 0.7f, 1.6f, maxSlope: 28f);
            ScatterCluster(root, terrain, "grass_medium_01", new Vector2( 60f,  40f), 60f, 260, 0.7f, 1.5f, maxSlope: 28f);
        }

        // ---------------------------------------------------------------- training ground

        private static readonly Vector2 GroundCenter = new Vector2(20f, -60f);   // == player spawn
        private const float GroundRadius = 14f;

        /// <summary>
        /// Flattens a disc of terrain at the centre and builds the shooting range on it:
        /// a table with the gun on top, and a target 12m downrange for the player to fire at.
        /// Flattening first means the table and target sit level and the player spawns clear.
        /// </summary>
        private static void BuildTrainingGround(Terrain terrain)
        {
            FlattenTerrainDisc(terrain, GroundCenter, GroundRadius);

            var root = new GameObject("TrainingGround").transform;
            float groundY = terrain.SampleHeight(new Vector3(GroundCenter.x, 0f, GroundCenter.y));

            // Table right in front of spawn so the gun is grab-able immediately.
            var tablePos = new Vector3(GroundCenter.x + 1.2f, groundY, GroundCenter.y + 1.0f);
            var table = BuildTable(root, tablePos);

            // Gun on the tabletop, ready to pick up.
            BuildGunPickup(root, new Vector3(tablePos.x, tablePos.y + 0.92f, tablePos.z), -25f);

            // Target 12m north (downrange), facing back toward the player.
            BuildTarget(root, new Vector3(GroundCenter.x, groundY, GroundCenter.y + 12f));
        }

        /// <summary>Levels a circle of terrain to its centre height, with a soft rim so it blends.</summary>
        private static void FlattenTerrainDisc(Terrain terrain, Vector2 worldCenter, float radius)
        {
            var data = terrain.terrainData;
            int res = data.heightmapResolution;
            Vector3 size = data.size;
            Vector3 tPos = terrain.transform.position;

            // world -> normalised terrain coords
            float cx = (worldCenter.x - tPos.x) / size.x;
            float cz = (worldCenter.y - tPos.z) / size.z;
            float targetH = data.GetInterpolatedHeight(cx, cz) / size.y;   // normalised height to flatten to

            float rNorm = radius / size.x;
            int minX = Mathf.Clamp(Mathf.FloorToInt((cx - rNorm * 1.6f) * res), 0, res - 1);
            int maxX = Mathf.Clamp(Mathf.CeilToInt((cx + rNorm * 1.6f) * res), 0, res - 1);
            int minZ = Mathf.Clamp(Mathf.FloorToInt((cz - rNorm * 1.6f) * res), 0, res - 1);
            int maxZ = Mathf.Clamp(Mathf.CeilToInt((cz + rNorm * 1.6f) * res), 0, res - 1);

            var heights = data.GetHeights(minX, minZ, maxX - minX + 1, maxZ - minZ + 1);
            for (int z = minZ; z <= maxZ; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    float u = x / (float)(res - 1);
                    float v = z / (float)(res - 1);
                    float dist = Mathf.Sqrt((u - cx) * (u - cx) + (v - cz) * (v - cz)) / rNorm;
                    if (dist >= 1.6f) continue;

                    // fully flat inside the radius, smoothly ramping back to natural terrain by 1.6r
                    float blend = 1f - Mathf.SmoothStep(1f, 1.6f, dist);
                    float cur = heights[z - minZ, x - minX];
                    heights[z - minZ, x - minX] = Mathf.Lerp(cur, targetH, blend);
                }
            }
            data.SetHeights(minX, minZ, heights);
        }

        // ---------------------------------------------------------------- range props

        private static readonly Color WoodDark = new Color(0.32f, 0.22f, 0.13f);
        private static readonly Color WoodLight = new Color(0.55f, 0.40f, 0.24f);

        private static Material FlatMat(string name, Color c, float smoothness = 0.15f)
        {
            string path = $"Assets/_Project/Weapons/M_{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(m, path);
            }
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Smoothness", smoothness);
            m.SetFloat("_Metallic", 0f);
            EditorUtility.SetDirty(m);
            return m;
        }

        /// <summary>A plain wooden table: a top slab on four legs. Built from boxes, sized for a person.</summary>
        private static GameObject BuildTable(Transform parent, Vector3 basePos)
        {
            var table = new GameObject("RangeTable");
            table.transform.SetParent(parent, false);
            table.transform.position = basePos;

            var topMat = FlatMat("TableTop", WoodLight);
            var legMat = FlatMat("TableLeg", WoodDark);

            const float w = 1.4f, d = 0.8f, h = 0.9f, thick = 0.08f, leg = 0.09f;

            var top = GameObject.CreatePrimitive(PrimitiveType.Cube);
            top.name = "Top";
            top.transform.SetParent(table.transform, false);
            top.transform.localPosition = new Vector3(0f, h, 0f);
            top.transform.localScale = new Vector3(w, thick, d);
            top.GetComponent<Renderer>().sharedMaterial = topMat;

            float lx = w * 0.5f - leg, lz = d * 0.5f - leg;
            var corners = new[] { new Vector2(lx, lz), new Vector2(-lx, lz), new Vector2(lx, -lz), new Vector2(-lx, -lz) };
            foreach (var c in corners)
            {
                var l = GameObject.CreatePrimitive(PrimitiveType.Cube);
                l.name = "Leg";
                l.transform.SetParent(table.transform, false);
                l.transform.localPosition = new Vector3(c.x, (h - thick * 0.5f) * 0.5f, c.y);
                l.transform.localScale = new Vector3(leg, h - thick, leg);
                l.GetComponent<Renderer>().sharedMaterial = legMat;
            }
            return table;
        }

        /// <summary>The pickup: the Kenney blaster with a GunPickup trigger the player walks into.</summary>
        private static void BuildGunPickup(Transform parent, Vector3 pos, float yaw)
        {
            var src = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Weapons/blaster-b.fbx");
            if (src == null) { Debug.LogWarning("[Wanderer] blaster-b.fbx missing"); return; }

            var gun = (GameObject)PrefabUtility.InstantiatePrefab(src);
            gun.name = "GunPickup";
            gun.transform.SetParent(parent, false);
            gun.transform.position = pos;
            gun.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            gun.transform.localScale = Vector3.one;

            var mat = FlatMat("Blaster", new Color(0.20f, 0.22f, 0.26f), 0.55f);
            foreach (var r in gun.GetComponentsInChildren<Renderer>()) r.sharedMaterial = mat;

            // a trigger so the player can detect and pick it up
            var trigger = gun.AddComponent<SphereCollider>();
            trigger.isTrigger = true;
            trigger.radius = 1.6f;
            gun.AddComponent<Wanderer.GunPickup>();
            gun.tag = "Untagged";
        }

        /// <summary>A round shooting target: concentric rings on a post, facing the player.</summary>
        private static void BuildTarget(Transform parent, Vector3 pos)
        {
            var target = new GameObject("Target");
            target.transform.SetParent(parent, false);
            target.transform.position = pos;
            target.transform.rotation = Quaternion.Euler(0f, 180f, 0f);   // face back toward spawn

            // post
            var post = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            post.name = "Post";
            post.transform.SetParent(target.transform, false);
            post.transform.localPosition = new Vector3(0f, 0.75f, 0f);
            post.transform.localScale = new Vector3(0.12f, 0.75f, 0.12f);
            post.GetComponent<Renderer>().sharedMaterial = FlatMat("TargetPost", WoodDark);

            // face: stacked discs from large (outer) to small (bull), each slightly proud of the last
            var ringColors = new[] {
                new Color(0.95f, 0.95f, 0.92f),  // white
                new Color(0.15f, 0.35f, 0.75f),  // blue
                new Color(0.90f, 0.20f, 0.20f),  // red
                new Color(0.98f, 0.85f, 0.20f),  // yellow bull
            };
            float[] radii = { 0.5f, 0.36f, 0.22f, 0.10f };
            var face = new GameObject("Face");
            face.transform.SetParent(target.transform, false);
            face.transform.localPosition = new Vector3(0f, 1.7f, 0f);
            for (int i = 0; i < radii.Length; i++)
            {
                var ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                ring.name = i == radii.Length - 1 ? "Bull" : "Ring" + i;
                ring.transform.SetParent(face.transform, false);
                ring.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);   // flat disc facing forward
                ring.transform.localPosition = new Vector3(0f, 0f, i * 0.012f);
                ring.transform.localScale = new Vector3(radii[i] * 2f, 0.02f, radii[i] * 2f);
                ring.GetComponent<Renderer>().sharedMaterial = FlatMat("TargetRing" + i, ringColors[i]);
                Object.DestroyImmediate(ring.GetComponent<Collider>());   // one collider on the parent instead
            }

            // single box collider over the whole face so raycasts register a "hit"
            var hit = target.AddComponent<BoxCollider>();
            hit.center = new Vector3(0f, 1.7f, 0f);
            hit.size = new Vector3(1.1f, 1.1f, 0.15f);
            target.AddComponent<Wanderer.ShootingTarget>();
        }

        private static GameObject LoadModel(string slug) =>
            AssetDatabase.LoadAssetAtPath<GameObject>($"{EnvDir}/Models/{slug}/{slug}.fbx");

        /// <summary>
        /// Seat an object into the ground using its real bounds. Guessing a "sink" offset is
        /// what left the hero cliff hanging in mid-air — measure instead.
        /// <paramref name="bury"/> is the fraction of the object's height pushed below the surface.
        /// </summary>
        private static void GroundObject(GameObject go, Terrain terrain, Vector3 pos, float bury,
                                         bool seatOnFootprint = true)
        {
            go.transform.position = pos;

            var rends = go.GetComponentsInChildren<Renderer>();
            if (rends.Length == 0) return;

            var bounds = rends[0].bounds;
            foreach (var r in rends) bounds.Encapsulate(r.bounds);

            float ground;
            if (seatOnFootprint)
            {
                // Rocks and cliffs: sample across the footprint and sit on the LOW point, so
                // they bed into a slope instead of hovering over their downhill side.
                ground = float.MaxValue;
                for (int i = 0; i < 5; i++)
                {
                    for (int j = 0; j < 5; j++)
                    {
                        float sx = Mathf.Lerp(bounds.min.x, bounds.max.x, i / 4f);
                        float sz = Mathf.Lerp(bounds.min.z, bounds.max.z, j / 4f);
                        ground = Mathf.Min(ground, terrain.SampleHeight(new Vector3(sx, 0f, sz)));
                    }
                }
            }
            else
            {
                // Trees and plants: a canopy is metres wider than the trunk, so a footprint
                // sample on a slope drags the whole tree down (we were burying them by 2.7m).
                // A trunk only ever meets the ground at its own position.
                ground = terrain.SampleHeight(pos);
            }

            // where the base sits relative to the pivot
            float baseOffset = bounds.min.y - go.transform.position.y;
            float target = ground - bury;
            go.transform.position = new Vector3(pos.x, target - baseOffset, pos.z);
        }

        private static void PlaceHero(Transform root, Terrain terrain, string slug,
                                      Vector3 pos, float scale, float bury, float yaw)
        {
            var src = LoadModel(slug);
            if (src == null) { Debug.LogWarning($"[Wanderer] missing model {slug}"); return; }

            var go = (GameObject)PrefabUtility.InstantiatePrefab(src);
            go.transform.SetParent(root, false);
            // Poly Haven FBXs bake their axis/unit conversion into the root transform
            // (a 270deg X rotation for Z-up -> Y-up, and often a 100x scale). COMPOSE with
            // those — assigning rotation/scale outright throws the conversion away and the
            // model ends up on its head or microscopic.
            go.transform.rotation = Quaternion.Euler(0f, yaw, 0f) * src.transform.rotation;
            go.transform.localScale = Vector3.Scale(src.transform.localScale, Vector3.one * scale);
            GroundObject(go, terrain, pos, bury, seatOnFootprint: !slug.Contains("tree"));

            // Trees get a slim trunk capsule; everything else its mesh collider.
            if (slug.Contains("tree") && !slug.Contains("dead_tree")) AddTrunkCollider(go);
            else AddCollider(go);
            // Deliberately NOT ContributeGI: these are million-vert photoscans with no
            // lightmap UVs (unwrapping them fails and takes forever). They read ambient
            // from the HDRI + light probes, which for a dynamic sun is the right call anyway.
            GameObjectUtility.SetStaticEditorFlags(go,
                StaticEditorFlags.OccluderStatic | StaticEditorFlags.BatchingStatic);
        }

        private static void ScatterCluster(Transform root, Terrain terrain, string slug,
                                           Vector2 center, float radius, int count,
                                           float minScale, float maxScale, float maxSlope)
        {
            var src = LoadModel(slug);
            if (src == null) { Debug.LogWarning($"[Wanderer] missing model {slug}"); return; }

            var group = new GameObject(slug + "_Cluster").transform;
            group.SetParent(root, false);

            int placed = 0, guard = 0;
            while (placed < count && guard++ < count * 25)
            {
                // Bias toward the cluster centre so groups read as groves, not confetti.
                float r = radius * Mathf.Pow(Random.value, 0.65f);
                float a = Random.value * Mathf.PI * 2f;
                var p = new Vector3(center.x + Mathf.Cos(a) * r, 0f, center.y + Mathf.Sin(a) * r);

                float half = EnvironmentBuilder.TerrainSize * 0.5f - 8f;
                if (Mathf.Abs(p.x) > half || Mathf.Abs(p.z) > half) continue;

                // Respect the ground: nothing grows on a cliff face.
                var norm = SampleNormal(terrain, p);
                if (Vector3.Angle(norm, Vector3.up) > maxSlope) continue;

                var go = (GameObject)PrefabUtility.InstantiatePrefab(src);
                go.transform.SetParent(group, false);

                // Poly Haven ships plant variants as siblings laid out in a row inside one
                // FBX. Instantiating the whole file drops the entire row at every point, so
                // keep exactly one variant and re-centre it on the pivot.
                KeepOneVariant(go);

                // Compose the random yaw with the FBX root's baked axis conversion — see
                // PlaceHero. Overwriting it is what left the trees upside-down.
                go.transform.rotation =
                    Quaternion.Euler(0f, Random.value * 360f, 0f) * src.transform.rotation;
                go.transform.localScale =
                    Vector3.Scale(src.transform.localScale, Vector3.one * Random.Range(minScale, maxScale));

                // Rocks bed into the ground (footprint-seated). Trees and plants sit ON it,
                // seated at the trunk — see GroundObject.
                bool isRock = slug.Contains("boulder") || slug.Contains("rocks") || slug.Contains("dead_tree");
                bool isTree = slug.Contains("tree") && !slug.Contains("dead_tree");
                GroundObject(go, terrain, p,
                    bury: isRock ? 0.25f : 0.08f,
                    seatOnFootprint: isRock);

                if (isTree) AddTrunkCollider(go);

                if (slug.Contains("boulder") || slug.Contains("rocks")) AddCollider(go);
                GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic);
                placed++;
            }
        }

        /// <summary>
        /// If a model is a set of variants (fern_a..d, several grass tufts), keep one at
        /// random and drop the rest, then slide it onto the parent's pivot so it lands where
        /// we asked. Objects with a real LOD chain (the cliffs) are left untouched.
        /// </summary>
        private static void KeepOneVariant(GameObject go)
        {
            if (go.GetComponentInChildren<LODGroup>() != null) return;

            var parts = new List<Transform>();
            foreach (Transform child in go.transform)
                if (child.GetComponent<MeshFilter>() != null) parts.Add(child);

            if (parts.Count < 2) return;

            // Children of a prefab instance can't be deleted while the link is intact.
            if (PrefabUtility.IsPartOfPrefabInstance(go))
                PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);

            parts.Clear();
            foreach (Transform child in go.transform)
                if (child.GetComponent<MeshFilter>() != null) parts.Add(child);

            var keep = parts[Random.Range(0, parts.Count)];
            foreach (var part in parts)
                if (part != keep) Object.DestroyImmediate(part.gameObject);

            keep.localPosition = Vector3.zero;   // variants are offset along the row; re-centre
        }

        private static Vector3 SampleNormal(Terrain t, Vector3 world)
        {
            var d = t.terrainData;
            var local = world - t.transform.position;
            return d.GetInterpolatedNormal(
                Mathf.Clamp01(local.x / d.size.x),
                Mathf.Clamp01(local.z / d.size.z));
        }

        /// <summary>
        /// Blocks the player at a tree's trunk with a capsule.
        ///
        /// A MeshCollider is wrong here twice over: the tree is ~1.6M triangles (a very
        /// expensive cook), and colliding against the canopy would stop you in mid-air on
        /// leaves. We want the trunk only — so measure the trunk's real radius from the
        /// vertices near the base, because the mesh bounds are all canopy.
        /// </summary>
        private static void AddTrunkCollider(GameObject go)
        {
            var rends = go.GetComponentsInChildren<Renderer>();
            if (rends.Length == 0) return;

            var bounds = rends[0].bounds;
            foreach (var r in rends) bounds.Encapsulate(r.bounds);

            var mf = go.GetComponentInChildren<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return;

            // Sample the lowest slice of the mesh — that's trunk, not foliage.
            var xf = mf.transform.localToWorldMatrix;
            var verts = mf.sharedMesh.vertices;
            float baseY = bounds.min.y;
            float sliceTop = baseY + Mathf.Max(0.35f, bounds.size.y * 0.10f);

            var centre = Vector2.zero;
            int n = 0;
            foreach (var v in verts)
            {
                var w = xf.MultiplyPoint3x4(v);
                if (w.y > sliceTop) continue;
                centre += new Vector2(w.x, w.z);
                n++;
            }
            if (n == 0) return;
            centre /= n;

            // Take a percentile, not the max: root flare and low branches produce outlier
            // vertices that would inflate the capsule to ~1m and block the player a metre
            // away from the trunk.
            var dists = new List<float>();
            foreach (var v in verts)
            {
                var w = xf.MultiplyPoint3x4(v);
                if (w.y > sliceTop) continue;
                dists.Add(Vector2.Distance(new Vector2(w.x, w.z), centre));
            }
            if (dists.Count == 0) return;
            dists.Sort();
            float radius = dists[Mathf.Clamp(Mathf.RoundToInt(dists.Count * 0.75f), 0, dists.Count - 1)];
            radius = Mathf.Clamp(radius, 0.15f, 0.7f);

            // Put the capsule on its own child with identity rotation and unit scale.
            // CapsuleCollider.direction is an axis in LOCAL space, and these FBX roots carry a
            // baked 270deg X rotation (Z-up -> Y-up) plus a 100x scale — so a capsule added
            // straight onto the tree ends up lying on its side and never blocks anything.
            var holder = new GameObject("TrunkCollider");
            holder.transform.SetParent(go.transform.parent, worldPositionStays: false);
            holder.transform.position = new Vector3(centre.x, baseY + bounds.size.y * 0.5f, centre.y);
            holder.transform.rotation = Quaternion.identity;
            holder.transform.localScale = Vector3.one;

            var col = holder.AddComponent<CapsuleCollider>();
            col.direction = 1;               // Y — now genuinely world-up
            col.center = Vector3.zero;
            col.radius = radius;             // world units: the holder is unscaled
            col.height = bounds.size.y;
        }

        /// <summary>
        /// Collide against the cheapest LOD, never LOD0. A 2M-triangle collision mesh is
        /// both pointless and a physics warning; the coarsest LOD is the same shape to
        /// within a few centimetres, which is far below what the player can feel.
        /// </summary>
        private static void AddCollider(GameObject go)
        {
            var lodGroup = go.GetComponentInChildren<LODGroup>();
            if (lodGroup != null)
            {
                var lods = lodGroup.GetLODs();
                if (lods.Length > 0)
                {
                    foreach (var r in lods[lods.Length - 1].renderers)   // coarsest level
                    {
                        if (r == null) continue;
                        var mf = r.GetComponent<MeshFilter>();
                        if (mf == null || mf.sharedMesh == null) continue;
                        r.gameObject.AddComponent<MeshCollider>().sharedMesh = mf.sharedMesh;
                    }
                    return;
                }
            }

            foreach (var mf in go.GetComponentsInChildren<MeshFilter>())
            {
                if (mf.sharedMesh == null) continue;
                if (mf.sharedMesh.triangles.Length / 3 > 150_000) continue;   // too heavy to collide
                mf.gameObject.AddComponent<MeshCollider>().sharedMesh = mf.sharedMesh;
            }
        }
    }
}
