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
