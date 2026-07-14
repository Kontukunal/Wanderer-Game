using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Wanderer.EditorTools
{
    /// <summary>
    /// Turns Poly Haven's raw PBR maps into correct URP Lit materials.
    ///
    /// Two things here are easy to get wrong and both wreck realism:
    ///  - colour vs data textures. Only base colour is sRGB; normal/roughness are linear.
    ///  - roughness vs smoothness. URP wants smoothness; Poly Haven ships roughness.
    ///    They are inverses, so we repack as metallic(RGB)+smoothness(A).
    /// </summary>
    public static class MaterialBuilder
    {
        private const string ModelsDir = "Assets/_Project/Env/Models";

        [MenuItem("Wanderer/Build Environment Materials", priority = 1)]
        public static void BuildAll()
        {
            foreach (var dir in Directory.GetDirectories(ModelsDir))
            {
                ConfigureTextures(dir);
                ExtractMaterials(dir);
            }
            AssetDatabase.Refresh();

            foreach (var dir in Directory.GetDirectories(ModelsDir))
                BuildMaterialsFor(Path.GetFileName(dir), dir);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[Wanderer] Environment materials rebuilt.");
        }

        /// <summary>
        /// Pull the FBX's embedded materials out as editable .mat assets. Extracting rather
        /// than authoring from scratch preserves the submesh -> material mapping the FBX
        /// declares (trunk / leaves / branches), which we'd otherwise have to guess.
        /// </summary>
        private static void ExtractMaterials(string dir)
        {
            string fbx = Directory.GetFiles(dir, "*.fbx").FirstOrDefault();
            if (fbx == null) return;

            bool any = false;
            foreach (var obj in AssetDatabase.LoadAllAssetRepresentationsAtPath(fbx))
            {
                if (obj is not Material mat) continue;
                string dst = Path.Combine(dir, mat.name + ".mat");
                if (File.Exists(dst)) continue;

                string err = AssetDatabase.ExtractAsset(mat, dst);
                if (string.IsNullOrEmpty(err)) any = true;
                else Debug.LogWarning($"[Wanderer] extract failed {mat.name}: {err}");
            }
            if (any) AssetDatabase.WriteImportSettingsIfDirty(fbx);
        }

        /// <summary>Flag normal maps as normal maps; keep roughness/AO linear.</summary>
        private static void ConfigureTextures(string dir)
        {
            foreach (var path in Directory.GetFiles(dir).Where(IsTexture))
            {
                var ti = AssetImporter.GetAtPath(path) as TextureImporter;
                if (ti == null) continue;

                string n = Path.GetFileNameWithoutExtension(path).ToLower();
                bool isNormal = n.Contains("_nor");
                bool isColor = n.Contains("_diff") || n.Contains("_basecolor");

                var wanted = isNormal ? TextureImporterType.NormalMap : TextureImporterType.Default;
                bool changed = ti.textureType != wanted || ti.sRGBTexture != isColor;

                ti.textureType = wanted;
                ti.sRGBTexture = isColor;
                ti.maxTextureSize = 2048;
                if (changed) ti.SaveAndReimport();
            }
        }

        private static bool IsTexture(string p)
        {
            string e = Path.GetExtension(p).ToLower();
            return e == ".jpg" || e == ".png" || e == ".exr";
        }

        private static Texture2D Load(string dir, string contains, params string[] mustNot)
        {
            var hit = Directory.GetFiles(dir)
                .Where(IsTexture)
                .FirstOrDefault(p =>
                {
                    string n = Path.GetFileNameWithoutExtension(p).ToLower();
                    if (!n.Contains(contains)) return false;
                    if (n.Contains("_ms_") || n.Contains("cutout")) return false;   // our own generated maps
                    foreach (var bad in mustNot)
                        if (bad != null && n.Contains(bad)) return false;
                    return true;
                });
            return hit == null ? null : AssetDatabase.LoadAssetAtPath<Texture2D>(hit);
        }

        private static void BuildMaterialsFor(string slug, string dir)
        {
            // Unity extracts FBX materials next to the model. Rewrite each one in place so
            // the submesh -> material mapping the FBX declared is preserved.
            var mats = AssetDatabase.FindAssets("t:Material", new[] { dir })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<Material>)
                .Where(m => m != null);

            foreach (var mat in mats)
            {
                string mn = mat.name.ToLower();

                // Which texture set does this material want? Poly Haven names its
                // sub-materials after the part: "leaves", "branches", or the model itself.
                string part = mn.Contains("leaves") ? "leaves"
                            : mn.Contains("branch") ? "branches"
                            : null;

                Texture2D albedo, normal, rough, alpha;
                if (part == null)
                {
                    // The trunk/body material must exclude BOTH the leaf and branch maps —
                    // otherwise the trunk ends up wearing the branch texture.
                    albedo = Load(dir, "_diff", "leaves", "branches") ?? Load(dir, "_diff");
                    normal = Load(dir, "_nor", "leaves", "branches");
                    rough = Load(dir, "_rough", "leaves", "branches");
                    // ferns and grass are a single material but still ship a cutout mask
                    alpha = Load(dir, "_alpha", "leaves", "branches");
                }
                else
                {
                    albedo = Load(dir, part + "_diff");
                    normal = Load(dir, part + "_nor");
                    rough = Load(dir, part + "_rough");
                    alpha = Load(dir, part + "_alpha");
                }

                if (albedo == null) continue;

                mat.shader = Shader.Find("Universal Render Pipeline/Lit");
                mat.SetTexture("_BaseMap", albedo);
                mat.SetColor("_BaseColor", Color.white);
                mat.SetFloat("_Metallic", 0f);

                if (normal != null)
                {
                    mat.SetTexture("_BumpMap", normal);
                    mat.SetFloat("_BumpScale", 1f);
                    mat.EnableKeyword("_NORMALMAP");
                }

                if (rough != null)
                {
                    var ms = PackMetallicSmoothness(rough);
                    if (ms != null)
                    {
                        mat.SetTexture("_MetallicGlossMap", ms);
                        mat.EnableKeyword("_METALLICSPECGLOSSMAP");
                        mat.SetFloat("_Smoothness", 1f);
                        mat.SetFloat("_SmoothnessTextureChannel", 0f);
                    }
                }
                else
                {
                    mat.SetFloat("_Smoothness", 0.18f);
                }

                // Foliage: cut out the leaf cards and light them from both sides,
                // otherwise leaves read as black cardboard when backlit by a low sun.
                if (alpha != null || part == "leaves")
                {
                    var cut = alpha ?? albedo;
                    mat.SetFloat("_AlphaClip", 1f);
                    mat.EnableKeyword("_ALPHATEST_ON");
                    mat.SetFloat("_Cutoff", 0.42f);
                    mat.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
                    mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
                    if (alpha != null) mat.SetTexture("_BaseMap", MergeAlpha(albedo, alpha));
                }

                EditorUtility.SetDirty(mat);
            }
        }

        /// <summary>Roughness -> URP's metallic/smoothness map: RGB = metallic (0), A = 1 - roughness.</summary>
        private static Texture2D PackMetallicSmoothness(Texture2D rough)
        {
            string src = AssetDatabase.GetAssetPath(rough);
            string dst = Path.Combine(Path.GetDirectoryName(src),
                Path.GetFileNameWithoutExtension(src).Replace("_rough", "_ms") + ".png");

            if (File.Exists(dst)) return AssetDatabase.LoadAssetAtPath<Texture2D>(dst);

            if (!MakeReadable(src)) return null;
            var r = AssetDatabase.LoadAssetAtPath<Texture2D>(src);
            var px = r.GetPixels();
            var outPx = new Color[px.Length];
            for (int i = 0; i < px.Length; i++)
                outPx[i] = new Color(0f, 0f, 0f, 1f - px[i].r);

            WritePng(outPx, r.width, r.height, dst, srgb: false);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(dst);
        }

        /// <summary>Fold a separate alpha mask into the base map's alpha channel.</summary>
        private static Texture2D MergeAlpha(Texture2D albedo, Texture2D alpha)
        {
            string src = AssetDatabase.GetAssetPath(albedo);
            string dst = Path.Combine(Path.GetDirectoryName(src),
                Path.GetFileNameWithoutExtension(src) + "_cutout.png");

            if (File.Exists(dst)) return AssetDatabase.LoadAssetAtPath<Texture2D>(dst);

            if (!MakeReadable(src) || !MakeReadable(AssetDatabase.GetAssetPath(alpha))) return albedo;
            var a = AssetDatabase.LoadAssetAtPath<Texture2D>(src);
            var m = AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GetAssetPath(alpha));

            var ap = a.GetPixels();
            var mp = m.width == a.width && m.height == a.height
                ? m.GetPixels()
                : ResampleTo(m, a.width, a.height);

            for (int i = 0; i < ap.Length; i++) ap[i].a = mp[i].r;
            WritePng(ap, a.width, a.height, dst, srgb: true);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(dst);
        }

        private static Color[] ResampleTo(Texture2D t, int w, int h)
        {
            var outPx = new Color[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    outPx[y * w + x] = t.GetPixelBilinear(x / (float)w, y / (float)h);
            return outPx;
        }

        /// <summary>
        /// Readable is all we need to call GetPixels. Do NOT also force Uncompressed —
        /// on 2K EXR normal/roughness maps across every model that blows out memory and
        /// takes the asset importer down with it.
        /// </summary>
        private static bool MakeReadable(string path)
        {
            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null) return false;
            if (!ti.isReadable)
            {
                ti.isReadable = true;
                ti.SaveAndReimport();
            }
            return true;
        }

        private static void WritePng(Color[] px, int w, int h, string dst, bool srgb)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true, !srgb);
            tex.SetPixels(px);
            tex.Apply();
            File.WriteAllBytes(dst, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(dst);

            var ti = (TextureImporter)AssetImporter.GetAtPath(dst);
            ti.sRGBTexture = srgb;
            ti.alphaSource = TextureImporterAlphaSource.FromInput;
            ti.alphaIsTransparency = srgb;
            ti.maxTextureSize = 2048;
            ti.SaveAndReimport();
        }
    }
}
