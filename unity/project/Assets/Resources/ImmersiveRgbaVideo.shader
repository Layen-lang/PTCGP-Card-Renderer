// CRI's decoded RGB host adapter. The original card stencil writes reference 2.
Shader "Source/Immersive RGBA Video"
{
    Properties { [PerRendererData] _MainTex("Decoded movie", 2D) = "black" {} }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Pass
        {
            Tags { "LightMode"="UniversalForward" }
            Stencil { Ref 2 Comp Equal Pass Keep }
            Cull Off ZWrite Off ZTest Always
            Blend One Zero
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct Attributes { float4 positionOS:POSITION; float2 uv:TEXCOORD0; };
            struct Varyings { float4 positionCS:SV_POSITION; float2 uv:TEXCOORD0; };
            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            Varyings vert(Attributes v) { Varyings o; o.positionCS=TransformObjectToHClip(v.positionOS.xyz); o.uv=v.uv; return o; }
            struct Output { half4 color:SV_Target0; half4 emission:SV_Target1; };
            Output frag(Varyings i) { Output o; o.color=half4(SAMPLE_TEXTURE2D(_MainTex,sampler_MainTex,i.uv).rgb,1); o.emission=0; return o; }
            ENDHLSL
        }
    }
}
