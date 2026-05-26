// Global usings for CarlaNet.Nav.
//
// Centralises the DotRecast + CarlaNet types we touch in every file
// so the per-file `using` lists stay short.
global using System;
global using System.Collections.Generic;
global using System.Diagnostics;
global using System.IO;
global using System.Numerics;

global using CarlaNet.Types.Geom;

global using DotRecast.Core;
global using DotRecast.Core.Numerics;
global using DotRecast.Detour;
global using DotRecast.Detour.Crowd;
global using DotRecast.Detour.Io;

global using ActorId = uint;
