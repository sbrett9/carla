// Copyright (c) 2026 CARLA-Cesium digital-twin project.

#include "StagingBounds.h"

#include "Components/SceneComponent.h"
#include "Engine/Engine.h"
#include "Engine/World.h"
#include "EngineUtils.h"

AStagingBoundsActor::AStagingBoundsActor()
{
	PrimaryActorTick.bCanEverTick = false;
	// A transform-only holder: no geometry, no collision, nothing to draw.
	RootComponent = CreateDefaultSubobject<USceneComponent>(TEXT("Root"));
	SetHidden(true);
}

AStagingBoundsActor* UStagingBounds::Set(
	UObject* WorldContextObject,
	double MinXMeters, double MinYMeters,
	double MaxXMeters, double MaxYMeters,
	double MarginMeters)
{
	UWorld* World = GEngine
		? GEngine->GetWorldFromContextObject(WorldContextObject, EGetWorldErrorMode::ReturnNull)
		: nullptr;
	if (!World)
	{
		UE_LOG(LogTemp, Warning, TEXT("[StagingBounds] Set: no world."));
		return nullptr;
	}
	if (!(MaxXMeters > MinXMeters) || !(MaxYMeters > MinYMeters))
	{
		UE_LOG(LogTemp, Warning,
			TEXT("[StagingBounds] Set: degenerate rectangle (%.2f, %.2f) .. (%.2f, %.2f)."),
			MinXMeters, MinYMeters, MaxXMeters, MaxYMeters);
		return nullptr;
	}

	// Replace any previous record: one sandbox per world.
	for (TActorIterator<AStagingBoundsActor> It(World); It; ++It)
	{
		if (IsValid(*It)) { It->Destroy(); }
	}

	FActorSpawnParameters SpawnParams;
	SpawnParams.SpawnCollisionHandlingOverride = ESpawnActorCollisionHandlingMethod::AlwaysSpawn;
	AStagingBoundsActor* Actor = World->SpawnActor<AStagingBoundsActor>(
		AStagingBoundsActor::StaticClass(), FTransform::Identity, SpawnParams);
	if (!Actor)
	{
		UE_LOG(LogTemp, Warning, TEXT("[StagingBounds] Set: spawn failed."));
		return nullptr;
	}
	Actor->Tags.Add(FName(TEXT("staging_bounds")));
	Actor->MinXMeters = MinXMeters;
	Actor->MinYMeters = MinYMeters;
	Actor->MaxXMeters = MaxXMeters;
	Actor->MaxYMeters = MaxYMeters;
	Actor->MarginMeters = MarginMeters;

	UE_LOG(LogTemp, Display,
		TEXT("[StagingBounds] Set: (%.2f, %.2f) .. (%.2f, %.2f) m, staging margin %.2f m (inward)."),
		MinXMeters, MinYMeters, MaxXMeters, MaxYMeters, MarginMeters);
	return Actor;
}

bool UStagingBounds::Get(
	UObject* WorldContextObject,
	double& OutMinXMeters, double& OutMinYMeters,
	double& OutMaxXMeters, double& OutMaxYMeters,
	double& OutMarginMeters)
{
	UWorld* World = GEngine
		? GEngine->GetWorldFromContextObject(WorldContextObject, EGetWorldErrorMode::ReturnNull)
		: nullptr;
	if (!World) { return false; }
	for (TActorIterator<AStagingBoundsActor> It(World); It; ++It)
	{
		if (!IsValid(*It)) { continue; }
		OutMinXMeters = It->MinXMeters; OutMinYMeters = It->MinYMeters;
		OutMaxXMeters = It->MaxXMeters; OutMaxYMeters = It->MaxYMeters;
		OutMarginMeters = It->MarginMeters;
		return true;
	}
	return false;
}
