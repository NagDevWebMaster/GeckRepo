// =============================================================================
//  GeckoCinemaLocomotion.cs
//
//  Lets the player walk and turn around the cinema auditorium using the real
//  XR Interaction Toolkit (com.unity.xr.interaction.toolkit 3.5.1) - left stick
//  moves, right stick snap-turns.
//
//  Put this on BrowserPlane, alongside CinemaScreenAligner. No Inspector wiring
//  needed beyond dragging the same 'auditorium' reference.
//
//  Why this drives XRI's providers with ManualValue instead of Input Actions
//  ---------------------------------------------------------------------------
//  XRI 3.x's current (non-deprecated) ContinuousMoveProvider and SnapTurnProvider
//  read input through an XRInputValueReader<Vector2>, normally backed by an
//  InputActionReference asset you wire up in the Inspector. Building and
//  cross-referencing a correct .inputactions asset from a script, sight unseen,
//  is exactly the kind of thing that fails silently if one binding path is
//  slightly wrong - and this project has no way to test that short of trial and
//  error on-device.
//
//  XRInputValueReader<Vector2> has a documented escape hatch for precisely this
//  situation: InputSourceMode.ManualValue, which reads whatever you assign to
//  .manualValue that frame. This script reads the sticks the exact same way
//  GeckoPointerInput already does - InputDevices.GetDevicesWithCharacteristics,
//  CommonUsages.primary2DAxis, the pattern already verified working on-device in
//  this project - and pushes the result into the providers' manualValue every
//  frame. Same proven input path, still the real XRI locomotion pipeline
//  (LocomotionMediator -> XRBodyTransformer -> XROrigin), no Input Actions asset
//  to get wrong.
//
//  The one real input collision, and how it's resolved
//  -----------------------------------------------------
//  GeckoPointerInput already uses each hand's stick to scroll the page while
//  that hand's ray is resting on the browser plane. Binding turn to the right
//  stick would collide with that: aiming at the page and pushing up to scroll
//  could also register as a turn. Turn input from a hand is zeroed while
//  GeckoPointerInput.IsHandOnPage(that hand) is true, so scrolling always wins.
//  Move (left stick, by default) is not gated, since walking away is exactly
//  what you'd want to be able to do at any time, including mid-scroll.
//
//  Containment, not physics
//  -------------------------
//  No CharacterController, no colliders on the model's 2349 nodes (that would be
//  real physics cost for no benefit here - see CinemaScreenAligner's own
//  reasoning on why only the screen node needs a real collider). Instead this
//  computes the auditorium's overall world-space bounds once, and clamps the
//  rig's XZ position inside them (shrunk by wallMargin) plus locks Y to the
//  floor every frame. Cheap, robust, and correct for a static blockout - you
//  can still walk through individual seats, which is an acceptable first pass;
//  say so if you want real per-object collision later.
// =============================================================================

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit.Locomotion;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Movement;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Turning;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Readers;
using Unity.XR.CoreUtils;

[RequireComponent(typeof(CinemaScreenAligner))]
public class GeckoCinemaLocomotion : MonoBehaviour
{
    [Header("Model")]
    [Tooltip("Same auditorium reference as CinemaScreenAligner - used to compute " +
             "room bounds for containment. Leave empty to read it off " +
             "CinemaScreenAligner automatically.")]
    public Transform auditorium;

    [Tooltip("Wait for CinemaScreenAligner.IsAligned before enabling movement, so " +
             "the rig starts seated correctly instead of fighting the initial " +
             "seating placement.")]
    public bool waitForAlignment = true;
    public float waitTimeoutSeconds = 8f;

    [Header("Rig")]
    [Tooltip("Manual override if auto-detection (topmost parent of Camera.main) " +
             "picks the wrong object.")]
    public Transform xrRigOverride;

    [Header("Move (left stick)")]
    public bool enableMove = true;
    [Tooltip("Metres per second.")]
    public float moveSpeed = 2.2f;
    public bool enableStrafe = true;
    [Tooltip("Move relative to where the HEAD is looking, not the hand - the usual " +
             "VR convention, avoids disorientation when you point one way and walk.")]
    public bool useHeadRelativeForward = true;
    [Range(0f, 0.5f)] public float moveDeadzone = 0.12f;

    [Header("Turn (right stick, snap)")]
    public bool enableTurn = true;
    [Tooltip("Degrees per snap.")]
    public float snapTurnAmount = 45f;
    [Range(0f, 0.9f)] public float turnDeadzone = 0.5f;

    [Header("Containment")]
    [Tooltip("Keep the rig inside the auditorium's own bounds. Off = free-fly " +
             "through walls, useful for debugging alignment.")]
    public bool clampToRoomBounds = true;
    [Tooltip("Shrink the room bounds by this much so the player stops short of " +
             "the walls rather than clipping into them.")]
    public float wallMargin = 0.4f;
    [Tooltip("Keep the rig standing on the model's deck every frame. The floor is " +
             "sampled per position, not fixed at Start - the seating is raked in " +
             "0.19m risers, so one constant height is only right for the front row.")]
    public bool lockFloorHeight = true;

    [Tooltip("Metres per second the view rises or falls when the deck height " +
             "changes. Snapping instantly between risers reads as a jolt in a headset.")]
    public float stepSmoothSpeed = 8f;

    [Tooltip("Refuse a move that would drop the rig further than maxStepDown, or " +
             "onto nothing at all. This is what stops you walking off the back of " +
             "the top riser - a 2.34m fall to the carpet plane underneath.")]
    public bool blockLedgeDrops = true;
    public float maxStepDown = 0.6f;

    [Header("Debug")]
    public bool verboseLogging = true;

    private const string Tag = "[GeckoVulkanBridge] ";

    private CinemaScreenAligner _aligner;
    private GeckoPointerInput _pointerInput;
    private Transform _rig;
    private XROrigin _xrOrigin;
    private LocomotionMediator _mediator;
    private ContinuousMoveProvider _moveProvider;
    private SnapTurnProvider _turnProvider;

    private bool _boundsReady;
    private Bounds _roomBounds;
    private float _floorY;

    private CinemaFloorSampler _floor;
    private float _currentFloorY;      // smoothed, so risers are a step not a jump
    private Vector2 _lastGroundedXZ;   // last spot with real floor under it

    private static readonly List<InputDevice> s_Devices = new List<InputDevice>();

    private void Awake()
    {
        _aligner = GetComponent<CinemaScreenAligner>();
        _pointerInput = GetComponent<GeckoPointerInput>();   // optional; null-checked at use
        if (auditorium == null) auditorium = _aligner.auditorium;
    }

    private void Start() => StartCoroutine(SetUpWhenReady());

    /// <summary>
    /// Re-runs room-bounds/floor setup against a different auditorium - the
    /// counterpart to <see cref="CinemaScreenAligner.Realign"/> for anything
    /// that reuses this BrowserPlane (and the locomotion on it) across more
    /// than one auditorium. Standalone usage that only ever sets
    /// <see cref="auditorium"/> before Start() is unaffected.
    /// </summary>
    public void Reinitialize(Transform newAuditorium)
    {
        auditorium = newAuditorium;
        _boundsReady = false;
        _floor = null;
        StopAllCoroutines();
        StartCoroutine(SetUpWhenReady());
    }

    private IEnumerator SetUpWhenReady()
    {
        if (waitForAlignment)
        {
            float waited = 0f;
            while (!_aligner.IsAligned && waited < waitTimeoutSeconds)
            {
                waited += Time.deltaTime;
                yield return null;
            }
            if (!_aligner.IsAligned)
                Debug.LogWarning(Tag + "locomotion: CinemaScreenAligner never reported " +
                                 "IsAligned within the timeout - enabling locomotion " +
                                 "anyway, but the rig may not be seated correctly.");
        }

        _rig = xrRigOverride != null ? xrRigOverride : FindRigRoot();
        if (_rig == null)
        {
            Debug.LogError(Tag + "locomotion: no XR rig found (Camera.main is null " +
                           "or unparented). Assign 'xrRigOverride' manually.");
            yield break;
        }

        SetUpXROrigin();
        SetUpLocomotion();
        ComputeRoomBounds();
        SetUpFloor();

        Log($"ready: rig='{_rig.name}' moveSpeed={moveSpeed} turn={snapTurnAmount}deg " +
            $"bounds={(_boundsReady ? _roomBounds.ToString() : "n/a")} " +
            $"floorPieces={(_floor != null ? _floor.PieceCount : 0)} " +
            $"standingY={_currentFloorY:F2}");
    }

    // -------------------------------------------------------------------------
    private Transform FindRigRoot()
    {
        if (Camera.main == null) return null;
        Transform t = Camera.main.transform;
        while (t.parent != null) t = t.parent;
        return t;
    }

    /// <summary>
    /// Adds XROrigin to the existing rig hierarchy rather than replacing it -
    /// preserves everything already tuned (GeckoPointerInput's trackingSpace
    /// auto-detect, CinemaScreenAligner's own rig discovery) since both climb
    /// the SAME Camera.main parent chain this reuses.
    /// </summary>
    private void SetUpXROrigin()
    {
        _xrOrigin = _rig.GetComponent<XROrigin>();
        if (_xrOrigin == null) _xrOrigin = _rig.gameObject.AddComponent<XROrigin>();

        _xrOrigin.Origin = _rig.gameObject;
        _xrOrigin.Camera = Camera.main;

        // "Camera Offset" is Main Camera's parent, one level below the rig root,
        // in this project's existing hierarchy (Rig > Camera Offset > Main Camera).
        Transform floorOffset = Camera.main != null ? Camera.main.transform.parent : null;
        _xrOrigin.CameraFloorOffsetObject = floorOffset != null ? floorOffset.gameObject : _rig.gameObject;
    }

    private void SetUpLocomotion()
    {
        // GetComponentInChildren, not GetComponent: this project's XR rig ships its
        // LocomotionMediator/XRBodyTransformer and move/turn providers on child
        // objects ("Locomotion", "Locomotion/Move", "Locomotion/Turn"), not on the
        // rig root itself. Looking only at the root found nothing there and added a
        // SECOND, independent LocomotionMediator (RequireComponent pulls in its own
        // XRBodyTransformer) straight onto the root - two XRBodyTransformers both
        // reconciling the same tracked camera into the same CharacterController
        // every frame, which is what read as "the room follows my head" and
        // "can't move" (the two fought each other to a standstill). Finding and
        // reusing the existing ones keeps this to the one rig the scene already has.
        _mediator = _rig.GetComponentInChildren<LocomotionMediator>(true);
        if (_mediator == null) _mediator = _rig.gameObject.AddComponent<LocomotionMediator>();
        // RequireComponent(XRBodyTransformer) on LocomotionMediator means Awake
        // has already run and the body transformer exists by the time AddComponent
        // returns, so this assignment is safe immediately.
        _mediator.xrOrigin = _xrOrigin;

        // DynamicMoveProvider (XRI Starter Assets, already on "Locomotion/Move")
        // derives from ContinuousMoveProvider, so this finds and reconfigures it
        // rather than adding a second, competing move provider.
        _moveProvider = _rig.GetComponentInChildren<ContinuousMoveProvider>(true);
        if (_moveProvider == null) _moveProvider = _rig.gameObject.AddComponent<ContinuousMoveProvider>();
        _moveProvider.moveSpeed = moveSpeed;
        _moveProvider.enableStrafe = enableStrafe;
        _moveProvider.enableFly = false;
        _moveProvider.forwardSource = useHeadRelativeForward && Camera.main != null
            ? Camera.main.transform : null;
        _moveProvider.leftHandMoveInput.inputSourceMode = XRInputValueReader.InputSourceMode.ManualValue;
        _moveProvider.rightHandMoveInput.inputSourceMode = XRInputValueReader.InputSourceMode.Unused;
        _moveProvider.enabled = enableMove;

        _turnProvider = _rig.GetComponentInChildren<SnapTurnProvider>(true);
        if (_turnProvider == null) _turnProvider = _rig.gameObject.AddComponent<SnapTurnProvider>();
        _turnProvider.turnAmount = snapTurnAmount;
        _turnProvider.leftHandTurnInput.inputSourceMode = XRInputValueReader.InputSourceMode.Unused;
        _turnProvider.rightHandTurnInput.inputSourceMode = XRInputValueReader.InputSourceMode.ManualValue;
        _turnProvider.enabled = enableTurn;
    }

    /// <summary>
    /// Combined world-space bounds of every renderer under the model, once. Not
    /// per-object collision - see the file header for why that's an intentional
    /// scope cut, not an oversight.
    /// </summary>
    private void ComputeRoomBounds()
    {
        if (auditorium == null)
        {
            Debug.LogWarning(Tag + "locomotion: no auditorium reference - containment disabled.");
            _boundsReady = false;
            return;
        }

        var renderers = auditorium.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
        {
            _boundsReady = false;
            return;
        }

        Bounds b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);

        _roomBounds = b;
        _floorY = auditorium.position.y;
        _boundsReady = true;
    }

    /// <summary>
    /// Reuses the sampler CinemaScreenAligner already built (same model, same
    /// surfaces - no reason to walk 2349 nodes twice), and only builds its own if
    /// alignment was skipped. Seeds the smoothed height from wherever the rig was
    /// left, so the first frame does not lerp up from zero.
    /// </summary>
    private void SetUpFloor()
    {
        _floor = _aligner != null ? _aligner.Floor : null;
        if (_floor == null && auditorium != null)
            _floor = new CinemaFloorSampler(auditorium, _floorY);

        Vector3 p = _rig.position;
        _lastGroundedXZ = new Vector2(p.x, p.z);
        _currentFloorY = (_floor != null && _floor.PieceCount > 0)
            ? _floor.Sample(p.x, p.z)
            : p.y;
    }

    // -------------------------------------------------------------------------
    private void Update()
    {
        if (_moveProvider == null || _turnProvider == null) return;

        if (enableMove)
        {
            Vector2 move = ReadStick(useRightHand: false, out bool moveDeviceValid);
            if (!moveDeviceValid || move.magnitude < moveDeadzone) move = Vector2.zero;
            _moveProvider.leftHandMoveInput.manualValue = move;
        }

        if (enableTurn)
        {
            bool rightBusyOnPage = _pointerInput != null && _pointerInput.IsHandOnPage(true);
            Vector2 turn = rightBusyOnPage
                ? Vector2.zero
                : ReadStick(useRightHand: true, out bool turnDeviceValid);
            if (!rightBusyOnPage && (turn.magnitude < turnDeadzone)) turn = Vector2.zero;
            _turnProvider.rightHandTurnInput.manualValue = turn;
        }

        if (_rig == null) return;

        // Order matters: clamp XZ against the walls first, then resolve the ground
        // under wherever that left us. Doing it the other way round would sample
        // the floor at a position the wall clamp is about to reject.
        if (clampToRoomBounds && _boundsReady) ClampRigToRoom();
        if (lockFloorHeight) StickToFloor();
    }

    private void ClampRigToRoom()
    {
        Vector3 p = _rig.position;
        Vector3 min = _roomBounds.min + new Vector3(wallMargin, 0f, wallMargin);
        Vector3 max = _roomBounds.max - new Vector3(wallMargin, 0f, wallMargin);

        p.x = Mathf.Clamp(p.x, Mathf.Min(min.x, max.x), Mathf.Max(min.x, max.x));
        p.z = Mathf.Clamp(p.z, Mathf.Min(min.z, max.z), Mathf.Max(min.z, max.z));

        if (p != _rig.position) _rig.position = p;
    }

    /// <summary>
    /// Plants the rig on the deck under its current XZ. Height is only ever set
    /// here - the sticks drive XZ, and the rake decides Y.
    /// </summary>
    private void StickToFloor()
    {
        Vector3 p = _rig.position;

        if (_floor == null || _floor.PieceCount == 0)
        {
            // No surfaces to sample: the old flat-floor behaviour, which is still
            // right for a model without a rake.
            p.y = _floorY;
            if (p != _rig.position) _rig.position = p;
            return;
        }

        float target = _floor.Sample(p.x, p.z, out bool supported);

        // Walked onto nothing, or off a drop taller than a riser: rewind XZ to the
        // last spot that had floor under it and hold the height we had there.
        bool ledge = blockLedgeDrops && (_currentFloorY - target) > maxStepDown;
        if (!supported || ledge)
        {
            p.x = _lastGroundedXZ.x;
            p.z = _lastGroundedXZ.y;
            target = _currentFloorY;
        }
        else
        {
            _lastGroundedXZ = new Vector2(p.x, p.z);
        }

        _currentFloorY = Mathf.MoveTowards(_currentFloorY, target,
                                           stepSmoothSpeed * Time.deltaTime);
        p.y = _currentFloorY;

        if (p != _rig.position) _rig.position = p;
    }

    /// <summary>Same device-resolution pattern as GeckoPointerInput.TryGetHandDevice.</summary>
    private Vector2 ReadStick(bool useRightHand, out bool deviceValid)
    {
        var wanted = InputDeviceCharacteristics.HeldInHand | InputDeviceCharacteristics.Controller |
                     (useRightHand ? InputDeviceCharacteristics.Right : InputDeviceCharacteristics.Left);

        InputDevices.GetDevicesWithCharacteristics(wanted, s_Devices);
        InputDevice device = default;
        deviceValid = false;
        if (s_Devices.Count > 0 && s_Devices[0].isValid) { device = s_Devices[0]; deviceValid = true; }
        else
        {
            InputDevices.GetDevicesAtXRNode(useRightHand ? XRNode.RightHand : XRNode.LeftHand, s_Devices);
            if (s_Devices.Count > 0 && s_Devices[0].isValid) { device = s_Devices[0]; deviceValid = true; }
        }

        if (!deviceValid) return Vector2.zero;
        return device.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 v) ? v : Vector2.zero;
    }

    private void Log(string m) { if (verboseLogging) Debug.Log(Tag + "locomotion: " + m); }
}
