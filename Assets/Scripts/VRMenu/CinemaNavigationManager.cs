// =============================================================================
//  CinemaNavigationManager.cs
//
//  Where the menu's buttons actually take you.
//
//  This project does NOT currently have Theater1 / Theater2 / Lobby scenes, and
//  MainCinema is the only scene enabled in the Android build profile. So rather
//  than guessing scene names, this supports both architectures and refuses to
//  invent either:
//
//    SceneLoad            - one scene per destination. Fill in Scene Name and
//                           add the scene to the build profile.
//    GameObjectActivation - one scene, a root object per destination. Fill in
//                           Root, and optionally a Spawn Point to stand at.
//
//  Anything left unconfigured logs exactly what is missing instead of throwing
//  or silently doing nothing.
//
//  GameObjectActivation mode also carries the browser plane between theaters.
//  The Gecko native bridge is a process-wide singleton - GetVulkanImagePointer()
//  and friends take no instance handle - so there is exactly one live web
//  surface in the app. Rather than tearing it down and rebuilding it per
//  theater (which would lose the page), the one BrowserPlane is re-seated onto
//  whichever screen you just walked into, using a per-destination anchor that
//  carries that screen's world position, rotation and size.
// =============================================================================

using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace VRCinema
{
    public class CinemaNavigationManager : MonoBehaviour
    {
        public enum NavigationMode
        {
            SceneLoad,
            GameObjectActivation,
        }

        public enum StartDestination
        {
            Lobby,
            Theater1,
            Theater2,
        }

        [Serializable]
        public class Destination
        {
            [Tooltip("Shown in warnings so you can tell which entry is unconfigured.")]
            public string label = "Destination";

            [Tooltip("SceneLoad mode: exact scene name, and it must be in the build profile.")]
            public string sceneName;

            [Tooltip("GameObjectActivation mode: the root object for this destination.")]
            public GameObject root;

            [Tooltip("GameObjectActivation mode: optional. The rig is moved here on arrival.")]
            public Transform spawnPoint;

            [Tooltip("GameObjectActivation mode: optional. The browser plane is re-seated " +
                     "onto this transform - position, rotation and local scale are copied " +
                     "verbatim, so size the anchor to the screen it sits on.")]
            public Transform browserAnchor;
        }

        [SerializeField, Tooltip("Separate scenes, or one scene with a root object per theater.")]
        private NavigationMode mode = NavigationMode.GameObjectActivation;

        [SerializeField] private Destination theater1 = new Destination { label = "Theater 1" };
        [SerializeField] private Destination theater2 = new Destination { label = "Theater 2" };
        [SerializeField] private Destination lobby = new Destination { label = "Lobby" };

        [SerializeField, Tooltip("GameObjectActivation mode: the rig to reposition at the " +
                                 "spawn point. Leave empty to look up the XR Origin.")]
        private Transform playerRig;

        [SerializeField, Tooltip("Closed automatically after you pick a destination.")]
        private VRMenuController menu;

        [SerializeField, Tooltip("GameObjectActivation mode: the single browser plane moved " +
                                 "between theaters. Leave empty to look one up by its renderer.")]
        private Transform browserPlane;

        [SerializeField, Tooltip("Runs the light-dim + screen-fade beat when arriving at a " +
                                 "theater. Leave empty to look one up in the scene.")]
        private PreShowSequencer preShowSequencer;

        [SerializeField, Tooltip("Activate one destination on load, so the scene does not " +
                                 "start with every theater showing at once.")]
        private bool activateOnStart = true;

        [SerializeField, Tooltip("Which destination the player starts in.")]
        private StartDestination startIn = StartDestination.Lobby;

        // ---------------------------------------------------------------------
        private void Start()
        {
            if (!activateOnStart || mode != NavigationMode.GameObjectActivation) return;
            // Straight to GoToObject, not Go(): there is no menu open yet to close.
            GoToObject(StartingDestination());
        }

        private Destination StartingDestination()
        {
            switch (startIn)
            {
                case StartDestination.Theater1: return theater1;
                case StartDestination.Theater2: return theater2;
                default:                        return lobby;
            }
        }

        // ---------------------------------------------------------------------
        public void LoadTheater1() => Go(theater1);
        public void LoadTheater2() => Go(theater2);
        public void LoadLobby() => Go(lobby);

        // ---------------------------------------------------------------------
        private void Go(Destination d)
        {
            if (d == null)
            {
                Debug.LogError("CinemaNavigationManager: destination is null.", this);
                return;
            }

            bool moved = mode == NavigationMode.SceneLoad ? GoToScene(d) : GoToObject(d);
            if (moved && menu != null) menu.CloseMenu();
        }

        private bool GoToScene(Destination d)
        {
            if (string.IsNullOrWhiteSpace(d.sceneName))
            {
                Debug.LogError($"CinemaNavigationManager: {d.label} scene name is empty. " +
                               "Set it on this component, or switch Mode to " +
                               "GameObjectActivation.", this);
                return false;
            }

            // Catching this here turns a hard runtime failure into an actionable message.
            if (!Application.CanStreamedLevelBeLoaded(d.sceneName))
            {
                Debug.LogError($"CinemaNavigationManager: scene '{d.sceneName}' for {d.label} " +
                               "is not in the build profile, so it cannot be loaded. Add it " +
                               "under File > Build Profiles > Scene List.", this);
                return false;
            }

            SceneManager.LoadScene(d.sceneName);
            return true;
        }

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

            // Only ever touch the destinations this component owns - never sweep the
            // scene, or the browser plane and the rig would get caught in it.
            SetActive(theater1, d);
            SetActive(theater2, d);
            SetActive(lobby, d);

            if (d.spawnPoint != null)
            {
                Transform rig = ResolveRig();
                if (rig != null)
                    rig.SetPositionAndRotation(d.spawnPoint.position, d.spawnPoint.rotation);
                else
                    Debug.LogWarning($"CinemaNavigationManager: {d.label} has a spawn point " +
                                     "but no player rig could be found to move.", this);
            }

            MoveBrowser(d);
            return true;
        }

        /// <summary>
        /// Re-seats the one browser plane onto this destination's screen. The
        /// curved mesh is rebuilt from the new scale - GeckoScreenCurver bakes
        /// world width into its arc and only regenerates on Awake/OnEnable, so
        /// without this the collider keeps the previous theater's size and the
        /// ray stops disagreeing with the picture.
        /// </summary>
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

            var sequencer = ResolvePreShowSequencer();
            if (sequencer != null)
                sequencer.Play(d.root.transform, plane.GetComponent<Renderer>());
        }

        private Transform ResolveBrowserPlane()
        {
            if (browserPlane != null) return browserPlane;
            var renderer = FindAnyObjectByType<GeckoVulkanRenderer>();
            if (renderer != null) browserPlane = renderer.transform;
            return browserPlane;
        }

        private PreShowSequencer ResolvePreShowSequencer()
        {
            if (preShowSequencer != null) return preShowSequencer;
            preShowSequencer = FindAnyObjectByType<PreShowSequencer>();
            return preShowSequencer;
        }

        private static void SetActive(Destination d, Destination wanted)
        {
            if (d?.root != null) d.root.SetActive(d == wanted);
        }

        private Transform ResolveRig()
        {
            if (playerRig != null) return playerRig;
            var origin = FindAnyObjectByType<Unity.XR.CoreUtils.XROrigin>();
            return origin != null ? origin.transform : null;
        }
    }
}
