// =============================================================================
//  CinemaClickable.cs
//
//  Marks a GeckoUIButton as belonging to the cinema system (seats, teleport
//  pads, theater-entry markers) rather than to a BrowserPlane's own chrome
//  (the on-screen keyboard, browser toolbar). CinemaPointerInput only reacts
//  to buttons carrying this marker - without it, a theater's own
//  GeckoPointerInput (while that theater is active) and the always-on
//  CinemaPointerInput would both try to drive the same button and double-fire
//  its click.
// =============================================================================

using UnityEngine;

[RequireComponent(typeof(GeckoUIButton))]
public class CinemaClickable : MonoBehaviour
{
}
