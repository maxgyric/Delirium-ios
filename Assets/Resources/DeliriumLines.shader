// The physics view's lines (Menu > Physics view): vertex colours, drawn over everything (through the ground too:
// Delirium's ground under the map's shows).
Shader "Hidden/Delirium/Lines"
{
    SubShader
    {
        Tags { "Queue"="Overlay" "RenderType"="Overlay" }
        Pass
        {
            ZTest Always ZWrite Off Cull Off Blend SrcAlpha OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct v2f { float4 pos : SV_POSITION; fixed4 col : COLOR; };
            v2f vert(float4 v : POSITION, fixed4 c : COLOR) { v2f o; o.pos = UnityObjectToClipPos(v); o.col = c; return o; }
            fixed4 frag(v2f i) : SV_Target { return i.col; }
            ENDCG
        }
    }
}
