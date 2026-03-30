using Unity.Netcode;

/// <summary>
/// Serializable input data sent from the owning client to the server
/// each tick. The server uses this to drive the KCC motor for that player.
///
/// One-frame signals (jump, crouch press/release) are latched by the
/// sender and consumed by the receiver so they survive the RPC round-trip.
/// </summary>
public struct NetworkInputData : INetworkSerializable
{
    // ── Movement ─────────────────────────────────────────────────────
    public float MoveX;      // right/left  (-1..1)
    public float MoveZ;      // forward/back (-1..1)
    public bool  SprintHeld;

    // ── Look ─────────────────────────────────────────────────────────
    public float YawDegrees;
    public float PitchDegrees;

    // ── Actions (latched one-frame signals) ──────────────────────────
    public bool JumpPressed;
    public bool CrouchPressed;
    public bool CrouchReleased;
    public bool FireHeld;

    // ── Serialization ────────────────────────────────────────────────
    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref MoveX);
        serializer.SerializeValue(ref MoveZ);
        serializer.SerializeValue(ref SprintHeld);
        serializer.SerializeValue(ref YawDegrees);
        serializer.SerializeValue(ref PitchDegrees);
        serializer.SerializeValue(ref JumpPressed);
        serializer.SerializeValue(ref CrouchPressed);
        serializer.SerializeValue(ref CrouchReleased);
        serializer.SerializeValue(ref FireHeld);
    }
}
