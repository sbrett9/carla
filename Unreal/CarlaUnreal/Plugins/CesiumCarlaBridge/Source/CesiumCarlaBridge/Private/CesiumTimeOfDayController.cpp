// Copyright (c) 2026 CARLA-Cesium digital-twin project.

#include "CesiumTimeOfDayController.h"

#include "CesiumSunSky.h"
#include "EngineUtils.h" // TActorIterator
#include "Misc/DateTime.h" // FDateTime (CoreMinimal.h does not pull this in)
#include "Misc/Timespan.h" // FTimespan

namespace
{
	// Move the sun's calendar date by whole days. ACesiumSunSky holds Year/Month/Day as plain
	// integers with no calendar arithmetic of their own, so month lengths and leap years are left
	// to FDateTime.
	void RollSolarDate(ACesiumSunSky* SunSky, int32 DayDelta)
	{
		if (DayDelta == 0)
		{
			return;
		}
		if (!FDateTime::Validate(SunSky->Year, SunSky->Month, SunSky->Day, 0, 0, 0, 0))
		{
			UE_LOG(LogTemp, Warning,
				TEXT("[CesiumCarlaBridge] the solar clock passed midnight but %04d-%02d-%02d is not "
					 "a calendar date, so the date cannot be advanced."),
				SunSky->Year, SunSky->Month, SunSky->Day);
			return;
		}
		const int64 RolledTicks =
			FDateTime(SunSky->Year, SunSky->Month, SunSky->Day).GetTicks()
			+ FTimespan::FromDays(static_cast<double>(DayDelta)).GetTicks();
		if (RolledTicks < 0 || RolledTicks > FDateTime::MaxValue().GetTicks())
		{
			UE_LOG(LogTemp, Warning,
				TEXT("[CesiumCarlaBridge] advancing the solar date by %d day(s) from %04d-%02d-%02d "
					 "leaves the representable calendar; the date is left unchanged."),
				DayDelta, SunSky->Year, SunSky->Month, SunSky->Day);
			return;
		}
		const FDateTime Rolled(RolledTicks);
		SunSky->Year = Rolled.GetYear();
		SunSky->Month = Rolled.GetMonth();
		SunSky->Day = Rolled.GetDay();
	}
}

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
		// Whole days come off the clock and go onto the calendar. Wrapping the clock modulo 24 on
		// its own would return a run that crosses local midnight to 00:00 of the SAME day: the scene
		// would render the next morning under the previous day's solar declination, and the recorded
		// solar state would go on asserting the original date. Over a multi-day run the date would
		// never move at all, so day-of-week could never mean anything.
		const double DeltaHours = static_cast<double>(DeltaSeconds) * Rate / 3600.0;
		const double Advanced = SunSky->SolarTime + DeltaHours;
		// Floor division, so a negative rate rolls the date backwards rather than forwards.
		const double WholeDays = FMath::FloorToDouble(Advanced / 24.0);
		SunSky->SolarTime = Advanced - WholeDays * 24.0;
		// One tick moves the clock by DeltaSeconds * Rate, so more days than the calendar can hold
		// means a nonsensical rate rather than elapsed time. Bound it before the date arithmetic;
		// RollSolarDate reports anything that still leaves the representable range.
		RollSolarDate(SunSky, static_cast<int32>(FMath::Clamp(WholeDays, -4000000.0, 4000000.0)));
		SunSky->UpdateSun();
		break;
	}
}
