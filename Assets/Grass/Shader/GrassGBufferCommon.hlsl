#ifndef GRASS_GBUFFER_COMMON_INCLUDED
#define GRASS_GBUFFER_COMMON_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/Shaders/LitInput.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/GBufferOutput.hlsl"
#if defined(LOD_FADE_CROSSFADE)
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
#endif

// Grass subsurface transmission (simple two-sided model).
// These live outside LitInput.hlsl's UnityPerMaterial cbuffer (which cannot be extended),
// so Unity falls back to per-draw material property upload for them. _Transmission is the
// runtime on/off switch driven by the GrassRenderer feature settings.
half4 _TransmissionColor;
half  _TransmissionAttenuation;
half  _Transmission;

// keep this in sync with LitGBufferPass.hlsl
#if defined(_PARALLAXMAP) && (SHADER_TARGET >= 30)
#define REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR
#endif

#if (defined(_NORMALMAP) || (defined(_PARALLAXMAP) && !defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR))) || defined(_DETAIL)
#define REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR
#endif

struct Varyings
{
    float2 uv                       : TEXCOORD0;

#if defined(REQUIRES_WORLD_SPACE_POS_INTERPOLATOR)
    float3 positionWS               : TEXCOORD1;
#endif

    half3 normalWS                  : TEXCOORD2;
#if defined(REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR)
    half4 tangentWS                 : TEXCOORD3;    // xyz: tangent, w: sign
#endif
#ifdef _ADDITIONAL_LIGHTS_VERTEX
    half3 vertexLighting            : TEXCOORD4;    // xyz: vertex lighting
#endif

#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    float4 shadowCoord              : TEXCOORD5;
#endif

#if defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR)
    half3 viewDirTS                 : TEXCOORD6;
#endif

    DECLARE_LIGHTMAP_OR_SH(staticLightmapUV, vertexSH, 7);
#ifdef DYNAMICLIGHTMAP_ON
    float2 dynamicLightmapUV        : TEXCOORD8; // Dynamic lightmap UVs
#endif

#ifdef USE_APV_PROBE_OCCLUSION
    float4 probeOcclusion           : TEXCOORD9;
#endif

    float4 positionCS               : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

// Shared vertex body (same as LitGBufferPassVertex) parameterized by the object-space
// vertex data and the final object-to-world matrix. Used by both the procedural
// (GPU-driven) vertex path and the classic attribute vertex path.
Varyings BuildGrassVaryings(float3 positionOS, float3 normalOS, float4 tangentOS, float2 texcoord,
                            float2 staticLightmapUV, float2 dynamicLightmapUV, float4x4 objectToWorld)
{
    Varyings output = (Varyings)0;

    float3x3 normalMatrix = (float3x3)objectToWorld;
    float3 positionWS = mul(objectToWorld, float4(positionOS, 1.0)).xyz;

    VertexPositionInputs vertexInput;
    vertexInput.positionWS = positionWS;
    vertexInput.positionVS = TransformWorldToView(positionWS);
    vertexInput.positionCS = TransformWorldToHClip(positionWS);
    vertexInput.positionNDC = vertexInput.positionCS;

    // normalWS and tangentWS already normalize.
    // this is required to avoid skewing the direction during interpolation
    // also required for per-vertex lighting and SH evaluation
    VertexNormalInputs normalInput;
    normalInput.normalWS = normalize(mul(normalMatrix, normalOS));
    normalInput.tangentWS = normalize(mul(normalMatrix, tangentOS.xyz));
    normalInput.bitangentWS = cross(normalInput.normalWS, normalInput.tangentWS);

    output.uv = TRANSFORM_TEX(texcoord, _BaseMap);

    // already normalized from normal transform to WS.
    output.normalWS = normalInput.normalWS;

#if defined(REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR) || defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR)
    // Matches GetOddNegativeScale(): flip the tangent sign when the transform mirrors.
    real sign = tangentOS.w * (determinant(normalMatrix) < 0.0 ? -1.0 : 1.0);
    half4 tangentWS = half4(normalInput.tangentWS.xyz, sign);
#endif

#if defined(REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR)
    output.tangentWS = tangentWS;
#endif

#if defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR)
    half3 viewDirWS = GetWorldSpaceNormalizeViewDir(vertexInput.positionWS);
    half3 viewDirTS = GetViewDirectionTangentSpace(tangentWS, output.normalWS, viewDirWS);
    output.viewDirTS = viewDirTS;
#endif

    OUTPUT_LIGHTMAP_UV(staticLightmapUV, unity_LightmapST, output.staticLightmapUV);
#ifdef DYNAMICLIGHTMAP_ON
    output.dynamicLightmapUV = dynamicLightmapUV.xy * unity_DynamicLightmapST.xy + unity_DynamicLightmapST.zw;
#endif
    OUTPUT_SH4(vertexInput.positionWS, output.normalWS.xyz, GetWorldSpaceNormalizeViewDir(vertexInput.positionWS), output.vertexSH, output.probeOcclusion);

#ifdef _ADDITIONAL_LIGHTS_VERTEX
    half3 vertexLight = VertexLighting(vertexInput.positionWS, normalInput.normalWS);
    output.vertexLighting = vertexLight;
#endif

#if defined(REQUIRES_WORLD_SPACE_POS_INTERPOLATOR)
    output.positionWS = vertexInput.positionWS;
#endif

#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    output.shadowCoord = GetShadowCoord(vertexInput);
#endif

    output.positionCS = vertexInput.positionCS;

    return output;
}

// Copied from LitGBufferPass.hlsl so the GBuffer data flow matches Lit exactly.
void InitializeInputData(Varyings input, half3 normalTS, out InputData inputData)
{
    inputData = (InputData)0;

#if defined(REQUIRES_WORLD_SPACE_POS_INTERPOLATOR)
    inputData.positionWS = input.positionWS;
#endif

    inputData.positionCS = input.positionCS;
    half3 viewDirWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
#if defined(_NORMALMAP) || defined(_DETAIL)
    float sgn = input.tangentWS.w;      // should be either +1 or -1
    float3 bitangent = sgn * cross(input.normalWS.xyz, input.tangentWS.xyz);
    inputData.normalWS = TransformTangentToWorld(normalTS, half3x3(input.tangentWS.xyz, bitangent.xyz, input.normalWS.xyz));
#else
    inputData.normalWS = input.normalWS;
#endif

    inputData.normalWS = NormalizeNormalPerPixel(inputData.normalWS);
    inputData.viewDirectionWS = viewDirWS;

#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    inputData.shadowCoord = input.shadowCoord;
#elif defined(MAIN_LIGHT_CALCULATE_SHADOWS)
    inputData.shadowCoord = TransformWorldToShadowCoord(inputData.positionWS);
#else
    inputData.shadowCoord = float4(0, 0, 0, 0);
#endif

    inputData.fogCoord = 0.0; // we don't apply fog in the gbuffer pass

#ifdef _ADDITIONAL_LIGHTS_VERTEX
    inputData.vertexLighting = input.vertexLighting.xyz;
#else
    inputData.vertexLighting = half3(0, 0, 0);
#endif

    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
}

// Copied from LitGBufferPass.hlsl.
void InitializeBakedGIData(Varyings input, inout InputData inputData)
{
#if defined(_SCREEN_SPACE_IRRADIANCE)
    inputData.bakedGI = SAMPLE_GI(_ScreenSpaceIrradiance, input.positionCS.xy, inputData.normalWS);
#elif defined(DYNAMICLIGHTMAP_ON)
    inputData.bakedGI = SAMPLE_GI(input.staticLightmapUV, input.dynamicLightmapUV, input.vertexSH, inputData.normalWS);
    inputData.shadowMask = SAMPLE_SHADOWMASK(input.staticLightmapUV);
#elif !defined(LIGHTMAP_ON) && (defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2))
    inputData.bakedGI = SAMPLE_GI(input.vertexSH,
        GetAbsolutePositionWS(inputData.positionWS),
        inputData.normalWS,
        inputData.viewDirectionWS,
        inputData.positionCS.xy,
        input.probeOcclusion,
        inputData.shadowMask);
#else
    inputData.bakedGI = SAMPLE_GI(input.staticLightmapUV, input.vertexSH, inputData.normalWS);
    inputData.shadowMask = SAMPLE_SHADOWMASK(input.staticLightmapUV);
#endif
}

// Simple two-sided transmission (main directional light only). The camera-facing side is
// shaded by the deferred pass with the stored (flipped) normal. Here we add the light that
// comes through from the opposite side: a Lambert term using the flipped normal, scaled by
// the attenuation.
half3 ComputeGrassTransmission(SurfaceData surfaceData, InputData inputData, Light mainLight)
{
    half3 L = mainLight.direction;

    // inputData.normalWS is the camera-facing normal (already flipped for back faces).
    half ndl = dot(inputData.normalWS, L);

    // Flipped-normal Lambert.
    half lambert = saturate(-ndl);

    return mainLight.color * surfaceData.albedo * _TransmissionColor.rgb * (_TransmissionAttenuation * lambert);
}

// Same as LitGBufferPassFragment, with a safety alpha clip so the grass cutout keeps
// working even when the material has not enabled _ALPHATEST_ON.
GBufferFragOutput Frag(Varyings input)
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

#if defined(_PARALLAXMAP)
    #if defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR)
        half3 viewDirTS = input.viewDirTS;
    #else
        half3 viewDirWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
        half3 viewDirTS = GetViewDirectionTangentSpace(input.tangentWS, input.normalWS, viewDirWS);
    #endif
    ApplyPerPixelDisplacement(viewDirTS, input.uv);
#endif

    SurfaceData surfaceData;
    InitializeStandardLitSurfaceData(input.uv, surfaceData);

    // The original placeholder always clipped; ensure the cutout works without _ALPHATEST_ON.
    clip(surfaceData.alpha - _Cutoff);

#ifdef LOD_FADE_CROSSFADE
    LODFadeCrossFade(input.positionCS);
#endif

    InputData inputData;
    InitializeInputData(input, surfaceData.normalTS, inputData);

    // Two-sided rendering: flip the world normal for back-facing fragments so the
    // deferred direct lighting and the transmission term use a consistent orientation.
    // Derived from the normal/view relation (no VFACE semantic, which fails to compile
    // in a nested fragment-input struct on Vulkan).
    half facing = (dot(inputData.normalWS, inputData.viewDirectionWS) >= 0.0h) ? 1.0h : -1.0h;
    inputData.normalWS *= facing;

    SETUP_DEBUG_TEXTURE_DATA(inputData, UNDO_TRANSFORM_TEX(input.uv, _BaseMap));

#if defined(_DBUFFER)
    ApplyDecalToSurfaceData(input.positionCS, surfaceData, inputData);
#endif

    InitializeBakedGIData(input, inputData);

    BRDFData brdfData;
    InitializeBRDFData(surfaceData.albedo, surfaceData.metallic, surfaceData.specular, surfaceData.smoothness, surfaceData.alpha, brdfData);

    Light mainLight = GetMainLight(inputData.shadowCoord, inputData.positionWS, inputData.shadowMask);
    MixRealtimeAndBakedGI(mainLight, inputData.normalWS, inputData.bakedGI, inputData.shadowMask);

    half3 color = GlobalIllumination(brdfData, (BRDFData)0, 0,
                                              inputData.bakedGI, surfaceData.occlusion, inputData.positionWS,
                                              inputData.normalWS, inputData.viewDirectionWS, inputData.normalizedScreenSpaceUV);

    // Add the two-sided transmission to the lighting buffer (same channel as emission/GI),
    // so it survives the deferred lighting pass which only adds direct light on top.
    if (_Transmission > 0.5h)
        color += ComputeGrassTransmission(surfaceData, inputData, mainLight);

    GBufferFragOutput output = PackGBuffersBRDFData(brdfData, inputData, surfaceData.smoothness, surfaceData.emission + color, surfaceData.occlusion);

#if defined(GBUFFER_FEATURE_RENDERING_LAYERS)
    // The grass is drawn with DrawProceduralIndirect, so Unity never sets the built-in
    // unity_RenderingLayer (it stays 0). The G-buffer therefore records rendering layer 0,
    // and URP's light-layer mask (ClusterDeferred/StencilDeferred) rejects the grass for
    // lights whose Rendering Layers mask does not include layer 0. Report the default mesh
    // rendering layer (bit 0) so lighting behaves the same as regular renderers.
    output.meshRenderingLayers = 1u;
#endif

    return output;
}

// Mirrors LitDepthNormalsPass's DepthNormalsFragment. Used by the grass
// DepthNormals passes so the Screen Space Ambient Occlusion renderer feature can
// read the grass surface normals and depth from the depth-normals prepass.
half4 FragDepthNormals(Varyings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

#if defined(_PARALLAXMAP)
    #if defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR)
        half3 viewDirTS = input.viewDirTS;
    #else
        half3 viewDirWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
        half3 viewDirTS = GetViewDirectionTangentSpace(input.tangentWS, input.normalWS, viewDirWS);
    #endif
    ApplyPerPixelDisplacement(viewDirTS, input.uv);
#endif

    SurfaceData surfaceData;
    InitializeStandardLitSurfaceData(input.uv, surfaceData);

    // The original placeholder always clipped; ensure the cutout works without _ALPHATEST_ON.
    clip(surfaceData.alpha - _Cutoff);

#ifdef LOD_FADE_CROSSFADE
    LODFadeCrossFade(input.positionCS);
#endif

    InputData inputData;
    InitializeInputData(input, surfaceData.normalTS, inputData);

    // Match the GBuffer pass: flip the normal for back-facing fragments so SSAO sees a
    // consistent two-sided surface.
    half facing = (dot(inputData.normalWS, inputData.viewDirectionWS) >= 0.0h) ? 1.0h : -1.0h;
    inputData.normalWS *= facing;

#if defined(_GBUFFER_NORMALS_OCT)
    float3 normalWS = normalize(inputData.normalWS);
    float2 octNormalWS = PackNormalOctQuadEncode(normalWS);          // values between [-1, +1]
    float2 remappedOctNormalWS = saturate(octNormalWS * 0.5 + 0.5);  // values between [ 0, +1]
    return half4(PackFloat2To888(remappedOctNormalWS), 0.0);
#else
    return half4(NormalizeNormalPerPixel(inputData.normalWS), 0.0);
#endif
}

#endif
