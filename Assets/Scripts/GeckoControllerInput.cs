// =============================================================================
//  GeckoControllerInput.cs
//
//  Complete Quest 3 (Touch Plus) controller map: every button the runtime exposes
//  to an application, on both hands, with edge detection and an Inspector-assignable
//  action per button.
//
//  Put this on the SAME GameObject as GeckoVulkanRenderer (the BrowserPlane),
//  alongside GeckoPointerInput.
//
//  Why the buttons did nothing before
//  ----------------------------------
//  Nothing was broken in the XR setup - OpenXR has the Oculus Touch Controller
//  Profile enabled for Android, so both controllers are found and their poses and
//  triggers work. The buttons simply had no code behind them: across the whole
//  project the only inputs ever read were
//      CommonUsages.trigger / triggerButton   (GeckoPointerInput - page clicks)
//      CommonUsages.primary2DAxis             (scroll, and move/turn in locomotion)
//      CommonUsages.devicePosition / Rotation (the ray)
//  A, B, X, Y, both grips, both stick clicks and Menu were never polled at all, so
//  pressing them could not do anything. This adds them.
//
//  What Quest 3 actually exposes
//  -----------------------------
//    Right: A = primaryButton   B = secondaryButton   trigger, grip,
//           thumbstick + click, and the touch sensors on A/B/stick.
//    Left:  X = primaryButton   Y = secondaryButton   menuButton, and the same
//           trigger/grip/stick set.
//
//  Two are deliberately absent because no application can read them: the right
//  controller's Meta/Oculus button and a long press of the left Menu button are
//  both reserved by the system runtime. If you need "open my own menu", bind it to
//  a short Menu press (the default here) or to a face button - nothing you write
//  will ever see the Meta button.
//
//  Why polling UnityEngine.XR and not Input Actions
//  ------------------------------------------------
//  Same reasoning GeckoCinemaLocomotion's header sets out: the rest of this project
//  reads InputDevices.GetDevicesWithCharacteristics directly, that path is already
//  proven on-device here, and it needs no .inputactions asset to be wired correctly
//  (the scene's InputActionManager has an empty action-asset list, so an Input
//  System binding would silently never fire).
// =============================================================================

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using VRCinema;

[RequireComponent(typeof(GeckoVulkanRenderer))]
public class GeckoControllerInput : MonoBehaviour
{
    // -------------------------------------------------------------------------
    /// <summary>Every button this app can see on a Quest 3 controller.</summary>
    public enum Button
    {
        Primary,        // A (right) / X (left)
        Secondary,      // B (right) / Y (left)
        Trigger,
        Grip,
        StickClick,
        Menu            // left controller only on Quest
    }

    /// <summary>What a button does when pressed.</summary>
    public enum Action
    {
        None,
        Back,
        Forward,
        Reload,
        Home,
        ToggleKeyboard,
        Recenter,
        ScrollPageUp,
        ScrollPageDown,
        ToggleLaser,
        ToggleMenu      // VRMenuController.ToggleMenu() - see `menu` field below
    }

    [System.Serializable]
    public class HandMap
    {
        public Action primary   = Action.None;
        public Action secondary = Action.None;
        public Action grip      = Action.None;
        public Action stickClick = Action.None;
        public Action menu      = Action.None;

        [Tooltip("The trigger is owned by GeckoPointerInput for page clicks. Set " +
                 "anything other than None only if you want a SECOND job on top of " +
                 "clicking - it will fire on every page press too.")]
        public Action trigger = Action.None;
    }

    [Header("Right controller (A / B)")]
    public HandMap right = new HandMap
    {
        primary    = Action.Back,       // A
        secondary  = Action.Forward,    // B
        grip       = Action.None,
        stickClick = Action.ToggleMenu,
        menu       = Action.None,       // system-reserved on the right hand
        trigger    = Action.None
    };

    [Header("Left controller (X / Y)")]
    public HandMap left = new HandMap
    {
        primary    = Action.Reload,     // X
        secondary  = Action.Home,       // Y
        grip       = Action.ToggleLaser,
        stickClick = Action.Recenter,
        menu       = Action.ToggleKeyboard,
        trigger    = Action.None
    };

    [Header("Analogue thresholds")]
    [Tooltip("Trigger/grip pull past this counts as a press, for controllers that " +
             "report an axis but no boolean.")]
    [Range(0.1f, 0.9f)] public float pressThreshold = 0.5f;

    [Header("Scrolling")]
    [Tooltip("Wheel ticks sent for a ScrollPageUp/Down action.")]
    public float pageScrollTicks = 12f;

    [Header("Menu")]
    [Tooltip("Opened/closed by Action.ToggleMenu. Uses this same raw-polled input path " +
             "rather than XRI's Input System actions - the project's own Input System " +
             "bindings have proven unreliable on-device, this one hasn't. Leave empty to " +
             "auto-find the one VRMenuController in the scene.")]
    [SerializeField] private VRMenuController menu;

    [Header("Debug")]
    [Tooltip("Logs every press and release on both hands. Turn this on first if a " +
             "button seems dead - if nothing appears, the controller is not being " +
             "found at all and the problem is the XR setup, not the mapping.")]
    public bool logButtonPresses = true;

    [Tooltip("On startup, log which controllers were found and which features each " +
             "one actually reports.")]
    public bool logDeviceCapabilities = true;

    private const string Tag = "[GeckoVulkanBridge] ";

    // -------------------------------------------------------------------------
    private class HandState
    {
        public readonly bool IsRight;
        public readonly XRNode Node;
        public InputDevice Device;
        public bool DeviceValid;
        public bool Logged;

        // index by (int)Button
        public readonly bool[] Down = new bool[6];
        public readonly bool[] Prev = new bool[6];

        public float Trigger, Grip;
        public Vector2 Stick;

        public HandState(bool isRight)
        {
            IsRight = isRight;
            Node = isRight ? XRNode.RightHand : XRNode.LeftHand;
        }
    }

    private GeckoVulkanRenderer _browser;
    private GeckoBrowserUI _ui;               // optional
    private GeckoPointerInput _pointer;       // optional
    private CinemaScreenAligner _aligner;     // optional

    private HandState _right, _left;
    private static readonly List<InputDevice> s_Devices = new List<InputDevice>();

    // -------------------------------------------------------------------------
    private void Awake()
    {
        _browser = GetComponent<GeckoVulkanRenderer>();
        _ui      = GetComponent<GeckoBrowserUI>();
        _pointer = GetComponent<GeckoPointerInput>();
        _aligner = GetComponent<CinemaScreenAligner>();
        if (menu == null) menu = FindAnyObjectByType<VRMenuController>();

        _right = new HandState(true);
        _left  = new HandState(false);
    }

    private void Update()
    {
        Poll(_right);
        Poll(_left);

        Dispatch(_right, right);
        Dispatch(_left,  left);
    }

    // -------------------------------------------------------------------------
    private void Poll(HandState h)
    {
        System.Array.Copy(h.Down, h.Prev, h.Down.Length);

        if (!Resolve(h))
        {
            for (int i = 0; i < h.Down.Length; i++) h.Down[i] = false;
            h.Trigger = h.Grip = 0f;
            h.Stick = Vector2.zero;
            return;
        }

        if (logDeviceCapabilities && !h.Logged) { LogCapabilities(h); h.Logged = true; }

        h.Down[(int)Button.Primary]    = ReadBool(h, CommonUsages.primaryButton);
        h.Down[(int)Button.Secondary]  = ReadBool(h, CommonUsages.secondaryButton);
        h.Down[(int)Button.StickClick] = ReadBool(h, CommonUsages.primary2DAxisClick);
        h.Down[(int)Button.Menu]       = ReadBool(h, CommonUsages.menuButton);

        // Trigger and grip: prefer the boolean, fall back to the axis. Some runtimes
        // report only one of the two.
        h.Trigger = ReadFloat(h, CommonUsages.trigger);
        h.Grip    = ReadFloat(h, CommonUsages.grip);
        h.Down[(int)Button.Trigger] = ReadBool(h, CommonUsages.triggerButton) || h.Trigger >= pressThreshold;
        h.Down[(int)Button.Grip]    = ReadBool(h, CommonUsages.gripButton)    || h.Grip    >= pressThreshold;

        h.Stick = h.Device.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 s) ? s : Vector2.zero;
    }

    private bool ReadBool(HandState h, InputFeatureUsage<bool> usage)
        => h.Device.TryGetFeatureValue(usage, out bool v) && v;

    private float ReadFloat(HandState h, InputFeatureUsage<float> usage)
        => h.Device.TryGetFeatureValue(usage, out float v) ? v : 0f;

    /// <summary>Same device-resolution pattern as GeckoPointerInput.TryGetHandDevice.</summary>
    private bool Resolve(HandState h)
    {
        var wanted = InputDeviceCharacteristics.HeldInHand |
                     InputDeviceCharacteristics.Controller |
                     (h.IsRight ? InputDeviceCharacteristics.Right
                                : InputDeviceCharacteristics.Left);

        InputDevices.GetDevicesWithCharacteristics(wanted, s_Devices);
        if (s_Devices.Count > 0 && s_Devices[0].isValid)
        {
            h.Device = s_Devices[0]; h.DeviceValid = true; return true;
        }

        InputDevices.GetDevicesAtXRNode(h.Node, s_Devices);
        if (s_Devices.Count > 0 && s_Devices[0].isValid)
        {
            h.Device = s_Devices[0]; h.DeviceValid = true; return true;
        }

        h.DeviceValid = false;
        return false;
    }

    private void Dispatch(HandState h, HandMap map)
    {
        Fire(h, map.primary,    Button.Primary);
        Fire(h, map.secondary,  Button.Secondary);
        Fire(h, map.grip,       Button.Grip);
        Fire(h, map.stickClick, Button.StickClick);
        Fire(h, map.menu,       Button.Menu);
        Fire(h, map.trigger,    Button.Trigger);
    }

    private void Fire(HandState h, Action action, Button b)
    {
        bool now = h.Down[(int)b], was = h.Prev[(int)b];
        if (now == was) return;

        if (logButtonPresses)
            Debug.Log(Tag + $"input: {(h.IsRight ? "R" : "L")}.{Label(h.IsRight, b)} " +
                      $"{(now ? "DOWN" : "UP")}" +
                      (action == Action.None ? " (unmapped)" : $" -> {action}"));

        if (now && action != Action.None) Run(action);
    }

    /// <summary>Quest's physical labels, so the log reads like the hardware.</summary>
    private static string Label(bool right, Button b)
    {
        switch (b)
        {
            case Button.Primary:   return right ? "A" : "X";
            case Button.Secondary: return right ? "B" : "Y";
            default:               return b.ToString();
        }
    }

    private void Run(Action a)
    {
        switch (a)
        {
            case Action.Back:
                if (_browser.CanGoBack()) _browser.GoBack();
                else Debug.Log(Tag + "input: Back ignored - no history.");
                break;

            case Action.Forward:
                if (_browser.CanGoForward()) _browser.GoForward();
                else Debug.Log(Tag + "input: Forward ignored - no history.");
                break;

            case Action.Reload:  _browser.Reload(); break;

            case Action.Home:
                if (_ui != null) _ui.GoHome();
                else _browser.LoadUrl(_browser.url);
                break;

            case Action.ToggleKeyboard:
                if (_ui != null) _ui.ToggleKeyboard();
                else Debug.LogWarning(Tag + "input: no GeckoBrowserUI - nothing to toggle.");
                break;

            case Action.Recenter:
                if (_aligner != null) _aligner.RecenterRig();
                else Debug.LogWarning(Tag + "input: no CinemaScreenAligner - cannot recentre.");
                break;

            // Scroll at the middle of the page: a face button has no pointer position
            // of its own, and Gecko needs the wheel event to land somewhere scrollable.
            case Action.ScrollPageUp:
                _browser.SendScroll(_browser.SurfaceWidth * 0.5f, _browser.SurfaceHeight * 0.5f,
                                    0f, -pageScrollTicks);
                break;

            case Action.ScrollPageDown:
                _browser.SendScroll(_browser.SurfaceWidth * 0.5f, _browser.SurfaceHeight * 0.5f,
                                    0f, pageScrollTicks);
                break;

            case Action.ToggleLaser:
                if (_pointer != null) _pointer.drawPointer = !_pointer.drawPointer;
                break;

            case Action.ToggleMenu:
                if (menu != null) menu.ToggleMenu();
                else Debug.LogWarning(Tag + "input: ToggleMenu has no VRMenuController - nothing to open.");
                break;
        }
    }

    // -------------------------------------------------------------------------
    /// <summary>
    /// Reports what the runtime says this controller can do. The usual cause of a
    /// genuinely dead button is that the device resolves to a HAND (hand tracking),
    /// which has poses but no buttons - that shows up here as a device name with no
    /// button features listed.
    /// </summary>
    private void LogCapabilities(HandState h)
    {
        var features = new List<InputFeatureUsage>();
        h.Device.TryGetFeatureUsages(features);

        var names = new List<string>();
        foreach (var f in features) names.Add(f.name);

        Debug.Log(Tag + $"input: {(h.IsRight ? "right" : "left")} device '{h.Device.name}' " +
                  $"chars={h.Device.characteristics} features=[{string.Join(", ", names)}]");
    }

    // -------------------------------------------------------------------------
    // Public query API, for anything that wants raw buttons rather than actions.
    // -------------------------------------------------------------------------
    public bool GetButton(bool rightHand, Button b)     => H(rightHand).Down[(int)b];
    public bool GetButtonDown(bool rightHand, Button b) { var h = H(rightHand); return h.Down[(int)b] && !h.Prev[(int)b]; }
    public bool GetButtonUp(bool rightHand, Button b)   { var h = H(rightHand); return !h.Down[(int)b] && h.Prev[(int)b]; }
    public float GetTrigger(bool rightHand)             => H(rightHand).Trigger;
    public float GetGrip(bool rightHand)                => H(rightHand).Grip;
    public Vector2 GetStick(bool rightHand)             => H(rightHand).Stick;
    public bool IsConnected(bool rightHand)             => H(rightHand).DeviceValid;

    private HandState H(bool rightHand) => rightHand ? _right : _left;
}
