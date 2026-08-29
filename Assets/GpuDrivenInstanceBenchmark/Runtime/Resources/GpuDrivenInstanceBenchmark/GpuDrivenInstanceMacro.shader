Shader "Hidden/Summit/GpuDrivenInstanceMacro"
{
    Properties
    {
        _GroupColor ("Group Color", Color) = (1, 1, 1, 1)
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Geometry"
            "RenderType" = "Opaque"
        }

        Pass
        {
            Cull Back
            ZTest LEqual
            ZWrite On

            CGPROGRAM
            #pragma target 5.0
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling
            #pragma multi_compile_local _ GPU_DRIVEN_MACRO

            #include "UnityCG.cginc"

            #if defined(GPU_DRIVEN_MACRO)
                struct GpuInstanceState
                {
                    float4 PositionRadius;
                    float4 LodDistances;
                    uint ApplicationId;
                    uint DrawGroupBase;
                    uint LodCount;
                    uint ViewMask;
                };

                StructuredBuffer<uint> _GpuGroupedInstanceIndices;
                StructuredBuffer<uint> _GpuGroupOffsets;
                StructuredBuffer<GpuInstanceState> _GpuInstanceStates;
                uint _GpuBinIndex;
                uint _GpuGroupIndex;
            #else
                uint _CpuGroupIndex;
            #endif

            struct VertexInput
            {
                float4 position : POSITION;
                #if !defined(GPU_DRIVEN_MACRO)
                    UNITY_VERTEX_INPUT_INSTANCE_ID
                #endif
            };

            struct VertexToFragment
            {
                float4 position : SV_POSITION;
                nointerpolation float4 color : COLOR0;
            };

            float4 GroupPalette(uint group)
            {
                switch (group & 7u)
                {
                    case 0u:
                        return float4(0.95, 0.20, 0.20, 1.0);
                    case 1u:
                        return float4(0.20, 0.85, 0.30, 1.0);
                    case 2u:
                        return float4(0.20, 0.45, 0.95, 1.0);
                    case 3u:
                        return float4(0.95, 0.75, 0.15, 1.0);
                    case 4u:
                        return float4(0.75, 0.25, 0.90, 1.0);
                    case 5u:
                        return float4(0.10, 0.85, 0.85, 1.0);
                    case 6u:
                        return float4(0.95, 0.45, 0.10, 1.0);
                    default:
                        return float4(0.75, 0.75, 0.75, 1.0);
                }
            }

            VertexToFragment Vertex(
                VertexInput input
                #if defined(GPU_DRIVEN_MACRO)
                    , uint svInstanceID : SV_InstanceID
                #endif
            )
            {
                VertexToFragment output;
                #if defined(GPU_DRIVEN_MACRO)
                    uint groupedIndex =
                        _GpuGroupOffsets[_GpuBinIndex] + svInstanceID;
                    uint instanceIndex =
                        _GpuGroupedInstanceIndices[groupedIndex];
                    GpuInstanceState instance =
                        _GpuInstanceStates[instanceIndex];
                    float diameter = instance.PositionRadius.w * 2.0;
                    float3 worldPosition =
                        instance.PositionRadius.xyz +
                        input.position.xyz * diameter;
                    output.position = mul(
                        UNITY_MATRIX_VP,
                        float4(worldPosition, 1.0));
                    output.color = GroupPalette(_GpuGroupIndex);
                #else
                    UNITY_SETUP_INSTANCE_ID(input);
                    float4 worldPosition = mul(
                        unity_ObjectToWorld,
                        input.position);
                    output.position = mul(UNITY_MATRIX_VP, worldPosition);
                    output.color = GroupPalette(_CpuGroupIndex);
                #endif
                return output;
            }

            float4 Fragment(VertexToFragment input) : SV_Target
            {
                return input.color;
            }
            ENDCG
        }
    }

    FallBack Off
}
