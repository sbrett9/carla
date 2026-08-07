// Copyright (c) 2026 CARLA-Cesium digital-twin project.
//
// Staging bounds: the digital-twin sandbox extent in CARLA-local metres, plus the inward
// staging-ring margin reserved at the OSM edge for traffic entry and exit. Boundary-aware traffic
// spawns inside that ring, drives into the scene and despawns on leaving it; the scene perimeter
// (region of interest) is these bounds inset by the margin.
//
// The record lives on its own actor rather than on the draped-terrain actor because the sandbox
// extent is a property of the OSM area, not of the collision surface. A draped heightfield is built
// only when the road is conformed per-point to the photoreal; the extent is equally well defined
// when the road is seated by a single constant offset, or left on bare earth.

#pragma once

#include "CoreMinimal.h"
#include "GameFramework/Actor.h"
#include "Kismet/BlueprintFunctionLibrary.h"
#include "StagingBounds.generated.h"

/** Holder for the sandbox extent + staging-ring margin. No geometry, no tick; tagged "staging_bounds". */
UCLASS()
class CESIUMCARLABRIDGE_API AStagingBoundsActor : public AActor
{
	GENERATED_BODY()

public:
	AStagingBoundsActor();

	UPROPERTY() double MinXMeters = 0.0;
	UPROPERTY() double MinYMeters = 0.0;
	UPROPERTY() double MaxXMeters = 0.0;
	UPROPERTY() double MaxYMeters = 0.0;
	UPROPERTY() double MarginMeters = 0.0;
};

UCLASS()
class CESIUMCARLABRIDGE_API UStagingBounds : public UBlueprintFunctionLibrary
{
	GENERATED_BODY()

public:
	/**
	 * Record (or replace) the staging bounds for WorldContextObject's world. All values are
	 * CARLA-local metres; MarginMeters is the width of the inward ring at the sandbox edge. Any
	 * existing record is destroyed first. Returns the actor, or nullptr when there is no world or
	 * the rectangle is degenerate.
	 */
	UFUNCTION(BlueprintCallable, Category = "CesiumCarla", meta = (WorldContext = "WorldContextObject"))
	static AStagingBoundsActor* Set(
		UObject* WorldContextObject,
		double MinXMeters, double MinYMeters,
		double MaxXMeters, double MaxYMeters,
		double MarginMeters);

	/**
	 * Read the recorded staging bounds. Returns false (outputs untouched) when this world has none,
	 * which is the case for any map that was loaded rather than generated from an OSM area.
	 */
	UFUNCTION(BlueprintCallable, Category = "CesiumCarla", meta = (WorldContext = "WorldContextObject"))
	static bool Get(
		UObject* WorldContextObject,
		double& OutMinXMeters, double& OutMinYMeters,
		double& OutMaxXMeters, double& OutMaxYMeters,
		double& OutMarginMeters);
};
