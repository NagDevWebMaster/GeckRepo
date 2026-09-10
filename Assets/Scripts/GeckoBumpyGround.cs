// =============================================================================
//  GeckoBumpyGround.cs
//
//  Replaces the Floor's flat Plane with a gently undulating one, so the ground
//  reads as real terrain under the grass material rather than a billiard table.
//
//  Put this on the Floor GameObject.
//
//  Why generated noise rather than the Displacement map
//  ---------------------------------------------------
//  Poliigon ships a Displacement.tiff with the grass set, but sampling it on
//  the CPU needs Read/Write enabled on the import, which keeps a second
//  uncompressed copy in memory for the whole session - a poor trade on a Quest
//  for a handful of vertex offsets. Value noise costs nothing at rest, tiles
//  seamlessly at any floor size, and is not locked to the material's own
//  tiling. The Displacement map still earns its keep through the normal map,
//  which is where the fine detail actually reads from.
//
//  Interaction with locomotion
//  ---------------------------
//  GeckoCinemaLocomotion has no auditorium to build a floor sampler from, so it
//  holds the rig at a fixed height (StickToFloor's flat-floor path). Keep the
//  amplitude small or the ground will visibly cut through the player's feet -
//  the default 6cm is under the noise floor of standing height.
// =============================================================================

using UnityEngine;

[ExecuteAlways]
[RequireComponent(typeof(MeshFilter))]
public class GeckoBumpyGround : MonoBehaviour
{
    [Header("Bumps")]
    [Tooltip("Peak height of the undulation, in metres. The rig walks at a " +
             "fixed height, so keep this small.")]
    [Range(0f, 0.5f)] public float amplitude = 0.06f;

    [Tooltip("Size of one bump in metres, crest to crest.")]
    [Range(0.5f, 30f)] public float wavelength = 6f;

    [Tooltip("Adds a second, finer pass at half the wavelength and a third of " +
             "the height, which stops the ground looking like a wave pool.")]
    public bool addDetailOctave = true;

    [Tooltip("Change to get a different arrangement of bumps.")]
    public int seed = 1;

    [Header("Tessellation")]
    [Tooltip("Grid resolution across the whole floor. 64 over a 50m floor puts " +
             "a vertex every 78cm, which is plenty for bumps this gentle.")]
    [Range(8, 254)] public int segments = 64;

    [Header("Debug")]
    public bool verboseLogging = true;

    private const string Tag = "[GeckoVulkanBridge] ";
    private const string MeshName = "GeckoBumpyGround";

    // The flat mesh the ground is generated FROM. Serialized so a domain reload
    // cannot leave us displacing an already-displaced mesh.
    [SerializeField, HideInInspector] private Mesh sourceMesh;

    private MeshFilter _filter;
    private MeshCollider _collider;
    private Mesh _generated;

    private void Awake() => Rebuild();

    private void OnEnable() => Rebuild();

    private void OnValidate()
    {
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

    /// <summary>Regenerates the displaced ground from the flat source.</summary>
    public void Rebuild()
    {
        _filter = GetComponent<MeshFilter>();
        _collider = GetComponent<MeshCollider>();
        if (_filter == null) return;

        if (sourceMesh == null)
        {
            Mesh current = _filter.sharedMesh;
            if (current == null || current.name == MeshName)
            {
                Debug.LogError(Tag + "ground: no flat source mesh. Assign Unity's " +
                               "Plane mesh to the MeshFilter and try again.");
                return;
            }
            sourceMesh = current;
        }

        if (amplitude < 0.0001f)
        {
            Apply(sourceMesh);
            Log("flat (amplitude 0) - using the source mesh directly");
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

        Log($"bumpy: amplitude={amplitude:F2}m wavelength={wavelength:F1}m " +
            $"grid={segments}x{segments} verts={built.vertexCount}");
    }

    private void Apply(Mesh m)
    {
        _filter.sharedMesh = m;
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
            Debug.LogError(Tag + "ground: source mesh is not a flat XZ quad.");
            return null;
        }

        Vector3 scale = transform.lossyScale;
        float sy = Mathf.Approximately(scale.y, 0f) ? 1f : scale.y;

        // The noise is evaluated in world metres so the bumps keep their real
        // size however the floor is scaled, then converted back to local space.
        float worldW = spanX * Mathf.Abs(scale.x);
        float worldD = spanZ * Mathf.Abs(scale.z);

        int n = segments + 1;
        var verts = new Vector3[n * n];
        var uvs = new Vector2[n * n];
        var tris = new int[segments * segments * 6];

        for (int j = 0; j < n; j++)
        {
            float tz = (float)j / segments;
            for (int i = 0; i < n; i++)
            {
                float tx = (float)i / segments;

                float wx = (tx - 0.5f) * worldW;
                float wz = (tz - 0.5f) * worldD;
                float h = Height(wx, wz);

                int k = j * n + i;
                verts[k] = new Vector3(b.min.x + spanX * tx, h / sy, b.min.z + spanZ * tz);
                uvs[k] = new Vector2(tx, tz);
            }
        }

        int t = 0;
        for (int j = 0; j < segments; j++)
        {
            for (int i = 0; i < segments; i++)
            {
                int k = j * n + i;
                // Normal up (+Y), matching Unity's Plane.
                tris[t++] = k;
                tris[t++] = k + n;
                tris[t++] = k + n + 1;

                tris[t++] = k;
                tris[t++] = k + n + 1;
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

    /// <summary>Height in world metres at a world-relative (x, z).</summary>
    private float Height(float x, float z)
    {
        float o = seed * 13.37f;
        float f = 1f / Mathf.Max(0.5f, wavelength);

        // Mathf.PerlinNoise returns 0..1 and is symmetric about 0.5.
        float h = (Mathf.PerlinNoise(x * f + o, z * f + o) - 0.5f) * 2f;

        if (addDetailOctave)
            h += (Mathf.PerlinNoise(x * f * 2f + o + 91f, z * f * 2f + o + 91f) - 0.5f) * 2f / 3f;

        return h * amplitude;
    }

    private void Log(string m) { if (verboseLogging) Debug.Log(Tag + "ground: " + m); }
}
