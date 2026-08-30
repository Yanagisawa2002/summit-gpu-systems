Shader "Hidden/GpuSystems/ExternalBrgBenchmark"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (0.10, 0.80, 0.95, 1.0)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "ExternalBrgBenchmark"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            Cull Back
            ZTest LEqual
            ZWrite On

            HLSLPROGRAM
            #pragma target 4.5
            #pragma exclude_renderers gles gles3 glcore
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma multi_compile _ DOTS_INSTANCING_ON
            #pragma multi_compile_local _ GPU_SYSTEMS_PATH

            float4x4 unity_MatrixVP;
            #define UNITY_MATRIX_VP unity_MatrixVP

            #if defined(GPU_SYSTEMS_PATH)
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
            #else
                #define UNITY_MATRIX_M unity_ObjectToWorld
                #define UNITY_SETUP_DOTS_SH_COEFFS
                #define UNITY_SETUP_DOTS_RENDER_BOUNDS
                #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
                #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/UnityInstancing.hlsl"

                CBUFFER_START(UnityPerMaterial)
                    float4 _BaseColor;
                CBUFFER_END

                #ifdef UNITY_DOTS_INSTANCING_ENABLED
                    UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
                        UNITY_DOTS_INSTANCED_PROP(float4, _BaseColor)
                    UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)

                    #undef unity_ObjectToWorld
                    UNITY_DOTS_INSTANCING_START(BuiltinPropertyMetadata)
                        UNITY_DOTS_INSTANCED_PROP(float3x4, unity_ObjectToWorld)
                    UNITY_DOTS_INSTANCING_END(BuiltinPropertyMetadata)

                    #define _BaseColor \
                        UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT( \
                            float4, _BaseColor)
                #else
                    CBUFFER_START(UnityPerDraw)
                        float4x4 unity_ObjectToWorld;
                        float4x4 unity_WorldToObject;
                        float4 unity_LODFade;
                        float4 unity_WorldTransformParams;
                    CBUFFER_END
                #endif
            #endif

            struct VertexInput
            {
                float4 position : POSITION;
                #if defined(GPU_SYSTEMS_PATH)
                    uint instanceId : SV_InstanceID;
                #else
                    UNITY_VERTEX_INPUT_INSTANCE_ID
                #endif
            };

            struct VertexOutput
            {
                float4 position : SV_POSITION;
                nointerpolation float4 color : COLOR0;
            };

            VertexOutput Vertex(VertexInput input)
            {
                VertexOutput output;
                #if defined(GPU_SYSTEMS_PATH)
                    uint groupedIndex =
                        _GpuGroupOffsets[_GpuBinIndex] + input.instanceId;
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
                    output.color = float4(0.10, 0.80, 0.95, 1.0);
                #else
                    UNITY_SETUP_INSTANCE_ID(input);
                    output.position = mul(
                        UNITY_MATRIX_VP,
                        mul(UNITY_MATRIX_M, input.position));
                    output.color = _BaseColor;
                #endif
                return output;
            }

            float4 Fragment(VertexOutput input) : SV_Target
            {
                return input.color;
            }
            ENDHLSL
        }
    }

    FallBack Off
}
