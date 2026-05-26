// Shared usings for CarlaNet.Map. Imported into every .cs file via SDK ImplicitUsings.
global using System.Collections.Generic;

global using CarlaNet.Types.Geom;

// OpenDRIVE typedefs (RoadTypes.h). Strong typing was considered but rejected:
// these IDs cross many boundaries (parser → builder → InMemoryMap → stages) and the
// repeated wrapping/unwrapping is more noise than the type safety is worth.
global using RoadId = uint;
global using JuncId = int;
global using LaneId = int;
global using SectionId = uint;
global using ObjId = uint;
global using ConId = uint;
global using SignId = string;
global using ContId = string;
