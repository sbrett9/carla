// Copyright (c) 2026 CARLA-Cesium digital-twin project.
//
// Per-point draped collision terrain as a Chaos heightfield ("drape" height-align mode).
//
// UDrapedTerrainComponent is a hidden, collision-only UPrimitiveComponent backed by a
// Chaos::FHeightField (a regular grid storing only Z per cell — the standard terrain-collision
// representation; ~bytes/cell, O(1) query). It is the universal physics/seating surface the
// digital-twin drapes onto the de-spiked photoreal over the OSM bounds, so vehicles seat on the
// photoreal on- AND off-road. The visual is still the Cesium photoreal; this mesh never renders.
// Telemetry truth comes from a separate per-cell offset field (client-side).
//
// The runtime physics-body creation mirrors ULandscapeHeightfieldCollisionComponent (the only
// FHeightField collision site in the engine).

#pragma once

#include "CoreMinimal.h"
#include "GameFramework/Actor.h"
#include "Components/PrimitiveComponent.h"
#include "Kismet/BlueprintFunctionLibrary.h"
#include "PhysicsEngine/BodyInstance.h"
#include "DrapedTerrain.generated.h"

namespace Chaos { class FHeightField; }

UCLASS()
class CESIUMCARLABRIDGE_API UDrapedTerrainComponent : public UPrimitiveComponent
{
	GENERATED_BODY()

public:
	UDrapedTerrainComponent();

	/**
	 * Set the heightfield grid BEFORE the physics state is created. Heights are world Z in
	 * CENTIMETRES (Unreal units), row-major [row * NumCols + col]; the grid corner cell (0,0) is
	 * placed at world (OriginXCm, OriginYCm). Column step is +X, row step is +Y, both CellSizeCm.
	 */
	void SetGrid(double InOriginXCm, double InOriginYCm, double InCellSizeCm,
		int32 InNumCols, int32 InNumRows, TArray<double>&& InHeightsCm);

	virtual bool ShouldCreatePhysicsState() const override { return true; }
	virtual void OnCreatePhysicsState() override;
	virtual void OnDestroyPhysicsState() override;
	virtual FBoxSphereBounds CalcBounds(const FTransform& LocalToWorld) const override;
	virtual void PostLoad() override;

private:
	/** Recompute the world-space bounds from the current grid. Cheap; derived, never serialized. */
	void RecomputeLocalBounds();

	// The grid is UPROPERTY so it survives a level save. Without that, a saved level reloads an actor
	// with the right tag, the right collision profile and NO heightfield -- OnCreatePhysicsState sees
	// NumCols == 0 and returns early, leaving a world whose ground silently does not exist.
	UPROPERTY() double OriginXCm = 0.0;
	UPROPERTY() double OriginYCm = 0.0;
	UPROPERTY() double CellSizeCm = 200.0;
	UPROPERTY() int32 NumCols = 0;
	UPROPERTY() int32 NumRows = 0;
	UPROPERTY() TArray<double> HeightsCm;      // row-major, world Z (cm)

	// Derived from the grid, so it is recomputed on load rather than stored: a serialized copy could
	// disagree with the heights it is supposed to bound.
	FBox LocalBox = FBox(ForceInit);
};

UCLASS()
class CESIUMCARLABRIDGE_API ADrapedTerrainActor : public AActor
{
	GENERATED_BODY()

public:
	ADrapedTerrainActor();

	UPROPERTY()
	TObjectPtr<UDrapedTerrainComponent> Terrain;
};

UCLASS()
class CESIUMCARLABRIDGE_API UDrapedTerrain : public UBlueprintFunctionLibrary
{
	GENERATED_BODY()

public:
	/**
	 * Build (or replace) the draped-terrain heightfield in WorldContextObject's world. Heights are
	 * world Z in METRES, row-major [row * NumCols + col], length NumCols*NumRows. The grid corner
	 * (col 0, row 0) is at world (OriginXMeters, OriginYMeters); +col is +X, +row is +Y, spacing
	 * CellSizeMeters. Any existing draped-terrain actor is destroyed first. Returns the actor (or
	 * nullptr on failure). Tagged "draped_terrain"; hidden; collision-only (WorldStatic, blocks all).
	 *
	 * The sandbox extent used by boundary-aware traffic is recorded separately (see StagingBounds.h),
	 * because it is defined for every height-align mode and this heightfield is not.
	 */
	UFUNCTION(BlueprintCallable, Category = "CesiumCarla", meta = (WorldContext = "WorldContextObject"))
	static ADrapedTerrainActor* Build(
		UObject* WorldContextObject,
		double OriginXMeters,
		double OriginYMeters,
		double CellSizeMeters,
		int32 NumCols,
		int32 NumRows,
		const TArray<double>& HeightsMeters);
};
