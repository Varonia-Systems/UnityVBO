using System;
using UnityEditor;
using UnityEngine;

namespace VaroniaBackOffice
{
    [CustomEditor(typeof(VaroniaSync))]
    public class VaroniaSyncEditor : Editor
    {
        // ── Style cache ───────────────────────────────────────────────────────────
        static bool     stylesBuilt;
        static GUIStyle headerStyle, sectionStyle, footerStyle, buttonStyle, tagStyle;
        static GUIStyle fieldLabelStyle, readOnlyStyle;

        // ── Colors ────────────────────────────────────────────────────────────────
        static readonly Color colBg          = new Color(0.11f, 0.11f, 0.14f, 1f);
        static readonly Color colCard        = new Color(0.15f, 0.15f, 0.19f, 1f);
        static readonly Color colAccent      = new Color(0.30f, 0.85f, 0.65f, 1f);
        static readonly Color colAccentDim   = new Color(0.30f, 0.85f, 0.65f, 0.15f);
        static readonly Color colWarn        = new Color(1f,    0.75f, 0.30f, 1f);
        static readonly Color colWarnDim     = new Color(1f,    0.75f, 0.30f, 0.12f);
        static readonly Color colTextPrimary = new Color(0.92f, 0.92f, 0.95f, 1f);
        static readonly Color colTextSecond  = new Color(0.55f, 0.55f, 0.62f, 1f);
        static readonly Color colTextMuted   = new Color(0.40f, 0.40f, 0.47f, 1f);
        static readonly Color colDivider     = new Color(1f,    1f,    1f,    0.06f);
        static readonly Color colBtnNormal   = new Color(0.22f, 0.22f, 0.28f, 1f);
        static readonly Color colBtnHover    = new Color(0.28f, 0.28f, 0.35f, 1f);

        // ── Textures ──────────────────────────────────────────────────────────────
        static Texture2D texBtn, texBtnHover, texAccentSolid, texWarnSolid;
        static Texture2D texCard, texAccentDim, texWarnDim, texDivider;

        // ─────────────────────────────────────────────────────────────────────────

        private void OnEnable() => stylesBuilt = false;

        // ── Texture helpers ───────────────────────────────────────────────────────

        static Texture2D MakeTex(Color col)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            t.SetPixel(0, 0, col); t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }

        static Texture2D MakeRoundedTex(int w, int h, Color col, int radius)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
            Color clear = new Color(0, 0, 0, 0);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    bool inside = true;
                    if      (x < radius      && y < radius)          inside = new Vector2(x - radius,           y - radius).magnitude           <= radius;
                    else if (x >= w - radius && y < radius)          inside = new Vector2(x - (w - radius - 1), y - radius).magnitude           <= radius;
                    else if (x < radius      && y >= h - radius)     inside = new Vector2(x - radius,           y - (h - radius - 1)).magnitude <= radius;
                    else if (x >= w - radius && y >= h - radius)     inside = new Vector2(x - (w - radius - 1), y - (h - radius - 1)).magnitude <= radius;
                    t.SetPixel(x, y, inside ? col : clear);
                }
            t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }

        // ── Style builder ─────────────────────────────────────────────────────────

        void BuildStyles()
        {
            if (stylesBuilt) return;
            stylesBuilt = true;

            texCard        = MakeRoundedTex(32, 32, colCard, 6);
            texAccentDim   = MakeRoundedTex(32, 32, colAccentDim, 6);
            texWarnDim     = MakeRoundedTex(32, 32, colWarnDim, 6);
            texDivider     = MakeTex(colDivider);
            texBtn         = MakeRoundedTex(32, 32, colBtnNormal, 5);
            texBtnHover    = MakeRoundedTex(32, 32, colBtnHover, 5);
            texAccentSolid = MakeRoundedTex(32, 32, colAccent, 5);
            texWarnSolid   = MakeRoundedTex(32, 32, colWarn, 5);

            headerStyle = new GUIStyle
            {
                fontSize  = 16,
                fontStyle = FontStyle.Bold,
                normal    = { textColor = colTextPrimary },
            };

            tagStyle = new GUIStyle
            {
                fontSize  = 9,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal    = { textColor = colAccent, background = texAccentDim },
                padding   = new RectOffset(8, 8, 3, 3),
                margin    = new RectOffset(0, 4, 0, 0),
                border    = new RectOffset(6, 6, 6, 6),
            };

            sectionStyle = new GUIStyle
            {
                fontSize  = 9,
                fontStyle = FontStyle.Bold,
                normal    = { textColor = colTextMuted },
                padding   = new RectOffset(0, 0, 6, 2),
                margin    = new RectOffset(0, 0, 4, 0),
            };

            footerStyle = new GUIStyle
            {
                fontSize  = 9,
                normal    = { textColor = colTextMuted },
                alignment = TextAnchor.MiddleCenter,
                padding   = new RectOffset(0, 0, 4, 4),
            };

            buttonStyle = new GUIStyle
            {
                fontSize  = 10,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal    = { textColor = colTextPrimary, background = texBtn },
                hover     = { textColor = Color.white,   background = texBtnHover },
                active    = { textColor = Color.white,   background = texAccentSolid },
                padding   = new RectOffset(12, 12, 6, 6),
                margin    = new RectOffset(2, 2, 2, 2),
                border    = new RectOffset(5, 5, 5, 5),
            };

            fieldLabelStyle = new GUIStyle
            {
                fontSize = 11,
                normal   = { textColor = colTextSecond },
                padding  = new RectOffset(0, 0, 3, 3),
            };

            readOnlyStyle = new GUIStyle
            {
                fontSize  = 11,
                fontStyle = FontStyle.Italic,
                normal    = { textColor = colTextMuted },
                padding   = new RectOffset(4, 0, 3, 3),
            };
        }

        // ─── Inspector ────────────────────────────────────────────────────────────

        public override void OnInspectorGUI()
        {
            BuildStyles();
            serializedObject.Update();

            var script = (VaroniaSync)target;

            var bgRect = EditorGUILayout.BeginVertical();
            EditorGUI.DrawRect(bgRect, colBg);
            GUILayout.Space(10);

            // ── Title bar ──
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(12);
            GUILayout.Label("VARONIA SYNC", headerStyle);
            GUILayout.Space(12);
            EditorGUILayout.EndHorizontal();

            GUILayout.Space(12);

            // ── Configuration card ──
            DrawCard(() =>
            {
                DrawSectionLabel("CONFIGURATION");
                DrawDivider();
                GUILayout.Space(6);

                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Instantiate Boundary", fieldLabelStyle, GUILayout.Width(130));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("instantiateBoundary"), GUIContent.none);
                EditorGUILayout.EndHorizontal();

                GUILayout.Space(4);

                EditorGUI.BeginDisabledGroup(!script.InstantiateBoundary);
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Boundary Prefab", fieldLabelStyle, GUILayout.Width(130));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("boundaryPrefab"), GUIContent.none);
                EditorGUILayout.EndHorizontal();

                GUILayout.Space(4);

                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Start Inactive", fieldLabelStyle, GUILayout.Width(130));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("startBoundaryInactive"), GUIContent.none);
                EditorGUILayout.EndHorizontal();
                EditorGUI.EndDisabledGroup();

            }, (!script.InstantiateBoundary || script.HasPrefab) ? colAccent : colWarn);

            GUILayout.Space(8);
            GUILayout.Label("Varonia Back Office  ·  VaroniaSync", footerStyle);
            GUILayout.Space(8);

            EditorGUILayout.EndVertical();
            serializedObject.ApplyModifiedProperties();
        }

        // ─── Helpers ──────────────────────────────────────────────────────────────

        void DrawCard(Action content, Color accentColor)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(8);
            EditorGUILayout.BeginVertical();
            Rect r = EditorGUILayout.BeginVertical();
            r.x -= 4; r.width += 8; r.y -= 4; r.height += 8;
            EditorGUI.DrawRect(r, colCard);
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, 2), accentColor);
            GUILayout.Space(10);
            content();
            GUILayout.Space(10);
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndVertical();
            GUILayout.Space(8);
            EditorGUILayout.EndHorizontal();
        }

        void DrawSectionLabel(string text) => GUILayout.Label(text, sectionStyle);

        void DrawDivider()
        {
            Rect r = GUILayoutUtility.GetRect(1, 1);
            r.x += 16; r.width -= 32;
            EditorGUI.DrawRect(r, colDivider);
        }
    }
}
