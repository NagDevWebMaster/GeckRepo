# Pre-show sequence design

Status: approved design, pending implementation plan
Date: 2026-09-10

## Problem

Walking into Home Theater or Luxury Theater currently snaps straight to a
fully lit room with the browser screen already live. Real cinemas have a
brief ritual before the film starts — house lights come down, the screen
comes to life — and the app has none of that. This spec covers approach B
from the immersion brainstorm: dim the room lights, then fade the screen in.

This spec also covers a second, tightly related problem: because there is
only one `BrowserPlane` that moves between theaters, switching theaters
while a video is playing currently leaves it playing invisibly (and
audibly) in the background during the move, then still running once
parked on the new screen at whatever timestamp it reached — not paused,
not restarted, just wallpapered over by the pre-show dim. Switching
theaters should pause playback immediately on switch-out and resume it
once the new screen has faded in.

Explicitly out of scope for this spec (tracked separately):
- Ambient/curtain audio (no audio assets exist yet in the project)
- A curtain 3D asset or wipe-style reveal (approach C, not chosen)
- Seat selection / teleport-to-seat (a separate sub-project, to be
  brainstormed next)

## Trigger

The sequence plays every time the player enters or switches to a theater
that has a screen — i.e. any `CinemaNavigationManager.Destination` whose
`browserAnchor` is non-null. This is the same check `MoveBrowser()` already
uses to decide whether a destination has a screen at all, so Lobby (which
has no `browserAnchor`) is excluded with no new flag.

## Components

### `PreShowSequencer` (new MonoBehaviour)

Lives alongside `CinemaNavigationManager` on the `Cinema Navigation` object.

Public API:

```csharp
public void Play(Transform theaterRoot, Renderer screenRenderer)
```

Behavior:

1. Collects every `Light` under `theaterRoot` via
   `GetComponentsInChildren<Light>(true)`.
2. For each light never seen before, caches its **authored** intensity in a
   `Dictionary<Light, float>` baseline map. Lights already in the map are
   not re-cached — the baseline is captured once, ever, per light.
3. Snaps every light in `theaterRoot` back to its cached baseline
   intensity immediately (undoes any leftover dim from a prior visit —
   Unity does not reset field values when a GameObject is deactivated and
   reactivated, so without this step a second visit would dim from an
   already-dimmed value).
4. Starts (or restarts) a single coroutine that:
   - Lerps every light's intensity from baseline down to `dimFactor`
     (0.35) of baseline over `lightsDuration` (1.2s).
   - Then lerps `screenRenderer`'s material color alpha from 0 to 1 over
     `fadeDuration` (1.0s).
5. If `Play()` is called again before the coroutine finishes (the player
   switched theaters again mid-sequence), the running coroutine is stopped
   and a new one started from the current (mid-dim) state — same
   stop-then-restart pattern already used in `VRMenuController.Animate()`.

The screen renderer's alpha is set to 0 at the *start* of `Play()` (step 3
area), before either lerp begins, so the plane is invisible until its fade
step runs — even though `MoveBrowser()` has already repositioned and
activated it by this point.

Alpha is animated via `renderer.material` (which creates a per-instance
copy on first access), not `renderer.sharedMaterial`. This matters because
`BrowserMatUnlit` is otherwise a shared asset — mutating `sharedMaterial`
at runtime would permanently edit the asset on disk. Using `.material` is
safe here specifically because there is only one `BrowserPlane` instance
in the whole scene (it moves between theaters rather than being
duplicated), so the per-instance copy costs nothing extra and never leaks.

`dimFactor`, `lightsDuration`, and `fadeDuration` are serialized fields
with the defaults above, tunable in the Inspector.

### Material change

`BrowserMatUnlit`'s Surface Type changes from Opaque to Transparent so its
alpha channel affects rendering. This is a one-time asset edit, not a new
material — `GeckoVulkanRenderer` only ever touches `mainTexture` /
`mainTextureScale` / `mainTextureOffset`, so it is unaffected by the
surface type change.

### `CinemaNavigationManager` change

`MoveBrowser(Destination d)` gains one call: after repositioning the plane
and rebuilding the curve (existing behavior, unchanged), it calls
`preShowSequencer.Play(d.root, plane.GetComponent<Renderer>())` instead of
leaving the plane at full alpha immediately. `preShowSequencer` is a new
serialized field, resolved via `FindAnyObjectByType` if left unassigned —
the same optional-reference pattern already used for `browserPlane`.

`GoToObject(Destination d)` gains one call at the very top, before
anything else runs: `ResolveBrowserPlane()?.GetComponent<GeckoVulkanRenderer>()
?.PauseMedia()`. This fires unconditionally, for every destination
including Lobby — pausing is about leaving whatever screen was showing,
not about where you're headed, so it is not gated behind `browserAnchor`
the way the pre-show and resume steps are.

### Media pause/resume (`GeckoVulkanRenderer` change)

Two new public methods, both thin wrappers around the existing
`SendJavaScript()` — the same `javascript:`-URI mechanism
`GeckoPageKeyboard` already uses for text entry, so no new native plugin
surface is needed:

```csharp
public void PauseMedia() =>
    SendJavaScript("document.querySelectorAll('video,audio').forEach(" +
                    "function(m){try{m.pause();}catch(e){}});");

public void ResumeMedia() =>
    SendJavaScript("document.querySelectorAll('video,audio').forEach(" +
                    "function(m){try{m.play();}catch(e){}});");
```

The native plugin has no callback channel back into Unity (see the
existing comment above `SendJavaScript`), so neither method can know
whether anything was actually playing — both are fire-and-forget, and a
no-op when the page has no media elements or nothing was playing. The
per-element `try/catch` guards against a rejected `play()` promise (e.g.
an autoplay-policy block) throwing into the page's own script context.

`PreShowSequencer.Play()` calls `ResumeMedia()` once the screen-alpha
lerp finishes (end of step 4), auto-resuming whatever was on the page
before the switch. This means pause and resume are not symmetric: pause
fires for every destination (including Lobby), resume only fires for
destinations with a screen (Lobby has nothing to resume onto).

### Manual lights toggle (`PreShowSequencer` change + new menu button)

A "Lights" button in the popup menu lets the player black out the current
theater's lights on demand, and bring them back — independent of the
automatic pre-show dim, reusing the same baseline data.

`PreShowSequencer` gains:

```csharp
public void ToggleBlackout()
```

Behavior:

1. Remembers the theater root passed into the most recent `Play()` call
   (a new private field, `_activeTheaterRoot`) — `ToggleBlackout()` acts
   on whichever theater's lights were last touched, so it always targets
   the theater the player is actually standing in. If no `Play()` has run
   yet (e.g. still in the Lobby), it no-ops.
2. If a `Play()` coroutine is still running (rare — the player would have
   to open the menu and hit this before the ~2.2s dim+fade finishes), it
   is stopped first, the same way a second `Play()` call would stop it,
   so the toggle and the automatic sequence never fight over the same
   light's intensity in the same frame.
3. Flips a `_isBlackedOut` bool and sets every light under
   `_activeTheaterRoot` to:
   - `0` when turning lights off, or
   - `baseline[light] * dimFactor` when turning them back on — the same
     movie-watching level the pre-show sequence already dims to, not full
     baseline brightness (confirmed: toggling back should not jolt the
     room to full brightness mid-movie).

No screen-alpha or media pause/resume is touched by this toggle — it is
lights-only, independent of what is playing.

The button's label stays static ("Lights") rather than reflecting current
state dynamically — the room itself is the feedback, and a
dynamically-updating label would couple `PreShowSequencer` to a specific
UI element for no real gain. Wiring follows the same pattern as the
existing Keyboard/Close buttons (`VRMenuButton` + a persistent `onClick`
listener calling `PreShowSequencer.ToggleBlackout()`), but — unlike those
buttons — does **not** call `VRMenuController.CloseMenu()` afterward: a
toggle the player may want to flip back quickly shouldn't force a
reopen-the-menu round trip each time.

## Data flow

```
Go(d) -> GoToObject(d)
  browserPlane.PauseMedia()                             (new, unconditional)
  activates d.root, teleports rig to d.spawnPoint      (unchanged)
  MoveBrowser(d):
    if d.browserAnchor == null -> return                (unchanged, Lobby)
    reposition + rebuild the browser plane               (unchanged)
    preShowSequencer.Play(d.root, plane's Renderer)      (new)
      -> snap lights to baseline, screen alpha = 0
      -> lerp lights down (1.2s)
      -> lerp screen alpha up (1.0s)
      -> browserPlane.ResumeMedia()                      (new)
```

The sequence is not a blocking modal — the player can already look around
and walk while it plays, exactly as they can today while the plane
repositions.

## Edge cases

- **Rapid theater switching**: handled by the stop-and-restart coroutine
  pattern in `Play()`; no stacked coroutines, no leftover half-dimmed
  state from an abandoned sequence, because the very next `Play()` call
  always re-snaps to baseline first.
- **First-ever visit to a theater**: baseline capture happens lazily on
  first contact with each light, so no pre-population step is needed at
  scene load.
- **Leaving and returning to the same theater**: the theater root is
  deactivated on exit; Unity preserves each Light's last runtime intensity
  across the deactivate/reactivate cycle, which is exactly why step 3
  (snap-to-baseline) runs unconditionally at the start of every `Play()`
  call rather than only once.
- **Switching away while nothing is playing, or the page has no media**:
  `PauseMedia()`/`ResumeMedia()` degrade to a no-op `querySelectorAll`
  over zero elements — harmless.
- **User manually paused a video on purpose before switching theaters**:
  `ResumeMedia()` cannot tell the difference and will restart it anyway.
  This is a known, accepted trade-off of having no state-query channel
  back from the page (see Media pause/resume section) — auto-resume was
  chosen over pause-only because it reads as more seamless in the common
  case.
- **Going to the Lobby while something is playing**: paused on switch-out
  like any other destination, but never resumed (Lobby has no
  `browserAnchor`, so `PreShowSequencer.Play()` never runs there) — it
  stays paused, sitting on the now-inactive theater screen, until the
  player returns to a theater.
- **Toggling blackout, then switching theaters**: the next `Play()` call
  snaps lights back to baseline before dimming (existing step 3
  behavior), so a manual blackout never leaks into the next theater —
  `_isBlackedOut` should also be reset to `false` at the start of every
  `Play()` call so the button's next press reflects the fresh theater's
  state rather than a stale one carried over.
- **Toggling blackout in the Lobby**: no-ops, since `_activeTheaterRoot`
  is only ever set by `Play()`, which never runs for Lobby.

## Testing

- Enter Home Theater from the Lobby: lights dim, screen fades in.
- Enter Luxury Theater directly: same behavior, independent of Home
  Theater's lights/screen state.
- Switch Home -> Luxury -> Home rapidly: no stuck dim lights, no
  instant/snapped transitions, no console errors from a stopped
  coroutine.
- Return to the Lobby: theater lights are irrelevant while deactivated;
  Lobby's own lighting is untouched by this feature.
- Confirm the existing baked-camera-disable and screen-mesh-disable logic
  (unrelated to this feature) still behaves as before.
- Start a video, switch theaters mid-playback: audio/video stops
  immediately on switch-out (not left running during the transition), and
  resumes once the new screen finishes fading in.
- Start a video, go to the Lobby: playback stops and stays stopped (no
  resume) until returning to a theater.
- Start a video, switch theaters rapidly more than once before any fade
  finishes: no duplicate pause/resume calls pile up in a way that leaves
  media in the wrong state (the coroutine restart in `Play()` already
  guarantees only the latest switch's resume ever fires).
- Open the menu in a theater, press "Lights": room goes fully dark; press
  again: lights return to the dimmed movie level (not full brightness).
  The video on screen is unaffected either way.
- Press "Lights" to black out, then switch to the other theater: the new
  theater enters at its normal dimmed level, not blacked out, and its own
  "Lights" press starts from off-state (not carrying over the previous
  theater's toggle).
- Press "Lights" while still in the Lobby (if the button is visible
  there): no-op, no errors.
