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

		PublicDependencyModuleNames.AddRange(new string[]
		{
			"Core",
			"CoreUObject",
			"Engine",
			"CesiumRuntime"
		});

		// Phase 2b: runtime Chaos heightfield collision (draped terrain). Same physics module
		// set Cesium itself uses from a plugin, so this is a proven-accessible dependency set.
		PrivateDependencyModuleNames.AddRange(new string[]
		{
			"Chaos",
			"ChaosCore",
			"PhysicsCore"
		});
	}
}
