// Your own models and maps (.glb): matt (Lambert), the texture times the material's colour times the vertices'
// colours (a map's corners darkened in its file show), cut where the texture is see-through (_Cutoff: leaves,
// fences; 0: nothing is cut), both faces drawn when the file says so (_Cull 0). A GMod map's baked light (_LightMap on
// the second texture coordinates, at half: doubled here) lights it the way it was lit in its game; the sun then only
// adds a little (and the shadows of what moves on it).
Shader "Delirium/Lit"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _MainTex ("Albedo", 2D) = "white" {}
        _Cutoff ("Alpha cutoff", Range(0,1)) = 0
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
        _LightMap ("Baked light", 2D) = "white" {}
        _LightMapOn ("Baked light on", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="AlphaTest" "RenderType"="TransparentCutout" "IgnoreProjector"="True" }
        LOD 200
        Cull [_Cull]

        CGPROGRAM
        #pragma surface surf Lambert addshadow fullforwardshadows
        #pragma target 3.0

        sampler2D _MainTex;
        sampler2D _LightMap;
        fixed4 _Color;
        half _Cutoff;
        half _LightMapOn;

        struct Input
        {
            float2 uv_MainTex;
            float2 uv2_LightMap;
            float4 color : COLOR;
        };

        void surf (Input IN, inout SurfaceOutput o)
        {
            fixed4 c = tex2D(_MainTex, IN.uv_MainTex) * _Color * IN.color;
            clip(c.a - _Cutoff);
            if (_LightMapOn > 0.5)
            {
                half3 baked = tex2D(_LightMap, IN.uv2_LightMap).rgb * 2.0;
                o.Emission = c.rgb * baked * 0.85;
                o.Albedo = c.rgb * 0.2;
            }
            else o.Albedo = c.rgb;
            o.Alpha = 1;
        }
        ENDCG
    }
    FallBack "Legacy Shaders/Transparent/Cutout/VertexLit"
}
