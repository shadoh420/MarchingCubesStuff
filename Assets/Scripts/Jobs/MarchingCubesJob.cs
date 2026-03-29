using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

/// <summary>
/// Burst-compiled parallel job that runs the Marching Cubes algorithm.
/// Each work item processes one voxel cube in the chunk.
/// Output: up to 15 vertices per voxel (5 triangles × 3 verts), written
/// to a flat NativeArray at offset [voxelIndex * 15].
/// </summary>
[BurstCompile]
public struct MarchingCubesJob : IJobParallelFor
{
    // ── Grid parameters ──────────────────────────────────────────────
    [ReadOnly] public int chunkSize;          // voxels per axis (e.g. 16)
    [ReadOnly] public int numPointsPerAxis;   // chunkSize + 1 (density samples per axis)
    [ReadOnly] public float isoLevel;         // surface threshold (0 = surface)

    // ── Input ────────────────────────────────────────────────────────
    [ReadOnly] public NativeArray<float> densities;             // length = numPointsPerAxis^3
    [ReadOnly] public NativeArray<int>   edgeTable;             // length = 256
    [ReadOnly] public NativeArray<int>   triTable;              // length = 256 * 16 (flattened)
    [ReadOnly] public NativeArray<int>   cornerIndexAFromEdge;  // length = 12
    [ReadOnly] public NativeArray<int>   cornerIndexBFromEdge;  // length = 12

    // ── Output ───────────────────────────────────────────────────────
    [NativeDisableParallelForRestriction] [WriteOnly]
    public NativeArray<float3> vertices;          // length = numVoxels * 15

    [NativeDisableParallelForRestriction] [WriteOnly]
    public NativeArray<int> vertexCountPerVoxel;  // length = numVoxels

    // -----------------------------------------------------------------
    public void Execute(int index)
    {
        // 1D index → 3D voxel coordinate
        int x = index % chunkSize;
        int y = (index / chunkSize) % chunkSize;
        int z = index / (chunkSize * chunkSize);

        // ── Sample the 8 corner densities ────────────────────────────
        //  Corner layout (Paul Bourke convention):
        //   0:(0,0,0)  1:(1,0,0)  2:(1,1,0)  3:(0,1,0)
        //   4:(0,0,1)  5:(1,0,1)  6:(1,1,1)  7:(0,1,1)
        float d0 = densities[IndexFromCoord(x,     y,     z    )];
        float d1 = densities[IndexFromCoord(x + 1, y,     z    )];
        float d2 = densities[IndexFromCoord(x + 1, y + 1, z    )];
        float d3 = densities[IndexFromCoord(x,     y + 1, z    )];
        float d4 = densities[IndexFromCoord(x,     y,     z + 1)];
        float d5 = densities[IndexFromCoord(x + 1, y,     z + 1)];
        float d6 = densities[IndexFromCoord(x + 1, y + 1, z + 1)];
        float d7 = densities[IndexFromCoord(x,     y + 1, z + 1)];

        // ── Build the cube index (bitmask of which corners are solid) ─
        int cubeIndex = 0;
        if (d0 > isoLevel) cubeIndex |= 1;
        if (d1 > isoLevel) cubeIndex |= 2;
        if (d2 > isoLevel) cubeIndex |= 4;
        if (d3 > isoLevel) cubeIndex |= 8;
        if (d4 > isoLevel) cubeIndex |= 16;
        if (d5 > isoLevel) cubeIndex |= 32;
        if (d6 > isoLevel) cubeIndex |= 64;
        if (d7 > isoLevel) cubeIndex |= 128;

        // Fully inside or fully outside — no surface here
        if (edgeTable[cubeIndex] == 0)
        {
            vertexCountPerVoxel[index] = 0;
            return;
        }

        // ── Walk the triangle table and emit vertices ─────────────────
        int baseOutput = index * 15;
        int count = 0;

        for (int i = 0; i < 15; i += 3)
        {
            int triIdx = cubeIndex * 16 + i;
            int edgeA = triTable[triIdx];
            if (edgeA == -1) break;                        // end-of-list sentinel

            int edgeB = triTable[triIdx + 1];
            int edgeC = triTable[triIdx + 2];

            vertices[baseOutput + count    ] = InterpolateEdge(edgeA, x, y, z, d0, d1, d2, d3, d4, d5, d6, d7);
            vertices[baseOutput + count + 1] = InterpolateEdge(edgeC, x, y, z, d0, d1, d2, d3, d4, d5, d6, d7);
            vertices[baseOutput + count + 2] = InterpolateEdge(edgeB, x, y, z, d0, d1, d2, d3, d4, d5, d6, d7);
            count += 3;
        }

        vertexCountPerVoxel[index] = count;
    }

    // ═════════════════════════════════════════════════════════════════
    //  Helpers
    // ═════════════════════════════════════════════════════════════════

    /// <summary>
    /// Converts (x,y,z) density-grid coordinates into a flat array index.
    /// </summary>
    int IndexFromCoord(int x, int y, int z)
    {
        return x + y * numPointsPerAxis + z * numPointsPerAxis * numPointsPerAxis;
    }

    /// <summary>
    /// Returns the interpolated vertex position along the given edge,
    /// placed at the iso-surface crossing point between the two corners.
    /// </summary>
    float3 InterpolateEdge(int edgeIndex, int x, int y, int z,
        float d0, float d1, float d2, float d3,
        float d4, float d5, float d6, float d7)
    {
        int idxA = cornerIndexAFromEdge[edgeIndex];
        int idxB = cornerIndexBFromEdge[edgeIndex];

        float3 posA = new float3(x, y, z) + CornerOffset(idxA);
        float3 posB = new float3(x, y, z) + CornerOffset(idxB);

        float densA = CornerDensity(idxA, d0, d1, d2, d3, d4, d5, d6, d7);
        float densB = CornerDensity(idxB, d0, d1, d2, d3, d4, d5, d6, d7);

        float t = (isoLevel - densA) / (densB - densA);
        t = math.saturate(t);   // clamp [0,1] for safety

        return posA + t * (posB - posA);
    }

    /// <summary>
    /// Unit offset for each of the 8 cube corners (Paul Bourke order).
    /// </summary>
    static float3 CornerOffset(int corner)
    {
        switch (corner)
        {
            case 0: return new float3(0, 0, 0);
            case 1: return new float3(1, 0, 0);
            case 2: return new float3(1, 1, 0);
            case 3: return new float3(0, 1, 0);
            case 4: return new float3(0, 0, 1);
            case 5: return new float3(1, 0, 1);
            case 6: return new float3(1, 1, 1);
            case 7: return new float3(0, 1, 1);
            default: return float3.zero;
        }
    }

    /// <summary>
    /// Returns the density value for a given corner index.
    /// </summary>
    static float CornerDensity(int corner,
        float d0, float d1, float d2, float d3,
        float d4, float d5, float d6, float d7)
    {
        switch (corner)
        {
            case 0: return d0;
            case 1: return d1;
            case 2: return d2;
            case 3: return d3;
            case 4: return d4;
            case 5: return d5;
            case 6: return d6;
            case 7: return d7;
            default: return 0f;
        }
    }
}
