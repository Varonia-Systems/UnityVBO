using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Profiling;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace VaroniaBackOffice
{
    /// <summary>
    /// Widget FPS + mémoire : FPS instantané, moyenne glissante, session, RAM process, VRAM.
    /// Rendu via RenderTexture + GL pour les carrés, OnGUI minimal pour le texte.
    ///
    /// v2 — Améliorations :
    ///   • Session AVG accumulé par frame (plus par tick) → moyenne frame-accurate
    ///   • Strings pré-allouées via StringBuilder réutilisé → 0 alloc GC en régime permanent
    ///   • RenderTexture recréée uniquement si la taille change
    ///   • GL.Begin(QUADS) batché : un seul Begin/End pour tous les carrés
    ///   • Mode Mini (bool) : affiche uniquement FPS + BG + accent bar
    ///   • VRAM : fallback SystemInfo.graphicsMemorySize en build (Profiler VRAM = 0 hors dev build)
    ///   • Dirty flag granulaire : RT redessinée seulement quand les données visuelles changent
    ///   • Suppression des divisions par frame dans OnGUI (layout pré-calculé)
    /// </summary>
    public class VaroniaFPSDisplay : MonoBehaviour
    {
        // ─── Config ───────────────────────────────────────────────────────────────

        public enum DisplayCorner { TopLeft, TopRight, BottomLeft, BottomRight }

        [Header("Display")]
        [SerializeField] private DisplayCorner corner = DisplayCorner.BottomRight;
        [SerializeField] private float margin = 12f;
        [SerializeField] private Vector2 size = new Vector2(150f, 190f);
        [SerializeField] private bool mini = false;

        /// <summary>Facteur d'échelle manuel (1 = 1080p).</summary>
        [Header("UI Scale")]
        public float scaleFactor = 1f;

        [Header("FPS")]
        [SerializeField] private int sampleCount = 300;
        [SerializeField] private float updateInterval = 0.1f;
        [SerializeField] private float memUpdateInterval = 0.5f;

        [Header("Thresholds")]
        [SerializeField] private int thresholdGood = 55;
        [SerializeField] private int thresholdWarn = 30;

        // ─── Colors ───────────────────────────────────────────────────────────────

        static readonly Color ColBg      = new Color(0.11f, 0.11f, 0.14f, 0.92f);
        static readonly Color ColGood    = new Color(0.30f, 0.85f, 0.65f, 1f);
        static readonly Color ColWarn    = new Color(1.00f, 0.75f, 0.30f, 1f);
        static readonly Color ColBad     = new Color(1.00f, 0.40f, 0.40f, 1f);
        static readonly Color ColMuted   = new Color(0.55f, 0.55f, 0.62f, 1f);
        static readonly Color ColValue   = new Color(0.92f, 0.92f, 0.95f, 1f);
        static readonly Color ColDivider = new Color(1f, 1f, 1f, 0.06f);
        static readonly Color ColEmpty   = new Color(1f, 1f, 1f, 0.08f);

        // ─── State ────────────────────────────────────────────────────────────────

        private float[] _samples;
        private int     _sampleIdx;
        private int     _sampleFilled; // how many valid samples we have (for startup)
        private float   _fpsTimer;
        private float   _memTimer;
        private int     _fps;
        private int     _avg;
        private int     _sessionAvg;
        private double  _sessionDtSum;
        private long    _sessionFrameCount;
        private int     _ramMb;
        private int     _vramMb;
        private int     _vramTotal; // total GPU VRAM via SystemInfo (fallback)

        // ─── RenderTexture ────────────────────────────────────────────────────────

        private RenderTexture _rt;
        private Material      _glMat;
        private bool          _rtDirty = true;

        // ─── Layout constants ─────────────────────────────────────────────────────

        private const float Pad        = 10f;
        private const float SquaresH   = 8f;
        private const float SquaresGap = 3f;
        private const float FpsH       = 28f;
        private const float GapFpsAvg  = 4f;
        private const float AvgH       = 18f;
        private const float SessionH   = 18f;
        private const float DivH       = 1f;
        private const float GapDiv     = 5f;
        private const float MemH       = 18f;
        private const float TimeH      = 18f;

        // Mini mode layout
        private const float MiniPad     = 8f;
        private const float MiniFpsH    = 24f;
        private const float MiniSquaresH = 6f;

        private float _totalH;
        private float _miniH;
        private float _panelW;

        // ─── Cached strings — rebuilt only when values change ─────────────────────

        // Pre-allocated StringBuilder to avoid GC allocs on string rebuilds
        private readonly StringBuilder _sb = new StringBuilder(32);

        private string _cachedFps        = "FPS   0";
        private string _cachedAvg        = "AVG   0";
        private string _cachedSessionAvg = "SES   0";
        private string _cachedRam        = "0 MB";
        private string _cachedVram       = "0 MB";
        private string _cachedTime       = "00:00:00";
        private Color  _cachedAccent     = ColGood;

        private int _lastFps        = -1;
        private int _lastAvg        = -1;
        private int _lastSessionAvg = -1;
        private int _lastRamMb      = -1;
        private int _lastVramMb     = -1;
        private int _lastSecond     = -1;
        private bool _lastMini;

        // ─── Styles ───────────────────────────────────────────────────────────────

        private bool     _stylesBuilt;
        private float    _lastScale = 1f;
        private GUIStyle _fpsStyle;
        private GUIStyle _avgStyle;
        private GUIStyle _sessionAvgStyle;
        private GUIStyle _memLabelStyle;
        private GUIStyle _memValueStyle;
        private GUIStyle _timeStyle;

        // ─────────────────────────────────────────────────────────────────────────

        private void Awake()
        {
            _samples = new float[sampleCount];

            RecalcLayout();

            // Cache total GPU VRAM from SystemInfo (works in all builds)
            _vramTotal = SystemInfo.graphicsMemorySize; // MB, returns 0 only on very old platforms
        }

        private void RecalcLayout()
        {
            _totalH = Pad + SquaresH + SquaresGap + FpsH + GapFpsAvg + AvgH + SessionH + 2f
                     + DivH + GapDiv + MemH + MemH + 2f + DivH + GapDiv + TimeH + Pad;
            _miniH  = MiniPad + MiniSquaresH + 2f + MiniFpsH + MiniPad;
            _panelW = size.x;
        }

        private void OnDestroy()
        {
            ReleaseRT();
            if (_glMat) Destroy(_glMat);
        }

        private void ReleaseRT()
        {
            if (_rt != null)
            {
                _rt.Release();
                Destroy(_rt);
                _rt = null;
            }
        }

        // ─── RT setup ─────────────────────────────────────────────────────────────

        private void EnsureRT(float scale)
        {
            float currentH = (mini ? _miniH : _totalH) * scale;
            int rtW = Mathf.Max(1, (int)(_panelW * scale));
            int rtH = Mathf.Max(1, (int)currentH);

            if (_rt == null || _rt.width != rtW || _rt.height != rtH)
            {
                ReleaseRT();
                _rt = new RenderTexture(rtW, rtH, 0, RenderTextureFormat.ARGB32)
                {
                    filterMode = FilterMode.Point,
                    hideFlags  = HideFlags.HideAndDontSave,
                    useMipMap  = false,
                    autoGenerateMips = false,
                };
                _rt.Create();
                _rtDirty = true;
            }

            if (_glMat == null)
            {
                var shader = Shader.Find("Hidden/Internal-Colored");
                if (shader == null) return; // shader stripped — shouldn't happen but defensive

                _glMat = new Material(shader)
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                _glMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                _glMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                _glMat.SetInt("_Cull",     (int)UnityEngine.Rendering.CullMode.Off);
                _glMat.SetInt("_ZWrite",   0);
            }
        }

        // ─── Update ───────────────────────────────────────────────────────────────

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;

            // Clamp aberrant dt (e.g. after loading, breakpoint, alt-tab)
            // Anything above 1s or <= 0 is discarded from statistics
            bool validFrame = dt > 0f && dt < 1f;

            // ── Ring buffer — always write (UI squares reflect raw data) ──
            _samples[_sampleIdx] = validFrame ? dt : 0f;
            _sampleIdx = (_sampleIdx + 1) % sampleCount;
            if (_sampleFilled < sampleCount) _sampleFilled++;

            // ── Session accumulation — per frame, not per tick ──
            if (validFrame)
            {
                _sessionDtSum += dt;
                _sessionFrameCount++;
            }

            // ── Display tick ──
            _fpsTimer += dt;
            if (_fpsTimer >= updateInterval)
            {
                _fpsTimer = 0f;

                // Instantaneous FPS from last valid dt
                _fps = validFrame ? Mathf.RoundToInt(1f / dt) : _fps; // keep last if invalid

                // Windowed average over filled samples only
                float sum = 0f;
                int count = Mathf.Min(_sampleFilled, sampleCount);
                for (int i = 0; i < count; i++) sum += _samples[i];
                _avg = (sum > 0f && count > 0) ? Mathf.RoundToInt(count / sum) : 0;

                // Session average (frame-accurate)
                _sessionAvg = _sessionDtSum > 0.0
                    ? (int)Math.Round(_sessionFrameCount / _sessionDtSum)
                    : 0;

                // ── Rebuild strings only on change ──
                if (_fps != _lastFps)
                {
                    _lastFps = _fps;
                    _sb.Clear().Append("FPS   ").Append(_fps);
                    _cachedFps = _sb.ToString();
                    _cachedAccent = _fps >= thresholdGood ? ColGood
                                  : _fps >= thresholdWarn ? ColWarn
                                  : ColBad;
                }
                if (_avg != _lastAvg)
                {
                    _lastAvg = _avg;
                    _sb.Clear().Append("AVG   ").Append(_avg);
                    _cachedAvg = _sb.ToString();
                }
                if (_sessionAvg != _lastSessionAvg)
                {
                    _lastSessionAvg = _sessionAvg;
                    _sb.Clear().Append("SES   ").Append(_sessionAvg);
                    _cachedSessionAvg = _sb.ToString();
                }

                _rtDirty = true;
            }

            // ── Memory (less frequent) ──
            _memTimer += dt;
            if (_memTimer >= memUpdateInterval)
            {
                _memTimer = 0f;

                int ram = (int)(Profiler.GetTotalAllocatedMemoryLong() / (1024L * 1024L));

                // VRAM: Profiler.GetAllocatedMemoryForGraphicsDriver() returns 0 in non-dev builds.
                // Fallback: SystemInfo.graphicsMemorySize gives total VRAM (not used, but useful).
                // Best effort: use Profiler value if > 0, otherwise show total VRAM with "~" prefix.
                long profilerVram = Profiler.GetAllocatedMemoryForGraphicsDriver();
                int vram;
                bool vramIsEstimate;

                if (profilerVram > 0)
                {
                    vram = (int)(profilerVram / (1024L * 1024L));
                    vramIsEstimate = false;
                }
                else
                {
                    // Fallback: total VRAM from SystemInfo (at least the user sees something)
                    vram = _vramTotal;
                    vramIsEstimate = true;
                }

                if (ram != _lastRamMb)
                {
                    _lastRamMb = ram;
                    _ramMb = ram;
                    _sb.Clear().Append(ram).Append(" MB");
                    _cachedRam = _sb.ToString();
                }

                if (vram != _lastVramMb)
                {
                    _lastVramMb = vram;
                    _vramMb = vram;
                    if (vramIsEstimate)
                    {
                        _sb.Clear().Append("~").Append(vram).Append(" MB");
                    }
                    else
                    {
                        _sb.Clear().Append(vram).Append(" MB");
                    }
                    _cachedVram = _sb.ToString();
                }
            }

            // ── Time (once per second) ──
            int sec = DateTime.Now.Second;
            if (sec != _lastSecond)
            {
                _lastSecond = sec;
                _cachedTime = DateTime.Now.ToString("HH:mm:ss");
            }

            // ── Mini mode toggle detection ──
            if (mini != _lastMini)
            {
                _lastMini = mini;
                _rtDirty = true;
            }
        }

        // ─── Events ───────────────────────────────────────────────────────────────

        private void OnEnable()
        {
            BackOfficeVaronia.OnMovieChanged += OnMovieChanged;
        }

        private void OnDisable()
        {
            BackOfficeVaronia.OnMovieChanged -= OnMovieChanged;
        }

        private void OnMovieChanged()
        {
            if (BackOfficeVaronia.Instance != null)
                show = BackOfficeVaronia.Instance.config.hideMode == 0;

            // Reset session stats
            _sessionDtSum      = 0.0;
            _sessionFrameCount = 0;
            _sessionAvg        = 0;
            _lastSessionAvg    = -1;

            // Auto-mini for spectator modes (delayed to let config settle)
            StopCoroutine(nameof(DelayedMiniCheck));
            StartCoroutine(nameof(DelayedMiniCheck));
        }

        private IEnumerator DelayedMiniCheck()
        {
            yield return new WaitForSecondsRealtime(0.2f);

            if (BackOfficeVaronia.Instance != null)
            {
                var mode = BackOfficeVaronia.Instance.config.DeviceMode;
                Mini = (mode == DeviceMode.Server_Spectator || mode == DeviceMode.Client_Spectator);
            }
        }

        private bool show = true;

        // ─── Public API ───────────────────────────────────────────────────────────

        /// <summary>Toggle mini mode at runtime.</summary>
        public bool Mini
        {
            get => mini;
            set
            {
                if (mini == value) return;
                mini = value;
                _rtDirty = true;
            }
        }

        // ─── GL Rendering into RenderTexture ──────────────────────────────────────

        private void RenderToRT()
        {
            if (_rt == null || _glMat == null) return;

            float W = _rt.width;
            float H = _rt.height;
            float scale = (float)H / (mini ? _miniH : _totalH);

            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = _rt;
            GL.Clear(true, true, Color.clear);

            _glMat.SetPass(0);
            GL.PushMatrix();
            GL.LoadPixelMatrix(0, W, H, 0);

            // ── Background ──
            GLRect(0, 0, W, H, ColBg);

            // ── Left accent bar ──
            GLRect(0, 0, 3f * scale, H, _cachedAccent);

            if (mini)
            {
                RenderSquaresBatched(12f * scale, W - 12f * scale, MiniPad * scale, MiniSquaresH * scale);
            }
            else
            {
                RenderSquaresBatched(12f * scale, W - 12f * scale, Pad * scale, SquaresH * scale);

                // ── Dividers ──
                float yDiv1 = (Pad + SquaresH + SquaresGap + FpsH + GapFpsAvg + AvgH + SessionH + 2f) * scale;
                GLRect(8f * scale, yDiv1, W - 16f * scale, DivH * scale, ColDivider);

                float yDiv2 = yDiv1 + (DivH + GapDiv + MemH + MemH + 2f) * scale;
                GLRect(8f * scale, yDiv2, W - 16f * scale, DivH * scale, ColDivider);
            }

            GL.PopMatrix();
            RenderTexture.active = prev;

            _rtDirty = false;
        }

        /// <summary>Draws all sample squares in a single GL.Begin/End batch.</summary>
        private void RenderSquaresBatched(float xStart, float xEnd, float yTop, float height)
        {
            float availW = xEnd - xStart;
            int count = sampleCount;
            float invCount = 1f / count;

            GL.Begin(GL.QUADS);
            for (int i = 0; i < count; i++)
            {
                float x0 = xStart + i       * availW * invCount;
                float x1 = xStart + (i + 1) * availW * invCount;
                if (x1 - x0 < 1f) x1 = x0 + 1f;

                int bufIdx = (_sampleIdx + i) % count;
                float sampleDt = _samples[bufIdx];

                Color col;
                if (sampleDt <= 0f)
                {
                    col = ColEmpty;
                }
                else
                {
                    int sampleFps = Mathf.RoundToInt(1f / sampleDt);
                    col = sampleFps >= thresholdGood ? ColGood
                        : sampleFps >= thresholdWarn ? ColWarn
                        : ColBad;
                }

                GL.Color(col);
                GL.Vertex3(x0, yTop,          0);
                GL.Vertex3(x1, yTop,          0);
                GL.Vertex3(x1, yTop + height, 0);
                GL.Vertex3(x0, yTop + height, 0);
            }
            GL.End();
        }

        private static void GLRect(float x, float y, float w, float h, Color c)
        {
            GL.Begin(GL.QUADS);
            GL.Color(c);
            GL.Vertex3(x,     y,     0);
            GL.Vertex3(x + w, y,     0);
            GL.Vertex3(x + w, y + h, 0);
            GL.Vertex3(x,     y + h, 0);
            GL.End();
        }

        // ─── OnGUI — single RT blit + text labels ────────────────────────────────

        private void OnGUI()
        {
            if (!show) return;

            float scale = (Screen.height / 1080f) * scaleFactor;
            EnsureStyles(scale);
            EnsureRT(scale);

            if (_rtDirty)
                RenderToRT();

            float currentH = (mini ? _miniH : _totalH) * scale;
            Rect panel = GetPanelRect(_panelW * scale, currentH, scale);

            // Single draw call for background + squares + dividers + accent bar
            GUI.DrawTexture(panel, _rt);

            float x = panel.x + 12f * scale;
            float w = panel.width - 16f * scale;

            if (mini)
            {
                // ── Mini mode: just FPS ──
                float yFps = panel.y + (MiniPad + MiniSquaresH + 2f) * scale;
                _fpsStyle.normal.textColor = _cachedAccent;
                GUI.Label(new Rect(x, yFps, w, MiniFpsH * scale), _cachedFps, _fpsStyle);
            }
            else
            {
                // ── Full mode ──
                float yFps     = panel.y + (Pad + SquaresH + SquaresGap) * scale;
                float yAvg     = yFps + (FpsH + GapFpsAvg) * scale;
                float ySession = yAvg + AvgH * scale;
                float yDiv1    = ySession + SessionH * scale + 2f * scale;
                float yRam     = yDiv1 + (DivH + GapDiv) * scale;
                float yVram    = yRam + MemH * scale;
                float yDiv2    = yVram + MemH * scale + 2f * scale;
                float yTime    = yDiv2 + (DivH + GapDiv) * scale;

                _fpsStyle.normal.textColor = _cachedAccent;
                GUI.Label(new Rect(x, yFps, w, FpsH * scale), _cachedFps, _fpsStyle);
                GUI.Label(new Rect(x, yAvg, w, AvgH * scale), _cachedAvg, _avgStyle);
                GUI.Label(new Rect(x, ySession, w, SessionH * scale), _cachedSessionAvg, _sessionAvgStyle);

                GUI.Label(new Rect(x,       yRam, 40f * scale,   MemH * scale), "RAM",       _memLabelStyle);
                GUI.Label(new Rect(x + 40f * scale, yRam, w-40f * scale, MemH * scale), _cachedRam,  _memValueStyle);

                GUI.Label(new Rect(x,       yVram, 45f * scale,   MemH * scale), "VRAM",      _memLabelStyle);
                GUI.Label(new Rect(x + 45f * scale, yVram, w-45f * scale, MemH * scale), _cachedVram, _memValueStyle);

                GUI.Label(new Rect(x, yTime, w, TimeH * scale), _cachedTime, _timeStyle);
            }
        }

        // ─── Helpers ──────────────────────────────────────────────────────────────

        private Rect GetPanelRect(float w, float h, float scale)
        {
            float px, py;
            float sMargin = margin * scale;
            switch (corner)
            {
                case DisplayCorner.TopLeft:     px = sMargin;                      py = sMargin;                       break;
                case DisplayCorner.TopRight:    px = Screen.width - w - sMargin;   py = sMargin;                       break;
                case DisplayCorner.BottomLeft:  px = sMargin;                      py = Screen.height - h - sMargin;   break;
                default:                        px = Screen.width - w - sMargin;   py = Screen.height - h - sMargin;   break;
            }
            return new Rect(px, py, w, h);
        }

        private void EnsureStyles(float scale)
        {
            if (_stylesBuilt && Mathf.Approximately(scale, _lastScale)) return;
            _stylesBuilt = true;
            _lastScale   = scale;

            _fpsStyle = new GUIStyle
            {
                fontSize  = Mathf.RoundToInt(15 * scale),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleRight,
                normal    = { textColor = ColGood },
            };

            _avgStyle = new GUIStyle
            {
                fontSize  = Mathf.RoundToInt(11 * scale),
                fontStyle = FontStyle.Normal,
                alignment = TextAnchor.MiddleRight,
                normal    = { textColor = ColMuted },
            };

            _sessionAvgStyle = new GUIStyle
            {
                fontSize  = Mathf.RoundToInt(11 * scale),
                fontStyle = FontStyle.Normal,
                alignment = TextAnchor.MiddleRight,
                normal    = { textColor = ColValue },
            };

            _memLabelStyle = new GUIStyle
            {
                fontSize  = Mathf.RoundToInt(9 * scale),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                normal    = { textColor = ColMuted },
            };

            _memValueStyle = new GUIStyle
            {
                fontSize  = Mathf.RoundToInt(10 * scale),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleRight,
                normal    = { textColor = ColValue },
            };

            _timeStyle = new GUIStyle
            {
                fontSize  = Mathf.RoundToInt(10 * scale),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleRight,
                normal    = { textColor = ColMuted },
            };
        }
    }
}