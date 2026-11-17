// A GMod map's 2D skybox: one equirectangular panorama (the native library's, of the map's six pictures; its
// longitude from Source's +X turning toward +Y, latitude up the picture), looked up by the view's direction.
Shader "Delirium/Sky"
{
    Properties
    {
        _MainTex ("Panorama", 2D) = "grey" {}
        _Tint ("Tint", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" }
        Cull Off ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            fixed4 _Tint;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 dir : TEXCOORD0;
            };

            v2f vert (appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.dir = v.vertex.xyz;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 u = normalize(i.dir);
                // Unity's (x, y, z) is Source's (-x, z, -y): back to Source's
                float3 s = float3(-u.x, -u.z, u.y);
                float lon = atan2(s.y, s.x);
                float lat = asin(clamp(s.z, -1.0, 1.0));
                float2 uv = float2(lon / (2.0 * UNITY_PI) + 0.5, 0.5 + lat / UNITY_PI);
                return tex2Dlod(_MainTex, float4(uv, 0, 0)) * _Tint;
            }
            ENDCG
        }
    }
    Fallback Off
}
