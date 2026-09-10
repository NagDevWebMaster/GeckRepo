// Renders a texture as a plain alpha mask tinted by _BaseColor, ignoring the
// texture's own RGB entirely - the icon PNGs used for the keyboard's
// shift/backspace/enter/close/keyboard-tab keys are solid black shapes on a
// transparent background, and the keys they sit on are nearly black too, so
// drawing the source colour would be invisible. Masking (the same trick a
// font atlas uses: alpha shapes the glyph, one colour fills it) makes the
// same PNG usable as a light icon on a dark key without needing a
// re-exported white version.
Shader "Custom/IconUnlit"
{
    Properties
    {
        _MainTex("Icon (alpha = shape)", 2D) = "white" {}
        _BaseColor("Tint", Color) = (1, 1, 1, 1)
    }
    SubShader
    {
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

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
            float4 _MainTex_ST;
            half4 _BaseColor;
            CBUFFER_END

            Varyings Vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = TRANSFORM_TEX(IN.uv, _MainTex);
                return OUT;
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                half a = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv).a;
                clip(a - 0.5);
                return half4(_BaseColor.rgb, 1);
            }
            ENDHLSL
        }
    }
}
