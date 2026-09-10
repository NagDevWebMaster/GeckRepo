// =============================================================================
//  CinemaPointerInput.cs
//
//  Always-on two-hand ray -> GeckoUIButton hover/click for the cinema system:
//  seats, teleport pads, theater-entry markers. Everything that ISN'T the web
//  page itself.
//
//  Why this exists instead of reusing GeckoPointerInput
//  ------------------------------------------------------
//  GeckoPointerInput is [RequireComponent(GeckoVulkanRenderer)] - it only ever
//  exists on a BrowserPlane, and its whole Update() is gated on that plane's
//  native bridge being interactive (Update() returns immediately otherwise).
//  Each theater carries its own BrowserPlane instance, and only the ACTIVE
//  theater's is ever initialised - so in the lobby, or in any theater that
//  isn't the current one, there is no live GeckoPointerInput to click a seat
//  or a teleport pad with. This component has no such dependency: it runs all
//  the time, on one place in the scene (see PlayerManager), driven by the same
//  ray-interactor transforms GeckoPointerInput itself now reads from.
//
//  Deliberately does not draw its own laser
//  -----------------------------------------
//  The assigned XR Ray/Near-Far Interactor already renders one, and the active
//  theater's GeckoPointerInput draws a second while that theater is live.
//  Adding a third here would triple it for no reason - hover/press feedback on
//  the button itself (GeckoUIButton's own colour states) is enough.
// =============================================================================

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

public class CinemaPointerInput : MonoBehaviour
{
    [Header("Ray source (assign the XR rig's Near-Far / Ray Interactor per hand)")]
    public Transform rightRayOrigin;
    public Transform leftRayOrigin;

    [Header("Ray")]
    public float maxRayDistance = 100f;
    [Range(0.1f, 0.9f)] public float triggerThreshold = 0.5f;

    [Header("Debug")]
    public bool verboseLogging = false;

    private const string Tag = "[Cinema] ";

    private class Hand
    {
        public readonly XRNode Node;
        public InputDevice Device;
        public bool DeviceValid;
        public GeckoUIButton Hovered;
        public GeckoUIButton Pressed;
        public Hand(XRNode node) { Node = node; }
    }

    private readonly Hand _right = new Hand(XRNode.RightHand);
    private readonly Hand _left = new Hand(XRNode.LeftHand);
    private static readonly List<InputDevice> s_Devices = new List<InputDevice>();

    private void Update()
    {
        UpdateHand(_right, rightRayOrigin);
        UpdateHand(_left, leftRayOrigin);
    }

    private void UpdateHand(Hand h, Transform rayOrigin)
    {
        if (rayOrigin == null || !TryGetDevice(h))
        {
            ClearHover(h);
            return;
        }

        Vector3 origin = rayOrigin.position;
        Vector3 dir = rayOrigin.forward;

        GeckoUIButton hovered = null;
        if (Physics.Raycast(origin, dir, out RaycastHit hit, maxRayDistance) &&
            hit.collider.GetComponent<CinemaClickable>() != null)
        {
            hovered = hit.collider.GetComponent<GeckoUIButton>();
        }

        if (h.Hovered != null && h.Hovered != hovered) h.Hovered.SetHovered(false);
        h.Hovered = hovered;
        if (h.Hovered != null) h.Hovered.SetHovered(true);

        bool hasBool = h.Device.TryGetFeatureValue(CommonUsages.triggerButton, out bool tBool);
        bool hasAxis = h.Device.TryGetFeatureValue(CommonUsages.trigger, out float tAxis);
        bool trigger = (hasBool && tBool) || (hasAxis && tAxis >= triggerThreshold);

        // Same convention as GeckoPointerInput's UI buttons: press arms it,
        // release fires it ONLY if still hovering the button that was pressed -
        // sliding off between press and release cancels instead of clicking
        // whatever the ray happened to land on at release.
        if (trigger && h.Pressed == null && hovered != null)
        {
            h.Pressed = hovered;
            h.Pressed.SetPressed(true);
        }
        else if (!trigger && h.Pressed != null)
        {
            h.Pressed.SetPressed(false);
            if (h.Pressed == hovered)
            {
                h.Pressed.Activate();
                if (verboseLogging) Debug.Log(Tag + "clicked " + h.Pressed.name);
            }
            h.Pressed = null;
        }
    }

    private void ClearHover(Hand h)
    {
        if (h.Hovered != null) h.Hovered.SetHovered(false);
        if (h.Pressed != null) { h.Pressed.SetPressed(false); h.Pressed = null; }
        h.Hovered = null;
    }

    private bool TryGetDevice(Hand h)
    {
        if (h.DeviceValid && h.Device.isValid) return true;
        InputDevices.GetDevicesAtXRNode(h.Node, s_Devices);
        if (s_Devices.Count > 0)
        {
            h.Device = s_Devices[0];
            h.DeviceValid = h.Device.isValid;
        }
        else h.DeviceValid = false;
        return h.DeviceValid;
    }
}
