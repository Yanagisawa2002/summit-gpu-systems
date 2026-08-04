Shader "NYCGIS/GpuVegetationURP"
{
    Properties
    {
        _VegetationCrownTint("Crown Tint", Color) = (1,1,1,1)
        _VegetationTrunkColor("Trunk Color", Color) = (0.20,0.095,0.035,1)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
            "RenderPipeline" = "UniversalPipeline"
        }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

        StructuredBuffer<uint> _VegetationTreeWords;
        StructuredBuffer<uint> _VegetationVisibleIndices;

        CBUFFER_START(UnityPerMaterial)
            float4x4 _VegetationDataToWorld;
            float4 _VegetationCrownTint;
            float4 _VegetationTrunkColor;
            float4 _VegetationWind;
            int _VegetationGeometryMode;
        CBUFFER_END

        struct VegetationTree
        {
            float3 basePosition;
            float height;
            float crownRadius;
            float trunkRadius;
            float rotation;
            float4 color;
        };

        struct VegetationVertex
        {
            float3 position;
            float3 normal;
            float4 color;
        };

        VegetationTree LoadVegetationTree(uint instanceId)
        {
            uint treeIndex = _VegetationVisibleIndices[instanceId];
            uint word = treeIndex * 8u;
            VegetationTree tree;
            tree.basePosition = float3(
                asfloat(_VegetationTreeWords[word + 0u]),
                asfloat(_VegetationTreeWords[word + 1u]),
                asfloat(_VegetationTreeWords[word + 2u]));
            tree.height = asfloat(_VegetationTreeWords[word + 3u]);
            tree.crownRadius = asfloat(_VegetationTreeWords[word + 4u]);
            tree.trunkRadius = asfloat(_VegetationTreeWords[word + 5u]);
            tree.rotation = asfloat(_VegetationTreeWords[word + 6u]);
            uint packedColor = _VegetationTreeWords[word + 7u];
            tree.color = float4(
                packedColor & 0xffu,
                (packedColor >> 8u) & 0xffu,
                (packedColor >> 16u) & 0xffu,
                (packedColor >> 24u) & 0xffu) / 255.0;
            return tree;
        }

        float2 RotateVegetation(float2 value, float angle)
        {
            float sine;
            float cosine;
            sincos(angle, sine, cosine);
            return float2(
                value.x * cosine - value.y * sine,
                value.x * sine + value.y * cosine);
        }

        float3 VegetationWindOffset(VegetationTree tree, float verticalWeight)
        {
            float phase =
                tree.basePosition.x * 0.021 +
                tree.basePosition.z * 0.017 +
                _VegetationWind.z * _VegetationWind.y;
            float sway = sin(phase) * _VegetationWind.x * verticalWeight;
            return float3(sway, 0.0, sway * 0.42);
        }

        VegetationVertex BuildTrunkVertex(uint vertexId, VegetationTree tree)
        {
            const uint sides = 8u;
            uint triangleIndex = vertexId / 3u;
            uint corner = vertexId % 3u;
            uint side = triangleIndex / 2u;
            bool secondTriangle = (triangleIndex & 1u) != 0u;
            uint nextSide = (side + 1u) % sides;
            uint selectedSide;
            float y;
            if (!secondTriangle)
            {
                selectedSide = corner == 2u ? nextSide : side;
                y = corner == 0u ? 0.0 : 1.0;
            }
            else
            {
                selectedSide = corner == 0u ? side : nextSide;
                y = corner == 2u ? 0.0 : 1.0;
            }
            float angle = selectedSide / (float)sides * TWO_PI + tree.rotation;
            float sine;
            float cosine;
            sincos(angle, sine, cosine);
            float3 normal = float3(cosine, 0.0, sine);
            VegetationVertex output;
            output.position =
                tree.basePosition +
                float3(
                    cosine * tree.trunkRadius,
                    y * tree.height * 0.55,
                    sine * tree.trunkRadius);
            output.normal = normal;
            output.color = _VegetationTrunkColor;
            return output;
        }

        float3 SpherePoint(uint segment, uint ring)
        {
            const uint segments = 10u;
            const uint rings = 5u;
            float longitude = segment / (float)segments * TWO_PI;
            float latitude = ring / (float)rings * PI;
            float sineLatitude;
            float cosineLatitude;
            sincos(latitude, sineLatitude, cosineLatitude);
            float sineLongitude;
            float cosineLongitude;
            sincos(longitude, sineLongitude, cosineLongitude);
            return float3(
                cosineLongitude * sineLatitude,
                cosineLatitude,
                sineLongitude * sineLatitude);
        }

        VegetationVertex BuildNearCrownVertex(uint vertexId, VegetationTree tree)
        {
            const uint segments = 10u;
            uint triangleIndex = vertexId / 3u;
            uint corner = vertexId % 3u;
            uint cell = triangleIndex / 2u;
            bool secondTriangle = (triangleIndex & 1u) != 0u;
            uint segment = cell % segments;
            uint ring = cell / segments;
            uint nextSegment = (segment + 1u) % segments;
            uint selectedSegment;
            uint selectedRing;
            if (!secondTriangle)
            {
                selectedSegment = corner == 2u ? nextSegment : segment;
                selectedRing = corner == 1u ? ring + 1u : ring;
            }
            else
            {
                selectedSegment = corner == 0u ? nextSegment : (corner == 1u ? segment : nextSegment);
                selectedRing = corner == 0u ? ring : ring + 1u;
            }
            float3 sphere = SpherePoint(selectedSegment, selectedRing);
            float2 rotated = RotateVegetation(sphere.xz, tree.rotation);
            sphere.xz = rotated;
            float verticalRadius = max(1.0, tree.height * 0.34);
            float verticalWeight = saturate(sphere.y * 0.5 + 0.5);
            VegetationVertex output;
            output.position =
                tree.basePosition +
                float3(0.0, tree.height * 0.68, 0.0) +
                float3(
                    sphere.x * tree.crownRadius,
                    sphere.y * verticalRadius,
                    sphere.z * tree.crownRadius) +
                VegetationWindOffset(tree, verticalWeight);
            output.normal = normalize(float3(
                sphere.x / max(tree.crownRadius, 0.01),
                sphere.y / max(verticalRadius, 0.01),
                sphere.z / max(tree.crownRadius, 0.01)));
            output.color = tree.color * _VegetationCrownTint;
            return output;
        }

        uint OctahedronIndex(uint vertexId)
        {
            static const uint indices[24] =
            {
                0u, 4u, 2u, 0u, 3u, 4u, 0u, 5u, 3u, 0u, 2u, 5u,
                1u, 2u, 4u, 1u, 4u, 3u, 1u, 3u, 5u, 1u, 5u, 2u
            };
            return indices[vertexId];
        }

        float3 OctahedronPoint(uint index)
        {
            if (index == 0u) return float3(0.0, 1.0, 0.0);
            if (index == 1u) return float3(0.0, -1.0, 0.0);
            if (index == 2u) return float3(1.0, 0.0, 0.0);
            if (index == 3u) return float3(-1.0, 0.0, 0.0);
            if (index == 4u) return float3(0.0, 0.0, 1.0);
            return float3(0.0, 0.0, -1.0);
        }

        VegetationVertex BuildFarCrownVertex(uint vertexId, VegetationTree tree)
        {
            float3 octahedron = OctahedronPoint(OctahedronIndex(vertexId));
            float2 rotated = RotateVegetation(octahedron.xz, tree.rotation);
            octahedron.xz = rotated;
            float verticalRadius = max(1.0, tree.height * 0.34);
            VegetationVertex output;
            output.position =
                tree.basePosition +
                float3(0.0, tree.height * 0.68, 0.0) +
                float3(
                    octahedron.x * tree.crownRadius,
                    octahedron.y * verticalRadius,
                    octahedron.z * tree.crownRadius) +
                VegetationWindOffset(tree, saturate(octahedron.y * 0.5 + 0.5));
            output.normal = normalize(octahedron);
            output.color = tree.color * _VegetationCrownTint;
            return output;
        }

        VegetationVertex BuildVegetationVertex(uint vertexId, uint instanceId)
        {
            VegetationTree tree = LoadVegetationTree(instanceId);
            if (_VegetationGeometryMode == 0)
            {
                return BuildTrunkVertex(vertexId, tree);
            }
            if (_VegetationGeometryMode == 1)
            {
                return BuildNearCrownVertex(vertexId, tree);
            }
            return BuildFarCrownVertex(vertexId, tree);
        }

        float3 VegetationPositionWS(float3 dataPosition)
        {
            return mul(_VegetationDataToWorld, float4(dataPosition, 1.0)).xyz;
        }

        float3 VegetationNormalWS(float3 dataNormal)
        {
            return normalize(mul((float3x3)_VegetationDataToWorld, dataNormal));
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull Back
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex VegetationForwardVertex
            #pragma fragment VegetationForwardFragment
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT

            struct ForwardAttributes
            {
                uint vertexId : SV_VertexID;
                uint instanceId : SV_InstanceID;
            };

            struct ForwardVaryings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                half4 color : COLOR0;
                half fogFactor : TEXCOORD2;
            };

            ForwardVaryings VegetationForwardVertex(ForwardAttributes input)
            {
                VegetationVertex vertex = BuildVegetationVertex(input.vertexId, input.instanceId);
                ForwardVaryings output;
                output.positionWS = VegetationPositionWS(vertex.position);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.normalWS = VegetationNormalWS(vertex.normal);
                output.color = vertex.color;
                output.fogFactor = ComputeFogFactor(output.positionCS.z);
                return output;
            }

            half4 VegetationForwardFragment(ForwardVaryings input) : SV_Target
            {
                half3 normalWS = normalize(input.normalWS);
                Light mainLight = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                half diffuse = saturate(dot(normalWS, mainLight.direction));
                half3 ambient = max(SampleSH(normalWS), 0.0h);
                half3 direct =
                    mainLight.color *
                    diffuse *
                    mainLight.distanceAttenuation *
                    mainLight.shadowAttenuation;
                half3 color = input.color.rgb * (ambient + direct);
                color = MixFog(color, input.fogFactor);
                return half4(color, 1.0h);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            Cull Back
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex VegetationShadowVertex
            #pragma fragment VegetationShadowFragment
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            float3 _LightDirection;
            float3 _LightPosition;

            struct ShadowAttributes
            {
                uint vertexId : SV_VertexID;
                uint instanceId : SV_InstanceID;
            };

            struct ShadowVaryings
            {
                float4 positionCS : SV_POSITION;
            };

            ShadowVaryings VegetationShadowVertex(ShadowAttributes input)
            {
                VegetationVertex vertex = BuildVegetationVertex(input.vertexId, input.instanceId);
                float3 positionWS = VegetationPositionWS(vertex.position);
                float3 normalWS = VegetationNormalWS(vertex.normal);
                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                    float3 lightDirectionWS = normalize(_LightPosition - positionWS);
                #else
                    float3 lightDirectionWS = _LightDirection;
                #endif
                ShadowVaryings output;
                output.positionCS =
                    TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
                #if UNITY_REVERSED_Z
                    output.positionCS.z = min(output.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    output.positionCS.z = max(output.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                return output;
            }

            half4 VegetationShadowFragment(ShadowVaryings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }

    FallBack Off
}
