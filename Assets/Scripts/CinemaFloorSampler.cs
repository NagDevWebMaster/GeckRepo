// =============================================================================
//  CinemaFloorSampler.cs
//
//  Answers "how high is the walkable floor under this point?" for the imported
//  auditorium, so the rig stands ON the raked seating deck instead of sinking
//  through it.
//
//  Why this is needed at all
//  -------------------------
//  A cinema floor is not flat, and this model's is not either. Behind the front
//  stalls it rakes upward in twelve solid risers:
//
//      Step_00  top y = 0.25   z in [-7.29,  -6.01]
//        ...                      (+0.19 per row)
//      Step_11  top y = 2.34   z in [-21.59, -20.31]
//
//  with a flat carpet plane at y = 0.01 covering the whole footprint beneath
//  them. Both CinemaScreenAligner (once, at spawn) and GeckoCinemaLocomotion
//  (every frame) used to pin the rig to a single flat 'auditorium.position.y',
//  i.e. 0 - which is the floor height of the front stalls and nowhere else.
//  Anywhere behind the first row the real deck is up to 2.34 m higher, so the
//  rig sat below the surface it was supposed to be standing on and the camera
//  looked out through the inside of the steps.
//
//  Why bounds and not a Physics raycast
//  ------------------------------------
//  Nothing in the model has a collider - see GeckoCinemaLocomotion's header for
//  why putting one on each of 2349 nodes is not worth it - so a downward
//  raycast would hit nothing at all. The floor is only a handful of static
//  boxes and planes, so their world-space renderer bounds are gathered once and
//  the tallest piece covering the query point wins. That is ~16 AABB tests per
//  frame, no colliders, no physics, and it reads the heights out of the model
//  rather than hardcoding the table above, so a re-export still works.
// =============================================================================

using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Static floor-height lookup over an imported auditorium. Build once, after the
/// model exists; query per frame. Deliberately not a MonoBehaviour - it holds no
/// scene state of its own, so CinemaScreenAligner and GeckoCinemaLocomotion can
/// share a single instance.
/// </summary>
public class CinemaFloorSampler
{
    /// <summary>Node-name fragments that count as walkable surface.</summary>
    public static readonly string[] DefaultFloorNames =
        { "Step", "Carpet", "Aisle", "Floor", "Stage", "Riser", "Platform" };

    /// <summary>
    /// Excluded even when they match the include list. StepLED_NN sits 2 cm proud
    /// of every step top and AisleLED likewise; treating them as floor would put a
    /// 2 cm lip along every riser edge that you would stand on top of.
    /// </summary>
    public static readonly string[] DefaultExcludeNames = { "LED", "Light" };

    /// <summary>
    /// How far each surface's XZ footprint is grown before testing, to bridge the
    /// gaps between abutting pieces.
    ///
    /// This is not cosmetic. The risers are 1.28m deep but spaced 1.3m apart, so
    /// there is a 0.02m crack between every pair of them - and the seat centroid
    /// this model spawns you at (z = -13.80) lands in one. Without the tolerance a
    /// query in a crack sees only the carpet plane 1.4m below and reports a fall,
    /// which registers as a ledge every 1.3m and makes walking up the rake
    /// impossible.
    /// </summary>
    public const float DefaultSeamTolerance = 0.06f;

    private readonly List<Bounds> _pieces = new List<Bounds>();
    private readonly float _fallbackY;

    /// <summary>How many surfaces were found. 0 means every query returns the fallback.</summary>
    public int PieceCount => _pieces.Count;

    public float FallbackY => _fallbackY;

    public CinemaFloorSampler(Transform auditorium, float fallbackY,
                              IList<string> include = null, IList<string> exclude = null,
                              float seamTolerance = DefaultSeamTolerance)
    {
        _fallbackY = fallbackY;
        if (auditorium == null) return;

        if (include == null) include = DefaultFloorNames;
        if (exclude == null) exclude = DefaultExcludeNames;

        // Expand() grows the SIZE, so pass twice the tolerance to move each face out
        // by the tolerance itself. Y is left alone - only the footprint is widened.
        var grow = new Vector3(seamTolerance * 2f, 0f, seamTolerance * 2f);

        foreach (var r in auditorium.GetComponentsInChildren<Renderer>(true))
        {
            if (!ContainsAny(r.name, include)) continue;
            if (ContainsAny(r.name, exclude)) continue;

            Bounds b = r.bounds;
            b.Expand(grow);
            _pieces.Add(b);
        }
    }

    /// <summary>
    /// Height of the highest walkable surface covering (x, z).
    /// </summary>
    /// <param name="supported">
    /// False when no piece covers the point at all - the caller is over a hole or
    /// off the edge of the model, and should refuse the move rather than fall.
    /// </param>
    public float Sample(float x, float z, out bool supported)
    {
        bool any = false;
        float best = 0f;

        for (int i = 0; i < _pieces.Count; i++)
        {
            Bounds b = _pieces[i];
            // XZ footprint only: the risers are solid boxes spanning y = 0 to their
            // top, so a full Contains() would report "outside" for anyone standing
            // on one.
            if (x < b.min.x || x > b.max.x || z < b.min.z || z > b.max.z) continue;

            if (!any || b.max.y > best) { best = b.max.y; any = true; }
        }

        supported = any;
        return any ? best : _fallbackY;
    }

    /// <summary>Convenience overload for callers that do not care about support.</summary>
    public float Sample(float x, float z) => Sample(x, z, out _);

    /// <summary>Splits an Inspector-friendly "A,B,C" list. Null/blank returns null.</summary>
    public static string[] ParseNames(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return null;
        var parts = csv.Split(',');
        var result = new List<string>(parts.Length);
        foreach (var p in parts)
        {
            string t = p.Trim();
            if (t.Length > 0) result.Add(t);
        }
        return result.Count > 0 ? result.ToArray() : null;
    }

    private static bool ContainsAny(string name, IList<string> fragments)
    {
        for (int i = 0; i < fragments.Count; i++)
            if (name.IndexOf(fragments[i], System.StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        return false;
    }
}
