Shader "Custom/TriplanarTerrain"
{
    Properties
    {
        [Header(Textures)]
        _TopTex      ("Top Texture (Grass)",  2D) = "white" {}
        _SideTex     ("Side Texture (Rock)",  2D) = "white" {}

        [Header(Tiling)]
        _TexScale    ("Texture Scale",  Float) = 0.25

        [Header(Blending)]
        _BlendSharpness ("Triplanar Blend Sharpness", Range(1, 32)) = 8
        _TopSpread      ("Top Spread (Normal.y threshold)", Range(0, 1)) = 0.7
        _TopBlend       ("Top/Side Blend Sharpness", Range(1, 64)) = 16

        [Header(Lighting)]
        _Color       ("Tint Color",  Color) = (1,1,1,1)
    }

    SubShader
    {
        Tags
        {
            "RenderType"     = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue"          = "Geometry"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag

            // URP keywords for main & additional lights + shadows
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            // ────────────────────────────────────────────────────
            //  Uniforms
            // ────────────────────────────────────────────────────
            TEXTURE2D(_TopTex);   SAMPLER(sampler_TopTex);
            TEXTURE2D(_SideTex);  SAMPLER(sampler_SideTex);

            CBUFFER_START(UnityPerMaterial)
                float  _TexScale;
                float  _BlendSharpness;
                float  _TopSpread;
                float  _TopBlend;
                float4 _Color;
            CBUFFER_END

            // ────────────────────────────────────────────────────
            //  Structs
            // ────────────────────────────────────────────────────
            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
                float  fogFactor   : TEXCOORD2;
            };

            // ────────────────────────────────────────────────────
            //  Vertex
            // ────────────────────────────────────────────────────
            Varyings vert(Attributes IN)
            {
                Varyings OUT;

                VertexPositionInputs posInputs = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs   nrmInputs = GetVertexNormalInputs(IN.normalOS);

                OUT.positionCS = posInputs.positionCS;
                OUT.positionWS = posInputs.positionWS;
                OUT.normalWS   = nrmInputs.normalWS;
                OUT.fogFactor  = ComputeFogFactor(posInputs.positionCS.z);

                return OUT;
            }

            // ────────────────────────────────────────────────────
            //  Triplanar sampling helper
            // ────────────────────────────────────────────────────
            float3 SampleTriplanar(TEXTURE2D_PARAM(tex, samp), float3 worldPos, float3 blendWeights)
            {
                float2 uvX = worldPos.yz * _TexScale;
                float2 uvY = worldPos.xz * _TexScale;
                float2 uvZ = worldPos.xy * _TexScale;

                float3 colX = SAMPLE_TEXTURE2D(tex, samp, uvX).rgb;
                float3 colY = SAMPLE_TEXTURE2D(tex, samp, uvY).rgb;
                float3 colZ = SAMPLE_TEXTURE2D(tex, samp, uvZ).rgb;

                return colX * blendWeights.x
                     + colY * blendWeights.y
                     + colZ * blendWeights.z;
            }

            // ────────────────────────────────────────────────────
            //  Fragment
            // ────────────────────────────────────────────────────
            half4 frag(Varyings IN) : SV_Target
            {
                float3 normal = normalize(IN.normalWS);

                // ── Triplanar blend weights from world normal ──
                float3 blend = pow(abs(normal), _BlendSharpness);
                blend /= (blend.x + blend.y + blend.z + 1e-6);

                // ── Sample both textures triplanarly ────────────
                float3 topColor  = SampleTriplanar(TEXTURE2D_ARGS(_TopTex,  sampler_TopTex),  IN.positionWS, blend);
                float3 sideColor = SampleTriplanar(TEXTURE2D_ARGS(_SideTex, sampler_SideTex), IN.positionWS, blend);

                // ── Top vs side selection based on normal.y ─────
                // Surfaces with normal.y >= _TopSpread get top texture
                float topWeight = saturate((normal.y - _TopSpread) * _TopBlend);
                float3 albedo = lerp(sideColor, topColor, topWeight) * _Color.rgb;

                // ── URP main directional light ──────────────────
                float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                Light mainLight = GetMainLight(shadowCoord);

                float NdotL = saturate(dot(normal, mainLight.direction));
                float3 diffuse = albedo * mainLight.color * (NdotL * mainLight.shadowAttenuation * mainLight.distanceAttenuation);

                // Ambient / SH
                float3 ambient = SampleSH(normal) * albedo;

                // ── Additional lights ───────────────────────────
                float3 addLighting = float3(0, 0, 0);
                #ifdef _ADDITIONAL_LIGHTS
                int addCount = GetAdditionalLightsCount();
                for (int i = 0; i < addCount; i++)
                {
                    Light addLight = GetAdditionalLight(i, IN.positionWS);
                    float addNdotL = saturate(dot(normal, addLight.direction));
                    addLighting += albedo * addLight.color * (addNdotL * addLight.shadowAttenuation * addLight.distanceAttenuation);
                }
                #endif

                float3 finalColor = ambient + diffuse + addLighting;
                finalColor = MixFog(finalColor, IN.fogFactor);

                return half4(finalColor, 1.0);
            }
            ENDHLSL
        }

        // Shadow caster pass so terrain casts shadows
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex   ShadowVert
            #pragma fragment ShadowFrag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;

            struct ShadowAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct ShadowVaryings
            {
                float4 positionCS : SV_POSITION;
            };

            ShadowVaryings ShadowVert(ShadowAttributes IN)
            {
                ShadowVaryings OUT;
                float3 posWS = TransformObjectToWorld(IN.positionOS.xyz);
                float3 nrmWS = TransformObjectToWorldNormal(IN.normalOS);
                posWS = ApplyShadowBias(posWS, nrmWS, _LightDirection);
                OUT.positionCS = TransformWorldToHClip(posWS);

                // Clamp to near plane to avoid shadow clipping
                #if UNITY_REVERSED_Z
                    OUT.positionCS.z = min(OUT.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    OUT.positionCS.z = max(OUT.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif

                return OUT;
            }

            half4 ShadowFrag(ShadowVaryings IN) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        // Depth-only pass (required by URP for depth prepass)
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex   DepthVert
            #pragma fragment DepthFrag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct DepthAttributes
            {
                float4 positionOS : POSITION;
            };

            struct DepthVaryings
            {
                float4 positionCS : SV_POSITION;
            };

            DepthVaryings DepthVert(DepthAttributes IN)
            {
                DepthVaryings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                return OUT;
            }

            half4 DepthFrag(DepthVaryings IN) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }

    Fallback Off
}
