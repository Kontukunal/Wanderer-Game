using UnityEngine;

namespace Wanderer
{
    /// <summary>
    /// Minimal on-screen HUD drawn with IMGUI (no Canvas needed, so it can't be broken by a
    /// missing EventSystem or misconfigured UI). Shows a crosshair, the pickup prompt when the
    /// player is near a gun, and the current weapon/hit state — so it's always clear what to
    /// press and whether it registered.
    /// </summary>
    public class GameHUD : MonoBehaviour
    {
        private WeaponController weapon;
        private GUIStyle prompt, small, center;
        private Texture2D dot, hitDot, panel, white;

        private void Awake()
        {
            weapon = Object.FindFirstObjectByType<WeaponController>();

            dot = Solid(new Color(1f, 1f, 1f, 0.9f));
            hitDot = Solid(new Color(1f, 0.3f, 0.2f, 1f));
            panel = Solid(new Color(0f, 0f, 0f, 0.55f));
            white = Solid(Color.white);   // tinted per-draw via GUI.color for the scope overlay
        }

        private static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        private void EnsureStyles()
        {
            if (prompt != null) return;
            prompt = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            prompt.normal.textColor = Color.white;
            small = new GUIStyle(GUI.skin.label) { fontSize = 15, alignment = TextAnchor.MiddleLeft };
            small.normal.textColor = new Color(0.9f, 0.9f, 0.9f);
            center = new GUIStyle(GUI.skin.label) { fontSize = 15, alignment = TextAnchor.MiddleCenter };
            center.normal.textColor = Color.white;
        }

        private void OnGUI()
        {
            EnsureStyles();
            float w = Screen.width, h = Screen.height;

            // controls panel, top-left
            GUI.DrawTexture(new Rect(12, 12, 250, 140), panel);
            GUI.Label(new Rect(24, 18, 240, 22), "WASD move · Shift run · Space jump", small);
            GUI.Label(new Rect(24, 40, 240, 22), "Mouse: aim camera", small);
            GUI.Label(new Rect(24, 62, 240, 22), "E: pick up gun", small);
            GUI.Label(new Rect(24, 84, 240, 22), "Left click: fire", small);
            GUI.Label(new Rect(24, 106, 240, 22), "Right click: aim / scope", small);
            GUI.Label(new Rect(24, 128, 240, 22), "Hunt the sheep — they run!", small);

            if (weapon == null) return;

            // debug readout, top-right — lets us see exactly why a pickup did/didn't fire
            GUI.DrawTexture(new Rect(w - 262, 12, 250, 66), panel);
            GUI.Label(new Rect(w - 250, 16, 240, 20),
                string.Format("gun distance: {0:F2} m", weapon.DistanceToGun), small);
            GUI.Label(new Rect(w - 250, 36, 240, 20),
                "in range: " + weapon.GunInRange + (weapon.HasGun ? " (equipped)" : ""), small);
            GUI.Label(new Rect(w - 250, 56, 240, 20),
                "shots fired: " + weapon.ShotsFired, small);

            if (!weapon.HasGun)
            {
                // crosshair off; show pickup prompt when in range
                if (weapon.GunInRange)
                {
                    GUI.DrawTexture(new Rect(w * 0.5f - 150f, h * 0.72f, 300f, 40f), panel);
                    GUI.Label(new Rect(w * 0.5f - 150f, h * 0.72f, 300f, 40f), "Press  [ E ]  to pick up the gun", prompt);
                }
            }
            else
            {
                float blend = weapon.AimBlend;
                if (blend > 0.02f) DrawScope(w, h, blend);   // scoped-in tunnel + reticle

                // crosshair — flashes into a red hit-marker just after a sheep is hit
                bool marker = Time.time - weapon.LastSheepHitTime < 0.15f;
                if (marker)
                {
                    // little X
                    foreach (var o in new[] { -6f, 6f })
                    {
                        GUI.DrawTexture(new Rect(w * 0.5f + o - 5f, h * 0.5f - 1f, 10f, 2f), hitDot);
                        GUI.DrawTexture(new Rect(w * 0.5f - 1f, h * 0.5f + o - 5f, 2f, 10f), hitDot);
                    }
                }
                else if (blend <= 0.02f)
                {
                    float s = 4f;
                    GUI.DrawTexture(new Rect(w * 0.5f - s * 0.5f, h * 0.5f - s * 0.5f, s, s), dot);
                }
                // status line, bottom-centre
                GUI.DrawTexture(new Rect(w * 0.5f - 150f, h - 46f, 300f, 30f), panel);
                GUI.Label(new Rect(w * 0.5f - 150f, h - 46f, 300f, 30f),
                    "Armed  ·  L-click fire  ·  R-click aim", center);
            }
        }

        /// <summary>
        /// The scoped-in overlay: a dark "tunnel" that closes in as the zoom builds, plus a fine
        /// precision reticle. Everything is tinted through <see cref="white"/> via GUI.color, so a
        /// single 1x1 texture draws both the shade and the crosshair. <paramref name="blend"/> is
        /// 0..1 from the weapon, so the scope fades in and out with the zoom rather than popping.
        /// </summary>
        private void DrawScope(float w, float h, float blend)
        {
            float cx = w * 0.5f, cy = h * 0.5f;

            // tunnel vignette: four dark bars framing a central clear box that shrinks as we zoom
            float hole = Mathf.Lerp(h * 0.95f, h * 0.5f, blend);
            float half = hole * 0.5f;
            GUI.color = new Color(0f, 0f, 0f, 0.55f * blend);
            GUI.DrawTexture(new Rect(0, 0, w, cy - half), white);                       // top
            GUI.DrawTexture(new Rect(0, cy + half, w, h - (cy + half)), white);         // bottom
            GUI.DrawTexture(new Rect(0, cy - half, cx - half, hole), white);            // left
            GUI.DrawTexture(new Rect(cx + half, cy - half, w - (cx + half), hole), white); // right

            // precision reticle: four arms with a centre gap + a centre dot
            GUI.color = new Color(1f, 1f, 1f, blend);
            const float gap = 7f, len = 16f, th = 2f;
            GUI.DrawTexture(new Rect(cx - gap - len, cy - th * 0.5f, len, th), white);  // left arm
            GUI.DrawTexture(new Rect(cx + gap, cy - th * 0.5f, len, th), white);        // right arm
            GUI.DrawTexture(new Rect(cx - th * 0.5f, cy - gap - len, th, len), white);  // top arm
            GUI.DrawTexture(new Rect(cx - th * 0.5f, cy + gap, th, len), white);        // bottom arm
            GUI.DrawTexture(new Rect(cx - 1.5f, cy - 1.5f, 3f, 3f), white);             // centre dot

            GUI.color = Color.white;
        }
    }
}
