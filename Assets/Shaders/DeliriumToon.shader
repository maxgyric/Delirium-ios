// VRChat avatars (imported from a .unitypackage): their toon look, roughly as Poiyomi / lilToon draw it. The light
// is a step, not a slope (a lit side and a soft shadow side, never black), shadows cast on it softened, the
// material's emission (glowing parts) added; texture, colour, cut-outs and culling as Delirium/Lit.
Shader "Delirium/Toon"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _MainTex ("Albedo", 2D) = "white" {}
        _Cutoff ("Alpha cutoff", Range(0,1)) = 0
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
        _ShadowLevel ("Shadow side brightness", Range(0,1)) = 0.72
        _EmissionColor ("Emission", Color) = (0,0,0,1)
        _EmissionMap ("Emission map", 2D) = "white" {}
        _EmissionBase ("Emission tinted by the texture", Range(0,1)) = 0
    }
    SubShader
    {
        Tags { "Queue"="AlphaTest" "RenderType"="TransparentCutout" "IgnoreProjector"="True" }
        LOD 200
        Cull [_Cull]

        CGPROGRAM
        #pragma surface surf Toon addshadow fullforwardshadows
        #pragma target 3.0

        sampler2D _MainTex, _EmissionMap;
        fixed4 _Color, _EmissionColor;
        half _Cutoff, _ShadowLevel, _EmissionBase;

        struct Input
        {
            float2 uv_MainTex;
            float4 color : COLOR;
        };

        half4 LightingToon(SurfaceOutput s, half3 lightDir, half atten)
        {
            half ndl = dot(s.Normal, lightDir);
            half lit = smoothstep(-0.05, 0.2, ndl) * lerp(0.5, 1.0, atten);
            half shade = lerp(_ShadowLevel, 1.0, lit);
            return half4(s.Albedo * _LightColor0.rgb * shade, 1);
        }

        void surf (Input IN, inout SurfaceOutput o)
        {
            fixed4 c = tex2D(_MainTex, IN.uv_MainTex) * _Color * IN.color;
            clip(c.a - _Cutoff);
            o.Albedo = c.rgb;
            o.Emission = tex2D(_EmissionMap, IN.uv_MainTex).rgb * _EmissionColor.rgb * lerp(fixed3(1, 1, 1), c.rgb, _EmissionBase);
            o.Alpha = 1;
        }
        ENDCG
    }
    FallBack "Legacy Shaders/Transparent/Cutout/VertexLit"
}
