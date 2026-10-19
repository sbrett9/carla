namespace CarlaNet.CoSim;

/// <summary>
/// The session's hold on which of a world's rendering layers are drawn: written once before the
/// first tick, fixed for the run, and given back when the session lets go.
/// </summary>
/// <remarks>
/// <para><b>Why the bridge writes them at all.</b> What is in frame is a property of the corpus, not
/// a preference of whoever launched the run. The generated road mesh is a flat grey ribbon drawn
/// over the photogrammetry of the real road surface, and the generated traffic lights and signs are
/// a limited set of meshes frequently misaligned against that photogrammetry; both are rendering
/// artefacts in every captured frame, and a detector trained on them learns the artefact. So the
/// session decides, at its start, rather than leaving the decision to whichever script happened to
/// launch it.</para>
///
/// <para><b>Fixed for the run, not toggled during it.</b> A layer that changed halfway through would
/// make two frames of one capture incomparable with nothing in the record saying why. Every layer is
/// written once, before the first tick, and <see cref="Applied"/> is what the run report carries —
/// so a consumer reading a capture can tell what was in frame from the record rather than from the
/// imagery.</para>
///
/// <para><b>Rendering only; collision is independent.</b> The server drives visibility and collision
/// through two separate calls, so hiding the road mesh leaves its collision exactly as it was and
/// hiding the signals leaves their stop-line triggers live. Nothing here removes a driving surface.
/// It would not matter to this mode if it did — a SUMO-driven body is teleported with its physics
/// off and rests on nothing — but the next reader of this code will be someone wondering whether
/// hiding the road drops the vehicles through it, and the answer is no, twice over.</para>
///
/// <para><b>What "given back" can mean here.</b> The world offers a setter for layer visibility and
/// no getter, so the state a layer was in before the session touched it is not readable and cannot
/// be restored. This puts every layer it wrote back to <i>drawn</i>, which is how a generated world
/// renders before anything hides anything. That is a declared state rather than the state it found,
/// and the asymmetry is deliberate: a run that hides the road mesh and then throws must not leave an
/// operator's editor showing a world with no road network in it.</para>
/// </remarks>
public sealed class LayerVisibilityLease : IDisposable
{
    /// <summary>The generated road surface drawn over the photogrammetry.</summary>
    public const string RoadLayer = "road";

    /// <summary>The generated traffic-light and sign actors.</summary>
    public const string SignalLayer = "signals";

    /// <summary>What a layer is put back to, the world having no way to say what it was.</summary>
    private const bool RestoredVisibility = true;

    private readonly ICarlaWorld _world;
    private readonly Dictionary<string, bool> _applied;
    private bool _released;

    private LayerVisibilityLease(ICarlaWorld world, Dictionary<string, bool> applied)
    {
        _world = world;
        _applied = applied;
    }

    /// <summary>Which layers the session wrote, and what it wrote them to.</summary>
    public IReadOnlyDictionary<string, bool> Applied => _applied;

    /// <summary>Whether the layers have been given back.</summary>
    public bool IsReleased => _released;

    /// <summary>
    /// Write every layer's visibility for the run.
    /// </summary>
    /// <param name="world">The world whose layers to write.</param>
    /// <param name="layers">Each layer and whether it is drawn for the session.</param>
    /// <remarks>
    /// The write is not checked, because there is nothing to check it against: the server answers
    /// this call with a bare success and offers no reader, so a caller cannot establish from the
    /// answer that the frame changed. What a layer did to the imagery is established by looking at a
    /// written frame, which is the only place it can be.
    /// </remarks>
    public static LayerVisibilityLease Take(ICarlaWorld world,
                                            IReadOnlyDictionary<string, bool> layers)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(layers);

        Dictionary<string, bool> applied = [];
        var lease = new LayerVisibilityLease(world, applied);
        try
        {
            foreach ((string layer, bool visible) in layers)
            {
                world.WriteLayerVisible(layer, visible);
                applied[layer] = visible;
            }
        }
        catch
        {
            // Whatever was written before the failure is given back, so a session that never
            // started cannot leave a layer hidden with nothing holding it.
            lease.Dispose();
            throw;
        }

        return lease;
    }

    /// <summary>
    /// Draw every layer this lease wrote. Doing it twice does nothing the second time.
    /// </summary>
    public void Dispose()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        foreach (string layer in _applied.Keys)
        {
            _world.WriteLayerVisible(layer, RestoredVisibility);
        }
    }
}
