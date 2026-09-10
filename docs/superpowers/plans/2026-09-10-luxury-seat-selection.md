# Luxury Theater Seat Selection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the player walk up to any of Luxury Theater's 336 real seats, point and click to teleport into it (standing movement disabled while seated), and stand back up via a new "Stand Up" popup-menu button.

**Architecture:** Reuse the existing (currently unused) `SeatManager`/`Seat`/`PlayerManager` trio from `Assets/Scripts/Cinema/` almost unmodified — they are already input-agnostic and gravity-agnostic. One line changes in `PlayerManager` (swap its locomotion-disable target from the unused `CinemaManager` pipeline to this scene's real `DynamicMoveProvider`). One new script, `LuxurySeatWireup`, ports `TheaterBuilder.WireUpRealSeats()`'s node-wiring logic to run once against the real FBX's seat nodes, and also adds the walkable-surface colliders the raked seating area is currently missing entirely. `CinemaNavigationManager` gains one unconditional `StandUp()` call, matching its existing unconditional `PauseMedia()` call.

**Tech Stack:** Unity 6000.5.5f1, C#, XR Interaction Toolkit 3.5.1 Starter Assets (`DynamicMoveProvider`), Unity Editor scripting via the project's `unity-mcp-cli` bridge for all verification and scene wiring.

**Spec:** `docs/superpowers/specs/2026-09-10-luxury-seat-selection-design.md`

## Testing Note (read before starting)

This project has **no automated test framework**. Every change is verified through Unity Editor scripting (the `script-execute` MCP tool), reading console output back, and reload-from-disk verification for anything saved to a scene.

**Exact `script-execute` invocation format** (confirmed working this session — do not use a bare method body, it will fail to compile):

```bash
cat > /tmp/task_script.json <<'EOF'
{"csharpCode": "using UnityEngine; public class Script { public static object Main() { /* your code */ return \"some result\"; } }"}
EOF
npx --yes unity-mcp-cli run-tool script-execute --input-file /tmp/task_script.json --timeout 90000
```

`Main()` must return an `object` (a string summary is easiest to read back). The class must be named `Script` and the method `Main`.

**The Editor's MCP connection is intermittently unresponsive** (`HTTP 503`/`504`, or a 60-120s timeout) — this happened repeatedly during this project's earlier work and is not a sign anything is wrong with your code. Retry 2-3 times with a few seconds between attempts before concluding something is actually broken.

**Inactive GameObjects are invisible to `GameObject.Find`.** Auditorium (Luxury) starts inactive (`m_IsActive: 0`) — use `SceneManager.GetActiveScene().GetRootGameObjects()` plus `GetComponentsInChildren<Transform>(true)` (the `true` includes inactive) to find things under it, never a bare `GameObject.Find`.

**Critical lesson from this project's history, repeated here because it has bitten this exact codebase multiple times:** calling a live scene object's real runtime methods (e.g. actually invoking `CinemaNavigationManager.LoadTheater1()` to "look at" a theater) leaks Edit-mode side effects into the scene the moment `SaveScene()` next runs — material instances, moved transforms, toggled active-states. **Never call live navigation/gameplay methods on the real scene objects for verification.** Read state, set specific fields/components directly, or build throwaway test objects instead. Every scene-mutating step in this plan ends with `EditorUtility.SetDirty` + `EditorSceneManager.MarkSceneDirty` + `EditorSceneManager.SaveScene`, and every task's final verification step **reopens the scene from disk** before checking state. Immediately before saving, and immediately after the reload-verify, run `git status --porcelain -uall` / `git diff --stat` — if anything outside this task's intended files shows as modified, investigate and revert it before moving on.

## Global Constraints

- Luxury Theater only (per direction) — Home Theater seating is explicitly out of scope.
- All 336 seats are wired, no curation.
- `Seat`/`SeatManager` are reused with **zero code changes**. `CinemaClickable` is still added to every seat even though nothing in this scene reads it — `Seat` requires it, and it's a zero-behavior marker.
- `PlayerManager.Teleport()` moves the XR rig directly by transform, matching the existing project-wide convention of not using XRI's `TeleportationProvider` (see `PlayerManager.cs`'s own header comment).
- Walkable-surface node names (exact match, not Contains): `Riser_01` through `Riser_14`, `Center Aisle Carpet`, `Side Aisle`, `Side Aisle.001`, `Screen Stage`.
- Seat node match: `Contains("_Base")` (matches `Seat_L_##_##_Base` / `Seat_R_##_##_Base`).
- Seat anchor: `localPosition (0, 0.6, 0)` relative to the seat node, world rotation `Quaternion.LookRotation(Vector3.forward, Vector3.up)` (audience faces +Z world in this model).
- BoxCollider fallback size when a seat node has no `Renderer`: `(0.5, 0.5, 0.5)`.
- The "Stand Up" button does **not** call `VRMenuController.CloseMenu()` on press (same exception the "Lights" button already has).

---

## File Structure

- **Modify** `Assets/Scripts/Cinema/PlayerManager.cs` — `SetStandingLocomotionEnabled()` (currently ~line 82-89) swaps its target from the unused `CinemaManager`/`BrowserPlaneAdapter`/`GeckoCinemaLocomotion` pipeline to this scene's real `DynamicMoveProvider`.
- **Create** `Assets/Scripts/Cinema/LuxurySeatWireup.cs` — new MonoBehaviour, no namespace (matches every other file in `Assets/Scripts/Cinema/`). Lives on `Auditorium (Luxury)`. On `Awake()`, adds `MeshCollider`s to the walkable surfaces and wires every `_Base` seat node into a `Seat`.
- **Modify** `Assets/Scripts/VRMenu/CinemaNavigationManager.cs` — one line in `GoToObject()` (currently ~line 158-195): unconditional `PlayerManager.Instance?.StandUp()`.
- **Modify (scene)** `Assets/Scenes/HomeTheaterScene.unity`:
  - Add a `PlayerManager` component to the `Cinema Navigation` GameObject (same object that already hosts `CinemaNavigationManager` and `PreShowSequencer`), with `xrRig` wired to the `XR Origin (XR Rig)` object.
  - Add `SeatManager` and `LuxurySeatWireup` components to `Auditorium (Luxury)`.
  - Add a "Stand Up" button to the popup menu (`VR Menu Anchor/VR Menu`), cloned from `CloseButton`, at `anchoredPosition (165, -190)`, `sizeDelta (80, 80)` — the next open slot in the existing bottom action row (`KeyboardButton` at x=-55, `CloseButton` at x=55, `LightsButton` at x=-165, all spaced 110 apart; 165 continues that spacing rightward, confirmed empty by direct inspection of the live scene).

---

### Task 1: Fix `PlayerManager.SetStandingLocomotionEnabled()`

**Files:**
- Modify: `Assets/Scripts/Cinema/PlayerManager.cs`

**Interfaces:**
- Consumes: `UnityEngine.XR.Interaction.Toolkit.Samples.StarterAssets.DynamicMoveProvider` (from `Assets/Samples/XR Interaction Toolkit/3.5.1/Starter Assets/Scripts/DynamicMoveProvider.cs`), the move-provider component already present on this scene's `XR Origin (XR Rig)` instance.
- Produces: `PlayerManager.SitAt(Seat)` / `PlayerManager.StandUp()` (unchanged signatures, already public) now actually disable/enable movement in this scene. No new public surface.

- [ ] **Step 1: Read the current file to confirm line numbers haven't drifted**

```bash
cat -n Assets/Scripts/Cinema/PlayerManager.cs
```

Confirm `SetStandingLocomotionEnabled` still reads exactly as below before editing (if it doesn't, the file changed since this plan was written — stop and re-read it fully before proceeding):

```csharp
    /// <summary>
    /// Enables/disables the ACTIVE theater's GeckoCinemaLocomotion rather than
    /// a fixed reference - which BrowserPlane instance owns that component
    /// changes as the player moves between theaters, since only the active
    /// theater's copy is ever live.
    /// </summary>
    private void SetStandingLocomotionEnabled(bool isEnabled)
    {
        var screen = CinemaManager.Instance != null ? CinemaManager.Instance.ActiveScreen : null;
        if (screen == null || screen.Adapter == null) return;

        var locomotion = screen.Adapter.GetComponent<GeckoCinemaLocomotion>();
        if (locomotion != null) locomotion.enabled = isEnabled;
    }
```

- [ ] **Step 2: Replace it**

```csharp
    /// <summary>
    /// Enables/disables this scene's real standing-locomotion component
    /// (the XR Interaction Toolkit Starter Assets' DynamicMoveProvider on
    /// the XR Origin) rather than GeckoCinemaLocomotion - HomeTheaterScene
    /// does not use the CinemaManager/BrowserPlaneAdapter/GeckoCinemaLocomotion
    /// pipeline that method targeted (that pipeline belongs to a different,
    /// disabled prototype scene - see PlayerManager.cs's own class header).
    /// Resolved once and cached, same pattern as
    /// CinemaNavigationManager.ResolveBrowserPlane().
    /// </summary>
    private DynamicMoveProvider _moveProvider;

    private DynamicMoveProvider ResolveMoveProvider()
    {
        if (_moveProvider != null) return _moveProvider;
        _moveProvider = FindAnyObjectByType<DynamicMoveProvider>();
        return _moveProvider;
    }

    private void SetStandingLocomotionEnabled(bool isEnabled)
    {
        var provider = ResolveMoveProvider();
        if (provider != null) provider.enabled = isEnabled;
    }
```

- [ ] **Step 3: Add the using directive**

At the top of the file, add this line alongside the existing `using UnityEngine;`:

```csharp
using UnityEngine.XR.Interaction.Toolkit.Samples.StarterAssets;
```

- [ ] **Step 4: Refresh and check for compile errors**

```bash
cat > /tmp/task1_refresh.json <<'EOF'
{"csharpCode": "using UnityEditor; public class Script { public static object Main() { AssetDatabase.Refresh(); return \"refreshed\"; } }"}
EOF
npx --yes unity-mcp-cli run-tool script-execute --input-file /tmp/task1_refresh.json --timeout 90000
```

Poll `editor-application-get-state` (retrying on timeout per the Testing Note) until `IsCompiling: false`, then:

```bash
npx --yes unity-mcp-cli run-tool console-get-logs --input '{"maxCount": 20, "logTypes": ["Error"]}' --timeout 90000
```

Expected: no errors mentioning `PlayerManager.cs`. (Pre-existing `CS0618` obsolete-API warnings from other files are fine and unrelated.)

- [ ] **Step 5: Verify by direct field/behavior check**

`DynamicMoveProvider` won't exist in the live scene as a findable object until Task 3 wires up the XR rig verification — but this class-level check doesn't need the scene at all, just confirm the type resolves and the method compiles clean:

```bash
cat > /tmp/task1_verify.json <<'EOF'
{"csharpCode": "using UnityEngine; using UnityEngine.XR.Interaction.Toolkit.Samples.StarterAssets; public class Script { public static object Main() { var t = typeof(DynamicMoveProvider); var pm = typeof(PlayerManager).GetMethod(\"SetStandingLocomotionEnabled\", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance); return \"DynamicMoveProvider resolved=\" + (t != null) + \" SetStandingLocomotionEnabled found=\" + (pm != null); } }"}
EOF
npx --yes unity-mcp-cli run-tool script-execute --input-file /tmp/task1_verify.json --timeout 90000
```

Expected: `DynamicMoveProvider resolved=True SetStandingLocomotionEnabled found=True`

- [ ] **Step 6: Commit**

```bash
git add Assets/Scripts/Cinema/PlayerManager.cs
git commit -m "Point PlayerManager's locomotion toggle at this scene's real rig

HomeTheaterScene uses the XR Interaction Toolkit Starter Assets rig
(DynamicMoveProvider), not the CinemaManager/GeckoCinemaLocomotion
pipeline PlayerManager originally targeted - that pipeline belongs to
a different, disabled prototype scene."
```

---

### Task 2: Create `LuxurySeatWireup.cs`

**Files:**
- Create: `Assets/Scripts/Cinema/LuxurySeatWireup.cs`

**Interfaces:**
- Consumes: `SeatManager.Register(Seat)` (existing, `Assets/Scripts/Cinema/SeatManager.cs`), `Seat.Init(SeatManager, Transform)` (existing, `Assets/Scripts/Cinema/Seat.cs`), `CinemaClickable` (existing, empty marker), `GeckoUIButton` (existing, `Assets/Scripts/GeckoUIButton.cs`).
- Produces: `LuxurySeatWireup` component — no public methods beyond the MonoBehaviour lifecycle; Task 3 adds it (and a `SeatManager`) to `Auditorium (Luxury)`.

- [ ] **Step 1: Write the file**

```csharp
// =============================================================================
//  LuxurySeatWireup.cs
//
//  Makes every real seat in the Luxury Theater model clickable, and adds
//  walkable colliders to its risers/aisles - both one-time setup that runs
//  the first time this theater is activated (Awake() only ever fires once
//  per GameObject instance, so the per-node "already has a Seat" check
//  below is a defensive guard against a stray double-call, not the
//  primary guarantee against double-wiring).
//
//  Ports TheaterBuilder.WireUpRealSeats() (Assets/Scripts/Cinema/TheaterBuilder.cs)
//  almost line for line - that method already solves "find real seat nodes
//  by name, size a collider from the node's own renderer bounds, wire a
//  Seat" for exactly this kind of model. CinemaClickable is still added
//  even though nothing in this scene reads it - Seat requires it, and it's
//  a zero-behavior marker. No CinemaPointerInput port is needed:
//  GeckoXRInteraction.AdoptButton() gives every GeckoUIButton (which Seat
//  also requires) an XR interactable automatically, the same mechanism
//  that already drives the popup menu's Close/Lights buttons.
// =============================================================================

using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(SeatManager))]
public class LuxurySeatWireup : MonoBehaviour
{
    [Tooltip("Node name (contains-match) for a seat's click target - matches " +
             "LuxuryTheater.fbx's real node names (Seat_L_##_##_Base / " +
             "Seat_R_##_##_Base).")]
    public string seatNodeContains = "_Base";

    // Exact names, not a Contains-match - "Center Aisle LED" must NOT pick up
    // a collider just because it contains "Aisle".
    private static readonly string[] kWalkableNames = BuildWalkableNames();

    private SeatManager _seats;

    private void Awake()
    {
        _seats = GetComponent<SeatManager>();
        AddWalkableColliders();
        WireSeats();
    }

    private static string[] BuildWalkableNames()
    {
        var names = new List<string>();
        for (int i = 1; i <= 14; i++) names.Add($"Riser_{i:00}");
        names.Add("Center Aisle Carpet");
        names.Add("Side Aisle");
        names.Add("Side Aisle.001");
        names.Add("Screen Stage");
        return names.ToArray();
    }

    /// <summary>
    /// The XR rig's CharacterController uses real gravity, and this model's
    /// risers/aisles ship with zero colliders (FBX import has addColliders: 0)
    /// - without this, walking into the raked seating area (which sitting
    /// and standing both require) drops the player through the floor.
    /// </summary>
    private void AddWalkableColliders()
    {
        int added = 0;
        foreach (var t in GetComponentsInChildren<Transform>(true))
        {
            bool isWalkable = false;
            foreach (var n in kWalkableNames)
            {
                if (t.name == n) { isWalkable = true; break; }
            }
            if (!isWalkable) continue;
            if (t.GetComponent<Collider>() != null) continue;   // already has one

            var mesh = t.GetComponent<MeshFilter>();
            if (mesh == null || mesh.sharedMesh == null)
            {
                Debug.LogWarning("[LuxurySeatWireup] " + t.name +
                                 " has no MeshFilter/sharedMesh - cannot add a MeshCollider.");
                continue;
            }
            var collider = t.gameObject.AddComponent<MeshCollider>();
            collider.sharedMesh = mesh.sharedMesh;
            added++;
        }

        if (added == 0)
            Debug.LogWarning("[LuxurySeatWireup] no walkable-surface nodes found under " +
                             name + " - locomotion will fall through the floor.");
    }

    /// <summary>
    /// Adds a click target to every seat mesh in the real model. Real seat
    /// nodes are plain FBX meshes with no collider, so one is sized from the
    /// node's own renderer bounds - not a fixed guess - meaning it fits
    /// whatever the actual seat mesh looks like without per-seat tuning.
    /// </summary>
    private void WireSeats()
    {
        int wired = 0;
        foreach (var t in GetComponentsInChildren<Transform>(true))
        {
            if (!t.name.Contains(seatNodeContains)) continue;
            if (t.GetComponent<Seat>() != null) continue;   // already wired

            var renderer = t.GetComponent<Renderer>();
            var box = t.gameObject.AddComponent<BoxCollider>();
            if (renderer != null)
            {
                Bounds wb = renderer.bounds;
                box.center = t.InverseTransformPoint(wb.center);
                Vector3 size = t.InverseTransformVector(wb.size);
                box.size = new Vector3(Mathf.Max(0.05f, Mathf.Abs(size.x)),
                                       Mathf.Max(0.05f, Mathf.Abs(size.y)),
                                       Mathf.Max(0.05f, Mathf.Abs(size.z)));
            }
            else
            {
                box.size = Vector3.one * 0.5f;
            }

            t.gameObject.AddComponent<CinemaClickable>();
            if (t.gameObject.GetComponent<GeckoUIButton>() == null)
                t.gameObject.AddComponent<GeckoUIButton>();

            var anchor = new GameObject("SitAnchor");
            anchor.transform.SetParent(t, false);
            anchor.transform.localPosition = new Vector3(0f, 0.6f, 0f);
            // World-space, not local: audience faces +Z world in this model,
            // regardless of any baked rotation on the individual seat node.
            anchor.transform.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);

            var seat = t.gameObject.AddComponent<Seat>();
            seat.Init(_seats, anchor.transform);
            _seats.Register(seat);
            wired++;
        }

        if (wired == 0)
            Debug.LogWarning("[LuxurySeatWireup] no nodes containing '" + seatNodeContains +
                             "' found under " + name + " - no seats are selectable.");
    }
}
```

- [ ] **Step 2: Refresh and check for compile errors**

```bash
cat > /tmp/task2_refresh.json <<'EOF'
{"csharpCode": "using UnityEditor; public class Script { public static object Main() { AssetDatabase.Refresh(); return \"refreshed\"; } }"}
EOF
npx --yes unity-mcp-cli run-tool script-execute --input-file /tmp/task2_refresh.json --timeout 90000
```

Poll until `IsCompiling: false`, then check `console-get-logs` for errors mentioning `LuxurySeatWireup.cs`. Expected: none.

- [ ] **Step 3: Verify the type resolves and requires SeatManager**

```bash
cat > /tmp/task2_verify.json <<'EOF'
{"csharpCode": "using UnityEngine; public class Script { public static object Main() { var t = typeof(LuxurySeatWireup); var attrs = t.GetCustomAttributes(typeof(RequireComponent), false); bool requiresSeatManager = false; foreach (RequireComponent rc in attrs) if (rc.m_Type0 == typeof(SeatManager)) requiresSeatManager = true; return \"type resolved=\" + (t != null) + \" requiresSeatManager=\" + requiresSeatManager; } }"}
EOF
npx --yes unity-mcp-cli run-tool script-execute --input-file /tmp/task2_verify.json --timeout 90000
```

Expected: `type resolved=True requiresSeatManager=True`

- [ ] **Step 4: Commit**

```bash
git add Assets/Scripts/Cinema/LuxurySeatWireup.cs
git commit -m "Add LuxurySeatWireup: wires all 336 real seats + walkable colliders

Ports TheaterBuilder.WireUpRealSeats() to run once against Luxury
Theater's actual FBX seat nodes, and adds MeshColliders to its
risers/aisles (the model ships with none, so the XR rig's gravity
drops the player through the raked floor without this)."
```

---

### Task 3: Scene wiring — PlayerManager, SeatManager, LuxurySeatWireup

**Files:**
- Modify (scene): `Assets/Scenes/HomeTheaterScene.unity`

**Interfaces:**
- Consumes: `PlayerManager` (Task-1-fixed), `LuxurySeatWireup`/`SeatManager` (Task 2).
- Produces: a live `PlayerManager.Instance` at runtime (Task 4 and Task 5 both call `PlayerManager.Instance.StandUp()`), and `Auditorium (Luxury)` carrying working `SeatManager` + `LuxurySeatWireup` components.

- [ ] **Step 1: Add `PlayerManager` to `Cinema Navigation`, wired to the XR rig**

```bash
cat > /tmp/task3_wire_playermanager.json <<'EOF'
{"csharpCode": "using UnityEngine; using UnityEngine.SceneManagement; using UnityEditor; using UnityEditor.SceneManagement; public class Script { public static object Main() { GameObject cinemaNav = null, xrOrigin = null; foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects()) { if (root.name == \"Cinema Navigation\") cinemaNav = root; if (root.name == \"XR Origin (XR Rig)\") xrOrigin = root; } if (cinemaNav == null) return \"Cinema Navigation not found\"; if (xrOrigin == null) return \"XR Origin (XR Rig) not found\"; var pm = cinemaNav.GetComponent<PlayerManager>(); if (pm == null) pm = cinemaNav.AddComponent<PlayerManager>(); pm.xrRig = xrOrigin.transform; EditorUtility.SetDirty(cinemaNav); EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene()); bool saved = EditorSceneManager.SaveScene(SceneManager.GetActiveScene()); return \"PlayerManager added, xrRig=\" + pm.xrRig.name + \" saved=\" + saved; } }"}
EOF
npx --yes unity-mcp-cli run-tool script-execute --input-file /tmp/task3_wire_playermanager.json --timeout 90000
```

Expected: `PlayerManager added, xrRig=XR Origin (XR Rig) saved=True`

- [ ] **Step 2: Add `SeatManager` + `LuxurySeatWireup` to `Auditorium (Luxury)`**

`LuxurySeatWireup` requires `SeatManager` on the same object (`[RequireComponent]`) — add `SeatManager` first, or `AddComponent<LuxurySeatWireup>` will add it automatically; adding it explicitly first keeps the order obvious in the Inspector.

```bash
cat > /tmp/task3_wire_seats.json <<'EOF'
{"csharpCode": "using UnityEngine; using UnityEngine.SceneManagement; using UnityEditor; using UnityEditor.SceneManagement; public class Script { public static object Main() { GameObject auditorium = null; foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects()) if (root.name == \"Auditorium (Luxury)\") auditorium = root; if (auditorium == null) return \"Auditorium (Luxury) not found\"; var sm = auditorium.GetComponent<SeatManager>(); if (sm == null) sm = auditorium.AddComponent<SeatManager>(); var wireup = auditorium.GetComponent<LuxurySeatWireup>(); if (wireup == null) wireup = auditorium.AddComponent<LuxurySeatWireup>(); EditorUtility.SetDirty(auditorium); EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene()); bool saved = EditorSceneManager.SaveScene(SceneManager.GetActiveScene()); return \"SeatManager added=\" + (sm != null) + \" LuxurySeatWireup added=\" + (wireup != null) + \" seatNodeContains=\" + wireup.seatNodeContains + \" saved=\" + saved; } }"}
EOF
npx --yes unity-mcp-cli run-tool script-execute --input-file /tmp/task3_wire_seats.json --timeout 90000
```

Expected: `SeatManager added=True LuxurySeatWireup added=True seatNodeContains=_Base saved=True`

- [ ] **Step 3: Check for scene contamination before reload-verify**

```bash
git status --porcelain -uall
git diff --stat
```

Expected: only `Assets/Scenes/HomeTheaterScene.unity` modified (plus any pre-existing, already-known unrelated diffs already in the working tree from earlier unrelated work — do not touch those). If anything else changed, investigate and revert it before continuing (see Testing Note's critical-lesson paragraph).

- [ ] **Step 4: Reload-verify from disk**

```bash
cat > /tmp/task3_reload_verify.json <<'EOF'
{"csharpCode": "using UnityEngine; using UnityEngine.SceneManagement; using UnityEditor.SceneManagement; public class Script { public static object Main() { EditorSceneManager.OpenScene(\"Assets/Scenes/HomeTheaterScene.unity\", OpenSceneMode.Single); GameObject cinemaNav = null, auditorium = null; foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects()) { if (root.name == \"Cinema Navigation\") cinemaNav = root; if (root.name == \"Auditorium (Luxury)\") auditorium = root; } var pm = cinemaNav.GetComponent<PlayerManager>(); var sm = auditorium.GetComponent<SeatManager>(); var wireup = auditorium.GetComponent<LuxurySeatWireup>(); return \"PlayerManager.xrRig=\" + (pm != null ? pm.xrRig?.name : \"MISSING\") + \" SeatManager=\" + (sm != null) + \" LuxurySeatWireup=\" + (wireup != null); } }"}
EOF
npx --yes unity-mcp-cli run-tool script-execute --input-file /tmp/task3_reload_verify.json --timeout 90000
```

Expected: `PlayerManager.xrRig=XR Origin (XR Rig) SeatManager=True LuxurySeatWireup=True`

- [ ] **Step 5: Verify seat wiring actually runs (via a throwaway activation, not the real navigation methods)**

Per the Testing Note, never call `CinemaNavigationManager.LoadTheater2()` or similar live navigation methods — instead, activate `Auditorium (Luxury)` directly just long enough to confirm `Awake()` wired the seats, then deactivate it again without saving:

```bash
cat > /tmp/task3_verify_wiring.json <<'EOF'
{"csharpCode": "using UnityEngine; using UnityEngine.SceneManagement; using System.Linq; public class Script { public static object Main() { GameObject auditorium = null; foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects()) if (root.name == \"Auditorium (Luxury)\") auditorium = root; bool wasActive = auditorium.activeSelf; auditorium.SetActive(true); var sm = auditorium.GetComponent<SeatManager>(); int seatCount = sm.Seats.Count; int riserColliders = auditorium.GetComponentsInChildren<Transform>(true).Count(t => t.name.StartsWith(\"Riser_\") && t.GetComponent<Collider>() != null); auditorium.SetActive(wasActive); return \"seatCount=\" + seatCount + \" riserColliders=\" + riserColliders; } }"}
EOF
npx --yes unity-mcp-cli run-tool script-execute --input-file /tmp/task3_verify_wiring.json --timeout 90000
```

Expected: `seatCount=336 riserColliders=14`. (`auditorium.SetActive(wasActive)` restores the original inactive state — this does not need a scene save since no persisted field changed, only runtime-added components on an already-saved GameObject configuration; confirm with `git status --porcelain -uall` that the working tree is still clean relative to Step 3's commit point before moving on.)

- [ ] **Step 6: Commit**

```bash
git add Assets/Scenes/HomeTheaterScene.unity
git commit -m "Wire PlayerManager/SeatManager/LuxurySeatWireup into HomeTheaterScene

PlayerManager lives on Cinema Navigation (alongside the other scene-wide
managers) with xrRig pointed at the XR Origin. SeatManager and
LuxurySeatWireup live on Auditorium (Luxury) itself."
```

---

### Task 4: "Stand Up" popup-menu button

**Files:**
- Modify (scene): `Assets/Scenes/HomeTheaterScene.unity`

**Interfaces:**
- Consumes: `PlayerManager.StandUp()` (existing public instance method; Task 1 makes it actually work in this scene; Task 3 put the one live `PlayerManager` component on the `Cinema Navigation` GameObject).
- Produces: nothing new for later tasks — this is a leaf UI addition.

**How the popup menu's buttons actually work (confirmed by direct inspection this session — do not re-derive from GeckoUIButton, which is a different, unrelated button system used elsewhere in this project):** `CloseButton` is a uGUI `Button` (`UnityEngine.UI.Button`) with an `Image`, a `VRCinema.VRMenuButton` (hover/press color+scale feedback only, no click logic), and a `UnityEngine.UI.Shadow`. Its click is a standard **persistent listener** on `Button.onClick` (a `UnityEvent`), confirmed live: `target=VR Menu Anchor (VRCinema.VRMenuController) method=CloseMenu`, count=1. Persistent listeners **are** serialized into the scene file — this is the same mechanism `LightsButton` uses for `PreShowSequencer.ToggleBlackout()` (per this project's own history). `Instantiate`-cloning `CloseButton` copies that listener too, so the clone must have it **removed** before adding the real one, or the new button would both stand the player up and close the menu's `CloseMenu()`... except `CloseMenu()` on a clone that isn't `CloseButton` would still just close the menu, which is specifically what the spec says NOT to do for this button.

- [ ] **Step 1: Clone `CloseButton`, reposition/relabel, and rewire the persistent listener in one pass**

Confirmed live layout (queried directly from the scene before writing this plan): bottom action row is `KeyboardButton` at `anchoredPosition (-55, -190)`, `CloseButton` at `(55, -190)`, `LightsButton` at `(-165, -190)`, all `sizeDelta (80, 80)`, spaced 110 apart. `(165, -190)` continues that spacing and is unoccupied.

```bash
cat > /tmp/task4_standup_button.json <<'EOF'
{"csharpCode": "using UnityEngine; using UnityEngine.SceneManagement; using UnityEngine.UI; using UnityEditor; using UnityEditor.Events; using UnityEditor.SceneManagement; using TMPro; public class Script { public static object Main() { GameObject menuGo = null, cinemaNav = null; foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects()) { foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name == \"VR Menu\") menuGo = t.gameObject; if (root.name == \"Cinema Navigation\") cinemaNav = root; } if (menuGo == null) return \"VR Menu not found\"; if (cinemaNav == null) return \"Cinema Navigation not found\"; var playerManager = cinemaNav.GetComponent<PlayerManager>(); if (playerManager == null) return \"PlayerManager not found on Cinema Navigation - run Task 3 first\"; Transform closeBtn = null; foreach (Transform t in menuGo.GetComponentsInChildren<Transform>(true)) if (t.name == \"CloseButton\") closeBtn = t; if (closeBtn == null) return \"CloseButton not found\"; foreach (var t in menuGo.GetComponentsInChildren<Transform>(true)) if (t.name == \"StandUpButton\") return \"StandUpButton already exists - not duplicating\"; var clone = Object.Instantiate(closeBtn.gameObject, closeBtn.parent); clone.name = \"StandUpButton\"; var rt = clone.GetComponent<RectTransform>(); rt.anchoredPosition = new Vector2(165f, -190f); rt.sizeDelta = new Vector2(80f, 80f); var label = clone.GetComponentInChildren<TextMeshProUGUI>(); if (label != null) label.text = \"Stand Up\"; var btn = clone.GetComponent<Button>(); int inheritedCount = btn.onClick.GetPersistentEventCount(); while (btn.onClick.GetPersistentEventCount() > 0) UnityEventTools.RemovePersistentListener(btn.onClick, 0); UnityEventTools.AddPersistentListener(btn.onClick, playerManager.StandUp); int finalCount = btn.onClick.GetPersistentEventCount(); string finalTarget = finalCount > 0 ? (btn.onClick.GetPersistentTarget(0) + \" / \" + btn.onClick.GetPersistentMethodName(0)) : \"NONE\"; EditorUtility.SetDirty(menuGo); EditorUtility.SetDirty(clone); EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene()); bool saved = EditorSceneManager.SaveScene(SceneManager.GetActiveScene()); return \"created at \" + rt.anchoredPosition + \" label=\" + (label != null ? label.text : \"NO LABEL\") + \" inheritedListeners=\" + inheritedCount + \" finalListener=\" + finalTarget + \" saved=\" + saved; } }"}
EOF
npx --yes unity-mcp-cli run-tool script-execute --input-file /tmp/task4_standup_button.json --timeout 90000
```

Expected: `created at (165.00, -190.00) label=Stand Up inheritedListeners=1 finalListener=Cinema Navigation (PlayerManager) / StandUp saved=True`

- [ ] **Step 2: Check for contamination**

```bash
git status --porcelain -uall
git diff --stat
```

Expected: only `Assets/Scenes/HomeTheaterScene.unity` modified (beyond any pre-existing unrelated diffs already in the working tree — leave those alone).

- [ ] **Step 3: Reload-verify — position, label, AND the persistent listener (this is exactly the kind of thing that silently fails to persist per the Testing Note)**

```bash
cat > /tmp/task4_reload_verify.json <<'EOF'
{"csharpCode": "using UnityEngine; using UnityEngine.SceneManagement; using UnityEngine.UI; using UnityEditor.SceneManagement; using TMPro; public class Script { public static object Main() { EditorSceneManager.OpenScene(\"Assets/Scenes/HomeTheaterScene.unity\", OpenSceneMode.Single); GameObject standUpBtn = null; foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects()) foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name == \"StandUpButton\") standUpBtn = t.gameObject; if (standUpBtn == null) return \"StandUpButton missing after reload\"; var rt = standUpBtn.GetComponent<RectTransform>(); var label = standUpBtn.GetComponentInChildren<TextMeshProUGUI>(); var btn = standUpBtn.GetComponent<Button>(); int n = btn.onClick.GetPersistentEventCount(); string listener = n > 0 ? (btn.onClick.GetPersistentTarget(0) + \" / \" + btn.onClick.GetPersistentMethodName(0)) : \"NONE\"; return \"pos=\" + rt.anchoredPosition + \" label=\" + (label != null ? label.text : \"NONE\") + \" listenerCount=\" + n + \" listener=\" + listener; } }"}
EOF
npx --yes unity-mcp-cli run-tool script-execute --input-file /tmp/task4_reload_verify.json --timeout 90000
```

Expected: `pos=(165.00, -190.00) label=Stand Up listenerCount=1 listener=Cinema Navigation (PlayerManager) / StandUp`

- [ ] **Step 4: Commit**

```bash
git add Assets/Scenes/HomeTheaterScene.unity
git commit -m "Add Stand Up button to the popup menu

Same construction pattern as the Lights button - clones CloseButton,
strips the inherited CloseMenu() persistent listener and replaces it
with PlayerManager.StandUp(), does not call CloseMenu() on press."
```

---

### Task 5: `CinemaNavigationManager.GoToObject()` — stand up on any switch

**Files:**
- Modify: `Assets/Scripts/VRMenu/CinemaNavigationManager.cs`

**Interfaces:**
- Consumes: `PlayerManager.Instance.StandUp()` (Task 1).
- Produces: nothing new for later tasks.

- [ ] **Step 1: Confirm current line numbers**

```bash
sed -n '158,196p' Assets/Scripts/VRMenu/CinemaNavigationManager.cs
```

Confirm it still matches:

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

- [ ] **Step 2: Add the unconditional `StandUp()` call, right alongside the existing `PauseMedia()` call**

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
            // Lobby, unlike the dim/fade/resume sequence below. Standing up
            // is the same: leaving a seat is about where you're leaving, not
            // where you're headed, so it isn't gated behind browserAnchor
            // the way the pre-show sequence is.
            Transform currentPlane = ResolveBrowserPlane();
            if (currentPlane != null)
            {
                var currentRenderer = currentPlane.GetComponent<GeckoVulkanRenderer>();
                if (currentRenderer != null) currentRenderer.PauseMedia();
            }

            if (PlayerManager.Instance != null) PlayerManager.Instance.StandUp();
```

(The rest of the method — `SetActive` calls, spawn-point teleport, `MoveBrowser(d)` — is unchanged.)

- [ ] **Step 3: Refresh and check for compile errors**

```bash
cat > /tmp/task5_refresh.json <<'EOF'
{"csharpCode": "using UnityEditor; public class Script { public static object Main() { AssetDatabase.Refresh(); return \"refreshed\"; } }"}
EOF
npx --yes unity-mcp-cli run-tool script-execute --input-file /tmp/task5_refresh.json --timeout 90000
```

Poll until `IsCompiling: false`, check `console-get-logs` for errors mentioning `CinemaNavigationManager.cs`. Expected: none. (`PlayerManager` has no namespace, `CinemaNavigationManager` is in `namespace VRCinema` — this compiles without a `using` directive since global-namespace types are visible everywhere; if the compiler disagrees, add `using global::PlayerManager;`... actually a type reference needs no `using` for the global namespace at all, so no import is needed. If an error appears here regardless, add nothing to `using` — instead double check `PlayerManager.cs` truly has no `namespace` wrapper as read in Task 1's Step 1.)

- [ ] **Step 4: Verify by inspecting the compiled method (do not invoke it live — see Testing Note)**

```bash
cat > /tmp/task5_verify.json <<'EOF'
{"csharpCode": "using System.Reflection; public class Script { public static object Main() { var t = System.Type.GetType(\"VRCinema.CinemaNavigationManager, Assembly-CSharp\"); if (t == null) return \"type not found\"; var m = t.GetMethod(\"GoToObject\", BindingFlags.NonPublic | BindingFlags.Instance); return \"CinemaNavigationManager found=\" + (t != null) + \" GoToObject found=\" + (m != null); } }"}
EOF
npx --yes unity-mcp-cli run-tool script-execute --input-file /tmp/task5_verify.json --timeout 90000
```

Expected: `CinemaNavigationManager found=True GoToObject found=True`. This confirms the file compiles and the method still exists under its expected name — it deliberately does not call `GoToObject` on the live scene object, per the Testing Note's rule against invoking real navigation methods.

- [ ] **Step 5: Commit**

```bash
git add Assets/Scripts/VRMenu/CinemaNavigationManager.cs
git commit -m "Stand up on any theater switch, including to the Lobby

Matches the existing unconditional PauseMedia() call right above it -
leaving a seat is about where you're leaving, not where you're headed."
```

---

## Self-Review

**Spec coverage:**
- "Point-and-teleport at any seat" → Task 2 (`LuxurySeatWireup`) + reused `Seat`/`SeatManager`. ✓
- "Standing movement disabled while seated" → Task 1 (`PlayerManager.SetStandingLocomotionEnabled` fix). ✓
- "Stand Up" menu button → Task 4. ✓
- Walkable-surface collider gap → Task 2's `AddWalkableColliders()`. ✓
- Switching theaters while seated stands the player up → Task 5. ✓
- All edge cases in the spec (second-seat-without-standing, re-entry, "Stand Up" while not seated, Lobby no-op) are already handled by unmodified `SeatManager.RequestSit` / `PlayerManager.StandUp` — no additional task needed; covered by inspection, not a new test, since the reused code is unmodified. ✓
- Out-of-scope items (Home Theater seating, occupancy persistence, multiplayer) — no task touches them. ✓

**Placeholder scan:** no TBD/TODO. Task 4's button-wiring mechanism was directly verified live against the real scene before being written into this plan (`CloseButton`'s actual component list and its exact persistent listener — `VRMenuController.CloseMenu()` — were both queried, not assumed), so no fallback/investigation step was needed there.

**Type consistency:** `PlayerManager.Instance`, `.StandUp()`, `.SitAt()`, `.xrRig` all match `PlayerManager.cs` as read verbatim in this session. `SeatManager.Register()`, `.Seats`, `.RequestSit()` match `SeatManager.cs` as read verbatim. `Seat.Init()`, `.SitAnchor` match `Seat.cs` as read verbatim. `LuxurySeatWireup` has no external consumers beyond its own scene-wiring, so no cross-task signature risk there.
