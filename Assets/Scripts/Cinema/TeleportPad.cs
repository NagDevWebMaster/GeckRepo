// =============================================================================
//  TeleportPad.cs
//
//  A clickable floor marker for moving around the cinema: lobby <-> theater
//  navigation, or a plain walk-to-here point. Three independent uses - set
//  enterTheater for a theater's entrance, exitsToLobby for a "back to lobby"
//  pad inside a theater, or leave both alone for a plain move.
// =============================================================================

using UnityEngine;

[RequireComponent(typeof(GeckoUIButton))]
[RequireComponent(typeof(CinemaClickable))]
public class TeleportPad : MonoBehaviour
{
    [Tooltip("Where the player lands. Empty = this transform's own position/rotation. " +
             "Ignored when exitsToLobby is on - CinemaManager's own lobby spawn wins.")]
    public Transform destination;

    [Tooltip("Assign to make this pad a theater entrance - it deactivates whichever " +
             "theater is currently active and activates this one instead of just " +
             "moving the player.")]
    public TheaterManager enterTheater;

    [Tooltip("Makes this the 'back to lobby' pad inside a theater: deactivates the " +
             "active theater, frees the player's seat, and returns them to " +
             "CinemaManager's lobby spawn point. Overrides enterTheater/destination.")]
    public bool exitsToLobby = false;

    private void Awake()
    {
        if (destination == null) destination = transform;
        GetComponent<GeckoUIButton>().onClick = OnClicked;
    }

    private void OnClicked()
    {
        if (exitsToLobby)
        {
            if (CinemaManager.Instance != null) CinemaManager.Instance.ExitToLobby();
            return;
        }

        if (enterTheater != null && CinemaManager.Instance != null)
        {
            CinemaManager.Instance.EnterTheater(enterTheater);
        }

        if (PlayerManager.Instance != null)
            PlayerManager.Instance.Teleport(destination.position, destination.rotation);
    }
}
