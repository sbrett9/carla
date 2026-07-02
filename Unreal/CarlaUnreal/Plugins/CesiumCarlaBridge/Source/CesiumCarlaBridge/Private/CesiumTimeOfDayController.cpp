// Copyright (c) 2026 CARLA-Cesium digital-twin project.

#include "CesiumTimeOfDayController.h"

#include "CesiumSunSky.h"
#include "EngineUtils.h" // TActorIterator

ACesiumTimeOfDayController::ACesiumTimeOfDayController()
{
	PrimaryActorTick.bCanEverTick = true;
	PrimaryActorTick.bStartWithTickEnabled = true;
}

void ACesiumTimeOfDayController::Tick(float DeltaSeconds)
{
	Super::Tick(DeltaSeconds);
	if (!bAdvancing)
	{
		return;
	}
	UWorld* World = GetWorld();
	if (!World)
	{
		return;
	}
	// Advance the (single) CesiumSunSky's solar clock and refresh the sun.
	for (TActorIterator<ACesiumSunSky> It(World); It; ++It)
	{
		ACesiumSunSky* SunSky = *It;
		if (!IsValid(SunSky))
		{
			continue;
		}
		const double DeltaHours = static_cast<double>(DeltaSeconds) * Rate / 3600.0;
		SunSky->SolarTime = FMath::Fmod(FMath::Fmod(SunSky->SolarTime + DeltaHours, 24.0) + 24.0, 24.0);
		SunSky->UpdateSun();
		break;
	}
}
