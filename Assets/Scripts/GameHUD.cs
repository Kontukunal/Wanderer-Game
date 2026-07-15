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
        private Texture2D dot, panel;

        private void Awake()
        {
            weapon = Object.FindFirstObjectByType<WeaponController>();

            dot = Solid(new Color(1f, 1f, 1f, 0.9f));
            panel = Solid(new Color(0f, 0f, 0f, 0.55f));
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
            GUI.DrawTexture(new Rect(12, 12, 250, 96), panel);
            GUI.Label(new Rect(24, 18, 240, 22), "WASD move · Shift run · Space jump", small);
            GUI.Label(new Rect(24, 40, 240, 22), "Mouse: aim camera", small);
            GUI.Label(new Rect(24, 62, 240, 22), "E: pick up gun", small);
            GUI.Label(new Rect(24, 84, 240, 22), "Left click: fire", small);

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
                // crosshair
                float s = 4f;
                GUI.DrawTexture(new Rect(w * 0.5f - s * 0.5f, h * 0.5f - s * 0.5f, s, s), dot);
                // status line, bottom-centre
                GUI.DrawTexture(new Rect(w * 0.5f - 130f, h - 46f, 260f, 30f), panel);
                GUI.Label(new Rect(w * 0.5f - 130f, h - 46f, 260f, 30f),
                    "Armed  ·  Left click to fire  ·  Hits: " + weapon.TargetHits, center);
            }
        }
    }
}
