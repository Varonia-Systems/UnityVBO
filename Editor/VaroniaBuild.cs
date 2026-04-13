using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Callbacks;
using UnityEngine;
using UnityEngine.SceneManagement;

// ─── Build hooks ─────────────────────────────────────────────────────────────

class BuildProcessor : IPreprocessBuildWithReport
{
    public int callbackOrder => 0;
    public void OnPreprocessBuild(BuildReport report)
    {
        IncrementVersion.Increment();
        VaroniaBackOffice.Buildwindows.SetState("BUILD", "Compilation du projet en cours...", 0.04f);
        EditorUtility.ClearProgressBar();
    }
}

class BuildProcessor_2 : IPostprocessBuildWithReport
{
    public int callbackOrder => 0;
    public void OnPostprocessBuild(BuildReport report)
    {
        EditorUtility.ClearProgressBar();
        bool success = report.summary.result != BuildResult.Failed;

        try
        {
            var messages = VaroniaBackOffice.BuildReportExtractor.Extract(report);
            VaroniaBackOffice.BuildReportExtractor.LogToConsole(report, messages);
            VaroniaBackOffice.VaroniaBuild.LastBuildMessages = messages;
            VaroniaBackOffice.VaroniaBuild.LastBuildReport   = report;
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError($"[VaroniaBuild] Erreur extraction rapport : {e.Message}");
        }

        VaroniaBackOffice.VaroniaBuild.EndBuild(success);
    }
}

public class IncrementVersion
{
    public static void Increment()
    {
        PlayerSettings.bundleVersion = DateTime.Now.ToString("yyyy.MM.dd HH:mm");
        UnityEngine.Debug.Log("New version : " + PlayerSettings.bundleVersion);
    }
}

// ─────────────────────────────────────────────────────────────────────────────

namespace VaroniaBackOffice
{
    // ═════════════════════════════════════════════════════════════════════════
    //  BuildMessage
    // ═════════════════════════════════════════════════════════════════════════

    [Serializable]
    public struct BuildMessage
    {
        public LogType Type;
        public string  Content;
        public string  File;
        public int     Line;

        public bool IsError   => Type == LogType.Error || Type == LogType.Exception;
        public bool IsWarning => Type == LogType.Warning;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  PipelineTimings — chrono de chaque étape
    // ═════════════════════════════════════════════════════════════════════════

    public static class PipelineTimings
    {
        public static readonly Stopwatch BuildTimer = new Stopwatch();
        public static readonly Stopwatch ZipTimer   = new Stopwatch();
        public static readonly Stopwatch CopyTimer  = new Stopwatch();

        public static void ResetAll()
        {
            BuildTimer.Reset();
            ZipTimer.Reset();
            CopyTimer.Reset();
        }

        public static string Format(Stopwatch sw)
        {
            if (!sw.IsRunning && sw.ElapsedMilliseconds == 0) return "—";
            var ts = sw.Elapsed;
            if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
            return $"{ts.Seconds}.{ts.Milliseconds / 100}s";
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  BuildReportExtractor
    // ═════════════════════════════════════════════════════════════════════════

    public static class BuildReportExtractor
    {
        public static List<BuildMessage> Extract(BuildReport report)
        {
            var messages = new List<BuildMessage>();
            if (report == null) return messages;

            foreach (var step in report.steps)
            {
                foreach (var msg in step.messages)
                {
                    if (msg.type != LogType.Error &&
                        msg.type != LogType.Exception &&
                        msg.type != LogType.Warning)
                        continue;

                    var bm = new BuildMessage
                    {
                        Type    = msg.type,
                        Content = msg.content ?? "",
                        File    = "",
                        Line    = 0,
                    };

                    ParseFileAndLine(msg.content, out bm.File, out bm.Line);
                    messages.Add(bm);
                }
            }

            return messages;
        }

        static void ParseFileAndLine(string content, out string file, out int line)
        {
            file = "";
            line = 0;
            if (string.IsNullOrEmpty(content)) return;

            int parenOpen = content.IndexOf('(');
            if (parenOpen < 0) return;

            int parenClose = content.IndexOf(')', parenOpen);
            if (parenClose < 0) return;

            int colonAfter = content.IndexOf(':', parenClose);
            if (colonAfter < 0) return;

            file = content.Substring(0, parenOpen).Trim();

            string lineCol = content.Substring(parenOpen + 1, parenClose - parenOpen - 1);
            string[] parts = lineCol.Split(',');
            if (parts.Length >= 1)
                int.TryParse(parts[0].Trim(), out line);
        }

        public static void LogToConsole(BuildReport report, List<BuildMessage> messages)
        {
            int errors   = messages.Count(m => m.IsError);
            int warnings = messages.Count(m => m.IsWarning);

            string summary = report.summary.result == BuildResult.Failed
                ? $"[VaroniaBuild] ❌ Build ÉCHOUÉ — {errors} erreur(s), {warnings} warning(s)"
                : $"[VaroniaBuild] ✅ Build réussi — {warnings} warning(s)";

            if (report.summary.result == BuildResult.Failed)
                UnityEngine.Debug.LogError(summary);
            else if (warnings > 0)
                UnityEngine.Debug.LogWarning(summary);
            else
                UnityEngine.Debug.Log(summary);

            foreach (var msg in messages.Where(m => m.IsError))
            {
                string loc = !string.IsNullOrEmpty(msg.File) ? $"{msg.File}:{msg.Line}" : "unknown";
                UnityEngine.Debug.LogError($"  ● [{loc}] {msg.Content}");
            }

            int warnCount = 0;
            foreach (var msg in messages.Where(m => m.IsWarning))
            {
                if (warnCount++ >= 20)
                {
                    UnityEngine.Debug.LogWarning($"  … et {warnings - 20} autres warnings.");
                    break;
                }
                UnityEngine.Debug.LogWarning($"  ▲ {msg.Content}");
            }

            if (report.summary.result == BuildResult.Succeeded)
            {
                double sizeMB = report.summary.totalSize / (1024.0 * 1024.0);
                var    dur    = report.summary.totalTime;
                UnityEngine.Debug.Log($"[VaroniaBuild] 📦 Taille: {sizeMB:F1} MB — Durée: {dur.Minutes}m {dur.Seconds}s");
            }
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  ChangelogEntry — historique des changelogs précédents
    // ═════════════════════════════════════════════════════════════════════════

    [Serializable]
    public struct ChangelogEntry
    {
        public string Date;
        public string Version;
        public string Type;     // RELEASE / BETA / LTS
        public string Added;
        public string Changed;
        public string Fixed;
        public string Removed;
        public string Notes;

        public bool IsEmpty => string.IsNullOrEmpty(Added) && string.IsNullOrEmpty(Changed)
                            && string.IsNullOrEmpty(Fixed) && string.IsNullOrEmpty(Removed)
                            && string.IsNullOrEmpty(Notes);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  VaroniaBuild — fenêtre principale
    // ═════════════════════════════════════════════════════════════════════════

    public class VaroniaBuild : EditorWindow
    {
        // ─── State ────────────────────────────────────────────────────────────────
        [SerializeField] public bool   BetaVersion   = false;
        [SerializeField] public bool   LTS           = false;
        [SerializeField] public bool   Normal        = true;
        [SerializeField] public bool   CopyToServer  = true;
        [SerializeField] public bool   SkipBuild     = false;
        [SerializeField] public bool   ZipBuild      = true;

        // ── Changelog par catégories ──
        [SerializeField] public string ChangelogAdded   = "";
        [SerializeField] public string ChangelogChanged = "";
        [SerializeField] public string ChangelogFixed   = "";
        [SerializeField] public string ChangelogRemoved = "";
        [SerializeField] public string ChangelogNotes   = "";

        // ── Historique des changelogs ──
        [SerializeField] public List<ChangelogEntry> ChangelogHistory = new List<ChangelogEntry>();
        bool _showHistory = false;
        Vector2 _scrollHistory;

        // ─── Build report data ────────────────────────────────────────────────────
        public static List<BuildMessage> LastBuildMessages;
        public static BuildReport        LastBuildReport;
        static bool _endBuildCalled;

        static VaroniaBuild VaroniaBuild_;
        Vector2 _scrollLog;

        // ── Style cache ──
        static bool     stylesBuilt;
        static GUIStyle headerStyle;
        static GUIStyle sectionStyle;
        static GUIStyle footerStyle;
        static GUIStyle buttonStyle;
        static GUIStyle tagStyle;
        static GUIStyle changelogStyle;
        static GUIStyle buildTypeBtnOff;
        static GUIStyle buildTypeBtnOn;

        // ── Colors ──
        static readonly Color colBg          = new Color(0.11f, 0.11f, 0.14f, 1f);
        static readonly Color colCard        = new Color(0.15f, 0.15f, 0.19f, 1f);
        static readonly Color colAccent      = new Color(0.30f, 0.85f, 0.65f, 1f);
        static readonly Color colAccentDim   = new Color(0.30f, 0.85f, 0.65f, 0.15f);
        static readonly Color colWarn        = new Color(1f,    0.75f, 0.30f, 1f);
        static readonly Color colWarnDim     = new Color(1f,    0.75f, 0.30f, 0.12f);
        static readonly Color colError       = new Color(1f,    0.40f, 0.40f, 1f);
        static readonly Color colErrorDim    = new Color(1f,    0.40f, 0.40f, 0.15f);
        static readonly Color colTextPrimary = new Color(0.92f, 0.92f, 0.95f, 1f);
        static readonly Color colTextSecond  = new Color(0.55f, 0.55f, 0.62f, 1f);
        static readonly Color colTextMuted   = new Color(0.40f, 0.40f, 0.47f, 1f);
        static readonly Color colDivider     = new Color(1f,    1f,    1f,    0.06f);
        static readonly Color colBtnNormal   = new Color(0.22f, 0.22f, 0.28f, 1f);
        static readonly Color colBtnHover    = new Color(0.28f, 0.28f, 0.35f, 1f);

        // ── Textures ──
        static Texture2D texBtn, texBtnHover, texAccentSolid, texWarnSolid, texChangelogBg, texTypeOff, texTypeOn;
        private static Texture2D _cachedPillTex;
        private static Color _cachedPillCol;
        private static Texture2D _cachedBuildNormalTex, _cachedBuildHoverTex, _cachedBuildActiveTex;
        private static Color _cachedBuildCol;
        private static Texture2D _cachedFolderTex;

#if VBO_ADVANCED
        [MenuItem("Varonia/Build Menu")]
#endif
        public static void ShowWindow()
        {
            var w = GetWindow<VaroniaBuild>("Varonia Build");
            w.titleContent = new GUIContent("Varonia Build");
            w.minSize = new Vector2(620, 960);
            w.maxSize = new Vector2(620, 960);
        }

        void OnInspectorUpdate() => Repaint();

        protected void OnEnable()
        {
            stylesBuilt = false;
            var data = EditorPrefs.GetString(Application.productName + "Build", JsonUtility.ToJson(this, false));
            JsonUtility.FromJsonOverwrite(data, this);
            VaroniaBuild_ = this;
        }

        protected void OnDisable()
        {
            EditorPrefs.SetString(Application.productName + "Build", JsonUtility.ToJson(this, false));
        }

        // ── Texture helpers ──────────────────────────────────────────────────────

        static Texture2D MakeTex(Color col)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            t.SetPixel(0, 0, col);
            t.Apply();
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
                    if      (x < radius      && y < radius)
                        inside = new Vector2(x - radius,            y - radius).magnitude           <= radius;
                    else if (x >= w - radius && y < radius)
                        inside = new Vector2(x - (w - radius - 1),  y - radius).magnitude           <= radius;
                    else if (x < radius      && y >= h - radius)
                        inside = new Vector2(x - radius,            y - (h - radius - 1)).magnitude <= radius;
                    else if (x >= w - radius && y >= h - radius)
                        inside = new Vector2(x - (w - radius - 1),  y - (h - radius - 1)).magnitude <= radius;
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

            if (texBtn == null) texBtn = MakeRoundedTex(32, 32, colBtnNormal, 5);
            if (texBtnHover == null) texBtnHover = MakeRoundedTex(32, 32, colBtnHover, 5);
            if (texAccentSolid == null) texAccentSolid = MakeRoundedTex(32, 32, colAccent, 5);
            if (texWarnSolid == null) texWarnSolid = MakeRoundedTex(32, 32, colWarn, 5);
            if (texChangelogBg == null) texChangelogBg = MakeTex(new Color(0.10f, 0.10f, 0.13f, 1f));
            if (texTypeOff == null) texTypeOff = MakeRoundedTex(32, 32, new Color(0.19f, 0.19f, 0.25f, 1f), 6);
            if (texTypeOn == null) texTypeOn = MakeRoundedTex(32, 32, colAccentDim, 6);

            headerStyle = new GUIStyle
            {
                fontSize  = 20,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal    = { textColor = colTextPrimary },
                padding   = new RectOffset(0, 0, 0, 0),
            };

            tagStyle = new GUIStyle
            {
                fontSize  = 9,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal    = { textColor = colAccent, background = MakeRoundedTex(32, 32, colAccentDim, 6) },
                padding   = new RectOffset(8, 8, 3, 3),
                margin    = new RectOffset(0, 4, 0, 0),
                border    = new RectOffset(6, 6, 6, 6),
            };

            sectionStyle = new GUIStyle
            {
                fontSize  = 10,
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
                padding   = new RectOffset(0, 0, 6, 6),
            };

            buttonStyle = new GUIStyle
            {
                fontSize  = 11,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal    = { textColor = colTextPrimary, background = texBtn },
                hover     = { textColor = Color.white,   background = texBtnHover },
                active    = { textColor = Color.white,   background = texAccentSolid },
                padding   = new RectOffset(16, 16, 8, 8),
                margin    = new RectOffset(2, 2, 2, 2),
                border    = new RectOffset(5, 5, 5, 5),
            };

            changelogStyle = new GUIStyle(EditorStyles.textArea)
            {
                fontSize = 11,
                normal   = { textColor = colTextPrimary, background = texChangelogBg },
                focused  = { textColor = colTextPrimary },
                padding  = new RectOffset(8, 8, 6, 6),
                wordWrap = true,
            };

            buildTypeBtnOff = new GUIStyle
            {
                fontSize  = 11,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal    = { textColor = colTextMuted,   background = texTypeOff },
                hover     = { textColor = colTextPrimary, background = MakeRoundedTex(32, 32, new Color(0.24f, 0.24f, 0.32f, 1f), 6) },
                padding   = new RectOffset(12, 12, 8, 8),
                margin    = new RectOffset(2, 2, 0, 0),
                border    = new RectOffset(6, 6, 6, 6),
            };

            buildTypeBtnOn = new GUIStyle(buildTypeBtnOff)
            {
                normal = { textColor = colAccent, background = texTypeOn },
            };
        }

        // ─── GUI ──────────────────────────────────────────────────────────────────

        void OnGUI()
        {
            BuildStyles();
            EditorGUI.DrawRect(new Rect(0, 0, position.width, position.height), colBg);

            string gameId    = "";
            bool   hasGameId = false;
            try
            {
                using (var sr = new StreamReader(Application.streamingAssetsPath + "/GameID.txt"))
                {
                    gameId    = sr.ReadToEnd();
                    hasGameId = !string.IsNullOrEmpty(gameId);
                }
            }
            catch { }

            bool has7Zip      = !string.IsNullOrEmpty(EditorPrefs.GetString("VBO_7ZipPath"));
            bool hasBuildPath = !string.IsNullOrEmpty(EditorPrefs.GetString("VBO_BuildPath"));
            bool hasServer    = !string.IsNullOrEmpty(EditorPrefs.GetString("VBO_BuildServerPath"));
            bool hasContent   =  Directory.Exists(EditorPrefs.GetString("VBO_ContentSourcePath") + "/" + Application.productName);
            bool canBuild     = hasGameId && hasBuildPath;

            EditorGUILayout.Space(12);

            // ── Titre ──
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(16);
            GUILayout.Label("VARONIA BUILD", headerStyle);
            GUILayout.FlexibleSpace();

            Color  pillCol  = canBuild ? colAccent : colError;
            string pillText = canBuild ? "  READY  " : "  ERROR  ";
            var    pill     = new GUIStyle(tagStyle);
            pill.normal.textColor  = pillCol;
            if (_cachedPillTex == null || _cachedPillCol != pillCol)
            {
                _cachedPillCol = pillCol;
                _cachedPillTex = MakeRoundedTex(32, 32, new Color(pillCol.r, pillCol.g, pillCol.b, 0.15f), 6);
            }
            pill.normal.background = _cachedPillTex;
            GUILayout.Label(pillText, pill);

            GUILayout.Space(16);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(12);

            // ── Card Statut ──
            Color statusAccent = !canBuild ? colError : (!hasContent || !hasServer) ? colWarn : colAccent;
            DrawCard(() =>
            {
                DrawSectionLabel("STATUT  ·  GAME ID " + (hasGameId ? gameId : "—"));
                EditorGUILayout.Space(4);
                DrawDivider();
                EditorGUILayout.Space(6);
                DrawStatusRow("Game ID",     hasGameId,    gameId, "Not set",  isError: true);
                DrawStatusRow("Build Path",  hasBuildPath, "OK",   "Not set",  isError: true);
                DrawStatusRow("7-Zip",       has7Zip,      "OK",   "Not set",  isError: true);
                DrawStatusRow("Server Path", hasServer,    "OK",   "Not set",  isError: false);
                DrawStatusRow("Content",     hasContent,   "Found","Missing",  isError: false);
            }, statusAccent);

            EditorGUILayout.Space(8);

            // ── Card ChangeLog (catégories) ──
            DrawCard(() =>
            {
                DrawSectionLabel("CHANGELOG");
                EditorGUILayout.Space(4);
                DrawDivider();
                EditorGUILayout.Space(6);

                _scrollLog = EditorGUILayout.BeginScrollView(_scrollLog, GUILayout.Height(220));

                DrawChangelogCategory("✦  ADDED", ref ChangelogAdded, new Color(0.30f, 0.85f, 0.65f, 1f));
                EditorGUILayout.Space(4);
                DrawChangelogCategory("✧  CHANGED", ref ChangelogChanged, new Color(0.55f, 0.75f, 1f, 1f));
                EditorGUILayout.Space(4);
                DrawChangelogCategory("✔  FIXED", ref ChangelogFixed, new Color(1f, 0.75f, 0.30f, 1f));
                EditorGUILayout.Space(4);
                DrawChangelogCategory("✖  REMOVED", ref ChangelogRemoved, new Color(1f, 0.40f, 0.40f, 1f));
                EditorGUILayout.Space(4);
                DrawChangelogCategory("📝  NOTES", ref ChangelogNotes, colTextMuted);

                EditorGUILayout.EndScrollView();

                EditorGUILayout.Space(4);
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();

                var clearBtnStyle = new GUIStyle(buttonStyle);
                clearBtnStyle.fontSize = 9;
                clearBtnStyle.padding  = new RectOffset(10, 10, 4, 4);
                if (GUILayout.Button("Vider les champs", clearBtnStyle, GUILayout.Height(22), GUILayout.Width(120)))
                {
                    ChangelogAdded = ChangelogChanged = ChangelogFixed = ChangelogRemoved = ChangelogNotes = "";
                }
                EditorGUILayout.EndHorizontal();
            }, colTextMuted);

            EditorGUILayout.Space(8);

            // ── Card Historique ──
            DrawCard(() =>
            {
                EditorGUILayout.BeginHorizontal();
                DrawSectionLabel("HISTORIQUE");
                GUILayout.FlexibleSpace();

                var toggleHistStyle = new GUIStyle
                {
                    fontSize  = 9,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter,
                    normal    = { textColor = colAccent },
                    padding   = new RectOffset(8, 8, 4, 4),
                };
                string histLabel = _showHistory
                    ? $"▼  Masquer ({ChangelogHistory.Count})"
                    : $"▶  Afficher ({ChangelogHistory.Count})";
                if (GUILayout.Button(histLabel, toggleHistStyle, GUILayout.Height(20)))
                    _showHistory = !_showHistory;

                EditorGUILayout.EndHorizontal();

                if (_showHistory && ChangelogHistory.Count > 0)
                {
                    EditorGUILayout.Space(4);
                    DrawDivider();
                    EditorGUILayout.Space(4);

                    _scrollHistory = EditorGUILayout.BeginScrollView(_scrollHistory, GUILayout.Height(180));

                    for (int i = ChangelogHistory.Count - 1; i >= 0; i--)
                    {
                        var entry = ChangelogHistory[i];
                        var entryHeaderStyle = new GUIStyle
                        {
                            fontSize  = 10,
                            fontStyle = FontStyle.Bold,
                            normal    = { textColor = colAccent },
                            padding   = new RectOffset(0, 0, 2, 2),
                        };
                        GUILayout.Label($"v{entry.Version}  ·  {entry.Date}  ·  {entry.Type}", entryHeaderStyle);

                        var entryBodyStyle = new GUIStyle
                        {
                            fontSize = 9,
                            normal   = { textColor = colTextSecond },
                            padding  = new RectOffset(8, 0, 0, 4),
                            wordWrap = true,
                        };

                        if (!string.IsNullOrEmpty(entry.Added))   GUILayout.Label($"Added: {entry.Added.Replace("\n", ", ")}",   entryBodyStyle);
                        if (!string.IsNullOrEmpty(entry.Changed)) GUILayout.Label($"Changed: {entry.Changed.Replace("\n", ", ")}", entryBodyStyle);
                        if (!string.IsNullOrEmpty(entry.Fixed))   GUILayout.Label($"Fixed: {entry.Fixed.Replace("\n", ", ")}",   entryBodyStyle);
                        if (!string.IsNullOrEmpty(entry.Removed)) GUILayout.Label($"Removed: {entry.Removed.Replace("\n", ", ")}", entryBodyStyle);

                        EditorGUILayout.Space(4);

                        if (i > 0)
                        {
                            Rect sep = GUILayoutUtility.GetRect(1, 1);
                            sep.x += 8; sep.width -= 16;
                            EditorGUI.DrawRect(sep, colDivider);
                            EditorGUILayout.Space(4);
                        }
                    }

                    EditorGUILayout.EndScrollView();
                }
                else if (_showHistory && ChangelogHistory.Count == 0)
                {
                    EditorGUILayout.Space(4);
                    DrawDivider();
                    EditorGUILayout.Space(8);
                    var emptyStyle = new GUIStyle
                    {
                        fontSize  = 10,
                        alignment = TextAnchor.MiddleCenter,
                        normal    = { textColor = colTextMuted },
                    };
                    GUILayout.Label("Aucun historique", emptyStyle);
                    EditorGUILayout.Space(8);
                }
            }, colTextMuted);

            EditorGUILayout.Space(8);

            // ── Card Build Type ──
            DrawCard(() =>
            {
                DrawSectionLabel("BUILD TYPE");
                EditorGUILayout.Space(4);
                DrawDivider();
                EditorGUILayout.Space(8);
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("NORMAL", Normal      ? buildTypeBtnOn : buildTypeBtnOff, GUILayout.Height(32), GUILayout.MinWidth(110)))
                { Normal = true; BetaVersion = false; LTS = false; }
                GUILayout.Space(4);
                if (GUILayout.Button("BETA",   BetaVersion ? buildTypeBtnOn : buildTypeBtnOff, GUILayout.Height(32), GUILayout.MinWidth(110)))
                { BetaVersion = true; Normal = false; LTS = false; }
                GUILayout.Space(4);
                if (GUILayout.Button("LTS",    LTS         ? buildTypeBtnOn : buildTypeBtnOff, GUILayout.Height(32), GUILayout.MinWidth(110)))
                { LTS = true; Normal = false; BetaVersion = false; }
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.Space(4);
            }, colAccent);

            EditorGUILayout.Space(8);

            // ── Card Options (toggles indépendants) ──
            DrawCard(() =>
            {
                DrawSectionLabel("OPTIONS");
                EditorGUILayout.Space(4);
                DrawDivider();
                EditorGUILayout.Space(6);
                DrawToggleRow(ref SkipBuild,    "Skip Build",       "Ne pas compiler, utiliser le build existant");
                EditorGUILayout.Space(2);
                DrawToggleRow(ref ZipBuild,     "Zip Build",        "Compresser le build en archive ZIP");
                EditorGUILayout.Space(2);
                DrawToggleRow(ref CopyToServer, "Copy to Server",   "Copier les archives sur le serveur de build");
            }, colTextMuted);

            EditorGUILayout.Space(12);

            // ── Bouton principal ──
            GUI.enabled = canBuild;
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(12);

            string buildLabel;
            Color  buildCol;

            if (!SkipBuild)
            {
                buildLabel = "🛠️  BUILD";
                buildCol   = colAccent;
            }
            else if (ZipBuild)
            {
                buildLabel = "⚙️  ZIP" + (CopyToServer ? " & DEPLOY" : "");
                buildCol   = colWarn;
            }
            else if (CopyToServer)
            {
                buildLabel = "📤  DEPLOY";
                buildCol   = colWarn;
            }
            else
            {
                buildLabel = "⚠️  RIEN À FAIRE";
                buildCol   = colError;
                GUI.enabled = false;
            }

            if (_cachedBuildNormalTex == null || _cachedBuildCol != buildCol)
            {
                _cachedBuildCol = buildCol;
                _cachedBuildNormalTex = MakeRoundedTex(32, 32, new Color(buildCol.r, buildCol.g, buildCol.b, 0.80f), 8);
                _cachedBuildHoverTex = MakeRoundedTex(32, 32, buildCol, 8);
                _cachedBuildActiveTex = MakeRoundedTex(32, 32, new Color(buildCol.r * 0.75f, buildCol.g * 0.75f, buildCol.b * 0.75f, 1f), 8);
            }

            var buildBtnStyle = new GUIStyle
            {
                fontSize  = 22,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal    = { textColor = Color.white, background = _cachedBuildNormalTex },
                hover     = { textColor = Color.white, background = _cachedBuildHoverTex },
                active    = { textColor = Color.white, background = _cachedBuildActiveTex },
                border    = new RectOffset(8, 8, 8, 8),
            };

            if (GUILayout.Button(buildLabel, buildBtnStyle, GUILayout.Height(58)))
            {
                savelog();
                StartPipeline(gameId);
            }

            GUILayout.Space(12);
            EditorGUILayout.EndHorizontal();
            GUI.enabled = true;

            EditorGUILayout.Space(8);

            // ── Boutons dossiers ──
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(12);

            var folderStyle = new GUIStyle(buttonStyle);
            if (_cachedFolderTex == null)
                _cachedFolderTex = MakeRoundedTex(32, 32, new Color(colAccent.r, colAccent.g, colAccent.b, 0.13f), 5);
            folderStyle.normal.background = _cachedFolderTex;
            folderStyle.normal.textColor  = colAccent;
            folderStyle.hover.textColor   = Color.white;

            if (GUILayout.Button("📁  Server Path", folderStyle, GUILayout.Height(30)))
                EditorUtility.RevealInFinder(EditorPrefs.GetString("VBO_BuildServerPath") + "/" + gameId);

            GUILayout.Space(4);

            if (GUILayout.Button("📁  Build Path", folderStyle, GUILayout.Height(30)))
                EditorUtility.RevealInFinder(EditorPrefs.GetString("VBO_BuildPath") + "/" + Application.productName);

            GUILayout.Space(12);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(6);
            GUILayout.Label("Varonia Back Office  ·  Build Pipeline", footerStyle);
            EditorGUILayout.Space(6);
        }

        // ─── Row helpers ──────────────────────────────────────────────────────────

        void DrawStatusRow(string label, bool ok, string okText, string errText, bool isError)
        {
            Color col = ok ? colAccent : (isError ? colError : colWarn);
            EditorGUILayout.BeginHorizontal();

            var dotStyle = new GUIStyle { fontSize = 11, fontStyle = FontStyle.Bold, normal = { textColor = col }, padding = new RectOffset(0, 4, 2, 2) };
            GUILayout.Label("●", dotStyle, GUILayout.Width(16));

            var lblStyle = new GUIStyle { fontSize = 11, normal = { textColor = colTextSecond }, padding = new RectOffset(0, 0, 2, 2) };
            GUILayout.Label(label, lblStyle, GUILayout.Width(100));
            GUILayout.FlexibleSpace();

            var valStyle = new GUIStyle { fontSize = 11, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight, normal = { textColor = col }, padding = new RectOffset(0, 0, 2, 2) };
            GUILayout.Label(ok ? okText : errText, valStyle);
            EditorGUILayout.EndHorizontal();
        }

        void DrawToggleRow(ref bool value, string label, string tooltip = "")
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            bool v = EditorGUILayout.Toggle(value, GUILayout.Width(20));
            if (EditorGUI.EndChangeCheck()) value = v;

            var lblStyle = new GUIStyle { fontSize = 11, normal = { textColor = value ? colTextPrimary : colTextSecond }, padding = new RectOffset(4, 0, 3, 3) };
            GUILayout.Label(new GUIContent(label, tooltip), lblStyle);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        // ─── Draw helpers ─────────────────────────────────────────────────────────

        void DrawCard(Action content, Color accentColor)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(12);
            EditorGUILayout.BeginVertical();

            Rect r = EditorGUILayout.BeginVertical();
            r.x -= 4; r.width += 8; r.y -= 4; r.height += 8;

            EditorGUI.DrawRect(r, colCard);
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, 2), accentColor);

            GUILayout.Space(12);
            content();
            GUILayout.Space(12);

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndVertical();
            GUILayout.Space(12);
            EditorGUILayout.EndHorizontal();
        }

        void DrawSectionLabel(string text) => GUILayout.Label(text, sectionStyle);

        void DrawDivider()
        {
            Rect r = GUILayoutUtility.GetRect(1, 1);
            r.x += 20; r.width -= 40;
            EditorGUI.DrawRect(r, colDivider);
        }

        // ─── Logique ─────────────────────────────────────────────────────────────

        void savelog()
        {
            // Archive le changelog actuel dans l'historique avant de builder
            var currentEntry = new ChangelogEntry
            {
                Date    = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                Version = DateTime.Now.ToString("yyyy.M.d"),
                Type    = BetaVersion ? "BETA" : LTS ? "LTS" : "RELEASE",
                Added   = ChangelogAdded,
                Changed = ChangelogChanged,
                Fixed   = ChangelogFixed,
                Removed = ChangelogRemoved,
                Notes   = ChangelogNotes,
            };

            if (!currentEntry.IsEmpty)
            {
                ChangelogHistory.Add(currentEntry);
                // Garde les 50 derniers max
                if (ChangelogHistory.Count > 50)
                    ChangelogHistory.RemoveAt(0);
            }

            // Sauvegarde dans les EditorPrefs (sera persisté par OnDisable)
            EditorPrefs.SetString(Application.productName + "Build", JsonUtility.ToJson(this, false));
        }

        // ── Helper pour dessiner une catégorie de changelog ──────────────────

        void DrawChangelogCategory(string label, ref string content, Color labelColor)
        {
            var catLabelStyle = new GUIStyle
            {
                fontSize  = 10,
                fontStyle = FontStyle.Bold,
                normal    = { textColor = labelColor },
                padding   = new RectOffset(0, 0, 2, 2),
            };
            GUILayout.Label(label, catLabelStyle);

            var catTextStyle = new GUIStyle(EditorStyles.textArea)
            {
                fontSize = 10,
                normal   = { textColor = colTextPrimary, background = texChangelogBg },
                focused  = { textColor = colTextPrimary },
                padding  = new RectOffset(6, 6, 4, 4),
                wordWrap = true,
            };
            content = EditorGUILayout.TextArea(content, catTextStyle, GUILayout.MinHeight(32));
        }

        // ── Pipeline ─────────────────────────────────────────────────────────────

        void StartPipeline(string gameId)
        {
            PipelineTimings.ResetAll();

            if (!SkipBuild)
                Build(gameId);
            else
                ContinuePipelineAfterBuild(gameId);
        }

        void ContinuePipelineAfterBuild(string gameId)
        {
            if (ZipBuild)
                Zip(gameId);
            else if (CopyToServer)
                Copy(gameId);
            else
            {
                PlaySuccess();
                ShowSuccessReport();
            }
        }

        public static void EndBuild(bool success)
        {
            if (_endBuildCalled) return;
            _endBuildCalled = true;

            PipelineTimings.BuildTimer.Stop();

            if (!success)
            {
                PlayFailure();
                Buildwindows.CloseWindow();
                UnityEngine.Debug.LogError("[VaroniaBuild] ❌ Build échoué.");

                if (LastBuildReport != null && LastBuildMessages != null)
                {
                    var errorsOnly = LastBuildMessages.Where(m => m.IsError).ToList();
                    EditorApplication.delayCall += () => BuildErrorWindow.ShowErrors(LastBuildReport, errorsOnly);
                }
                return;
            }

            try
            {
                string gid;
                using (var sr = new StreamReader(Application.streamingAssetsPath + "/GameID.txt"))
                    gid = sr.ReadToEnd();
                VaroniaBuild_.Version(gid);
            }
            catch { }

            Buildwindows.CloseWindow();

            try
            {
                string gid;
                using (var sr = new StreamReader(Application.streamingAssetsPath + "/GameID.txt"))
                    gid = sr.ReadToEnd();
                VaroniaBuild_.ContinuePipelineAfterBuild(gid);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError($"[VaroniaBuild] Erreur pipeline post-build : {e.Message}");
                PlayFailure();
            }
        }

        static void ShowSuccessReport()
        {
            EditorApplication.delayCall += () => BuildErrorWindow.ShowSuccess();
        }

        static void PlaySuccess() => VaroniaBuildSounds.Play(success: true);
        static void PlayFailure() => VaroniaBuildSounds.Play(success: false);
        static void PlayStep()    => VaroniaBuildSounds.PlayStep();

        async void Version(string GameId)
        {
            await Task.Delay(1500);

            string buildPath = EditorPrefs.GetString("VBO_BuildPath") + "/" + Application.productName;
            string backup    = buildPath + "/" + Application.productName + "_BackUpThisFolder_ButDontShipItWithYourGame";
            string burst     = buildPath + "/" + Application.productName + "_BurstDebugInformation_DoNotShip";

            // Suppression avec retry — les dossiers peuvent être verrouillés
            // quelques secondes après le build (IL2CPP, Burst, antivirus, etc.)
            await TryDeleteDirectoryAsync(backup, maxRetries: 5, delayMs: 2000);
            await TryDeleteDirectoryAsync(burst,  maxRetries: 5, delayMs: 2000);

            // Changelog formaté avec catégories
            string changelogFile = buildPath + "/Changelog.txt";
            if (File.Exists(changelogFile)) File.Delete(changelogFile);
            using (var sw = new StreamWriter(changelogFile, true))
                sw.Write(BuildChangelogText(GameId));

            var versionData = new
            {
                AppValue = int.Parse(GameId),
                Version  = DateTime.Now.ToString("yyyy.M.d"),
                IsBeta   = BetaVersion,
                IsLTS    = LTS,
            };

            using (var sw = new StreamWriter(buildPath + "/version.json"))
                sw.Write(JsonConvert.SerializeObject(versionData, Formatting.Indented));
        }

        /// <summary>
        /// Tente de supprimer un dossier avec retry.
        /// Les dossiers de build (Burst, Backup) peuvent rester verrouillés
        /// pendant quelques secondes après la fin du build.
        /// </summary>
        static async Task TryDeleteDirectoryAsync(string path, int maxRetries = 5, int delayMs = 2000)
        {
            if (!Directory.Exists(path)) return;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    Directory.Delete(path, true);
                    return; // succès
                }
                catch (Exception e)
                {
                    if (attempt < maxRetries)
                    {
                        UnityEngine.Debug.LogWarning(
                            $"[VaroniaBuild] Impossible de supprimer \"{Path.GetFileName(path)}\" " +
                            $"(tentative {attempt}/{maxRetries}), retry dans {delayMs / 1000}s… ({e.Message})");
                        await Task.Delay(delayMs);
                    }
                    else
                    {
                        // Dernier essai raté — on log mais on ne bloque pas le pipeline
                        UnityEngine.Debug.LogWarning(
                            $"[VaroniaBuild] ⚠️ Suppression de \"{Path.GetFileName(path)}\" échouée " +
                            $"après {maxRetries} tentatives. Ignoré. ({e.Message})");
                    }
                }
            }
        }

        /// <summary>
        /// Génère le contenu texte du changelog pour le fichier Changelog.txt du build.
        /// Inclut le header (version, date, type) et le contenu structuré par catégories.
        /// </summary>
        string BuildChangelogText(string GameId)
        {
            string type = BetaVersion ? "BETA" : LTS ? "LTS" : "RELEASE";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("════════════════════════════════════════════════════════");
            sb.AppendLine($"  {Application.productName}  ·  v{DateTime.Now:yyyy.M.d}  ·  {type}");
            sb.AppendLine($"  Build {DateTime.Now:yyyy-MM-dd HH:mm}  ·  Game ID {GameId}");
            sb.AppendLine("════════════════════════════════════════════════════════");
            sb.AppendLine();

            // Parse les catégories depuis les champs sérialisés
            if (!string.IsNullOrEmpty(ChangelogAdded))
            {
                sb.AppendLine("✦ ADDED");
                foreach (var line in SplitLines(ChangelogAdded))
                    sb.AppendLine($"  • {line}");
                sb.AppendLine();
            }

            if (!string.IsNullOrEmpty(ChangelogChanged))
            {
                sb.AppendLine("✧ CHANGED");
                foreach (var line in SplitLines(ChangelogChanged))
                    sb.AppendLine($"  • {line}");
                sb.AppendLine();
            }

            if (!string.IsNullOrEmpty(ChangelogFixed))
            {
                sb.AppendLine("✔ FIXED");
                foreach (var line in SplitLines(ChangelogFixed))
                    sb.AppendLine($"  • {line}");
                sb.AppendLine();
            }

            if (!string.IsNullOrEmpty(ChangelogRemoved))
            {
                sb.AppendLine("✖ REMOVED");
                foreach (var line in SplitLines(ChangelogRemoved))
                    sb.AppendLine($"  • {line}");
                sb.AppendLine();
            }

            // Notes libres
            if (!string.IsNullOrEmpty(ChangelogNotes))
            {
                sb.AppendLine("─── Notes ───");
                sb.AppendLine(ChangelogNotes);
                sb.AppendLine();
            }

            return sb.ToString();
        }

        static string[] SplitLines(string text)
        {
            return text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                       .Select(l => l.Trim())
                       .Where(l => !string.IsNullOrEmpty(l))
                       .ToArray();
        }

        async void Copy(string GameId)
        {
            PipelineTimings.CopyTimer.Restart();

            string serverPath = EditorPrefs.GetString("VBO_BuildServerPath");
            string contentSrc = EditorPrefs.GetString("VBO_ContentSourcePath");
            string buildPath_ = EditorPrefs.GetString("VBO_BuildPath");
            string contentZip = contentSrc + "/" + Application.productName + "/Content.zip";
            string gameZip    = buildPath_ + "/" + Application.productName + "/Game.zip";
            bool   hasContent = File.Exists(contentZip);

            if (!File.Exists(gameZip))
            {
                PipelineTimings.CopyTimer.Stop();
                UnityEngine.Debug.LogError($"[VaroniaBuild] ❌ Game.zip introuvable : {gameZip}");
                PlayFailure();
                return;
            }

            string dest = serverPath + "/" + GameId + "/" + DateTime.Now.ToString("yyyy.M.d");
            if (BetaVersion) dest += "_BETA";
            if (LTS)         dest += "_LTS";

            Buildwindows.SetState("COPIE SERVEUR", "Création du dossier de destination...", 0f);
            Buildwindows.ShowWindow();
            Directory.CreateDirectory(dest);

            if (hasContent)
            {
                Buildwindows.SetState("COPIE SERVEUR", "Copie de Content.zip...", 0.10f);
                await Task.Run(() => File.Copy(contentZip, dest + "/Content.zip", true));
            }

            Buildwindows.SetState("COPIE SERVEUR", "Copie de Game.zip...", hasContent ? 0.55f : 0.10f);
            await Task.Run(() => File.Copy(gameZip, dest + "/Game.zip", true));

            if (hasContent)
            {
                Buildwindows.SetState("COPIE SERVEUR", "Nettoyage...", 0.95f);
                File.Delete(contentZip);
            }

            Buildwindows.SetState("COPIE SERVEUR", "Copie terminée ✓", 1f);

            PipelineTimings.CopyTimer.Stop();
            PlaySuccess();

            await Task.Delay(700);
            Buildwindows.CloseWindow();

            ShowSuccessReport();
        }

        async void Zip(string GameId)
        {
            PipelineTimings.ZipTimer.Restart();

            string sevenZip    = EditorPrefs.GetString("VBO_7ZipPath") + "/7z.exe";
            string buildPath   = EditorPrefs.GetString("VBO_BuildPath") + "/" + Application.productName;
            string contentPath = EditorPrefs.GetString("VBO_ContentSourcePath") + "/" + Application.productName;
            string gameZip     = buildPath   + "/Game.zip";
            string contentZip  = contentPath + "/Content.zip";
            bool   hasContent  = Directory.Exists(contentPath);

            Buildwindows.SetState("PACKAGING", "Nettoyage des archives précédentes...", 0f);
            Buildwindows.ShowWindow();

            if (File.Exists(gameZip))    { File.Delete(gameZip);    await Task.Delay(300); }
            if (File.Exists(contentZip)) { File.Delete(contentZip); await Task.Delay(200); }

            if (hasContent)
            {
                await RunZip(sevenZip,
                    $@"a -tZIP -bsp1 ""{contentZip}"" ""{contentPath}/*"" -r",
                    from: 0.03f, to: 0.48f,
                    label: "Compression du content  ·  Content.zip");
            }

            await Task.Delay(500);

            await RunZip(sevenZip,
                $@"a -tZIP -bsp1 ""{gameZip}"" ""{buildPath}/*"" -r",
                from: hasContent ? 0.50f : 0.03f, to: 0.97f,
                label: "Compression du jeu  ·  Game.zip");

            Buildwindows.SetState("PACKAGING", "Packaging terminé ✓", 1f);

            PipelineTimings.ZipTimer.Stop();
            PlayStep();

            await Task.Delay(700);
            Buildwindows.CloseWindow();
            await Task.Delay(400);

            if (CopyToServer)
                Copy(GameId);
            else
            {
                PlaySuccess();
                ShowSuccessReport();
            }
        }

        static async Task RunZip(string exe, string args, float from, float to, string label)
        {
            Buildwindows.SetState("PACKAGING", label, from);

            var psi = new ProcessStartInfo
            {
                FileName               = exe,
                Arguments              = args,
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            Process proc;
            try   { proc = Process.Start(psi); }
            catch { Buildwindows.SetState("PACKAGING", "❌ 7-Zip introuvable", from); return; }
            if (proc == null) return;

            var readThread = new System.Threading.Thread(() =>
            {
                try
                {
                    var reader = proc.StandardOutput;
                    var token  = new System.Text.StringBuilder(32);
                    int ch;
                    while ((ch = reader.Read()) != -1)
                    {
                        if (ch == '\r' || ch == '\n') { ParseToken(token.ToString(), from, to); token.Clear(); }
                        else token.Append((char)ch);
                    }
                    if (token.Length > 0) ParseToken(token.ToString(), from, to);
                }
                catch { }
            });
            readThread.IsBackground = true;
            readThread.Start();

            while (!proc.HasExited) await Task.Delay(100);

            readThread.Join(1000);
            Buildwindows.Progress = to;
        }

        static void ParseToken(string raw, float from, float to)
        {
            foreach (string part in raw.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string p = part.Trim();
                if (p.EndsWith("%") && int.TryParse(p.TrimEnd('%'), out int pct))
                {
                    Buildwindows.Progress = from + Mathf.Clamp01(pct / 100f) * (to - from);
                    return;
                }
            }
        }

        void Build(string GameId)
        {
            _endBuildCalled = false;
            PipelineTimings.BuildTimer.Restart();

            Buildwindows.SetState("BUILD", "Compilation du projet en cours...", 0f, indeterminate: true);
            Buildwindows.ShowWindow(large: true);
            EditorUtility.ClearProgressBar();

            EditorApplication.delayCall += () =>
            {
                var levels = new List<string>();
                for (int i = 0; i < EditorBuildSettings.scenes.Length; i++)
                    if (EditorBuildSettings.scenes[i].enabled)
                        levels.Add(SceneUtility.GetScenePathByBuildIndex(i));

                var report = BuildPipeline.BuildPlayer(
                    levels.ToArray(),
                    EditorPrefs.GetString("VBO_BuildPath") + "/" + Application.productName + "/" + Application.productName + ".exe",
                    BuildTarget.StandaloneWindows64,
                    BuildOptions.None
                );

                if (report.summary.result == BuildResult.Failed && !_endBuildCalled)
                {
                    PipelineTimings.BuildTimer.Stop();
                    try
                    {
                        var messages = BuildReportExtractor.Extract(report);
                        BuildReportExtractor.LogToConsole(report, messages);
                        LastBuildMessages = messages;
                        LastBuildReport   = report;
                    }
                    catch (Exception e)
                    {
                        UnityEngine.Debug.LogError($"[VaroniaBuild] Erreur extraction rapport : {e.Message}");
                    }
                    EndBuild(false);
                }
            };
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  Buildwindows — fenêtre de progression
    // ═════════════════════════════════════════════════════════════════════════

    public class Buildwindows : EditorWindow
    {
        public static string StepLabel     = "BUILD";
        public static string SubLabel      = "";
        public static float  Progress      = 0f;
        public static bool   Indeterminate = false;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        static readonly IntPtr HWND_TOPMOST   = new IntPtr(-1);
        static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010;

        bool _topmost;

        static readonly Color colBg          = new Color(0.11f, 0.11f, 0.14f, 1f);
        static readonly Color colAccent      = new Color(0.30f, 0.85f, 0.65f, 1f);
        static readonly Color colAccentDim   = new Color(0.30f, 0.85f, 0.65f, 0.15f);
        static readonly Color colTextPrimary = new Color(0.92f, 0.92f, 0.95f, 1f);
        static readonly Color colTextSecond  = new Color(0.55f, 0.55f, 0.62f, 1f);
        static readonly Color colBarBg       = new Color(1f, 1f, 1f, 0.07f);
        private static Texture2D _cachedTagTex;

        public static void SetState(string step, string sub, float progress, bool indeterminate = false)
        {
            StepLabel = step; SubLabel = sub; Progress = Mathf.Clamp01(progress); Indeterminate = indeterminate;
            if (HasOpenInstances<Buildwindows>()) GetWindow<Buildwindows>().Repaint();
        }

        public static void ShowWindow(bool large = false)
        {
            float W = large ? 680 : 540, H = large ? 220 : 148;
            var w = GetWindow<Buildwindows>(true, "Varonia Build");
            w.minSize = w.maxSize = new Vector2(W, H);
            w.position = new Rect(Screen.currentResolution.width / 2f - W / 2f, Screen.currentResolution.height / 2f - H / 2f, W, H);
        }

        public static void CloseWindow()
        {
            if (!HasOpenInstances<Buildwindows>()) return;
            IntPtr hwnd = FindWindow(null, "Varonia Build");
            if (hwnd != IntPtr.Zero) SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            GetWindow<Buildwindows>().Close();
        }

        void OnEnable()          => _topmost = false;
        void OnInspectorUpdate() => Repaint();

        void OnGUI()
        {
            if (!_topmost)
            {
                IntPtr hwnd = FindWindow(null, "Varonia Build");
                if (hwnd != IntPtr.Zero) { SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE); _topmost = true; }
            }

            float W = position.width, H = position.height;
            EditorGUI.DrawRect(new Rect(0, 0, W, H), colBg);
            EditorGUI.DrawRect(new Rect(0, 0, W, 2), colAccent);

            GUILayout.Space(16);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(20);
            GUILayout.Label("VARONIA BUILD", new GUIStyle { fontSize = 13, fontStyle = FontStyle.Bold, normal = { textColor = colTextPrimary }, padding = new RectOffset(0, 0, 2, 2) });
            GUILayout.FlexibleSpace();

            if (_cachedTagTex == null) { _cachedTagTex = new Texture2D(1, 1, TextureFormat.RGBA32, false); _cachedTagTex.SetPixel(0, 0, colAccentDim); _cachedTagTex.Apply(); _cachedTagTex.hideFlags = HideFlags.HideAndDontSave; }
            GUILayout.Label(StepLabel, new GUIStyle { fontSize = 9, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = colAccent, background = _cachedTagTex }, padding = new RectOffset(10, 10, 4, 4), margin = new RectOffset(0, 20, 0, 0) });
            EditorGUILayout.EndHorizontal();

            GUILayout.Space(14);

            Rect barRect = GUILayoutUtility.GetRect(1, 10);
            barRect.x += 20; barRect.width -= 40;
            EditorGUI.DrawRect(barRect, colBarBg);

            if (Indeterminate)
            {
                float t = (float)(EditorApplication.timeSinceStartup % 1.4) / 1.4f;
                float segW = barRect.width * 0.32f, segX = barRect.x + (barRect.width + segW) * t - segW;
                float clampedX = Mathf.Clamp(segX, barRect.x, barRect.xMax), clampedW = Mathf.Clamp(segX + segW, barRect.x, barRect.xMax) - clampedX;
                EditorGUI.DrawRect(new Rect(clampedX, barRect.y, clampedW, barRect.height), colAccent);
                EditorGUI.DrawRect(new Rect(clampedX, barRect.y, clampedW, barRect.height * 0.4f), new Color(1f, 1f, 1f, 0.12f));
            }
            else
            {
                float fillW = Mathf.Max(0, barRect.width * Progress);
                if (fillW > 0) EditorGUI.DrawRect(new Rect(barRect.x, barRect.y, fillW, barRect.height), colAccent);
                EditorGUI.DrawRect(new Rect(barRect.x, barRect.y, fillW, barRect.height * 0.4f), new Color(1f, 1f, 1f, 0.08f));
            }

            GUILayout.Space(12);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(20);
            GUILayout.Label(SubLabel, new GUIStyle { fontSize = 10, normal = { textColor = colTextSecond }, padding = new RectOffset(0, 0, 2, 2) });
            GUILayout.FlexibleSpace();
            GUILayout.Label(Indeterminate ? "…" : $"{Mathf.RoundToInt(Progress * 100)} %", new GUIStyle { fontSize = 11, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight, normal = { textColor = colAccent }, padding = new RectOffset(0, 0, 2, 2) });
            GUILayout.Space(20);
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(14);
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  BuildErrorWindow — erreurs (échec) OU résumé succès (timings)
    // ═════════════════════════════════════════════════════════════════════════

    public class BuildErrorWindow : EditorWindow
    {
        enum Mode { Errors, Success }

        static Mode               _mode;
        static List<BuildMessage> _messages = new List<BuildMessage>();
        static BuildResult        _result;
        static double             _buildTimeSec;

        Vector2 _scroll;
        string  _filter = "";

        static readonly Color colBg          = new Color(0.11f, 0.11f, 0.14f, 1f);
        static readonly Color colAccent      = new Color(0.30f, 0.85f, 0.65f, 1f);
        static readonly Color colAccentDim   = new Color(0.30f, 0.85f, 0.65f, 0.15f);
        static readonly Color colError       = new Color(1f,    0.40f, 0.40f, 1f);
        static readonly Color colTextPrimary = new Color(0.92f, 0.92f, 0.95f, 1f);
        static readonly Color colTextSecond  = new Color(0.55f, 0.55f, 0.62f, 1f);
        static readonly Color colTextMuted   = new Color(0.40f, 0.40f, 0.47f, 1f);
        static readonly Color colDivider     = new Color(1f,    1f,    1f,    0.06f);
        static readonly Color colCard        = new Color(0.15f, 0.15f, 0.19f, 1f);

        public static void ShowErrors(BuildReport report, List<BuildMessage> errorsOnly)
        {
            _mode = Mode.Errors;
            _messages = errorsOnly ?? new List<BuildMessage>();
            _result = BuildResult.Failed;
            _buildTimeSec = report != null ? report.summary.totalTime.TotalSeconds : 0;
            var w = GetWindow<BuildErrorWindow>(true, "Build Report");
            w.minSize = new Vector2(700, 460);
            w.Show();
        }

        public static void ShowSuccess()
        {
            _mode = Mode.Success;
            _messages = new List<BuildMessage>();
            _result = BuildResult.Succeeded;
            var w = GetWindow<BuildErrorWindow>(true, "Build Report");
            w.minSize = new Vector2(480, 300);
            w.maxSize = new Vector2(480, 300);
            w.Show();
        }

        void OnInspectorUpdate() => Repaint();

        void OnGUI()
        {
            if (_mode == Mode.Success) DrawSuccessView();
            else DrawErrorsView();
        }

        // ── Vue succès ───────────────────────────────────────────────────────

        void DrawSuccessView()
        {
            float W = position.width, H = position.height;
            EditorGUI.DrawRect(new Rect(0, 0, W, H), colBg);
            EditorGUI.DrawRect(new Rect(0, 0, W, 3), colAccent);

            GUILayout.Space(24);
            GUILayout.Label("✅  PIPELINE TERMINÉ", new GUIStyle { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = colAccent } });
            GUILayout.Space(4);
            GUILayout.Label("Toutes les étapes ont été exécutées avec succès", new GUIStyle { fontSize = 11, alignment = TextAnchor.MiddleCenter, normal = { textColor = colTextSecond } });

            GUILayout.Space(20);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(40);
            EditorGUILayout.BeginVertical();

            Rect cardRect = EditorGUILayout.BeginVertical();
            cardRect.x -= 4; cardRect.width += 8; cardRect.y -= 4; cardRect.height += 8;
            EditorGUI.DrawRect(cardRect, colCard);
            EditorGUI.DrawRect(new Rect(cardRect.x, cardRect.y, cardRect.width, 2), colAccent);

            GUILayout.Space(12);

            var bt = PipelineTimings.BuildTimer;
            var zt = PipelineTimings.ZipTimer;
            var ct = PipelineTimings.CopyTimer;

            if (bt.ElapsedMilliseconds > 0) DrawTimingRow("🛠️  Build", PipelineTimings.Format(bt));
            if (zt.ElapsedMilliseconds > 0) DrawTimingRow("📦  Zip",   PipelineTimings.Format(zt));
            if (ct.ElapsedMilliseconds > 0) DrawTimingRow("📤  Copy",  PipelineTimings.Format(ct));

            long totalMs = bt.ElapsedMilliseconds + zt.ElapsedMilliseconds + ct.ElapsedMilliseconds;
            var totalTs = TimeSpan.FromMilliseconds(totalMs);
            string totalStr = totalTs.TotalMinutes >= 1 ? $"{(int)totalTs.TotalMinutes}m {totalTs.Seconds}s" : $"{totalTs.Seconds}.{totalTs.Milliseconds / 100}s";

            GUILayout.Space(4);
            Rect divR = GUILayoutUtility.GetRect(1, 1); divR.x += 12; divR.width -= 24;
            EditorGUI.DrawRect(divR, colDivider);
            GUILayout.Space(6);

            DrawTimingRow("⏱️  Total", totalStr, highlight: true);

            GUILayout.Space(12);
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndVertical();
            GUILayout.Space(40);
            EditorGUILayout.EndHorizontal();

            GUILayout.Space(16);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Fermer", GUILayout.Height(28), GUILayout.Width(100))) Close();
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        void DrawTimingRow(string label, string value, bool highlight = false)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(16);
            GUILayout.Label(label, new GUIStyle { fontSize = 12, normal = { textColor = highlight ? colAccent : colTextPrimary }, padding = new RectOffset(0, 0, 3, 3) }, GUILayout.Width(160));
            GUILayout.FlexibleSpace();
            GUILayout.Label(value, new GUIStyle { fontSize = 12, fontStyle = highlight ? FontStyle.Bold : FontStyle.Normal, alignment = TextAnchor.MiddleRight, normal = { textColor = highlight ? colAccent : colTextSecond }, padding = new RectOffset(0, 0, 3, 3) });
            GUILayout.Space(16);
            EditorGUILayout.EndHorizontal();
        }

        // ── Vue erreurs ──────────────────────────────────────────────────────

        void DrawErrorsView()
        {
            float W = position.width, H = position.height;
            EditorGUI.DrawRect(new Rect(0, 0, W, H), colBg);
            EditorGUI.DrawRect(new Rect(0, 0, W, 3), colError);

            int errors = _messages.Count;

            GUILayout.Space(12);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(16);
            GUILayout.Label("❌  BUILD ÉCHOUÉ", new GUIStyle { fontSize = 18, fontStyle = FontStyle.Bold, normal = { textColor = colError } });
            GUILayout.FlexibleSpace();
            GUILayout.Label($"{errors} erreur(s)  ·  {_buildTimeSec:F1}s", new GUIStyle { fontSize = 10, alignment = TextAnchor.MiddleRight, normal = { textColor = colTextMuted }, padding = new RectOffset(0, 0, 6, 0) });
            GUILayout.Space(16);
            EditorGUILayout.EndHorizontal();

            GUILayout.Space(8);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(16);
            GUILayout.Label("🔍", new GUIStyle { fontSize = 10, normal = { textColor = colTextMuted }, padding = new RectOffset(0, 6, 4, 0) }, GUILayout.Width(20));
            _filter = EditorGUILayout.TextField(_filter, GUILayout.Height(20));
            GUILayout.Space(16);
            EditorGUILayout.EndHorizontal();

            GUILayout.Space(8);
            Rect divRect = GUILayoutUtility.GetRect(1, 1);
            EditorGUI.DrawRect(divRect, colDivider);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            var filtered = _messages.Where(m =>
            {
                if (string.IsNullOrEmpty(_filter)) return true;
                return m.Content.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                       m.File.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0;
            }).ToList();

            if (filtered.Count == 0)
            {
                GUILayout.Space(40);
                GUILayout.Label("Aucune erreur à afficher", new GUIStyle { fontSize = 12, alignment = TextAnchor.MiddleCenter, normal = { textColor = colTextMuted } });
            }

            for (int i = 0; i < filtered.Count; i++)
                DrawMessageRow(filtered[i], i);

            EditorGUILayout.EndScrollView();

            GUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Copier tout dans le presse-papier", GUILayout.Height(26), GUILayout.Width(260)))
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"=== Varonia Build Report — {DateTime.Now:yyyy-MM-dd HH:mm} ===");
                sb.AppendLine($"Résultat : ÉCHOUÉ  |  Erreurs : {errors}");
                sb.AppendLine();
                foreach (var m in _messages)
                {
                    string loc = !string.IsNullOrEmpty(m.File) ? $" ({m.File}:{m.Line})" : "";
                    sb.AppendLine($"[ERR]{loc} {m.Content}");
                }
                EditorGUIUtility.systemCopyBuffer = sb.ToString();
                UnityEngine.Debug.Log("[VaroniaBuild] Rapport copié dans le presse-papier.");
            }
            GUILayout.Space(16);
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(8);
        }

        void DrawMessageRow(BuildMessage msg, int index)
        {
            Rect rowRect = EditorGUILayout.BeginHorizontal(GUILayout.MinHeight(28));
            if (index % 2 == 0) EditorGUI.DrawRect(rowRect, new Color(1, 1, 1, 0.02f));
            EditorGUI.DrawRect(new Rect(rowRect.x, rowRect.y, 3, rowRect.height), colError);

            GUILayout.Space(12);
            GUILayout.Label("●", new GUIStyle { fontSize = 11, fontStyle = FontStyle.Bold, normal = { textColor = colError }, padding = new RectOffset(0, 4, 4, 4) }, GUILayout.Width(16));

            if (!string.IsNullOrEmpty(msg.File))
            {
                string shortFile = msg.File.StartsWith("Assets/") ? msg.File.Substring(7) : msg.File;
                string fileLabel = msg.Line > 0 ? $"{shortFile}:{msg.Line}" : shortFile;
                if (GUILayout.Button(fileLabel, new GUIStyle { fontSize = 10, normal = { textColor = colAccent }, padding = new RectOffset(0, 0, 5, 4) }, GUILayout.Width(200)))
                {
                    var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(msg.File);
                    if (asset != null) AssetDatabase.OpenAsset(asset, msg.Line);
                }
                EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);
            }
            else GUILayout.Space(200);

            string display = msg.Content;
            if (!string.IsNullOrEmpty(msg.File))
            {
                int colonIdx = display.IndexOf(": ", StringComparison.Ordinal);
                if (colonIdx >= 0 && colonIdx < display.Length - 2) display = display.Substring(colonIdx + 2);
            }
            GUILayout.Label(display, new GUIStyle { fontSize = 10, normal = { textColor = colTextPrimary }, padding = new RectOffset(4, 8, 5, 4), wordWrap = true });
            EditorGUILayout.EndHorizontal();
        }
    }
}

// ─── Utility ─────────────────────────────────────────────────────────────────

public static class FolderSearchUtility
{
    public static string[] FindFoldersByName(string folderName)
    {
        return AssetDatabase.FindAssets($"t:DefaultAsset {folderName}")
            .Select(guid => AssetDatabase.GUIDToAssetPath(guid))
            .Where(path => AssetDatabase.IsValidFolder(path) && path.EndsWith("/" + folderName))
            .ToArray();
    }
}