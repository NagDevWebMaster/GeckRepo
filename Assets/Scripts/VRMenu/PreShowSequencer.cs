// =============================================================================
//  PreShowSequencer.cs
//
//  Plays a brief "house lights down, screen comes to life" beat whenever the
//  player enters a theater with a screen (see CinemaNavigationManager, which
//  calls Play() from MoveBrowser() - it already knows which destinations
//  have a screen via their browserAnchor being non-null, and Lobby has none).
//
//  Also exposes a manual ToggleBlackout(), wired to a "Lights" button in the
//  popup menu, that reuses the same baseline data to black the room out and
//  bring it back to the movie-watching level on demand.
// =============================================================================

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace VRCinema
{
    public class PreShowSequencer : MonoBehaviour
    {
        [Header("Timing")]
        [Tooltip("Lights dim to this fraction of their authored intensity.")]
        [Range(0f, 1f)] public float dimFactor = 0.35f;

        [Tooltip("Seconds for the light dim.")]
        public float lightsDuration = 1.2f;

        [Tooltip("Seconds for the screen fade-in, after the lights finish dimming.")]
        public float fadeDuration = 1.0f;

        // Authored intensity per light, captured once on first contact - never
        // re-captured, so a light already dimmed from a prior visit doesn't
        // become the new "baseline" on a later Play() call.
        private readonly Dictionary<Light, float> _baseline = new Dictionary<Light, float>();

        private Coroutine _running;
        private Transform _activeTheaterRoot;
        private Renderer _activeScreenRenderer;
        private bool _isBlackedOut;

        // ---------------------------------------------------------------------
        /// <summary>Dims theaterRoot's lights, then fades screenRenderer's
        /// material in, then resumes any paused page media.</summary>
        public void Play(Transform theaterRoot, Renderer screenRenderer)
        {
            if (theaterRoot == null || screenRenderer == null) return;

            if (_running != null) StopCoroutine(_running);

            _activeTheaterRoot = theaterRoot;
            _activeScreenRenderer = screenRenderer;
            _isBlackedOut = false;

            Light[] lights = theaterRoot.GetComponentsInChildren<Light>(true);
            foreach (Light light in lights)
            {
                if (!_baseline.ContainsKey(light)) _baseline[light] = light.intensity;
                light.intensity = _baseline[light]; // snap to baseline before dimming
            }

            Color c = screenRenderer.material.color;
            c.a = 0f;
            screenRenderer.material.color = c;

            _running = StartCoroutine(PlayRoutine(lights, screenRenderer));
        }

        private IEnumerator PlayRoutine(Light[] lights, Renderer screenRenderer)
        {
            float elapsed = 0f;
            while (elapsed < lightsDuration)
            {
                elapsed += Time.unscaledDeltaTime;
                float k = Mathf.Clamp01(elapsed / lightsDuration);
                foreach (Light light in lights)
                    light.intensity = Mathf.Lerp(_baseline[light], _baseline[light] * dimFactor, k);
                yield return null;
            }
            foreach (Light light in lights) light.intensity = _baseline[light] * dimFactor;

            elapsed = 0f;
            Material mat = screenRenderer.material;
            while (elapsed < fadeDuration)
            {
                elapsed += Time.unscaledDeltaTime;
                float k = Mathf.Clamp01(elapsed / fadeDuration);
                Color c = mat.color;
                c.a = k;
                mat.color = c;
                yield return null;
            }
            Color full = mat.color;
            full.a = 1f;
            mat.color = full;

            var vulkanRenderer = screenRenderer.GetComponent<GeckoVulkanRenderer>();
            if (vulkanRenderer != null) vulkanRenderer.ResumeMedia();

            _running = null;
        }

        // ---------------------------------------------------------------------
        /// <summary>Blacks out (or restores) the active theater's lights on
        /// demand. Acts on whichever theater the last Play() call targeted;
        /// a no-op if Play() has never run (e.g. still in the Lobby).</summary>
        public void ToggleBlackout()
        {
            if (_activeTheaterRoot == null) return;
            if (_running != null)
            {
                StopCoroutine(_running);
                _running = null;

                if (_activeScreenRenderer != null)
                {
                    Color c = _activeScreenRenderer.material.color;
                    c.a = 1f;
                    _activeScreenRenderer.material.color = c;

                    var vulkanRenderer = _activeScreenRenderer.GetComponent<GeckoVulkanRenderer>();
                    if (vulkanRenderer != null) vulkanRenderer.ResumeMedia();
                }
            }

            _isBlackedOut = !_isBlackedOut;
            Light[] lights = _activeTheaterRoot.GetComponentsInChildren<Light>(true);
            foreach (Light light in lights)
            {
                if (!_baseline.ContainsKey(light)) _baseline[light] = light.intensity;
                light.intensity = _isBlackedOut ? 0f : _baseline[light] * dimFactor;
            }
        }
    }
}
