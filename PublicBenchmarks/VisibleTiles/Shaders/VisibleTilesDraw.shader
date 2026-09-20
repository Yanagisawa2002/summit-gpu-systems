Shader "Summit/VisibleTiles/Validation"
{
    SubShader
    {
        // Nonoverlapping, unlit/two-sided fixture: depth/shading are not tested.
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "VisibleTilesDraw.hlsl"
            ENDHLSL
        }
        Pass
        {
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex ReferenceVert
            #pragma fragment Frag
            #include "VisibleTilesDraw.hlsl"
            ENDHLSL
        }
    }
}
