// =============================================================================
//  BrowserPlaneAdapter.cs
//
//  The ONLY script the cinema system talks to on a BrowserPlane instance -
//  MovieScreen never touches GeckoVulkanRenderer, AndroidJavaObject or
//  anything WebView-specific directly. That stays entirely inside
//  GeckoVulkanRenderer, exactly as it already was; this just sequences its
//  existing public lifecycle (BeginInitialise/ShutdownBridge/LoadUrl) for
//  "this screen is the one the player is watching now" / "it isn't".
// =============================================================================

using System.Collections;
using UnityEngine;

[RequireComponent(typeof(GeckoVulkanRenderer))]
public class BrowserPlaneAdapter : MonoBehaviour
{
    [Tooltip("Give up and log a warning if the bridge never reports interactive " +
             "within this many seconds of activation.")]
    public float readyTimeoutSeconds = 20f;

    public bool IsActive { get; private set; }

    private GeckoVulkanRenderer _renderer;

    private void Awake() => _renderer = GetComponent<GeckoVulkanRenderer>();

    /// <summary>Brings this BrowserPlane's native bridge up (idempotent) and
    /// loads <paramref name="url"/> once it reports interactive.</summary>
    public void Activate(string url)
    {
        IsActive = true;
        _renderer.BeginInitialise();
        StopAllCoroutines();
        StartCoroutine(LoadWhenReady(url));
    }

    private IEnumerator LoadWhenReady(string url)
    {
        float waited = 0f;
        while (!_renderer.IsInteractive && waited < readyTimeoutSeconds)
        {
            waited += Time.deltaTime;
            yield return null;
        }

        if (_renderer.IsInteractive) _renderer.LoadUrl(url);
        else Debug.LogWarning("[Cinema] BrowserPlaneAdapter: bridge on " + name +
                              " never became interactive for " + url);
    }

    /// <summary>Tears the native bridge down without destroying this instance -
    /// it can be Activate()'d again later for the same or a different theater.</summary>
    public void Deactivate()
    {
        IsActive = false;
        StopAllCoroutines();
        _renderer.ShutdownBridge();
    }
}
