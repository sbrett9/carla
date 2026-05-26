// Shared usings for CarlaNet.TrafficManager. Imported into every .cs file via SDK ImplicitUsings.
//
// The C++ TrafficManager source uses `namespace cc = carla::client;` and
// `namespace cg = carla::geom;` shortcut aliases everywhere; in C# we
// achieve the same effect by globally importing Types.Geom / Rpc.Enums.
global using System;
global using System.Collections.Generic;
global using System.Collections.Concurrent;
global using System.Threading;

global using CarlaNet.Types.Geom;
global using CarlaNet.Types.Rpc.Actors;
global using CarlaNet.Types.Rpc.Enums;

// ActorId is `uint` matching `carla::ActorId` (carla/rpc/ActorId.h).
global using ActorId = uint;
// JuncId / RoadId / LaneId from CarlaNet.Map (carla/road/RoadTypes.h). The
// values themselves are plain primitive aliases; we duplicate them here to
// avoid taking a hard dependency on CarlaNet.Map's GlobalUsings just for the
// typedefs. SimpleWaypoint/TrackTraffic use them by name.
global using JuncId = int;
global using RoadId = uint;
global using LaneId = int;
global using SectionId = uint;
// GeoGridId is an alias of JuncId in C++ (SimpleWaypoint.h:24).
global using GeoGridId = int;

// TLS is the alias used pervasively in DataStructures.h / SimulationState.h.
global using TLS = CarlaNet.Types.Rpc.Enums.TrafficLightState;
