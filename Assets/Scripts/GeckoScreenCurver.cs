// =============================================================================
//  GeckoScreenCurver.cs
//
//  Bends the flat BrowserPlane into a cylindrical curved screen, the way a
//  curved monitor wraps around the viewer.
//
//  Put this on the SAME GameObject as GeckoVulkanRenderer (BrowserPlane).
//
//  Why it rebuilds the mesh instead of using a shader
//  --------------------------------------------------
//  A vertex-shader bend would move the pixels but not the geometry, and this
//  project points at the page with a physics ray: GeckoPointerInput reads
//  RaycastHit.textureCoord off the MeshCollider to turn a hit into a page
//  pixel. The collider has to be the curve, or every tap would land where the
//  page used to be flat. So the curve is real geometry, shared by the
//  MeshFilter and the MeshCollider.
//
//  Why the UVs are derived rather than written
//  -------------------------------------------
//  GeckoPointerInput's flipHorizontally / flipVertically settings are tuned
//  against the UV layout of Unity's built-in Plane. Inventing a fresh UV
//  convention here would silently mirror every tap. Instead the affine map
//  (x,z) -> (u,v) is read back off the source mesh's own corners and applied to
//  the new, denser grid, so the curve inherits whatever convention the source
//  had and the existing flip settings stay correct.
//
//  The generated mesh is marked DontSave: it is rebuilt from the source on
//  load, so it never bloats the scene file.
// =============================================================================

using UnityEngine;

[ExecuteAlways]
[RequireComponent(typeof(MeshFilter))]
public class GeckoScreenCurver : MonoBehaviour
{
    [Header("Curve")]
    [Tooltip("Total horizontal arc the screen wraps through, in degrees. " +
             "0 leaves it flat. A screen this wide viewed from its own width " +
             "away sits near 55 degrees.")]
    [Range(0f, 120f)] public float curvatureDegrees = 45f;

    [Tooltip("Curve toward the viewer (concave, like a monitor). Off bends it " +
             "away, which reads as a pillar.")]
    public bool concave = true;

    [Header("Tessellation")]
    [Tooltip("Segments across the curve. This is what makes the silhouette " +
             "smooth; the flat Plane's own 10 would facet visibly up close.")]
    [Range(4, 128)] public int segmentsAcross = 48;

    [Tooltip("Segments down the screen. The curve is cylindrical, so this only " +
             "needs to be high enough for lighting.")]
    [Range(1, 32)] public int segmentsDown = 8;

    [Header("Debug")]
    public bool verboseLogging = true;

    private const string Tag = "[GeckoVulkanBridge] ";
    private const string MeshName = "GeckoCurvedScreen";

    // The flat mesh the curve is generated FROM. Serialized so a domain reload
    // in the editor cannot leave us bending an already-bent mesh.
    [SerializeField, HideInInspector] private Mesh sourceMesh;

    private MeshFilter _filter;
    private MeshCollider _collider;
    private Mesh _generated;

    /// <summary>Radius of the cylinder in world units. 0 while flat.</summary>
    public float Radius { get; private set; }

    private void Awake() => Rebuild();

    private void OnEnable() => Rebuild();

    private void OnValidate()
    {
        // OnValidate runs during deserialization, where building meshes is not
        // allowed. Defer by one editor tick.
        if (!isActiveAndEnabled) return;
#if UNITY_EDITOR
        UnityEditor.EditorApplication.delayCall += () => { if (this != null) Rebuild(); };
#else
        Rebuild();
#endif
    }

    private void OnDestroy()
    {
        if (_generated == null) return;
        if (Application.isPlaying) Destroy(_generated);
        else DestroyImmediate(_generated);
    }

    /// <summary>Regenerates the curved mesh from the flat source.</summary>
    public void Rebuild()
    {
        _filter = GetComponent<MeshFilter>();
        _collider = GetComponent<MeshCollider>();
        if (_filter == null) return;

        // First run captures the flat mesh. Afterwards sharedMesh is ours, so
        // the captured reference is the only way back to the original.
        if (sourceMesh == null)
        {
            Mesh current = _filter.sharedMesh;
            if (current == null || current.name == MeshName)
            {
                Debug.LogError(Tag + "curver: no flat source mesh to bend. Assign " +
                               "Unity's Plane mesh to the MeshFilter and try again.");
                return;
            }
            sourceMesh = current;
        }

        if (curvatureDegrees < 0.01f)
        {
            Radius = 0f;
            Apply(sourceMesh);
            Log("flat (curvature 0) - using the source mesh directly");
            return;
        }

        Mesh built = Build();
        if (built == null) return;

        if (_generated != null && _generated != built)
        {
            if (Application.isPlaying) Destroy(_generated);
            else DestroyImmediate(_generated);
        }
        _generated = built;
        Apply(_generated);

        Log($"curved: {curvatureDegrees:F0}deg radius={Radius:F2}m " +
            $"grid={segmentsAcross}x{segmentsDown} verts={built.vertexCount}");
    }

    private void Apply(Mesh m)
    {
        _filter.sharedMesh = m;

        // Reassigning is what makes PhysX recook, so the ray and the picture
        // never disagree.
        if (_collider != null) _collider.sharedMesh = m;
    }

    // -------------------------------------------------------------------------
    // Geometry
    // -------------------------------------------------------------------------
    private Mesh Build()
    {
        Bounds b = sourceMesh.bounds;
        float spanX = b.size.x;
        float spanZ = b.size.z;
        if (spanX <= 0.0001f || spanZ <= 0.0001f)
        {
            Debug.LogError(Tag + "curver: source mesh is not a flat XZ quad.");
            return null;
        }

        if (!DeriveUvMap(b, out Vector2 uvOrigin, out Vector2 duX, out Vector2 duZ))
            return null;

        Vector3 scale = transform.lossyScale;
        float worldW = spanX * Mathf.Abs(scale.x);
        float theta = curvatureDegrees * Mathf.Deg2Rad;
        Radius = worldW / theta;

        // Local-space displacement has to be divided back out of the transform's
        // own scale, which on this plane is heavily non-uniform (a 0.2 x 1 x 0.1
        // Plane is 2m x 1m in the world). Doing the trigonometry in world units
        // and converting back is what keeps the arc circular rather than an
        // ellipse.
        float sx = Mathf.Approximately(scale.x, 0f) ? 1f : scale.x;
        float sy = Mathf.Approximately(scale.y, 0f) ? 1f : scale.y;
        float sign = concave ? 1f : -1f;

        int nx = segmentsAcross + 1;
        int nz = segmentsDown + 1;

        var verts = new Vector3[nx * nz];
        var uvs = new Vector2[nx * nz];
        var tris = new int[segmentsAcross * segmentsDown * 6];

        for (int j = 0; j < nz; j++)
        {
            float tz = (float)j / segmentsDown;
            float z = b.min.z + spanZ * tz;

            for (int i = 0; i < nx; i++)
            {
                float tx = (float)i / segmentsAcross;
                float x = b.min.x + spanX * tx;

                // Arc-length position along the cylinder, measured in world units
                // from the screen's centre.
                float arc = (tx - 0.5f) * worldW;
                float a = arc / Radius;

                float worldAcross = Radius * Mathf.Sin(a);
                float worldOut = sign * Radius * (1f - Mathf.Cos(a));

                int k = j * nx + i;
                verts[k] = new Vector3(worldAcross / sx, worldOut / sy, z);
                uvs[k] = uvOrigin + duX * (x - b.min.x) + duZ * (z - b.min.z);
            }
        }

        int t = 0;
        for (int j = 0; j < segmentsDown; j++)
        {
            for (int i = 0; i < segmentsAcross; i++)
            {
                int k = j * nx + i;
                // Winding matched to Unity's Plane: normal is +Y, so the front
                // face is the one the browser material is already drawn on.
                tris[t++] = k;
                tris[t++] = k + nx;
                tris[t++] = k + nx + 1;

                tris[t++] = k;
                tris[t++] = k + nx + 1;
                tris[t++] = k + 1;
            }
        }

        var mesh = new Mesh
        {
            name = MeshName,
            hideFlags = HideFlags.DontSave,
            indexFormat = verts.Length > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16
        };
        mesh.vertices = verts;
        mesh.uv = uvs;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
        mesh.RecalculateTangents();
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>
    /// Reads the source mesh's own UV convention back out of its corner
    /// vertices, as an origin plus the per-unit change along local x and z.
    /// Guessing this instead would mirror every tap on a mesh whose UVs run the
    /// other way, and the failure would look like a calibration problem rather
    /// than a wrong assumption.
    /// </summary>
    private bool DeriveUvMap(Bounds b, out Vector2 origin, out Vector2 duX, out Vector2 duZ)
    {
        origin = Vector2.zero; duX = Vector2.zero; duZ = Vector2.zero;

        Vector3[] v = sourceMesh.vertices;
        Vector2[] uv = sourceMesh.uv;
        if (v.Length == 0 || uv.Length != v.Length)
        {
            Debug.LogError(Tag + "curver: source mesh has no usable UVs.");
            return false;
        }

        int c00 = Nearest(v, new Vector3(b.min.x, 0f, b.min.z));
        int c10 = Nearest(v, new Vector3(b.max.x, 0f, b.min.z));
        int c01 = Nearest(v, new Vector3(b.min.x, 0f, b.max.z));

        origin = uv[c00];
        duX = (uv[c10] - uv[c00]) / b.size.x;
        duZ = (uv[c01] - uv[c00]) / b.size.z;
        return true;
    }

    private static int Nearest(Vector3[] v, Vector3 target)
    {
        int best = 0;
        float bestD = float.MaxValue;
        for (int i = 0; i < v.Length; i++)
        {
            float d = (v[i].x - target.x) * (v[i].x - target.x)
                    + (v[i].z - target.z) * (v[i].z - target.z);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    private void Log(string m) { if (verboseLogging) Debug.Log(Tag + "curver: " + m); }
}
