// =============================================================================
//  GeckoPageKeyboard.cs
//
//  A small floating on-screen keyboard that types into the WEB PAGE - form
//  fields, search boxes, anything the page has focused - rather than into a
//  URL bar.
//
//  Put this on the SAME GameObject as GeckoVulkanRenderer (BrowserPlane).
//  Everything is built at runtime: no prefabs, no Canvas, no EventSystem.
//
//  It renders as a small dialog box floating a short distance in front of the
//  page, sized in absolute metres rather than as a fraction of the plane -
//  so a 3m cinema screen and a 0.3m phone-sized panel both get the same
//  hand-sized keyboard instead of one scaled to the screen.
//
//  How a keypress reaches the page
//  -------------------------------
//   1. You point at a field on the plane and pull the trigger. GeckoPointerInput
//      turns that into injectTouch, and the page focuses the field as it would
//      for any tap.
//   2. You point at a key here. The key is a GeckoUIButton on a BoxCollider,
//      which GeckoPointerInput already hit-tests ahead of the plane, so no
//      pointer changes were needed.
//   3. The key calls GeckoVulkanRenderer.SendText / SendBackspace / SendEnter,
//      which edit document.activeElement through an injected script.
//
//  Tapping a key does NOT move focus inside the page - these quads live in
//  Unity, not in the DOM - so the field you picked in step 1 stays focused for
//  as long as you keep typing.
//
//  Requires: Window > TextMeshPro > Import TMP Essential Resources
//  (one-time, per project), same as GeckoBrowserUI.
//
//  Where it sits
//  -------------
//  By default the dialog follows the VIEWER, not the page. The page-relative
//  placement below is still available (followViewer = false) and is right for a
//  small panel you stand in front of, but it scales with the page: on a 15m
//  cinema screen 10m away it puts 3.5cm keys 10m from your hands. Following the
//  head keeps the keyboard at arm's length whatever the page is doing.
//
//  It does not track your head rigidly - a panel welded to your face cannot be
//  pointed at. It holds still in world space while you look at it, and drifts
//  back in front of you only once you have turned or walked away.
//
//  Page-relative geometry (followViewer = false):
//
//      normal  n  = plane.transform.up      (Plane primitive's normal is +Y,
//                                             and faces the viewer)
//      ui up      = world up projected onto the plane's surface
//      ui right   = cross(n, uiUp)
//
//  The dialog floats forward of the page along +n (toward the viewer) and
//  down along -uiUp from the plane's centre, rather than sitting flush against
//  the bottom edge - that's what reads as a floating window instead of a
//  toolbar welded to the screen.
//
//  Drag and resize
//  ----------------
//  Build() adds two plain physics handles above the tab: a wide "DragHandle"
//  bar and a small "ResizeHandle" square, each just a BoxCollider plus an
//  XRSimpleInteractable - no GeckoUIButton, so they never touch the
//  GeckoXRButton/GeckoUIButton click path that runs the keys off the trigger.
//  They select on XRI's default Select action, which is grip - the button
//  GeckoXRInteraction's own header comment documents as deliberately left
//  free for exactly this kind of grab. Holding DragHandle moves _root 1:1
//  with the hand; holding ResizeHandle scales the whole cluster by the ratio
//  of the hand's current distance from _root to its distance at grab time.
//  Both are position-only - _root's rotation stays under LateUpdate/
//  SnapToViewer's control, never the hand's, and scale is a single uniform
//  factor (_scaleFactor) applied to _root.localScale, so every key, label
//  and collider grows or shrinks together.
//
//  A drag or resize sets _userPositioned, which suspends followViewer's
//  auto-recenter until the next Show()/SnapToViewer() - otherwise a keyboard
//  dragged out of the way would drift straight back the moment you turned
//  your head far enough to trip the recenter angle.
// =============================================================================

using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

[RequireComponent(typeof(GeckoVulkanRenderer))]
public class GeckoPageKeyboard : MonoBehaviour
{
    [Header("Visibility")]
    [Tooltip("Show the keyboard as soon as the scene starts, instead of waiting " +
             "for the tab or a call to Toggle().")]
    public bool startVisible = false;

    [Tooltip("Draw the small keyboard tab under the page. Turn this off if you " +
             "drive Toggle() from a controller button instead.")]
    public bool showToggleTab = true;

    [Header("Dialog size (metres, NOT relative to the plane)")]
    [Tooltip("Width and height of one key, in metres. 0.035 gives roughly a " +
             "40cm x 20cm dialog - big enough to read and point at " +
             "comfortably at arm's length, still small next to the page.")]
    [Range(0.015f, 0.08f)] public float keySize = 0.035f;

    [Range(0.001f, 0.02f)] public float gap = 0.006f;

    [Tooltip("Empty margin between the keys and the dialog's border.")]
    [Range(0.0f, 0.05f)] public float padding = 0.016f;

    [Header("Placement")]
    [Tooltip("Keep the keyboard in front of the viewer instead of welding it " +
             "under the page. Leave this on for anything bigger than a desk " +
             "panel - page-relative placement scales with the page, so a cinema " +
             "screen puts the keys metres away and centimetres tall.")]
    public bool followViewer = true;

    [Tooltip("Head transform. Camera.main is used when this is empty.")]
    public Transform viewer;

    [Tooltip("How far in front of the eyes the dialog floats, in metres.")]
    [Range(0.3f, 1.5f)] public float viewerDistance = 0.6f;

    [Tooltip("Height of the cluster relative to eye level, in metres. Negative " +
             "puts it below your eyeline, where a keyboard belongs.")]
    [Range(-1f, 0.5f)] public float viewerHeightOffset = -0.15f;

    [Tooltip("Turn your head further than this off the keyboard and it drifts " +
             "back in front of you. Too small and it sticks to your face, which " +
             "is both queasy and impossible to aim at.")]
    [Range(0f, 90f)] public float recenterAngle = 35f;

    [Tooltip("Walk further than this from the keyboard and it comes with you.")]
    [Range(0.3f, 5f)] public float recenterDistance = 1.2f;

    [Tooltip("How briskly it catches up once it has decided to move.")]
    [Range(1f, 20f)] public float followSharpness = 5f;

    [Header("Floating placement (followViewer = false)")]
    [Tooltip("Distance the dialog floats forward of the page toward the " +
             "viewer, in metres. This is what makes it read as a separate " +
             "floating window instead of a panel glued to the screen.")]
    [Range(0.0f, 0.5f)] public float floatForward = 0.12f;

    [Tooltip("Gap between the bottom edge of the page and the top of the " +
             "dialog, in metres.")]
    [Range(0.0f, 0.3f)] public float verticalGap = 0.03f;

    [Tooltip("Tilt the dialog back toward the floor, in degrees, so it reads " +
             "like a little desk rather than a second vertical screen. 0 keeps " +
             "it facing the viewer exactly like the page.")]
    [Range(0f, 60f)] public float tiltDegrees = 20f;

    [Header("Typing")]
    [Tooltip("Hide the keyboard after Enter - most fields are done at that point.")]
    public bool hideOnEnter = true;

    [Tooltip("Shift falls back to lower case after one character, the way a " +
             "phone keyboard does. Off = shift latches until tapped again.")]
    public bool autoUnshift = true;

    [Header("Look")]
    public Color panelColor = new Color(0.11f, 0.11f, 0.12f, 0.98f);
    public Color panelBorderColor = new Color(0.03f, 0.03f, 0.04f, 1f);

    [Tooltip("Keycap fill. Hover/pressed come from the same saturated-cyan " +
             "scheme GeckoUIButton defaults to unless overridden here.")]
    public Color keyColor = new Color(0.165f, 0.165f, 0.180f, 1f);
    public Color keyHoverColor = new Color(0.22f, 0.22f, 0.24f, 1f);
    public Color keyPressedColor = new Color(0.27f, 0.27f, 0.31f, 1f);
    [Tooltip("Solid, not translucent - the shader is opaque + alpha-tested " +
             "(cutout) rather than alpha-blended, for reliable draw order " +
             "against the panel and the rest of the stack. See RoundedRectUnlit.shader.")]
    public Color keyShadowColor = new Color(0.045f, 0.045f, 0.05f, 1f);

    [Range(0f, 0.02f)] public float keyCornerRadius = 0.006f;
    [Range(0f, 0.02f)] public float dialogCornerRadius = 0.014f;
    [Range(0f, 0.006f)] public float keyShadowOffset = 0.0025f;

    [Header("Drag & Resize")]
    [Tooltip("Draw a grip bar above the tab so the whole cluster can be dragged.")]
    public bool enableDragHandle = true;

    [Tooltip("Draw a small grip square so the whole cluster can be resized.")]
    public bool enableResizeHandle = true;

    [Range(0.4f, 1f)] public float minScale = 0.5f;
    [Range(1f, 3f)] public float maxScale = 2f;

    [Header("Debug")]
    public bool verboseLogging = true;

    private const string Tag = "[GeckoVulkanBridge] ";

    private GeckoVulkanRenderer _browser;

    private Transform _root;
    private Transform _dialogRoot;   // floats forward; tilts; hosts panel + keys
    private Transform _keysRoot;
    private GeckoUIButton _shiftBtn, _layerBtn;

    // Plane basis, computed once in Build().
    private Vector3 _n, _uiUp, _uiRight;
    private Quaternion _facing;
    private float _planeH;

    private bool _shift;
    private bool _symbols;

    // Follow state. _repositioning latches on when you have looked away and
    // latches off once the panel has caught up, so it either sits perfectly
    // still or moves deliberately - never creeps.
    private bool _repositioning;

    // Set by a drag or resize grab; suspends the follow-viewer recenter until
    // the next Show()/SnapToViewer() snaps the cluster back under head control.
    private bool _userPositioned;

    private XRSimpleInteractable _dragHandle;
    private Transform _dragInteractor;
    private Vector3 _dragOffset;

    private XRSimpleInteractable _resizeHandle;
    private Transform _resizeInteractor;
    private float _resizeGrabDistance;
    private float _resizeGrabScale;

    // Uniform scale applied to _root.localScale on top of its authored (1,1,1).
    // Survives RebuildLayout(); Build() re-applies it after recreating _root.
    private float _scaleFactor = 1f;

    // Rebuilt whenever the layer or shift state changes - the labels and the
    // payloads both have to follow.
    private readonly List<TextMeshPro> _charLabels = new List<TextMeshPro>();
    private readonly List<GeckoUIButton> _charKeys = new List<GeckoUIButton>();

    private static readonly string[] kLetterRows =
    {
        "qwertyuiop",
        "asdfghjkl",
        "zxcvbnm"
    };

    private static readonly string[] kSymbolRows =
    {
        "1234567890",
        "@#$%&-+()",
        "*\"':;!?"
    };

    private const int kCols = 10;   // widest row (the digit row / top letter row)

    private static Shader _roundedShader;
    private static Shader RoundedShader =>
        _roundedShader != null ? _roundedShader : (_roundedShader = Shader.Find("Custom/RoundedRectUnlit"));

    public bool IsOpen => _dialogRoot != null && _dialogRoot.gameObject.activeSelf;

    private void Awake()
    {
        _browser = GetComponent<GeckoVulkanRenderer>();

        if (!TmpIsUsable())
        {
            Debug.LogError(Tag + "TextMeshPro has no default font asset. " +
                           "Run Window > TextMeshPro > Import TMP Essential Resources, " +
                           "then rebuild. Page keyboard disabled.");
            enabled = false;
            return;
        }

        Build();
        _dialogRoot.gameObject.SetActive(startVisible);
        Log($"built: keySize={keySize:F3} followViewer={followViewer} distance={viewerDistance:F2} visible={startVisible}");
    }

    /// <summary>
    /// Tears down and rebuilds the dialog from the plane's CURRENT transform.
    /// Build() runs once at Awake() and snapshots world-space position/rotation/
    /// scale into a separate GameObject (_root, parented to this plane's PARENT,
    /// not to the plane itself) - fine for a plane that never moves after Awake,
    /// but anything that repositions/rescales the plane LATER (CinemaScreenAligner
    /// re-aligning a cinema screen to a real auditorium, well after Awake) leaves
    /// the keyboard frozen at the old snapshot, which can end up anywhere relative
    /// to the plane's new pose - including inside it. Call this after such a
    /// reposition to make the dialog follow. Standalone usage that never moves
    /// the plane post-Awake has no reason to call this and is unaffected.
    /// </summary>
    public void RebuildLayout()
    {
        if (!enabled) return;   // Awake() already bailed (e.g. no TMP essentials) - nothing built to rebuild

        bool wasOpen = IsOpen;
        _dragInteractor = null;
        _resizeInteractor = null;
        if (_root != null) Destroy(_root.gameObject);
        _charLabels.Clear();
        _charKeys.Clear();
        _shiftBtn = null;
        _layerBtn = null;

        Build();
        _dialogRoot.gameObject.SetActive(wasOpen);
        Log("layout rebuilt against the plane's current transform");
    }

    private static bool TmpIsUsable()
    {
        // TMP_Settings.instance is null until the Essentials are imported, and
        // touching defaultFontAsset then throws rather than returning null.
        try { return TMP_Settings.defaultFontAsset != null; }
        catch { return false; }
    }

    // -------------------------------------------------------------------------
    // Public control surface - wire these to a controller button if you prefer
    // that to the on-screen tab.
    // -------------------------------------------------------------------------
    public void Show()
    {
        if (_dialogRoot == null) return;
        _userPositioned = false;  // a fresh open resumes following the viewer
        SnapToViewer();          // open it where you are looking, not where you left it
        _dialogRoot.gameObject.SetActive(true);
    }

    public void Hide() { if (_dialogRoot != null) _dialogRoot.gameObject.SetActive(false); }
    public void Toggle() { if (IsOpen) Hide(); else Show(); }

    /// <summary>Jump the cluster in front of the viewer immediately, no easing.</summary>
    public void SnapToViewer()
    {
        if (!followViewer || _root == null) return;
        if (!TryGetViewerPose(out Vector3 p, out Quaternion r)) return;
        _root.SetPositionAndRotation(p, r);
        _repositioning = false;
    }

    /// <summary>Where the cluster wants to be: arm's length ahead, below the eyeline.</summary>
    private bool TryGetViewerPose(out Vector3 pos, out Quaternion rot)
    {
        pos = default;
        rot = default;

        Transform head = viewer != null ? viewer
                       : (Camera.main != null ? Camera.main.transform : null);
        if (head == null) return false;

        // Level the forward vector: a keyboard that pitches with your gaze ends
        // up face-down on the floor the moment you glance at your hands.
        Vector3 fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up);
        if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.ProjectOnPlane(head.up, Vector3.up);
        if (fwd.sqrMagnitude < 1e-4f) return false;
        fwd.Normalize();

        pos = head.position + fwd * viewerDistance + Vector3.up * viewerHeightOffset;
        rot = Quaternion.LookRotation(fwd, Vector3.up);
        return true;
    }

    private void Update()
    {
        if (_dragInteractor != null)
            _root.position = _dragInteractor.position + _dragOffset;

        if (_resizeInteractor != null)
        {
            float d = Vector3.Distance(_root.position, _resizeInteractor.position);
            _scaleFactor = Mathf.Clamp(
                _resizeGrabScale * (d / _resizeGrabDistance), minScale, maxScale);
            _root.localScale = Vector3.one * _scaleFactor;
        }
    }

    // -------------------------------------------------------------------------
    private void OnDragSelectEntered(SelectEnterEventArgs args)
    {
        _dragInteractor = args.interactorObject.transform;
        _dragOffset = _root.position - _dragInteractor.position;
        _userPositioned = true;
    }

    private void OnDragSelectExited(SelectExitEventArgs args) => _dragInteractor = null;

    private void OnResizeSelectEntered(SelectEnterEventArgs args)
    {
        _resizeInteractor = args.interactorObject.transform;
        _resizeGrabDistance = Mathf.Max(
            Vector3.Distance(_root.position, _resizeInteractor.position), 0.01f);
        _resizeGrabScale = _scaleFactor;
        _userPositioned = true;
    }

    private void OnResizeSelectExited(SelectExitEventArgs args) => _resizeInteractor = null;

    private void LateUpdate()
    {
        if (!followViewer || _root == null || _userPositioned) return;

        Transform head = viewer != null ? viewer
                       : (Camera.main != null ? Camera.main.transform : null);
        if (head == null) return;
        if (!TryGetViewerPose(out Vector3 want, out Quaternion wantRot)) return;

        // Sit still while it is in front of you. Chasing the head every frame
        // makes a panel you cannot point at and a stomach you cannot settle,
        // so only start moving once you have actually turned or walked away.
        Vector3 toPanel = _root.position - head.position;
        Vector3 flat = Vector3.ProjectOnPlane(toPanel, Vector3.up);
        Vector3 gaze = Vector3.ProjectOnPlane(head.forward, Vector3.up);
        float offAngle = (flat.sqrMagnitude < 1e-4f || gaze.sqrMagnitude < 1e-4f)
                       ? 180f : Vector3.Angle(gaze, flat);

        if (!_repositioning &&
            (offAngle > recenterAngle || toPanel.magnitude > recenterDistance))
            _repositioning = true;

        if (!_repositioning) return;

        // Frame-rate independent easing; Lerp with a raw factor would move
        // faster on a 120Hz headset than a 72Hz one.
        float t = 1f - Mathf.Exp(-followSharpness * Time.unscaledDeltaTime);
        _root.position = Vector3.Lerp(_root.position, want, t);
        _root.rotation = Quaternion.Slerp(_root.rotation, wantRot, t);

        if ((_root.position - want).sqrMagnitude < 0.0004f &&
            Quaternion.Angle(_root.rotation, wantRot) < 1f)
            _repositioning = false;
    }

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------
    private void Build()
    {
        // _root comes first now: in follow mode the whole cluster is laid out
        // around _root's pose and parented to it, so LateUpdate can carry the
        // tab, the panel and every key by moving one transform.
        var rootGo = new GameObject("GeckoPageKeyboard");
        rootGo.transform.SetParent(transform.parent, false);
        _root = rootGo.transform;

        if (followViewer)
        {
            if (TryGetViewerPose(out Vector3 vp, out Quaternion vr))
                _root.SetPositionAndRotation(vp, vr);
            else
                _root.SetPositionAndRotation(transform.position, transform.rotation);

            _n = -_root.forward;                                        // faces the viewer
            _uiUp = Vector3.up;
            _uiRight = Vector3.Cross(_n, _uiUp).normalized;
            _facing = Quaternion.LookRotation(-_n, _uiUp);
            _planeH = 0f;                                               // nothing to hang under
            _repositioning = false;
        }
        else
        {
            // Unity's Plane mesh spans 10x10 units, so world size is 10 * scale.
            _planeH = 10f * transform.lossyScale.z;

            _n = transform.up.normalized;                               // faces the viewer
            _uiUp = Vector3.ProjectOnPlane(Vector3.up, _n).normalized;
            if (_uiUp.sqrMagnitude < 0.001f) _uiUp = transform.forward; // plane is horizontal
            _uiRight = Vector3.Cross(_n, _uiUp).normalized;
            _facing = Quaternion.LookRotation(-_n, _uiUp);
        }

        // Dialog size is fixed in metres regardless of plane size: 3 letter
        // rows + 1 action row tall, kCols keys wide, plus padding on all sides.
        int rows = kLetterRows.Length + 1;
        float dialogW = kCols * keySize + (kCols - 1) * gap + padding * 2f;
        float dialogH = rows * keySize + (rows - 1) * gap + padding * 2f;

        // The tab that opens the keyboard has to survive Hide() untouched, or
        // there is no way back once it's closed - so it is built on _root
        // (always active) rather than on the dialog it opens, and the dialog
        // is stacked below it instead of the other way around.
        float tabW = dialogW * 0.5f;
        float tabH = keySize * 0.8f;
        float tabGapBelowPage = 0.02f;

        // Follow mode hangs the cluster from _root, which is already parked in
        // front of the eyes. Page mode points it under the page, floated toward
        // the viewer so the tab and the dialog read as one floating cluster.
        Vector3 stackTop = followViewer
                         ? _root.position
                         : transform.position
                           + _n * floatForward
                           - _uiUp * (_planeH * 0.5f + tabGapBelowPage);

        if (showToggleTab)
        {
            var tabGo = new GameObject("KeyboardTab");
            tabGo.transform.SetParent(_root, false);
            tabGo.transform.position = stackTop - _uiUp * (tabH * 0.5f);
            tabGo.transform.rotation = _facing;   // stays upright even while the dialog is tilted

            var tab = MakeKey("KeyboardTab", "⌨", tabGo.transform, Vector3.zero,
                              tabW, tabH, Toggle);
            tab.SetColors(new Color(0.09f, 0.10f, 0.12f, 1f),
                          new Color(0.14f, 0.16f, 0.19f, 1f),
                          new Color(0.10f, 0.30f, 0.50f, 1f));
        }

        // Dialog hangs below the tab (or below the page directly, if the tab
        // is turned off) with its own gap, floated and tilted independently.
        float belowTab = showToggleTab ? tabH + verticalGap : verticalGap;
        Vector3 anchor = stackTop - _uiUp * (belowTab + dialogH * 0.5f);

        var dialogGo = new GameObject("Dialog");
        dialogGo.transform.SetParent(_root, false);
        dialogGo.transform.position = anchor;
        // Hinge at the dialog's own centre: a small panel tilting about its
        // centre reads as "propped up", not "sinking into the floor".
        dialogGo.transform.rotation = _facing * Quaternion.Euler(-tiltDegrees, 0f, 0f);
        _dialogRoot = dialogGo.transform;

        CreatePanel(dialogW, dialogH);

        var keysGo = new GameObject("Keys");
        keysGo.transform.SetParent(_dialogRoot, false);
        keysGo.transform.localPosition = new Vector3(0f, 0f, -0.006f);   // in front of the panel
        _keysRoot = keysGo.transform;

        BuildKeys(dialogW, dialogH, rows);

        BuildHandles(dialogW, stackTop);

        // Scale is applied last, about _root's own origin, so every child
        // built above with a world-space .position setter lands in the right
        // place first and is then grown/shrunk uniformly around that origin.
        _root.localScale = Vector3.one * _scaleFactor;
    }

    /// <summary>
    /// A drag bar and a resize square, stacked above the tab (or above the
    /// dialog directly, if the tab is off). Plain BoxCollider + XRSimpleInteractable
    /// - no GeckoUIButton - so grabbing them (grip) can never be mistaken for a
    /// key press (trigger) by GeckoXRButton's adoption path.
    /// </summary>
    private void BuildHandles(float dialogW, Vector3 stackTop)
    {
        if (!enableDragHandle && !enableResizeHandle) return;

        float dragH = keySize * 0.55f;
        float resizeSize = keySize * 0.7f;
        Vector3 rowCenter = stackTop + _uiUp * (0.012f + dragH * 0.5f);

        if (enableDragHandle && enableResizeHandle)
        {
            float dragW = dialogW - resizeSize - gap;
            Vector3 dragPos = rowCenter - _uiRight * (resizeSize * 0.5f + gap * 0.5f);
            Vector3 resizePos = rowCenter + _uiRight * (dragW * 0.5f + gap * 0.5f);
            _dragHandle = MakeHandle("DragHandle", dragPos, dragW, dragH,
                                     new Color(0.14f, 0.16f, 0.20f, 0.92f));
            _resizeHandle = MakeHandle("ResizeHandle", resizePos, resizeSize, resizeSize,
                                       new Color(0.10f, 0.30f, 0.50f, 0.92f));
        }
        else if (enableDragHandle)
        {
            _dragHandle = MakeHandle("DragHandle", rowCenter, dialogW, dragH,
                                     new Color(0.14f, 0.16f, 0.20f, 0.92f));
        }
        else
        {
            Vector3 pos = stackTop + _uiUp * (0.012f + resizeSize * 0.5f);
            _resizeHandle = MakeHandle("ResizeHandle", pos, resizeSize, resizeSize,
                                       new Color(0.10f, 0.30f, 0.50f, 0.92f));
        }

        if (_dragHandle != null)
        {
            _dragHandle.selectEntered.AddListener(OnDragSelectEntered);
            _dragHandle.selectExited.AddListener(OnDragSelectExited);
        }
        if (_resizeHandle != null)
        {
            _resizeHandle.selectEntered.AddListener(OnResizeSelectEntered);
            _resizeHandle.selectExited.AddListener(OnResizeSelectExited);
        }
    }

    /// <summary>A borderless, non-interactive-by-GeckoUIButton quad with a real physics collider.</summary>
    private XRSimpleInteractable MakeHandle(string name, Vector3 worldPos, float w, float h, Color color)
    {
        var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.name = name;
        Destroy(quad.GetComponent<MeshCollider>());
        var box = quad.AddComponent<BoxCollider>();
        box.size = new Vector3(1f, 1f, 0.05f);

        quad.transform.SetParent(_root, false);
        quad.transform.position = worldPos;
        quad.transform.rotation = _facing;
        quad.transform.localScale = new Vector3(w, h, 1f);

        quad.GetComponent<Renderer>().sharedMaterial = MakeRoundedMaterial(color, w, h, keyCornerRadius);

        return quad.AddComponent<XRSimpleInteractable>();
    }

    /// <summary>
    /// A material using the rounded-rect shader when available, falling back to
    /// a plain Unlit fill (square corners) if the shader failed to load - never
    /// a missing-shader magenta quad.
    /// </summary>
    private Material MakeRoundedMaterial(Color color, float w, float h, float cornerRadius)
    {
        Shader shader = RoundedShader
                      ?? Shader.Find("Universal Render Pipeline/Unlit")
                      ?? _browser.GetComponent<Renderer>().sharedMaterial.shader;
        var mat = new Material(shader);
        mat.mainTexture = null;
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
        if (mat.HasProperty("_Size")) mat.SetVector("_Size", new Vector4(w, h, 0f, 0f));
        if (mat.HasProperty("_CornerRadius")) mat.SetFloat("_CornerRadius", cornerRadius);
        return mat;
    }

    /// <summary>Builds a rounded-rect quad parented and positioned like any other dialog element.</summary>
    private GameObject CreateRoundedQuad(string name, Transform parent, Vector3 localPos,
                                         float w, float h, Color color, float cornerRadius)
    {
        var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.name = name;
        Destroy(quad.GetComponent<Collider>());   // callers add their own if they need one

        quad.transform.SetParent(parent, false);
        quad.transform.localPosition = localPos;
        quad.transform.localRotation = Quaternion.identity;
        quad.transform.localScale = new Vector3(w, h, 1f);

        quad.GetComponent<Renderer>().sharedMaterial = MakeRoundedMaterial(color, w, h, cornerRadius);
        return quad;
    }

    /// <summary>
    /// A borderless, non-interactive backing quad behind the keys - what makes
    /// the keyboard read as one small dialog box instead of loose floating keys.
    /// A slightly larger, darker quad behind it acts as the border.
    /// </summary>
    /// <summary>
    /// The dialog's local -Z points at the viewer, so MORE negative is NEARER.
    /// These two were stacked the wrong way round: the border sat in front of
    /// the panel and the panel in front of the keys, so every keycap was hidden
    /// and only its label - pushed further forward still - showed through. That
    /// is why hover appeared to do nothing: the surface changing colour was
    /// behind an opaque panel. Order from the viewer inward is labels, keys,
    /// panel, border.
    /// </summary>
    private void CreatePanel(float w, float h)
    {
        MakeBackdrop("PanelBorder", w + padding * 0.6f, h + padding * 0.6f,
                    -0.002f, panelBorderColor);
        MakeBackdrop("Panel", w, h, -0.004f, panelColor);
    }

    private void MakeBackdrop(string name, float w, float h, float zOffset, Color color)
    {
        CreateRoundedQuad(name, _dialogRoot, new Vector3(0f, 0f, zOffset), w, h, color, dialogCornerRadius);
    }

    private void BuildKeys(float dialogW, float dialogH, int rows)
    {
        float keyH = keySize;
        float keyW = keySize;

        // Local space of _keysRoot, which is centred on the dialog: top row
        // starts just under the top padding.
        float topY = dialogH * 0.5f - padding - keyH * 0.5f;

        for (int r = 0; r < kLetterRows.Length; r++)
        {
            int len = kLetterRows[r].Length;
            float rowW = len * keyW + (len - 1) * gap;
            float startX = -rowW * 0.5f + keyW * 0.5f;
            float y = topY - r * (keyH + gap);

            for (int c = 0; c < len; c++)
            {
                int row = r, col = c;                       // captured per key
                var pos = new Vector3(startX + c * (keyW + gap), y, 0f);
                var key = MakeKey($"Key_{r}_{c}", "", _keysRoot, pos, keyW, keyH, null);
                key.onClick = () => TypeChar(CharAt(row, col));
                _charKeys.Add(key);
                _charLabels.Add(key.GetComponentInChildren<TextMeshPro>());

                // Small secondary character in the corner, always showing the
                // symbol layer regardless of the current shift/layer state -
                // matches the reference keyboard's number/symbol hints.
                if (r < kSymbolRows.Length && c < kSymbolRows[r].Length)
                    AddCornerHint(key.transform, kSymbolRows[r][c].ToString(), keyW, keyH);
            }
        }

        // Action row: shift | layer | space | backspace | enter | hide.
        float ay = topY - kLetterRows.Length * (keyH + gap);
        float wideW = keyW * 3f + gap * 2f;                // space
        float actW = keyW * 1.3f;

        float totalW = actW * 5f + wideW + gap * 5f;
        float ax = -totalW * 0.5f + actW * 0.5f;

        _shiftBtn = MakeKey("Shift", "⇧", _keysRoot, new Vector3(ax, ay, 0f),
                            actW, keyH, ToggleShift);
        ax += actW * 0.5f + gap + actW * 0.5f;

        _layerBtn = MakeKey("Layer", "?123", _keysRoot, new Vector3(ax, ay, 0f),
                            actW, keyH, ToggleLayer);
        ax += actW * 0.5f + gap + wideW * 0.5f;

        MakeKey("Space", "space", _keysRoot, new Vector3(ax, ay, 0f),
                wideW, keyH, () => TypeChar(" "));
        ax += wideW * 0.5f + gap + actW * 0.5f;

        MakeKey("Backspace", "⌫", _keysRoot, new Vector3(ax, ay, 0f),
                actW, keyH, () => _browser.SendBackspace());
        ax += actW + gap;

        var enter = MakeKey("Enter", "↵", _keysRoot, new Vector3(ax, ay, 0f),
                            actW, keyH, PressEnter);
        enter.SetColors(new Color(0.10f, 0.45f, 0.25f, 1f),
                        new Color(0.14f, 0.60f, 0.33f, 1f),
                        new Color(0.10f, 0.70f, 0.40f, 1f));
        ax += actW + gap;

        MakeKey("Hide", "✕", _keysRoot, new Vector3(ax, ay, 0f),
                actW, keyH, Hide);

        RefreshLabels();
    }

    private GeckoUIButton MakeKey(string name, string label, Transform parent,
                                  Vector3 localPos, float w, float h,
                                  System.Action onClick)
    {
        // Shadow: a larger, darker, offset copy behind the key - the raw-quad
        // equivalent of the UI.Shadow component the popup menu's Canvas
        // buttons use, which only works on uGUI Graphics, not world-space quads.
        float shadowW = w + keyShadowOffset * 2f;
        float shadowH = h + keyShadowOffset * 2f;
        Vector3 shadowPos = localPos + new Vector3(keyShadowOffset, -keyShadowOffset, 0.001f);
        CreateRoundedQuad(name + "_Shadow", parent, shadowPos, shadowW, shadowH,
                          keyShadowColor, keyCornerRadius);

        var quad = CreateRoundedQuad(name, parent, localPos, w, h, keyColor, keyCornerRadius);
        var box = quad.AddComponent<BoxCollider>();       // we don't need textureCoord here
        box.size = new Vector3(1f, 1f, 0.05f);

        var btn = quad.AddComponent<GeckoUIButton>();
        btn.onClick = onClick;
        btn.payload = label;
        btn.SetColors(keyColor, keyHoverColor, keyPressedColor);

        var textGo = new GameObject("Label");
        textGo.transform.SetParent(quad.transform, false);
        // Undo the parent's non-uniform scale so glyphs aren't stretched, and
        // float slightly in front so it doesn't z-fight with the quad.
        textGo.transform.localScale = new Vector3(1f / w, 1f / h, 1f);
        textGo.transform.localPosition = new Vector3(0f, 0f, -0.01f);
        textGo.transform.localRotation = Quaternion.identity;

        var tmp = textGo.AddComponent<TextMeshPro>();
        tmp.text = label;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.color = Color.white;
        tmp.enableWordWrapping = false;
        tmp.rectTransform.sizeDelta = new Vector2(w, h);

        // A fixed fontSize has to be tuned against TMP's font-size-to-world-size
        // ratio, which depends on the active font asset and is easy to get
        // wrong by a large factor - exactly what caused the labels to overflow
        // their keycaps and overlap their neighbours. Auto-sizing sidesteps
        // that entirely: TMP shrinks the glyph until it fits inside sizeDelta,
        // whatever the true ratio is. fontSizeMax is a generous upper bound,
        // never the actual rendered size.
        tmp.enableAutoSizing = true;
        tmp.fontSizeMin = 0.01f;
        tmp.fontSizeMax = 500f;
        // Margin keeps glyphs off the keycap's edge instead of touching it.
        tmp.margin = new Vector4(w * 0.12f, h * 0.12f, w * 0.12f, h * 0.12f);

        return btn;
    }

    /// <summary>
    /// Small dim label near a letter key's top-right corner. Parented to the
    /// key, so it moves/scales with it automatically. keyTransform.localScale
    /// is (w, h, 1) - dividing it back out here keeps the glyph itself
    /// unstretched, the same trick the main key label already uses.
    /// </summary>
    private void AddCornerHint(Transform keyTransform, string hint, float w, float h)
    {
        var hintGo = new GameObject("CornerHint");
        hintGo.transform.SetParent(keyTransform, false);
        hintGo.transform.localScale = new Vector3(1f / w, 1f / h, 1f);
        hintGo.transform.localPosition = new Vector3(0.30f, 0.30f, -0.011f);
        hintGo.transform.localRotation = Quaternion.identity;

        var tmp = hintGo.AddComponent<TextMeshPro>();
        tmp.text = hint;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.color = new Color(0.55f, 0.55f, 0.62f, 1f);
        tmp.enableWordWrapping = false;
        tmp.rectTransform.sizeDelta = new Vector2(keySize * 0.4f, keySize * 0.4f);
        tmp.enableAutoSizing = true;
        tmp.fontSizeMin = 0.01f;
        tmp.fontSizeMax = 300f;
    }

    // -------------------------------------------------------------------------
    // Behaviour
    // -------------------------------------------------------------------------
    private string CharAt(int row, int col)
    {
        string[] set = _symbols ? kSymbolRows : kLetterRows;
        if (row >= set.Length) return "";
        string r = set[row];
        if (col >= r.Length) return "";
        string ch = r[col].ToString();
        return _shift && !_symbols ? ch.ToUpperInvariant() : ch;
    }

    private void TypeChar(string ch)
    {
        if (string.IsNullOrEmpty(ch)) return;
        _browser.SendText(ch);
        if (_shift && autoUnshift && !_symbols) { _shift = false; RefreshLabels(); }
    }

    private void PressEnter()
    {
        _browser.SendEnter();
        if (hideOnEnter) Hide();
    }

    private void ToggleShift() { _shift = !_shift; RefreshLabels(); }

    private void ToggleLayer()
    {
        _symbols = !_symbols;
        if (_symbols) _shift = false;
        RefreshLabels();
    }

    /// <summary>
    /// Repaints every character key for the current layer. Keys whose row is
    /// shorter in this layer than in the other one go blank and stop responding,
    /// which is cheaper and steadier than destroying and rebuilding the grid.
    /// </summary>
    private void RefreshLabels()
    {
        int i = 0;
        for (int r = 0; r < kLetterRows.Length; r++)
        {
            for (int c = 0; c < kLetterRows[r].Length; c++, i++)
            {
                if (i >= _charKeys.Count) break;
                string ch = CharAt(r, c);
                _charLabels[i].text = ch;
                _charKeys[i].payload = ch;
                _charKeys[i].Interactable = !string.IsNullOrEmpty(ch);
            }
        }

        if (_layerBtn != null)
        {
            var lbl = _layerBtn.GetComponentInChildren<TextMeshPro>();
            if (lbl != null) lbl.text = _symbols ? "ABC" : "?123";
        }

        if (_shiftBtn != null)
        {
            _shiftBtn.SetColors(
                _shift ? new Color(0.10f, 0.35f, 0.55f, 1f) : new Color(0.16f, 0.17f, 0.20f, 1f),
                _shift ? new Color(0.14f, 0.45f, 0.68f, 1f) : new Color(0.26f, 0.29f, 0.34f, 1f),
                new Color(0.10f, 0.45f, 0.75f, 1f));
        }
    }

    private void Log(string m) { if (verboseLogging) Debug.Log(Tag + "keyboard: " + m); }
}
