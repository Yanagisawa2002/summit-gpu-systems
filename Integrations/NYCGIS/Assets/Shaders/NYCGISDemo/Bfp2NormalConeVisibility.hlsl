#ifndef BFP2_NORMAL_CONE_VISIBILITY_INCLUDED
#define BFP2_NORMAL_CONE_VISIBILITY_INCLUDED

// Inputs use the existing object-space camera contract (at most eight views).
// A union output may reject a cluster only when every prepared view rejects it.
// Distance/screen-size selection may use a nearest view; a cone test may not.
bool Bfp2NormalConeRejectsView(float3 center, float3 cameraPosition,
    float3 coneAxis, float coneCutoff)
{
    float3 toCamera = cameraPosition - center;
    float distanceToCamera = length(toCamera);
    // An undefined view direction is not evidence for rejection. The negated
    // comparison also retains a view with a NaN distance conservatively.
    if (!(distanceToCamera > 0.001f))
    {
        return false;
    }
    return dot(coneAxis, toCamera / distanceToCamera) < -coneCutoff;
}

bool Bfp2NormalConeRejectsAllCameras(float3 center, float3 coneAxis, float coneCutoff)
{
    uint cameraCount = min(_Bfp2CameraFrameCount, 8u);
    if (cameraCount == 0u)
    {
        return Bfp2NormalConeRejectsView(
            center, _Bfp2CameraPositionOS, coneAxis, coneCutoff);
    }
    for (uint cameraIndex = 0u; cameraIndex < cameraCount; cameraIndex++)
    {
        if (!Bfp2NormalConeRejectsView(
            center, _Bfp2CameraPositionsOS[cameraIndex].xyz, coneAxis, coneCutoff))
        {
            return false;
        }
    }
    return true;
}

#endif
