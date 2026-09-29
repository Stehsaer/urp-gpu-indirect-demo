using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

public class GrassRenderer : ScriptableRendererFeature
{
    [Serializable]
    public class GrassMeshEntry
    {
        public Mesh lod0;
        public Mesh lod1;
    }

    [Serializable]
    public class GrassRendererSettings
    {
        [Header("Meshes (each entry has 2 LODs)")]
        public List<GrassMeshEntry> meshes = new List<GrassMeshEntry>();

        [Header("GPU Driven")]
        public ComputeShader cullingCS;
        public Material grassMaterial;
        public int initialCapacity = 16384;

        [Header("Switches")]
        [Tooltip("Draw object-by-object with the classic vertex path instead of a single indirect draw.")]
        public bool useFallbackDraw = false;
        [Tooltip("Distance based LOD selection. Off renders LOD0 only.")]
        public bool enableLod = true;
        [Tooltip("Frustum + distance culling. Off draws every instance in range.")]
        public bool enableCulling = false;
        [Tooltip("Two-sided subsurface transmission. Light entering the light-facing front face scatters through the blade and lights the back face. Main directional light only.")]
        public bool enableTransmission = true;

        [Header("LOD")]
        public float lod0Distance = 10f;
        public float lod1Distance = 20f;
        public bool cullBeyondLod1 = true;
    }

    [SerializeField] GrassRendererSettings settings;
    GrassRendererPass pass;
    GrassDepthNormalsPass depthNormalsPass;

    public override void Create()
    {
        settings ??= new GrassRendererSettings();

        pass = new GrassRendererPass(settings)
        {
            renderPassEvent = RenderPassEvent.BeforeRenderingGbuffer
        };

        // Renders grass depth + normals into the depth-normals prepass targets so the
        // URP Screen Space Ambient Occlusion feature (which runs immediately after the
        // prepasses) takes the grass into account, both for occlusion and receiving AO.
        depthNormalsPass = new GrassDepthNormalsPass(pass)
        {
            renderPassEvent = RenderPassEvent.BeforeRenderingPrePasses
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (settings == null || settings.grassMaterial == null)
            return;

        renderer.EnqueuePass(depthNormalsPass);
        renderer.EnqueuePass(pass);
    }

    protected override void Dispose(bool disposing)
    {
        pass?.Dispose();
        base.Dispose(disposing);
    }

    // Companion pass that only feeds the depth-normals prepass. All GPU state lives in
    // the shared GrassRendererPass, which records the (single) culling dispatch here.
    class GrassDepthNormalsPass : ScriptableRenderPass
    {
        readonly GrassRendererPass owner;

        public GrassDepthNormalsPass(GrassRendererPass owner)
        {
            this.owner = owner;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            owner.RecordDepthNormals(renderGraph, frameData);
        }
    }

    class GrassRendererPass : ScriptableRenderPass
    {
        readonly GrassRendererSettings settings;

        // Packed geometry (one vertex array, one index array).
        readonly List<GrassVertexGpu> vertexData = new List<GrassVertexGpu>();
        readonly List<uint> indexData = new List<uint>();
        readonly List<MeshLodMetaGpu> lodMetaData = new List<MeshLodMetaGpu>();
        readonly List<MeshBoundsGpu> meshBoundsData = new List<MeshBoundsGpu>();
        int geometryHash;
        int maxIndexCount;

        GraphicsBuffer vertexBuffer;
        GraphicsBuffer indexBuffer;
        GraphicsBuffer meshLodMetaBuffer;
        GraphicsBuffer meshBoundsBuffer;
        int vertexCapacity;
        int indexCapacity;
        int lodMetaCapacity;
        int boundsCapacity;

        // Instance data (uploaded from the external source).
        GraphicsBuffer instanceBuffer;
        GraphicsBuffer meshIndexBuffer;
        int instanceCapacity;
        int meshIndexCapacity;
        int cachedInstanceVersion = int.MinValue;
        int cachedInstanceCount = -1;
        GrassInstanceSource resolvedSource;
        Matrix4x4[] fallbackMatrices;
        int[] fallbackMeshIndices;
        bool loggedNoSource;

        // Compute output.
        GraphicsBuffer visibleInstanceBuffer;
        int visibleCapacity;
        GraphicsBuffer argsBuffer;
        int cullKernel = -1;

        // Shader pass indices (must match IndirectDrawLit.shader).
        const int GBufferPassIndex = 0;
        const int GBufferClassicPassIndex = 1;
        const int DepthNormalsPassIndex = 2;
        const int DepthNormalsClassicPassIndex = 3;

        // Buffer handles imported once per frame. The depth-normals pass records the
        // culling dispatch and the gbuffer pass reuses whichever handles were imported.
        BufferHandle instancesHandle;
        BufferHandle meshIndicesHandle;
        BufferHandle meshLodMetaHandle;
        BufferHandle meshBoundsHandle;
        BufferHandle vertexHandle;
        BufferHandle indexHandle;
        BufferHandle visibleHandle;
        BufferHandle argsHandle;
        bool cullingRecorded;
        bool indirectWarningLogged;

        // Scratch frustum planes for the CPU (classic) culling switch.
        readonly Plane[] cullPlanes = new Plane[6];

        public GrassRendererPass(GrassRendererSettings settings)
        {
            this.settings = settings;
        }

        // The grass shader cannot extend LitInput's UnityPerMaterial cbuffer, so its
        // transmission uniforms live at global scope. Unity still uploads the matching
        // material properties per draw, so the feature simply drives material state.
        // GrassRendererSettings.enableTransmission is the on/off switch; the look is tuned
        // with the transmission parameters on the material.
        void ApplyTransmissionSettings()
        {
            var material = settings.grassMaterial;
            if (material != null && material.HasProperty("_Transmission"))
                material.SetFloat("_Transmission", settings.enableTransmission ? 1f : 0f);
        }

        // A runtime override (set by GrassRenderModeController) takes precedence over the
        // serialized switches so the UI can flip dimensions while the game is running.
        bool UseIndirect => GrassSwitches.overrideActive
            ? GrassSwitches.useIndirect
            : !settings.useFallbackDraw;

        bool LODEnabled => GrassSwitches.overrideActive
            ? GrassSwitches.enableLod
            : settings.enableLod;

        bool CullingEnabled => GrassSwitches.overrideActive
            ? GrassSwitches.enableCulling
            : settings.enableCulling;

        // LOD off collapses both thresholds to infinity so the shader always picks LOD0.
        // This is the "LOD0Distance = LOD1Distance" trick; it needs no shader changes.
        float EffectiveLod0Distance => LODEnabled ? settings.lod0Distance : float.MaxValue;
        float EffectiveLod1Distance => LODEnabled ? settings.lod1Distance : float.MaxValue;

        // Distance culling ("cull beyond LOD1") is part of the culling switch.
        bool CullBeyondLod1 => CullingEnabled && settings.cullBeyondLod1;

        public void Dispose()
        {
            vertexBuffer?.Dispose();
            indexBuffer?.Dispose();
            meshLodMetaBuffer?.Dispose();
            meshBoundsBuffer?.Dispose();
            instanceBuffer?.Dispose();
            meshIndexBuffer?.Dispose();
            visibleInstanceBuffer?.Dispose();
            argsBuffer?.Dispose();

            vertexBuffer = null;
            indexBuffer = null;
            meshLodMetaBuffer = null;
            meshBoundsBuffer = null;
            instanceBuffer = null;
            meshIndexBuffer = null;
            visibleInstanceBuffer = null;
            argsBuffer = null;

            vertexCapacity = indexCapacity = lodMetaCapacity = boundsCapacity = 0;
            instanceCapacity = meshIndexCapacity = visibleCapacity = 0;
            geometryHash = 0;
        }

        // ---------------------------------------------------------------------
        // Buffer helpers
        // ---------------------------------------------------------------------

        static void EnsureBuffer(ref GraphicsBuffer buffer, ref int capacity, int required,
                                 int initial, GraphicsBuffer.Target target, int stride)
        {
            if (buffer != null && required <= capacity)
                return;

            int newCapacity = required <= capacity ? capacity : Mathf.NextPowerOfTwo(Mathf.Max(initial, required));
            if (newCapacity <= 0)
                newCapacity = 1;

            if (buffer == null || capacity != newCapacity)
            {
                buffer?.Dispose();
                capacity = newCapacity;
                buffer = new GraphicsBuffer(target, capacity, stride);
            }
        }

        static int NextCapacity(int current, int required, int initial)
        {
            if (required <= current)
                return current;
            return Mathf.NextPowerOfTwo(Mathf.Max(initial, required));
        }

        // ---------------------------------------------------------------------
        // Geometry / instances
        // ---------------------------------------------------------------------

        int ComputeGeometryHash()
        {
            int hash = 17;
            if (settings.meshes != null)
            {
                for (int i = 0; i < settings.meshes.Count; i++)
                {
                    var e = settings.meshes[i];
                    hash = hash * 31 + (e?.lod0 != null ? e.lod0.GetEntityId().GetHashCode() : 0);
                    hash = hash * 31 + (e?.lod1 != null ? e.lod1.GetEntityId().GetHashCode() : 0);
                }
            }
            return hash;
        }

        void EnsureGeometry()
        {
            int hash = ComputeGeometryHash();
            if (vertexBuffer != null && hash == geometryHash)
                return;

            geometryHash = hash;

            vertexData.Clear();
            indexData.Clear();
            lodMetaData.Clear();
            meshBoundsData.Clear();
            maxIndexCount = 0;

            int meshCount = settings.meshes != null ? settings.meshes.Count : 0;
            for (int m = 0; m < meshCount; m++)
            {
                var entry = settings.meshes[m];
                Vector3 maxBound = new(float.MinValue, float.MinValue, float.MinValue);
                Vector3 minBound = new(float.MaxValue, float.MaxValue, float.MaxValue);

                for (int lod = 0; lod < 2; lod++)
                {
                    Mesh mesh = lod == 0 ? entry.lod0 : entry.lod1;
                    if (mesh == null)
                    {
                        lodMetaData.Add(new MeshLodMetaGpu { indexOffset = 0, indexCount = 0 });
                        continue;
                    }

                    int indexOffset = indexData.Count;
                    AppendMesh(mesh, vertexData, indexData);
                    int indexCount = indexData.Count - indexOffset;
                    lodMetaData.Add(new MeshLodMetaGpu { indexOffset = (uint)indexOffset, indexCount = (uint)indexCount });
                    maxIndexCount = Mathf.Max(maxIndexCount, indexCount);

                    maxBound = Vector3.Max(maxBound, mesh.bounds.center + mesh.bounds.extents);
                    minBound = Vector3.Min(minBound, mesh.bounds.center - mesh.bounds.extents);
                }

                meshBoundsData.Add(new MeshBoundsGpu
                {
                    min = new(minBound.x, minBound.y, minBound.z, 0f),
                    max = new(maxBound.x, maxBound.y, maxBound.z, 0f)
                });
            }

            if (maxIndexCount == 0)
                Debug.LogWarning("Max index count is 0");

            EnsureBuffer(ref vertexBuffer, ref vertexCapacity, vertexData.Count, settings.initialCapacity,
                GraphicsBuffer.Target.Structured, 64);
            EnsureBuffer(ref indexBuffer, ref indexCapacity, indexData.Count, settings.initialCapacity,
                GraphicsBuffer.Target.Structured, 4);
            EnsureBuffer(ref meshLodMetaBuffer, ref lodMetaCapacity, lodMetaData.Count, 2,
                GraphicsBuffer.Target.Structured, 8);
            EnsureBuffer(ref meshBoundsBuffer, ref boundsCapacity, meshBoundsData.Count, 2,
                GraphicsBuffer.Target.Structured, 32);

            if (vertexData.Count > 0) vertexBuffer.SetData(vertexData);
            if (indexData.Count > 0) indexBuffer.SetData(indexData);
            if (lodMetaData.Count > 0) meshLodMetaBuffer.SetData(lodMetaData);
            if (meshBoundsData.Count > 0) meshBoundsBuffer.SetData(meshBoundsData);
        }

        static void AppendMesh(Mesh mesh, List<GrassVertexGpu> vertices, List<uint> indices)
        {
            using var meshDataArray = Mesh.AcquireReadOnlyMeshData(mesh);
            var data = meshDataArray[0];

            int vertexCount = data.vertexCount;
            int vertexBase = vertices.Count;

            using var positions = new NativeArray<Vector3>(vertexCount, Allocator.Temp);
            data.GetVertices(positions);

            bool hasNormals = data.HasVertexAttribute(VertexAttribute.Normal);
            using var normals = new NativeArray<Vector3>(hasNormals ? vertexCount : 0, Allocator.Temp);
            if (hasNormals)
                data.GetNormals(normals);

            bool hasTangents = data.HasVertexAttribute(VertexAttribute.Tangent);
            using var tangents = new NativeArray<Vector4>(hasTangents ? vertexCount : 0, Allocator.Temp);
            if (hasTangents)
                data.GetTangents(tangents);

            bool hasUV = data.HasVertexAttribute(VertexAttribute.TexCoord0);
            using var uvs = new NativeArray<Vector2>(hasUV ? vertexCount : 0, Allocator.Temp);
            if (hasUV)
                data.GetUVs(0, uvs);

            for (int v = 0; v < vertexCount; v++)
            {
                Vector3 p = positions[v];
                Vector3 n = hasNormals ? normals[v] : Vector3.up;
                Vector4 t = hasTangents ? tangents[v] : new Vector4(1f, 0f, 0f, 1f);
                Vector2 uv = hasUV ? uvs[v] : Vector2.zero;

                vertices.Add(new GrassVertexGpu
                {
                    positionOS = new Vector4(p.x, p.y, p.z, 1f),
                    normalOS = new Vector4(n.x, n.y, n.z, 0f),
                    tangentOS = t,
                    uv = new Vector4(uv.x, uv.y, 0f, 0f),
                });
            }

            // Raw index views are owned by the MeshData, so they are not disposed here.
            bool is32 = data.indexFormat == IndexFormat.UInt32;
            NativeArray<ushort> indices16 = default;
            NativeArray<int> indices32 = default;
            if (is32)
                indices32 = data.GetIndexData<int>();
            else
                indices16 = data.GetIndexData<ushort>();

            int subMeshCount = data.subMeshCount;
            for (int s = 0; s < subMeshCount; s++)
            {
                SubMeshDescriptor sm = data.GetSubMesh(s);
                int end = sm.indexStart + sm.indexCount;
                for (int k = sm.indexStart; k < end; k++)
                {
                    int vi = is32 ? indices32[k] : indices16[k];
                    indices.Add((uint)(vi + sm.baseVertex + vertexBase));
                }
            }
        }

        // Culling/LOD should follow the reference camera (if one is set) rather than
        // whichever camera is currently being rendered (scene view, previews, etc.).
        static Camera ResolveCamera(UniversalCameraData cameraData)
        {
            Camera reference = GrassReferenceCamera.Registry.referenceCamera;
            return reference != null ? reference : cameraData.camera;
        }

        GrassInstanceSource ResolveSource()
        {
            if (resolvedSource == null)
                resolvedSource = GrassInstanceSource.Registry.First;

            // Fallback for the rare case where the registry has not been populated yet.
            if (resolvedSource == null)
                resolvedSource = UnityEngine.Object.FindFirstObjectByType<GrassInstanceSource>();

            return resolvedSource;
        }

        void EnsureInstances()
        {
            var source = ResolveSource();
            if (source != null)
                source.EnsureGenerated();

            Matrix4x4[] matrices;
            int[] meshIndices;
            int version;

            if (source != null && source.Count > 0)
            {
                matrices = source.Matrices;
                meshIndices = source.MeshIndices;
                version = source.Version;
            }
            else
            {
                // No usable GrassInstanceSource in the scene: use built-in defaults so the
                // feature still renders. Add a GrassInstanceSource to drive the instances.
                if (fallbackMatrices == null)
                {
                    int meshCount = settings.meshes != null ? Mathf.Max(1, settings.meshes.Count) : 1;
                    GrassInstanceSource.BuildDefault(meshCount, out fallbackMatrices, out fallbackMeshIndices);
                }

                if (!loggedNoSource)
                {
                    loggedNoSource = true;
                    Debug.LogWarning("GrassRenderer: no GrassInstanceSource found in the scene; using built-in fallback instances. " +
                                     "Add a GrassInstanceSource component to provide your own matrices and mesh indices.");
                }

                matrices = fallbackMatrices;
                meshIndices = fallbackMeshIndices;
                version = -1;
            }

            int count = matrices != null ? matrices.Length : 0;

            if (instanceBuffer != null && version == cachedInstanceVersion && count == cachedInstanceCount)
                return;

            cachedInstanceVersion = version;
            cachedInstanceCount = count;

            EnsureBuffer(ref instanceBuffer, ref instanceCapacity, count, settings.initialCapacity,
                GraphicsBuffer.Target.Structured, 64);
            EnsureBuffer(ref meshIndexBuffer, ref meshIndexCapacity, count, settings.initialCapacity,
                GraphicsBuffer.Target.Structured, 4);

            if (count > 0)
            {
                instanceBuffer.SetData(matrices, 0, 0, count);
                meshIndexBuffer.SetData(meshIndices, 0, 0, count);
            }
        }

        Mesh GetLodMesh(int meshIndex, Matrix4x4 worldMatrix, Vector3 cameraPosition)
        {
            if (settings.meshes == null || meshIndex < 0 || meshIndex >= settings.meshes.Count)
                return null;

            var entry = settings.meshes[meshIndex];
            float dist = Vector3.Distance(cameraPosition, worldMatrix.MultiplyPoint3x4(Vector3.zero));

            if (dist < EffectiveLod0Distance)
                return entry.lod0 != null ? entry.lod0 : entry.lod1;
            if (dist < EffectiveLod1Distance)
                return entry.lod1 != null ? entry.lod1 : entry.lod0;
            if (CullBeyondLod1)
                return null;
            return entry.lod1 != null ? entry.lod1 : entry.lod0;
        }

        // Conservative world-space AABB test against the cached frustum planes.
        bool IsVisibleInFrustum(Mesh mesh, Matrix4x4 m)
        {
            Bounds local = mesh.bounds;
            Vector3 center = m.MultiplyPoint3x4(local.center);
            Vector3 e = local.extents;
            Vector3 extents = new Vector3(
                Mathf.Abs(m.m00) * e.x + Mathf.Abs(m.m01) * e.y + Mathf.Abs(m.m02) * e.z,
                Mathf.Abs(m.m10) * e.x + Mathf.Abs(m.m11) * e.y + Mathf.Abs(m.m12) * e.z,
                Mathf.Abs(m.m20) * e.x + Mathf.Abs(m.m21) * e.y + Mathf.Abs(m.m22) * e.z);

            var worldBounds = new Bounds(center, extents * 2f);
            return GeometryUtility.TestPlanesAABB(cullPlanes, worldBounds);
        }

        // ---------------------------------------------------------------------
        // Render graph
        // ---------------------------------------------------------------------

        // Runs at BeforeRenderingPrePasses. Feeds the camera depth + normals targets so the
        // URP Screen Space Ambient Occlusion feature (which executes immediately after the
        // prepasses) includes the grass in its occlusion computation.
        public void RecordDepthNormals(RenderGraph renderGraph, ContextContainer frameData)
        {
            indirectWarningLogged = false;
            cullingRecorded = false;

            if (settings.grassMaterial == null)
                return;

            var resourceData = frameData.Get<UniversalResourceData>();
            var cameraData = frameData.Get<UniversalCameraData>();

            // These targets only exist when a pass (for example SSAO) requested them. If
            // they are missing there is nothing for this pass to contribute.
            if (!resourceData.cameraNormalsTexture.IsValid() || !resourceData.activeDepthTexture.IsValid())
                return;

            EnsureGeometry();
            EnsureInstances();

            bool canUseIndirect = CanUseIndirect();
            if (!UseIndirect || !canUseIndirect)
            {
                RecordClassic(renderGraph, resourceData, cameraData, DepthNormalsClassicPassIndex, true);
                return;
            }

            // The culling dispatch is shared with the gbuffer pass, so it must run here
            // (before both the depth-normals raster and the gbuffer raster).
            RecordIndirectCulling(renderGraph, cameraData);
            RecordIndirectDepthNormals(renderGraph, resourceData);
            cullingRecorded = true;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (settings.grassMaterial == null)
                return;

            ApplyTransmissionSettings();

            var resourceData = frameData.Get<UniversalResourceData>();
            var cameraData = frameData.Get<UniversalCameraData>();

            EnsureGeometry();
            EnsureInstances();

            bool canUseIndirect = CanUseIndirect();
            if (!UseIndirect || !canUseIndirect)
            {
                RecordClassic(renderGraph, resourceData, cameraData, GBufferClassicPassIndex, false);
                return;
            }

            // Normally the culling dispatch was recorded by the depth-normals prepass. If
            // no pass requested the prepass targets this frame, run it here instead.
            if (!cullingRecorded)
                RecordIndirectCulling(renderGraph, cameraData);

            RecordIndirectGBuffer(renderGraph, resourceData);
        }

        bool CanUseIndirect()
        {
            bool canUseIndirect = settings.cullingCS != null
                && SystemInfo.supportsComputeShaders
                && cachedInstanceCount > 0
                && maxIndexCount > 0;

            if (!indirectWarningLogged)
            {
                indirectWarningLogged = true;

                if (settings.cullingCS == null)
                    Debug.LogWarning("Culling shader is empty");
                if (!SystemInfo.supportsComputeShaders)
                    Debug.LogWarning("Doesn't support compute shader");
                if (cachedInstanceCount <= 0)
                    Debug.LogWarning(string.Format("cachedInstanceCount <= 0, got {0}", cachedInstanceCount));
                if (maxIndexCount <= 0)
                    Debug.LogWarning(string.Format("maxIndexCount <= 0, got {0}", maxIndexCount));
            }

            return canUseIndirect;
        }

        // ---------------- Indirect ----------------

        class ComputePassData
        {
            public ComputeShader compute;
            public int cullKernel;
            public int instanceCount;
            public int meshCount;
            public Matrix4x4 vp;
            public Vector3 cameraPosition;
            public float lod0Distance;
            public float lod1Distance;
            public bool cullBeyondLod1;
            public bool enableCulling;
            public uint[] argsTemplate;
            public BufferHandle instances;
            public BufferHandle meshIndices;
            public BufferHandle meshLodMeta;
            public BufferHandle meshBounds;
            public BufferHandle visible;
            public BufferHandle args;
        }

        class IndirectRasterPassData
        {
            public Material material;
            public int shaderPass;
            public BufferHandle vertex;
            public BufferHandle index;
            public BufferHandle visible;
            public BufferHandle args;
        }

        static void ExecuteCompute(ComputePassData data, ComputeGraphContext context)
        {
            var cmd = context.cmd;

            // Single indirect draw: instanceCount = total instances, so culled instances stay
            // in the list and are skipped by their per-instance indexCount (0).
            cmd.SetBufferData(data.args, data.argsTemplate);

            cmd.SetComputeIntParam(data.compute, "_InstanceCount", data.instanceCount);
            cmd.SetComputeIntParam(data.compute, "_MeshCount", data.meshCount);
            cmd.SetComputeMatrixParam(data.compute, "_VPMatrix", data.vp);
            cmd.SetComputeVectorParam(data.compute, "_CameraPositionWS", data.cameraPosition);
            cmd.SetComputeFloatParam(data.compute, "_Lod0Distance", data.lod0Distance);
            cmd.SetComputeFloatParam(data.compute, "_Lod1Distance", data.lod1Distance);
            cmd.SetComputeIntParam(data.compute, "_CullBeyondLod1", data.cullBeyondLod1 ? 1 : 0);
            cmd.SetComputeIntParam(data.compute, "_CullingEnabled", data.enableCulling ? 1 : 0);

            cmd.SetComputeBufferParam(data.compute, data.cullKernel, "_Instances", data.instances);
            cmd.SetComputeBufferParam(data.compute, data.cullKernel, "_MeshIndices", data.meshIndices);
            cmd.SetComputeBufferParam(data.compute, data.cullKernel, "_MeshLodMeta", data.meshLodMeta);
            cmd.SetComputeBufferParam(data.compute, data.cullKernel, "_MeshBounds", data.meshBounds);
            cmd.SetComputeBufferParam(data.compute, data.cullKernel, "_VisibleInstances", data.visible);

            int groups = (data.instanceCount + 63) / 64;
            cmd.DispatchCompute(data.compute, data.cullKernel, groups, 1, 1);
        }

        static void ExecuteIndirectDraw(IndirectRasterPassData data, RasterGraphContext context)
        {
            data.material.SetBuffer("_VisibleInstances", data.visible);
            data.material.SetBuffer("_VertexBuffer", data.vertex);
            data.material.SetBuffer("_IndexBuffer", data.index);

            // One draw for all instances; culled instances have indexCount = 0 in
            // _VisibleInstances, so the vertex shader emits only degenerate vertices.
            context.cmd.DrawProceduralIndirect(Matrix4x4.identity, data.material, data.shaderPass, MeshTopology.Triangles, data.args, 0);
        }

        void RecordIndirectCulling(RenderGraph renderGraph, UniversalCameraData cameraData)
        {
            var compute = settings.cullingCS;
            if (cullKernel < 0)
                cullKernel = compute.FindKernel("CSCullAndBuild");

            // Keep the compute output buffer large enough for all visible instances.
            int requiredVisible = NextCapacity(visibleCapacity, cachedInstanceCount, settings.initialCapacity);
            if (visibleInstanceBuffer == null || visibleCapacity != requiredVisible)
            {
                visibleInstanceBuffer?.Dispose();
                visibleCapacity = requiredVisible;
                visibleInstanceBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, visibleCapacity, 80);
            }

            if (argsBuffer == null)
                argsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 4, sizeof(uint));

            Camera camera = ResolveCamera(cameraData);
            Matrix4x4 vp = camera != null
                ? camera.projectionMatrix * camera.worldToCameraMatrix
                : Matrix4x4.identity;
            Vector3 cameraPosition = camera != null
                ? camera.transform.position
                : cameraData.worldSpaceCameraPos;

            instancesHandle = renderGraph.ImportBuffer(instanceBuffer);
            meshIndicesHandle = renderGraph.ImportBuffer(meshIndexBuffer);
            vertexHandle = renderGraph.ImportBuffer(vertexBuffer);
            indexHandle = renderGraph.ImportBuffer(indexBuffer);
            meshLodMetaHandle = renderGraph.ImportBuffer(meshLodMetaBuffer);
            meshBoundsHandle = renderGraph.ImportBuffer(meshBoundsBuffer);
            visibleHandle = renderGraph.ImportBuffer(visibleInstanceBuffer);
            argsHandle = renderGraph.ImportBuffer(argsBuffer);

            using (var builder = renderGraph.AddComputePass<ComputePassData>("Grass Culling", out var passData))
            {
                passData.compute = compute;
                passData.cullKernel = cullKernel;
                passData.instanceCount = cachedInstanceCount;
                passData.meshCount = settings.meshes != null ? settings.meshes.Count : 0;
                passData.vp = vp;
                passData.cameraPosition = cameraPosition;
                passData.lod0Distance = EffectiveLod0Distance;
                passData.lod1Distance = EffectiveLod1Distance;
                passData.cullBeyondLod1 = CullBeyondLod1;
                passData.enableCulling = CullingEnabled;
                passData.argsTemplate = new uint[] { (uint)maxIndexCount, (uint)cachedInstanceCount, 0u, 0u };
                passData.instances = instancesHandle;
                passData.meshIndices = meshIndicesHandle;
                passData.meshLodMeta = meshLodMetaHandle;
                passData.meshBounds = meshBoundsHandle;
                passData.visible = visibleHandle;
                passData.args = argsHandle;

                builder.UseBuffer(instancesHandle, AccessFlags.Read);
                builder.UseBuffer(meshIndicesHandle, AccessFlags.Read);
                builder.UseBuffer(meshLodMetaHandle, AccessFlags.Read);
                builder.UseBuffer(meshBoundsHandle, AccessFlags.Read);
                builder.UseBuffer(visibleHandle, AccessFlags.ReadWrite);
                builder.UseBuffer(argsHandle, AccessFlags.ReadWrite);

                builder.SetRenderFunc(static (ComputePassData data, ComputeGraphContext context) => ExecuteCompute(data, context));
            }
        }

        void RecordIndirectDepthNormals(RenderGraph renderGraph, UniversalResourceData resourceData)
        {
            using (var builder = renderGraph.AddRasterRenderPass<IndirectRasterPassData>("Grass DepthNormals", out var passData))
            {
                passData.material = settings.grassMaterial;
                passData.shaderPass = DepthNormalsPassIndex;
                passData.vertex = vertexHandle;
                passData.index = indexHandle;
                passData.visible = visibleHandle;
                passData.args = argsHandle;

                builder.UseBuffer(vertexHandle, AccessFlags.Read);
                builder.UseBuffer(indexHandle, AccessFlags.Read);
                builder.UseBuffer(visibleHandle, AccessFlags.Read);
                builder.UseBuffer(argsHandle, AccessFlags.Read);

                builder.SetRenderAttachment(resourceData.cameraNormalsTexture, 0, AccessFlags.Write);
                builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.ReadWrite);

                builder.SetRenderFunc(static (IndirectRasterPassData data, RasterGraphContext context) => ExecuteIndirectDraw(data, context));
            }
        }

        void RecordIndirectGBuffer(RenderGraph renderGraph, UniversalResourceData resourceData)
        {
            using (var builder = renderGraph.AddRasterRenderPass<IndirectRasterPassData>("Grass Indirect", out var passData))
            {
                passData.material = settings.grassMaterial;
                passData.shaderPass = GBufferPassIndex;
                passData.vertex = vertexHandle;
                passData.index = indexHandle;
                passData.visible = visibleHandle;
                passData.args = argsHandle;

                builder.UseBuffer(vertexHandle, AccessFlags.Read);
                builder.UseBuffer(indexHandle, AccessFlags.Read);
                builder.UseBuffer(visibleHandle, AccessFlags.Read);
                builder.UseBuffer(argsHandle, AccessFlags.Read);

                var gbuffer = resourceData.gBuffer;
                for (int i = 0; i < gbuffer.Length; i++)
                    builder.SetRenderAttachment(gbuffer[i], i, AccessFlags.Write);
                builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.ReadWrite);

                builder.SetRenderFunc(static (IndirectRasterPassData data, RasterGraphContext context) => ExecuteIndirectDraw(data, context));
            }
        }

        // ---------------- Classic fallback ----------------

        struct DrawRequest
        {
            public Mesh mesh;
            public Matrix4x4 matrix;
        }

        class ClassicRasterPassData
        {
            public Material material;
            public int shaderPass;
            public List<DrawRequest> draws;
        }

        void RecordClassic(RenderGraph renderGraph, UniversalResourceData resourceData, UniversalCameraData cameraData, int shaderPass, bool depthNormalsOnly)
        {
            var draws = new List<DrawRequest>();
            var source = ResolveSource();
            if (source != null)
                source.EnsureGenerated();

            Matrix4x4[] matrices = source != null && source.Count > 0 ? source.Matrices : fallbackMatrices;
            int[] meshIndices = source != null && source.Count > 0 ? source.MeshIndices : fallbackMeshIndices;

            if (matrices == null)
            {
                int meshCount = settings.meshes != null ? Mathf.Max(1, settings.meshes.Count) : 1;
                GrassInstanceSource.BuildDefault(meshCount, out fallbackMatrices, out fallbackMeshIndices);
                matrices = fallbackMatrices;
                meshIndices = fallbackMeshIndices;
            }

            if (matrices != null && meshIndices != null)
            {
                Camera camera = ResolveCamera(cameraData);
                Vector3 cameraPosition = camera != null
                    ? camera.transform.position
                    : cameraData.worldSpaceCameraPos;

                bool frustumCull = CullingEnabled && camera != null;
                if (frustumCull)
                    GeometryUtility.CalculateFrustumPlanes(camera, cullPlanes);

                int count = Mathf.Min(matrices.Length, meshIndices.Length);
                for (int i = 0; i < count; i++)
                {
                    Matrix4x4 m = matrices[i];
                    Mesh mesh = GetLodMesh(meshIndices[i], m, cameraPosition);
                    if (mesh == null)
                        continue;
                    if (frustumCull && !IsVisibleInFrustum(mesh, m))
                        continue;

                    draws.Add(new DrawRequest { mesh = mesh, matrix = m });
                }
            }

            using (var builder = renderGraph.AddRasterRenderPass<ClassicRasterPassData>(
                depthNormalsOnly ? "Grass DepthNormals Classic" : "Grass Classic", out var passData))
            {
                passData.material = settings.grassMaterial;
                passData.shaderPass = shaderPass;
                passData.draws = draws;

                if (depthNormalsOnly)
                {
                    builder.SetRenderAttachment(resourceData.cameraNormalsTexture, 0, AccessFlags.Write);
                }
                else
                {
                    var gbuffer = resourceData.gBuffer;
                    for (int i = 0; i < gbuffer.Length; i++)
                        builder.SetRenderAttachment(gbuffer[i], i, AccessFlags.Write);
                }
                builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.ReadWrite);

                builder.SetRenderFunc(static (ClassicRasterPassData data, RasterGraphContext context) =>
                {
                    for (int i = 0; i < data.draws.Count; i++)
                    {
                        var d = data.draws[i];
                        context.cmd.DrawMesh(d.mesh, d.matrix, data.material, 0, data.shaderPass);
                    }
                });
            }
        }

        // ---------------------------------------------------------------------
        // GPU layouts (must match the HLSL / compute structs)
        // ---------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        struct GrassVertexGpu
        {
            public Vector4 positionOS;
            public Vector4 normalOS;
            public Vector4 tangentOS;
            public Vector4 uv;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MeshLodMetaGpu
        {
            public uint indexOffset;
            public uint indexCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MeshBoundsGpu
        {
            public Vector4 min;
            public Vector4 max;
        }
    }
}
