// VR/AR/xR lib

#ifndef UNITY_POSTFX_XRLIB
#define UNITY_POSTFX_XRLIB

#if defined(UNITY_SINGLE_PASS_STEREO) || defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)
#define USING_STEREO_MATRICES
#endif

#if defined(USING_STEREO_MATRICES)
    #define glstate_matrix_projection unity_StereoMatrixP[unity_StereoEyeIndex]
    #define unity_MatrixV unity_StereoMatrixV[unity_StereoEyeIndex]
    #define unity_MatrixInvV unity_StereoMatrixInvV[unity_StereoEyeIndex]
    #define unity_MatrixVP unity_StereoMatrixVP[unity_StereoEyeIndex]

    #define unity_CameraProjection unity_StereoCameraProjection[unity_StereoEyeIndex]
    #define unity_CameraInvProjection unity_StereoCameraInvProjection[unity_StereoEyeIndex]
    #define unity_WorldToCamera unity_StereoWorldToCamera[unity_StereoEyeIndex]
    #define unity_CameraToWorld unity_StereoCameraToWorld[unity_StereoEyeIndex]
    #define _WorldSpaceCameraPos unity_StereoWorldSpaceCameraPos[unity_StereoEyeIndex]
#endif

#if defined(USING_STEREO_MATRICES)
CBUFFER_START(UnityStereoGlobals)
    float4x4 unity_StereoMatrixP[2];
    float4x4 unity_StereoMatrixV[2];
    float4x4 unity_StereoMatrixInvV[2];
    float4x4 unity_StereoMatrixVP[2];

    float4x4 unity_StereoCameraProjection[2];
    float4x4 unity_StereoCameraInvProjection[2];
    float4x4 unity_StereoWorldToCamera[2];
    float4x4 unity_StereoCameraToWorld[2];

    float3 unity_StereoWorldSpaceCameraPos[2];
    float4 unity_StereoScaleOffset[2];
CBUFFER_END

CBUFFER_START(UnityStereoEyeIndex)
    int unity_StereoEyeIndex;
CBUFFER_END
#endif

float _RenderViewportScaleFactor;

float2 UnityStereoScreenSpaceUVAdjust(float2 uv, float4 scaleAndOffset)
{
    return uv.xy * scaleAndOffset.xy + scaleAndOffset.zw;
}

float4 UnityStereoScreenSpaceUVAdjust(float4 uv, float4 scaleAndOffset)
{
    return float4(UnityStereoScreenSpaceUVAdjust(uv.xy, scaleAndOffset), UnityStereoScreenSpaceUVAdjust(uv.zw, scaleAndOffset));
}

float2 UnityStereoClampScaleOffset(float2 uv, float4 scaleAndOffset)
{
    return clamp(uv, scaleAndOffset.zw, scaleAndOffset.zw + scaleAndOffset.xy);
}

#if defined(UNITY_SINGLE_PASS_STEREO)
float2 TransformStereoScreenSpaceTex(float2 uv, float w)
{
    float4 scaleOffset = unity_StereoScaleOffset[unity_StereoEyeIndex];
    scaleOffset.xy *= _RenderViewportScaleFactor;
    return uv.xy * scaleOffset.xy + scaleOffset.zw * w;
}

float2 UnityStereoTransformScreenSpaceTex(float2 uv)
{
    return TransformStereoScreenSpaceTex(saturate(uv), 1.0);
}

float4 UnityStereoTransformScreenSpaceTex(float4 uv)
{
    return float4(UnityStereoTransformScreenSpaceTex(uv.xy), UnityStereoTransformScreenSpaceTex(uv.zw));
}

float2 UnityStereoClamp(float2 uv)
{
    float4 scaleOffset = unity_StereoScaleOffset[unity_StereoEyeIndex];
    scaleOffset.xy *= _RenderViewportScaleFactor;
    return UnityStereoClampScaleOffset(uv, scaleOffset);
}
#else
float2 TransformStereoScreenSpaceTex(float2 uv, float w)
{
    return uv * _RenderViewportScaleFactor;
}

float2 UnityStereoTransformScreenSpaceTex(float2 uv)
{
    return TransformStereoScreenSpaceTex(saturate(uv), 1.0);
}

float2 UnityStereoClamp(float2 uv)
{
    float4 scaleOffset = float4(_RenderViewportScaleFactor, _RenderViewportScaleFactor, 0.f, 0.f);
    return UnityStereoClampScaleOffset(uv, scaleOffset);
}
#endif

#endif // UNITY_POSTFX_XRLIB
