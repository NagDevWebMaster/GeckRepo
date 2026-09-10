// =============================================================================
//  CinematicLakeEnvironment.cs
//
//  Builds a sunset lake theatre around the browser plane: gradient sky dome,
//  rippling water, a wooden dock at the viewer, silhouetted treeline, drifting
//  mist, and a mirrored reflection of the page on the water.
//
//  Put this on the SAME GameObject as GeckoVulkanRenderer (BrowserPlane).
//  Everything is generated at runtime - no models, no textures to import.
//
//  Why it looks like this
//  ----------------------
//  A sunset lake is mostly silhouettes, gradients and fog, which is exactly what
//  a tile-based mobile GPU is good at. The expensive things were deliberately
//  avoided:
//
//   - No planar-reflection camera. Reflections re-render the scene; on Quest
//     that roughly doubles frame cost. The page reflection is one extra quad
//     sharing the SAME live browser texture, and the water fakes sky
//     reflectivity with fresnel.
//   - No depth or opaque texture sampling in the water. Grabbing either forces
//     a resolve on a tiler and is the usual reason "nice" water tanks framerate.
//   - Trees are unlit dark silhouettes. Against a sunset that reads correctly
//     AND costs nothing - no shadows, no lighting, no normal maps.
//   - Mist is a single soft-particle system with a procedurally generated
//     texture, capped low.
// =============================================================================

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[RequireComponent(typeof(GeckoVulkanRenderer))]
public class CinematicLakeEnvironment : MonoBehaviour
{
    [Header("Build")]
    [Tooltip("Rebuild in play mode after changing values (context menu > Rebuild).")]
    public bool buildOnAwake = true;

    [Header("Water")]
    [Tooltip("Turn off to rule the water out when something is hiding the screen.")]
    public bool buildWater = true;
    public float lakeRadius = 90f;
    [Tooltip("Water height, in metres below the bottom edge of the browser plane.")]
    public float waterDropBelowScreen = 1.5f;
    [Tooltip("Grid resolution across the whole lake. 96 is plenty - the waves are " +
             "low frequency and higher costs vertex time for nothing.")]
    [Range(16, 200)] public int waterSubdivisions = 96;

    public Color deepColor    = new Color(0.02f, 0.06f, 0.11f);
    public Color shallowColor = new Color(0.10f, 0.22f, 0.28f);
    public Color horizonTint  = new Color(0.98f, 0.55f, 0.28f);

    [Header("Sky & Light")]
    [Tooltip("Turn off to rule the sky dome out. The dome is a 270-unit-radius " +
             "inverted sphere around the whole scene - if its scale ever ends up " +
             "smaller than your camera-to-screen distance you end up inside-out " +
             "and it covers everything.")]
    public bool buildSky = true;
    public Color skyZenith  = new Color(0.05f, 0.08f, 0.22f);
    public Color skyMid     = new Color(0.55f, 0.33f, 0.42f);
    public Color skyHorizon = new Color(1.00f, 0.52f, 0.22f);
    [Tooltip("Sun elevation in degrees. Low is what makes it read as sunset.")]
    [Range(-5f, 30f)] public float sunElevation = 6f;
    [Range(0f, 360f)] public float sunAzimuth = 200f;
    public Color sunColor = new Color(1f, 0.68f, 0.42f);
    [Range(0f, 3f)] public float sunIntensity = 1.1f;

    [Header("Fog / Mist")]
    public bool enableFog = true;
    public Color fogColor = new Color(0.75f, 0.55f, 0.48f);
    public float fogDensity = 0.011f;
    public bool enableMistParticles = true;
    [Range(0, 400)] public int mistParticleCount = 140;

    [Header("Dock")]
    public bool buildDock = true;
    public float dockWidth = 3.0f;
    public float dockLength = 5.0f;
    public Color dockColor = new Color(0.16f, 0.11f, 0.08f);

    [Header("Treeline")]
    public bool buildTrees = true;
    [Range(0, 200)] public int treeCount = 64;
    [Tooltip("Trees ring the lake just inside its edge.")]
    [Range(0.5f, 1f)] public float treeRingFraction = 0.88f;
    public Vector2 treeHeightRange = new Vector2(6f, 16f);
    public Color treeColor = new Color(0.045f, 0.05f, 0.06f);

    [Header("Screen Reflection")]
    public bool buildReflection = true;
    [Range(0f, 1f)] public float reflectionStrength = 0.30f;
    public Color reflectionTint = new Color(0.55f, 0.65f, 0.80f);

    private const string Tag = "[GeckoVulkanBridge] ";

    private GeckoVulkanRenderer _browser;
    private Transform _root;
    private Material _reflectionMat;
    private Renderer _reflectionRenderer;

    private void Awake()
    {
        _browser = GetComponent<GeckoVulkanRenderer>();
        if (buildOnAwake) Build();
    }

    private void LateUpdate()
    {
        // The browser texture is created asynchronously (the AHardwareBuffer only
        // exists once the bridge is up), so the reflection can't be wired at
        // build time - poll until it appears, then stop.
        if (_reflectionMat != null && _reflectionMat.mainTexture == null)
        {
            var src = _browser.GetComponent<Renderer>().sharedMaterial;
            if (src != null && src.mainTexture != null)
            {
                _reflectionMat.mainTexture = src.mainTexture;
                _reflectionMat.mainTextureScale  = src.mainTextureScale;
                _reflectionMat.mainTextureOffset = src.mainTextureOffset;
                Debug.Log(Tag + "lake: reflection bound to live page texture");
            }
        }
    }

    [ContextMenu("Rebuild")]
    public void Build()
    {
        if (_root != null) DestroyImmediate(_root.gameObject);

        var rootGo = new GameObject("CinematicLake");
        rootGo.transform.SetParent(transform.parent, false);
        _root = rootGo.transform;

        // Plane basis. Unity's Plane mesh is 10x10 units, so world size is
        // 10 * lossyScale, and its normal is +Y in local space.
        float planeH = 10f * transform.lossyScale.z;
        Vector3 n = transform.up.normalized;
        Vector3 uiUp = Vector3.ProjectOnPlane(Vector3.up, n).normalized;
        if (uiUp.sqrMagnitude < 0.001f) uiUp = transform.forward;

        Vector3 screenBottom = transform.position - uiUp * (planeH * 0.5f);
        float waterY = screenBottom.y - waterDropBelowScreen;
        Vector3 centre = new Vector3(transform.position.x, waterY, transform.position.z);

        if (buildSky) BuildSky(centre);
        BuildLight();
        if (buildWater) BuildWater(centre);
        if (buildReflection) BuildReflection(screenBottom, waterY, n, uiUp);
        if (buildTrees) BuildTrees(centre);
        if (buildDock) BuildDock(waterY, n);
        if (enableMistParticles) BuildMist(centre);
        ApplyFog();

        // Geometry report - compare these against the screen to see what could
        // possibly be in front of it.
        var cam = Camera.main;
        Debug.Log(Tag + $"lake built: screenCentre={transform.position} " +
                  $"screenBottomY={screenBottom.y:F2} waterY={waterY:F2} " +
                  $"planeH={planeH:F2} lakeRadius={lakeRadius} " +
                  $"skyRadius={(buildSky ? lakeRadius * 3f : 0f):F1} " +
                  $"camera={(cam != null ? cam.transform.position.ToString() : "none")} " +
                  $"camToScreen={(cam != null ? Vector3.Distance(cam.transform.position, transform.position) : -1f):F2}");
    }

    // -------------------------------------------------------------------------
    private Material Mat(string shaderName)
    {
        Shader s = Shader.Find(shaderName);
        if (s == null)
        {
            Debug.LogError(Tag + $"Shader '{shaderName}' not found. If this only " +
                           "happens in a build, add it to Project Settings > " +
                           "Graphics > Always Included Shaders.");
            return null;
        }
        return new Material(s);
    }

    private void BuildSky(Vector3 centre)
    {
        var m = Mat("GeckoLake/SkyDome");
        if (m == null) return;
        m.SetColor("_TopColor", skyZenith);
        m.SetColor("_MidColor", skyMid);
        m.SetColor("_BottomColor", skyHorizon);

        var dome = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        dome.name = "SkyDome";
        Destroy(dome.GetComponent<Collider>());
        dome.transform.SetParent(_root, false);
        dome.transform.position = centre;
        dome.transform.localScale = Vector3.one * (lakeRadius * 6f);
        var r = dome.GetComponent<Renderer>();
        r.sharedMaterial = m;
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;

        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor     = skyZenith;
        RenderSettings.ambientEquatorColor = skyMid;
        RenderSettings.ambientGroundColor  = deepColor;
    }

    private void BuildLight()
    {
        var go = new GameObject("SunsetLight");
        go.transform.SetParent(_root, false);
        var l = go.AddComponent<Light>();
        l.type = LightType.Directional;
        l.color = sunColor;
        l.intensity = sunIntensity;
        // Shadows off: the treeline is unlit silhouettes and the water is
        // analytic, so nothing here would benefit, and shadow maps are a real
        // cost on Quest.
        l.shadows = LightShadows.None;
        go.transform.rotation = Quaternion.Euler(sunElevation, sunAzimuth, 0f);

        RenderSettings.sun = l;
    }

    private void BuildWater(Vector3 centre)
    {
        var m = Mat("GeckoLake/Water");
        if (m == null) return;
        m.SetColor("_DeepColor", deepColor);
        m.SetColor("_ShallowColor", shallowColor);
        m.SetColor("_SkyTint", horizonTint);
        m.SetColor("_SunColor", sunColor);

        Vector3 sunDir = -(Quaternion.Euler(sunElevation, sunAzimuth, 0f) * Vector3.forward);
        m.SetVector("_SunDir", sunDir);

        var go = new GameObject("LakeWater");
        go.transform.SetParent(_root, false);
        go.transform.position = centre;

        var mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = BuildGrid(lakeRadius * 2f, waterSubdivisions);

        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = m;
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;
    }

    /// <summary>Flat XZ grid centred on the origin. Waves are applied in the vertex shader.</summary>
    private static Mesh BuildGrid(float size, int div)
    {
        div = Mathf.Clamp(div, 2, 250);            // 251^2 verts stays under 65k
        var verts = new Vector3[(div + 1) * (div + 1)];
        var uvs   = new Vector2[verts.Length];
        var tris  = new int[div * div * 6];

        float step = size / div, half = size * 0.5f;
        for (int z = 0, i = 0; z <= div; z++)
            for (int x = 0; x <= div; x++, i++)
            {
                verts[i] = new Vector3(x * step - half, 0f, z * step - half);
                uvs[i] = new Vector2((float)x / div, (float)z / div);
            }

        for (int z = 0, t = 0; z < div; z++)
            for (int x = 0; x < div; x++)
            {
                int a = z * (div + 1) + x, b = a + 1, c = a + div + 1, d = c + 1;
                tris[t++] = a; tris[t++] = c; tris[t++] = b;
                tris[t++] = b; tris[t++] = c; tris[t++] = d;
            }

        var mesh = new Mesh { name = "LakeGrid" };
        mesh.indexFormat = verts.Length > 65000
            ? IndexFormat.UInt32 : IndexFormat.UInt16;
        mesh.vertices = verts; mesh.uv = uvs; mesh.triangles = tris;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    private void BuildReflection(Vector3 screenBottom, float waterY, Vector3 n, Vector3 uiUp)
    {
        _reflectionMat = Mat("GeckoLake/ScreenReflection");
        if (_reflectionMat == null) return;
        _reflectionMat.SetColor("_Tint", reflectionTint);
        _reflectionMat.SetFloat("_Strength", reflectionStrength);

        float planeW = 10f * transform.lossyScale.x;
        float planeH = 10f * transform.lossyScale.z;

        var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.name = "ScreenReflection";
        Destroy(quad.GetComponent<Collider>());     // must not block the pointer ray
        quad.transform.SetParent(_root, false);

        // Mirror through the water surface: same distance below it as the screen
        // bottom is above, flipped vertically.
        float above = screenBottom.y - waterY;
        Vector3 pos = screenBottom - uiUp * (planeH * 0.5f) - Vector3.up * (above * 2f);
        quad.transform.position = pos;
        quad.transform.rotation = Quaternion.LookRotation(-n, uiUp);
        quad.transform.localScale = new Vector3(planeW, -planeH, 1f);   // negative Y = mirrored

        _reflectionRenderer = quad.GetComponent<Renderer>();
        _reflectionRenderer.sharedMaterial = _reflectionMat;
        _reflectionRenderer.shadowCastingMode = ShadowCastingMode.Off;
        _reflectionRenderer.receiveShadows = false;
    }

    private void BuildTrees(Vector3 centre)
    {
        var m = Mat("Universal Render Pipeline/Unlit");
        if (m == null) return;
        m.color = treeColor;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", treeColor);

        var parent = new GameObject("Treeline").transform;
        parent.SetParent(_root, false);

        var rng = new System.Random(12345);        // deterministic between runs
        float ring = lakeRadius * treeRingFraction;

        for (int i = 0; i < treeCount; i++)
        {
            float a = (float)(i / (double)treeCount) * Mathf.PI * 2f
                      + (float)rng.NextDouble() * 0.06f;
            float rad = ring * (0.94f + (float)rng.NextDouble() * 0.12f);
            Vector3 basePos = centre + new Vector3(Mathf.Cos(a) * rad, 0f, Mathf.Sin(a) * rad);

            float h = Mathf.Lerp(treeHeightRange.x, treeHeightRange.y, (float)rng.NextDouble());

            var tree = new GameObject("Tree" + i).transform;
            tree.SetParent(parent, false);
            tree.position = basePos;
            tree.rotation = Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f);

            var trunk = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Destroy(trunk.GetComponent<Collider>());
            trunk.transform.SetParent(tree, false);
            trunk.transform.localPosition = new Vector3(0f, h * 0.25f, 0f);
            trunk.transform.localScale = new Vector3(h * 0.045f, h * 0.25f, h * 0.045f);
            Paint(trunk, m);

            // Two stacked cones read as a conifer at silhouette distance.
            for (int c = 0; c < 2; c++)
            {
                var canopy = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                Destroy(canopy.GetComponent<Collider>());
                canopy.transform.SetParent(tree, false);
                float cy = h * (0.52f + c * 0.26f);
                float cr = h * (0.20f - c * 0.07f);
                canopy.transform.localPosition = new Vector3(0f, cy, 0f);
                canopy.transform.localScale = new Vector3(cr, h * 0.20f, cr);
                Paint(canopy, m);
            }
        }
    }

    private static void Paint(GameObject go, Material m)
    {
        var r = go.GetComponent<Renderer>();
        r.sharedMaterial = m;
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
    }

    private void BuildDock(float waterY, Vector3 n)
    {
        var m = Mat("Universal Render Pipeline/Unlit");
        if (m == null) return;
        m.color = dockColor;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", dockColor);

        var dock = new GameObject("Dock").transform;
        dock.SetParent(_root, false);

        // In front of the viewer, on the screen side of the lake centre.
        Vector3 toward = Vector3.ProjectOnPlane(n, Vector3.up).normalized;
        Vector3 dockCentre = new Vector3(transform.position.x, waterY + 0.06f, transform.position.z)
                             + toward * (dockLength * 0.5f + 1.0f);
        dock.position = dockCentre;
        dock.rotation = Quaternion.LookRotation(-toward, Vector3.up);

        // Planks, gapped so the water shows through.
        int planks = Mathf.Max(4, Mathf.RoundToInt(dockLength / 0.32f));
        float plankLen = dockLength / planks;
        for (int i = 0; i < planks; i++)
        {
            var p = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(p.GetComponent<Collider>());
            p.transform.SetParent(dock, false);
            p.transform.localPosition = new Vector3(0f, 0f, -dockLength * 0.5f + (i + 0.5f) * plankLen);
            p.transform.localScale = new Vector3(dockWidth, 0.08f, plankLen * 0.82f);
            Paint(p, m);
        }

        // Posts at the far corners.
        for (int sx = -1; sx <= 1; sx += 2)
        {
            var post = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(post.GetComponent<Collider>());
            post.transform.SetParent(dock, false);
            post.transform.localPosition =
                new Vector3(sx * (dockWidth * 0.5f - 0.08f), 0.35f, dockLength * 0.5f - 0.1f);
            post.transform.localScale = new Vector3(0.12f, 0.8f, 0.12f);
            Paint(post, m);
        }
    }

    private void BuildMist(Vector3 centre)
    {
        var go = new GameObject("Mist");
        go.transform.SetParent(_root, false);
        go.transform.position = centre + Vector3.up * 0.4f;

        var ps = go.AddComponent<ParticleSystem>();
        var main = ps.main;
        main.loop = true;
        main.startLifetime = 18f;
        main.startSpeed = 0.25f;
        main.startSize = new ParticleSystem.MinMaxCurve(6f, 16f);
        main.startColor = new ParticleSystem.MinMaxGradient(
            new Color(fogColor.r, fogColor.g, fogColor.b, 0.05f),
            new Color(1f, 0.85f, 0.75f, 0.12f));
        main.maxParticles = mistParticleCount;
        main.simulationSpace = ParticleSystemSimulationSpace.World;

        var emission = ps.emission;
        emission.rateOverTime = mistParticleCount / 18f;

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.scale = new Vector3(lakeRadius * 1.4f, 0.6f, lakeRadius * 1.4f);

        var renderer = go.GetComponent<ParticleSystemRenderer>();
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;

        // Additive soft blob, generated rather than imported.
        var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit")
                     ?? Shader.Find("Sprites/Default");
        if (shader != null)
        {
            var m = new Material(shader) { mainTexture = SoftBlobTexture(64) };
            m.SetColor("_BaseColor", Color.white);
            renderer.sharedMaterial = m;
        }
    }

    private static Texture2D SoftBlobTexture(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        { wrapMode = TextureWrapMode.Clamp };
        float c = (size - 1) * 0.5f;
        var px = new Color[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c;
                float a = Mathf.Clamp01(1f - d);
                a = a * a * (3f - 2f * a);                  // smoothstep falloff
                px[y * size + x] = new Color(1f, 1f, 1f, a);
            }
        tex.SetPixels(px);
        tex.Apply();
        return tex;
    }

    private void ApplyFog()
    {
        RenderSettings.fog = enableFog;
        if (!enableFog) return;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogColor = fogColor;
        RenderSettings.fogDensity = fogDensity;
    }
}
