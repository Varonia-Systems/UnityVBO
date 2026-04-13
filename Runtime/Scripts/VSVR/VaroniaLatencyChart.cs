using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Valve.VR;

#if STEAMVR_ENABLED
using Valve.VR;
#endif

namespace VaroniaBackOffice
{
    /// <summary>
    /// Graphe de latence ALVR temps réel via WebSocket ws://localhost:8082/api/events.
    /// Se connecte automatiquement au Start. Rendu via RenderTexture + GL — 1 seul draw call OnGUI par frame.
    /// </summary>
    public class VaroniaLatencyChart : MonoBehaviour
    {

#if STEAMVR_ENABLED

        // ─── ALVR Statistics ──────────────────────────────────────────────────────

        public class StatisticsSummaryItem
        {
            public double video_packets_total    { get; set; }
            public double video_packets_per_sec  { get; set; }
            public double video_mbytes_total     { get; set; }
            public double video_mbits_per_sec    { get; set; }
            public double total_latency_ms       { get; set; }
            public double network_latency_ms     { get; set; }
            public double encode_latency_ms      { get; set; }
            public double decode_latency_ms      { get; set; }
            public double packets_lost_total     { get; set; }
            public double packets_lost_per_sec   { get; set; }
            public double client_fps             { get; set; }
            public double server_fps             { get; set; }
            public double bitrate_mbps           => video_mbits_per_sec;
            public double battery_hmd            { get; set; }
            public bool   hmd_plugged            { get; set; }
        }

        // ─── Config ───────────────────────────────────────────────────────────────

        public enum DisplayCorner { TopLeft, TopRight, BottomLeft, BottomRight }

        [Header("Display")]
        [SerializeField] private DisplayCorner corner = DisplayCorner.BottomLeft;
        [SerializeField] private float         margin = 12f;
        [SerializeField] private Vector2       size   = new Vector2(340f, 160f);
        public float scaleFactor = 1f;

        [Header("VSVR")]
        [SerializeField] private string wsUrl          = "ws://localhost:8082/api/events";
        [SerializeField] private float  timeoutSeconds = 5f;

        [Header("Chart Display")]
        [SerializeField] private int   maxBars    = 100;
        [SerializeField] private float maxLatency = 200f;
        [SerializeField] private float maxTotalLatency = 200f;

        [Header("Network Latency Thresholds (ms)")]
        [SerializeField] private float orangeThreshold = 100f;
        [SerializeField] private float redThreshold    = 140f;

        [Header("Total Latency Thresholds (ms)")]
        [SerializeField] private float orangeTotalThreshold = 100f;
        [SerializeField] private float redTotalThreshold    = 140f;

        [Header("Encode Latency Thresholds (ms)")]
        [SerializeField] private float orangeEncodeThreshold = 10f;
        [SerializeField] private float redEncodeThreshold    = 20f;

        [Header("Decode Latency Thresholds (ms)")]
        [SerializeField] private float orangeDecodeThreshold = 10f;
        [SerializeField] private float redDecodeThreshold    = 20f;

        // ─── Colors ───────────────────────────────────────────────────────────────

        static readonly Color ColBg      = new Color(0.11f, 0.11f, 0.14f, 0.92f);
        static readonly Color ColGood    = new Color(0.30f, 0.85f, 0.65f, 1f);
        static readonly Color ColWarn    = new Color(1.00f, 0.75f, 0.30f, 1f);
        static readonly Color ColBad     = new Color(1.00f, 0.40f, 0.40f, 1f);
        static readonly Color ColMuted   = new Color(0.55f, 0.55f, 0.62f, 0.35f);
        static readonly Color ColMutedFg = new Color(0.55f, 0.55f, 0.62f, 1f);
        static readonly Color ColValue   = new Color(0.92f, 0.92f, 0.95f, 1f);
        static readonly Color ColDivider = new Color(1f,    1f,    1f,    0.06f);
        static readonly Color ColAvgLine = new Color(1f,    1f,    1f,    0.5f);

        // Pre-computed muted colors (avoid new Color() in hot path)
        static readonly Color ColGoodMuted = new Color(ColGood.r, ColGood.g, ColGood.b, 0.15f);
        static readonly Color ColWarnMuted = new Color(ColWarn.r, ColWarn.g, ColWarn.b, 0.15f);
        static readonly Color ColBadMuted  = new Color(ColBad.r,  ColBad.g,  ColBad.b,  0.15f);

        // ─── WebSocket state ──────────────────────────────────────────────────────

        private volatile bool         _needStop;
        private volatile bool         _wsConnected;
        private StatisticsSummaryItem _live;
        private volatile bool         _hasNewData;
        private DateTime              _lastMessageTime = DateTime.UtcNow;

        // ─── Avg ring buffer ──────────────────────────────────────────────────────
        private const int AvgCapacity = 600;
        private readonly StatisticsSummaryItem[] _avgRing = new StatisticsSummaryItem[AvgCapacity];
        private int _avgHead;
        private int _avgCount;

        // ─── Chart data ───────────────────────────────────────────────────────────
        private float[] _buffer;
        private float[] _totalBuffer;
        private int     _writeIdx;
        private float   _lastValue = -1f;

        private int     _lostStreamCount;
        private bool    _isCurrentlyLost;
        private bool    _timeoutEventFired;
        private float   _lastTickTime;

        // ─── RenderTexture for chart ──────────────────────────────────────────────
        private RenderTexture _chartRT;
        private Material      _glMat;
        private bool          _rtDirty = true;

        // chart sub-rect offsets (pixel coords inside the RT)
        private float _chartOfsX, _chartOfsY, _chartW, _chartH;

        // ─── Cached data (computed in Update 10Hz, consumed in OnGUI) ─────────────
        private readonly StatisticsSummaryItem _cachedAvg = new StatisticsSummaryItem();
        private double _cachedAvgTotal5s;
        private bool   _hasAvgData;

        private string _cachedVsvrLabel    = "VSVR";
        private string _cachedStatNet      = "—";
        private string _cachedStatTot      = "—";
        private string _cachedStatEnc      = "—";
        private string _cachedStatDec      = "—";
        private string _cachedStatFps      = "—";
        private string _cachedAvgNet       = "—";
        private string _cachedAvgTot       = "—";
        private string _cachedAvgEnc       = "—";
        private string _cachedAvgDec       = "—";
        private string _cachedAvgLost      = "—";
        private string _cachedAvgLineLabel = "";

        private Color _cachedColNet, _cachedColTot, _cachedColEnc, _cachedColDec;
        private Color _cachedAvgColNet, _cachedAvgColTot, _cachedAvgColEnc, _cachedAvgColDec;
        private bool  _cachedHasLive;

        // ─── Styles ───────────────────────────────────────────────────────────────

        private bool      _stylesBuilt;
        private float     _lastScale;
        private GUIStyle  _labelStyle;
        private GUIStyle  _pillStyle;
        private GUIStyle  _statLabelStyle;
        private GUIStyle  _statValueStyle;
        private Texture2D _texPillGood, _texPillBad;

        // ─── String format helpers (no boxing) ────────────────────────────────────

        private static string FormatF0(double v)
        {
            if (v < 0) return "—";
            return ((long)v).ToString();
        }

        private static string FormatF1(double v)
        {
            if (v < 0) return "—";
            long integer = (long)v;
            long frac = (long)((v - integer) * 10 + 0.5) % 10;
            return string.Concat(integer.ToString(), ".", frac.ToString());
        }

        // ─────────────────────────────────────────────────────────────────────────

        private void Awake()
        {
            _buffer      = new float[maxBars];
            _totalBuffer = new float[maxBars];
            for (int i = 0; i < maxBars; i++)
            {
                _buffer[i]      = -1f;
                _totalBuffer[i] = -1f;
            }
        }

        private bool _ready;

        private IEnumerator Start()
        {
            yield return new WaitForSeconds(2);

            CVRSystem vr = SteamVRBridge.GetSystem();
            if (vr == null) Destroy(this);

            var sb = new StringBuilder(256);
            ETrackedPropertyError err = ETrackedPropertyError.TrackedProp_Success;
            vr.GetStringTrackedDeviceProperty(0, ETrackedDeviceProperty.Prop_ModelNumber_String, sb, 256, ref err);
            string headsetName = sb.Length > 0 ? sb.ToString() : "—";

            if (headsetName != "Miramar" && headsetName != "Oculus Quest2")
                Destroy(this);

            EnsureRT();
            StartWebSocket();

            _ready = true;
        }

        private void OnDestroy()
        {
            _needStop = true;

            if (_chartRT)    { _chartRT.Release(); Destroy(_chartRT); }
            if (_glMat)      Destroy(_glMat);
            if (_texPillGood) Destroy(_texPillGood);
            if (_texPillBad)  Destroy(_texPillBad);
        }

        // ─── RenderTexture setup ──────────────────────────────────────────────────

        private void EnsureRT()
        {
            float scale = (Screen.height / 1080f) * scaleFactor;
            int rtW = (int)(size.x * scale);
            int rtH = (int)(size.y * scale);

            if (_chartRT == null || _chartRT.width != rtW || _chartRT.height != rtH)
            {
                if (_chartRT) { _chartRT.Release(); Destroy(_chartRT); }
                _chartRT = new RenderTexture(rtW, rtH, 0, RenderTextureFormat.ARGB32);
                _chartRT.filterMode = FilterMode.Point;
                _chartRT.hideFlags  = HideFlags.HideAndDontSave;
                _chartRT.Create();
                _rtDirty = true;
            }

            if (_glMat == null)
            {
                _glMat = new Material(Shader.Find("Hidden/Internal-Colored"));
                _glMat.hideFlags = HideFlags.HideAndDontSave;
                _glMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                _glMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                _glMat.SetInt("_Cull",     (int)UnityEngine.Rendering.CullMode.Off);
                _glMat.SetInt("_ZWrite",   0);
            }
        }

        // ─── Update ───────────────────────────────────────────────────────────────

        private void Update()
        {
            if (!_ready) return;

            if (!_timeoutEventFired && (DateTime.UtcNow - _lastMessageTime).TotalSeconds > 0.1 && Time.time > 5)
            {
                _timeoutEventFired = true;
                OnWebsocketTimeout();
            }

            if (Time.time - _lastTickTime >= 0.1f)
            {
                _lastTickTime = Time.time;

                if (_live != null)
                {
                    if (_hasNewData && _live.network_latency_ms != -1)
                        _timeoutEventFired = false;

                    AddLatencyValue((float)_live.network_latency_ms, (float)_live.total_latency_ms);

                    _avgRing[_avgHead] = _live;
                    _avgHead = (_avgHead + 1) % AvgCapacity;
                    if (_avgCount < AvgCapacity) _avgCount++;

                    _hasNewData = false;

                    RebuildCachedData();
                    _rtDirty = true;
                }
            }
        }

        private void RebuildCachedData()
        {
            var live = _live;
            _cachedHasLive = live != null;
            if (!_cachedHasLive) return;

            _cachedVsvrLabel = live.video_mbits_per_sec > 0
                ? string.Concat("VSVR (", FormatF1(live.video_mbits_per_sec), " Mbps)")
                : "VSVR";

            _cachedStatNet = live.network_latency_ms >= 0 ? string.Concat(FormatF0(live.network_latency_ms), "ms") : "—";
            _cachedStatTot = live.total_latency_ms   >= 0 ? string.Concat(FormatF0(live.total_latency_ms), "ms")   : "—";
            _cachedStatEnc = live.encode_latency_ms  >= 0 ? string.Concat(FormatF0(live.encode_latency_ms), "ms")  : "—";
            _cachedStatDec = live.decode_latency_ms  >= 0 ? string.Concat(FormatF0(live.decode_latency_ms), "ms")  : "—";
            _cachedStatFps = live.client_fps         >= 0 ? FormatF0(live.client_fps)                               : "—";

            _cachedColNet = GetColor((float)live.network_latency_ms);
            _cachedColTot = GetTotalColor((float)live.total_latency_ms, false);
            _cachedColEnc = GetEncodeColor((float)live.encode_latency_ms);
            _cachedColDec = GetDecodeColor((float)live.decode_latency_ms);

            _hasAvgData = _avgCount > 0;
            if (_hasAvgData)
            {
                CalculerMoyenneRing(_cachedAvg);

                _cachedAvgNet  = _cachedAvg.network_latency_ms >= 0 ? string.Concat(FormatF0(_cachedAvg.network_latency_ms), "ms") : "—";
                _cachedAvgTot  = _cachedAvg.total_latency_ms   >= 0 ? string.Concat(FormatF0(_cachedAvg.total_latency_ms), "ms")   : "—";
                _cachedAvgEnc  = _cachedAvg.encode_latency_ms  >= 0 ? string.Concat(FormatF0(_cachedAvg.encode_latency_ms), "ms")  : "—";
                _cachedAvgDec  = _cachedAvg.decode_latency_ms  >= 0 ? string.Concat(FormatF0(_cachedAvg.decode_latency_ms), "ms")  : "—";
                _cachedAvgLost = _lostStreamCount.ToString();

                _cachedAvgColNet = GetColor((float)_cachedAvg.network_latency_ms);
                _cachedAvgColTot = GetTotalColor((float)_cachedAvg.total_latency_ms, false);
                _cachedAvgColEnc = GetEncodeColor((float)_cachedAvg.encode_latency_ms);
                _cachedAvgColDec = GetDecodeColor((float)_cachedAvg.decode_latency_ms);

                int countFor5s = 300;
                int count = _avgCount < countFor5s ? _avgCount : countFor5s;
                double sumTotal = 0;
                int validCount = 0;
                for (int i = 0; i < count; i++)
                {
                    int idx = ((_avgHead - 1 - i) % AvgCapacity + AvgCapacity) % AvgCapacity;
                    double v = _avgRing[idx].total_latency_ms;
                    if (v != -1) { sumTotal += v; validCount++; }
                }
                _cachedAvgTotal5s = validCount > 0 ? sumTotal / validCount : 0;
                _cachedAvgLineLabel = string.Concat("avg ", FormatF1(_cachedAvgTotal5s));
            }
        }

        private void OnWebsocketTimeout()
        {
            Debug.Log("[VSVR] /!\\ Lost Streaming connection /!\\");

            var lostStats = new StatisticsSummaryItem
            {
                total_latency_ms   = -1,
                network_latency_ms = -1,
                encode_latency_ms  = -1,
                decode_latency_ms  = -1
            };

            _live       = lostStats;
            _hasNewData = true;
        }

        // ─── Public API ───────────────────────────────────────────────────────────

        public void AddLatencyValue(float latency, float totalLatency = -1f)
        {
            if (latency == -1f)
            {
                if (!_isCurrentlyLost) { _lostStreamCount++; _isCurrentlyLost = true; }
            }
            else
            {
                _isCurrentlyLost = false;
            }

            _buffer[_writeIdx]      = latency;
            _totalBuffer[_writeIdx] = totalLatency;
            _writeIdx = (_writeIdx + 1) % maxBars;
            _lastValue = latency;
        }

        public StatisticsSummaryItem GetAverage()
        {
            var result = new StatisticsSummaryItem();
            CalculerMoyenneRing(result);
            return result;
        }

        // ─── WebSocket ────────────────────────────────────────────────────────────

        private void StartWebSocket()
        {
            _needStop = false;

            Task.Run(async () =>
            {
                using (var ws = new ClientWebSocket())
                {
                    try
                    {
                        ws.Options.SetRequestHeader("X-ALVR", "true");
                        await ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None);
                        _wsConnected = true;
                        _lastMessageTime = DateTime.UtcNow;
                    }
                    catch
                    {
                        _wsConnected = false;
                        return;
                    }

                    var receiveBuffer = new byte[4096];
                    var bufferString  = "";

                    while (ws.State == WebSocketState.Open && !_needStop)
                    {
                        if ((DateTime.UtcNow - _lastMessageTime).TotalSeconds > timeoutSeconds)
                            _wsConnected = false;

                        try
                        {
                            var result = await ws.ReceiveAsync(
                                new ArraySegment<byte>(receiveBuffer), CancellationToken.None);

                            if (result.MessageType == WebSocketMessageType.Text)
                            {
                                _lastMessageTime = DateTime.UtcNow;
                                _wsConnected     = true;

                                bufferString += Encoding.UTF8.GetString(receiveBuffer, 0, result.Count);
                                var parts = SplitBuffer(bufferString);

                                if (parts.Count > 1)
                                {
                                    try
                                    {
                                        var tData = JObject.Parse(parts[0]);
                                        string evtId = tData["event_type"]?["id"]?.ToString();

                                        if (evtId == "StatisticsSummary")
                                        {
                                            var stats = JsonConvert.DeserializeObject<StatisticsSummaryItem>(
                                                tData["event_type"]["data"].ToString());

                                            _live       = stats;
                                            _hasNewData = true;

                                            if (stats.network_latency_ms > 13)
                                                Debug.Log($"[VSVR] /!\\ Network Latency Alert '{stats.network_latency_ms}' /!\\");
                                        }
                                    }
                                    catch { /* ignore parse errors */ }

                                    bufferString = parts[1];
                                }
                            }
                        }
                        catch { /* socket error */ }
                    }
                }

                _wsConnected = false;
            });
        }

        private static List<string> SplitBuffer(string input)
        {
            return input
                .Split(new[] { "{\"timestamp\"" }, StringSplitOptions.None)
                .Where(w => !string.IsNullOrEmpty(w))
                .Select(s => "{\"timestamp\"" + s)
                .ToList();
        }

        private void CalculerMoyenneRing(StatisticsSummaryItem result)
        {
            result.total_latency_ms    = 0;
            result.network_latency_ms  = 0;
            result.encode_latency_ms   = 0;
            result.decode_latency_ms   = 0;
            result.client_fps          = 0;
            result.video_mbits_per_sec = 0;

            if (_avgCount == 0) return;

            double sumTotal = 0, sumNet = 0, sumEnc = 0, sumDec = 0, sumFps = 0, sumBitrate = 0;
            int    cTotal = 0, cNet = 0, cEnc = 0, cDec = 0, cFps = 0, cBitrate = 0;

            for (int i = 0; i < _avgCount; i++)
            {
                int idx = ((_avgHead - _avgCount + i) % AvgCapacity + AvgCapacity) % AvgCapacity;
                var v = _avgRing[idx];
                if (v == null) continue;

                if (v.total_latency_ms   != -1) { sumTotal   += v.total_latency_ms;   cTotal++;   }
                if (v.network_latency_ms != -1) { sumNet     += v.network_latency_ms; cNet++;     }
                if (v.encode_latency_ms  != -1) { sumEnc     += v.encode_latency_ms;  cEnc++;     }
                if (v.decode_latency_ms  != -1) { sumDec     += v.decode_latency_ms;  cDec++;     }
                if (v.client_fps > 0)           { sumFps     += v.client_fps;          cFps++;     }
                if (v.video_mbits_per_sec > 0)  { sumBitrate += v.video_mbits_per_sec; cBitrate++; }
            }

            result.total_latency_ms    = cTotal   > 0 ? sumTotal   / cTotal   : 0;
            result.network_latency_ms  = cNet     > 0 ? sumNet     / cNet     : 0;
            result.encode_latency_ms   = cEnc     > 0 ? sumEnc     / cEnc     : 0;
            result.decode_latency_ms   = cDec     > 0 ? sumDec     / cDec     : 0;
            result.client_fps          = cFps     > 0 ? sumFps     / cFps     : 0;
            result.video_mbits_per_sec = cBitrate > 0 ? sumBitrate / cBitrate : 0;
        }

        // ─── GL Rendering into RenderTexture ──────────────────────────────────────

        private void RenderToRT()
        {
            if (_chartRT == null || _glMat == null) return;

            float scale = (Screen.height / 1080f) * scaleFactor;
            float W = _chartRT.width;
            float H = _chartRT.height;

            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = _chartRT;
            GL.Clear(true, true, Color.clear);

            _glMat.SetPass(0);
            GL.PushMatrix();
            GL.LoadPixelMatrix(0, W, H, 0); // top-left origin

            // ── Background ──
            GLRect(0, 0, W, H, ColBg);

            // ── Left accent bar ──
            GLRect(0, 0, 3f * scale, H, GetColor(_lastValue));

            float HeaderH = 22f * scale;
            float StatsH  = 22f * scale;
            float PadH    =  6f * scale;

            // ── Dividers ──
            float div1Y = HeaderH;
            GLRect(8f * scale, div1Y, W - 16f * scale, 1f * scale, ColDivider);

            float div2Y = div1Y + 1f * scale + StatsH;
            GLRect(8f * scale, div2Y, W - 16f * scale, 1f * scale, ColDivider);

            float div3Y = div2Y + 1f * scale + StatsH;
            GLRect(8f * scale, div3Y, W - 16f * scale, 1f * scale, ColDivider);

            // ── Chart bars ──
            float chartX = 8f * scale;
            float chartY = div3Y + PadH;
            float chartW = W - 16f * scale;
            float chartH = H - HeaderH - StatsH * 2f - PadH * 2f - 3f * scale;
            float barW   = chartW / maxBars;

            _chartOfsX = chartX;
            _chartOfsY = chartY;
            _chartW    = chartW;
            _chartH    = chartH;

            // Batch all quads in a single GL.Begin block for maximum throughput
            GL.Begin(GL.QUADS);

            for (int i = 0; i < maxBars; i++)
            {
                int idx = (_writeIdx + i) % maxBars;
                float bx = chartX + i * barW;
                float bw = barW > 1f * scale ? barW - 1f * scale : 1f * scale;

                // 1. Total Latency (muted background)
                float vt = _totalBuffer[idx];
                if (vt > 0f)
                {
                    float barHT = Mathf.Clamp01(vt / maxTotalLatency) * chartH;
                    float by = chartY + chartH - barHT;
                    GL.Color(GetTotalBarColor(vt));
                    GL.Vertex3(bx,      by,        0);
                    GL.Vertex3(bx + bw, by,        0);
                    GL.Vertex3(bx + bw, by + barHT, 0);
                    GL.Vertex3(bx,      by + barHT, 0);
                }
                else if (vt == -1f)
                {
                    GL.Color(ColMuted);
                    GL.Vertex3(bx,      chartY,          0);
                    GL.Vertex3(bx + bw, chartY,          0);
                    GL.Vertex3(bx + bw, chartY + chartH, 0);
                    GL.Vertex3(bx,      chartY + chartH, 0);
                }

                // 2. Network Latency (foreground)
                float v = _buffer[idx];
                float barH = v < 0f ? (v == -1f ? chartH : 2f * scale) : Mathf.Clamp01(v / maxLatency) * chartH;
                float ny = chartY + chartH - barH;
                GL.Color(v == -1f ? ColMuted : GetBarColor(v));
                GL.Vertex3(bx,      ny,        0);
                GL.Vertex3(bx + bw, ny,        0);
                GL.Vertex3(bx + bw, ny + barH, 0);
                GL.Vertex3(bx,      ny + barH, 0);
            }

            // 3. Total latency connecting line segments
            GL.Color(ColAvgLine);
            for (int i = 1; i < maxBars; i++)
            {
                int idxPrev = (_writeIdx + i - 1) % maxBars;
                int idxCurr = (_writeIdx + i) % maxBars;

                float vPrev = _totalBuffer[idxPrev];
                float vCurr = _totalBuffer[idxCurr];

                if (vPrev > 0f && vCurr > 0f)
                {
                    float yCurr = chartY + chartH - (Mathf.Clamp01(vCurr / maxTotalLatency) * chartH);
                    float lx = chartX + (i - 1) * barW;
                    GL.Vertex3(lx,               yCurr,            0);
                    GL.Vertex3(lx + barW,        yCurr,            0);
                    GL.Vertex3(lx + barW,        yCurr + 1f * scale, 0);
                    GL.Vertex3(lx,               yCurr + 1f * scale, 0);
                }
            }

            // 4. Average line
            if (_hasAvgData)
            {
                float lineY = chartY + chartH - (Mathf.Clamp01((float)_cachedAvgTotal5s / maxTotalLatency) * chartH);
                GL.Color(ColAvgLine);
                GL.Vertex3(chartX,          lineY,            0);
                GL.Vertex3(chartX + chartW, lineY,            0);
                GL.Vertex3(chartX + chartW, lineY + 1f * scale, 0);
                GL.Vertex3(chartX,          lineY + 1f * scale, 0);
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
            GL.Vertex3(x,     y,     0);
            GL.Vertex3(x + w, y,     0);
            GL.Vertex3(x + w, y + h, 0);
            GL.Vertex3(x,     y + h, 0);
            GL.End();
        }

        // ─── Color helpers ────────────────────────────────────────────────────────

        private Color GetBarColor(float v)
        {
            if (v < 0f)               return ColMuted;
            if (v >= redThreshold)    return ColBad;
            if (v >= orangeThreshold) return ColWarn;
            return ColGood;
        }

        private Color GetTotalBarColor(float v)
        {
            if (v < 0f)                    return ColMuted;
            if (v >= redTotalThreshold)    return ColBadMuted;
            if (v >= orangeTotalThreshold) return ColWarnMuted;
            return ColGoodMuted;
        }

        // ─── OnGUI — single RT blit + text labels only ───────────────────────────

        private bool show;

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
        }

        private void OnGUI()
        {
            if (!_ready || !show) return;

            EnsureStyles();
            EnsureRT();

            if (_rtDirty)
                RenderToRT();

            float scale = (Screen.height / 1080f) * scaleFactor;
            // ── Single draw call for entire panel ──
            Rect panel = GetPanelRect();
            GUI.DrawTexture(panel, _chartRT);

            // ── Text overlays ──

            float HeaderH = 22f * scale;
            float StatsH  = 22f * scale;

            // Header
            GUI.Label(
                new Rect(panel.x + 12f * scale, panel.y + 4f * scale, 150f * scale, HeaderH),
                _cachedVsvrLabel, _labelStyle);

            // Connection pill
            bool connected = _wsConnected;
            _pillStyle.normal.textColor  = connected ? ColGood : ColBad;
            _pillStyle.normal.background = connected ? _texPillGood : _texPillBad;
            GUI.Label(
                new Rect(panel.x + panel.width - 110f * scale, panel.y + 5f * scale, 106f * scale, HeaderH - 4f * scale),
                connected ? "● CONNECTED" : "● OFFLINE", _pillStyle);

            // Stats row
            float statsY = panel.y + HeaderH + 1f * scale;
            if (_cachedHasLive)
            {
                DrawStatCached(panel.x, statsY, StatsH, panel.width,
                    "NET",  _cachedStatNet,  _cachedColNet,
                    "TOT",  _cachedStatTot,  _cachedColTot,
                    "ENC",  _cachedStatEnc,  _cachedColEnc,
                    "DEC",  _cachedStatDec,  _cachedColDec,
                    "FPS",  _cachedStatFps,  ColValue);
            }
            else
            {
                GUI.Label(new Rect(panel.x + 12f * scale, statsY + 4f * scale, panel.width - 16f * scale, StatsH),
                    "En attente de données…", _statLabelStyle);
            }

            // Averages row
            float avgY = statsY + StatsH + 1f * scale;
            if (_hasAvgData)
            {
                DrawStatCached(panel.x, avgY, StatsH, panel.width,
                    "Avg.NET", _cachedAvgNet,  _cachedAvgColNet,
                    "Avg.TOT", _cachedAvgTot,  _cachedAvgColTot,
                    "AVG.ENC", _cachedAvgEnc,  _cachedAvgColEnc,
                    "AVG.DEC", _cachedAvgDec,  _cachedAvgColDec,
                    "LOST",    _cachedAvgLost, ColValue);
            }
            else
            {
                GUI.Label(new Rect(panel.x + 12f * scale, avgY + 4f * scale, panel.width - 16f * scale, StatsH),
                    "Calcul des moyennes…", _statLabelStyle);
            }

            // Avg line label
            if (_hasAvgData)
            {
                float lineY = _chartOfsY + _chartH - (Mathf.Clamp01((float)_cachedAvgTotal5s / maxTotalLatency) * _chartH);
                GUI.Label(new Rect(panel.x + _chartOfsX + 2f * scale, panel.y + lineY - 12f * scale, 50f * scale, 12f * scale),
                    _cachedAvgLineLabel, _labelStyle);
            }
        }

        // ─── Stats row helper (zero-alloc) ────────────────────────────────────────

        private void DrawStatCached(float px, float py, float h, float totalW,
            string l1, string v1, Color c1,
            string l2, string v2, Color c2,
            string l3, string v3, Color c3,
            string l4, string v4, Color c4,
            string l5, string v5, Color c5)
        {
            float scale = (Screen.height / 1080f) * scaleFactor;
            float colW = (totalW - 16f * scale) / 5f;
            DrawStatColCached(px + colW * 0f + 8f * scale, py, colW, h, l1, v1, c1);
            DrawStatColCached(px + colW * 1f + 8f * scale, py, colW, h, l2, v2, c2);
            DrawStatColCached(px + colW * 2f + 8f * scale, py, colW, h, l3, v3, c3);
            DrawStatColCached(px + colW * 3f + 8f * scale, py, colW, h, l4, v4, c4);
            DrawStatColCached(px + colW * 4f + 8f * scale, py, colW, h, l5, v5, c5);
        }

        private void DrawStatColCached(float x, float y, float w, float h,
            string label, string formattedValue, Color valueColor)
        {
            float scale = (Screen.height / 1080f) * scaleFactor;
            float halfH = h * 0.44f;
            GUI.Label(new Rect(x, y + 1f * scale,    w, halfH), label, _statLabelStyle);
            _statValueStyle.normal.textColor = valueColor;
            GUI.Label(new Rect(x, y + halfH, w, halfH + 2f * scale), formattedValue, _statValueStyle);
        }

        // ─── Helpers ──────────────────────────────────────────────────────────────

        private Rect GetPanelRect()
        {
            float scale = (Screen.height / 1080f) * scaleFactor;
            float w = size.x * scale, h = size.y * scale;
            float m = margin * scale;
            float x, y;
            switch (corner)
            {
                case DisplayCorner.TopLeft:     x = m;                       y = m;                        break;
                case DisplayCorner.TopRight:    x = Screen.width - w - m;    y = m;                        break;
                case DisplayCorner.BottomLeft:  x = m;                       y = Screen.height - h - m;    break;
                default:                        x = Screen.width - w - m;    y = Screen.height - h - m;    break;
            }
            return new Rect(x, y, w, h);
        }

        private Color GetColor(float v)
        {
            if (v < 0f)               return ColMuted;
            if (v >= redThreshold)    return ColBad;
            if (v >= orangeThreshold) return ColWarn;
            return ColGood;
        }

        private Color GetEncodeColor(float v)
        {
            if (v < 0f)                     return ColMuted;
            if (v >= redEncodeThreshold)    return ColBad;
            if (v >= orangeEncodeThreshold) return ColWarn;
            return ColGood;
        }

        private Color GetDecodeColor(float v)
        {
            if (v < 0f)                     return ColMuted;
            if (v >= redDecodeThreshold)    return ColBad;
            if (v >= orangeDecodeThreshold) return ColWarn;
            return ColGood;
        }

        private Color GetTotalColor(float v, bool muted = true)
        {
            float alpha = muted ? 0.25f : 1.0f;
            if (v < 0f)                    return ColMuted;
            if (v >= redTotalThreshold)    return new Color(ColBad.r, ColBad.g, ColBad.b, alpha);
            if (v >= orangeTotalThreshold) return new Color(ColWarn.r, ColWarn.g, ColWarn.b, alpha);
            return new Color(ColGood.r, ColGood.g, ColGood.b, alpha);
        }

        // ─── Styles ──────────────────────────────────────────────────────────────

        private void EnsureStyles()
        {
            float scale = (Screen.height / 1080f) * scaleFactor;
            if (_stylesBuilt && Mathf.Approximately(_lastScale, scale)) return;
            _stylesBuilt = true;
            _lastScale = scale;

            _texPillGood = MakeTex(new Color(ColGood.r, ColGood.g, ColGood.b, 0.15f));
            _texPillBad  = MakeTex(new Color(ColBad.r,  ColBad.g,  ColBad.b,  0.15f));

            _labelStyle = new GUIStyle
            {
                fontSize  = Mathf.RoundToInt(9 * scale),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                normal    = { textColor = ColMutedFg },
            };

            _pillStyle = new GUIStyle
            {
                fontSize  = Mathf.RoundToInt(9 * scale),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal    = { textColor = ColGood, background = _texPillGood },
                padding   = new RectOffset(Mathf.RoundToInt(4 * scale), Mathf.RoundToInt(4 * scale), Mathf.RoundToInt(2 * scale), Mathf.RoundToInt(2 * scale)),
            };

            _statLabelStyle = new GUIStyle
            {
                fontSize  = Mathf.RoundToInt(8 * scale),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                normal    = { textColor = ColMutedFg },
            };

            _statValueStyle = new GUIStyle
            {
                fontSize  = Mathf.RoundToInt(10 * scale),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                normal    = { textColor = ColValue },
            };
        }

        private static Texture2D MakeTex(Color col)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            t.SetPixel(0, 0, col);
            t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }

#endif
    }
}