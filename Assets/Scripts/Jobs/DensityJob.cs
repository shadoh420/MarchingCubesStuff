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
    [ReadOnly] public int   numPointsPerAxis;
    [ReadOnly] public float3 worldOffset;      // chunk world-space origin

    // ── Noise parameters ─────────────────────────────────────────────
    [ReadOnly] public float noiseScale;
    [ReadOnly] public float amplitude;
    [ReadOnly] public float surfaceY;

    // ── Output ───────────────────────────────────────────────────────
    [WriteOnly] public NativeArray<float> densities;   // length = numPointsPerAxis^3

    // -----------------------------------------------------------------
    public void Execute(int index)
    {
        // Flat index → 3-D grid coords  (must match TerrainChunk.FlatIndex)
        int npa = numPointsPerAxis;
        int x = index % npa;
        int y = (index / npa) % npa;
        int z = index / (npa * npa);

        float wx = x + worldOffset.x;
        float wy = y + worldOffset.y;
        float wz = z + worldOffset.z;

        // Base gradient: solid below surfaceY, air above
        float density = surfaceY - wy;

        // 3-D simplex noise for organic variation
        float3 np = new float3(wx, wy, wz) * noiseScale;
        density += noise.snoise(np) * amplitude;

        densities[index] = density;
    }
}
