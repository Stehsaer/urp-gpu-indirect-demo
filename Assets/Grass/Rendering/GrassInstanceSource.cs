using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// External source of grass instances.
/// Exposes a local-to-world matrix array plus a mesh index array, e.g.
/// matrices = [M1, M2, M3], meshIndices = [0, 1, 0] means:
/// mesh0 is drawn with M1 and M3, mesh1 is drawn with M2.
///
/// Generation places a dense, even carpet of grass meshes (indices 0..3) over a square
/// field, then sprinkles flower meshes (indices 4..6) in a handful of soft clusters so
/// the field reads as distinctly dense/sparse rather than uniformly noisy.
/// </summary>
[DisallowMultipleComponent]
public class GrassInstanceSource : MonoBehaviour
{
    // Renderer features live in assets and cannot reference scene objects, so instances
    // register themselves here and the render feature looks them up at runtime.
    public static class Registry
    {
        public static readonly List<GrassInstanceSource> sources = new List<GrassInstanceSource>();
        public static GrassInstanceSource First => sources.Count > 0 ? sources[0] : null;
    }

    public struct ScatterParams
    {
        public float fieldSize;
        public int seed;

        public int grassMeshCount;
        public float grassCellSize;
        public float grassJitter;

        public int flowerMeshStart;
        public int flowerMeshCount;
        public int flowerClusterCount;
        public int flowerClusterMin;
        public int flowerClusterMax;
        public float flowerClusterRadius;
        public float flowerClusterFalloff;

        public Vector2 grassScaleRange;
        public Vector2 flowerScaleRange;
        public Vector2 yawRange;

        public Matrix4x4 grassExtra;
        public Matrix4x4 flowerExtra;
    }

    [Header("Field")]
    [Tooltip("Side length of the square (XZ) the instances are scattered in.")]
    public float fieldSize = 30f;
    public int seed = 1234;

    [Header("Grass (dense, even scatter)")]
    public int grassMeshCount = 4;
    [Tooltip("Jittered-grid cell size. Smaller = denser carpet.")]
    public float grassCellSize = 0.4f;
    [Range(0f, 1f)] public float grassJitter = 0.9f;
    public Vector2 grassScaleRange = new Vector2(0.8f, 1.2f);

    [Header("Flowers (soft dense/sparse clusters)")]
    [Tooltip("First mesh index used by flowers. Grasses occupy indices before this.")]
    public int flowerMeshStart = 4;
    public int flowerMeshCount = 3;
    public int flowerClusterCount = 12;
    public Vector2Int flowerClusterSize = new Vector2Int(10, 45);
    public float flowerClusterRadius = 3.5f;
    [Tooltip("Radius bias exponent. >1 packs flowers toward the cluster center.")]
    public float flowerClusterFalloff = 2.5f;
    public Vector2 flowerScaleRange = new Vector2(0.7f, 1.3f);

    [Header("Rotation")]
    public Vector2 yawRange = new Vector2(0f, 360f);

    [Header("Extra Transform (baked into every matrix)")]
    public Vector3 extraPosition = Vector3.zero;
    public Vector3 extraEulerAngles = new Vector3(-90f, 0f, 0f);
    public Vector3 grassExtraScale = Vector3.one * 50f;
    public Vector3 flowerExtraScale = Vector3.one * 50f;

    [SerializeField, HideInInspector] Matrix4x4[] m_Matrices = new Matrix4x4[0];
    [SerializeField, HideInInspector] int[] m_MeshIndices = new int[0];
    [SerializeField, HideInInspector] int m_Version;
    [SerializeField, HideInInspector] int m_ParameterHash;

    public Matrix4x4[] Matrices => m_Matrices;
    public int[] MeshIndices => m_MeshIndices;
    public int Version => m_Version;
    public int Count => m_Matrices != null ? m_Matrices.Length : 0;

    void OnEnable()
    {
        if (!Registry.sources.Contains(this))
            Registry.sources.Add(this);

        EnsureGenerated();
    }

    void OnDisable()
    {
        Registry.sources.Remove(this);
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        // Regenerate as soon as any generation parameter changes in the inspector.
        EnsureGenerated();
    }
#endif

    /// <summary>
    /// Regenerates the instances when they are missing or when the generation
    /// parameters changed. Safe to call every frame.
    /// </summary>
    public void EnsureGenerated()
    {
        if (m_Matrices == null || m_Matrices.Length == 0 || ComputeParameterHash() != m_ParameterHash)
            Generate();
    }

    [ContextMenu("Generate Instances")]
    public void Generate()
    {
        Build(CurrentParams(), out m_Matrices, out m_MeshIndices);
        m_ParameterHash = ComputeParameterHash();
        m_Version++;
    }

    ScatterParams CurrentParams()
    {
        int clusterMin = Mathf.Max(0, flowerClusterSize.x);
        int clusterMax = Mathf.Max(clusterMin, flowerClusterSize.y);
        return new ScatterParams
        {
            fieldSize = fieldSize,
            seed = seed,
            grassMeshCount = grassMeshCount,
            grassCellSize = grassCellSize,
            grassJitter = grassJitter,
            flowerMeshStart = flowerMeshStart,
            flowerMeshCount = flowerMeshCount,
            flowerClusterCount = flowerClusterCount,
            flowerClusterMin = clusterMin,
            flowerClusterMax = clusterMax,
            flowerClusterRadius = flowerClusterRadius,
            flowerClusterFalloff = flowerClusterFalloff,
            grassScaleRange = grassScaleRange,
            flowerScaleRange = flowerScaleRange,
            yawRange = yawRange,
            grassExtra = Matrix4x4.TRS(extraPosition, Quaternion.Euler(extraEulerAngles), grassExtraScale),
            flowerExtra = Matrix4x4.TRS(extraPosition, Quaternion.Euler(extraEulerAngles), flowerExtraScale),
        };
    }

    int ComputeParameterHash()
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + fieldSize.GetHashCode();
            h = h * 31 + seed;
            h = h * 31 + grassMeshCount;
            h = h * 31 + grassCellSize.GetHashCode();
            h = h * 31 + grassJitter.GetHashCode();
            h = h * 31 + flowerMeshStart;
            h = h * 31 + flowerMeshCount;
            h = h * 31 + flowerClusterCount;
            h = h * 31 + flowerClusterSize.GetHashCode();
            h = h * 31 + flowerClusterRadius.GetHashCode();
            h = h * 31 + flowerClusterFalloff.GetHashCode();
            h = h * 31 + grassScaleRange.GetHashCode();
            h = h * 31 + flowerScaleRange.GetHashCode();
            h = h * 31 + yawRange.GetHashCode();
            h = h * 31 + extraPosition.GetHashCode();
            h = h * 31 + extraEulerAngles.GetHashCode();
            h = h * 31 + grassExtraScale.GetHashCode();
            h = h * 31 + flowerExtraScale.GetHashCode();
            return h;
        }
    }

    /// <summary>
    /// Builds a default set for the renderer when no GrassInstanceSource exists in the scene.
    /// The first <paramref name="meshCount"/> meshes are treated as grass, any beyond the
    /// first four are treated as flowers.
    /// </summary>
    public static void BuildDefault(int meshCount, out Matrix4x4[] matrices, out int[] meshIndices)
    {
        const int grassMeshCount = 4;
        var p = new ScatterParams
        {
            fieldSize = 30f,
            seed = 1234,
            grassMeshCount = Mathf.Clamp(meshCount, 0, grassMeshCount),
            grassCellSize = 0.4f,
            grassJitter = 0.9f,
            flowerMeshStart = grassMeshCount,
            flowerMeshCount = Mathf.Max(0, meshCount - grassMeshCount),
            flowerClusterCount = 12,
            flowerClusterMin = 10,
            flowerClusterMax = 45,
            flowerClusterRadius = 3.5f,
            flowerClusterFalloff = 2.5f,
            grassScaleRange = new Vector2(0.8f, 1.2f),
            flowerScaleRange = new Vector2(0.7f, 1.3f),
            yawRange = new Vector2(0f, 360f),
            grassExtra = Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(-90f, 0f, 0f), Vector3.one * 50f),
            flowerExtra = Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(-90f, 0f, 0f), Vector3.one * 50f),
        };
        Build(p, out matrices, out meshIndices);
    }

    public static void Build(in ScatterParams p, out Matrix4x4[] matrices, out int[] meshIndices)
    {
        var rng = new System.Random(p.seed);
        var mats = new List<Matrix4x4>();
        var meshes = new List<int>();

        float half = Mathf.Max(0f, p.fieldSize) * 0.5f;

        // --- Grass: jittered grid gives dense but even, non-overlapping coverage. ---
        if (p.grassMeshCount > 0 && p.grassCellSize > 0f && half > 0f)
        {
            int cells = Mathf.Max(1, Mathf.CeilToInt(p.fieldSize / p.grassCellSize));
            float cell = p.fieldSize / cells;
            for (int z = 0; z < cells; z++)
            {
                for (int x = 0; x < cells; x++)
                {
                    float jx = ((float)rng.NextDouble() - 0.5f) * cell * p.grassJitter;
                    float jz = ((float)rng.NextDouble() - 0.5f) * cell * p.grassJitter;
                    Vector3 pos = new Vector3(-half + (x + 0.5f) * cell + jx, 0f,
                                              -half + (z + 0.5f) * cell + jz);
                    AddInstance(mats, meshes, pos,
                        PickMesh(rng, 0, p.grassMeshCount),
                        RandomScale(rng, p.grassScaleRange),
                        RandomYaw(rng, p.yawRange), p.grassExtra);
                }
            }
        }

        // --- Flowers: soft clusters -> dense cores fading into sparse edges. ---
        if (p.flowerMeshCount > 0 && p.flowerClusterCount > 0 && p.flowerClusterRadius > 0f && half > 0f)
        {
            float limit = Mathf.Max(0f, half - p.flowerClusterRadius);
            for (int c = 0; c < p.flowerClusterCount; c++)
            {
                Vector2 center = new Vector2(
                    ((float)rng.NextDouble() * 2f - 1f) * limit,
                    ((float)rng.NextDouble() * 2f - 1f) * limit);

                int count = rng.Next(p.flowerClusterMin, p.flowerClusterMax + 1);
                for (int k = 0; k < count; k++)
                {
                    // Bias the radius toward the center: r = u^falloff * radius.
                    float r = Mathf.Pow((float)rng.NextDouble(), p.flowerClusterFalloff) * p.flowerClusterRadius;
                    float a = (float)rng.NextDouble() * Mathf.PI * 2f;
                    Vector3 pos = new Vector3(center.x + Mathf.Cos(a) * r, 0f, center.y + Mathf.Sin(a) * r);
                    AddInstance(mats, meshes, pos,
                        PickMesh(rng, p.flowerMeshStart, p.flowerMeshCount),
                        RandomScale(rng, p.flowerScaleRange),
                        RandomYaw(rng, p.yawRange), p.flowerExtra);
                }
            }
        }

        matrices = mats.ToArray();
        meshIndices = meshes.ToArray();
    }

    static void AddInstance(List<Matrix4x4> mats, List<int> meshes, Vector3 pos,
                            int mesh, float scale, Quaternion yaw, Matrix4x4 extra)
    {
        mats.Add(Matrix4x4.TRS(pos, yaw, Vector3.one * scale) * extra);
        meshes.Add(mesh);
    }

    static int PickMesh(System.Random rng, int start, int count) => start + rng.Next(count);

    static float RandomScale(System.Random rng, Vector2 range)
        => Mathf.Lerp(range.x, range.y, (float)rng.NextDouble());

    static Quaternion RandomYaw(System.Random rng, Vector2 range)
        => Quaternion.Euler(0f, Mathf.Lerp(range.x, range.y, (float)rng.NextDouble()), 0f);
}
