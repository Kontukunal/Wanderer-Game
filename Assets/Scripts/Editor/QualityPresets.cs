using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Wanderer.EditorTools
{
    /// <summary>
    /// Three quality presets, under Wanderer ▸ Quality.
    ///
    /// This scene is built from photoscans (a single tree is ~1.6M triangles), so realism
    /// and framerate genuinely trade against each other. Rather than bake in one compromise,
    /// pick the one that suits what you're doing: Cinematic to look at it, Balanced to play
    /// it, Performance to iterate quickly.
    ///
    /// Two things are ALWAYS on because they cost nothing visually:
    ///   - GPU Resident Drawer: without it Unity ignores the mesh LODs entirely and every
    ///     tree draws at full 1.6M tris at any distance.
    ///   - MSAA off: with a post-processing stack it does almost nothing, and SMAA handles AA.
    /// </summary>
    public static class QualityPresets
    {
        [MenuItem("Wanderer/Quality/Cinematic (best looking)", priority = 20)]
        public static void Cinematic() => Apply(
            name: "Cinematic",
            shadowDistance: 150f, cascades: 4, shadowRes: 4096,
            lodBias: 1.2f, terrainError: 4f,
            aoDownsample: false, aoHigh: true,
            groundCoverShadows: true, renderScale: 1.0f);

        [MenuItem("Wanderer/Quality/Balanced (default)", priority = 21)]
        public static void Balanced() => Apply(
            name: "Balanced",
            shadowDistance: 110f, cascades: 2, shadowRes: 2048,
            lodBias: 0.85f, terrainError: 6f,
            aoDownsample: true, aoHigh: true,
            groundCoverShadows: true, renderScale: 0.85f);

        [MenuItem("Wanderer/Quality/Performance (smoothest)", priority = 22)]
        public static void Performance() => Apply(
            name: "Performance",
            shadowDistance: 70f, cascades: 2, shadowRes: 1024,
            lodBias: 0.5f, terrainError: 14f,
            aoDownsample: true, aoHigh: false,
            groundCoverShadows: false, renderScale: 0.7f);

        private static void Apply(string name, float shadowDistance, int cascades, int shadowRes,
                                  float lodBias, float terrainError, bool aoDownsample, bool aoHigh,
                                  bool groundCoverShadows, float renderScale)
        {
            var rp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (rp == null) { Debug.LogError("[Wanderer] URP asset not found."); return; }

            // Always on: the mesh LODs we generated are only honoured by the resident drawer.
            rp.gpuResidentDrawerMode = GPUResidentDrawerMode.InstancedDrawing;
            rp.gpuResidentDrawerEnableOcclusionCullingInCameras = true;

            var so = new SerializedObject(rp);
            so.FindProperty("m_ShadowDistance").floatValue = shadowDistance;
            so.FindProperty("m_ShadowCascadeCount").intValue = cascades;
            so.FindProperty("m_MainLightShadowmapResolution").intValue = shadowRes;
            so.FindProperty("m_SoftShadowsSupported").boolValue = true;
            so.FindProperty("m_RenderScale").floatValue = renderScale;
            var msaa = so.FindProperty("m_MSAA");
            if (msaa != null) msaa.intValue = 1;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(rp);

            QualitySettings.lodBias = lodBias;

            var terrain = Object.FindFirstObjectByType<Terrain>();
            if (terrain != null)
            {
                terrain.heightmapPixelError = terrainError;
                terrain.basemapDistance = 250f;
            }

            ApplyAO(so, aoDownsample, aoHigh);
            ApplyGroundCoverShadows(groundCoverShadows);

            AssetDatabase.SaveAssets();
            Debug.Log($"[Wanderer] Quality: {name} — shadows {shadowDistance}m/{cascades}x/{shadowRes}, " +
                      $"lodBias {lodBias}, renderScale {renderScale}");
        }

        private static void ApplyAO(SerializedObject rpSo, bool downsample, bool high)
        {
            var data = rpSo.FindProperty("m_RendererDataList").GetArrayElementAtIndex(0).objectReferenceValue;
            var rdSo = new SerializedObject(data);
            var feats = rdSo.FindProperty("m_RendererFeatures");
            for (int i = 0; i < feats.arraySize; i++)
            {
                var f = feats.GetArrayElementAtIndex(i).objectReferenceValue;
                if (f == null || !f.GetType().Name.Contains("AmbientOcclusion")) continue;

                var fSo = new SerializedObject(f);
                fSo.FindProperty("m_Settings.Downsample").boolValue = downsample;
                fSo.FindProperty("m_Settings.Samples").enumValueIndex = high ? 2 : 0;
                fSo.FindProperty("m_Settings.BlurQuality").enumValueIndex = high ? 0 : 2;
                fSo.ApplyModifiedProperties();
                EditorUtility.SetDirty(f);
            }
        }

        /// <summary>1,640 grass/fern renderers — re-rendered once per shadow cascade.</summary>
        private static void ApplyGroundCoverShadows(bool on)
        {
            var env = GameObject.Find("Environment");
            if (env == null) return;

            var mode = on ? ShadowCastingMode.On : ShadowCastingMode.Off;
            foreach (Transform group in env.transform)
            {
                if (!group.name.Contains("grass") && !group.name.Contains("fern")) continue;
                foreach (var r in group.GetComponentsInChildren<Renderer>(true))
                    r.shadowCastingMode = mode;
            }
        }
    }
}
