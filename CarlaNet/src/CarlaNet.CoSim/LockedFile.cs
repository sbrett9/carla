namespace CarlaNet.CoSim;

/// <summary>One file a compile lock binds: where it is, and the SHA-256 of its bytes.</summary>
/// <param name="Path">As the lock names it, taken against the lock's own directory.</param>
/// <param name="Sha256">Lowercase hex SHA-256 of the file's bytes as the compiler wrote them.</param>
public sealed record LockedFile(string Path, string Sha256);
