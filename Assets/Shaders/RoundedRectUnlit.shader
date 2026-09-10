// Rounded-rectangle unlit quad, used for the on-screen keyboard's keys,
// backdrops and shadows. A signed-distance rounded-box test in the fragment
// shader means the same shader gives a correct, undistorted corner radius on
// any quad aspect ratio (square letter keys, a 3-key-wide spacebar) - a
// stretched rounded-corner texture would smear the radius into an oval on
// the wide ones instead.
//
// _Size is the quad's own world size in metres (w, h), set once per key at
// creation. _BaseColor is written every frame by GeckoUIButton's
// MaterialPropertyBlock (hover/press colours) - _CornerRadius/_Size are
// plain material properties set once and never touched by that block.
//
// _GradientTop/_GradientBottom multiply _BaseColor rather than exposing a
// second independent colour, specifically so a hover/press colour change
// (which only ever touches _BaseColor via the property block) still shades
// the WHOLE quad, top and bottom together, instead of flashing two-tone
// while _BaseColor moves but a separate top colour sits still. Both default
// to 1 (no gradient, identical to a flat fill) - only the dialog panel
// background sets them to anything else.
Shader "Custom/RoundedRectUnlit"
{
    Properties
    {
        _BaseColor("Color", Color) = (1, 1, 1, 1)
        _Size("Size (world units, w/h)", Vector) = (1, 1, 0, 0)
        _CornerRadius("Corner Radius (world units)", Float) = 0.01
        _GradientTop("Gradient Top Multiplier", Float) = 1.0
        _GradientBottom("Gradient Bottom Multiplier", Float) = 1.0
    }
    SubShader
    {
        // Opaque + alpha-tested (clip), not alpha-blended: many keycaps sit at
        // nearly the same depth in a tight stack (key, shadow, panel, border),
        // and Transparent-queue sorting is by per-object distance to camera -
        // with that many near-tied distances the sort order is unstable and
        // the large panel quad can draw AFTER (on top of) a row of keys,
        // hiding them intermittently. Opaque rendering uses the real
        // per-pixel depth buffer instead, which has no such ambiguity - clip()
        // alone is enough to cut the rounded corners.
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }
        ZWrite On
        Cull Back

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            CBUFFER_START(UnityPerMaterial)
            half4 _BaseColor;
            float4 _Size;
            float _CornerRadius;
            float _GradientTop;
            float _GradientBottom;
            CBUFFER_END

            Varyings Vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            // Inigo Quilez's rounded-box SDF: p and halfSize/radius all in the
            // same physical units (metres here), centred on the box.
            float RoundedBoxSDF(float2 p, float2 halfSize, float radius)
            {
                float2 q = abs(p) - halfSize + radius;
                return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                float2 p = (IN.uv - 0.5) * _Size.xy;
                float2 halfSize = _Size.xy * 0.5;
                float dist = RoundedBoxSDF(p, halfSize, _CornerRadius);

                float aa = max(fwidth(dist), 1e-5);
                float alpha = 1.0 - smoothstep(-aa, aa, dist);
                clip(alpha - 0.001);

                float shade = lerp(_GradientBottom, _GradientTop, IN.uv.y);
                return half4(_BaseColor.rgb * shade, _BaseColor.a * alpha);
            }
            ENDHLSL
        }
    }
}
