using CarlaNet.Types.Geom;

using ActorId = uint;

namespace CarlaNet.CoSim;

/// <summary>
/// One registered camera as a render-set pass sees it: where it stood on the last rendered frame, and
/// the picture it takes.
/// </summary>
/// <param name="Actor">The camera's actor id.</param>
/// <param name="Pose">
/// Its world transform in the CARLA frame, from the client's snapshot of the last frame the session
/// rendered.
/// </param>
/// <param name="Optics">Its image size and field of view, from the attributes it was spawned with.</param>
public readonly record struct CameraView(ActorId Actor, Transform Pose, CameraOptics Optics);
