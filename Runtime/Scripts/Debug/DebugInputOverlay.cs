using UnityEngine;
using VBO_Ultimate.Runtime.Scripts.Input;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace VaroniaBackOffice
{
    /// <summary>
    /// Overlay 2D Screen Space affichant l'état des 4 boutons VaroniaInput (1-2-3-4)
    /// avec carré coloré, et la dernière fois qu'un input a été déclenché (en secondes).
    /// En debug mode (F10), affiche aussi l'état des boutons souris (clic gauche=1, droit=2, molette=3, scroll=4).
    /// Le coin d'affichage est configurable via l'Inspector.
    /// Doit être placé sur un GameObject possédant un composant VaroniaWeaponTracking.
    /// La marge verticale est automatiquement décalée selon le weaponIndex de l'arme.
    /// </summary>
    public class DebugInputOverlay : MonoBehaviour
    {
        // ─── Config ───────────────────────────────────────────────────────────────

        public enum DisplayCorner { TopLeft, TopRight, BottomLeft, BottomRight }

        [Header("Display")]
        [SerializeField] private DisplayCorner corner = DisplayCorner.TopRight;
        [SerializeField] private Vector2 margin = new Vector2(12f, 12f);
        [SerializeField] private Vector2 size   = new Vector2(160f, 160f);

        /// <summary>Facteur d'échelle manuel (1 = 1080p).</summary>
        [Header("UI Scale")]
        public float scaleFactor = 1f;

        // ─── Weapon ───────────────────────────────────────────────────────────────
        private int                    _weaponIndex;

        // ─── Colors (même palette que VaroniaFPSDisplay / WorldSpaceDebugUI) ─────

        static readonly Color ColBg       = new Color(0.11f, 0.11f, 0.14f, 0.92f);
        static readonly Color ColGood     = new Color(0.30f, 0.85f, 0.65f, 1f);
        static readonly Color ColBad      = new Color(1.00f, 0.40f, 0.40f, 1f);
        static readonly Color ColMuted    = new Color(0.55f, 0.55f, 0.62f, 1f);
        static readonly Color ColValue    = new Color(0.92f, 0.92f, 0.95f, 1f);
        static readonly Color ColBtnOff   = new Color(0.20f, 0.20f, 0.24f, 1f);
        static readonly Color ColAccent   = new Color(0.30f, 0.85f, 0.65f, 1f);

        // ─── State ────────────────────────────────────────────────────────────────

        private float _lastInputTime = -1f;

        // État souris courant (mis à jour dans Update)
        private bool  _mouseLeft;
        private bool  _mouseRight;
        private bool  _mouseMiddle;
        private bool  _mouseScroll;

        // ─── Styles ───────────────────────────────────────────────────────────────

        private bool      _stylesBuilt;
        private float     _lastScale = 1f;
        private GUIStyle  _titleStyle;
        private GUIStyle  _btnLabelStyle;
        private GUIStyle  _lastInputLabelStyle;
        private GUIStyle  _lastInputValueStyle;
        private Texture2D _bgTex;
        private Texture2D _accentTex;
        private Texture2D _btnOnTex;
        private Texture2D _btnOffTex;
        private Texture2D _btnFireTex;

        // ─────────────────────────────────────────────────────────────────────────

        VaroniaWeaponTracking  tracking;
        
        private void Awake()
        {
            // Récupère le weaponIndex depuis le VaroniaWeaponTracking parent
             tracking = GetComponentInParent<VaroniaWeaponTracking>();
            if (tracking != null)
            {
                _weaponIndex = tracking.weaponIndex;
                margin.y = margin.y + _weaponIndex * (size.y);
            }

            // S'abonne à l'event générique pour tracker le dernier input (toutes armes)
            VaroniaInput.OnButtonChanged += OnButtonChangedHandler;

            OnMovieChanged();

        }
        
        
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

        
        
        
        

        private void Update()
        {
            if (!DebugModeOverlay.IsSuperDebugMode) return;

#if ENABLE_INPUT_SYSTEM
            if (Mouse.current != null)
            {
                _mouseLeft   = Mouse.current.leftButton.isPressed;
                _mouseRight  = Mouse.current.rightButton.isPressed;
                _mouseMiddle = Mouse.current.middleButton.isPressed;
                _mouseScroll = Mathf.Abs(Mouse.current.scroll.ReadValue().y) > 0.01f;
            }
            else
            {
                _mouseLeft = _mouseRight = _mouseMiddle = _mouseScroll = false;
            }
#else
            _mouseLeft   = Input.GetMouseButton(0);
            _mouseRight  = Input.GetMouseButton(1);
            _mouseMiddle = Input.GetMouseButton(2);
            _mouseScroll = Mathf.Abs(Input.mouseScrollDelta.y) > 0.01f;
#endif

            // Propager l'état courant vers VaroniaInput chaque frame (pour cette arme uniquement)
            VaroniaInput.SetButton(_weaponIndex, VaroniaButton.Primary,    _mouseLeft);
            VaroniaInput.SetButton(_weaponIndex, VaroniaButton.Secondary,  _mouseRight);
            VaroniaInput.SetButton(_weaponIndex, VaroniaButton.Tertiary,   _mouseMiddle);
            VaroniaInput.SetButton(_weaponIndex, VaroniaButton.Quaternary, _mouseScroll);

        }

        private void OnDestroy()
        {
            VaroniaInput.OnButtonChanged -= OnButtonChangedHandler;

            if (_bgTex)     Destroy(_bgTex);
            if (_accentTex) Destroy(_accentTex);
            if (_btnOnTex)  Destroy(_btnOnTex);
            if (_btnOffTex) Destroy(_btnOffTex);
            if (_btnFireTex) Destroy(_btnFireTex);
        }

        private void OnButtonChangedHandler(int weaponIndex, VaroniaButton button, bool pressed)
        {
            if (weaponIndex == _weaponIndex && pressed)
                _lastInputTime = Time.unscaledTime;
        }


        private bool show;
        
        private void OnGUI()
        {
            
            if(!show) return;

            float scale = (Screen.height / 1080f) * scaleFactor;
            EnsureStyles(scale);

            Rect panel = GetPanelRect(scale);
            float W    = panel.width;
            float H    = panel.height;

            // ── Background ──
            GUI.DrawTexture(panel, _bgTex);

            // ── Left accent bar (3px) ──
            GUI.DrawTexture(new Rect(panel.x, panel.y, 3f * scale, H), _accentTex);

            float lx  = panel.x + 10f * scale;
            float pad = 6f * scale;

            // ── Titre ──
            float titleH = 18f * scale;
            string title = !string.IsNullOrEmpty(VaroniaInput.GetModel(_weaponIndex)) ? VaroniaInput.GetModel(_weaponIndex) : "INPUT VARONIA";
            GUI.Label(new Rect(lx, panel.y + pad, W - 14f * scale, titleH), title, _titleStyle);

            // ── 4 boutons ──
            float btnAreaY = panel.y + pad + titleH + 4f * scale;
            float btnSize  = 28f * scale;
            float btnSpacing = (W - 20f * scale - btnSize * 4f) / 3f;

            string[] labels = { "1", "2", "3", "4" };

            bool[] states = DebugModeOverlay.IsDebugMode
                ? new bool[] { _mouseLeft, _mouseRight, _mouseMiddle, _mouseScroll }
                : new bool[] {
                    VaroniaInput.GetButton(_weaponIndex, VaroniaButton.Primary),
                    VaroniaInput.GetButton(_weaponIndex, VaroniaButton.Secondary),
                    VaroniaInput.GetButton(_weaponIndex, VaroniaButton.Tertiary),
                    VaroniaInput.GetButton(_weaponIndex, VaroniaButton.Quaternary)
                };

            for (int i = 0; i < 4; i++)
            {
                bool isOn = states[i];
                float bx  = lx + i * (btnSize + btnSpacing);

                // Carré fond
                Texture2D activeTex = isOn ? (DebugModeOverlay.IsDebugMode ? _btnFireTex : _btnOnTex) : _btnOffTex;
                GUI.DrawTexture(new Rect(bx, btnAreaY, btnSize, btnSize), activeTex);

                // Chiffre centré
                GUI.Label(new Rect(bx, btnAreaY, btnSize, btnSize), labels[i], _btnLabelStyle);
            }

            // ── Last input ──
            float lastY = btnAreaY + btnSize + 6f * scale;
            GUI.Label(new Rect(lx, lastY, W - 14f * scale, 16f * scale), "last input :", _lastInputLabelStyle);

            string lastVal;
            if (_lastInputTime < 0f)
                lastVal = "—";
            else
            {
                float elapsed = Time.unscaledTime - _lastInputTime;
                lastVal = elapsed.ToString("F1") + " s";
            }

            GUI.Label(new Rect(lx, lastY + 16f * scale, W - 14f * scale, 16f * scale), lastVal, _lastInputValueStyle);

            // ── Telemetry ──
            float telY = lastY + 16f * scale + 18f * scale;

            // IsTracked — toujours affiché
            bool isTracked = tracking != null && tracking.trackerFollower != null && tracking.trackerFollower.isTracking;
            DrawTelemetryRow(lx, telY, W, "tracked :", isTracked ? "yes" : "no", isTracked ? ColGood : ColBad, scale);
            telY += 16f * scale;

            // IsConnected — toujours affiché
            string connLabel = VaroniaInput.GetIsConnected(_weaponIndex) ? "connected" : "disconnected";
            Color  connColor = VaroniaInput.GetIsConnected(_weaponIndex) ? ColGood : ColBad;
            DrawTelemetryRow(lx, telY, W, "connected :", connLabel, connColor, scale);
            telY += 16f * scale;

            // Battery — affiché uniquement si != 0
            if (VaroniaInput.GetBattery(_weaponIndex) != 0)
            {
                DrawTelemetryRow(lx, telY, W, "battery :", VaroniaInput.GetBattery(_weaponIndex) + " %", ColValue, scale);
                telY += 16f * scale;
            }

            // RSSI — affiché uniquement si != 0
            if (VaroniaInput.GetRSSI(_weaponIndex) != 0)
            {
                DrawTelemetryRow(lx, telY, W, "rssi :", VaroniaInput.GetRSSI(_weaponIndex).ToString("F2") + " dBm", ColValue, scale);
                telY += 16f * scale;
            }

            // BootTime — affiché uniquement si != 0
            if (VaroniaInput.GetBootTime(_weaponIndex) != 0)
            {
                DrawTelemetryRow(lx, telY, W, "boot :", VaroniaInput.GetBootTime(_weaponIndex) + " s", ColValue, scale);
                telY += 16f * scale;
            }
            
        }

        private void DrawTelemetryRow(float x, float y, float W, string label, string value, Color valueColor, float scale)
        {
            GUI.Label(new Rect(x, y, 60f * scale, 15f * scale), label, _lastInputLabelStyle);
            GUIStyle valStyle = new GUIStyle(_lastInputValueStyle) { normal = { textColor = valueColor } };
            GUI.Label(new Rect(x + 60f * scale, y, W - 74f * scale, 15f * scale), value, valStyle);
        }

        // ─── Helpers ──────────────────────────────────────────────────────────────

        private Rect GetPanelRect(float scale)
        {
            float w = size.x * scale, h = size.y * scale;
            // Décalage horizontal selon l'index de l'arme (chaque arme est décalée de size.x + margin.x)
           
            float x, y;
            float mx = margin.x * scale, my = margin.y * scale;
            switch (corner)
            {
                case DisplayCorner.TopLeft:
                    x = mx; y = my; break;
                case DisplayCorner.TopRight:
                    x = Screen.width - w - mx; y = my; break;
                case DisplayCorner.BottomLeft:
                    x = mx; y = Screen.height - h - my; break;
                default: // BottomRight
                    x = Screen.width - w - mx; y = Screen.height - h - my; break;
            }
            return new Rect(x, y, w, h);
        }

        private void EnsureStyles(float scale)
        {
            if (_stylesBuilt && Mathf.Approximately(scale, _lastScale)) return;
            _stylesBuilt = true;
            _lastScale = scale;

            if (_bgTex == null)
            {
                _bgTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                _bgTex.SetPixel(0, 0, ColBg);
                _bgTex.Apply();
                _bgTex.hideFlags = HideFlags.HideAndDontSave;
            }

            if (_accentTex == null)
            {
                _accentTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                _accentTex.SetPixel(0, 0, ColAccent);
                _accentTex.Apply();
                _accentTex.hideFlags = HideFlags.HideAndDontSave;
            }

            if (_btnOnTex == null)
            {
                _btnOnTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                _btnOnTex.SetPixel(0, 0, ColGood);
                _btnOnTex.Apply();
                _btnOnTex.hideFlags = HideFlags.HideAndDontSave;
            }

            if (_btnOffTex == null)
            {
                _btnOffTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                _btnOffTex.SetPixel(0, 0, ColBtnOff);
                _btnOffTex.Apply();
                _btnOffTex.hideFlags = HideFlags.HideAndDontSave;
            }

            if (_btnFireTex == null)
            {
                _btnFireTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                _btnFireTex.SetPixel(0, 0, ColBad);
                _btnFireTex.Apply();
                _btnFireTex.hideFlags = HideFlags.HideAndDontSave;
            }

            _titleStyle = new GUIStyle
            {
                fontSize  = Mathf.RoundToInt(9 * scale),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                normal    = { textColor = ColMuted },
            };

            _btnLabelStyle = new GUIStyle
            {
                fontSize  = Mathf.RoundToInt(13 * scale),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal    = { textColor = ColValue },
            };

            _lastInputLabelStyle = new GUIStyle
            {
                fontSize  = Mathf.RoundToInt(9 * scale),
                fontStyle = FontStyle.Normal,
                alignment = TextAnchor.MiddleLeft,
                normal    = { textColor = ColMuted },
            };

            _lastInputValueStyle = new GUIStyle
            {
                fontSize  = Mathf.RoundToInt(11 * scale),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                normal    = { textColor = ColValue },
            };
        }
    }
}
