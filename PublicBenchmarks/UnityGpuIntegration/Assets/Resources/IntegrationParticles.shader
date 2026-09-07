Shader "PublicIntegration/QueryConsumer"
{
    Properties { _Palette("Loaded content palette",2D)="white"{} }
    SubShader
    {
        Pass
        {
            ZWrite On ZTest LEqual Cull Off
            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            StructuredBuffer<uint4> _Samples;
            StructuredBuffer<uint> _Active;
            StructuredBuffer<uint4> _Digests;
            sampler2D _Palette;
            struct V { float4 pos:SV_POSITION; float4 color:COLOR; float2 uv:TEXCOORD0; };
            V vert(uint id:SV_VertexID)
            {
                V o; uint4 s=_Samples[id];uint4 q=_Digests[id%9];
                float3 world=(float3(s.xyz)/65535.0-0.5)*64.0;
                o.pos=mul(UNITY_MATRIX_VP,float4(world,1));
                if(_Active[id]==0)o.pos=float4(2e8,2e8,2e8,1);
                uint h=q.y^q.z^s.w;
                o.color=float4(0.2+0.8*float3(h&255,(h>>8)&255,(h>>16)&255)/255.0,1);
                o.uv=float2(id&255,(id>>8)&255)/255.0;
                return o;
            }
            float4 frag(V i):SV_Target { return float4(i.color.rgb*(0.55+0.45*tex2D(_Palette,i.uv).rgb),1); }
            ENDHLSL
        }
        Pass
        {
            ZWrite Off ZTest Always Cull Off
            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex vert
            #pragma fragment frag
            StructuredBuffer<uint4> _Digests;
            struct V { float4 pos:SV_POSITION; float4 color:COLOR; };
            V vert(uint id:SV_VertexID)
            {
                uint q=id/6;uint corner=id%6;
                float2 uv=corner==0?float2(0,0):corner==1?float2(1,0):corner==2?float2(1,1):corner==3?float2(0,0):corner==4?float2(1,1):float2(0,1);
                uint4 d=_Digests[q];float height=0.015+0.35*log2(1.0+d.x)/19.0;
                V o;o.pos=float4(-0.88+q*0.075+uv.x*0.05,-0.85+uv.y*height,0,1);
                o.color=float4(0.12+0.7*q/9.0,0.75,0.95-0.06*q,1);return o;
            }
            float4 frag(V i):SV_Target{return i.color;}
            ENDHLSL
        }
    }
}
