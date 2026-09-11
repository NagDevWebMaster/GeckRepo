// =============================================================================
//  PlayerManager.cs
//
//  Owns the player's state across the cinema: which seat (if any) they're in,
//  and moving the XR rig for teleport pads and seat selection alike.
//
//  Why direct rig repositioning instead of XRI's TeleportationProvider
//  ---------------------------------------------------------------------
//  GeckoCinemaLocomotion already repositions the rig transform directly for
//  its own floor-lock/room-clamp behaviour - this follows the same,
//  already-proven convention rather than introducing a second locomotion
//  mechanism (XRI's TeleportationProvider needs a LocomotionMediator wired up
//  correctly to do anything, and this project doesn't have one set up; get it
//  wrong and it fails silently). A future pass can swap this for a real
//  TeleportationProvider without anything else in the cinema system changing,
//  since everything else only ever calls PlayerManager.Teleport().
// =============================================================================

using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Samples.StarterAssets;

public class PlayerManager : MonoBehaviour
{
    public static PlayerManager Instance { get; private set; }

    [Tooltip("Root of the XR rig this manager moves - drag your XR Origin here.")]
    public Transform xrRig;

    public Seat CurrentSeat { get; private set; }
    public bool IsSeated => CurrentSeat != null;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[Cinema] More than one PlayerManager in the scene - " +
                             "keeping the first, destroying this one.");
            Destroy(this);
            return;
        }
        Instance = this;
    }

    /// <summary>Moves the rig to a world position/rotation. Yaw only - an XR
    /// rig should never pitch or roll, the headset already owns that.</summary>
    public void Teleport(Vector3 position, Quaternion rotation)
    {
        if (xrRig == null)
        {
            Debug.LogWarning("[Cinema] PlayerManager.Teleport: no xrRig assigned.");
            return;
        }
        float yaw = rotation.eulerAngles.y;
        xrRig.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));
    }

    /// <summary>Called by SeatManager once a seat is reserved - moves the
    /// player there and hands standing locomotion off for the duration.</summary>
    public void SitAt(Seat seat)
    {
        CurrentSeat = seat;
        if (seat.SitAnchor != null) Teleport(seat.SitAnchor.position, seat.SitAnchor.rotation);
        SetStandingLocomotionEnabled(false);
    }

    /// <summary>Leaves the current seat (if any) and restores standing movement.</summary>
    public void StandUp()
    {
        if (CurrentSeat != null)
        {
            CurrentSeat.SetOccupied(false);
            CurrentSeat = null;
        }
        SetStandingLocomotionEnabled(true);
    }

    /// <summary>
    /// Enables/disables this scene's real standing-locomotion component
    /// (the XR Interaction Toolkit Starter Assets' DynamicMoveProvider on
    /// the XR Origin) rather than GeckoCinemaLocomotion - HomeTheaterScene
    /// does not use the CinemaManager/BrowserPlaneAdapter/GeckoCinemaLocomotion
    /// pipeline that method targeted (that pipeline belongs to a different,
    /// disabled prototype scene - see PlayerManager.cs's own class header).
    /// Resolved once and cached, same pattern as
    /// CinemaNavigationManager.ResolveBrowserPlane().
    /// </summary>
    private DynamicMoveProvider _moveProvider;

    private DynamicMoveProvider ResolveMoveProvider()
    {
        if (_moveProvider != null) return _moveProvider;
        _moveProvider = FindAnyObjectByType<DynamicMoveProvider>();
        return _moveProvider;
    }

    private void SetStandingLocomotionEnabled(bool isEnabled)
    {
        var provider = ResolveMoveProvider();
        if (provider != null) provider.enabled = isEnabled;
    }
}
