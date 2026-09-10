// =============================================================================
//  Seat.cs
//
//  One theater seat: a clickable marker plus the SitAnchor the player is
//  teleported to and faced from when they select it. Built by TheaterBuilder;
//  clicked via CinemaPointerInput (this is a GeckoUIButton like everything
//  else clickable in this project, marked with CinemaClickable so the
//  cinema-wide pointer - not a BrowserPlane's own - is the one driving it).
// =============================================================================

using UnityEngine;

[RequireComponent(typeof(GeckoUIButton))]
[RequireComponent(typeof(CinemaClickable))]
public class Seat : MonoBehaviour
{
    public Transform SitAnchor { get; private set; }
    public bool IsOccupied { get; private set; }

    private GeckoUIButton _button;
    private SeatManager _manager;

    private static readonly Color kFree  = new Color(0.15f, 0.45f, 0.20f, 1f);
    private static readonly Color kHover = new Color(0.20f, 0.60f, 0.28f, 1f);
    private static readonly Color kPress = new Color(0.10f, 0.75f, 0.35f, 1f);
    private static readonly Color kTaken = new Color(0.40f, 0.13f, 0.13f, 1f);

    /// <summary>Wires this seat to its manager and anchor. Called once by
    /// TheaterBuilder right after the seat is built.</summary>
    public void Init(SeatManager manager, Transform sitAnchor)
    {
        _manager = manager;
        SitAnchor = sitAnchor;
        _button = GetComponent<GeckoUIButton>();
        _button.onClick = OnClicked;
        Refresh();
    }

    private void OnClicked()
    {
        if (IsOccupied || _manager == null) return;
        _manager.RequestSit(this);
    }

    public void SetOccupied(bool occupied)
    {
        IsOccupied = occupied;
        if (_button != null) _button.Interactable = !occupied;
        Refresh();
    }

    private void Refresh()
    {
        if (_button == null) return;
        _button.SetColors(IsOccupied ? kTaken : kFree, kHover, kPress);
    }
}
