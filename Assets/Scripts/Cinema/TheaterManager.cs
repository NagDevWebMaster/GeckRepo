// =============================================================================
//  TheaterManager.cs
//
//  One theater: its seats, its screen, its own auditorium root. Doesn't know
//  anything about the WebView/AAR - that's entirely behind MovieScreen.
// =============================================================================

using UnityEngine;

public class TheaterManager : MonoBehaviour
{
    [Header("Config")]
    public string displayName = "Theater";
    public string movieUrl = "https://www.youtube.com";

    [Header("Wired by TheaterBuilder")]
    public MovieScreen screen;
    public SeatManager seats;
    public Transform auditoriumRoot;

    public bool IsActive { get; private set; }

    /// <summary>Brings this theater's screen up and marks it the one the player
    /// is in. Called by CinemaManager, which is the single place deciding
    /// which theater (if any) is allowed to be active.</summary>
    public void Activate()
    {
        IsActive = true;
        if (screen == null)
        {
            Debug.LogError("[Cinema] " + displayName + " has no MovieScreen - nothing to play.");
            return;
        }
        screen.auditoriumRoot = auditoriumRoot;
        screen.Play(movieUrl);
    }

    /// <summary>Tears this theater's screen down and frees its seats.</summary>
    public void Deactivate()
    {
        IsActive = false;
        if (seats != null) seats.ReleaseAll();
        if (screen != null) screen.Stop();
    }
}
