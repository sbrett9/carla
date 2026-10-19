namespace CarlaNet.Recording;

/// <summary>
/// Something a SUMO drive session hands a recorder -- its render set, its illumination -- that knows the
/// SUMO release driving the vehicles, so every still the recorder writes can name it in its record of
/// what made it (<see cref="CarlaNet.Types.Provenance.ProducerRecord"/>) without the caller passing it.
/// </summary>
public interface ISumoDriven
{
    /// <summary>The SUMO release the session launched, or null where it could not be read.</summary>
    string? SumoRelease { get; }
}
