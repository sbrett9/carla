// Copyright (c) 2026 CARLA-Cesium digital-twin project.
//
// Bare-earth reference: everything the telemetry path needs to turn a vehicle's PHYSICAL height
// (the height it is actually driving at, on a surface that may have been shifted to seat on the
// photoreal imagery) into BARE-EARTH truth.
//
//     bare-earth HAE = physical HAE - offset
//
// The offset takes one of two forms, matching the height-align mode the world was generated with:
//
//   * a single constant, when the whole road surface was shifted by one amount ("area"/"origin"
//     modes), or zero when it was left on bare earth ("none");
//   * a per-cell field over the OSM sandbox, when the road was conformed point-by-point to the
//     photoreal surface ("drape" mode), because the shift then varies with position.
//
// This record lives on the server, on its own actor, so that ANY client can recover truth for a
// world it did not itself generate — a client that reconnects to a running server, or one that
// opens a world persisted as a level. Previously it existed only as in-memory state on the client
// process that ran the build, and a second client silently reported the shifted (photoreal-
// referenced) height as bare-earth truth.
//
// Grids are row-major [row * NumCols + col], metres, with grid corner cell (0,0) at world
// (MinXMeters, MinYMeters); +col is +X, +row is +Y, spacing CellSizeMeters. This is the same
// convention as the draped collision heightfield in DrapedTerrain.h, and the same grid.

#pragma once

#include "CoreMinimal.h"
#include "GameFramework/Actor.h"
#include "Kismet/BlueprintFunctionLibrary.h"
#include "BareEarthReference.generated.h"

/**
 * Holder for the bare-earth offset reference. No geometry, no collision, no tick;
 * tagged "bare_earth_reference".
 */
UCLASS()
class CESIUMCARLABRIDGE_API ABareEarthReferenceActor : public AActor
{
	GENERATED_BODY()

public:
	ABareEarthReferenceActor();

	/** Constant surface shift in metres, used when bDrapeActive is false. Zero for bare-earth roads. */
	UPROPERTY() double HeightAlignOffsetMeters = 0.0;

	/** True when the per-cell field below is authoritative and HeightAlignOffsetMeters does not apply. */
	UPROPERTY() bool bDrapeActive = false;

	UPROPERTY() double MinXMeters = 0.0;
	UPROPERTY() double MinYMeters = 0.0;
	UPROPERTY() double CellSizeMeters = 0.0;
	UPROPERTY() int32 NumCols = 0;
	UPROPERTY() int32 NumRows = 0;

	/** Per-cell surface shift (draped surface height minus bare-earth height), metres. */
	UPROPERTY() TArray<float> OffsetMeters;

	/** Per-cell bare-earth ground height, ellipsoidal metres. Reported as the ground under a vehicle. */
	UPROPERTY() TArray<float> BareEarthDtmMeters;
};

UCLASS()
class CESIUMCARLABRIDGE_API UBareEarthReference : public UBlueprintFunctionLibrary
{
	GENERATED_BODY()

public:
	/**
	 * Record (or replace) the bare-earth reference for WorldContextObject's world. Any existing
	 * record is destroyed first, so one world holds at most one. When bDrapeActive is false the grid
	 * arguments are ignored and may be empty. Returns the actor, or nullptr when there is no world,
	 * or when drape is active but the grid is degenerate or the wrong length.
	 */
	UFUNCTION(BlueprintCallable, Category = "CesiumCarla", meta = (WorldContext = "WorldContextObject"))
	static ABareEarthReferenceActor* Set(
		UObject* WorldContextObject,
		double HeightAlignOffsetMeters,
		bool bDrapeActive,
		double MinXMeters, double MinYMeters, double CellSizeMeters,
		int32 NumCols, int32 NumRows,
		const TArray<float>& OffsetMeters,
		const TArray<float>& BareEarthDtmMeters);

	/**
	 * Read the scalar part of the record. Returns false (outputs untouched) when this world has
	 * none, which is the case for any map loaded rather than generated from an OSM area.
	 */
	UFUNCTION(BlueprintCallable, Category = "CesiumCarla", meta = (WorldContext = "WorldContextObject"))
	static bool Get(
		UObject* WorldContextObject,
		double& OutHeightAlignOffsetMeters,
		bool& bOutDrapeActive,
		double& OutMinXMeters, double& OutMinYMeters, double& OutCellSizeMeters,
		int32& OutNumCols, int32& OutNumRows);

	/** Per-cell surface shift, empty when this world has no record or was not draped. */
	UFUNCTION(BlueprintCallable, Category = "CesiumCarla", meta = (WorldContext = "WorldContextObject"))
	static const TArray<float>& GetOffsetGrid(UObject* WorldContextObject);

	/** Per-cell bare-earth ground height, empty when this world has no record or was not draped. */
	UFUNCTION(BlueprintCallable, Category = "CesiumCarla", meta = (WorldContext = "WorldContextObject"))
	static const TArray<float>& GetBareEarthDtmGrid(UObject* WorldContextObject);

private:
	/** The single record for a world, or nullptr when it has none. */
	static ABareEarthReferenceActor* Find(UObject* WorldContextObject);
};
