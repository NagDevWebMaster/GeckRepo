// =============================================================================
//  MovieScreen.cs
//
//  Lives on a theater and knows how to host the existing BrowserPlane - the
//  cinema system talks to THIS, never to GeckoVulkanRenderer or the WebView
//  bridge directly. Also re-triggers the BrowserPlane's own (otherwise
//  dormant) auditorium-alignment and room-bounds/floor logic against this
//  theater's built geometry, instead of the cinema system reimplementing
//  screen placement or floor-locking itself.
// =============================================================================

using System.Collections;
using UnityEngine;

public class MovieScreen : MonoBehaviour
{
    [Tooltip("The BrowserPlane instance for this theater - assigned by TheaterBuilder.")]
    public BrowserPlaneAdapter adapter;
    public BrowserPlaneAdapter Adapter => adapter;

    [Tooltip("This theater's auditorium root - the same node CinemaScreenAligner and " +
             "GeckoCinemaLocomotion expect, built by TheaterBuilder (a 'ProjectionScreen' " +
             "child, seat nodes, a floor). Reused rather than reimplemented.")]
    public Transform auditoriumRoot;

    /// <summary>Activates this screen: brings the bridge up, loads the movie, and
    /// re-aligns/re-initialises this BrowserPlane's dormant auditorium logic
    /// against THIS theater's geometry.</summary>
    public void Play(string url)
    {
        if (adapter == null)
        {
            Debug.LogError("[Cinema] MovieScreen on " + name + " has no BrowserPlane adapter.");
            return;
        }

        adapter.Activate(url);

        var aligner = adapter.GetComponent<CinemaScreenAligner>();
        if (aligner != null && auditoriumRoot != null) aligner.Realign(auditoriumRoot);

        var locomotion = adapter.GetComponent<GeckoCinemaLocomotion>();
        if (locomotion != null)
        {
            locomotion.enabled = true;
            if (auditoriumRoot != null) locomotion.Reinitialize(auditoriumRoot);
        }

        // CinemaScreenAligner repositions/rescales/rotates the plane itself once
        // alignment finishes - anything that built its OWN geometry once at
        // Awake() from the plane's pre-alignment transform (the on-screen
        // keyboard's floating dialog, the curved-screen mesh) is now stale
        // relative to the plane's real, post-alignment pose and needs telling.
        if (aligner != null)
        {
            StopAllCoroutines();
            StartCoroutine(RefreshDependentGeometryWhenAligned(aligner));
        }
    }

    private IEnumerator RefreshDependentGeometryWhenAligned(CinemaScreenAligner aligner)
    {
        float waited = 0f;
        while (!aligner.IsAligned && waited < 15f)
        {
            waited += Time.deltaTime;
            yield return null;
        }
        if (!aligner.IsAligned)
        {
            Debug.LogWarning("[Cinema] MovieScreen on " + name + ": alignment never " +
                             "completed - keyboard/curved-screen geometry left unrefreshed.");
            yield break;
        }

        var curver = adapter.GetComponent<GeckoScreenCurver>();
        if (curver != null) curver.Rebuild();

        var keyboard = adapter.GetComponent<GeckoPageKeyboard>();
        if (keyboard != null) keyboard.RebuildLayout();
    }

    /// <summary>Deactivates this screen: tears the bridge down and disables the
    /// locomotion this theater was driving.</summary>
    public void Stop()
    {
        if (adapter == null) return;
        adapter.Deactivate();

        var locomotion = adapter.GetComponent<GeckoCinemaLocomotion>();
        if (locomotion != null) locomotion.enabled = false;
    }
}
