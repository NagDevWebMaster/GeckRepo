// =============================================================================
//  GeckoXRInteraction.cs
//
//  Page input for the browser plane, driven by the XR Interaction Toolkit.
//
//  Replaces GeckoPointerInput. That script predates the XRI rig in this scene:
//  it read poses straight from UnityEngine.XR.InputDevices, cast its own ray,
//  drew its own laser and hand-rolled the "one page, two hands" arbitration,
//  because there was no rig to ask. There is one now, so all of that is XRI's
//  job and this component only does the parts XRI cannot do.
//
//  What XRI now owns
//  -----------------
//    hover / unhover      the interactors' own casters decide, not a Raycast here
//    which hand is where  IXRHoverInteractor.handedness
//    the laser            the Near-Far Interactor's LineVisual, which now stops
//                         on the plane because the plane is a real interactable
//    haptics              SimpleHapticFeedback already on each interactor
//
//  What is still done here, and why
//  --------------------------------
//  1. The pixel. XRI reports *that* an interactor is on this interactable, never
//     *where* in texture space. So for the interactor XRI hands us we cast its
//     ray at this collider once, purely to read RaycastHit.textureCoord. That
//     needs a MeshCollider.
//
//  2. The click. XRI's Select action is the GRIP on a Quest controller, not the
//     trigger - see GeckoXRPress. Clicking a page is a UI click, so the press
//     edge comes from XRI Left/Right Interaction/UI Press, which is the trigger.
//     Grip is left alone for grabbing.
//
//  3. Scroll, which has no XRI concept at all, from XRI Left/Right
//     Interaction/UI Scroll.
// =============================================================================

using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Inputs;

[RequireComponent(typeof(GeckoVulkanRenderer))]
[RequireComponent(typeof(XRSimpleInteractable))]
public class GeckoXRInteraction : MonoBehaviour
{
    [Header("Hit test")]
    [Tooltip("How far the UV hit test reaches from the interactor. Only needs to " +
             "cover the real distance to the plane; XRI has already decided the " +
             "ray is on it by the time this runs.")]
    public float maxRayDistance = 100f;

    [Header("Click")]
    [Tooltip("XRI Left Interaction/UI Press - the trigger. NOT Select, which is " +
             "the grip.")]
    public InputActionProperty leftPress;
    [Tooltip("XRI Right Interaction/UI Press - the trigger.")]
    public InputActionProperty rightPress;

    [Header("Orientation")]
    [Tooltip("EXTRA horizontal flip on top of the renderer's flipHorizontally.")]
    public bool invertPointerX = false;
    [Tooltip("EXTRA vertical flip on top of the renderer's flipVertically.")]
    public bool invertPointerY = false;

    [Header("Scrolling")]
    [Tooltip("XRI Left Interaction/UI Scroll.")]
    public InputActionProperty leftScroll;
    [Tooltip("XRI Right Interaction/UI Scroll.")]
    public InputActionProperty rightScroll;
    public float scrollSpeed = 10f;
    public float scrollDeadzone = 0.15f;

    [Header("Hover")]
    public bool sendHoverEvents = true;
    [Tooltip("Prefer the right hand's cursor when both hands hover and neither presses.")]
    public bool preferRightHand = true;
    public float hoverPixelThreshold = 3f;
    public float hoverRateHz = 30f;

    [Header("UI buttons")]
    [Tooltip("Give every GeckoUIButton an XR interactable so the toolbar and the " +
             "on-screen keyboard are driven by the same rays as the page. Turn " +
             "this off in scenes where CinemaPointerInput still raycasts for " +
             "those buttons itself, or each press would register twice.")]
    public bool convertUIButtons = true;

    [Header("Debug")]
    public bool logClicks = false;
    public bool logPointerState = false;

    private const string Tag = "[GeckoVulkanBridge] ";

    private GeckoVulkanRenderer _browser;
    private Collider _collider;
    private XRSimpleInteractable _interactable;

    /// <summary>One hand's view of the page, assembled from what XRI reports.</summary>
    private class Hand
    {
        public readonly InteractorHandedness Handedness;
        public IXRHoverInteractor Hovering;   // null when this hand is off the plane
        public bool Held;                     // owns the touch stream
        public Vector2 Pixel;
        public float ScrollAccum;

        public Hand(InteractorHandedness h) { Handedness = h; }
        public bool OnPage => Hovering != null;
    }

    private Hand _left, _right;
    private Hand _pageOwner;                  // only one hand may touch at a time

    private Vector2 _lastHoverSent = new Vector2(-999f, -999f);
    private float _lastHoverTime;

    // -------------------------------------------------------------------------
    private void Awake()
    {
        _browser = GetComponent<GeckoVulkanRenderer>();
        _interactable = GetComponent<XRSimpleInteractable>();
        _collider = GetComponent<Collider>();

        _left  = new Hand(InteractorHandedness.Left);
        _right = new Hand(InteractorHandedness.Right);

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

        // MeshCollider raycasts skip back faces by default; if the plane is ever
        // oriented so the ray arrives from behind, every hit test fails silently.
        Physics.queriesHitBackfaces = true;
    }

    private void OnEnable()
    {
        leftPress.EnableDirectAction();
        rightPress.EnableDirectAction();
        leftScroll.EnableDirectAction();
        rightScroll.EnableDirectAction();

        // Published so GeckoXRButton clicks off the same physical button.
        GeckoXRPress.Left  = leftPress.action;
        GeckoXRPress.Right = rightPress.action;

        if (leftPress.action == null || rightPress.action == null)
            Debug.LogError(Tag + "UI Press action not assigned - the trigger will " +
                           "not click the page. Assign XRI Left/Right " +
                           "Interaction/UI Press.");

        if (convertUIButtons)
        {
            // Buttons that exist now, and every one built from here on.
            GeckoUIButton.Created += AdoptButton;
            foreach (var b in FindObjectsByType<GeckoUIButton>(FindObjectsInactive.Include,
                                                               FindObjectsSortMode.None))
                AdoptButton(b);
        }
    }

    private void OnDisable()
    {
        leftPress.DisableDirectAction();
        rightPress.DisableDirectAction();
        leftScroll.DisableDirectAction();
        rightScroll.DisableDirectAction();

        GeckoUIButton.Created -= AdoptButton;
        GeckoXRPress.Left = GeckoXRPress.Right = null;

        if (_pageOwner != null && _browser != null)
            _browser.SendTouch(3, _pageOwner.Pixel.x, _pageOwner.Pixel.y);   // CANCEL

        _left.Hovering = _right.Hovering = null;
        _left.Held = _right.Held = false;
        _pageOwner = null;
    }

    private static void AdoptButton(GeckoUIButton button)
    {
        if (button != null && button.GetComponent<GeckoXRButton>() == null)
            button.gameObject.AddComponent<GeckoXRButton>();
    }

    // -------------------------------------------------------------------------
    private void Update()
    {
        if (_browser == null || !_browser.IsInteractive) return;

        // Ask XRI which interactor, if any, each hand currently has on the plane.
        _left.Hovering = _right.Hovering = null;
        var hovering = _interactable.interactorsHovering;
        for (int i = 0; i < hovering.Count; i++)
        {
            if (hovering[i].handedness == InteractorHandedness.Left)  _left.Hovering  = hovering[i];
            else if (hovering[i].handedness == InteractorHandedness.Right) _right.Hovering = hovering[i];
        }

        UpdateHand(_left);
        UpdateHand(_right);

        DriveHover();

        Scroll(_left,  leftScroll);
        Scroll(_right, rightScroll);

        if (logPointerState && Time.frameCount % 72 == 0)
            Debug.Log(Tag + $"L[on={_left.OnPage} px={_left.Pixel} held={_left.Held}]  " +
                      $"R[on={_right.OnPage} px={_right.Pixel} held={_right.Held}]  " +
                      $"owner={(_pageOwner == null ? "none" : _pageOwner.Handedness.ToString())}");
    }

    /// <summary>Pixel, then press / drag / release for one hand.</summary>
    private void UpdateHand(Hand h)
    {
        // The VR menu floats between the player and the page. Whichever hand is
        // pointing at it belongs to the menu, not to the browser.
        if (h.Hovering != null && GeckoXRPress.IsOverUI(h.Hovering))
        {
            h.Hovering = null;
            if (h.Held)
            {
                h.Held = false;
                if (_pageOwner == h) _pageOwner = null;
                _browser.SendTouch(3, h.Pixel.x, h.Pixel.y);          // CANCEL
            }
            return;
        }

        if (h.OnPage && TryGetPixel(h.Hovering, out Vector2 px, out Vector2 uv))
            h.Pixel = px;
        else if (!h.Held)
            return;                                  // off the plane and not holding

        bool pressed = GeckoXRPress.IsPressed(h.Handedness);

        if (pressed && !h.Held && h.OnPage && _pageOwner == null)
        {
            h.Held = true;
            _pageOwner = h;
            _browser.SendTouch(0, h.Pixel.x, h.Pixel.y);              // DOWN
            if (logClicks) Debug.Log(Tag + $"press {h.Handedness} px={h.Pixel}");
        }
        else if (pressed && h.Held)
        {
            _browser.SendTouch(2, h.Pixel.x, h.Pixel.y);              // MOVE
        }
        else if (!pressed && h.Held)
        {
            h.Held = false;
            if (_pageOwner == h) _pageOwner = null;
            _browser.SendTouch(1, h.Pixel.x, h.Pixel.y);              // UP -> click
            if (logClicks) Debug.Log(Tag + $"release {h.Handedness} px={h.Pixel}");
        }
    }

    /// <summary>
    /// One cursor, two hands: the preferred hand wins, else whichever is on the
    /// plane.
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
        // "pause itself" on fullscreen. Touch has no hover concept anyway, so
        // there is nothing to send while a press is in flight.
        if (_pageOwner != null) return;

        Hand first  = preferRightHand ? _right : _left;
        Hand second = preferRightHand ? _left  : _right;
        Hand driver = first.OnPage ? first : second.OnPage ? second : null;

        if (driver != null)
        {
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
        else if (_lastHoverSent.x > -900f)
        {
            // Nobody on the plane any more - clear the stuck :hover.
            _browser.SendHoverExit(_lastHoverSent.x, _lastHoverSent.y);
            _lastHoverSent = new Vector2(-999f, -999f);
        }
    }

    private void Scroll(Hand h, InputActionProperty action)
    {
        // Scrolling while pressing would fight the drag the page is already
        // tracking, so the hand holding the page does not scroll.
        if (scrollSpeed <= 0f || !h.OnPage || h.Held) { h.ScrollAccum = 0f; return; }

        var a = action.action;
        if (a == null) return;

        Vector2 stick = a.ReadValue<Vector2>();
        if (Mathf.Abs(stick.y) <= scrollDeadzone) { h.ScrollAccum = 0f; return; }

        h.ScrollAccum += stick.y * scrollSpeed * Time.deltaTime;
        if (Mathf.Abs(h.ScrollAccum) < 1f) return;

        // Truncate toward zero; Mathf has no Truncate and Floor would
        // over-scroll on negatives.
        float ticks = Mathf.Sign(h.ScrollAccum) * Mathf.Floor(Mathf.Abs(h.ScrollAccum));
        h.ScrollAccum -= ticks;
        _browser.SendScroll(h.Pixel.x, h.Pixel.y, 0f, -ticks);
    }

    // -------------------------------------------------------------------------
    //  The one thing XRI cannot answer: where on the page is this interactor?
    // -------------------------------------------------------------------------
    private bool TryGetPixel(IXRInteractor interactor, out Vector2 pixel, out Vector2 uv)
    {
        pixel = default;
        uv = default;

        RaycastHit hit;

        // A ray interactor already did this cast; reuse its result rather than
        // casting a second, subtly different ray.
        if (interactor is XRRayInteractor ray &&
            ray.TryGetCurrent3DRaycastHit(out hit) && hit.collider == _collider)
        {
            return ToPixel(hit, out pixel, out uv);
        }

        // Otherwise cast from wherever the interactor aims from. Near-Far and
        // poke interactors expose a ray origin; anything else falls back to its
        // own transform.
        Transform origin = (interactor as IXRRayProvider)?.GetOrCreateRayOrigin()
                           ?? interactor.transform;

        if (_collider.Raycast(new Ray(origin.position, origin.forward), out hit, maxRayDistance))
            return ToPixel(hit, out pixel, out uv);

        // Poking: the finger can already be at or through the surface, so a
        // forward ray from it misses. Back off along the approach and retry.
        Vector3 pos = interactor.GetAttachTransform(_interactable).position;
        Vector3 closest = _collider.ClosestPoint(pos);
        Vector3 dir = closest - pos;
        if (dir.sqrMagnitude < 1e-8f) dir = origin.forward;
        dir.Normalize();

        if (_collider.Raycast(new Ray(pos - dir * 0.1f, dir), out hit, maxRayDistance))
            return ToPixel(hit, out pixel, out uv);

        return false;
    }

    private bool ToPixel(RaycastHit hit, out Vector2 pixel, out Vector2 uv)
    {
        uv = hit.textureCoord;

        // Mirror exactly what the material does when it samples, so the texel
        // under the pointer is the texel you can see.
        float u = _browser.flipHorizontally ? 1f - uv.x : uv.x;
        float v = _browser.flipVertically   ? 1f - uv.y : uv.y;

        float px = u * _browser.SurfaceWidth;
        float py = (1f - v) * _browser.SurfaceHeight;   // texture V up, page Y down
        if (invertPointerX) px = _browser.SurfaceWidth  - px;
        if (invertPointerY) py = _browser.SurfaceHeight - py;

        pixel = new Vector2(
            Mathf.Clamp(px, 0f, _browser.SurfaceWidth  - 1f),
            Mathf.Clamp(py, 0f, _browser.SurfaceHeight - 1f));
        return true;
    }

    /// <summary>
    /// True while the given hand is on the browser plane. Kept for
    /// GeckoCinemaLocomotion, which suppresses that hand's turn/move input while
    /// it is mid-interaction with the page - without this, aiming at the page and
    /// pushing the same stick to scroll would also turn or walk.
    /// </summary>
    public bool IsHandOnPage(bool rightHand) => (rightHand ? _right : _left).OnPage;
}
