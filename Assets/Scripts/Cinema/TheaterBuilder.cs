// =============================================================================
//  TheaterBuilder.cs
//
//  Builds one modular theater at runtime: an auditorium, entrance/exit pads,
//  and an instance of the reusable BrowserPlane prefab - same runtime-
//  construction convention this project already uses for GeckoPageKeyboard/
//  GeckoScreenCurver/GeckoBumpyGround rather than hand-authored scene geometry,
//  so "modular" theaters are just different Inspector values on this component.
//
//  Two ways to build the auditorium
//  ---------------------------------
//  If auditoriumModelPrefab is assigned (Assets/Models/CinemaAuditorium.glb),
//  that real model is instantiated and CinemaScreenAligner/GeckoCinemaLocomotion
//  run their FULL original pipeline against it - seat-row culling, seating
//  centroid, floor sampling, GPU instancing, atmosphere - all the functionality
//  those scripts were originally written for, not just the naming-convention
//  subset a placeholder needs. Every seat mesh under it (nodes containing
//  "SeatBase") gets a collider and a Seat component so it's individually
//  clickable, same as a placeholder seat.
//
//  With no model assigned, BuildPlaceholder() generates simple primitive
//  geometry using the SAME node names ("ProjectionScreen", "Seat_r_c" /
//  "SeatBase", "Floor") so the theater still works end to end without the
//  asset - useful for testing layouts without loading the real (2356-node)
//  model twice.
// =============================================================================

using UnityEngine;

[RequireComponent(typeof(TheaterManager))]
[RequireComponent(typeof(SeatManager))]
public class TheaterBuilder : MonoBehaviour
{
    [Header("Real auditorium model (preferred)")]
    [Tooltip("Assign the imported CinemaAuditorium.glb root to build a real theater. " +
             "Leave empty to fall back to placeholder primitive geometry below.")]
    public GameObject auditoriumModelPrefab;

    [Tooltip("Node name (contains-match) for an individual seat's click target in " +
             "the real model - matches CinemaScreenAligner's own seatNodeNameContains.")]
    public string modelSeatNodeContains = "SeatBase";

    [Tooltip("How far behind the model's back row (in local Z) to put the entrance/" +
             "exit pads. Measured from CinemaAuditorium.glb: seats span roughly " +
             "local Z -18.6 to -33, screen near Z -12, audience faces +Z.")]
    public float modelBackWallZ = -37f;

    [Header("Real model alignment overrides")]
    [Tooltip("Overrides the BrowserPlane instance's CinemaScreenAligner.screenNodeName " +
             "for this theater. Every theater instantiates the SAME BrowserPlane prefab, " +
             "so a model with different node names than the prefab's own default " +
             "(\"ProjectionScreen\") needs this set or CinemaScreenAligner can't find its " +
             "screen. Blank = leave the prefab's own default in place.")]
    public string screenNodeNameOverride = "";

    [Tooltip("Overrides CinemaScreenAligner.anchorNodeName (default \"StageApron\") the " +
             "same way - the node the browser's bottom edge rests on.")]
    public string anchorNodeNameOverride = "";

    [Tooltip("Overrides CinemaScreenAligner.seatNodeNameContains (default \"SeatBase\") the " +
             "same way - used to find the seating centroid for camera placement. Usually " +
             "matches modelSeatNodeContains above.")]
    public string seatNodeNameOverride = "";

    [Header("Placeholder layout (used only without a model)")]
    public int rows = 3;
    public int seatsPerRow = 5;
    public float seatSpacing = 0.9f;
    public float rowSpacing = 1.4f;
    [Tooltip("Stadium-style step-up in height per row, back rows higher.")]
    public float rowRise = 0.22f;
    [Tooltip("Distance from the screen wall to the front row.")]
    public float frontRowDistance = 3f;
    public float screenWidth = 6f;
    public float screenHeight = 2.4f;

    [Header("Prefab")]
    [Tooltip("The reusable BrowserPlane prefab (Assets/Prefabs/BrowserPlane.prefab).")]
    public GameObject browserPlanePrefab;

    private TheaterManager _theater;
    private SeatManager _seats;

    private void Awake()
    {
        _theater = GetComponent<TheaterManager>();
        _seats = GetComponent<SeatManager>();
        Build();
    }

    private void Build()
    {
        _theater.auditoriumRoot = transform;
        _theater.seats = _seats;

        if (auditoriumModelPrefab != null) BuildFromRealModel();
        else BuildPlaceholder();

        BuildBrowserPlane();
    }

    // =========================================================================
    // Real model path
    // =========================================================================
    private void BuildFromRealModel()
    {
        var model = Instantiate(auditoriumModelPrefab, transform);
        model.name = "Auditorium";
        model.transform.localPosition = Vector3.zero;
        model.transform.localRotation = Quaternion.identity;

        WireUpRealSeats(model.transform);
        BuildPad("EntrancePad", new Vector3(0f, 0f, modelBackWallZ),
                 new Color(0.10f, 0.30f, 0.55f), enterTheater: _theater, exitsToLobby: false,
                 facingIntoRoomZ: 1f);
        BuildPad("ExitPad", new Vector3(3f, 0f, modelBackWallZ),
                 new Color(0.45f, 0.12f, 0.12f), enterTheater: null, exitsToLobby: true,
                 facingIntoRoomZ: 1f);
    }

    /// <summary>
    /// Adds a click target to every seat mesh in the real model. Real seat
    /// nodes are plain glTF meshes with no collider, so one is sized from the
    /// node's own renderer bounds - not a fixed guess - meaning it fits
    /// whatever the actual seat mesh looks like without per-seat tuning.
    /// </summary>
    private void WireUpRealSeats(Transform auditorium)
    {
        int wired = 0;
        foreach (var t in auditorium.GetComponentsInChildren<Transform>(true))
        {
            if (!t.name.Contains(modelSeatNodeContains)) continue;
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
            // World-space, not local: individual seat nodes may carry their own
            // baked rotation from the source scene, and this project's whole
            // audience-faces-+Z convention (matched to the real model's own
            // screen/seat layout) should hold regardless of that.
            anchor.transform.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);

            var seat = t.gameObject.AddComponent<Seat>();
            seat.Init(_seats, anchor.transform);
            _seats.Register(seat);
            wired++;
        }

        if (wired == 0)
            Debug.LogWarning("[Cinema] " + name + ": no nodes containing '" +
                             modelSeatNodeContains + "' found in the auditorium model - " +
                             "no seats are selectable.");
    }

    // =========================================================================
    // Placeholder path (no model assigned)
    // =========================================================================
    private void BuildPlaceholder()
    {
        BuildFloor();
        BuildScreen();
        BuildSeats();
        BuildEntranceAndExitPads();
    }

    private void BuildFloor()
    {
        float depth = frontRowDistance + rows * rowSpacing + 1.5f;
        float width = Mathf.Max(screenWidth, seatsPerRow * seatSpacing) + 2f;

        // Matches CinemaFloorSampler.ParseNames(floorNodeNames) - "Floor" is
        // one of the default names GeckoCinemaLocomotion looks for.
        var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.name = "Floor";
        floor.transform.SetParent(transform, false);
        floor.transform.localPosition = new Vector3(0f, 0f, depth * 0.5f - 0.75f);
        floor.transform.localScale = new Vector3(width / 10f, 1f, depth / 10f);
        floor.GetComponent<Renderer>().sharedMaterial = MakeMat(new Color(0.10f, 0.10f, 0.12f));
    }

    private void BuildScreen()
    {
        // Placeholder for CinemaScreenAligner to find by name and align the
        // real BrowserPlane against - it reads this node's world bounds for
        // size/aspect, then disables its Renderer (hidePlaceholderScreen).
        var screen = GameObject.CreatePrimitive(PrimitiveType.Quad);
        screen.name = "ProjectionScreen";
        screen.transform.SetParent(transform, false);
        screen.transform.localPosition = new Vector3(0f, screenHeight * 0.5f, 0f);
        screen.transform.localRotation = Quaternion.identity;
        screen.transform.localScale = new Vector3(screenWidth, screenHeight, 1f);
        screen.GetComponent<Renderer>().sharedMaterial = MakeMat(new Color(0.02f, 0.02f, 0.02f));
        Destroy(screen.GetComponent<Collider>());
    }

    private void BuildSeats()
    {
        Material seatMat = MakeMat(new Color(0.15f, 0.45f, 0.20f));

        for (int r = 0; r < rows; r++)
        {
            float z = frontRowDistance + r * rowSpacing;
            float y = r * rowRise;
            float rowWidth = (seatsPerRow - 1) * seatSpacing;

            for (int c = 0; c < seatsPerRow; c++)
            {
                float x = -rowWidth * 0.5f + c * seatSpacing;
                BuildSeat(r, c, new Vector3(x, y, z), seatMat);
            }
        }
    }

    private void BuildSeat(int row, int col, Vector3 localPos, Material seatMat)
    {
        // Name contains "Seat" - CinemaScreenAligner's seatRowNodeContains.
        var seatGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
        seatGo.name = $"Seat_{row}_{col}";
        seatGo.transform.SetParent(transform, false);
        seatGo.transform.localPosition = localPos + new Vector3(0f, 0.25f, 0f);
        seatGo.transform.localScale = new Vector3(seatSpacing * 0.8f, 0.5f, 0.8f);
        seatGo.GetComponent<Renderer>().sharedMaterial = seatMat;

        Destroy(seatGo.GetComponent<MeshCollider>());
        var box = seatGo.AddComponent<BoxCollider>();
        box.size = Vector3.one;

        // Name contains "SeatBase" - CinemaScreenAligner's seatNodeNameContains,
        // used to find the seating centroid.
        var baseGo = new GameObject("SeatBase");
        baseGo.transform.SetParent(seatGo.transform, false);
        baseGo.transform.localPosition = Vector3.zero;

        var anchor = new GameObject("SitAnchor");
        anchor.transform.SetParent(seatGo.transform, false);
        anchor.transform.localPosition = new Vector3(0f, 0.55f, 0f);
        // Seats face -Z (back toward the screen at z=0); rotation is local to
        // this theater's own root, so it holds regardless of where the theater
        // itself is placed in the world.
        anchor.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);

        seatGo.AddComponent<CinemaClickable>();
        var seat = seatGo.AddComponent<Seat>();
        seat.Init(_seats, anchor.transform);
        _seats.Register(seat);
    }

    private void BuildEntranceAndExitPads()
    {
        float backRowZ = frontRowDistance + Mathf.Max(0, rows - 1) * rowSpacing;

        // Just outside the seating area, facing in - this is what CinemaManager's
        // lobby navigation clicks to activate this theater.
        BuildPad("EntrancePad", new Vector3(0f, 0f, frontRowDistance - 1.5f),
                 new Color(0.10f, 0.30f, 0.55f), enterTheater: _theater, exitsToLobby: false,
                 facingIntoRoomZ: 1f);

        // Behind the back row, facing out - the "leave" pad inside the theater.
        BuildPad("ExitPad", new Vector3(0f, 0f, backRowZ + 1.5f),
                 new Color(0.45f, 0.12f, 0.12f), enterTheater: null, exitsToLobby: true,
                 facingIntoRoomZ: -1f);
    }

    // =========================================================================
    // Shared
    // =========================================================================
    private void BuildBrowserPlane()
    {
        if (browserPlanePrefab == null)
        {
            Debug.LogError("[Cinema] TheaterBuilder on " + name +
                           " has no browserPlanePrefab assigned - this theater has no screen.");
            return;
        }

        var instance = Instantiate(browserPlanePrefab, transform);
        instance.name = "BrowserPlane";

        // Matches CinemaScreenAligner.AlignTo's own convention exactly: local
        // +Y (the Plane mesh's normal) faces the audience, local +Z runs up
        // the wall, local +X is width - a Plane at identity rotation instead
        // lies flat with its normal pointing straight up, which is what an
        // inactive (not yet realigned) theater would show without this.
        // CinemaScreenAligner's own alignment on activation computes the same
        // pose from the real seating centroid and matches this, not fights it.
        instance.transform.localPosition = new Vector3(0f, screenHeight * 0.5f, 0f);
        instance.transform.localRotation = Quaternion.LookRotation(Vector3.up, Vector3.forward);
        instance.transform.localScale = new Vector3(screenWidth / 10f, 1f, screenHeight / 10f);

        var adapter = instance.GetComponent<BrowserPlaneAdapter>();
        if (adapter == null)
        {
            Debug.LogError("[Cinema] BrowserPlane prefab has no BrowserPlaneAdapter.");
            return;
        }

        var aligner = instance.GetComponent<CinemaScreenAligner>();
        if (aligner != null)
        {
            if (!string.IsNullOrWhiteSpace(screenNodeNameOverride)) aligner.screenNodeName = screenNodeNameOverride;
            if (!string.IsNullOrWhiteSpace(anchorNodeNameOverride)) aligner.anchorNodeName = anchorNodeNameOverride;
            if (!string.IsNullOrWhiteSpace(seatNodeNameOverride)) aligner.seatNodeNameContains = seatNodeNameOverride;
        }

        var screenComp = GetComponent<MovieScreen>();
        if (screenComp == null) screenComp = gameObject.AddComponent<MovieScreen>();
        screenComp.adapter = adapter;
        _theater.screen = screenComp;

        // Inactive until CinemaManager activates this theater - BeginInitialise()
        // is never called, so no native bridge exists for this instance yet.
        var renderer = instance.GetComponent<GeckoVulkanRenderer>();
        if (renderer != null) renderer.autoInitialize = false;

        var locomotion = instance.GetComponent<GeckoCinemaLocomotion>();
        if (locomotion != null) locomotion.enabled = false;
    }

    private void BuildPad(string padName, Vector3 localPos, Color color,
                          TheaterManager enterTheater, bool exitsToLobby,
                          float facingIntoRoomZ)
    {
        var pad = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        pad.name = padName;
        pad.transform.SetParent(transform, false);
        pad.transform.localPosition = localPos;
        pad.transform.localScale = new Vector3(0.6f, 0.02f, 0.6f);
        pad.GetComponent<Renderer>().sharedMaterial = MakeMat(color);

        Destroy(pad.GetComponent<MeshCollider>());
        var box = pad.AddComponent<BoxCollider>();
        box.size = Vector3.one;

        pad.AddComponent<CinemaClickable>();
        var teleport = pad.AddComponent<TeleportPad>();
        teleport.enterTheater = enterTheater;
        teleport.exitsToLobby = exitsToLobby;
        // Land a step further into the room from the pad itself, facing the
        // seats/screen, rather than exactly on top of the marker.
        // facingIntoRoomZ is +1 or -1: which local Z direction is "further
        // into the room" for this theater's own geometry - the placeholder
        // path and the real model disagree on this (see class header), so it
        // is never assumed here.
        var dest = new GameObject(padName + "_Destination");
        dest.transform.SetParent(transform, false);
        dest.transform.localPosition = localPos +
            new Vector3(0f, 0f, facingIntoRoomZ * (exitsToLobby ? 1f : 1.5f));
        dest.transform.localRotation = facingIntoRoomZ > 0f
            ? Quaternion.identity
            : Quaternion.Euler(0f, 180f, 0f);
        teleport.destination = dest.transform;
    }

    private static Material MakeMat(Color color)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
        {
            Debug.LogWarning("[Cinema] URP Lit shader not found - placeholder geometry " +
                             "will render with whatever Unity's default material is.");
            return null;
        }
        var mat = new Material(shader);
        mat.SetColor("_BaseColor", color);
        return mat;
    }
}
