using System;
using System.Collections;
using System.Text;
using UnityEngine;
#if STEAMVR_ENABLED
using Valve.VR;
#endif
using VaroniaBackOffice;

public enum HelmetState { Ok = 0, NoGameFocusOrMicroLag = 2, NoStreamOrPowerOff = 3 }
public enum TrackingState { Ok = 0, Strange = 1, Lost = 2, BigLost = 3, NO = 4 }

public class VBSLatencyChart : MonoBehaviour
{
#if STEAMVR_ENABLED

    public enum DisplayCorner { TopLeft, TopRight, BottomLeft, BottomRight }

    [Header("Display")]
    [SerializeField] private DisplayCorner corner = DisplayCorner.BottomRight;
    [SerializeField] private Vector2 size = new Vector2(340f, 130f);
    [SerializeField] private float margin = 12f;
    public float scaleFactor = 1f;

    [Header("Chart Config")]
    [SerializeField] private int maxBars = 80;
    [SerializeField] private float barHeight = 18f;
    [SerializeField] private float barGap = 3f;

    static readonly Color ColBg = new Color(0.11f, 0.11f, 0.14f, 0.92f);
    static readonly Color ColGood = new Color(0.30f, 0.85f, 0.65f, 1f);
    static readonly Color ColWarn = new Color(1.00f, 0.75f, 0.30f, 1f);
    static readonly Color ColBad = new Color(1.00f, 0.40f, 0.40f, 1f);
    static readonly Color ColOrange = new Color(1.00f, 0.55f, 0.20f, 1f);
    static readonly Color ColPurple = new Color(0.70f, 0.30f, 0.90f, 1f);
    static readonly Color ColMutedFg = new Color(0.55f, 0.55f, 0.62f, 1f);
    static readonly Color ColDivider = new Color(1f, 1f, 1f, 0.06f);

    // Bar history as color indices instead of Texture2D references
    // 0=empty, 1=green, 2=yellow, 3=orange, 4=purple, 5=red
    private byte[] _history;
    private int _writeIdx;
    private CVRSystem _vrSystem;
    private string _headsetName = "—";
    private bool _ready = false;

    private int _lagCount = 0;
    private float _totalLagTime = 0f;
    private bool _wasLagging = false;

    // Pre-allocated pose array (avoids new[] every 0.1s)
    private readonly TrackedDevicePose_t[] _poses = new TrackedDevicePose_t[1];

    public HelmetState helmet;
    public TrackingState tracking;

    // ─── RenderTexture + GL ───────────────────────────────────────────────────
    private RenderTexture _rt;
    private Material _glMat;
    private bool _rtDirty = true;

    // ─── Cached strings ───────────────────────────────────────────────────────
    private string _cachedHeader;
    private string _cachedHelmetStatus = "—";
    private string _cachedTrackingStatus = "—";
    private string _cachedLagCount = "0 EVENTS";
    private string _cachedLagTime = "0.0 SECONDS";

    private int _lastLagCount = -1;
    private float _lastLagTimeSnap = -1f;
    private HelmetState _lastHelmet = (HelmetState)(-1);
    private TrackingState _lastTracking = (TrackingState)(-1);

    // ─── Styles ───────────────────────────────────────────────────────────────
    private bool _stylesBuilt;
    private float _lastScale;
    private GUIStyle _headerStyle, _statusStyle, _valStyle, _statLabelStyle, _statValStyle;

    // ===== ANTI-CRASH =====
    private static bool _dead = false;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        _dead = false;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void InstallQuitHook()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.playModeStateChanged += (state) =>
        {
            if (state == UnityEditor.PlayModeStateChange.ExitingPlayMode)
                KillAll();
        };
#else
        Application.quitting += KillAll;
#endif
    }

    static void KillAll()
    {
        _dead = true;
        foreach (var chart in FindObjectsOfType<VBSLatencyChart>())
        {
            chart._vrSystem = null;
            chart._ready = false;
            chart.StopAllCoroutines();
            chart.enabled = false;
        }
    }

    private void Awake() => _history = new byte[maxBars];

    private IEnumerator Start()
    {
        yield return new WaitForSeconds(2);
        if (_dead) yield break;

        _vrSystem = SteamVRBridge.GetSystem();
        if (_vrSystem == null) { Destroy(this); yield break; }

        var sb = new StringBuilder(256);
        ETrackedPropertyError err = ETrackedPropertyError.TrackedProp_Success;
        _vrSystem.GetStringTrackedDeviceProperty(0, ETrackedDeviceProperty.Prop_ModelNumber_String, sb, 256, ref err);
        _headsetName = sb.Length > 0 ? sb.ToString() : "—";

        if (_headsetName != "Vive VBStreaming Focus3") { Destroy(this); yield break; }

        _cachedHeader = string.Concat("VBS MONITOR • ", _headsetName);

        EnsureRT();
        _ready = true;
        StartCoroutine(DataLoop());
    }

    private IEnumerator DataLoop()
    {
        var wait = new WaitForSeconds(0.1f);
        while (true)
        {
            if (_dead) yield break;

            UpdateStates();

            _history[_writeIdx] = GetCurrentStateIdx();
            _writeIdx = (_writeIdx + 1) % maxBars;

            bool isHelmetLagging = (helmet != HelmetState.Ok);

            if (isHelmetLagging)
            {
                _totalLagTime += 0.1f;
                _wasLagging = true;
            }
            else
            {
                if (_wasLagging)
                {
                    _lagCount++;
                    _wasLagging = false;
                }
            }

            // Rebuild cached strings only on change
            if (helmet != _lastHelmet)
            {
                _lastHelmet = helmet;
                _cachedHelmetStatus = GetHelmetFriendlyName(helmet);
            }
            if (tracking != _lastTracking)
            {
                _lastTracking = tracking;
                _cachedTrackingStatus = GetTrackingFriendlyName(tracking);
            }
            if (_lagCount != _lastLagCount)
            {
                _lastLagCount = _lagCount;
                _cachedLagCount = string.Concat(_lagCount.ToString(), " EVENTS");
            }
            // Snap lag time to 0.1 precision for string caching
            float snapped = Mathf.Round(_totalLagTime * 10f) / 10f;
            if (snapped != _lastLagTimeSnap)
            {
                _lastLagTimeSnap = snapped;
                // Format F1 without boxing
                long integer = (long)snapped;
                long frac = (long)((snapped - integer) * 10f + 0.5f) % 10;
                _cachedLagTime = string.Concat(integer.ToString(), ".", frac.ToString(), " SECONDS");
            }

            _rtDirty = true;

            yield return wait;
        }
    }

    private void UpdateStates()
    {
        if (_vrSystem == null || _dead) return;

        EDeviceActivityLevel act = _vrSystem.GetTrackedDeviceActivityLevel(0);
        helmet = (act == EDeviceActivityLevel.k_EDeviceActivityLevel_UserInteraction) ? HelmetState.Ok :
                 (act == EDeviceActivityLevel.k_EDeviceActivityLevel_Idle) ? HelmetState.NoGameFocusOrMicroLag : HelmetState.NoStreamOrPowerOff;

        _vrSystem.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseRawAndUncalibrated, 0, _poses);
        tracking = !_poses[0].bPoseIsValid ? TrackingState.Lost :
                   (_poses[0].eTrackingResult == ETrackingResult.Running_OK) ? TrackingState.Ok : TrackingState.Strange;
    }

    private static string GetHelmetFriendlyName(HelmetState state)
    {
        switch (state)
        {
            case HelmetState.Ok: return "READY / ACTIVE";
            case HelmetState.NoGameFocusOrMicroLag: return "FOCUS LOST / LAG";
            case HelmetState.NoStreamOrPowerOff: return "OFFLINE / DISCONNECTED";
            default: return "UNKNOWN";
        }
    }

    private static string GetTrackingFriendlyName(TrackingState state)
    {
        switch (state)
        {
            case TrackingState.Ok: return "STABLE";
            case TrackingState.Strange: return "POOR QUALITY";
            case TrackingState.Lost: return "TRACKING LOST";
            default: return "ERROR";
        }
    }

    /// <summary>Returns a byte index: 0=empty, 1=green, 2=yellow, 3=orange, 4=purple, 5=red</summary>
    private byte GetCurrentStateIdx()
    {
        if (helmet == HelmetState.NoStreamOrPowerOff) return 5;
        if (helmet == HelmetState.NoGameFocusOrMicroLag) return 4;
        if (tracking == TrackingState.Lost) return 3;
        if (tracking == TrackingState.Strange) return 2;
        return 1;
    }

    private static Color StateIdxToColor(byte idx)
    {
        switch (idx)
        {
            case 1: return ColGood;
            case 2: return ColWarn;
            case 3: return ColOrange;
            case 4: return ColPurple;
            case 5: return ColBad;
            default: return Color.clear;
        }
    }

    // ─── RenderTexture setup ──────────────────────────────────────────────────

    private void EnsureRT()
    {
        float scale = (Screen.height / 1080f) * scaleFactor;
        int rtW = (int)(size.x * scale);
        int rtH = (int)(size.y * scale);

        if (_rt == null || _rt.width != rtW || _rt.height != rtH)
        {
            if (_rt) { _rt.Release(); Destroy(_rt); }
            _rt = new RenderTexture(rtW, rtH, 0, RenderTextureFormat.ARGB32);
            _rt.filterMode = FilterMode.Point;
            _rt.hideFlags = HideFlags.HideAndDontSave;
            _rt.Create();
            _rtDirty = true;
        }

        if (_glMat == null)
        {
            _glMat = new Material(Shader.Find("Hidden/Internal-Colored"));
            _glMat.hideFlags = HideFlags.HideAndDontSave;
            _glMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _glMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            _glMat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            _glMat.SetInt("_ZWrite", 0);
        }
    }

    // ─── GL rendering ─────────────────────────────────────────────────────────

    private void RenderToRT()
    {
        if (_rt == null || _glMat == null) return;

        float scale = (Screen.height / 1080f) * scaleFactor;
        float W = _rt.width;
        float H = _rt.height;

        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = _rt;
        GL.Clear(true, true, Color.clear);

        _glMat.SetPass(0);
        GL.PushMatrix();
        GL.LoadPixelMatrix(0, W, H, 0);

        // Background
        GLRect(0, 0, W, H, ColBg);

        // Left accent bar
        GLRect(0, 0, 3f * scale, H, ColGood);

        // Dividers
        GLRect(8f * scale, 26f * scale, W - 16f * scale, 1f * scale, ColDivider);

        float statsY = (32f + 32f) * scale;
        GLRect(8f * scale, statsY - 4f * scale, W - 16f * scale, 1f * scale, ColDivider);

        // Chart bars
        float chartW = W - 16f * scale;
        float bw = chartW / maxBars;
        float chartY = H - (barHeight * scale) - 8f * scale;

        GL.Begin(GL.QUADS);
        for (int i = 0; i < maxBars; i++)
        {
            int idx = (_writeIdx + i) % maxBars;
            byte state = _history[idx];
            if (state == 0) continue;

            Color col = StateIdxToColor(state);
            float bx = 8f * scale + i * bw;
            float w = bw - (barGap * scale);
            if (w < 1f) w = 1f;

            GL.Color(col);
            GL.Vertex3(bx,     chartY,                    0);
            GL.Vertex3(bx + w, chartY,                    0);
            GL.Vertex3(bx + w, chartY + (barHeight * scale), 0);
            GL.Vertex3(bx,     chartY + (barHeight * scale), 0);
        }
        GL.End();

        GL.PopMatrix();
        RenderTexture.active = prev;
        _rtDirty = false;
    }

    private static void GLRect(float x, float y, float w, float h, Color c)
    {
        GL.Begin(GL.QUADS);
        GL.Color(c);
        GL.Vertex3(x, y, 0);
        GL.Vertex3(x + w, y, 0);
        GL.Vertex3(x + w, y + h, 0);
        GL.Vertex3(x, y + h, 0);
        GL.End();
    }

    // ─── OnGUI — single RT blit + text labels ────────────────────────────────

    private void OnGUI()
    {
        if (!_ready || _dead) return;
        EnsureStyles();
        EnsureRT();

        if (_rtDirty)
            RenderToRT();

        float scale = (Screen.height / 1080f) * scaleFactor;
        Rect panel = GetPanelRect();

        // Single draw call for background + bars + dividers + accent
        GUI.DrawTexture(panel, _rt);

        // Text overlays
        GUI.Label(new Rect(panel.x + 12 * scale, panel.y + 6 * scale, 200 * scale, 20 * scale), _cachedHeader, _headerStyle);

        float statusY = panel.y + 32 * scale;
        GUI.Label(new Rect(panel.x + 12 * scale, statusY, 150 * scale, 20 * scale), "HELMET STATUS", _statusStyle);
        GUI.Label(new Rect(panel.x + 12 * scale, statusY + 12 * scale, 180 * scale, 20 * scale), _cachedHelmetStatus, _valStyle);

        GUI.Label(new Rect(panel.x + 185 * scale, statusY, 150 * scale, 20 * scale), "TRACKING QUALITY", _statusStyle);
        GUI.Label(new Rect(panel.x + 185 * scale, statusY + 12 * scale, 140 * scale, 20 * scale), _cachedTrackingStatus, _valStyle);

        float statsY2 = statusY + 32 * scale;
        GUI.Label(new Rect(panel.x + 12 * scale, statsY2, 100 * scale, 20 * scale), "STREAM DROPS", _statLabelStyle);
        GUI.Label(new Rect(panel.x + 12 * scale, statsY2 + 11 * scale, 100 * scale, 20 * scale), _cachedLagCount, _statValStyle);

        GUI.Label(new Rect(panel.x + 120 * scale, statsY2, 150 * scale, 20 * scale), "TOTAL DOWN TIME", _statLabelStyle);
        GUI.Label(new Rect(panel.x + 120 * scale, statsY2 + 11 * scale, 150 * scale, 20 * scale), _cachedLagTime, _statValStyle);
    }

    private Rect GetPanelRect()
    {
        float scale = (Screen.height / 1080f) * scaleFactor;
        float w = size.x * scale;
        float h = size.y * scale;
        float m = margin * scale;

        float x = (corner == DisplayCorner.TopRight || corner == DisplayCorner.BottomRight) ? Screen.width - w - m : m;
        float y = (corner == DisplayCorner.BottomLeft || corner == DisplayCorner.BottomRight) ? Screen.height - h - m : m;
        return new Rect(x, y, w, h);
    }

    private void EnsureStyles()
    {
        float scale = (Screen.height / 1080f) * scaleFactor;
        if (_stylesBuilt && Mathf.Approximately(_lastScale, scale)) return;
        _stylesBuilt = true;
        _lastScale = scale;

        _headerStyle = new GUIStyle { fontSize = Mathf.RoundToInt(9 * scale), fontStyle = FontStyle.Bold, normal = { textColor = ColMutedFg } };
        _statusStyle = new GUIStyle { fontSize = Mathf.RoundToInt(7 * scale), fontStyle = FontStyle.Bold, normal = { textColor = ColMutedFg } };
        _valStyle = new GUIStyle { fontSize = Mathf.RoundToInt(10 * scale), fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
        _statLabelStyle = new GUIStyle { fontSize = Mathf.RoundToInt(7 * scale), fontStyle = FontStyle.Normal, normal = { textColor = ColMutedFg } };
        _statValStyle = new GUIStyle { fontSize = Mathf.RoundToInt(9 * scale), fontStyle = FontStyle.Bold, normal = { textColor = ColOrange } };
    }

    void OnDisable()
    {
        _vrSystem = null;
        _ready = false;
    }

    private void OnDestroy()
    {
        if (_rt) { _rt.Release(); Destroy(_rt); }
        if (_glMat) Destroy(_glMat);
    }

    void OnApplicationQuit()
    {
        _dead = true;
        _vrSystem = null;
        _ready = false;
        SteamVRBridge.SafeShutdown();
    }
#endif
}