// Copyright (c) 2026 CARLA-Cesium digital-twin project.
//
// Build rules for the Cesium-CARLA bridge module. Depends on CesiumRuntime so it
// can call ACesium3DTileset::SampleHeightMostDetailed and reference
// FCesiumSampleHeightResult directly from the public header.

using UnrealBuildTool;

public class CesiumCarlaBridge : ModuleRules
{
	public CesiumCarlaBridge(ReadOnlyTargetRules Target) : base(Target)
	{
		PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;

		// This module includes cesium-native headers (via CesiumRuntime), which use C++ exceptions
		// and RTTI. Editor builds enable exceptions by default, but Game/Shipping configs (e.g. the
		// packaged build) default to -fno-exceptions, so without this the package build fails with
		// "cannot use 'throw' with exceptions disabled" in cesium headers (AccessorView.h). Match the
		// sibling CARLA modules (Carla/CarlaTools/CarlaUnreal), which set both, and the RTTI-on
		// cesium-native this links against.
		bEnableExceptions = true;
		bUseRTTI = true;

		PublicDependencyModuleNames.AddRange(new string[]
		{
			"Core",
			"CoreUObject",
			"Engine",
			"CesiumRuntime"
		});

		// Runtime Chaos heightfield collision (draped terrain). Same physics module set Cesium
		// itself uses from a plugin, so this is a proven-accessible dependency set.
		PrivateDependencyModuleNames.AddRange(new string[]
		{
			"Chaos",
			"ChaosCore",
			"PhysicsCore"
		});
	}
}
