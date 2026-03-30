using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

/// <summary>
/// Burst-compiled parallel job that fills a density field using 3-D simplex
/// noise.  Each work item computes one density sample.
///
/// This replaces the main-thread PopulateDensityField() loop so that
/// density generation runs entirely on worker threads and benefits from
/// Burst SIMD vectorisation.
///
/// Designed to be chained with MarchingCubesJob via JobHandle dependency:
///   DensityJob handle → MarchingCubesJob.Schedule(..., densityHandle)
/// </summary>
[BurstCompile]
public struct DensityJob : IJobParallelFor
{
    // ── Grid parameters ──────────────────────────────────────────────
    [ReadOnly] public int   numPointsPerAxis;  // lodPointsPerAxis (= ChunkSize / step + 1)
    [ReadOnly] public float3 worldOffset;      // chunk world-space origin
    [ReadOnly] public int   vertexStep;        // LOD stride: 1 = full, 2 = half, 4 = quarter

    // ── Noise parameters ─────────────────────────────────────────────
    [ReadOnly] public float noiseScale;
    [ReadOnly] public float amplitude;
    [ReadOnly] public float surfaceY;

    // ── Output ───────────────────────────────────────────────────────
    [WriteOnly] public NativeArray<float> densities;   // capacity = max LOD0 size; we write [0..lodTotal)

    // -----------------------------------------------------------------
    public void Execute(int index)
    {
        // Flat index → 3-D LOD grid coords
        int npa = numPointsPerAxis;
        int lx = index % npa;
        int ly = (index / npa) % npa;
        int lz = index / (npa * npa);

        // Multiply by step to get the actual world-space sample position
        float wx = lx * vertexStep + worldOffset.x;
        float wy = ly * vertexStep + worldOffset.y;
        float wz = lz * vertexStep + worldOffset.z;

        // Base gradient: solid below surfaceY, air above
        float density = surfaceY - wy;

        // 3-D simplex noise for organic variation
        float3 np = new float3(wx, wy, wz) * noiseScale;
        density += noise.snoise(np) * amplitude;

        densities[index] = density;
    }
}
