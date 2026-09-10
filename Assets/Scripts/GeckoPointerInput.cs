// =============================================================================
//  GeckoPointerInput.cs
//
//  Point at the browser plane with EITHER Quest controller and click.
//
//  Put this on the SAME GameObject as GeckoVulkanRenderer (the BrowserPlane).
//
//  Two controllers, one page
//  -------------------------
//  Each hand gets its own ray, laser and hover/press state, so both lasers are
//  live at once and either hand can drive the toolbar.
//
//  Gecko, however, has exactly ONE touch stream and ONE mouse cursor. Two hands
//  pressing the page simultaneously would interleave DOWN/MOVE/UP events from
//  different coordinates and Gecko would see incoherent gestures. So page input
//  is arbitrated: the first hand to press on the page owns it until release, and
//  the other hand is ignored by the page (but still drives its own laser and can
//  still use the toolbar). Hover follows the owner, or the first hand on the
//  plane when nobody is pressing.
//
//  Why not the XR Interaction Toolkit
//  ----------------------------------
//  XRI 3.5.1 is in the project but the scene has no XRI rig - just
//  XRRig > Camera Offset > Main Camera. Reading poses from
//  UnityEngine.XR.InputDevices needs no rig, no EventSystem and no controller
//  GameObjects. Drop this on the plane and both hands work.
//
//  Click path:
//    controller pose -> ray -> MeshCollider hit -> hit.textureCoord (UV)
//    -> surface pixels -> injectTouch() -> MotionEvent -> PanZoomController
// =============================================================================

using UnityEngine;
using UnityEngine.XR;
using System.Collections.Generic;

[RequireComponent(typeof(GeckoVulkanRenderer))]
public class GeckoPointerInput : MonoBehaviour
{
    [Header("Hands")]
    public bool enableRightHand = true;
    public bool enableLeftHand  = true;

    [Tooltip("Which hand wins if both are on the page and neither is pressing.")]
    public bool preferRightHand = true;

    [Tooltip("The transform tracked poses are relative to - normally the " +
             "'Camera Offset' object under your XR rig. Auto-detected from " +
             "Camera.main's parent if left empty.")]
    public Transform trackingSpace;

    [Header("XR Interactor (optional)")]
    [Tooltip("Assign this hand's XR Ray Interactor (or Near-Far Interactor) " +
             "transform to aim the page click from THAT ray instead of a ray " +
             "recomputed here from the raw controller pose. This is what makes " +
             "the ray you already see for grabbing/UI/teleport the SAME ray that " +
             "clicks the page, instead of a second, independently-aimed one. " +
             "Leave empty to fall back to the raw device pose (trackingSpace).")]
    public Transform rightRayInteractor;
    public Transform leftRayInteractor;

    [Header("Ray")]
    [Tooltip("How far the hit test reaches. NOT the laser length - it must cover " +
             "the real distance to the plane or nothing is ever clickable.")]
    public float maxRayDistance = 100f;

    [Header("Orientation")]
    [Tooltip("EXTRA horizontal flip on top of the renderer's flipHorizontally.")]
    public bool invertPointerX = false;
    [Tooltip("EXTRA vertical flip on top of the renderer's flipVertically.")]
    public bool invertPointerY = false;

    [Header("Scrolling")]
    public float scrollSpeed = 8f;
    public float scrollDeadzone = 0.15f;

    [Header("Laser")]
    public bool drawPointer = true;
    [Tooltip("Length drawn when the ray hits nothing. Cosmetic only.")]
    public float pointerLength = 10f;
    public Color rightHandColor = new Color(0.30f, 0.80f, 1.00f, 0.9f);
    public Color leftHandColor  = new Color(1.00f, 0.70f, 0.30f, 0.9f);

    [Header("Hover")]
    public bool sendHoverEvents = true;
    public float hoverPixelThreshold = 3f;
    public float hoverRateHz = 30f;

    [Header("Haptics")]
    public bool haptics = true;
    [Range(0f, 1f)] public float hapticAmplitude = 0.3f;
    public float hapticDuration = 0.02f;

    [Header("Input")]
    [Range(0.1f, 0.9f)] public float triggerThreshold = 0.5f;

    [Header("Debug")]
    public bool logClicks = false;
    public bool logPointerState = false;

    private const string Tag = "[GeckoVulkanBridge] ";

    // -------------------------------------------------------------------------
    // Per-hand state
    // -------------------------------------------------------------------------
    private class Hand
    {
        public readonly bool IsRight;
        public readonly XRNode Node;

        public InputDevice Device;
        public bool DeviceValid;

        public LineRenderer Laser;

        public bool OnPlane;
        public Vector2 Pixel;
        public Vector2 Uv;

        public GeckoUIButton Hovered;
        public GeckoUIButton Pressed;

        public bool TriggerHeld;      // holding the PAGE (not a UI button)
        public bool WasOnPlane;

        public float ScrollAccum;
        public string RayHitDesc = "none";

        public Hand(bool isRight)
        {
            IsRight = isRight;
            Node = isRight ? XRNode.RightHand : XRNode.LeftHand;
        }
    }

    private GeckoVulkanRenderer _browser;
    private Collider _collider;

    private Hand _right, _left;
    private Hand _pageOwner;                 // hand currently pressing the page
    private Vector2 _lastHoverSent = new Vector2(-999f, -999f);
    private float _lastHoverTime;

    private static readonly List<InputDevice> s_Devices = new List<InputDevice>();

    // -------------------------------------------------------------------------
    private void Awake()
    {
        _browser  = GetComponent<GeckoVulkanRenderer>();
        _collider = GetComponent<Collider>();

        if (_collider == null)
        {
            Debug.LogError(Tag + "BrowserPlane has no Collider. Add a Mesh Collider.");
            enabled = false;
            return;
        }
        if (!(_collider is MeshCollider))
            Debug.LogWarning(Tag + "Collider is " + _collider.GetType().Name +
                             ", not MeshCollider - RaycastHit.textureCoord needs a " +
                             "MeshCollider, so clicks will not map.");

        if (trackingSpace == null && Camera.main != null)
            trackingSpace = Camera.main.transform.parent ?? Camera.main.transform;

        // MeshCollider raycasts skip back faces by default; if the plane is ever
        // oriented so the ray arrives from behind, every hit test fails silently.
        Physics.queriesHitBackfaces = true;

        _right = new Hand(true);
        _left  = new Hand(false);
        if (drawPointer)
        {
            _right.Laser = MakeLaser("GeckoLaserRight", rightHandColor);
            _left.Laser  = MakeLaser("GeckoLaserLeft",  leftHandColor);
        }
    }

    private LineRenderer MakeLaser(string name, Color colour)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);

        var lr = go.AddComponent<LineRenderer>();
        lr.widthMultiplier = 0.005f;
        lr.positionCount = 2;
        lr.useWorldSpace = true;

        Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
        if (shader == null)
        {
            Debug.LogWarning(Tag + "No laser shader; pointer ray hidden. Clicking still works.");
            Destroy(go);
            return null;
        }
        lr.material = new Material(shader);
        lr.startColor = lr.endColor = colour;
        lr.enabled = false;
        return lr;
    }

    // -------------------------------------------------------------------------
    private void Update()
    {
        if (_browser == null || !_browser.IsInteractive) return;

        UpdateHand(_right, enableRightHand, rightRayInteractor);
        UpdateHand(_left,  enableLeftHand,  leftRayInteractor);

        DriveHover();

        if (logPointerState && Time.frameCount % 72 == 0)
            Debug.Log(Tag + $"R[{Describe(_right)}]  L[{Describe(_left)}]  " +
                      $"owner={(_pageOwner == null ? "none" : (_pageOwner.IsRight ? "R" : "L"))}");
    }

    private string Describe(Hand h) =>
        !h.DeviceValid ? "no device"
                       : $"hit={h.OnPlane} px={h.Pixel} ray={h.RayHitDesc} held={h.TriggerHeld}";

    /// <summary>Ray, hover, buttons, page press and scroll for one hand.</summary>
    private void UpdateHand(Hand h, bool enabled, Transform rayOverride)
    {
        // The device is still resolved even when an interactor supplies the ray:
        // trigger/grip/stick and haptics below all read h.Device, and those are
        // the same physical inputs regardless of where the ray comes from.
        if (!enabled || !TryGetHandDevice(h))
        {
            if (h.Laser != null) h.Laser.enabled = false;
            ClearHand(h);
            return;
        }

        Vector3 origin, dir;
        if (rayOverride != null)
        {
            // Follow the assigned XR Interactor's own transform instead of
            // recomputing a ray from the raw device pose - this is what keeps
            // the ray you can already see (grabbing, UI, teleport aim) and the
            // one that clicks the page in exact agreement.
            origin = rayOverride.position;
            dir = rayOverride.forward;
        }
        else
        {
            if (!h.Device.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 localPos) ||
                !h.Device.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion localRot))
            {
                if (h.Laser != null) h.Laser.enabled = false;
                return;
            }

            origin = trackingSpace != null ? trackingSpace.TransformPoint(localPos) : localPos;
            Quaternion rot = trackingSpace != null ? trackingSpace.rotation * localRot : localRot;
            dir = rot * Vector3.forward;
        }

        // --- hit test ---------------------------------------------------------
        h.OnPlane = false;
        h.RayHitDesc = "none";
        Vector3 endPoint = origin + dir * pointerLength;

        GeckoUIButton hovered = null;
        if (Physics.Raycast(origin, dir, out RaycastHit anyHit, maxRayDistance))
        {
            endPoint = anyHit.point;
            hovered = anyHit.collider.GetComponent<GeckoUIButton>();
            h.RayHitDesc = anyHit.collider.name + "@" + anyHit.distance.ToString("F2");
        }

        if (h.Hovered != null && h.Hovered != hovered) h.Hovered.SetHovered(false);
        bool enteredButton = hovered != null && hovered != h.Hovered;
        h.Hovered = hovered;
        if (h.Hovered != null) h.Hovered.SetHovered(true);
        if (enteredButton) Pulse(h, hapticAmplitude, hapticDuration);

        if (hovered == null &&
            _collider.Raycast(new Ray(origin, dir), out RaycastHit hit, maxRayDistance))
        {
            h.OnPlane = true;
            endPoint = hit.point;
            h.Uv = hit.textureCoord;

            // Mirror exactly what the material does when it samples, so the texel
            // under the pointer is the texel you can see.
            float u = _browser.flipHorizontally ? 1f - h.Uv.x : h.Uv.x;
            float v = _browser.flipVertically   ? 1f - h.Uv.y : h.Uv.y;

            float px = u * _browser.SurfaceWidth;
            float py = (1f - v) * _browser.SurfaceHeight;   // texture V up, page Y down
            if (invertPointerX) px = _browser.SurfaceWidth  - px;
            if (invertPointerY) py = _browser.SurfaceHeight - py;

            h.Pixel = new Vector2(
                Mathf.Clamp(px, 0f, _browser.SurfaceWidth  - 1f),
                Mathf.Clamp(py, 0f, _browser.SurfaceHeight - 1f));
        }

        if (h.Laser != null)
        {
            h.Laser.enabled = drawPointer;
            h.Laser.SetPosition(0, origin);
            h.Laser.SetPosition(1, endPoint);
        }

        // --- trigger ----------------------------------------------------------
        bool hasBool  = h.Device.TryGetFeatureValue(CommonUsages.triggerButton, out bool tBool);
        bool hasAxis  = h.Device.TryGetFeatureValue(CommonUsages.trigger, out float tAxis);
        bool trigger  = (hasBool && tBool) || (hasAxis && tAxis >= triggerThreshold);

        // UI buttons: press, then release over the same button. Sliding off cancels.
        if (trigger && h.Pressed == null && !h.TriggerHeld && h.Hovered != null)
        {
            h.Pressed = h.Hovered;
            h.Pressed.SetPressed(true);
        }
        else if (!trigger && h.Pressed != null)
        {
            h.Pressed.SetPressed(false);
            if (h.Pressed == h.Hovered)
            {
                if (logClicks) Debug.Log(Tag + "UI click: " + h.Pressed.name);
                h.Pressed.Activate();
            }
            h.Pressed = null;
        }
        // Page: only one hand at a time owns the touch stream.
        else if (trigger && !h.TriggerHeld && h.Pressed == null && h.OnPlane &&
                 _pageOwner == null)
        {
            _pageOwner = h;
            h.TriggerHeld = true;
            Pulse(h, hapticAmplitude * 1.5f, hapticDuration);
            _browser.SendTouch(0, h.Pixel.x, h.Pixel.y);            // DOWN
            if (logClicks) Debug.Log(Tag + $"press uv={h.Uv} px={h.Pixel}");
        }
        else if (trigger && h.TriggerHeld && _pageOwner == h)
        {
            _browser.SendTouch(2, h.Pixel.x, h.Pixel.y);            // MOVE
        }
        else if (!trigger && h.TriggerHeld)
        {
            h.TriggerHeld = false;
            _browser.SendTouch(1, h.Pixel.x, h.Pixel.y);            // UP -> click
            if (_pageOwner == h) _pageOwner = null;
            if (logClicks) Debug.Log(Tag + $"release px={h.Pixel}");
        }

        // --- thumbstick scroll ------------------------------------------------
        if (scrollSpeed > 0f && h.OnPlane && !h.TriggerHeld && h.Pressed == null &&
            h.Device.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 stick))
        {
            if (Mathf.Abs(stick.y) > scrollDeadzone)
            {
                h.ScrollAccum += stick.y * scrollSpeed * Time.deltaTime;
                if (Mathf.Abs(h.ScrollAccum) >= 1f)
                {
                    // Truncate toward zero; Mathf has no Truncate and Floor would
                    // over-scroll on negatives.
                    float ticks = Mathf.Sign(h.ScrollAccum) * Mathf.Floor(Mathf.Abs(h.ScrollAccum));
                    h.ScrollAccum -= ticks;
                    _browser.SendScroll(h.Pixel.x, h.Pixel.y, 0f, -ticks);
                }
            }
            else h.ScrollAccum = 0f;
        }
    }

    /// <summary>
    /// One cursor, two hands: the page owner wins, else the preferred hand that
    /// is actually on the plane.
    /// </summary>
    private void DriveHover()
    {
        if (!sendHoverEvents) return;

        // Never interleave synthetic mouse hover with an active touch stream.
        // SendHover posts ACTION_HOVER_MOVE with SOURCE_MOUSE; delivering that
        // between a touch DOWN and UP makes Chromium see a mouse pointer AND a
        // touch pointer for one physical press, and players built for both
        // (YouTube) then handle it as two separate taps - the first toggles
        // play, the second toggles pause ~250ms later. That is what made video
        // "pause itself" on fullscreen: the tap on the fullscreen button was
        // also read as a tap on the video. Touch has no hover concept anyway,
        // so there is nothing to send while a press is in flight.
        if (_pageOwner != null) return;

        Hand first  = preferRightHand ? _right : _left;
        Hand second = preferRightHand ? _left  : _right;

        Hand driver = first.OnPlane  ? first
                    : second.OnPlane ? second
                    : null;

        if (driver != null)
        {
            if (!driver.WasOnPlane) Pulse(driver, hapticAmplitude * 0.6f, hapticDuration);

            bool moved = (driver.Pixel - _lastHoverSent).sqrMagnitude
                         >= hoverPixelThreshold * hoverPixelThreshold;
            bool rateOk = hoverRateHz <= 0f ||
                          (Time.unscaledTime - _lastHoverTime) >= 1f / hoverRateHz;
            if (moved && rateOk)
            {
                _browser.SendHover(driver.Pixel.x, driver.Pixel.y);
                _lastHoverSent = driver.Pixel;
                _lastHoverTime = Time.unscaledTime;
            }
        }
        else if (_right.WasOnPlane || _left.WasOnPlane)
        {
            // Nobody on the plane any more - clear the stuck :hover.
            _browser.SendHoverExit(_lastHoverSent.x, _lastHoverSent.y);
            _lastHoverSent = new Vector2(-999f, -999f);
        }

        _right.WasOnPlane = _right.OnPlane;
        _left.WasOnPlane  = _left.OnPlane;
    }

    /// <summary>
    /// Resolves a specific hand. Asks for HeldInHand|Controller|Left/Right first:
    /// a bare XRNode lookup can return a hand-tracking device, which has poses but
    /// no buttons and reads as "trigger exists but is always 0".
    /// </summary>
    private bool TryGetHandDevice(Hand h)
    {
        var wanted = InputDeviceCharacteristics.HeldInHand |
                     InputDeviceCharacteristics.Controller |
                     (h.IsRight ? InputDeviceCharacteristics.Right
                                : InputDeviceCharacteristics.Left);

        InputDevices.GetDevicesWithCharacteristics(wanted, s_Devices);
        if (s_Devices.Count > 0 && s_Devices[0].isValid)
        {
            h.Device = s_Devices[0];
            h.DeviceValid = true;
            return true;
        }

        InputDevices.GetDevicesAtXRNode(h.Node, s_Devices);
        if (s_Devices.Count > 0 && s_Devices[0].isValid)
        {
            h.Device = s_Devices[0];
            h.DeviceValid = true;
            return true;
        }

        h.DeviceValid = false;
        return false;
    }

    /// <summary>
    /// Controller haptics go through OpenXR. Android's Vibrator/VibratorManager
    /// would buzz the headset, not the controller.
    /// </summary>
    private void Pulse(Hand h, float amplitude, float duration)
    {
        if (!haptics || !h.DeviceValid || !h.Device.isValid) return;
        if (!h.Device.TryGetHapticCapabilities(out HapticCapabilities caps)) return;
        if (!caps.supportsImpulse) return;
        h.Device.SendHapticImpulse(0u, Mathf.Clamp01(amplitude), duration);
    }

    private void ClearHand(Hand h)
    {
        if (h.Hovered != null) { h.Hovered.SetHovered(false); h.Hovered = null; }
        if (h.Pressed != null) { h.Pressed.SetPressed(false); h.Pressed = null; }
        if (h.TriggerHeld)
        {
            h.TriggerHeld = false;
            _browser.SendTouch(3, h.Pixel.x, h.Pixel.y);            // CANCEL
            if (_pageOwner == h) _pageOwner = null;
        }
        h.OnPlane = false;
    }

    /// <summary>
    /// True while the given hand's ray is resting on the browser plane. Used by
    /// GeckoCinemaLocomotion to suppress that hand's turn/move input while it's
    /// mid-interaction with the page (e.g. scrolling) - without this, aiming at
    /// the page and pushing the same stick to scroll would also turn or walk.
    /// </summary>
    public bool IsHandOnPage(bool rightHand) => (rightHand ? _right : _left).OnPlane;

    private void OnDisable()
    {
        if (_right != null) ClearHand(_right);
        if (_left  != null) ClearHand(_left);
    }
}
