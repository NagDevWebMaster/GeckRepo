# Pre-show sequence design

Status: approved design, pending implementation plan
Date: 2026-09-10

## Problem

Walking into Home Theater or Luxury Theater currently snaps straight to a
fully lit room with the browser screen already live. Real cinemas have a
brief ritual before the film starts — house lights come down, the screen
comes to life — and the app has none of that. This spec covers approach B
from the immersion brainstorm: dim the room lights, then fade the screen in.

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

## Data flow

```
Go(d) -> GoToObject(d)
  activates d.root, teleports rig to d.spawnPoint      (unchanged)
  MoveBrowser(d):
    if d.browserAnchor == null -> return                (unchanged, Lobby)
    reposition + rebuild the browser plane               (unchanged)
    preShowSequencer.Play(d.root, plane's Renderer)      (new)
      -> snap lights to baseline, screen alpha = 0
      -> lerp lights down (1.2s)
      -> lerp screen alpha up (1.0s)
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
