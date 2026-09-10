// =============================================================================
//  GeckoBrowserUI.cs
//
//  Browser chrome for the VR plane: Back / Forward / Reload / Home, a URL bar,
//  and an on-screen keyboard for typing addresses and searches.
//
//  Put this on the SAME GameObject as GeckoVulkanRenderer (BrowserPlane).
//  Everything is built at runtime - no prefabs, no Canvas, no EventSystem.
//
//  Requires: Window > TextMeshPro > Import TMP Essential Resources
//  (one-time, per project). Without it TMP has no font asset and every label
//  renders blank; Awake() checks and tells you.
//
//  Geometry note
//  -------------
//  The chrome is positioned from the plane's own axes rather than world axes,
//  so it stays glued to the browser however you rotate or move it:
//
//      normal  n  = plane.transform.up      (Plane primitive's normal is +Y)
//      ui up      = world up projected onto the plane's surface
//      ui right   = cross(n, uiUp)
//
//  Quads and TextMeshPro (3D) are both readable from their -Z side, so both
//  get rotation LookRotation(-n, uiUp).
// =============================================================================

using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

[RequireComponent(typeof(GeckoVulkanRenderer))]
public class GeckoBrowserUI : MonoBehaviour
{
    [Header("Home")]
    public string homeUrl = "https://www.google.com";

    [Tooltip("Typed text with no dot and no scheme is sent here as a search.")]
    public string searchUrlFormat = "https://www.google.com/search?q={0}";

    [Header("Layout (fractions of plane width)")]
    [Range(0.02f, 0.20f)] public float toolbarHeight = 0.06f;
    [Range(0.00f, 0.05f)] public float gap = 0.01f;

    [Header("Keyboard")]
    public bool showKeyboardOnUrlTap = true;
    [Range(0.10f, 0.60f)] public float keyboardHeight = 0.34f;

    [Header("Debug")]
    public bool verboseLogging = true;

    private const string Tag = "[GeckoVulkanBridge] ";

    private GeckoVulkanRenderer _browser;

    private Transform _root;
    private Transform _keyboardRoot;

    private GeckoUIButton _backBtn, _forwardBtn;
    private TextMeshPro _urlLabel;

    private bool _editing;
    private string _editBuffer = "";
    private string _displayedUrl = "";

    // Plane basis, computed once in Build().
    private Vector3 _n, _uiUp, _uiRight, _planeCentre;
    private float _planeW, _planeH;
    private Quaternion _facing;

    private readonly List<GeckoUIButton> _allButtons = new List<GeckoUIButton>();

    private void Awake()
    {
        _browser = GetComponent<GeckoVulkanRenderer>();

        if (!TmpIsUsable())
        {
            Debug.LogError(Tag + "TextMeshPro has no default font asset. " +
                           "Run Window > TextMeshPro > Import TMP Essential Resources, " +
                           "then rebuild. Browser chrome disabled.");
            enabled = false;
            return;
        }

        Build();
        StartCoroutine(PollNavigationState());
    }

    private static bool TmpIsUsable()
    {
        // TMP_Settings.instance is null until the Essentials are imported, and
        // touching defaultFontAsset then throws rather than returning null.
        try { return TMP_Settings.defaultFontAsset != null; }
        catch { return false; }
    }

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------
    private void Build()
    {
        // Unity's Plane mesh spans 10x10 units, so world size is 10 * localScale.
        _planeW = 2f * transform.lossyScale.x;
        _planeH = 2f * transform.lossyScale.z;
        _planeCentre = transform.position;

        _n = transform.up.normalized;                                   // faces the user
        _uiUp = Vector3.ProjectOnPlane(Vector3.up, _n).normalized;
        if (_uiUp.sqrMagnitude < 0.001f) _uiUp = transform.forward;     // plane is horizontal
        _uiRight = Vector3.Cross(_n, _uiUp).normalized;
        _facing = Quaternion.LookRotation(-_n, _uiUp);

        var rootGo = new GameObject("GeckoBrowserUI");
        rootGo.transform.SetParent(transform.parent, false);
        _root = rootGo.transform;

        float barH = _planeW * toolbarHeight;
        float gapW = _planeW * gap;

        // Toolbar sits just below the bottom edge of the page.
        Vector3 barCentre = _planeCentre - _uiUp * (_planeH * 0.5f + gapW + barH * 0.5f);

       // BuildToolbar(barCentre, barH, gapW);

        if (showKeyboardOnUrlTap)
        {
            float kbH = _planeW * keyboardHeight;
            Vector3 kbCentre = barCentre - _uiUp * (barH * 0.5f + gapW + kbH * 0.5f);
            BuildKeyboard(kbCentre, kbH, gapW);
            _keyboardRoot.gameObject.SetActive(false);
        }

        Log($"chrome built: planeW={_planeW:F2} planeH={_planeH:F2} barH={barH:F2}");
    }

    private void BuildToolbar(Vector3 centre, float barH, float gapW)
    {
        float btnW = barH;                       // square nav buttons
        float urlW = _planeW - (btnW * 2f + gapW * 3f);

        float x = -_planeW * 0.5f + gapW + btnW * 0.5f;

        _backBtn = MakeButton("Back", "◀", centre + _uiRight * x, btnW, barH,
                              () => _browser.GoBack());
        x += btnW + gapW;

        _forwardBtn = MakeButton("Forward", "▶", centre + _uiRight * x, btnW, barH,
                                 () => _browser.GoForward());
        x += btnW + gapW;

        MakeButton("Reload", "↻", centre + _uiRight * x, btnW, barH,
                   () => _browser.Reload());
        x += btnW + gapW;

        MakeButton("Home", "⌂", centre + _uiRight * x, btnW, barH,
                   () => Navigate(homeUrl));
        x += btnW + gapW;

        // URL bar - a wide button that opens the keyboard.
        var urlBtn = MakeButton("UrlBar", "", centre + _uiRight * (x + urlW * 0.2f - btnW * 0.2f),
                                urlW, barH, BeginEditing);
        urlBtn.SetColors(new Color(0.09f, 0.10f, 0.12f, 1f),
                         new Color(0.14f, 0.16f, 0.19f, 1f),
                         new Color(0.10f, 0.30f, 0.50f, 1f));

        _urlLabel = urlBtn.transform.GetComponentInChildren<TextMeshPro>();
        _urlLabel.alignment = TextAlignmentOptions.Left;
        _urlLabel.margin = new Vector4(barH * 0.3f, 0f, barH * 0.3f, 0f);
        _urlLabel.fontSize = barH * 2f;
        _urlLabel.text = homeUrl;
    }

    // Compact layout: enough for URLs and searches without becoming a full IME.
    private static readonly string[] kRows =
    {
        "1234567890",
        "qwertyuiop",
        "asdfghjkl:",
        "zxcvbnm.-/"
    };

    private void BuildKeyboard(Vector3 centre, float kbH, float gapW)
    {
        var kbGo = new GameObject("Keyboard");
        kbGo.transform.SetParent(_root, false);
        _keyboardRoot = kbGo.transform;

        int rows = kRows.Length + 1;                       // + the action row
        float keyH = (kbH - gapW * (rows + 1)) / rows;
        float keyW = (_planeW - gapW * 11f) / 10f;

        for (int r = 0; r < kRows.Length; r++)
        {
            string row = kRows[r];
            float rowW = row.Length * keyW + (row.Length - 1) * gapW;
            float startX = -rowW * 0.5f + keyW * 0.5f;
            float y = kbH * 0.5f - gapW - keyH * 0.5f - r * (keyH + gapW);

            for (int c = 0; c < row.Length; c++)
            {
                string ch = row[c].ToString();
                Vector3 p = centre + _uiRight * (startX + c * (keyW + gapW)) + _uiUp * y;
                MakeButton("Key_" + ch, ch, p, keyW, keyH, () => TypeChar(ch), _keyboardRoot);
            }
        }

        // Action row: space is wide, the rest share the remainder.
        float ay = kbH * 0.5f - gapW - keyH * 0.5f - kRows.Length * (keyH + gapW);
        float wideW = keyW * 4f + gapW * 3f;
        float actW  = keyW * 2f + gapW;

        float ax = -(wideW + actW * 3f + gapW * 3f) * 0.5f + wideW * 0.5f;
        MakeButton("Space", "space", centre + _uiRight * ax + _uiUp * ay, wideW, keyH,
                   () => TypeChar(" "), _keyboardRoot);

        ax += wideW * 0.5f + gapW + actW * 0.5f;
        MakeButton("Back", "⌫", centre + _uiRight * ax + _uiUp * ay, actW, keyH,
                   Backspace, _keyboardRoot);

        ax += actW + gapW;
        var go = MakeButton("Go", "Go", centre + _uiRight * ax + _uiUp * ay, actW, keyH,
                            CommitEditing, _keyboardRoot);
        go.SetColors(new Color(0.10f, 0.45f, 0.25f, 1f),
                     new Color(0.14f, 0.60f, 0.33f, 1f),
                     new Color(0.10f, 0.70f, 0.40f, 1f));

        ax += actW + gapW;
        MakeButton("Cancel", "✕", centre + _uiRight * ax + _uiUp * ay, actW, keyH,
                   CancelEditing, _keyboardRoot);
    }

    private GeckoUIButton MakeButton(string name, string label, Vector3 worldPos,
                                     float w, float h, System.Action onClick,
                                     Transform parent = null)
    {
        var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.name = name;
        Destroy(quad.GetComponent<MeshCollider>());       // swap for a Box: cheaper, and
        var box = quad.AddComponent<BoxCollider>();       // we don't need textureCoord here
        box.size = new Vector3(1f, 1f, 0.05f);

        quad.transform.SetParent(parent != null ? parent : _root, false);
        quad.transform.position = worldPos;
        quad.transform.rotation = _facing;
        quad.transform.localScale = new Vector3(w, h, 1f);

        // Reuse the pipeline-correct unlit shader the browser plane already proves
        // is present in the build.
        var mat = new Material(_browser.GetComponent<Renderer>().sharedMaterial.shader);
        mat.mainTexture = null;
        quad.GetComponent<Renderer>().sharedMaterial = mat;

        var btn = quad.AddComponent<GeckoUIButton>();
        btn.onClick = onClick;
        btn.payload = label;
        _allButtons.Add(btn);

        if (!string.IsNullOrEmpty(label))
        {
            var textGo = new GameObject("Label");
            textGo.transform.SetParent(quad.transform, false);
            // Undo the parent's non-uniform scale so glyphs aren't stretched, and
            // float slightly in front so it doesn't z-fight with the quad.
            textGo.transform.localScale = new Vector3(1f / w, 1f / h, 1f);
            textGo.transform.localPosition = new Vector3(0f, 0f, -0.01f);
            textGo.transform.localRotation = Quaternion.identity;

            var tmp = textGo.AddComponent<TextMeshPro>();
            tmp.text = label;
            tmp.fontSize = h * 26f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            tmp.enableWordWrapping = false;
            tmp.rectTransform.sizeDelta = new Vector2(w, h);
        }

        return btn;
    }

    // -------------------------------------------------------------------------
    // Behaviour
    // -------------------------------------------------------------------------
    private void BeginEditing()
    {
        _editing = true;
        _editBuffer = _displayedUrl;
        if (_keyboardRoot != null) _keyboardRoot.gameObject.SetActive(true);
        RefreshUrlLabel();
        Log("editing started");
    }

    private void CancelEditing()
    {
        _editing = false;
        if (_keyboardRoot != null) _keyboardRoot.gameObject.SetActive(false);
        RefreshUrlLabel();
    }

    private void CommitEditing()
    {
        string typed = _editBuffer.Trim();
        _editing = false;
        if (_keyboardRoot != null) _keyboardRoot.gameObject.SetActive(false);

        if (string.IsNullOrEmpty(typed)) { RefreshUrlLabel(); return; }
        Navigate(typed);
    }

    // -------------------------------------------------------------------------
    // Control surface for GeckoControllerInput - the on-screen toolbar buttons and
    // the physical controller buttons drive the same code paths.
    // -------------------------------------------------------------------------

    /// <summary>True while the URL bar is being edited and the keyboard is up.</summary>
    public bool IsKeyboardOpen => _editing;

    /// <summary>Opens the keyboard if closed, cancels editing if already open.</summary>
    public void ToggleKeyboard()
    {
        if (_editing) CancelEditing();
        else BeginEditing();
    }

    /// <summary>Navigates to <see cref="homeUrl"/>.</summary>
    public void GoHome() => Navigate(homeUrl);

    private void Navigate(string typed)
    {
        string url;
        if (typed.StartsWith("http://") || typed.StartsWith("https://"))
            url = typed;
        else if (typed.Contains(".") && !typed.Contains(" "))
            url = "https://" + typed;
        else
            url = string.Format(searchUrlFormat, UnityEngine.Networking.UnityWebRequest.EscapeURL(typed));

        Log($"navigate -> {url}");
        _browser.LoadUrl(url);
        _displayedUrl = url;
        RefreshUrlLabel();
    }

    private void TypeChar(string ch)
    {
        if (!_editing) BeginEditing();
        _editBuffer += ch;
        RefreshUrlLabel();
    }

    private void Backspace()
    {
        if (!_editing) return;
        if (_editBuffer.Length > 0) _editBuffer = _editBuffer.Substring(0, _editBuffer.Length - 1);
        RefreshUrlLabel();
    }

    private void RefreshUrlLabel()
    {
        if (_urlLabel == null) return;
        _urlLabel.text = _editing ? _editBuffer + "|" : _displayedUrl;
    }

    /// <summary>
    /// Mirrors Gecko's real navigation state into the toolbar. Polled rather than
    /// pushed because the delegate fires on the Android main thread and Unity
    /// objects may only be touched from the Unity thread.
    /// </summary>
    private IEnumerator PollNavigationState()
    {
        var wait = new WaitForSeconds(0.5f);
        while (true)
        {
            yield return wait;
            if (!_browser.IsInteractive) continue;

            if (!_editing)
            {
                string u = _browser.GetCurrentUrl();
                if (!string.IsNullOrEmpty(u) && u != _displayedUrl)
                {
                    _displayedUrl = u;
                    RefreshUrlLabel();
                }
            }

            if (_backBtn != null)    _backBtn.Interactable    = _browser.CanGoBack();
            if (_forwardBtn != null) _forwardBtn.Interactable = _browser.CanGoForward();
        }
    }

    private void Log(string m) { if (verboseLogging) Debug.Log(Tag + "UI: " + m); }
}
