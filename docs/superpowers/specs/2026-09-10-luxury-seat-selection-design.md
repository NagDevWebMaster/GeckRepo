# Luxury Theater seat selection design

Status: approved design, pending implementation plan
Date: 2026-09-10

## Problem

Luxury Theater has 336 real seat nodes modeled in `LuxuryTheater.fbx`
(`Seat_L_##_##_Base` / `Seat_R_##_##_Base`), but nothing in
`HomeTheaterScene.unity` makes them selectable — the player has no way to
sit down, and no seat-aware state exists at all. This spec adds
point-and-teleport seat selection to Luxury Theater: walk up, point at any
seat, pull the trigger, get teleported into it with standing movement
disabled; open the popup menu and press "Stand Up" to leave.

A related, previously-undiscovered gap is in scope too: the XR rig's
`CharacterController` uses real gravity, and Luxury Theater's walkable
surfaces (`Riser_01..14`, `Center Aisle Carpet`, `Side Aisle`,
`Side Aisle.001`, `Screen Stage`) currently have zero colliders — so
walking into the raked seating area (which sitting/standing requires)
drops the player through the floor. This spec closes that gap as a
prerequisite, not a separate feature.

Explicitly out of scope:
- Home Theater seating (Luxury only, per direction)
- Any seat-occupancy persistence across app restarts
- Multiplayer / multiple simultaneous occupants (this app has one player)

## Components

### Reused almost verbatim from `Assets/Scripts/Cinema/`

This project already has a complete, disabled prototype seat system
(`SeatManager`, `Seat`, `PlayerManager`) built for a different, unused
scene (`CinemaScene.unity`, disabled in Build Settings). It is
input-agnostic and gravity-agnostic, so it ports directly:

- **`SeatManager`** — tracks every `Seat` in one theater, arbitrates
  selection (`RequestSit`: frees whichever seat the player was in before
  seating them in the new one). No changes.
- **`Seat`** — one seat's clickable marker plus its `SitAnchor` (where the
  player lands, facing the screen). Requires `GeckoUIButton` and
  `CinemaClickable`. No changes.
- **`PlayerManager`** — owns "which seat, if any" and moves the XR rig
  directly by transform (`Teleport`), the same direct-repositioning
  convention already used for `MoveBrowser()` rather than introducing
  XRI's `TeleportationProvider`. One change (see below).

### Why `CinemaClickable` stays even though it's a no-op here

`Seat` requires it (`[RequireComponent(typeof(CinemaClickable))]`), and it
is an empty marker component — adding it costs nothing and keeps `Seat`
unmodified. `HomeTheaterScene` doesn't use `CinemaPointerInput` (the
system that marker was built for); instead every `GeckoUIButton` — which
`Seat` also requires — is automatically wired to an XRI interactable by
`GeckoXRInteraction.AdoptButton()` on scene load, the same mechanism that
already makes the popup menu's Close/Lights buttons clickable. Seats work
for free through that path; no new input code is needed.

### `PlayerManager.SetStandingLocomotionEnabled()` — changed

Currently looks up `CinemaManager.Instance.ActiveScreen.Adapter
.GetComponent<GeckoCinemaLocomotion>()` — none of that pipeline
(`CinemaManager`, `BrowserPlaneAdapter`, `GeckoCinemaLocomotion`) exists
in `HomeTheaterScene`; this scene's real locomotion is the XR
Interaction Toolkit Starter Assets rig ("XR Origin (XR Rig)"), driven by
its own `DynamicMoveProvider`. The method changes to find and
enable/disable that component instead, via `FindAnyObjectByType`
(resolved once and cached, same pattern as
`CinemaNavigationManager.ResolveBrowserPlane()`).

### `LuxurySeatWireup` (new script)

Lives on `Auditorium (Luxury)`. Ports `TheaterBuilder.WireUpRealSeats()`
almost line for line: on first activation, walks its own children for
transforms whose name contains `_Base`, and for each one:
1. Adds a `BoxCollider` sized from the node's own `Renderer` bounds (if
   it has one) or a `0.5, 0.5, 0.5` default size (if the `_Base` node is
   a bare marker transform with no mesh — the real model hasn't been
   inspected closely enough to assume one way or the other, and
   `WireUpRealSeats` already handles both identically).
2. Adds `CinemaClickable`, `GeckoUIButton`, `Seat`.
3. Builds a child `SitAnchor` transform, offset up from the seat node
   (`localPosition (0, 0.6, 0)`) and facing the screen. Luxury Theater's
   audience faces +Z world (per the measured layout: screen at higher Z,
   seats at lower Z), matching the existing code's hardcoded
   `Quaternion.LookRotation(Vector3.forward, Vector3.up)` — no change
   needed there.
4. Calls `Seat.Init()` and `SeatManager.Register()`.

Runs once, guarded against double-wiring (checks `Seat` component
presence before wiring a node), matching this scene's existing
lazy-initialization pattern for per-theater one-time setup.

All 336 seats are wired — no curation. A `BoxCollider` plus a
`GeckoXRButton`'s per-frame `Update()` (a handful of list checks, no
allocation) per seat is not expected to be a meaningful cost on Quest 3;
this is flagged as something to confirm on-device, not solved
preemptively.

### Walkable colliders (new, plain data — added by `LuxurySeatWireup`)

Adds `MeshCollider`s to `Riser_01` through `Riser_14`, `Center Aisle
Carpet`, `Side Aisle`, `Side Aisle.001`, and `Screen Stage` (matched by
exact name), so the `CharacterController` has ground to stand on
anywhere in the seating area. Excludes `Center Aisle LED*` trim nodes.
Done in the same `LuxurySeatWireup.Awake()` pass as seat wiring, since
both are one-time setup for the same theater becoming walkable.

### "Stand Up" popup-menu button (new)

Same construction pattern as the just-added "Lights" button: clones
`CloseButton`, positioned in the existing popup menu grid, labeled
"Stand Up", wires a persistent `onClick` listener to
`PlayerManager.Instance.StandUp()`, does not call
`VRMenuController.CloseMenu()` afterward (consistent with "Lights" —
a player who just stood up may want the menu to stay open, and there's
no harm in leaving it up).

### `CinemaNavigationManager.GoToObject()` — one addition

Gains an unconditional call, alongside the existing unconditional
`PauseMedia()` call: `PlayerManager.Instance?.StandUp()`. Fires for every
destination, including Lobby — leaving a seat is about where you're
leaving, not where you're headed, matching the existing pause-on-switch
pattern exactly.

## Data flow

```
Enter Luxury Theater (first time)
  -> LuxurySeatWireup.Awake(): adds walkable colliders, wires all 336 seats

Player walks up to a seat, points, pulls trigger
  (GeckoXRButton — same mechanism as any menu button, no new input code)
  -> Seat.OnClicked -> SeatManager.RequestSit(seat)
     -> frees any previously-occupied seat in this theater
     -> PlayerManager.SitAt(seat)
        -> Teleport(seat.SitAnchor.position, seat.SitAnchor.rotation)
        -> SetStandingLocomotionEnabled(false)  [disables DynamicMoveProvider]

Player opens popup menu, presses "Stand Up"
  -> PlayerManager.StandUp()
     -> frees CurrentSeat, CurrentSeat = null
     -> SetStandingLocomotionEnabled(true)

Player switches theaters (any destination, including Lobby)
  -> CinemaNavigationManager.GoToObject(d)
     -> PlayerManager.Instance?.StandUp()  [new, unconditional]
     -> (existing PauseMedia, activation, teleport-to-spawn logic)
```

## Edge cases

- **Switching theaters while seated**: `GoToObject()`'s new unconditional
  `StandUp()` call frees the seat and restores movement before the
  existing spawn-point teleport runs, so the player never arrives at the
  new theater with movement still disabled.
- **Selecting a second seat without standing**: `SeatManager.RequestSit`
  already frees the previous seat automatically — no special handling
  needed.
- **First-ever entry to Luxury Theater**: `LuxurySeatWireup` runs lazily
  on activation and is guarded against re-running on a later visit
  (checks for an existing `Seat` component before wiring a node).
- **Re-entering Luxury Theater after having sat there before**: seats
  were already freed by the stand-up-on-switch behavior, so nothing
  arrives "pre-occupied."
- **Pressing "Stand Up" while not seated**: `PlayerManager.StandUp()`
  already no-ops safely (`if (CurrentSeat != null)` guard) — no change
  needed.
- **Walking into the raked seating area before sitting**: covered by the
  new walkable colliders; without them this would already be broken
  today (falls through the floor), independent of seat selection.

## Testing

Informal, matching this project's no-automated-test-suite convention
(verified via Unity Editor scripting + in-headset checks, per this
project's established pattern):

- Enter Luxury Theater, walk into the raked seating area: no falling
  through the floor.
- Point at a seat, pull trigger: teleported to that seat, facing the
  screen; standing movement stops working.
- Open the popup menu, press "Stand Up": movement returns; the seat's
  color reverts from "taken" to "free".
- Point at a second seat without standing first: teleported there
  directly; the first seat shows "free" again.
- While seated, switch to Home Theater: arrives at Home Theater able to
  walk immediately (not stuck with movement disabled).
- Confirm existing Luxury Theater behavior (pre-show dim/fade, media
  pause/resume, Lights toggle) is unaffected.
