// =============================================================================
//  CinemaScreenAligner.cs
//
//  Snaps the browser plane onto the auditorium model's own ProjectionScreen, and
//  fixes the two things that would otherwise make this model unusable on Quest.
//
//  Put this on BrowserPlane, drag the imported CinemaAuditorium instance into
//  'auditorium', press play.
//
//  Why align instead of eyeballing it
//  ----------------------------------
//  The GLB already defines the screen:
//      node ProjectionScreen  translation [0, 5.85, -0.08]  scale [26, 1, 10.5]
//  Reading that at runtime means the browser lands exactly where the model's
//  designer intended, and keeps working if the model is re-exported.
//
//  glTF is Y-up right-handed, Unity is Y-up LEFT-handed, so the importer negates
//  Z. Rather than redo that conversion by hand, this copies the world transform
//  of the imported object - whatever the importer decided is authoritative.
//
//  Why the CAMERA also has to move
//  --------------------------------
//  This model is authored at real-world scale - the walls alone span dozens of
//  metres. Earlier versions of this script only moved the browser plane to the
//  model's ProjectionScreen, leaving the XR rig wherever it started. On a small
//  prop that is harmless; on a full-size auditorium the screen can land tens of
//  metres from the player, which reads as "nothing is aligned" even though the
//  plane snapped exactly where it was told to. AlignRigToSeating() moves the rig
//  (not the Camera itself - OpenXR overwrites that every frame) to the centroid
//  of the model's own seat nodes, facing the screen, so the room, the screen and
//  the player end up in the same place.
//
//  The two performance problems
//  ----------------------------
//  1. 2349 mesh nodes but only 18 unique meshes / 16 materials, and 14k triangles
//     total. The triangles are nothing; 2349 draw calls is far too many for
//     Quest. Enabling GPU instancing on the materials collapses that, since
//     identical mesh+material pairs batch into one call.
//  2. Six punctual lights imported from Blender at watt-scale intensities
//     (13044, 9783, 8153...). glTFast converts units, but URP's mobile renderer
//     also caps additional lights per object - so they are re-ranged and culled
//     down to something a tiler can actually shade.
// =============================================================================

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[RequireComponent(typeof(GeckoVulkanRenderer))]
public class CinemaScreenAligner : MonoBehaviour
{
    [Header("Model")]
    [Tooltip("The imported CinemaAuditorium instance in the scene.")]
    public Transform auditorium;

    [Tooltip("Name of the screen node inside the model.")]
    public string screenNodeName = "ProjectionScreen";

    [Tooltip("Wait this long for a runtime glTF load to finish before giving up. " +
             "0 = the model is already in the scene (editor import).")]
    public float waitForModelSeconds = 5f;

    [Header("Alignment")]
    [Tooltip("Nudge the browser toward the audience so it never z-fights with the " +
             "model's own screen quad.")]
    public float forwardOffset = 0.05f;

    [Tooltip("Hide the model's flat screen once the browser takes its place.")]
    public bool hidePlaceholderScreen = true;

    [Tooltip("Match the plane's proportions to the model's screen (26 x 10.5 = " +
             "2.476:1). Set your texture to the same aspect or the page stretches.")]
    public bool matchScreenAspect = true;

    [Tooltip("Stand the browser on top of this node instead of centring it on the " +
             "screen node. Blank = use the screen's own centre. 'StageApron' rests " +
             "the bottom edge on the stage lip at y=0.30.")]
    public string anchorNodeName = "StageApron";

    [Header("Performance")]
    [Tooltip("2349 mesh nodes is the real cost here, not the 14k triangles. " +
             "Instancing collapses identical mesh+material pairs into one call.")]
    public bool enableGpuInstancing = true;

    [Tooltip("Merge the remaining geometry into combined meshes at runtime. Note " +
             "this replaces the old 'isStatic = true', which did nothing at all: " +
             "static batching is baked from the Editor's static flags at BUILD time, " +
             "so setting the flag from a running script is ignored. Off by default " +
             "because it trades memory for draw calls and GPU instancing (above) " +
             "already collapses this model's 18 meshes well.")]
    public bool combineStaticMeshes = false;

    [Header("Cull (performance)")]
    [Tooltip("Switch off whole categories of clutter. This model is 2356 objects but " +
             "only 14k triangles, so the cost is per-renderer overhead, not geometry - " +
             "Unity culls and submits every one of them each frame. CeilingTile alone " +
             "is 1350 objects (57% of the model) worth 2 triangles each, directly " +
             "above your head in a room where you are looking at a screen.")]
    public bool cullClutter = true;

    [Tooltip("Node-name fragments to disable outright (comma separated). Anything " +
             "load-bearing is protected automatically - the screen, the anchor, the " +
             "seats and every walkable surface cannot be culled even if listed here.")]
    public string cullNodeNames = "CeilingTile,HVACGrille,Diffuser,EquipRack,CableRaceway";

    [Header("Seating rows")]
    [Tooltip("Keep only this many rows of seats and switch the rest off. 0 = keep all " +
             "12. The model's 864 seat objects are the largest remaining cost once the " +
             "ceiling is culled, and they are three meshes repeated 288 times each.")]
    public int keepSeatRows = 0;

    [Tooltip("Count the kept rows from the back of the rake (the high end, furthest " +
             "from the screen) rather than the front.")]
    public bool keepRowsFromBack = true;

    [Tooltip("Every seat node, not just the SeatBase ones used for the spawn point.")]
    public string seatRowNodeContains = "Seat";

    [Tooltip("Distance along the viewing axis within which seats count as one row. " +
             "A row's own back/base/frame are ~0.24m apart here while adjacent rows " +
             "are 1.3m apart, so anything between the two works.")]
    public float seatRowTolerance = 0.6f;

    [Tooltip("Blender exports lights in watts; the imported intensities are in the " +
             "thousands. Rescale them to something URP mobile can shade.")]
    public bool tameLights = true;
    [Range(0f, 5f)] public float lightIntensity = 1.2f;

    [Tooltip("How many of the model's 6 ceiling point lights to keep. They are the " +
             "'AreaLight_N' nodes and they import at Blender watt scale (13044, 9783, " +
             "8152...). URP shades additional point lights per object, so on a tiler " +
             "each one is real per-pixel cost across everything it touches. In a " +
             "darkened cinema the screen is the light source: 2 is enough to read the " +
             "room, 0 leaves only ambient and the screen.")]
    [Range(0, 8)] public int maxLights = 2;

    [Header("Atmosphere")]
    [Tooltip("Everything outside the auditorium becomes solid black: no skybox, " +
             "no ambient sky, camera clears to voidColor.")]
    public bool blackVoidBackground = true;
    public Color voidColor = Color.black;

    [Tooltip("Fade distant geometry into the void so the auditorium's far end " +
             "dissolves instead of ending on a hard silhouette.")]
    public bool fogFalloff = true;
    public float fogDensity = 0.018f;

    [Tooltip("Ambient level. Near-black keeps it feeling like a darkened cinema; " +
             "raise slightly if the seats disappear entirely.")]
    public Color ambient = new Color(0.05f, 0.05f, 0.06f);

    [Tooltip("Switch off CinematicLakeEnvironment if it is on this object - the " +
             "lake and the cinema would occupy the same space.")]
    public bool disableLakeEnvironment = true;

    [Header("Camera Seating")]
    [Tooltip("Move the XR rig into the model's seating area once alignment " +
             "succeeds. Without this the browser plane snaps to the model's real " +
             "screen position but the player is left wherever they started - which " +
             "can be tens of metres away in a full-scale auditorium.")]
    public bool alignCameraToSeating = true;

    [Tooltip("Manual override if auto-detection picks the wrong object. Auto-detect " +
             "walks up from Camera.main to the topmost parent (the XR rig root).")]
    public Transform xrRigOverride;

    [Tooltip("Node-name fragment used to find seats, e.g. 'SeatBase' (case-" +
             "insensitive, matches any of the 288 seat nodes). Their average world " +
             "position, projected to floor height, becomes the rig's target spot.")]
    public string seatNodeNameContains = "SeatBase";

    [Tooltip("Used only if no matching seat nodes are found: distance in front of " +
             "the screen to place the rig instead.")]
    public float fallbackViewDistance = 8f;

    [Header("Floor")]
    [Tooltip("Stand on the model's own stepped deck instead of assuming one flat " +
             "floor at the auditorium's origin. The seating rake here climbs to " +
             "2.34m, so a flat assumption leaves the camera underneath the steps.")]
    public bool standOnRakedFloor = true;

    [Tooltip("Node-name fragments treated as walkable surface (comma separated).")]
    public string floorNodeNames = "Step,Carpet,Aisle,Floor,Stage,Riser,Platform";

    [Tooltip("Fragments excluded even if they match the list above - the LED strips " +
             "sit proud of the step tops and are not something you stand on.")]
    public string floorExcludeNames = "LED,Light";

    [Header("Fallback (no model found)")]
    [Tooltip("If 'auditorium' is unassigned or its screen node can't be found, reset " +
             "BrowserPlane to a sane camera-relative pose instead of leaving it at a " +
             "stale position from a previous run against a since-removed model.")]
    public bool resetBrowserOnMissingModel = true;
    public float defaultViewDistance = 3f;
    public float defaultEyeHeight = 1.6f;

    [Header("Debug")]
    public bool verboseLogging = true;

    /// <summary>
    /// True once AlignTo/AlignRigToSeating have both run successfully. Polled by
    /// GeckoCinemaLocomotion so it doesn't compute room bounds or enable movement
    /// against a rig position that's about to be overwritten by seating alignment.
    /// </summary>
    public bool IsAligned { get; private set; }

    /// <summary>
    /// Floor-height lookup over the model, built once alignment starts. Shared with
    /// GeckoCinemaLocomotion so the per-frame ground check and this spawn placement
    /// agree on where the deck is. Null until the model has been found.
    /// </summary>
    public CinemaFloorSampler Floor { get; private set; }

    private const string Tag = "[GeckoVulkanBridge] ";

    private void Start() => StartCoroutine(AlignWhenReady());

    /// <summary>
    /// Re-runs alignment against a different auditorium - the entry point for
    /// anything that reuses this BrowserPlane across more than one auditorium
    /// instead of pointing <see cref="auditorium"/> at a fixed model once at
    /// scene start (a cinema system re-purposing one screen for whichever
    /// theater the player just entered, for example). Standalone usage that
    /// only ever sets <see cref="auditorium"/> before Start() is unaffected -
    /// this is purely additive.
    /// </summary>
    public void Realign(Transform newAuditorium)
    {
        auditorium = newAuditorium;
        IsAligned = false;
        StopAllCoroutines();
        StartCoroutine(AlignWhenReady());
    }

    private IEnumerator AlignWhenReady()
    {
        if (auditorium == null)
        {
            Debug.LogError(Tag + "cinema: 'auditorium' is not assigned. Drag the " +
                           "imported CinemaAuditorium object into the field.");
            if (resetBrowserOnMissingModel) ResetBrowserPlaneToDefault();
            yield break;
        }

        // A runtime glTF load populates children over a few frames.
        Transform screen = null;
        float waited = 0f;
        do
        {
            screen = FindDeep(auditorium, screenNodeName);
            if (screen != null) break;
            waited += Time.deltaTime;
            yield return null;
        } while (waited < waitForModelSeconds);

        if (screen == null)
        {
            Debug.LogError(Tag + $"cinema: no child named '{screenNodeName}' under " +
                           $"{auditorium.name}. Check the imported hierarchy - the " +
                           "GLB defines it at node 1399.");
            if (resetBrowserOnMissingModel) ResetBrowserPlaneToDefault();
            yield break;
        }

        // The seating centroid is needed BEFORE the browser is placed: it is what
        // tells us which side of the screen the audience is on, and therefore which
        // way the page has to face. Deriving that from the model instead of from
        // screen.up keeps this correct whichever way the importer left the screen
        // node's normal pointing.
        _screen = screen;

        // Rows go first: SeatingCentroid below only counts seats that are still
        // active, so culling here is what makes the player spawn in the rows they
        // kept rather than in the middle of a block of switched-off ones.
        CullSeatRows(screen);

        Vector3 seatingPoint = SeatingCentroid(screen);

        AlignTo(screen, seatingPoint);
        BuildFloorSampler();
        Optimise();
        ApplyAtmosphere();
        if (alignCameraToSeating) AlignRigToSeating(screen, seatingPoint);
        IsAligned = true;
    }

    /// <summary>
    /// Gathers the model's walkable surfaces so both the spawn placement below and
    /// GeckoCinemaLocomotion's per-frame ground check read the same deck heights.
    /// Built unconditionally (not gated on alignCameraToSeating) because locomotion
    /// needs it even when the rig was placed by hand.
    /// </summary>
    private void BuildFloorSampler()
    {
        if (!standOnRakedFloor) { Floor = null; return; }

        Floor = new CinemaFloorSampler(
            auditorium,
            auditorium.position.y,
            CinemaFloorSampler.ParseNames(floorNodeNames),
            CinemaFloorSampler.ParseNames(floorExcludeNames));

        if (Floor.PieceCount == 0)
        {
            Debug.LogWarning(Tag + $"cinema: no walkable surfaces matched " +
                             $"'{floorNodeNames}' under {auditorium.name} - falling " +
                             $"back to a flat floor at y={auditorium.position.y:F2}. " +
                             "On a raked auditorium that puts the camera inside the steps.");
        }
        else
        {
            Log($"floor sampler: {Floor.PieceCount} walkable surfaces");
        }
    }

    /// <summary>Deck height at an XZ, or the auditorium's own Y if there is no sampler.</summary>
    private float FloorHeightAt(Vector3 worldPoint)
    {
        if (Floor == null || Floor.PieceCount == 0) return auditorium.position.y;
        return Floor.Sample(worldPoint.x, worldPoint.z);
    }

    /// <summary>
    /// Moves the XR rig root to the centroid of the model's seat nodes, facing the
    /// screen. Moves the RIG, never Camera.main directly - OpenXR drives the
    /// camera's local pose from tracking every frame, so a manual position set on
    /// it would be overwritten before the next frame renders.
    /// </summary>
    /// <summary>
    /// Average world position of the model's seat nodes - the audience's side of the
    /// room, and the only thing here that says which way the screen must face.
    ///
    /// The fallback deliberately does NOT use screen.up. This model's ProjectionScreen
    /// node carries its own 90-degree stand-up rotation, and after the importer's
    /// handedness flip its normal ends up pointing at the back wall, away from the
    /// seats - so "in front of the screen" along screen.up is behind it. Stepping
    /// toward the auditorium's own centre of mass instead cannot get the side wrong.
    /// </summary>
    private Vector3 SeatingCentroid(Transform screen)
    {
        var all = new List<Transform>();
        CollectByNameContains(auditorium, seatNodeNameContains, all);

        // Only seats that are actually still there. CollectByNameContains walks the
        // Transform hierarchy, which includes inactive objects, so without this the
        // centroid would average in rows CullSeatRows just switched off and drop the
        // player halfway down an empty rake.
        var seats = new List<Transform>();
        foreach (var s in all) if (s.gameObject.activeInHierarchy) seats.Add(s);

        if (seats.Count > 0)
        {
            Vector3 sum = Vector3.zero;
            foreach (var s in seats) sum += s.position;
            Vector3 rawAverage = sum / seats.Count;

            // Snap to the nearest ACTUAL seat rather than trusting the raw average.
            // A model with mirrored left/right seat blocks either side of a centre
            // aisle (e.g. LuxuryTheater's Seat_L_*/Seat_R_*) averages X back to ~0 -
            // the aisle itself, not a seat - and averaging Z across every row can
            // land between two rows rather than on either. Both the aisle strip and
            // the gap between risers can be lower/unsupported ground compared to the
            // seat rows either side, so spawning at the raw average can drop the rig
            // below the deck the seats are actually standing on. The nearest real
            // seat is always on solid, correctly-heighted riser ground.
            Transform nearest = seats[0];
            float bestSqrDist = float.MaxValue;
            foreach (var s in seats)
            {
                float dx = s.position.x - rawAverage.x;
                float dz = s.position.z - rawAverage.z;
                float sqrDist = dx * dx + dz * dz;
                if (sqrDist < bestSqrDist) { bestSqrDist = sqrDist; nearest = s; }
            }

            Log($"seating centroid from {seats.Count} active '{seatNodeNameContains}' " +
                $"nodes (of {all.Count}): raw average={rawAverage}, snapped to nearest " +
                $"seat '{nearest.name}' = {nearest.position}");
            return nearest.position;
        }

        Vector3 roomCentre = RoomCentre();
        Vector3 outward = roomCentre - screen.position;
        outward.y = 0f;
        outward = outward.sqrMagnitude > 1e-4f ? outward.normalized : Vector3.forward;

        Debug.LogWarning(Tag + $"cinema: no nodes containing '{seatNodeNameContains}' " +
                         $"found - falling back to {fallbackViewDistance}m from the " +
                         "screen toward the room's centre. Check the model's seat names.");
        return screen.position + outward * fallbackViewDistance;
    }

    /// <summary>
    /// Switches off every seat row except the <see cref="keepSeatRows"/> nearest the
    /// back of the rake.
    ///
    /// Rows are found by projecting each seat onto the screen-to-audience axis and
    /// clustering, rather than by reading a Z coordinate. Two reasons: the axis is
    /// derived from the model so it survives the importer's handedness flip and any
    /// rotation on the prefab instance, and a physical row is NOT a single
    /// coordinate - the seat backs sit 0.24m behind their own bases, so this model's
    /// 12 rows appear as 24 distinct Z values. Clustering with a tolerance between
    /// 0.24 and 1.3 collapses them back into the 12 real rows.
    /// </summary>
    private void CullSeatRows(Transform screen)
    {
        if (keepSeatRows <= 0) return;

        var seats = new List<Transform>();
        CollectByNameContains(auditorium, seatRowNodeContains, seats);
        if (seats.Count == 0)
        {
            Debug.LogWarning(Tag + $"cinema: no nodes containing '{seatRowNodeContains}' " +
                             "- no seat rows to cull.");
            return;
        }

        // Provisional audience direction, used only to rank rows. The real centroid
        // is recomputed from the survivors afterwards.
        Vector3 sum = Vector3.zero;
        foreach (var s in seats) sum += s.position;
        Vector3 axis = (sum / seats.Count) - screen.position;
        axis.y = 0f;
        axis = axis.sqrMagnitude > 1e-4f ? axis.normalized : Vector3.forward;

        var keyed = new List<KeyValuePair<float, Transform>>(seats.Count);
        foreach (var s in seats)
            keyed.Add(new KeyValuePair<float, Transform>(
                Vector3.Dot(s.position - screen.position, axis), s));
        keyed.Sort((a, b) => a.Key.CompareTo(b.Key));

        var rows = new List<List<Transform>>();
        float last = float.NegativeInfinity;
        foreach (var kv in keyed)
        {
            if (rows.Count == 0 || kv.Key - last > seatRowTolerance)
                rows.Add(new List<Transform>());
            rows[rows.Count - 1].Add(kv.Value);
            last = kv.Key;
        }

        int keep = Mathf.Clamp(keepSeatRows, 0, rows.Count);
        int firstKept = keepRowsFromBack ? rows.Count - keep : 0;
        int lastKept  = firstKept + keep - 1;

        int off = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            if (i >= firstKept && i <= lastKept) continue;
            foreach (var t in rows[i]) { t.gameObject.SetActive(false); off++; }
        }

        Log($"seat rows: {rows.Count} found, kept {keep} from the " +
            $"{(keepRowsFromBack ? "back" : "front")}, switched off {off} of {seats.Count} objects");
    }

    /// <summary>Centre of every renderer in the model - used only as an audience-side hint.</summary>
    private Vector3 RoomCentre()
    {
        var renderers = auditorium.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return auditorium.position;

        Bounds b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
        return b.center;
    }

    /// <summary>
    /// Puts the player back in the middle seat facing the screen. Bound to a
    /// controller button by GeckoControllerInput - after walking around, "reset my
    /// view" is the one piece of locomotion every VR app is expected to have.
    /// Safe to call before alignment finishes; it simply does nothing.
    /// </summary>
    public void RecenterRig()
    {
        if (!IsAligned || _screen == null)
        {
            Debug.LogWarning(Tag + "cinema: recenter ignored - alignment has not run yet.");
            return;
        }
        AlignRigToSeating(_screen, SeatingCentroid(_screen));
        Log("recentred to the seating centroid");
    }

    private Transform _screen;

    private void AlignRigToSeating(Transform screen, Vector3 seatingPoint)
    {
        Transform rig = xrRigOverride != null ? xrRigOverride : FindRigRoot();
        if (rig == null)
        {
            Debug.LogWarning(Tag + "cinema: no XR rig found to move (Camera.main is " +
                             "null or unparented). Assign 'xrRigOverride' manually.");
            return;
        }

        // Stand ON the deck at that spot, not at the auditorium's origin - the rake
        // climbs to 2.34m and the seating centroid is most of the way up it, so a
        // flat origin height would spawn the camera underneath the steps. The rig's
        // own child hierarchy (tracked HMD pose) supplies eye height on top of this.
        float floorY = FloorHeightAt(seatingPoint);
        Vector3 targetPos = new Vector3(seatingPoint.x, floorY, seatingPoint.z);

        Vector3 toScreen = screen.position - targetPos;
        toScreen.y = 0f;
        Quaternion targetRot = toScreen.sqrMagnitude > 0.0001f
            ? Quaternion.LookRotation(toScreen.normalized, Vector3.up)
            : rig.rotation;

        rig.SetPositionAndRotation(targetPos, targetRot);
        Log($"rig '{rig.name}' moved to {targetPos} (deck y={floorY:F2}), " +
            $"yaw={targetRot.eulerAngles.y:F1} deg (facing screen)");
    }

    /// <summary>
    /// Puts the browser back to a sane, camera-relative pose when there is no model
    /// to align to. Without this the plane is left wherever a PREVIOUS run (possibly
    /// against a since-removed model) put it - which is exactly how a plane ends up
    /// tens of metres from the camera with nothing left to explain why.
    /// </summary>
    private void ResetBrowserPlaneToDefault()
    {
        Transform rig = xrRigOverride != null ? xrRigOverride : FindRigRoot();
        if (rig == null)
        {
            Debug.LogWarning(Tag + "cinema: cannot reset BrowserPlane - no XR rig found.");
            return;
        }

        Vector3 flatForward = Vector3.ProjectOnPlane(rig.forward, Vector3.up);
        if (flatForward.sqrMagnitude < 1e-4f) flatForward = Vector3.forward;
        flatForward.Normalize();

        Vector3 targetPos = rig.position + flatForward * defaultViewDistance
                             + Vector3.up * defaultEyeHeight;

        // Face back toward the rig using the same n/uiUp convention as the rest of
        // this project (transform.up is the Plane primitive's normal in world space).
        Vector3 n = -flatForward;
        Vector3 uiUp = Vector3.ProjectOnPlane(Vector3.up, n).normalized;
        if (uiUp.sqrMagnitude < 1e-4f) uiUp = Vector3.forward;
        Quaternion targetRot = Quaternion.LookRotation(uiUp, n);

        transform.SetPositionAndRotation(targetPos, targetRot);
        transform.localScale = Vector3.one;

        Debug.LogWarning(Tag + $"cinema: no auditorium/screen to align to - reset " +
                         $"BrowserPlane to {targetPos} facing the rig, instead of " +
                         "leaving it at a stale position.");
    }

    private Transform FindRigRoot()
    {
        if (Camera.main == null) return null;
        Transform t = Camera.main.transform;
        while (t.parent != null) t = t.parent;
        return t;
    }

    private static void CollectByNameContains(Transform root, string fragment, List<Transform> results)
    {
        if (root.name.IndexOf(fragment, System.StringComparison.OrdinalIgnoreCase) >= 0)
            results.Add(root);
        for (int i = 0; i < root.childCount; i++)
            CollectByNameContains(root.GetChild(i), fragment, results);
    }

    /// <summary>
    /// Black void outside the model.
    ///
    /// Deliberately NOT a blur: a full-screen blur on Quest costs an extra render
    /// target plus a multi-tap pass per eye every frame, and blurred peripheral
    /// geometry in a headset reads as bad eyesight rather than depth of field.
    /// Clearing to black and letting fog swallow the far geometry gets the
    /// cinema-in-a-void look at zero cost.
    /// </summary>
    private void ApplyAtmosphere()
    {
        // The lake builds in Awake, which runs before this Start, so the built
        // hierarchy already exists and has to go too - not just the component.
        if (disableLakeEnvironment)
        {
            var lake = GetComponent<CinematicLakeEnvironment>();
            if (lake != null && lake.enabled)
            {
                lake.enabled = false;
                Log("disabled CinematicLakeEnvironment");
            }
            var built = GameObject.Find("CinematicLake");
            if (built != null) { Destroy(built); Log("removed generated lake hierarchy"); }
        }

        if (blackVoidBackground)
        {
            RenderSettings.skybox = null;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = ambient;
            DynamicGI.UpdateEnvironment();

            // Every camera, not just Camera.main - in XR there may be more than one.
            foreach (var cam in Camera.allCameras)
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = voidColor;
            }
        }

        RenderSettings.fog = fogFalloff;
        if (fogFalloff)
        {
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = voidColor;      // fade INTO the void, not away from it
            RenderSettings.fogDensity = fogDensity;
        }

        Log($"atmosphere: void={ColorUtility.ToHtmlStringRGB(voidColor)} " +
            $"fog={(fogFalloff ? fogDensity.ToString("F3") : "off")} " +
            $"ambient={ColorUtility.ToHtmlStringRGB(ambient)}");
    }

    /// <summary>
    /// Stands the browser plane up facing the audience, sized to the model's screen
    /// and resting on the anchor node.
    ///
    /// Why none of this copies the screen node's transform any more
    /// ------------------------------------------------------------
    /// It used to do 'rotation = screen.rotation * Euler(-90,0,0)', on the stated
    /// assumption that the screen node is a flat unit_plane lying in XZ exactly like
    /// Unity's Plane primitive, so the browser's own -90 still applied. Half of that
    /// is true - the MESH is a flat unit_plane with a +Y normal - but the NODE also
    /// carries a 90-degree X rotation that already stands it up. Applying -90 on top
    /// cancels it: the browser ended up horizontal, face down, 5.85m in the air, seen
    /// edge-on from every seat. That is the "not visible inside the cinema" symptom.
    ///
    /// The scale was wrong for the same reason. A vertical screen's world AABB is
    /// 26 x 10.5 x ~0 - width on X, height on Y, and essentially no depth on Z - so
    /// 'localScale = (size.x/10, 1, size.z/10)' set the height from the DEPTH and
    /// collapsed the plane to zero height. It would have been invisible even after
    /// the rotation was fixed.
    ///
    /// So instead of trusting either the node's rotation or a fixed axis mapping,
    /// this builds the pose from two things that cannot be ambiguous: world up, and
    /// the direction to the audience. That survives a re-export or an importer that
    /// resolves handedness differently.
    /// </summary>
    private void AlignTo(Transform screen, Vector3 audiencePoint)
    {
        Bounds screenBounds = WorldBounds(screen);

        // Which way the page has to face: horizontally, from the screen toward the seats.
        Vector3 toAudience = audiencePoint - screenBounds.center;
        toAudience.y = 0f;
        toAudience = toAudience.sqrMagnitude > 1e-4f ? toAudience.normalized : Vector3.forward;

        // Unity's Plane mesh lies in local XZ with its normal on local +Y, so
        // LookRotation(forward: world up, upwards: the normal) stands it up:
        // local +Y faces the audience, local +Z runs up the wall, local +X is width.
        // Same convention ResetBrowserPlaneToDefault already uses.
        transform.rotation = Quaternion.LookRotation(Vector3.up, toAudience);

        // Width is the screen's extent across the viewing direction, height is simply
        // its world Y extent - both read off the AABB without assuming an axis.
        Vector3 right = Vector3.Cross(Vector3.up, toAudience).normalized;
        Vector3 size = screenBounds.size;
        float width  = Mathf.Abs(size.x * right.x) + Mathf.Abs(size.z * right.z);
        float height = size.y;

        if (matchScreenAspect && width > 0.0001f && height > 0.0001f)
        {
            // Unity's Plane mesh is 10x10 units, and after the rotation above its
            // local X is the width and its local Z is the height.
            transform.localScale = new Vector3(width / 10f, 1f, height / 10f);
        }
        else
        {
            height = 10f * transform.localScale.z;   // keep the anchor maths honest
        }

        // Anchor: rest the bottom edge on top of the named node (the stage lip) rather
        // than centring on the screen, so the page sits inside the proscenium.
        Transform anchor = string.IsNullOrWhiteSpace(anchorNodeName)
            ? null : FindDeep(auditorium, anchorNodeName);

        Vector3 pos;
        if (anchor != null)
        {
            Bounds ab = WorldBounds(anchor);
            pos = new Vector3(ab.center.x, ab.max.y + height * 0.5f, ab.center.z);
            Log($"anchored to '{anchor.name}': top y={ab.max.y:F2}, " +
                $"browser centre y={pos.y:F2} (bottom edge resting on it)");
        }
        else
        {
            pos = screenBounds.center;
            if (!string.IsNullOrWhiteSpace(anchorNodeName))
                Debug.LogWarning(Tag + $"cinema: anchor node '{anchorNodeName}' not found " +
                                 "- centring on the screen node instead.");
        }

        // Nudge toward the audience, not along screen.up - which on this model points
        // at the back wall and would bury the page inside it.
        transform.position = pos + toAudience * forwardOffset;

        var browser = GetComponent<GeckoVulkanRenderer>();
        float modelAspect = height > 0.0001f ? width / height : 0f;
        float texAspect = browser.SurfaceHeight > 0
            ? (float)browser.SurfaceWidth / browser.SurfaceHeight : 0f;

        Log($"aligned to '{screen.name}': {width:F2}m x {height:F2}m aspect={modelAspect:F3} " +
            $"facing={toAudience} pos={transform.position} scale={transform.localScale}");

        if (texAspect > 0f && modelAspect > 0f && Mathf.Abs(texAspect - modelAspect) > 0.12f)
            Debug.LogWarning(Tag + $"cinema: screen aspect {modelAspect:F2} but texture " +
                             $"is {browser.SurfaceWidth}x{browser.SurfaceHeight} " +
                             $"({texAspect:F2}). The page will stretch - set the " +
                             $"texture to roughly {Mathf.RoundToInt(1080 * modelAspect)}x1080.");

        if (hidePlaceholderScreen)
        {
            var r = screen.GetComponent<Renderer>();
            if (r != null) r.enabled = false;
        }
    }

    /// <summary>
    /// World-space bounds of a node, including its children - the anchor and screen
    /// nodes may or may not carry the renderer themselves depending on how the
    /// importer split multi-primitive meshes.
    /// </summary>
    private static Bounds WorldBounds(Transform t)
    {
        var renderers = t.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return new Bounds(t.position, t.lossyScale);

        Bounds b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
        return b;
    }

    /// <summary>
    /// Names that must survive culling whatever the user types into cullNodeNames:
    /// the screen drives alignment, the anchor positions the browser, the seats give
    /// the spawn point, and the walkable surfaces are what CinemaFloorSampler reads
    /// to keep the player on the floor. Culling any of those would not just look
    /// wrong, it would drop the camera through the deck.
    /// </summary>
    private bool IsProtected(string nodeName)
    {
        if (Match(nodeName, screenNodeName)) return true;
        if (Match(nodeName, anchorNodeName)) return true;
        if (Match(nodeName, seatNodeNameContains)) return true;

        var floor = CinemaFloorSampler.ParseNames(floorNodeNames)
                    ?? CinemaFloorSampler.DefaultFloorNames;
        foreach (var f in floor) if (Match(nodeName, f)) return true;

        return false;
    }

    private static bool Match(string name, string fragment)
        => !string.IsNullOrWhiteSpace(fragment) &&
           name.IndexOf(fragment, System.StringComparison.OrdinalIgnoreCase) >= 0;

    private void Optimise()
    {
        int renderers = 0, materials = 0, lights = 0, culled = 0, protectedHits = 0;
        var seenMats = new HashSet<Material>();

        string[] cullList = cullClutter ? CinemaFloorSampler.ParseNames(cullNodeNames) : null;
        var live = new List<GameObject>();

        foreach (var r in auditorium.GetComponentsInChildren<Renderer>(true))
        {
            // Already switched off by CullSeatRows - don't count it as live, and
            // above all don't feed it to StaticBatchingUtility, which would bake the
            // hidden geometry straight back into the combined mesh.
            if (!r.gameObject.activeInHierarchy) continue;

            if (cullList != null)
            {
                bool wanted = false;
                foreach (var frag in cullList) if (Match(r.name, frag)) { wanted = true; break; }

                if (wanted)
                {
                    if (IsProtected(r.name)) protectedHits++;
                    else
                    {
                        // SetActive, not just renderer.enabled: a disabled GameObject
                        // leaves Unity's culling loop entirely, which is the whole
                        // point when the problem is 1350 objects rather than triangles.
                        r.gameObject.SetActive(false);
                        culled++;
                        continue;
                    }
                }
            }

            renderers++;
            // Nothing here casts meaningful shadows and shadow maps are expensive
            // on a tiler; the model is a flat-shaded blockout.
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;

            if (combineStaticMeshes) live.Add(r.gameObject);

            if (enableGpuInstancing)
                foreach (var m in r.sharedMaterials)
                    if (m != null && seenMats.Add(m))
                    {
                        m.enableInstancing = true;
                        materials++;
                    }
        }

        if (culled > 0)
            Log($"culled {culled} clutter objects ({cullNodeNames})" +
                (protectedHits > 0 ? $"; kept {protectedHits} protected ones" : ""));

        if (combineStaticMeshes && live.Count > 0)
        {
            // The real runtime equivalent of the static flag. Must run AFTER culling
            // so the combined mesh does not bake in geometry we just switched off.
            StaticBatchingUtility.Combine(live.ToArray(), auditorium.gameObject);
            Log($"static-batched {live.Count} renderers into combined meshes");
        }

        if (tameLights)
        {
            var all = auditorium.GetComponentsInChildren<Light>(true);
            for (int i = 0; i < all.Length; i++)
            {
                var l = all[i];
                if (i >= maxLights) { l.enabled = false; continue; }
                l.intensity = lightIntensity;
                l.shadows = LightShadows.None;
                if (l.type == LightType.Point && l.range < 1f) l.range = 20f;
                lights++;
            }
            Log($"lights: kept {lights} of {all.Length} at intensity {lightIntensity}");
        }

        Log($"optimised: {renderers} renderers live ({culled} culled), " +
            $"instancing on {materials} materials, combined={combineStaticMeshes}");
    }

    private static Transform FindDeep(Transform root, string name)
    {
        if (root.name == name) return root;
        for (int i = 0; i < root.childCount; i++)
        {
            var f = FindDeep(root.GetChild(i), name);
            if (f != null) return f;
        }
        return null;
    }

    private void Log(string m) { if (verboseLogging) Debug.Log(Tag + "cinema: " + m); }
}
