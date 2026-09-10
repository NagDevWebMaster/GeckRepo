// =============================================================================
//  GeckoXRButton.cs
//
//  Makes one GeckoUIButton an XR Interaction Toolkit interactable.
//
//  GeckoUIButton is a bare quad on a BoxCollider with hover / pressed colour
//  states and an onClick. It was driven by GeckoPointerInput, which raycast for
//  it by hand. With the rig converted to XRI nothing raycasts for it any more,
//  so the toolbar and the on-screen keyboard would be dead. This bridges the
//  two: XRI decides hover, GeckoUIButton keeps its colours and its onClick, and
//  neither one had to learn about the other.
//
//  The click comes from GeckoXRPress (the trigger), not from XRI select, which
//  is the grip - a key you have to squeeze rather than pull is not a key.
//
//  Added automatically by GeckoXRInteraction - both to buttons that already
//  exist and to every one GeckoBrowserUI / GeckoPageKeyboard creates later.
// =============================================================================

using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

[RequireComponent(typeof(GeckoUIButton))]
[DisallowMultipleComponent]
public class GeckoXRButton : MonoBehaviour
{
    private GeckoUIButton _button;
    private XRSimpleInteractable _interactable;

    // The hand whose trigger owns this press, and whether it is still armed.
    private InteractorHandedness _pressingHand = InteractorHandedness.None;
    private bool _held;

    private void Awake()
    {
        _button = GetComponent<GeckoUIButton>();

        _interactable = GetComponent<XRSimpleInteractable>();
        if (_interactable == null) _interactable = gameObject.AddComponent<XRSimpleInteractable>();
    }

    private void OnEnable()
    {
        _interactable.firstHoverEntered.AddListener(OnFirstHoverEntered);
        _interactable.lastHoverExited.AddListener(OnLastHoverExited);
    }

    private void OnDisable()
    {
        _interactable.firstHoverEntered.RemoveListener(OnFirstHoverEntered);
        _interactable.lastHoverExited.RemoveListener(OnLastHoverExited);

        _button.SetHovered(false);
        _button.SetPressed(false);
        _held = false;
        _pressingHand = InteractorHandedness.None;
    }

    private void OnFirstHoverEntered(HoverEnterEventArgs args) => _button.SetHovered(true);

    private void OnLastHoverExited(HoverExitEventArgs args)
    {
        _button.SetHovered(false);
        // Sliding off before release cancels the press, same as before XRI.
        if (_held) Cancel();
    }

    private void Update()
    {
        if (!GeckoXRPress.IsAvailable) return;

        if (!_held)
        {
            if (!_interactable.isHovered || !_button.Interactable) return;

            // Take the first hovering hand whose trigger is down. Ignore a
            // trigger that was already held when the ray arrived, or dragging a
            // held trigger across the keyboard would type every key it crosses.
            var hovering = _interactable.interactorsHovering;
            for (int i = 0; i < hovering.Count; i++)
            {
                var hand = hovering[i].handedness;
                if (hand == InteractorHandedness.None) continue;
                if (GeckoXRPress.IsOverUI(hovering[i])) continue;   // the menu has this hand
                if (!WasPressedThisFrame(hand)) continue;

                _pressingHand = hand;
                _held = true;
                _button.SetPressed(true);
                return;
            }
            return;
        }

        // Bringing the menu up mid-press cancels rather than types.
        var holding = _interactable.interactorsHovering;
        for (int i = 0; i < holding.Count; i++)
        {
            if (holding[i].handedness == _pressingHand && GeckoXRPress.IsOverUI(holding[i]))
            {
                Cancel();
                return;
            }
        }

        if (GeckoXRPress.IsPressed(_pressingHand)) return;

        // Released. Fire only if the ray is still on this button.
        bool onButton = _interactable.isHovered;
        _held = false;
        _pressingHand = InteractorHandedness.None;
        _button.SetPressed(false);
        if (onButton) _button.Activate();
    }

    private static bool WasPressedThisFrame(InteractorHandedness hand)
    {
        var a = hand == InteractorHandedness.Left ? GeckoXRPress.Left : GeckoXRPress.Right;
        return a != null && a.WasPressedThisFrame();
    }

    private void Cancel()
    {
        _held = false;
        _pressingHand = InteractorHandedness.None;
        _button.SetPressed(false);
    }
}
