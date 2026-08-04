Shader "NYCGIS/BFP2 Gpu Indirect URP"
{
    Properties
    {
        _LayerTint ("Layer Tint", Color) = (1, 1, 1, 1)
        _AmbientLift ("Sky Ambient Strength", Range(0, 2)) = 0.72
        _DiffuseBoost ("Sun Direct Strength", Range(0, 3)) = 1.0
        _Bfp2LayerIndex ("BFP2 Layer Index", Float) = 0
        _UseBfp2FacadeArray ("Use BFP2 Facade Array", Float) = 0
        _Bfp2FacadeStrength ("BFP2 Facade Strength", Range(0, 1)) = 0.88
        _Bfp2FacadeWidthMeters ("BFP2 Facade Width Meters", Float) = 42
        _Bfp2FacadeHeightMeters ("BFP2 Facade Height Meters", Float) = 72
        _Bfp2FacadeCellSizeMeters ("BFP2 Facade Random Cell Size Meters", Float) = 16
        _Bfp2FacadeTextureCount ("BFP2 Facade Texture Count", Float) = 0
        _UseBfp2RoofOrthophoto ("Use BFP2 Roof Orthophoto", Float) = 1
        _Bfp2RoofOrthophotoStrength ("BFP2 Roof Orthophoto Strength", Range(0, 1)) = 1
        _FallbackColor("Orthophoto Fallback Color", Color) = (0.34, 0.37, 0.35, 1)
        _OverlayOpacity("LOD0 Overlay Opacity", Range(0, 1)) = 1
        [NoScaleOffset] _Bfp2FacadeArray ("BFP2 Facade Array", 2DArray) = "" {}
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }

        Pass
        {
            Name "BFP2Forward"
            Tags { "LightMode" = "UniversalForward" }

            Cull Back
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            #define NYC_ORTHO_MAX_BASE_PAGES 128
            #define NYC_ORTHO_MAX_LOD0_PAGES 240

            StructuredBuffer<uint> _Bfp2VertexWords;
            StructuredBuffer<uint> _Bfp2VisibleIndices;
            StructuredBuffer<uint> _Bfp2Indices;
            StructuredBuffer<uint> _Bfp2PackedIndices16;
            StructuredBuffer<uint> _Bfp2ClusterIndexBases16;
            StructuredBuffer<uint> _Bfp2VisibleTileWords;
            TEXTURE2D_ARRAY(_Bfp2FacadeArray);
            SAMPLER(sampler_Bfp2FacadeArray);
            TEXTURE2D_ARRAY(_BasePages);
            SAMPLER(sampler_BasePages);
            TEXTURE2D_ARRAY(_Lod0Pages);
            SAMPLER(sampler_Lod0Pages);
            TEXTURE2D(_BasePageTable);
            SAMPLER(sampler_BasePageTable);
            TEXTURE2D(_Lod0PageTable);
            SAMPLER(sampler_Lod0PageTable);

            #include "NYCGISFacadeV2.hlsl"

            float _Bfp2GlobalAmbientLift;
            float _Bfp2GlobalDiffuseBoost;
            half _Bfp2GlobalUseFacadeArray;
            half _Bfp2GlobalFacadeStrength;
            float _Bfp2GlobalFacadeWidthMeters;
            float _Bfp2GlobalFacadeHeightMeters;
            float _Bfp2GlobalFacadeCellSizeMeters;
            half _Bfp2GlobalFacadeTextureCount;
            float _Bfp2GlobalFacadeLayerIndex;
            half _Bfp2GlobalUseRoofOrthophoto;
            half _Bfp2GlobalRoofOrthophotoStrength;

            CBUFFER_START(UnityPerMaterial)
                float4 _LayerTint;
                float _AmbientLift;
                float _DiffuseBoost;
                float3 _Bfp2QuantOrigin;
                float3 _Bfp2QuantScale;
                float _Bfp2VisibleTileTriangles;
                float _Bfp2VisibleTileDescriptorWords;
                float _Bfp2UseClusterLocalIndices16;
                float _Bfp2FlipTriangleWinding;
                float _Bfp2LayerIndex;
                half _UseBfp2FacadeArray;
                half _Bfp2FacadeStrength;
                float _Bfp2FacadeWidthMeters;
                float _Bfp2FacadeHeightMeters;
                float _Bfp2FacadeCellSizeMeters;
                half _Bfp2FacadeTextureCount;
                half _UseBfp2RoofOrthophoto;
                half _Bfp2RoofOrthophotoStrength;
                half4 _FallbackColor;
                half _OverlayOpacity;
                float4 _VirtualTextureBounds;
                float4 _PageTableTexelSize;
                float _PageTableNeighborMode;
                float _BasePageCapacity;
                float _Lod0PageCapacity;
                float4 _BasePageRects[NYC_ORTHO_MAX_BASE_PAGES];
                float4 _Lod0PageRects[NYC_ORTHO_MAX_LOD0_PAGES];
                float4 _BasePageUvScales[NYC_ORTHO_MAX_BASE_PAGES];
                float4 _Lod0PageUvScales[NYC_ORTHO_MAX_LOD0_PAGES];
            CBUFFER_END

            float _NYCGIS_LightingSync;
            float _NYCGIS_SurfaceExposure;
            float4 _NYCGIS_SurfaceTint;
            float _NYCGIS_BuildingFillLight;
            float _NYCGIS_BuildingShadowFloor;
            float _NYCGIS_ShadowVisibilityBoost;
            float _NYCGIS_EnvEnabled;
            float _NYCGIS_EnvCloudiness;
            float _NYCGIS_EnvRainStrength;
            float _NYCGIS_EnvWetness;
            float _NYCGIS_EnvHazeStrength;
            float _NYCGIS_EnvVisibilityMeters;
            float _NYCGIS_EnvPayloadVisibility;
            float4 _NYCGIS_EnvFogColor;
            float _NYCGIS_WeatherActivity;

            struct Attributes
            {
                uint vertexID : SV_VertexID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                half4 color : COLOR0;
                half fogFactor : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
            };

            int Bfp2SignExtend16(uint value)
            {
                int v = (int)(value & 0xffffu);
                return (v >= 32768) ? (v - 65536) : v;
            }

            float3 Bfp2DecodeOct16(int2 packedOct)
            {
                float2 f = clamp(float2(packedOct) / 32767.0, -1.0, 1.0);
                float3 n = float3(f.x, f.y, 1.0 - abs(f.x) - abs(f.y));
                if (n.z < 0.0)
                {
                    float2 folded = (1.0 - abs(n.yx)) * sign(n.xy);
                    n.xy = folded;
                }
                return normalize(n);
            }

            uint Bfp2ResolveSourceVertex(uint proceduralVertexId)
            {
                uint tileTriangleCount =
                    (uint)max(round(_Bfp2VisibleTileTriangles), 0.0);
                if (tileTriangleCount == 0u)
                {
                    return _Bfp2VisibleIndices[proceduralVertexId];
                }

                uint tileIndex;
                uint tileLocalIndex;
                if (tileTriangleCount == 32u)
                {
                    tileIndex = proceduralVertexId / 96u;
                    tileLocalIndex = proceduralVertexId - tileIndex * 96u;
                }
                else
                {
                    tileIndex = proceduralVertexId / 192u;
                    tileLocalIndex = proceduralVertexId - tileIndex * 192u;
                }

                uint descriptorWordCount =
                    (uint)max(round(_Bfp2VisibleTileDescriptorWords), 2.0);
                uint descriptorWord = tileIndex * descriptorWordCount;
                uint firstSourceIndex = _Bfp2VisibleTileWords[descriptorWord + 0u];
                uint packedTileMetadata =
                    _Bfp2VisibleTileWords[descriptorWord + 1u];
                bool useClusterLocalIndices16 =
                    _Bfp2UseClusterLocalIndices16 > 0.5;
                uint validIndexCount =
                    useClusterLocalIndices16
                        ? packedTileMetadata & 0x7fu
                        : packedTileMetadata;
                uint clusterBaseVertex =
                    useClusterLocalIndices16
                        ? _Bfp2ClusterIndexBases16[packedTileMetadata >> 7u]
                        : 0u;
                uint sourceOffset =
                    tileLocalIndex < validIndexCount ? tileLocalIndex : 0u;

                if (tileLocalIndex < validIndexCount &&
                    _Bfp2FlipTriangleWinding > 0.5)
                {
                    uint triangleLane = sourceOffset % 3u;
                    if (triangleLane == 1u)
                    {
                        sourceOffset++;
                    }
                    else if (triangleLane == 2u)
                    {
                        sourceOffset--;
                    }
                }

                uint sourceIndex = firstSourceIndex + sourceOffset;
                if (useClusterLocalIndices16)
                {
                    uint packedWord = _Bfp2PackedIndices16[sourceIndex >> 1u];
                    uint localIndex =
                        (sourceIndex & 1u) == 0u
                            ? packedWord & 0xffffu
                            : packedWord >> 16u;
                    return clusterBaseVertex + localIndex;
                }
                return _Bfp2Indices[sourceIndex];
            }

            float Bfp2Hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float Bfp2Hash31(float3 p)
            {
                p = frac(p * float3(127.1, 311.7, 74.7));
                p += dot(p, p.yzx + 19.19);
                return frac((p.x + p.y) * p.z);
            }

            bool Inside01(float2 uv)
            {
                return all(uv >= 0.0.xx) && all(uv <= 1.0.xx);
            }

            bool HasOrthophotoRgb(half3 rgb)
            {
                return max(max(rgb.r, rgb.g), rgb.b) > 0.01h;
            }

            bool HasOrthophotoCoverage(half4 color)
            {
                return HasOrthophotoRgb(color.rgb);
            }

            float2 VirtualTextureUv(float2 worldXZ)
            {
                return (worldXZ - _VirtualTextureBounds.xy) * _VirtualTextureBounds.zw;
            }

            int DecodePageIndex(half encoded, float capacity)
            {
                int pageIndex = (int)round(encoded * 255.0h) - 1;
                return pageIndex >= 0 && pageIndex < (int)capacity ? pageIndex : -1;
            }

            int ReadBasePageIndex(float2 vtUv)
            {
                half encoded = SAMPLE_TEXTURE2D(_BasePageTable, sampler_BasePageTable, vtUv).r;
                return DecodePageIndex(encoded, _BasePageCapacity);
            }

            int ReadLod0PageIndex(float2 vtUv)
            {
                half encoded = SAMPLE_TEXTURE2D(_Lod0PageTable, sampler_Lod0PageTable, vtUv).r;
                return DecodePageIndex(encoded, _Lod0PageCapacity);
            }
            bool TryUseBasePage(int candidate, float2 worldXZ, out int pageIndex, out float2 pageUv)
            {
                pageIndex = -1;
                pageUv = 0.0.xx;
                if (candidate < 0)
                {
                    return false;
                }

                float4 rect = _BasePageRects[candidate];
                float2 candidateUv = (worldXZ - rect.xy) * rect.zw;
                if (!Inside01(candidateUv))
                {
                    return false;
                }

                pageIndex = candidate;
                pageUv = candidateUv;
                return true;
            }

            bool TryUseLod0Page(int candidate, float2 worldXZ, out int pageIndex, out float2 pageUv)
            {
                pageIndex = -1;
                pageUv = 0.0.xx;
                if (candidate < 0)
                {
                    return false;
                }

                float4 rect = _Lod0PageRects[candidate];
                float2 candidateUv = (worldXZ - rect.xy) * rect.zw;
                if (!Inside01(candidateUv))
                {
                    return false;
                }

                pageIndex = candidate;
                pageUv = candidateUv;
                return true;
            }
            bool TryResolveBasePage(float2 worldXZ, float2 vtUv, out int pageIndex, out float2 pageUv)
            {
                if (TryUseBasePage(ReadBasePageIndex(vtUv), worldXZ, pageIndex, pageUv))
                {
                    return true;
                }

                if (_PageTableNeighborMode < 0.5)
                {
                    return false;
                }

                float2 step = _PageTableTexelSize.xy;
                if (TryUseBasePage(ReadBasePageIndex(saturate(vtUv + float2(-step.x, 0.0))), worldXZ, pageIndex, pageUv)) return true;
                if (TryUseBasePage(ReadBasePageIndex(saturate(vtUv + float2(step.x, 0.0))), worldXZ, pageIndex, pageUv)) return true;
                if (TryUseBasePage(ReadBasePageIndex(saturate(vtUv + float2(0.0, -step.y))), worldXZ, pageIndex, pageUv)) return true;
                if (TryUseBasePage(ReadBasePageIndex(saturate(vtUv + float2(0.0, step.y))), worldXZ, pageIndex, pageUv)) return true;

                if (_PageTableNeighborMode < 1.5)
                {
                    int capacity = min((int)_BasePageCapacity, NYC_ORTHO_MAX_BASE_PAGES);
                    [loop]
                    for (int i = 0; i < capacity; i++)
                    {
                        if (TryUseBasePage(i, worldXZ, pageIndex, pageUv))
                        {
                            return true;
                        }
                    }
                    return false;
                }

                if (TryUseBasePage(ReadBasePageIndex(saturate(vtUv + float2(-step.x, -step.y))), worldXZ, pageIndex, pageUv)) return true;
                if (TryUseBasePage(ReadBasePageIndex(saturate(vtUv + float2(step.x, -step.y))), worldXZ, pageIndex, pageUv)) return true;
                if (TryUseBasePage(ReadBasePageIndex(saturate(vtUv + float2(-step.x, step.y))), worldXZ, pageIndex, pageUv)) return true;
                if (TryUseBasePage(ReadBasePageIndex(saturate(vtUv + float2(step.x, step.y))), worldXZ, pageIndex, pageUv)) return true;

                int capacity = min((int)_BasePageCapacity, NYC_ORTHO_MAX_BASE_PAGES);
                [loop]
                for (int i = 0; i < capacity; i++)
                {
                    if (TryUseBasePage(i, worldXZ, pageIndex, pageUv))
                    {
                        return true;
                    }
                }
                return false;
            }

            bool TryResolveLod0Page(float2 worldXZ, float2 vtUv, out int pageIndex, out float2 pageUv)
            {
                if (TryUseLod0Page(ReadLod0PageIndex(vtUv), worldXZ, pageIndex, pageUv))
                {
                    return true;
                }

                if (_PageTableNeighborMode < 0.5)
                {
                    return false;
                }

                float2 step = _PageTableTexelSize.xy;
                if (TryUseLod0Page(ReadLod0PageIndex(saturate(vtUv + float2(-step.x, 0.0))), worldXZ, pageIndex, pageUv)) return true;
                if (TryUseLod0Page(ReadLod0PageIndex(saturate(vtUv + float2(step.x, 0.0))), worldXZ, pageIndex, pageUv)) return true;
                if (TryUseLod0Page(ReadLod0PageIndex(saturate(vtUv + float2(0.0, -step.y))), worldXZ, pageIndex, pageUv)) return true;
                if (TryUseLod0Page(ReadLod0PageIndex(saturate(vtUv + float2(0.0, step.y))), worldXZ, pageIndex, pageUv)) return true;

                if (_PageTableNeighborMode < 1.5)
                {
                    return false;
                }

                if (TryUseLod0Page(ReadLod0PageIndex(saturate(vtUv + float2(-step.x, -step.y))), worldXZ, pageIndex, pageUv)) return true;
                if (TryUseLod0Page(ReadLod0PageIndex(saturate(vtUv + float2(step.x, -step.y))), worldXZ, pageIndex, pageUv)) return true;
                if (TryUseLod0Page(ReadLod0PageIndex(saturate(vtUv + float2(-step.x, step.y))), worldXZ, pageIndex, pageUv)) return true;
                if (TryUseLod0Page(ReadLod0PageIndex(saturate(vtUv + float2(step.x, step.y))), worldXZ, pageIndex, pageUv)) return true;
                return false;
            }
            half4 SampleBaseLayer(float2 worldXZ, float2 vtUv)
            {
                if (!Inside01(vtUv) || _BasePageCapacity <= 0.5)
                {
                    return half4(_FallbackColor.rgb, 0.0h);
                }

                int pageIndex;
                float2 pageUv;
                if (!TryResolveBasePage(worldXZ, vtUv, pageIndex, pageUv))
                {
                    return half4(_FallbackColor.rgb, 0.0h);
                }

                pageUv *= max(_BasePageUvScales[pageIndex].xy, 0.0001.xx);
                half4 color = SAMPLE_TEXTURE2D_ARRAY(_BasePages, sampler_BasePages, pageUv, pageIndex);
                half4 coverage = SAMPLE_TEXTURE2D_ARRAY_LOD(_BasePages, sampler_BasePages, pageUv, pageIndex, 0.0);
                return HasOrthophotoCoverage(coverage) ? half4(color.rgb, 1.0h) : half4(_FallbackColor.rgb, 0.0h);
            }

            half4 SampleLod0Layer(float2 worldXZ, float2 vtUv)
            {
                if (!Inside01(vtUv) || _Lod0PageCapacity <= 0.5)
                {
                    return half4(0, 0, 0, 0);
                }

                int pageIndex;
                float2 pageUv;
                if (!TryResolveLod0Page(worldXZ, vtUv, pageIndex, pageUv))
                {
                    return half4(0, 0, 0, 0);
                }

                float4 uvScale = _Lod0PageUvScales[pageIndex];
                pageUv *= max(uvScale.xy, 0.0001.xx);
                pageUv = clamp(pageUv, uvScale.zw, max(uvScale.xy - uvScale.zw, uvScale.zw));
                bool partialPage = uvScale.x < 0.999 || uvScale.y < 0.999;
                half4 color = partialPage
                    ? SAMPLE_TEXTURE2D_ARRAY_LOD(_Lod0Pages, sampler_Lod0Pages, pageUv, pageIndex, 0.0)
                    : SAMPLE_TEXTURE2D_ARRAY(_Lod0Pages, sampler_Lod0Pages, pageUv, pageIndex);
                half4 coverage = SAMPLE_TEXTURE2D_ARRAY_LOD(_Lod0Pages, sampler_Lod0Pages, pageUv, pageIndex, 0.0);
                return half4(color.rgb, HasOrthophotoCoverage(coverage) ? 1.0h : 0.0h);
            }
            half4 SampleBfp2Orthophoto(float2 worldXZ)
            {
                float2 vtUv = VirtualTextureUv(worldXZ);
                half4 baseColor = SampleBaseLayer(worldXZ, vtUv);
                half4 lod0Color = SampleLod0Layer(worldXZ, vtUv);
                if (_OverlayOpacity >= 0.999h && lod0Color.a > 0.999h)
                {
                    return lod0Color;
                }

                half lod0Overlay = saturate(lod0Color.a * _OverlayOpacity);
                half3 color = lerp(baseColor.rgb, lod0Color.rgb, lod0Overlay);
                return half4(color, max(baseColor.a, lod0Color.a));
            }

            half3 ApplyBfp2Facade(half3 baseColor, float3 positionWS, half3 normalWS)
            {
                if (_Bfp2GlobalUseFacadeArray < 0.5h || _Bfp2GlobalFacadeTextureCount < 0.5h || abs(_Bfp2LayerIndex - _Bfp2GlobalFacadeLayerIndex) > 0.5)
                {
                    return baseColor;
                }

                half wallMask = 1.0h - smoothstep(0.35h, 0.78h, abs(normalWS.y));
                if (wallMask <= 0.001h)
                {
                    return baseColor;
                }

                int textureCount = max(1, min(64, (int)round(_Bfp2GlobalFacadeTextureCount)));
                float cellSize = max(_Bfp2GlobalFacadeCellSizeMeters, 4.0);
                float horizontal = abs(normalWS.x) > abs(normalWS.z) ? positionWS.z : positionWS.x;
                float cross = abs(normalWS.x) > abs(normalWS.z) ? positionWS.x : positionWS.z;
                float side = abs(normalWS.x) > abs(normalWS.z)
                    ? (normalWS.x > 0.0h ? 0.0 : 1.0)
                    : (normalWS.z > 0.0h ? 2.0 : 3.0);
                float2 cell = floor(float2(horizontal, cross) / cellSize);
                int textureIndex = min(textureCount - 1, (int)floor(Bfp2Hash31(float3(cell, side)) * textureCount));
                float2 uv = frac(float2(
                    horizontal / max(_Bfp2GlobalFacadeWidthMeters, 2.0),
                    positionWS.y / max(_Bfp2GlobalFacadeHeightMeters, 2.0)));
                half3 facade = SAMPLE_TEXTURE2D_ARRAY(_Bfp2FacadeArray, sampler_Bfp2FacadeArray, uv, textureIndex).rgb;
                half facadeValueJitter = lerp(0.86h, 1.12h, (half)Bfp2Hash31(float3(cell + 17.0.xx, side + 5.0)));
                facade *= facadeValueJitter;

                return lerp(baseColor, facade, saturate(_Bfp2GlobalFacadeStrength * wallMask));
            }

            half3 ApplyBfp2RoofOrthophoto(
                half3 baseColor,
                float3 positionWS,
                half3 normalWS,
                out half orthophotoWeight)
            {
                orthophotoWeight = 0.0h;
                if (_Bfp2GlobalUseRoofOrthophoto < 0.5h || _Bfp2GlobalRoofOrthophotoStrength <= 0.001h || abs(_Bfp2LayerIndex - _Bfp2GlobalFacadeLayerIndex) > 0.5)
                {
                    return baseColor;
                }

                half roofMask = smoothstep(0.48h, 0.78h, normalWS.y);
                if (roofMask <= 0.001h)
                {
                    return baseColor;
                }

                half4 ortho = SampleBfp2Orthophoto(positionWS.xz);
                if (ortho.a <= 0.001h)
                {
                    return baseColor;
                }

                orthophotoWeight = saturate(_Bfp2GlobalRoofOrthophotoStrength * roofMask);
                return lerp(baseColor, ortho.rgb, orthophotoWeight);
            }

            Varyings Vert(Attributes input)
            {
                Varyings output;

                uint sourceVertex = Bfp2ResolveSourceVertex(input.vertexID);
                uint wordBase = sourceVertex * 4u;
                uint w0 = _Bfp2VertexWords[wordBase + 0u];
                uint w1 = _Bfp2VertexWords[wordBase + 1u];
                uint w2 = _Bfp2VertexWords[wordBase + 2u];
                uint w3 = _Bfp2VertexWords[wordBase + 3u];

                uint qx = w0 & 0xffffu;
                uint qy = (w0 >> 16) & 0xffffu;
                uint qz = w1 & 0xffffu;
                int nx = Bfp2SignExtend16(w1 >> 16);
                int ny = Bfp2SignExtend16(w2);

                float3 positionOS = _Bfp2QuantOrigin + float3(qx, qy, qz) * _Bfp2QuantScale;
                float3 normalOS = Bfp2DecodeOct16(int2(nx, ny));
                float3 positionWS = TransformObjectToWorld(positionOS);
                float3 normalWS = TransformObjectToWorldNormal(normalOS);

                uint r = (w2 >> 16) & 0xffu;
                uint g = (w2 >> 24) & 0xffu;
                uint b = w3 & 0xffu;
                uint a = (w3 >> 8) & 0xffu;
                half4 color = half4(r, g, b, a) / 255.0h;
                color *= half4(_LayerTint);

                output.positionWS = positionWS;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.normalWS = normalize(normalWS);
                output.color = color;
                output.fogFactor = ComputeFogFactor(output.positionCS.z);
                return output;
            }

            half3 ApplyNYCGISVisualWeather(half3 rgb, float3 positionWS, half3 normalWS)
            {
                if (_NYCGIS_EnvEnabled < 0.5 ||
                    (_NYCGIS_WeatherActivity <= 0.001 && _NYCGIS_EnvPayloadVisibility >= 0.999))
                {
                    return rgb;
                }

                half cloudDim = lerp(1.0h, 0.90h, saturate((half)_NYCGIS_EnvCloudiness));
                half rainDim = lerp(1.0h, 0.82h, saturate((half)_NYCGIS_EnvRainStrength));
                half wetness = saturate((half)_NYCGIS_EnvWetness);
                half payloadLoss = 1.0h - saturate((half)_NYCGIS_EnvPayloadVisibility);
                float visibilityMeters = max(_NYCGIS_EnvVisibilityMeters, 1.0);
                float cameraDistance = distance(positionWS, _WorldSpaceCameraPos.xyz);
                half haze = saturate((half)((1.0 - exp(-3.912 * cameraDistance / visibilityMeters)) * _NYCGIS_EnvHazeStrength));
                haze = saturate(haze + payloadLoss * (0.04h + 0.22h * saturate((half)(cameraDistance / visibilityMeters))));
                half surfaceWetness = wetness * lerp(0.48h, 1.0h, saturate(normalWS.y * 0.5h + 0.5h));
                half rainStreak = smoothstep(0.86h, 1.0h, frac((half)(positionWS.x * 0.071 + positionWS.z * 0.053)));
                half3 wetColor = rgb * lerp(0.70h, 0.62h, rainStreak) + (half3)_NYCGIS_EnvFogColor.rgb * 0.045h;
                rgb = lerp(rgb, wetColor, surfaceWetness * 0.68h);
                rgb *= cloudDim * rainDim;
                half luminance = dot(rgb, half3(0.2126h, 0.7152h, 0.0722h));
                rgb = lerp(rgb, luminance.xxx, payloadLoss * 0.14h);
                return lerp(rgb, (half3)_NYCGIS_EnvFogColor.rgb, haze);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                half3 geometryNormal = normalize((half3)input.normalWS);
                half3 baseColor = input.color.rgb;
                NYCGISFacadeSurface facadeSurface = NYCGIS_ApplyFacadeV2(baseColor, input.positionWS, geometryNormal);
                half3 shadingNormal = facadeSurface.normalWS;
                if (_NYCGIS_FacadeV2Enabled < 0.5)
                {
                    baseColor = ApplyBfp2Facade(baseColor, input.positionWS, geometryNormal);
                    facadeSurface.occlusion = 1.0h;
                    facadeSurface.emission = 0.0h;
                }
                else
                {
                    baseColor = facadeSurface.albedo;
                }
                half roofOrthophotoWeight;
                baseColor = ApplyBfp2RoofOrthophoto(
                    baseColor,
                    input.positionWS,
                    geometryNormal,
                    roofOrthophotoWeight);

                Light mainLight = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                half ndotl = saturate(dot(shadingNormal, mainLight.direction));
                half lightingSync = saturate((half)_NYCGIS_LightingSync);
                half sceneExposure = lerp(1.0h, max((half)_NYCGIS_SurfaceExposure, 0.02h), lightingSync);
                half3 sceneTint = lerp(
                    half3(1.0h, 1.0h, 1.0h),
                    max(half3(_NYCGIS_SurfaceTint.rgb), half3(0.02h, 0.02h, 0.02h)),
                    lightingSync);
                half3 skyAmbient = max(SampleSH(shadingNormal), 0.0h) * _Bfp2GlobalAmbientLift;
                skyAmbient = max(skyAmbient, sceneTint * ((half)_NYCGIS_BuildingFillLight * lightingSync));
                half rawShadow = mainLight.shadowAttenuation;
                half shadowMask = saturate((1.0h - rawShadow) * max((half)_NYCGIS_ShadowVisibilityBoost, 1.0h));
                half boostedShadow = lerp(1.0h, saturate((half)_NYCGIS_BuildingShadowFloor), shadowMask);
                half sunShadow = lerp(rawShadow, boostedShadow, lightingSync);
                half3 sunDirect = mainLight.color * (ndotl * mainLight.distanceAttenuation * sunShadow * _Bfp2GlobalDiffuseBoost);
                half3 lighting = skyAmbient + sunDirect;
                // Orthophotos already contain aerial lighting. Re-lighting them as building
                // albedo blows high-altitude roofs toward white and hides the sampled imagery.
                lighting = lerp(lighting, 1.0h.xxx, roofOrthophotoWeight);
                half surfaceOcclusion = lerp(facadeSurface.occlusion, 1.0h, roofOrthophotoWeight);
                half3 rgb = baseColor * lighting * sceneTint * sceneExposure * surfaceOcclusion;
                rgb += facadeSurface.emission * sceneExposure;
                rgb = ApplyNYCGISVisualWeather(rgb, input.positionWS, shadingNormal);
                if (_NYCGIS_EnvEnabled < 0.5 ||
                    (_NYCGIS_WeatherActivity <= 0.001 && _NYCGIS_EnvPayloadVisibility >= 0.999))
                {
                    rgb = MixFog(rgb, input.fogFactor);
                }
                return half4(rgb, input.color.a);
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
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            #define NYC_ORTHO_MAX_BASE_PAGES 128
            #define NYC_ORTHO_MAX_LOD0_PAGES 240

            StructuredBuffer<uint> _Bfp2VertexWords;
            StructuredBuffer<uint> _Bfp2VisibleIndices;
            StructuredBuffer<uint> _Bfp2Indices;
            StructuredBuffer<uint> _Bfp2PackedIndices16;
            StructuredBuffer<uint> _Bfp2ClusterIndexBases16;
            StructuredBuffer<uint> _Bfp2VisibleTileWords;

            CBUFFER_START(UnityPerMaterial)
                float4 _LayerTint;
                float _AmbientLift;
                float _DiffuseBoost;
                float3 _Bfp2QuantOrigin;
                float3 _Bfp2QuantScale;
                float _Bfp2VisibleTileTriangles;
                float _Bfp2VisibleTileDescriptorWords;
                float _Bfp2UseClusterLocalIndices16;
                float _Bfp2FlipTriangleWinding;
                float _Bfp2LayerIndex;
                half _UseBfp2FacadeArray;
                half _Bfp2FacadeStrength;
                float _Bfp2FacadeWidthMeters;
                float _Bfp2FacadeHeightMeters;
                float _Bfp2FacadeCellSizeMeters;
                half _Bfp2FacadeTextureCount;
                half _UseBfp2RoofOrthophoto;
                half _Bfp2RoofOrthophotoStrength;
                half4 _FallbackColor;
                half _OverlayOpacity;
                float4 _VirtualTextureBounds;
                float4 _PageTableTexelSize;
                float _PageTableNeighborMode;
                float _BasePageCapacity;
                float _Lod0PageCapacity;
                float4 _BasePageRects[NYC_ORTHO_MAX_BASE_PAGES];
                float4 _Lod0PageRects[NYC_ORTHO_MAX_LOD0_PAGES];
                float4 _BasePageUvScales[NYC_ORTHO_MAX_BASE_PAGES];
                float4 _Lod0PageUvScales[NYC_ORTHO_MAX_LOD0_PAGES];
            CBUFFER_END

            uint Bfp2ResolveShadowSourceVertex(uint proceduralVertexId)
            {
                uint tileTriangleCount =
                    (uint)max(round(_Bfp2VisibleTileTriangles), 0.0);
                if (tileTriangleCount == 0u)
                {
                    return _Bfp2VisibleIndices[proceduralVertexId];
                }

                uint tileIndex;
                uint tileLocalIndex;
                if (tileTriangleCount == 32u)
                {
                    tileIndex = proceduralVertexId / 96u;
                    tileLocalIndex = proceduralVertexId - tileIndex * 96u;
                }
                else
                {
                    tileIndex = proceduralVertexId / 192u;
                    tileLocalIndex = proceduralVertexId - tileIndex * 192u;
                }

                uint descriptorWordCount =
                    (uint)max(round(_Bfp2VisibleTileDescriptorWords), 2.0);
                uint descriptorWord = tileIndex * descriptorWordCount;
                uint firstSourceIndex = _Bfp2VisibleTileWords[descriptorWord + 0u];
                uint packedTileMetadata =
                    _Bfp2VisibleTileWords[descriptorWord + 1u];
                bool useClusterLocalIndices16 =
                    _Bfp2UseClusterLocalIndices16 > 0.5;
                uint validIndexCount =
                    useClusterLocalIndices16
                        ? packedTileMetadata & 0x7fu
                        : packedTileMetadata;
                uint clusterBaseVertex =
                    useClusterLocalIndices16
                        ? _Bfp2ClusterIndexBases16[packedTileMetadata >> 7u]
                        : 0u;
                uint sourceOffset =
                    tileLocalIndex < validIndexCount ? tileLocalIndex : 0u;

                if (tileLocalIndex < validIndexCount &&
                    _Bfp2FlipTriangleWinding > 0.5)
                {
                    uint triangleLane = sourceOffset % 3u;
                    if (triangleLane == 1u)
                    {
                        sourceOffset++;
                    }
                    else if (triangleLane == 2u)
                    {
                        sourceOffset--;
                    }
                }

                uint sourceIndex = firstSourceIndex + sourceOffset;
                if (useClusterLocalIndices16)
                {
                    uint packedWord = _Bfp2PackedIndices16[sourceIndex >> 1u];
                    uint localIndex =
                        (sourceIndex & 1u) == 0u
                            ? packedWord & 0xffffu
                            : packedWord >> 16u;
                    return clusterBaseVertex + localIndex;
                }
                return _Bfp2Indices[sourceIndex];
            }

            struct Attributes
            {
                uint vertexID : SV_VertexID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings ShadowPassVertex(Attributes input)
            {
                uint sourceVertex = Bfp2ResolveShadowSourceVertex(input.vertexID);
                uint wordBase = sourceVertex * 4u;
                uint w0 = _Bfp2VertexWords[wordBase + 0u];
                uint w1 = _Bfp2VertexWords[wordBase + 1u];

                uint qx = w0 & 0xffffu;
                uint qy = (w0 >> 16) & 0xffffu;
                uint qz = w1 & 0xffffu;

                float3 positionOS = _Bfp2QuantOrigin + float3(qx, qy, qz) * _Bfp2QuantScale;
                float3 positionWS = TransformObjectToWorld(positionOS);

                Varyings output;
                output.positionCS = TransformWorldToHClip(positionWS);
                return output;
            }

            half4 ShadowPassFragment(Varyings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }

    FallBack Off
}
