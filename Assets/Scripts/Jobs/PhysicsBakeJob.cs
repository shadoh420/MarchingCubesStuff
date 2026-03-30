using Unity.Jobs;
using UnityEngine;

/// <summary>
/// Runs Physics.BakeMesh on a worker thread so the expensive collision-tree
/// build does NOT stall the main thread.
///
/// Usage:
///   1. Apply vertices/triangles to the Mesh on the main thread.
///   2. Schedule this job with the mesh's GetInstanceID().
///   3. After the job completes, assign meshCollider.sharedMesh = mesh
///      on the main thread (cheap pointer swap — bake data already exists).
///
/// NOTE: Physics.BakeMesh is thread-safe but NOT Burst-compatible,
///       so this job intentionally has no [BurstCompile] attribute.
/// </summary>
public struct PhysicsBakeJob : IJob
{
    public int meshInstanceId;
    public bool convex;

    public void Execute()
    {
        Physics.BakeMesh(meshInstanceId, convex);
    }
}
