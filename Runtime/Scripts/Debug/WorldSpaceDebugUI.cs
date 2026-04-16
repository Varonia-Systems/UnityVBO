using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace VaroniaBackOffice
{
    /// <summary>
    /// Crée un Canvas World Space devant la MainCamera (compatible VR) et y duplique
    /// les overlays de VaroniaLatencyChart, VaroniaInfoDisplay, VaroniaFPSDisplay et
    /// AdvBoundaryDebug — uniquement si un Canvas en mode WorldSpace existe déjà dans la scène.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class WorldSpaceDebugUI : MonoBehaviour
    {
        // ─── Config ───────────────────────────────────────────────────────────────

        [Header("Canvas World Space")]
        [Tooltip("Distance devant la caméra (en mètres). ~1.2m recommandé en VR.")]
        [SerializeField] private float canvasDistance   = 1.2f;
        [Tooltip("Largeur du canvas en unités monde.")]
        [SerializeField] private float canvasWorldWidth = 1.4f;
        [Tooltip("Hauteur du canvas en unités monde.")]
        [SerializeField] private float canvasWorldHeight = 0.9f;
        [Tooltip("Résolution de rendu du canvas (pixels).")]
        [SerializeField] private Vector2Int canvasResolution = new Vector2Int(1400, 900);
        [Tooltip("Offset vertical par rapport au centre de la caméra.")]
        [SerializeField] private float verticalOffset = 0f;
        [Tooltip("Vitesse de suivi de la caméra (lerp). 0 = fixe, 1 = instantané.")]
        [SerializeField] private float followSpeed = 2f;
        [Tooltip("Si true, le canvas suit la rotation de la caméra en continu.")]
        [SerializeField] private bool  alwaysFaceCamera = true;
        [Tooltip("Si true, le canvas s'affiche toujours au-dessus de la géométrie 3D (ZTest Always).")]
        [SerializeField] private bool  alwaysOnTop = true;

        [Header("Panels")]
        [SerializeField] private float panelPadding   = 12f;
        [SerializeField] private float panelSpacing   = 8f;

        [Header("Cycle Key")]
        [Tooltip("Touche pour cycler les panneaux. Défaut : F9")]
#if ENABLE_INPUT_SYSTEM
        private Key cycleKey_ = Key.F9;
#else
 private KeyCode cycleKey_ = KeyCode.F9;
#endif

        // ─── Colors (même palette que les scripts Varonia) ────────────────────────

        static readonly Color ColBg      = new Color(0.11f, 0.11f, 0.14f, 0.92f);
        static readonly Color ColGood    = new Color(0.30f, 0.85f, 0.65f, 1f);
        static readonly Color ColWarn    = new Color(1.00f, 0.75f, 0.30f, 1f);
        static readonly Color ColBad     = new Color(1.00f, 0.40f, 0.40f, 1f);
        static readonly Color ColMutedFg = new Color(0.55f, 0.55f, 0.62f, 1f);
        static readonly Color ColValue   = new Color(0.92f, 0.92f, 0.95f, 1f);

        // ─── Runtime ──────────────────────────────────────────────────────────────

        // 0=caché, 1=FPS, 2=FPS+Latency, 3=FPS+Latency+Boundary
        private int       _displayState = 0;

        private Canvas    _canvas;
        private Transform _canvasTransform;
        private Camera    _cam;
        private bool      _initialized;

        // Références aux composants source (trouvés dans la scène)
        private MonoBehaviour _fpsDisplay;
        private MonoBehaviour _infoDisplay;
        private MonoBehaviour _latencyChart;
        private MonoBehaviour _boundaryDebug;

        // Panels UI
        private PanelFPS      _panelFps;
        private PanelInfo     _panelInfo;
        private PanelLatency  _panelLatency;
        private PanelBoundary _panelBoundary;

        // ─────────────────────────────────────────────────────────────────────────

        private void Start()
        {
            _cam = Camera.main;
            if (_cam == null)
            {
                Debug.LogWarning("[WorldSpaceDebugUI] MainCamera introuvable au Start, nouvelle tentative dans Update.");
            }

            FindSourceComponents();
            BuildCanvas();
            _initialized = true;
        }

        private void Update()
        {
            if (!_initialized) return;

            // Récupère la caméra si pas encore trouvée (VR lazy init)
            if (_cam == null)
            {
                _cam = Camera.main;
                if (_cam == null) return;
            }

            HandleCycleInput();

            if (_displayState == 0)
            {
                _canvasTransform.gameObject.SetActive(false);
                return;
            }

            _canvasTransform.gameObject.SetActive(true);
            FollowCamera();
            RefreshPanels();
        }

        private void HandleCycleInput()
        {
#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null && Keyboard.current[cycleKey_].wasPressedThisFrame)
            {
                _displayState = (_displayState + 1) % 4;
                if (_displayState == 2 && _latencyChart == null)
                    _displayState = 3;
            }
#else
            if (Input.GetKeyDown(cycleKey_))
            {
                _displayState = (_displayState + 1) % 4;
                if (_displayState == 2 && _latencyChart == null)
                    _displayState = 3;
            }
#endif
        }

        // ─── Source components ────────────────────────────────────────────────────

        private void FindSourceComponents()
        {
            _fpsDisplay    = FindOfType("VaroniaBackOffice.VaroniaFPSDisplay");
            _infoDisplay   = FindOfType("VaroniaBackOffice.VaroniaInfoDisplay");
            _latencyChart  = FindOfType("VaroniaBackOffice.VaroniaLatencyChart");
            _boundaryDebug = FindOfType("VaroniaBackOffice.AdvBoundaryDebug");
        }

        private static MonoBehaviour FindOfType(string fullTypeName)
        {
            var all = FindObjectsOfType<MonoBehaviour>();
            foreach (var mb in all)
            {
                if (mb.GetType().FullName == fullTypeName)
                    return mb;
            }
            return null;
        }

        // ─── Canvas build ─────────────────────────────────────────────────────────

        private void BuildCanvas()
        {
            var go = new GameObject("WorldSpaceDebugCanvas");
            go.hideFlags = HideFlags.DontSave;
            DontDestroyOnLoad(go);

            _canvas = go.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;

            var scaler = go.AddComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = 10f;

            go.AddComponent<GraphicRaycaster>();

            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(canvasResolution.x, canvasResolution.y);

            float scale = canvasWorldWidth / canvasResolution.x;
            go.transform.localScale = new Vector3(scale, scale, scale);

            _canvasTransform = go.transform;

            // Position initiale
            if (_cam != null)
                SnapToCamera();

            // Construit les 4 panneaux
            float px = canvasResolution.x;
            float py = canvasResolution.y;
            float pad = panelPadding;
            float sp  = panelSpacing;

            // Layout : 2 colonnes, 2 lignes
            // [FPS | Info]
            // [Latency | Boundary]
            float halfW = (px - pad * 2f - sp) / 2f;
            float halfH = (py - pad * 2f - sp) / 2f;

            _panelFps      = new PanelFPS     (go.transform, new Rect(pad,              pad,              halfW, halfH), this);
            _panelInfo     = new PanelInfo    (go.transform, new Rect(pad + halfW + sp,  pad,              halfW, halfH), this);
            _panelLatency  = new PanelLatency (go.transform, new Rect(pad,              pad + halfH + sp, halfW, halfH), this);
            _panelBoundary = new PanelBoundary(go.transform, new Rect(pad + halfW + sp,  pad + halfH + sp, halfW, halfH), this);

            if (alwaysOnTop)
                ApplyAlwaysOnTop();
        }

        // ─── Always On Top ────────────────────────────────────────────────────────

        private Material _alwaysOnTopMat;

        private void ApplyAlwaysOnTop()
        {
            if (_alwaysOnTopMat == null)
            {
                var shader = Shader.Find("UI/AlwaysOnTop");
                if (shader == null) shader = Shader.Find("UI/Default"); // fallback
                if (shader == null) return;
                _alwaysOnTopMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }

            foreach (var graphic in _canvas.GetComponentsInChildren<Graphic>(true))
                graphic.material = _alwaysOnTopMat;
        }

        // ─── Camera follow ────────────────────────────────────────────────────────

        private void FollowCamera()
        {
            if (alwaysFaceCamera)
            {
                Vector3 targetPos = _cam.transform.position
                    + _cam.transform.forward * canvasDistance
                    + Vector3.up * verticalOffset;

                _canvasTransform.position = Vector3.Lerp(
                    _canvasTransform.position, targetPos,
                    followSpeed > 0f ? followSpeed * Time.deltaTime : 1f);

                Quaternion targetRot = Quaternion.LookRotation(
                    _canvasTransform.position - _cam.transform.position);
                _canvasTransform.rotation = Quaternion.Slerp(
                    _canvasTransform.rotation, targetRot,
                    followSpeed > 0f ? followSpeed * Time.deltaTime : 1f);
            }
        }

        private void SnapToCamera()
        {
            _canvasTransform.position = _cam.transform.position
                + _cam.transform.forward * canvasDistance
                + Vector3.up * verticalOffset;
            _canvasTransform.rotation = Quaternion.LookRotation(
                _canvasTransform.position - _cam.transform.position);
        }

        // ─── Panels refresh ───────────────────────────────────────────────────────

        private void RefreshPanels()
        {
            // Re-cherche les composants manquants (chargement tardif)
            if (_fpsDisplay    == null) _fpsDisplay    = FindOfType("VaroniaBackOffice.VaroniaFPSDisplay");
            if (_infoDisplay   == null) _infoDisplay   = FindOfType("VaroniaBackOffice.VaroniaInfoDisplay");
            if (_latencyChart  == null) _latencyChart  = FindOfType("VaroniaBackOffice.VaroniaLatencyChart");
            if (_boundaryDebug == null) _boundaryDebug = FindOfType("VaroniaBackOffice.AdvBoundaryDebug");

            // Ré-applique always-on-top après chaque changement de visibilité (SetActive reset les matériaux)
            if (alwaysOnTop) ApplyAlwaysOnTop();

            // État 1 : FPS seulement
            // État 2 : FPS + Latency
            // État 3 : FPS + Latency + Boundary
            bool showFPS      = _displayState >= 1;
            bool showLatency  = _displayState >= 2;
            bool showBoundary = _displayState >= 3;
            bool showInfo     = false; // jamais affiché dans le cycle

            _panelFps     .SetVisible(showFPS);
            _panelInfo    .SetVisible(showInfo);
            _panelLatency .SetVisible(showLatency);
            _panelBoundary.SetVisible(showBoundary);

            if (showFPS)      _panelFps     .Refresh(_fpsDisplay);
            if (showLatency)  _panelLatency .Refresh(_latencyChart);
            if (showBoundary) _panelBoundary.Refresh(_boundaryDebug);

            RepositionPanels(showFPS, showLatency, showBoundary);
        }

        private void RepositionPanels(bool fps, bool latency, bool boundary)
        {
            // Compte les panneaux visibles
            int count = (fps ? 1 : 0) + (latency ? 1 : 0) + (boundary ? 1 : 0);
            if (count == 0) return;

            float px  = canvasResolution.x;
            float py  = canvasResolution.y;
            float pad = panelPadding;
            float sp  = panelSpacing;

            if (count == 1)
            {
                // Panneau garde sa taille originale (quart du canvas) et est centré
                float halfW = (px - pad * 2f - sp) / 2f;
                float halfH = (py - pad * 2f - sp) / 2f;
                float cx = (px - halfW) / 2f;
                float cy = (py - halfH) / 2f;
                Rect centeredRect = new Rect(cx, cy, halfW, halfH);
                if (fps)      _panelFps     .SetRect(centeredRect);
                if (latency)  _panelLatency .SetRect(centeredRect);
                if (boundary) _panelBoundary.SetRect(centeredRect);
            }
            else if (count == 2)
            {
                // 2 panneaux : taille originale, côte à côte, centrés verticalement
                float halfW = (px - pad * 2f - sp) / 2f;
                float halfH = (py - pad * 2f - sp) / 2f;
                float totalW = halfW * 2f + sp;
                float startX = (px - totalW) / 2f;
                float cy = (py - halfH) / 2f;
                var panels = new System.Collections.Generic.List<PanelBase>();
                if (fps)      panels.Add(_panelFps);
                if (latency)  panels.Add(_panelLatency);
                if (boundary) panels.Add(_panelBoundary);
                panels[0].SetRect(new Rect(startX,              cy, halfW, halfH));
                panels[1].SetRect(new Rect(startX + halfW + sp, cy, halfW, halfH));
            }
            else
            {
                // 3 panneaux : taille originale, centrés, layout 2 colonnes
                float halfW = (px - pad * 2f - sp) / 2f;
                float halfH = (py - pad * 2f - sp) / 2f;
                float totalW = halfW * 2f + sp;
                float totalH = halfH * 2f + sp;
                float startX = (px - totalW) / 2f;
                float startY = (py - totalH) / 2f;
                _panelFps     .SetRect(new Rect(startX,              startY + halfH + sp, halfW, halfH));
                _panelLatency .SetRect(new Rect(startX,              startY,              halfW, halfH));
                _panelBoundary.SetRect(new Rect(startX + halfW + sp, startY,              halfW, halfH));
            }
        }

        private void OnDestroy()
        {
            if (_canvas != null)
                Destroy(_canvas.gameObject);
            if (_alwaysOnTopMat != null)
                Destroy(_alwaysOnTopMat);
        }

        // ─── Reflection helpers ───────────────────────────────────────────────────

        internal static T GetField<T>(object obj, string fieldName)
        {
            if (obj == null) return default;
            var fi = obj.GetType().GetField(fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (fi == null) return default;
            var val = fi.GetValue(obj);
            if (val is T t) return t;
            return default;
        }

        internal static T GetProp<T>(object obj, string propName)
        {
            if (obj == null) return default;
            var pi = obj.GetType().GetProperty(propName,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (pi == null) return default;
            var val = pi.GetValue(obj);
            if (val is T t) return t;
            return default;
        }

        // ─── Panel base ───────────────────────────────────────────────────────────

        private abstract class PanelBase
        {
            protected readonly GameObject Root;
            protected readonly Image      Background;
            protected readonly Image      AccentBar;
            protected Rect             CanvasRect { get => CanvasRectMutable; }
            protected Rect             CanvasRectMutable;
            protected readonly WorldSpaceDebugUI Owner;

            protected PanelBase(Transform parent, Rect rect, WorldSpaceDebugUI owner)
            {
                Owner      = owner;
                CanvasRectMutable = rect;

                Root = new GameObject(GetType().Name);
                Root.transform.SetParent(parent, false);

                var rt = Root.AddComponent<RectTransform>();
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.zero;
                rt.pivot     = Vector2.zero;
                rt.anchoredPosition = new Vector2(rect.x, rect.y);
                rt.sizeDelta        = new Vector2(rect.width, rect.height);

                // Background
                var bgGo = new GameObject("BG");
                bgGo.transform.SetParent(Root.transform, false);
                var bgRt = bgGo.AddComponent<RectTransform>();
                bgRt.anchorMin = Vector2.zero; bgRt.anchorMax = Vector2.one;
                bgRt.offsetMin = Vector2.zero; bgRt.offsetMax = Vector2.zero;
                Background = bgGo.AddComponent<Image>();
                Background.color = ColBg;

                // Accent bar (3px left)
                var acGo = new GameObject("Accent");
                acGo.transform.SetParent(Root.transform, false);
                var acRt = acGo.AddComponent<RectTransform>();
                acRt.anchorMin = Vector2.zero;
                acRt.anchorMax = new Vector2(0f, 1f);
                acRt.offsetMin = Vector2.zero;
                acRt.offsetMax = new Vector2(3f, 0f);
                AccentBar = acGo.AddComponent<Image>();
                AccentBar.color = ColGood;

                BuildContent();
            }

            protected abstract void BuildContent();
            public abstract void Refresh(MonoBehaviour source);
            public void SetVisible(bool visible) { Root.SetActive(visible); }

            public void SetRect(Rect rect)
            {
                CanvasRectMutable = rect;
                var rt = Root.GetComponent<RectTransform>();
                rt.anchoredPosition = new Vector2(rect.x, rect.y);
                rt.sizeDelta        = new Vector2(rect.width, rect.height);
            }

            protected Text MakeLabel(Transform parent, Rect r, string txt, int fontSize, Color col, TextAnchor anchor = TextAnchor.MiddleLeft)
            {
                var go = new GameObject("Label_" + txt);
                go.transform.SetParent(parent, false);
                var rt = go.AddComponent<RectTransform>();
                rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.zero;
                rt.pivot     = Vector2.zero;
                rt.anchoredPosition = new Vector2(r.x, r.y);
                rt.sizeDelta        = new Vector2(r.width, r.height);
                var t = go.AddComponent<Text>();
                t.text      = txt;
                t.fontSize  = fontSize;
                t.color     = col;
                t.alignment = anchor;
                
                string fontName;
                
            #if UNITY_2022_2_OR_NEWER
            fontName = "LegacyRuntime.ttf";
          #else
                fontName = "Arial.ttf";
           #endif
                
                t.font      = Resources.GetBuiltinResource<Font>(fontName);
                t.fontStyle = FontStyle.Bold;
                return t;
            }

            protected float W => CanvasRect.width;
            protected float H => CanvasRect.height;
        }

        // ─── Panel FPS ────────────────────────────────────────────────────────────

        private class PanelFPS : PanelBase
        {
            private Text _fps, _avg, _ram, _vram;

            public PanelFPS(Transform p, Rect r, WorldSpaceDebugUI o) : base(p, r, o) { }

            protected override void BuildContent()
            {
                float titleH = 70f;
                float bodyH  = H - titleH;
                float rowH   = bodyH / 4f;
                float lx = 10f, lw = W * 0.55f;
                float vx = W * 0.55f, vw = W * 0.42f;

                MakeLabel(Root.transform, new Rect(lx, H - titleH,        W - 20f, titleH), "FPS DISPLAY", 60, ColMutedFg);

                MakeLabel(Root.transform, new Rect(lx, bodyH - rowH,     lw, rowH), "FPS",  80, ColMutedFg);
                MakeLabel(Root.transform, new Rect(lx, bodyH - rowH * 2, lw, rowH), "AVG",  80, ColMutedFg);
                MakeLabel(Root.transform, new Rect(lx, bodyH - rowH * 3, lw, rowH), "RAM",  80, ColMutedFg);
                MakeLabel(Root.transform, new Rect(lx, bodyH - rowH * 4, lw, rowH), "VRAM", 80, ColMutedFg);

                _fps  = MakeLabel(Root.transform, new Rect(vx, bodyH - rowH,     vw, rowH), "—", 95, ColValue, TextAnchor.MiddleRight);
                _avg  = MakeLabel(Root.transform, new Rect(vx, bodyH - rowH * 2, vw, rowH), "—", 95, ColValue, TextAnchor.MiddleRight);
                _ram  = MakeLabel(Root.transform, new Rect(vx, bodyH - rowH * 3, vw, rowH), "—", 85, ColValue, TextAnchor.MiddleRight);
                _vram = MakeLabel(Root.transform, new Rect(vx, bodyH - rowH * 4, vw, rowH), "—", 85, ColValue, TextAnchor.MiddleRight);
            }

            public override void Refresh(MonoBehaviour source)
            {
                if (source == null) { Root.SetActive(false); return; }
                Root.SetActive(true);

                int fps  = GetField<int>(source, "_fps");
                int avg  = GetField<int>(source, "_avg");
                int ram  = GetField<int>(source, "_ramMb");
                int vram = GetField<int>(source, "_vramMb");

                _fps.text  = fps  + " fps";
                _avg.text  = avg  + " fps";
                _ram.text  = ram  + " MB";
                _vram.text = vram + " MB";

                Color fpsCol = fps >= 55 ? ColGood : fps >= 30 ? ColWarn : ColBad;
                _fps.color = fpsCol;
                AccentBar.color = fpsCol;
            }
        }

        // ─── Panel Info ───────────────────────────────────────────────────────────

        private class PanelInfo : PanelBase
        {
            private Text _game, _pkg, _mqtt;

            public PanelInfo(Transform p, Rect r, WorldSpaceDebugUI o) : base(p, r, o) { }

            protected override void BuildContent()
            {
                float titleH = 70f;
                float bodyH  = H - titleH;
                float rowH   = bodyH / 3f;
                float lx = 10f, lw = W * 0.5f;
                float vx = W * 0.5f, vw = W * 0.47f;

                MakeLabel(Root.transform, new Rect(lx, H - titleH, W - 20f, titleH), "INFO DISPLAY", 60, ColMutedFg);

                MakeLabel(Root.transform, new Rect(lx, bodyH - rowH,     lw, rowH), "GAME",        80, ColMutedFg);
                MakeLabel(Root.transform, new Rect(lx, bodyH - rowH * 2, lw, rowH), "BACK OFFICE", 80, ColMutedFg);
                MakeLabel(Root.transform, new Rect(lx, bodyH - rowH * 3, lw, rowH), "MQTT",        80, ColMutedFg);

                _game = MakeLabel(Root.transform, new Rect(vx, bodyH - rowH,     vw, rowH), "—", 95, ColValue, TextAnchor.MiddleRight);
                _pkg  = MakeLabel(Root.transform, new Rect(vx, bodyH - rowH * 2, vw, rowH), "—", 95, ColValue, TextAnchor.MiddleRight);
                _mqtt = MakeLabel(Root.transform, new Rect(vx, bodyH - rowH * 3, vw, rowH), "—", 80, ColGood,  TextAnchor.MiddleRight);
            }

            public override void Refresh(MonoBehaviour source)
            {
                if (source == null) { Root.SetActive(false); return; }
                Root.SetActive(true);

                string game = GetField<string>(source, "_gameVersion")    ?? Application.version;
                string pkg  = GetField<string>(source, "_packageVersion") ?? "—";

                // MQTT via réflexion statique sur MqttClient
                bool mqttOk = false;
                try
                {
                    var t = Type.GetType("uPLibrary.Networking.M2Mqtt.MqttClient, Assembly-CSharp") ??
                            Type.GetType("uPLibrary.Networking.M2Mqtt.MqttClient");
                    if (t != null)
                    {
                        var pi = t.GetProperty("IsConnected__",
                            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        if (pi != null) mqttOk = (bool)pi.GetValue(null);
                    }
                }
                catch { }

                _game.text  = game;
                _pkg.text   = pkg;
                _mqtt.text  = mqttOk ? "CONNECTED" : "OFFLINE";
                _mqtt.color = mqttOk ? ColGood : ColBad;
                AccentBar.color = mqttOk ? ColGood : ColBad;
            }
        }

        // ─── Panel Latency ────────────────────────────────────────────────────────

        private class PanelLatency : PanelBase
        {
            private Text _totalLat, _netLat, _encLat, _decLat, _clientFps, _serverFps;

            public PanelLatency(Transform p, Rect r, WorldSpaceDebugUI o) : base(p, r, o) { }

            protected override void BuildContent()
            {
                float titleH = 70f;
                float bodyH  = H - titleH;
                float rowH   = bodyH / 6f;
                float lx = 10f, lw = W * 0.6f;
                float vx = W * 0.6f, vw = W * 0.37f;

                MakeLabel(Root.transform, new Rect(lx, H - titleH, W - 20f, titleH), "LATENCY CHART", 60, ColMutedFg);

                string[] labels = { "TOTAL LAT", "NETWORK", "ENCODE", "DECODE", "CLIENT FPS", "SERVER FPS" };
                for (int i = 0; i < labels.Length; i++)
                    MakeLabel(Root.transform, new Rect(lx, bodyH - rowH * (i + 1), lw, rowH), labels[i], 75, ColMutedFg);

                _totalLat  = MakeLabel(Root.transform, new Rect(vx, bodyH - rowH,     vw, rowH), "—", 85, ColValue, TextAnchor.MiddleRight);
                _netLat    = MakeLabel(Root.transform, new Rect(vx, bodyH - rowH * 2, vw, rowH), "—", 85, ColValue, TextAnchor.MiddleRight);
                _encLat    = MakeLabel(Root.transform, new Rect(vx, bodyH - rowH * 3, vw, rowH), "—", 85, ColValue, TextAnchor.MiddleRight);
                _decLat    = MakeLabel(Root.transform, new Rect(vx, bodyH - rowH * 4, vw, rowH), "—", 85, ColValue, TextAnchor.MiddleRight);
                _clientFps = MakeLabel(Root.transform, new Rect(vx, bodyH - rowH * 5, vw, rowH), "—", 85, ColValue, TextAnchor.MiddleRight);
                _serverFps = MakeLabel(Root.transform, new Rect(vx, bodyH - rowH * 6, vw, rowH), "—", 85, ColValue, TextAnchor.MiddleRight);
            }

            public override void Refresh(MonoBehaviour source)
            {
                if (source == null) { Root.SetActive(false); return; }
                Root.SetActive(true);

                // Lit _live (StatisticsSummaryItem) via réflexion
                var live = GetField<object>(source, "_live");
                if (live == null)
                {
                    _totalLat.text = "NO DATA";
                    return;
                }

                double total  = GetProp<double>(live, "total_latency_ms");
                double net    = GetProp<double>(live, "network_latency_ms");
                double enc    = GetProp<double>(live, "encode_latency_ms");
                double dec    = GetProp<double>(live, "decode_latency_ms");
                double cfps   = GetProp<double>(live, "client_fps");
                double sfps   = GetProp<double>(live, "server_fps");

                _totalLat.text  = total.ToString("F1") + " ms";
                _netLat.text    = net.ToString("F1")   + " ms";
                _encLat.text    = enc.ToString("F1")   + " ms";
                _decLat.text    = dec.ToString("F1")   + " ms";
                _clientFps.text = cfps.ToString("F0")  + " fps";
                _serverFps.text = sfps.ToString("F0")  + " fps";

                Color latCol = total < 100 ? ColGood : total < 140 ? ColWarn : ColBad;
                _totalLat.color = latCol;
                AccentBar.color = latCol;
            }
        }

        // ─── Panel Boundary ───────────────────────────────────────────────────────

        private class PanelBoundary : PanelBase
        {
            private Text _inside, _dist, _prox;

            public PanelBoundary(Transform p, Rect r, WorldSpaceDebugUI o) : base(p, r, o) { }

            protected override void BuildContent()
            {
                float titleH = 70f;
                float bodyH  = H - titleH;
                float rowH   = bodyH / 3f;
                float lx = 10f, lw = W * 0.6f;
                float vx = W * 0.6f, vw = W * 0.37f;

                MakeLabel(Root.transform, new Rect(lx, H - titleH, W - 20f, titleH), "BOUNDARY DEBUG", 60, ColMutedFg);

                MakeLabel(Root.transform, new Rect(lx, bodyH - rowH,     lw, rowH), "STATUS",    80, ColMutedFg);
                MakeLabel(Root.transform, new Rect(lx, bodyH - rowH * 2, lw, rowH), "DIST WALL", 80, ColMutedFg);
                MakeLabel(Root.transform, new Rect(lx, bodyH - rowH * 3, lw, rowH), "PROXIMITY", 80, ColMutedFg);

                _inside = MakeLabel(Root.transform, new Rect(vx, bodyH - rowH,     vw, rowH), "—", 80, ColGood,  TextAnchor.MiddleRight);
                _dist   = MakeLabel(Root.transform, new Rect(vx, bodyH - rowH * 2, vw, rowH), "—", 85, ColValue, TextAnchor.MiddleRight);
                _prox   = MakeLabel(Root.transform, new Rect(vx, bodyH - rowH * 3, vw, rowH), "—", 85, ColValue, TextAnchor.MiddleRight);
            }

            public override void Refresh(MonoBehaviour source)
            {
                if (source == null) { Root.SetActive(false); return; }
                Root.SetActive(true);

                bool  inside = GetField<bool> (source, "IsInsideBoundary");
                float dist   = GetField<float>(source, "DistanceToWall");
                float prox   = GetField<float>(source, "ProximityFade");

                _inside.text  = inside ? "INSIDE" : "OUTSIDE";
                _inside.color = inside ? ColGood : ColBad;
                _dist.text    = dist.ToString("F2") + " m";
                _prox.text    = (prox * 100f).ToString("F0") + " %";

                AccentBar.color = inside ? ColGood : ColBad;
            }
        }
    }
}
