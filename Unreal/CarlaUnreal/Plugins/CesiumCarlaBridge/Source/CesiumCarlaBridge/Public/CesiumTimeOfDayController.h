// Copyright (c) 2026 CARLA-Cesium digital-twin project.
//
// ACesiumTimeOfDayController advances a CesiumSunSky's solar clock over time so the sun moves as
// the scene runs. It is spawned and driven by UCesiumHeightSampler::SetTimeAdvance (the
// set_time_advance RPC); nothing else needs to reference it. Because it ticks with the world, it
// advances in wall-clock time under asynchronous mode and in simulation time under synchronous
// ticking (world.tick()).

#pragma once

#include "CoreMinimal.h"
#include "GameFramework/Actor.h"
#include "CesiumTimeOfDayController.generated.h"

UCLASS()
class CESIUMCARLABRIDGE_API ACesiumTimeOfDayController : public AActor
{
	GENERATED_BODY()

public:
	ACesiumTimeOfDayController();

	virtual void Tick(float DeltaSeconds) override;

	/** When true, the first CesiumSunSky's SolarTime advances each tick. */
	UPROPERTY()
	bool bAdvancing = false;

	/** Sun-clock seconds elapsed per real (or sim) second. 1.0 = real time; >1 accelerates. */
	UPROPERTY()
	double Rate = 1.0;
};
