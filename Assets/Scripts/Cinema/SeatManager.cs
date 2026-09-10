// =============================================================================
//  SeatManager.cs
//
//  Tracks every Seat in one theater and arbitrates selection: only one seat
//  can be occupied by the (single) player at a time, so sitting in a new one
//  frees whichever one they were in.
// =============================================================================

using System.Collections.Generic;
using UnityEngine;

public class SeatManager : MonoBehaviour
{
    private readonly List<Seat> _seats = new List<Seat>();

    public IReadOnlyList<Seat> Seats => _seats;

    public void Register(Seat seat)
    {
        if (!_seats.Contains(seat)) _seats.Add(seat);
    }

    public void RequestSit(Seat seat)
    {
        if (seat.IsOccupied || PlayerManager.Instance == null) return;

        var current = PlayerManager.Instance.CurrentSeat;
        if (current != null) current.SetOccupied(false);

        seat.SetOccupied(true);
        PlayerManager.Instance.SitAt(seat);
    }

    /// <summary>Frees every seat - called when the theater is deactivated so a
    /// stale occupied seat doesn't greet the next visitor.</summary>
    public void ReleaseAll()
    {
        foreach (var s in _seats) s.SetOccupied(false);
    }
}
