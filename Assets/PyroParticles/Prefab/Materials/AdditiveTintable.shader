Shader "Custom/AdditiveTintable"
{
    Properties
    {
        [MainTexture] _MainTex ("Color (RGB) Alpha (A)", 2D) = "white" {}
        [MainColor] _TintColor ("Tint Color (RGB)", Color) = (1, 1, 1, 1)
        _InvFade ("Soft Particles Factor", Range(0.01, 3.0)) = 1.0
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" }
        LOD 200

        Pass
        {
            Cull Back
            Lighting Off
            ZWrite Off
            Blend SrcAlpha One

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_particles

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS   : POSITION;
                half4  color        : COLOR;
                float2 uv           : TEXCOORD0;
            };

            struct Varyings
            {
                float2 uv           : TEXCOORD0;
                half4  color        : COLOR;
                float4 positionCS   : SV_POSITION;
                
                #if defined(SOFTPARTICLES_ON)
                    float4 projPos  : TEXCOORD1;
                    float  viewDepth: TEXCOORD2;
                #endif
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half4 _TintColor;
                float _InvFade;
            CBUFFER_END

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.color = v.color * _TintColor;

                #if defined(SOFTPARTICLES_ON)
                    o.projPos = ComputeScreenPos(o.positionCS);
                    o.viewDepth = -TransformWorldToView(TransformObjectToWorld(v.positionOS.xyz)).z;
                #endif

                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                half4 col = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv) * i.color;

                #if defined(SOFTPARTICLES_ON)
                    float rawSceneDepth = SampleSceneDepth(i.projPos.xy / i.projPos.w);
                    float sceneZ = LinearEyeDepth(rawSceneDepth, _ZBufferParams);
                    float partZ = i.viewDepth;
                    float fade = saturate(_InvFade * (sceneZ - partZ));
                    col.a *= fade;
                #endif

                return col;
            }
            ENDHLSL
        }
    }
}