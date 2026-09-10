// =============================================================================
//  GeckoXRPress.cs
//
//  Shared "is this hand's trigger down" state for the browser plane and its
//  buttons.
//
//  Why this exists: XRI's Select action is the GRIP on a Quest controller
//  (<XRController>{Hand}/{GripButton}), not the trigger. Pressing a web page or
//  a key is a UI click, and XRI's action for that is UI Press, which IS the
//  trigger (<XRController>{Hand}/{TriggerButton}) - the same action
//  XRUIInputModule uses to click a canvas with a ray. So hover, targeting and
//  laser visuals come from XRI as normal, but the click edge is read here.
//
//  GeckoXRInteraction publishes the actions; GeckoXRButton reads them, so both
//  the page and the keyboard click off the same physical button.
// =============================================================================

using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

public static class GeckoXRPress
{
    public static InputAction Left;
    public static InputAction Right;

    /// <summary>True while that hand's trigger is past its press point.</summary>
    public static bool IsPressed(InteractorHandedness hand)
    {
        InputAction a = hand == InteractorHandedness.Left  ? Left
                      : hand == InteractorHandedness.Right ? Right
                      : null;
        return a != null && a.IsPressed();
    }

    public static bool IsAvailable => Left != null || Right != null;

    /// <summary>
    /// True while this interactor's ray is on a world-space UI canvas.
    ///
    /// A Canvas has no collider, so a physics ray aimed at the VR menu sails
    /// straight through it and lands on the browser plane a few metres behind.
    /// Without this check one trigger pull would press a menu button AND tap the
    /// web page underneath. UI wins: it is the thing the player can see under
    /// the cursor. XRI has no shared interface for this, so the two interactor
    /// types that can raycast UI are asked directly.
    /// </summary>
    public static bool IsOverUI(IXRInteractor interactor)
    {
        switch (interactor)
        {
            case NearFarInteractor nearFar: return nearFar.TryGetCurrentUIRaycastResult(out _);
            case XRRayInteractor ray:       return ray.TryGetCurrentUIRaycastResult(out _);
            default:                        return false;
        }
    }
}
