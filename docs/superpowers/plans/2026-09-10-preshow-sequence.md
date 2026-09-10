# Pre-Show Sequence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Dim theater lights and fade the browser screen in whenever the player enters a theater, pause/auto-resume page media across theater switches, and add a manual "Lights" toggle to the popup menu.

**Architecture:** One new `PreShowSequencer` MonoBehaviour owns light-baseline caching and the dim/fade coroutine; `CinemaNavigationManager` calls into it from the same two methods (`GoToObject`, `MoveBrowser`) that already own theater-switch logic; two new thin JS-injection wrapper methods on `GeckoVulkanRenderer` handle media pause/resume, reusing the existing `SendJavaScript()` bridge.

**Tech Stack:** Unity 6000.5.5f1, C#, URP (Universal Render Pipeline), Unity UI (uGUI/TextMeshPro), Unity Editor scripting via the project's `unity-mcp-cli` bridge (`script-execute`, `gameobject-*`, `scene-save` tools) for all verification and scene wiring — see the note below.

**Spec:** `docs/superpowers/specs/2026-09-10-preshow-sequence-design.md`

## Testing Note (read before starting)

This project has **no automated test framework** — no `.asmdef` test assembly, no EditMode/PlayMode tests anywhere in `Assets/Scripts`. Introducing one is out of scope for this feature (YAGNI, and it would be an unapproved architecture change). Every other visual/behavioral change in this codebase to date has been verified by:

1. Editing via Unity Editor scripting (the `script-execute` MCP tool), which compiles and runs real C# against the live scene.
2. Asserting on reachable end-states via `Debug.Log` + reading the console back (`console-get-logs`).
3. For visual/animated results, a `screenshot-camera` capture reviewed directly.

This plan follows that same pattern. One added wrinkle specific to this feature: this dev environment's headless Editor scripting does not reliably tick real frames (`Time.frameCount` was observed stuck at 1 across multiple `script-execute` calls in earlier work on this project), so a coroutine's frame-by-frame lerp cannot be driven synchronously from a script. Each task below verifies the coroutine's **reachable end-states** (baseline captured correctly, alpha snapped to 0 at start, final intensity/alpha values correct) rather than the smoothness of the animation itself — the animation's real-time behavior is a normal build-and-test-on-device concern, same as the MSAA and rounded-corner work earlier in this project.

**Critical lesson from this project's history:** a `script-execute` write to a live GameObject can look successful (reads back correctly in the same session) but silently fail to persist if the scene isn't explicitly saved with `EditorUtility.SetDirty` + `EditorSceneManager.MarkSceneDirty` + `EditorSceneManager.SaveScene`, and a later domain reload or Editor build can wipe the un-persisted change. Every scene-mutating step in this plan ends with that exact three-call save sequence, and every task's final verification step **reopens the scene from disk** (`EditorSceneManager.OpenScene(path, OpenSceneMode.Single)`) before checking state, to catch exactly this failure mode.

## Global Constraints

- `dimFactor = 0.35`, `lightsDuration = 1.2` (seconds), `fadeDuration = 1.0` (seconds) — from the spec, serialized fields on `PreShowSequencer`, tunable in the Inspector but these are the shipped defaults.
- The pre-show sequence, media pause, and media resume are gated exactly as the spec states: pause fires for every `Destination` including Lobby; the dim/fade/resume sequence only fires when `d.browserAnchor != null` (Lobby is excluded because it has none).
- Screen alpha is animated via `renderer.material` (per-instance copy), never `renderer.sharedMaterial` — mutating the shared asset at runtime would permanently edit `BrowserMatUnlit.mat` on disk.
- `ToggleBlackout()` restores to `baseline * dimFactor` (the movie-watching level), never to full baseline brightness.
- The "Lights" menu button does **not** call `VRMenuController.CloseMenu()` on press, unlike every other menu button.

---

## File Structure

- **Create** `Assets/Scripts/VRMenu/PreShowSequencer.cs` — new MonoBehaviour, namespace `VRCinema` (matches the other files in this folder). Owns light-baseline caching, the dim/fade/resume coroutine, and the blackout toggle.
- **Modify** `Assets/Scripts/GeckoVulkanRenderer.cs` — add `PauseMedia()` / `ResumeMedia()` public methods near the existing `SendJavaScript()` (around line 463).
- **Modify** `Assets/Scripts/VRMenu/CinemaNavigationManager.cs` — add a `preShowSequencer` field, one call in `GoToObject()` (line ~154), one call in `MoveBrowser()` (line ~190).
- **Modify (asset)** `Assets/Prefabs/BrowserMatUnlit.mat` — Surface Type Opaque → Transparent.
- **Modify (scene)** `Assets/Scenes/HomeTheaterScene.unity` — add a `PreShowSequencer` component to the `Cinema Navigation` GameObject, wire `CinemaNavigationManager.preShowSequencer` to it, add a new "Lights" button to the popup menu.

---

### Task 1: `GeckoVulkanRenderer` media pause/resume

**Files:**
- Modify: `Assets/Scripts/GeckoVulkanRenderer.cs:463-468` (immediately after the existing `SendJavaScript` method)

**Interfaces:**
- Consumes: existing `public void SendJavaScript(string js)` (already defined at line 463 of this file).
- Produces: `public void PauseMedia()`, `public void ResumeMedia()` — both called later by `PreShowSequencer` (Task 3) and `CinemaNavigationManager` (Task 4).

- [ ] **Step 1: Add the two methods**

Open `Assets/Scripts/GeckoVulkanRenderer.cs` and insert immediately after the closing brace of `SendJavaScript` (after line 468):

```csharp

    /// <summary>Pauses every &lt;video&gt;/&lt;audio&gt; element on the page. Fire-and-forget -
    /// the plugin has no callback channel back into Unity, so this cannot know
    /// whether anything was actually playing. A no-op when the page has no
    /// media elements.</summary>
    public void PauseMedia()
    {
        SendJavaScript(
            "document.querySelectorAll('video,audio').forEach(" +
            "function(m){try{m.pause();}catch(e){}});");
    }

    /// <summary>Resumes every &lt;video&gt;/&lt;audio&gt; element on the page. Same
    /// fire-and-forget caveat as PauseMedia().</summary>
    public void ResumeMedia()
    {
        SendJavaScript(
            "document.querySelectorAll('video,audio').forEach(" +
            "function(m){try{m.play();}catch(e){}});");
    }
```

- [ ] **Step 2: Refresh and compile**

Run via the `unity-mcp-cli` bridge:

```bash
unity-mcp-cli run-tool assets-refresh --input '{}'
```

Then poll `editor-application-get-state` until `IsCompiling: false`, and check `console-get-logs` with `{"level":"error","count":5}` for compile errors. Expected: no errors mentioning `GeckoVulkanRenderer.cs`.

- [ ] **Step 3: Verify by direct invocation**

Run this body-only script via `script-execute` (this only checks the methods exist, compile, and call `SendJavaScript` without throwing — it cannot verify actual page behavior, since there is no callback channel, matching the spec's documented limitation):

```csharp
var browserGo = GameObject.Find("BrowserPlane");
var renderer = browserGo.GetComponent<GeckoVulkanRenderer>();
renderer.PauseMedia();
renderer.ResumeMedia();
Debug.Log("[TASK1] PauseMedia/ResumeMedia called without throwing");
```

Expected console output: `[TASK1] PauseMedia/ResumeMedia called without throwing`, plus two `[GeckoVulkanBridge] js: ...` log lines (from `SendJavaScript`'s own `verboseLogging`) showing the exact `querySelectorAll` payloads for pause and resume respectively.

- [ ] **Step 4: Commit**

```bash
git add Assets/Scripts/GeckoVulkanRenderer.cs
git commit -m "Add PauseMedia/ResumeMedia to GeckoVulkanRenderer

Thin wrappers around the existing SendJavaScript() javascript: URI
bridge - no new native plugin surface needed."
```

---

### Task 2: `BrowserMatUnlit` surface type change

**Files:**
- Modify (asset): `Assets/Prefabs/BrowserMatUnlit.mat`

**Interfaces:**
- Consumes: nothing.
- Produces: a material whose alpha channel affects rendering, which Task 3's screen-fade coroutine depends on.

- [ ] **Step 1: Change the surface type via script**

Run via `script-execute`:

```csharp
var mat = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>("Assets/Prefabs/BrowserMatUnlit.mat");
Debug.Log("[TASK2] before renderQueue=" + mat.renderQueue);

mat.SetFloat("_Surface", 1f);   // 0 = Opaque, 1 = Transparent (URP convention)
mat.SetFloat("_Blend", 0f);     // 0 = Alpha blend
mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
mat.SetFloat("_ZWrite", 0f);
mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
mat.SetOverrideTag("RenderType", "Transparent");
mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

UnityEditor.EditorUtility.SetDirty(mat);
UnityEditor.AssetDatabase.SaveAssets();
Debug.Log("[TASK2] after renderQueue=" + mat.renderQueue + " surface=" + mat.GetFloat("_Surface"));
```

Expected: `before renderQueue=2000` (Opaque's queue value), `after renderQueue=3000 surface=1`.

- [ ] **Step 2: Verify on disk**

```bash
grep -n "_Surface\|_ZWrite" /Users/nag/GeckViewProject/Assets/Prefabs/BrowserMatUnlit.mat
```

Expected: a `_Surface` float entry with value `1`, matching the script's change (this is a plain asset file — `AssetDatabase.SaveAssets()` writes immediately and reliably, unlike scene state, so no reload-verify is needed here).

- [ ] **Step 3: Visual sanity check**

The plane should still render exactly as before (fully opaque, since its material's actual `_BaseColor`/tint alpha is still 1 at this point — only the *capability* to fade has been added, nothing fades yet until Task 3 wires it up). Take a screenshot via `screenshot-camera` of either theater's screen and confirm no visual regression (no transparency, no z-fighting, no missing texture).

- [ ] **Step 4: Commit**

```bash
git add Assets/Prefabs/BrowserMatUnlit.mat
git commit -m "Switch BrowserMatUnlit to Transparent surface type

One-time asset edit so the material's alpha channel affects rendering,
needed for the upcoming screen fade-in. GeckoVulkanRenderer only ever
touches mainTexture/mainTextureScale/mainTextureOffset, so it is
unaffected by this change."
```

---

### Task 3: `PreShowSequencer` — light dim + screen fade

**Files:**
- Create: `Assets/Scripts/VRMenu/PreShowSequencer.cs`

**Interfaces:**
- Consumes: `GeckoVulkanRenderer.ResumeMedia()` (Task 1), `Light.intensity` (Unity API), `Renderer.material.color` (Unity API).
- Produces: `public void Play(Transform theaterRoot, Renderer screenRenderer)` and `public void ToggleBlackout()` — both called by `CinemaNavigationManager` (Task 4) and the new menu button (Task 6). `ToggleBlackout()` is written in this task even though it is only wired to a button in Task 6, since both methods share the same baseline-cache state and coroutine, and splitting them across two files-worth of behavior would leave `Play()` unable to be reviewed for correctness on the state it shares with `ToggleBlackout()`.

- [ ] **Step 1: Write the script**

Create `Assets/Scripts/VRMenu/PreShowSequencer.cs`:

```csharp
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
        private bool _isBlackedOut;

        // ---------------------------------------------------------------------
        /// <summary>Dims theaterRoot's lights, then fades screenRenderer's
        /// material in, then resumes any paused page media.</summary>
        public void Play(Transform theaterRoot, Renderer screenRenderer)
        {
            if (theaterRoot == null || screenRenderer == null) return;

            if (_running != null) StopCoroutine(_running);

            _activeTheaterRoot = theaterRoot;
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
            if (_running != null) { StopCoroutine(_running); _running = null; }

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
```

- [ ] **Step 2: Refresh and compile**

```bash
unity-mcp-cli run-tool assets-refresh --input '{}'
```

Poll until `IsCompiling: false`; check `console-get-logs` for errors mentioning `PreShowSequencer.cs`. Expected: none.

- [ ] **Step 3: Verify baseline capture and blackout math with a throwaway test object**

Run via `script-execute` (creates and destroys its own temp objects — does not touch the real scene):

```csharp
var root = new GameObject("__TestTheaterRoot");
var lightGo = new GameObject("__TestLight");
lightGo.transform.SetParent(root.transform);
var light = lightGo.AddComponent<Light>();
light.intensity = 2.0f;

var screenGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
screenGo.name = "__TestScreen";
var renderer = screenGo.GetComponent<Renderer>();
renderer.material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));

var seqGo = new GameObject("__TestSequencer");
var seq = seqGo.AddComponent<VRCinema.PreShowSequencer>();

seq.Play(root.transform, renderer);
Debug.Log("[TASK3] after Play() call: alpha=" + renderer.material.color.a + " (expect 0, before any coroutine frame runs)");

// ToggleBlackout reuses Play()'s baseline even though the coroutine never
// gets to run a frame in this headless environment (see Testing Note) -
// call it directly to check the math without needing real frame ticks.
var seq2Go = new GameObject("__TestSequencer2");
var seq2 = seq2Go.AddComponent<VRCinema.PreShowSequencer>();
light.intensity = 2.0f; // reset, since seq's own coroutine never ran to move it
seq2.Play(root.transform, renderer);
seq2.ToggleBlackout();
Debug.Log("[TASK3] after first ToggleBlackout: light.intensity=" + light.intensity + " (expect 0)");
seq2.ToggleBlackout();
Debug.Log("[TASK3] after second ToggleBlackout: light.intensity=" + light.intensity + " (expect 0.7 = 2.0 * 0.35)");

UnityEngine.Object.DestroyImmediate(root);
UnityEngine.Object.DestroyImmediate(screenGo);
UnityEngine.Object.DestroyImmediate(seqGo);
UnityEngine.Object.DestroyImmediate(seq2Go);
Debug.Log("[TASK3] cleanup done");
```

Expected console output (via `console-get-logs`, filter for `[TASK3]`):
```
[TASK3] after Play() call: alpha=0 (expect 0, before any coroutine frame runs)
[TASK3] after first ToggleBlackout: light.intensity=0 (expect 0)
[TASK3] after second ToggleBlackout: light.intensity=0.7 (expect 0.7 = 2.0 * 0.35)
[TASK3] cleanup done
```

If the second line doesn't read exactly `0`, or the third doesn't read exactly `0.7`, the baseline dictionary or the blackout math has a bug — check that `_baseline[light]` was captured as `2.0` (the value set before `Play()`) and not some other value.

- [ ] **Step 4: Commit**

```bash
git add Assets/Scripts/VRMenu/PreShowSequencer.cs
git commit -m "Add PreShowSequencer: light dim + screen fade-in + blackout toggle

New MonoBehaviour, not yet wired into the scene. Caches each light's
authored intensity once on first contact so repeated visits always
dim from the true baseline, never from a residual dimmed value left
by Unity preserving field values across GameObject deactivate/
reactivate. ToggleBlackout() shares that same baseline data for a
manual on-demand lights-off/lights-back-to-dimmed toggle."
```

---

### Task 4: Wire `PreShowSequencer` into `CinemaNavigationManager` and the scene

**Files:**
- Modify: `Assets/Scripts/VRMenu/CinemaNavigationManager.cs:79-90` (new field), `:154-181` (`GoToObject`), `:190-207` (`MoveBrowser`)
- Modify (scene): `Assets/Scenes/HomeTheaterScene.unity` — add `PreShowSequencer` to the `Cinema Navigation` GameObject, assign the new field.

**Interfaces:**
- Consumes: `PreShowSequencer.Play(Transform, Renderer)` (Task 3), `GeckoVulkanRenderer.PauseMedia()` (Task 1), existing `ResolveBrowserPlane()` (this file).
- Produces: end-to-end behavior — `LoadTheater1()`/`LoadTheater2()` now trigger the full pause → move → dim → fade → resume sequence.

- [ ] **Step 1: Add the field**

In `Assets/Scripts/VRMenu/CinemaNavigationManager.cs`, immediately after the `browserPlane` field (after line 86, before the `activateOnStart` field):

```csharp

        [SerializeField, Tooltip("Runs the light-dim + screen-fade beat when arriving at a " +
                                 "theater. Leave empty to look one up in the scene.")]
        private PreShowSequencer preShowSequencer;
```

- [ ] **Step 2: Add the pause call to `GoToObject`**

Change the start of `GoToObject` (currently line 154-161):

```csharp
        private bool GoToObject(Destination d)
        {
            if (d.root == null)
            {
                Debug.LogError($"CinemaNavigationManager: {d.label} has no Root object " +
                               "assigned, so there is nothing to activate.", this);
                return false;
            }
```

to:

```csharp
        private bool GoToObject(Destination d)
        {
            if (d.root == null)
            {
                Debug.LogError($"CinemaNavigationManager: {d.label} has no Root object " +
                               "assigned, so there is nothing to activate.", this);
                return false;
            }

            // Pausing is about leaving whatever screen was showing, not about
            // where we're headed - fires for every destination, including
            // Lobby, unlike the dim/fade/resume sequence below.
            Transform currentPlane = ResolveBrowserPlane();
            if (currentPlane != null)
            {
                var currentRenderer = currentPlane.GetComponent<GeckoVulkanRenderer>();
                if (currentRenderer != null) currentRenderer.PauseMedia();
            }
```

- [ ] **Step 3: Add the sequencer call to `MoveBrowser`**

Change `MoveBrowser` (currently lines 190-207) from:

```csharp
        private void MoveBrowser(Destination d)
        {
            if (d.browserAnchor == null) return;

            Transform plane = ResolveBrowserPlane();
            if (plane == null)
            {
                Debug.LogWarning($"CinemaNavigationManager: {d.label} has a browser anchor " +
                                 "but no browser plane was found to move onto it.", this);
                return;
            }

            plane.SetPositionAndRotation(d.browserAnchor.position, d.browserAnchor.rotation);
            plane.localScale = d.browserAnchor.localScale;

            var curver = plane.GetComponent<GeckoScreenCurver>();
            if (curver != null) curver.Rebuild();
        }
```

to:

```csharp
        private void MoveBrowser(Destination d)
        {
            if (d.browserAnchor == null) return;

            Transform plane = ResolveBrowserPlane();
            if (plane == null)
            {
                Debug.LogWarning($"CinemaNavigationManager: {d.label} has a browser anchor " +
                                 "but no browser plane was found to move onto it.", this);
                return;
            }

            plane.SetPositionAndRotation(d.browserAnchor.position, d.browserAnchor.rotation);
            plane.localScale = d.browserAnchor.localScale;

            var curver = plane.GetComponent<GeckoScreenCurver>();
            if (curver != null) curver.Rebuild();

            if (preShowSequencer != null)
                preShowSequencer.Play(d.root.transform, plane.GetComponent<Renderer>());
        }
```

- [ ] **Step 4: Refresh and compile**

```bash
unity-mcp-cli run-tool assets-refresh --input '{}'
```

Poll until `IsCompiling: false`; check for errors mentioning `CinemaNavigationManager.cs`. Expected: none.

- [ ] **Step 5: Add the component to the scene and wire the field**

Run via `script-execute`:

```csharp
var navGo = GameObject.Find("Cinema Navigation");
var seq = navGo.GetComponent<VRCinema.PreShowSequencer>();
if (seq == null) seq = navGo.AddComponent<VRCinema.PreShowSequencer>();

var nav = navGo.GetComponent<VRCinema.CinemaNavigationManager>();
var field = typeof(VRCinema.CinemaNavigationManager)
    .GetField("preShowSequencer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
field.SetValue(nav, seq);

UnityEditor.EditorUtility.SetDirty(navGo);
UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(navGo.scene);
bool saved = UnityEditor.SceneManagement.EditorSceneManager.SaveScene(navGo.scene);
Debug.Log("[TASK4] sequencer added=" + (seq != null) + " field wired=" + (field.GetValue(nav) == seq) + " saved=" + saved);
```

Expected: `[TASK4] sequencer added=True field wired=True saved=True`.

- [ ] **Step 6: Reload-verify persistence**

Run via `script-execute` (reopens the scene from disk — see Testing Note above for why this step exists):

```csharp
var path = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path;
UnityEditor.SceneManagement.EditorSceneManager.OpenScene(path, UnityEditor.SceneManagement.OpenSceneMode.Single);

var navGo = GameObject.Find("Cinema Navigation");
var seq = navGo.GetComponent<VRCinema.PreShowSequencer>();
var nav = navGo.GetComponent<VRCinema.CinemaNavigationManager>();
var field = typeof(VRCinema.CinemaNavigationManager)
    .GetField("preShowSequencer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
var wired = field.GetValue(nav);

Debug.Log("[TASK4VERIFY] sequencer persisted=" + (seq != null) + " fieldPersisted=" + (wired == seq));

int missing = 0;
foreach (var c in navGo.GetComponentsInChildren<Component>(true)) if (c == null) missing++;
Debug.Log("[TASK4VERIFY] missingScripts=" + missing);
```

Expected: `[TASK4VERIFY] sequencer persisted=True fieldPersisted=True` and `missingScripts=0`.

- [ ] **Step 7: End-to-end functional check**

Enter Play mode (`editor-application-set-state` with `{"IsPlaying": true}`, wait for `IsPlaying: true` via polling, then unpause if `IsPaused: true`). Run via `script-execute`:

```csharp
var nav = GameObject.Find("Cinema Navigation").GetComponent<VRCinema.CinemaNavigationManager>();
nav.LoadTheater1();

var plane = GameObject.Find("BrowserPlane");
var mat = plane.GetComponent<Renderer>().material;
Debug.Log("[TASK4E2E] immediately after LoadTheater1: screen alpha=" + mat.color.a + " (expect 0)");

var home = GameObject.Find("Auditorium");
Light sampleLight = home != null ? home.GetComponentInChildren<Light>(true) : null;
Debug.Log("[TASK4E2E] sample light found=" + (sampleLight != null));
```

Expected: `screen alpha=0` immediately after switching (the fade hasn't run any frames yet, matching the Testing Note's known limitation in this environment) and a sample light is found under the Home Theater root. Then exit Play mode (`{"IsPlaying": false}`) — Play mode changes are never saved, so no scene-save step follows this check.

- [ ] **Step 8: Commit**

```bash
git add Assets/Scripts/VRMenu/CinemaNavigationManager.cs Assets/Scenes/HomeTheaterScene.unity
git commit -m "Wire PreShowSequencer into CinemaNavigationManager

GoToObject() pauses whatever the browser plane was showing before
touching anything else, for every destination including Lobby.
MoveBrowser() hands off to PreShowSequencer.Play() instead of leaving
the plane at full alpha immediately - gated by the same
browserAnchor-null check that already excludes Lobby from screen
logic."
```

---

### Task 5: "Lights" menu button

**Files:**
- Modify (scene): `Assets/Scenes/HomeTheaterScene.unity` — new button in the popup menu (`VR Menu Anchor/VR Menu`).

**Interfaces:**
- Consumes: `PreShowSequencer.ToggleBlackout()` (Task 3).
- Produces: a player-facing control; no new code interfaces for later tasks.

- [ ] **Step 1: Create and wire the button**

Run via `script-execute` (clones the existing `CloseButton` for its `VRMenuButton` + `Button` component structure, matching the same pattern used for every other menu button added to this scene so far):

```csharp
var menuAnchor = GameObject.Find("VR Menu Anchor");
var menuRoot = menuAnchor.transform.Find("VR Menu");
var navGo = GameObject.Find("Cinema Navigation");
var seq = navGo.GetComponent<VRCinema.PreShowSequencer>();

var existing = menuRoot.Find("LightsButton");
if (existing != null) UnityEngine.Object.DestroyImmediate(existing.gameObject);

// CloseButton, not KeyboardButton: KeyboardButton's own Label was cleared
// to an empty string when its icon image was added (the visible word
// "Keyboard" lives in a separate sibling caption object instead) - cloning
// it would produce a blank button. CloseButton still holds real text in
// its own Label ("X") and has no icon child to strip out, which matches
// Lights (no icon asset) - so its Label can just hold the word directly,
// no separate caption object needed.
var source = menuRoot.Find("CloseButton").gameObject;
var clone = UnityEngine.Object.Instantiate(source, menuRoot, false);
clone.name = "LightsButton";

// Reuse the same 80x80 dock-icon footprint as Keyboard/Close, placed to
// their left so all three sit in one row.
var rt = clone.GetComponent<RectTransform>();
rt.anchoredPosition = new Vector2(-165f, -190f);
rt.sizeDelta = new Vector2(80f, 80f);

var label = clone.transform.Find("Label").GetComponent<TMPro.TextMeshProUGUI>();
label.text = "Lights";
label.fontSize = 16;

var button = clone.GetComponent<UnityEngine.UI.Button>();
while (button.onClick.GetPersistentEventCount() > 0)
    UnityEditor.Events.UnityEventTools.RemovePersistentListener(button.onClick, 0);
UnityEditor.Events.UnityEventTools.AddPersistentListener(button.onClick, seq.ToggleBlackout);
// Deliberately no CloseMenu() listener here, unlike every other menu
// button - see the spec's "Manual lights toggle" section for why.

UnityEditor.EditorUtility.SetDirty(clone);
UnityEditor.EditorUtility.SetDirty(menuRoot.gameObject);
UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(menuAnchor.scene);
bool saved = UnityEditor.SceneManagement.EditorSceneManager.SaveScene(menuAnchor.scene);
Debug.Log("[TASK5] created=" + (clone != null) + " persistentCount=" + button.onClick.GetPersistentEventCount() + " saved=" + saved);
```

Expected: `[TASK5] created=True persistentCount=1 saved=True`.

- [ ] **Step 2: Reload-verify persistence**

```csharp
var path = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path;
UnityEditor.SceneManagement.EditorSceneManager.OpenScene(path, UnityEditor.SceneManagement.OpenSceneMode.Single);

var menuAnchor = GameObject.Find("VR Menu Anchor");
var menuRoot = menuAnchor.transform.Find("VR Menu");
var lightsBtnGo = menuRoot.Find("LightsButton");
Debug.Log("[TASK5VERIFY] found=" + (lightsBtnGo != null));

if (lightsBtnGo != null)
{
    var label = lightsBtnGo.Find("Label").GetComponent<TMPro.TextMeshProUGUI>();
    var button = lightsBtnGo.GetComponent<UnityEngine.UI.Button>();
    Debug.Log("[TASK5VERIFY] label=" + label.text + " persistentCount=" + button.onClick.GetPersistentEventCount());
    Debug.Log("[TASK5VERIFY] listener target=" + button.onClick.GetPersistentTarget(0) + " method=" + button.onClick.GetPersistentMethodName(0));
}

int missing = 0;
foreach (var c in menuAnchor.GetComponentsInChildren<Component>(true)) if (c == null) missing++;
Debug.Log("[TASK5VERIFY] missingScripts=" + missing);
```

Expected:
```
[TASK5VERIFY] found=True
[TASK5VERIFY] label=Lights persistentCount=1
[TASK5VERIFY] listener target=Cinema Navigation (VRCinema.PreShowSequencer) method=ToggleBlackout
[TASK5VERIFY] missingScripts=0
```

- [ ] **Step 3: Visual check**

Open the menu (activate `menuRoot`, set `builtScale`, `PlaceInFrontOfPlayer()` — same temp-camera screenshot technique used throughout this project's menu work) and take a `screenshot-camera` capture. Confirm the "Lights" button sits cleanly in the dock row alongside Keyboard and Close, with no overlap.

- [ ] **Step 4: Functional check in Play mode**

Enter Play mode. Run via `script-execute`:

```csharp
var nav = GameObject.Find("Cinema Navigation").GetComponent<VRCinema.CinemaNavigationManager>();
nav.LoadTheater1();

var seq = GameObject.Find("Cinema Navigation").GetComponent<VRCinema.PreShowSequencer>();
seq.ToggleBlackout();

var home = GameObject.Find("Auditorium");
var sampleLight = home.GetComponentInChildren<Light>(true);
Debug.Log("[TASK5E2E] after first toggle, light.intensity=" + sampleLight.intensity + " (expect 0)");

seq.ToggleBlackout();
Debug.Log("[TASK5E2E] after second toggle, light.intensity=" + sampleLight.intensity + " (expect baseline * 0.35)");
```

Expected: first line reads `0`; second line reads a nonzero value (the specific number depends on that light's authored intensity, but it must not be `0` and must not equal the raw baseline — it should be exactly `0.35` of whatever the light's original intensity was). Exit Play mode afterward (no save needed, Play mode changes don't persist).

- [ ] **Step 5: Commit**

```bash
git add Assets/Scenes/HomeTheaterScene.unity
git commit -m "Add Lights toggle button to the popup menu

Clones the existing dock-icon button pattern (Keyboard/Close),
positioned to their left in the same row. Wired to
PreShowSequencer.ToggleBlackout() only - deliberately no CloseMenu()
listener, since it's a toggle the player may want to flip back
quickly."
```

---

## Self-Review

**Spec coverage:**
- Trigger (browserAnchor-null gating Lobby out) — Task 4, Steps 2-3. ✓
- `PreShowSequencer.Play()` behavior (baseline caching, snap-to-baseline, dim lerp, fade lerp, coroutine restart) — Task 3. ✓
- Alpha via `renderer.material` not `sharedMaterial` — Task 3, `Play()` and `PlayRoutine()` both use `screenRenderer.material`. ✓
- `BrowserMatUnlit` Opaque → Transparent — Task 2. ✓
- `CinemaNavigationManager` wiring (`preShowSequencer` field, `MoveBrowser` call, `GoToObject` pause call) — Task 4. ✓
- `GeckoVulkanRenderer.PauseMedia()`/`ResumeMedia()` — Task 1. ✓
- `ResumeMedia()` called at end of fade — Task 3, `PlayRoutine()` final block. ✓
- Manual lights toggle (`ToggleBlackout()`, `_activeTheaterRoot`, `_isBlackedOut` reset on `Play()`) — Task 3. ✓
- "Lights" menu button, no `CloseMenu()` call — Task 5. ✓

**Placeholder scan:** no "TBD"/"TODO" strings; every code block is complete and runnable; every test step has concrete expected output.

**Type consistency:** `Play(Transform theaterRoot, Renderer screenRenderer)` and `ToggleBlackout()` signatures match between Task 3 (definition) and Tasks 4-5 (call sites). `PauseMedia()`/`ResumeMedia()` take no arguments consistently between Task 1 (definition) and Tasks 3-4 (call sites).
