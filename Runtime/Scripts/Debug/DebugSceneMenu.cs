using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using VaroniaBackOffice;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Debug Menu: Scene selection (Numeric keys) + Shortcuts cheatsheet.
/// F1: Toggle this menu.
/// </summary>
public class DebugSceneMenu : MonoBehaviour
{
    [Header("Display")]
    [SerializeField] private float width = 460f;
    [SerializeField] private int scenesPerPage = 10;

    /// <summary>Facteur d'échelle manuel (1 = 1080p).</summary>
    [Header("UI Scale")]
    public float scaleFactor = 1f;

    // ─── Colors ─────────────────────────────────────────────────────────────

    static readonly Color ColBg     = new Color(0.11f, 0.11f, 0.14f, 0.98f);
    static readonly Color ColAccent = new Color(1.00f, 0.75f, 0.00f, 1f);
    static readonly Color ColGood   = new Color(0.30f, 0.85f, 0.65f, 1f);
    static readonly Color ColBad    = new Color(1.00f, 0.40f, 0.40f, 1f);
    static readonly Color ColMuted  = new Color(0.55f, 0.55f, 0.62f, 1f);
    static readonly Color ColValue  = new Color(0.92f, 0.92f, 0.95f, 1f);
    static readonly Color ColDiv    = new Color(1f,    1f,    1f,    0.06f);

    // ─── Cheatsheet ───────────────────────────────────────────────────────────

    struct ShortcutEntry
    {
        public string key;
        public string desc;
        public Color  keyColor;
        public ShortcutEntry(string k, string d, Color c) { key = k; desc = d; keyColor = c; }
    }

    struct Section
    {
        public string          title;
        public Color           titleColor;
        public ShortcutEntry[] entries;
        public Section(string t, Color c, ShortcutEntry[] e) { title = t; titleColor = c; entries = e; }
    }

    static readonly Section[] Sections = new Section[]
    {
        new Section("SYSTEM", new Color(1f, 0.75f, 0f, 1f), new ShortcutEntry[]
        {
            new ShortcutEntry("F8",  "Cycle Overlays (0→1→2→0)",   new Color(0.92f, 0.92f, 0.95f, 1f)),
            new ShortcutEntry("F9",  "Toggle VR Overlay",          new Color(0.30f, 0.85f, 0.65f, 1f)),
            new ShortcutEntry("F10", "Cycle Debug: OFF/DEBUG/SUPER", new Color(0.30f, 0.85f, 0.65f, 1f)),
        })
    };

    // ─── State ────────────────────────────────────────────────────────────────

    private bool         _menuVisible = false;
    private List<string> _sceneNames  = new List<string>();
    private int          _currentPage = 0;

    // ─── Styles ───────────────────────────────────────────────────────────────

    private bool      _stylesBuilt;
    private float     _lastScale = 1f;
    private GUIStyle  _titleStyle;
    private GUIStyle  _sectionStyle;
    private GUIStyle  _labelStyle;
    private GUIStyle  _pillStyle;
    private GUIStyle  _keyStyle;
    private GUIStyle  _descStyle;
    private GUIStyle  _footerStyle;
    private GUIStyle  _f2Style;
    private GUIStyle  _f3Style;
    private Texture2D _bgTex;
    private Texture2D _accentTex;
    private Texture2D _activePillTex;
    private Texture2D _divTex;

    // ─────────────────────────────────────────────────────────────────────────

    private void Awake()
    {
        int count = SceneManager.sceneCountInBuildSettings;
        for (int i = 0; i < count; i++)
        {
            string path = SceneUtility.GetScenePathByBuildIndex(i);
            _sceneNames.Add(System.IO.Path.GetFileNameWithoutExtension(path));
        }
    }

    private void OnDestroy() { CleanTextures(); }

    private void Update()
    {
#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current != null && Keyboard.current.f1Key.wasPressedThisFrame)
            _menuVisible = !_menuVisible;
#else
        if (Input.GetKeyDown(KeyCode.F1)) _menuVisible = !_menuVisible;
#endif
        if (!_menuVisible) return;

        HandlePagination();
        HandleSelection();
        HandleSpecialTriggers();
    }

    private void HandleSpecialTriggers()
    {
#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current == null) return;
        // The logic for F2/F3 is kept here as requested previously
        if (Keyboard.current.f2Key.wasPressedThisFrame) BackOfficeVaronia.Instance.TriggerStartGame(false);
        if (Keyboard.current.f3Key.wasPressedThisFrame) BackOfficeVaronia.Instance.TriggerStartGame(true);
#else
        if (Input.GetKeyDown(KeyCode.F2)) BackOfficeVaronia.Instance.TriggerStartGame(false);
        if (Input.GetKeyDown(KeyCode.F3)) BackOfficeVaronia.Instance.TriggerStartGame(true);
#endif
    }

    private void HandlePagination()
    {
        int maxPages = Mathf.Max(1, Mathf.CeilToInt((float)_sceneNames.Count / scenesPerPage));
#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current == null) return;
        if (Keyboard.current.pageUpKey.wasPressedThisFrame)   _currentPage = (_currentPage - 1 + maxPages) % maxPages;
        if (Keyboard.current.pageDownKey.wasPressedThisFrame) _currentPage = (_currentPage + 1) % maxPages;
#else
        if (Input.GetKeyDown(KeyCode.PageUp))   _currentPage = (_currentPage - 1 + maxPages) % maxPages;
        if (Input.GetKeyDown(KeyCode.PageDown)) _currentPage = (_currentPage + 1) % maxPages;
#endif
    }

    private void HandleSelection()
    {
        int startIdx = _currentPage * scenesPerPage;
        for (int i = 0; i < scenesPerPage; i++)
        {
            int sceneIdx = startIdx + i;
            if (sceneIdx >= _sceneNames.Count) break;
            if (IsNumericDown((i + 1) % 10)) ExecuteAction(sceneIdx);
        }
    }

    private void ExecuteAction(int buildIndex)
    {
        var settings = VaroniaRuntimeSettings.Load();
        string targetObjectName = settings != null ? settings.debugMenuTargetObjectName : null;
        string targetMethodName = settings != null ? settings.debugMenuTargetMethodName : null;

        if (!string.IsNullOrEmpty(targetObjectName) && !string.IsNullOrEmpty(targetMethodName))
        {
            GameObject target = GameObject.Find(targetObjectName);
            if (target != null) target.SendMessage(targetMethodName, _sceneNames[buildIndex], SendMessageOptions.DontRequireReceiver);
            _menuVisible = false;
        }
        else
        {
            _menuVisible = false;
            SceneManager.LoadScene(buildIndex);
        }
    }

    // ─── GUI ──────────────────────────────────────────────────────────────────

    private void OnGUI()
    {
        if (!_menuVisible) return;
        
        float scale = (Screen.height / 1080f) * scaleFactor;
        EnsureStyles(scale);

        int   startIdx        = _currentPage * scenesPerPage;
        int   scenesInThisPage = Mathf.Min(scenesPerPage, _sceneNames.Count - startIdx);
        int   totalPages      = Mathf.Max(1, Mathf.CeilToInt((float)_sceneNames.Count / scenesPerPage));

        // ── Dimensions ──
        float rowH    = 22f * scale;
        float headerH = 40f * scale;
        float divH    = 1f * scale;
        float secH    = 20f * scale;
        float padBot  = 14f * scale;
        float padTop  = 12f * scale;

        float scenesBlockH = headerH + (scenesInThisPage * rowH) + 25f * scale; 
        float triggersBlockH = divH + 30f * scale + 25f * scale; 
        float shortcutsH = 0f;
        foreach (var sec in Sections) shortcutsH += divH + 6f * scale + secH + (sec.entries.Length * rowH);

        float totalH = padTop + scenesBlockH + triggersBlockH + shortcutsH + padBot;
        float sWidth = width * scale;

        Rect panel = new Rect((Screen.width - sWidth) * 0.5f, (Screen.height - totalH) * 0.5f, sWidth, totalH);

        GUI.DrawTexture(panel, _bgTex);
        GUI.DrawTexture(new Rect(panel.x, panel.y, 3f * scale, panel.height), _accentTex);

        float lx = panel.x + 15f * scale;
        float lw = sWidth - 30f * scale;
        float y  = panel.y + padTop;

        // ── 1. SCENE SELECTION ──
        GUI.Label(new Rect(lx, y, lw, 20f * scale), "SCENE SELECTION", _titleStyle);
        y += 25f * scale;

        for (int i = 0; i < scenesInThisPage; i++)
        {
            int  sceneIdx = startIdx + i;
            bool isCurrent = (sceneIdx == SceneManager.GetActiveScene().buildIndex);
            GUI.Label(new Rect(lx, y, lw, rowH), _sceneNames[sceneIdx].ToUpper(), _labelStyle);

            Rect badgeRect = new Rect(panel.x + sWidth - 65f * scale, y + 2f * scale, 50f * scale, rowH - 6f * scale);
            if (isCurrent)
            {
                _pillStyle.normal.background = _activePillTex;
                _pillStyle.normal.textColor  = ColGood;
                GUI.Label(badgeRect, "ACTIVE", _pillStyle);
            }
            else
            {
                _pillStyle.normal.background = null;
                _pillStyle.normal.textColor  = ColMuted;
                GUI.Label(badgeRect, $"[{(i + 1) % 10}]", _pillStyle);
            }
            y += rowH;
        }

        y += 4f * scale;
        GUI.Label(new Rect(lx, y, lw, 15f * scale), $"PAGE {_currentPage + 1}/{totalPages}  •  PGUP / PGDN", _footerStyle);
        y += 20f * scale;

        // ── 2. GAME TRIGGERS (TUTORIAL) ──
        GUI.DrawTexture(new Rect(panel.x + 8f * scale, y, sWidth - 16f * scale, divH), _divTex);
        y += 8f * scale;
        GUI.Label(new Rect(lx, y, lw, 20f * scale), "GAME TRIGGERS", _sectionStyle);
        y += 20f * scale;

        float halfW = lw / 2f;
        GUI.Label(new Rect(lx,         y, halfW, 22f * scale), "F2: START (NO TUTO)", _f2Style);
        GUI.Label(new Rect(lx + halfW, y, halfW, 22f * scale), "F3: START (W/ TUTO)",  _f3Style);
        y += 30f * scale;

        // ── 3. SYSTEM ──
        float keyW  = 90f * scale;
        float descX = lx + keyW + 6f * scale;
        float descW = lw - keyW - 6f * scale;

        foreach (var sec in Sections)
        {
            GUI.DrawTexture(new Rect(panel.x + 8f * scale, y, sWidth - 16f * scale, divH), _divTex);
            y += divH + 6f * scale;

            _sectionStyle.normal.textColor = sec.titleColor;
            GUI.Label(new Rect(lx, y, lw, secH), sec.title, _sectionStyle);
            y += secH;

            foreach (var e in sec.entries)
            {
                _keyStyle.normal.textColor = e.keyColor;
                GUI.Label(new Rect(lx,    y, keyW,  rowH), e.key,  _keyStyle);
                GUI.Label(new Rect(descX, y, descW, rowH), e.desc, _descStyle);
                y += rowH;
            }
        }
    }

    private bool IsNumericDown(int digit)
    {
#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current == null) return false;
        return digit switch {
            0 => Keyboard.current.digit0Key.wasPressedThisFrame || Keyboard.current.numpad0Key.wasPressedThisFrame,
            1 => Keyboard.current.digit1Key.wasPressedThisFrame || Keyboard.current.numpad1Key.wasPressedThisFrame,
            2 => Keyboard.current.digit2Key.wasPressedThisFrame || Keyboard.current.numpad2Key.wasPressedThisFrame,
            3 => Keyboard.current.digit3Key.wasPressedThisFrame || Keyboard.current.numpad3Key.wasPressedThisFrame,
            4 => Keyboard.current.digit4Key.wasPressedThisFrame || Keyboard.current.numpad4Key.wasPressedThisFrame,
            5 => Keyboard.current.digit5Key.wasPressedThisFrame || Keyboard.current.numpad5Key.wasPressedThisFrame,
            6 => Keyboard.current.digit6Key.wasPressedThisFrame || Keyboard.current.numpad6Key.wasPressedThisFrame,
            7 => Keyboard.current.digit7Key.wasPressedThisFrame || Keyboard.current.numpad7Key.wasPressedThisFrame,
            8 => Keyboard.current.digit8Key.wasPressedThisFrame || Keyboard.current.numpad8Key.wasPressedThisFrame,
            9 => Keyboard.current.digit9Key.wasPressedThisFrame || Keyboard.current.numpad9Key.wasPressedThisFrame,
            _ => false
        };
#else
        return Input.GetKeyDown(KeyCode.Alpha0 + digit) || Input.GetKeyDown(KeyCode.Keypad0 + digit);
#endif
    }

    private void EnsureStyles(float scale)
    {
        if (_stylesBuilt && Mathf.Approximately(scale, _lastScale)) return;
        _stylesBuilt = true;
        _lastScale   = scale;

        if (_bgTex == null)         _bgTex         = MakeTex(ColBg);
        if (_accentTex == null)     _accentTex     = MakeTex(ColAccent);
        if (_activePillTex == null) _activePillTex = MakeTex(new Color(ColGood.r, ColGood.g, ColGood.b, 0.15f));
        if (_divTex == null)        _divTex        = MakeTex(ColDiv);

        _titleStyle   = new GUIStyle { fontSize = Mathf.RoundToInt(11 * scale), fontStyle = FontStyle.Bold, normal = { textColor = ColAccent } };
        _sectionStyle = new GUIStyle { fontSize = Mathf.RoundToInt(9 * scale),  fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, normal = { textColor = ColAccent } };
        _labelStyle   = new GUIStyle { fontSize = Mathf.RoundToInt(10 * scale), fontStyle = FontStyle.Bold, normal = { textColor = ColValue } };
        _pillStyle    = new GUIStyle { fontSize = Mathf.RoundToInt(8 * scale),  fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        _keyStyle     = new GUIStyle { fontSize = Mathf.RoundToInt(9 * scale),  fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, normal = { textColor = ColValue } };
        _descStyle    = new GUIStyle { fontSize = Mathf.RoundToInt(9 * scale),  fontStyle = FontStyle.Normal, alignment = TextAnchor.MiddleLeft, normal = { textColor = ColMuted } };
        _footerStyle  = new GUIStyle(_labelStyle) { fontSize = Mathf.RoundToInt(9 * scale), alignment = TextAnchor.MiddleCenter, normal = { textColor = ColMuted } };
        _f2Style      = new GUIStyle(_footerStyle) { fontSize = Mathf.RoundToInt(11 * scale), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft,  normal = { textColor = ColAccent } };
        _f3Style      = new GUIStyle(_footerStyle) { fontSize = Mathf.RoundToInt(11 * scale), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight, normal = { textColor = ColBad } };
    }

    private static Texture2D MakeTex(Color col)
    {
        var t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        t.SetPixel(0, 0, col); t.Apply();
        t.hideFlags = HideFlags.HideAndDontSave;
        return t;
    }

    private void CleanTextures()
    {
        if (_bgTex)         Destroy(_bgTex);
        if (_accentTex)     Destroy(_accentTex);
        if (_activePillTex) Destroy(_activePillTex);
        if (_divTex)        Destroy(_divTex);
    }
}