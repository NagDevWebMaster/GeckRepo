// =============================================================================
//  VRMenuController.cs
//
//  Shows, hides and places the floating cinema menu.
//
//  Placement rule: the menu belongs to the PLAYER, not to the headset. It is
//  re-aimed in front of the eyes when it opens, then smoothly follows position
//  and rotation every frame (LateUpdate, followViewer) so it stays in front of
//  you as you walk and look around - unless you've grabbed dragHandle/
//  resizeHandle, which suspends following for the rest of the open session.
//
//  Nothing here touches locomotion, the XR Origin, or the browser plane.
//
//  Drag and resize
//  ----------------
//  Two optional handles - dragHandle and resizeHandle - are plain
//  XRSimpleInteractables. Grabbing one selects it via XRI's Select action,
//  which every other Gecko script in this project deliberately leaves bound
//  to grip alone (see GeckoXRInteraction's header comment), so wiring drag
//  and resize off grip cannot collide with the trigger-driven page/menu
//  clicks. Holding dragHandle moves menuAnchor 1:1 with the hand, offset by
//  wherever on the handle you grabbed. Holding resizeHandle scales the menu
//  by the ratio of the hand's current distance from menuAnchor to its
//  distance at the moment of the grab - pull away to grow, pull in to
//  shrink. Both are position-only: rotation always stays under
//  PlaceInFrontOfPlayer/LateUpdate's control, never the hand's.
//
//  Grabbing either handle sets _userPositioned, which suspends the
//  followViewer auto-recenter for the rest of this open session - otherwise
//  a menu dragged out to the side would immediately drift back the moment
//  you turned enough to trip the recenter angle. OpenMenu() still calls
//  PlaceInFrontOfPlayer() as before, so the NEXT time the menu opens it is
//  back at arm's length; only the resized scale carries over between opens.
// =============================================================================

using System.Collections;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace VRCinema
{
    public class VRMenuController : MonoBehaviour
    {
        [Header("Wiring")]
        [SerializeField, Tooltip("The menu object to show and hide. Disabled, never destroyed.")]
        private GameObject menuRoot;

        [SerializeField, Tooltip("Transform the menu hangs from. Usually this component's own object.")]
        private Transform menuAnchor;

        [SerializeField, Tooltip("The player's head. Camera.main is used when empty.")]
        private Transform playerCamera;

        [SerializeField, Tooltip("Fades the panel in and out. Optional.")]
        private CanvasGroup canvasGroup;

        [Header("Placement")]
        [SerializeField, Range(0.5f, 2.5f), Tooltip("Metres in front of the eyes.")]
        private float menuDistance = 1.0f;

        [SerializeField, Range(-0.8f, 0.5f), Tooltip("Height relative to eye level, in metres.")]
        private float heightOffset = -0.1f;

        [Header("Comfort")]
        [SerializeField, Tooltip("Keep the menu smoothly following in front of you as you move and look around.")]
        private bool followViewer = true;

        [SerializeField, Range(1f, 20f)] private float followSharpness = 5f;

        [Header("Animation")]
        [SerializeField, Range(0.05f, 0.6f)] private float animationDuration = 0.2f;

        [Header("Testing")]
        [SerializeField, Tooltip("Open the menu automatically at Start(), instead of waiting for " +
                                 "the controller's menu button. For bring-up testing when the " +
                                 "button binding itself is what's being diagnosed - turn this back " +
                                 "off once the button is confirmed working.")]
        private bool openOnStart = false;

        [Header("Drag & Resize")]
        [SerializeField, Tooltip("Grip this to move the whole menu. Leave empty to disable dragging.")]
        private XRSimpleInteractable dragHandle;

        [SerializeField, Tooltip("Grip this and pull away from the menu to grow it, push closer to " +
                                 "shrink it. Leave empty to disable resizing.")]
        private XRSimpleInteractable resizeHandle;

        [SerializeField, Range(0.4f, 1f)] private float minScale = 0.6f;
        [SerializeField, Range(1f, 3f)] private float maxScale = 1.8f;

        private Coroutine _animation;
        private bool _isOpen;

        // Set once a drag or resize grab happens, so LateUpdate's followViewer
        // logic stops fighting the player's manual placement for the rest of
        // this open session.
        private bool _userPositioned;

        private Transform _dragInteractor;
        private Vector3 _dragOffset;

        private Transform _resizeInteractor;
        private float _resizeGrabDistance;
        private float _resizeGrabScale;
        private float _scaleMultiplier = 1f;
        private float _currentOpenAmount = 1f;

        // The menu root is the world-space Canvas, whose authored scale is tiny
        // (800 units wide rendered at 1m => 0.00125). Animating to Vector3.one
        // would blow it up 800x into a panel the player stands inside, so the
        // open animation scales RELATIVE to whatever it was built at.
        //
        // This is now a serialized field rather than something captured live
        // from menuRoot.transform.localScale in Awake(). Live-capture seemed
        // convenient - author the Canvas at any scale and the script "just
        // reads" it - but resize can leave menuRoot's actual transform sitting
        // at some mid-drag scale, and if the scene is saved while it is there
        // (an editor mistake, not something gameplay itself can do - Play Mode
        // and a built device app never write back to the scene asset), the
        // NEXT Awake() would capture that mid-resize scale as the new
        // "authored" baseline, compounding on every subsequent save. Set this
        // to match whatever the Canvas is actually authored at in the
        // Inspector; it is never overwritten at runtime.
        [SerializeField, Tooltip("The menu's authored resting scale - e.g. an 800-unit-wide " +
                                 "Canvas built to render 1m wide is 0.00125. Set this to match " +
                                 "the Canvas's own Inspector scale; it is read once, never " +
                                 "written, so a resize can never corrupt it.")]
        private Vector3 builtScale = new Vector3(0.00125f, 0.00125f, 0.00125f);

        private Vector3 _builtScale;

        // Tracks intent, not menuRoot.activeSelf: activeSelf only flips at the end of the
        // close animation, so a request to reopen mid-animation must not be silently
        // dropped for looking "still open" a moment longer than it should.
        public bool IsOpen => _isOpen;

        // ---------------------------------------------------------------------
        private void Awake()
        {
            if (menuAnchor == null) menuAnchor = transform;

            if (menuRoot == null)
            {
                Debug.LogError("VRMenuController: Menu Root is not assigned. " +
                               "Assign the menu object to show and hide.", this);
                enabled = false;
                return;
            }
            if (canvasGroup == null) canvasGroup = menuRoot.GetComponent<CanvasGroup>();
            _builtScale = builtScale;
            if (!TryGetHead(out _))
                Debug.LogWarning("VRMenuController: no Player Camera assigned and Camera.main " +
                                 "is null. The menu will open at the anchor's current pose.", this);

            if (dragHandle != null)
            {
                dragHandle.selectEntered.AddListener(OnDragSelectEntered);
                dragHandle.selectExited.AddListener(OnDragSelectExited);
            }
            if (resizeHandle != null)
            {
                resizeHandle.selectEntered.AddListener(OnResizeSelectEntered);
                resizeHandle.selectExited.AddListener(OnResizeSelectExited);
            }
        }

        private void OnDestroy()
        {
            if (dragHandle != null)
            {
                dragHandle.selectEntered.RemoveListener(OnDragSelectEntered);
                dragHandle.selectExited.RemoveListener(OnDragSelectExited);
            }
            if (resizeHandle != null)
            {
                resizeHandle.selectEntered.RemoveListener(OnResizeSelectEntered);
                resizeHandle.selectExited.RemoveListener(OnResizeSelectExited);
            }
        }

        private void Start()
        {
            // Start hidden, without animating on frame one.
            menuRoot.SetActive(false);
            ApplyOpenAmount(1f);

            if (openOnStart) OpenMenu();
        }

        // ---------------------------------------------------------------------
        public void OpenMenu()
        {
            if (menuRoot == null) return;
            if (_isOpen) return;
            _isOpen = true;

            _userPositioned = false;
            PlaceInFrontOfPlayer();
            menuRoot.SetActive(true);
            Animate(0f, 1f, false);
        }

        public void CloseMenu()
        {
            if (menuRoot == null || !_isOpen) return;
            _isOpen = false;
            Animate(1f, 0f, true);
        }

        public void ToggleMenu()
        {
            if (IsOpen) CloseMenu();
            else OpenMenu();
        }

        // ---------------------------------------------------------------------
        /// <summary>Park the anchor at arm's length ahead, level, facing the player.</summary>
        public void PlaceInFrontOfPlayer()
        {
            if (!TryGetPose(out Vector3 pos, out Quaternion rot)) return;
            menuAnchor.SetPositionAndRotation(pos, rot);
        }

        private bool TryGetHead(out Transform head)
        {
            head = playerCamera != null ? playerCamera
                 : (Camera.main != null ? Camera.main.transform : null);
            return head != null;
        }

        private bool TryGetPose(out Vector3 pos, out Quaternion rot)
        {
            pos = default;
            rot = default;
            if (!TryGetHead(out Transform head)) return false;

            // Level the forward vector. A menu that pitches with your gaze ends up
            // on the floor or the ceiling the moment you glance away.
            Vector3 fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.ProjectOnPlane(head.up, Vector3.up);
            if (fwd.sqrMagnitude < 1e-4f) return false;
            fwd.Normalize();

            pos = head.position + fwd * menuDistance + Vector3.up * heightOffset;
            rot = Quaternion.LookRotation(fwd, Vector3.up);   // panel faces back at the player
            return true;
        }

        private void Update()
        {
            if (_dragInteractor != null)
                menuAnchor.position = _dragInteractor.position + _dragOffset;

            if (_resizeInteractor != null)
            {
                float d = Vector3.Distance(menuAnchor.position, _resizeInteractor.position);
                _scaleMultiplier = Mathf.Clamp(
                    _resizeGrabScale * (d / _resizeGrabDistance), minScale, maxScale);
                ApplyOpenAmount(_currentOpenAmount);
            }
        }

        // ---------------------------------------------------------------------
        private void OnDragSelectEntered(SelectEnterEventArgs args)
        {
            _dragInteractor = args.interactorObject.transform;
            _dragOffset = menuAnchor.position - _dragInteractor.position;
            _userPositioned = true;
        }

        private void OnDragSelectExited(SelectExitEventArgs args) => _dragInteractor = null;

        private void OnResizeSelectEntered(SelectEnterEventArgs args)
        {
            _resizeInteractor = args.interactorObject.transform;
            _resizeGrabDistance = Mathf.Max(
                Vector3.Distance(menuAnchor.position, _resizeInteractor.position), 0.01f);
            _resizeGrabScale = _scaleMultiplier;
            _userPositioned = true;
        }

        private void OnResizeSelectExited(SelectExitEventArgs args) => _resizeInteractor = null;

        private void LateUpdate()
        {
            if (!followViewer || !IsOpen || menuAnchor == null || _userPositioned) return;
            if (!TryGetPose(out Vector3 want, out Quaternion wantRot)) return;

            // Frame-rate independent: a raw Lerp factor moves faster at 120Hz than 72Hz.
            float t = 1f - Mathf.Exp(-followSharpness * Time.unscaledDeltaTime);
            menuAnchor.position = Vector3.Lerp(menuAnchor.position, want, t);
            menuAnchor.rotation = Quaternion.Slerp(menuAnchor.rotation, wantRot, t);
        }

        // ---------------------------------------------------------------------
        private void Animate(float from, float to, bool disableAtEnd)
        {
            if (_animation != null) StopCoroutine(_animation);
            _animation = StartCoroutine(AnimateRoutine(from, to, disableAtEnd));
        }

        private IEnumerator AnimateRoutine(float from, float to, bool disableAtEnd)
        {
            float elapsed = 0f;
            while (elapsed < animationDuration)
            {
                // Unscaled: a paused or slowed game must still be able to close its menu.
                elapsed += Time.unscaledDeltaTime;
                float k = Mathf.SmoothStep(from, to, Mathf.Clamp01(elapsed / animationDuration));
                ApplyOpenAmount(k);
                yield return null;
            }

            ApplyOpenAmount(to);
            if (disableAtEnd)
            {
                menuRoot.SetActive(false);
                ApplyOpenAmount(1f);   // leave it ready for the next open
            }
            _animation = null;
        }

        private void ApplyOpenAmount(float k)
        {
            _currentOpenAmount = k;
            menuRoot.transform.localScale = _builtScale * Mathf.Max(k, 0.0001f) * _scaleMultiplier;
            if (canvasGroup != null) canvasGroup.alpha = k;
        }
    }
}
