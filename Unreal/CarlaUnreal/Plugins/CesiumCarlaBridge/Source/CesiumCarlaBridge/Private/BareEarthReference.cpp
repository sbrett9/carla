// Copyright (c) 2026 CARLA-Cesium digital-twin project.

#include "BareEarthReference.h"

#include "Components/SceneComponent.h"
#include "Engine/Engine.h"
#include "Engine/World.h"
#include "EngineUtils.h"

namespace
{
	/** Returned by the grid accessors when a world carries no record; avoids returning a dangling ref. */
	const TArray<float>& EmptyGrid()
	{
		static const TArray<float> Empty;
		return Empty;
	}
}

ABareEarthReferenceActor::ABareEarthReferenceActor()
{
	PrimaryActorTick.bCanEverTick = false;
	// A data-only holder: no geometry, no collision, nothing to draw.
	RootComponent = CreateDefaultSubobject<USceneComponent>(TEXT("Root"));
	SetHidden(true);
}

ABareEarthReferenceActor* UBareEarthReference::Find(UObject* WorldContextObject)
{
	UWorld* World = GEngine
		? GEngine->GetWorldFromContextObject(WorldContextObject, EGetWorldErrorMode::ReturnNull)
		: nullptr;
	if (!World) { return nullptr; }
	for (TActorIterator<ABareEarthReferenceActor> It(World); It; ++It)
	{
		if (IsValid(*It)) { return *It; }
	}
	return nullptr;
}

ABareEarthReferenceActor* UBareEarthReference::Set(
	UObject* WorldContextObject,
	double HeightAlignOffsetMeters,
	bool bDrapeActive,
	double MinXMeters, double MinYMeters, double CellSizeMeters,
	int32 NumCols, int32 NumRows,
	const TArray<float>& OffsetMeters,
	const TArray<float>& BareEarthDtmMeters)
{
	UWorld* World = GEngine
		? GEngine->GetWorldFromContextObject(WorldContextObject, EGetWorldErrorMode::ReturnNull)
		: nullptr;
	if (!World)
	{
		UE_LOG(LogTemp, Warning, TEXT("[BareEarthReference] Set: no world."));
		return nullptr;
	}

	// A draped world is only useful with a well-formed grid: telemetry samples it per vehicle, and a
	// short or degenerate grid would read out of bounds or interpolate nonsense. Reject it here
	// rather than store a record that reports wrong truth.
	if (bDrapeActive)
	{
		const int64 Expected = static_cast<int64>(NumCols) * static_cast<int64>(NumRows);
		if (NumCols < 2 || NumRows < 2 || !(CellSizeMeters > 0.0))
		{
			UE_LOG(LogTemp, Warning,
				TEXT("[BareEarthReference] Set: degenerate grid %dx%d, cell %.3f m."),
				NumCols, NumRows, CellSizeMeters);
			return nullptr;
		}
		if (OffsetMeters.Num() != Expected || BareEarthDtmMeters.Num() != Expected)
		{
			UE_LOG(LogTemp, Warning,
				TEXT("[BareEarthReference] Set: grid length mismatch (offset %d, ground %d, expected %lld)."),
				OffsetMeters.Num(), BareEarthDtmMeters.Num(), Expected);
			return nullptr;
		}
	}

	// Replace any previous record: one bare-earth reference per world.
	for (TActorIterator<ABareEarthReferenceActor> It(World); It; ++It)
	{
		if (IsValid(*It)) { It->Destroy(); }
	}

	FActorSpawnParameters SpawnParams;
	SpawnParams.SpawnCollisionHandlingOverride = ESpawnActorCollisionHandlingMethod::AlwaysSpawn;
	ABareEarthReferenceActor* Actor = World->SpawnActor<ABareEarthReferenceActor>(
		ABareEarthReferenceActor::StaticClass(), FTransform::Identity, SpawnParams);
	if (!Actor)
	{
		UE_LOG(LogTemp, Warning, TEXT("[BareEarthReference] Set: spawn failed."));
		return nullptr;
	}
	Actor->Tags.Add(FName(TEXT("bare_earth_reference")));
	Actor->HeightAlignOffsetMeters = HeightAlignOffsetMeters;
	Actor->bDrapeActive = bDrapeActive;
	Actor->MinXMeters = MinXMeters;
	Actor->MinYMeters = MinYMeters;
	Actor->CellSizeMeters = CellSizeMeters;
	Actor->NumCols = NumCols;
	Actor->NumRows = NumRows;
	if (bDrapeActive)
	{
		Actor->OffsetMeters = OffsetMeters;
		Actor->BareEarthDtmMeters = BareEarthDtmMeters;
	}
	else
	{
		Actor->OffsetMeters.Empty();
		Actor->BareEarthDtmMeters.Empty();
	}

	if (bDrapeActive)
	{
		UE_LOG(LogTemp, Display,
			TEXT("[BareEarthReference] Set: per-cell field %dx%d, cell %.2f m, corner (%.2f, %.2f) m."),
			NumCols, NumRows, CellSizeMeters, MinXMeters, MinYMeters);
	}
	else
	{
		UE_LOG(LogTemp, Display,
			TEXT("[BareEarthReference] Set: constant surface shift %.3f m."), HeightAlignOffsetMeters);
	}
	return Actor;
}

bool UBareEarthReference::Get(
	UObject* WorldContextObject,
	double& OutHeightAlignOffsetMeters,
	bool& bOutDrapeActive,
	double& OutMinXMeters, double& OutMinYMeters, double& OutCellSizeMeters,
	int32& OutNumCols, int32& OutNumRows)
{
	const ABareEarthReferenceActor* Actor = Find(WorldContextObject);
	if (!Actor) { return false; }
	OutHeightAlignOffsetMeters = Actor->HeightAlignOffsetMeters;
	bOutDrapeActive = Actor->bDrapeActive;
	OutMinXMeters = Actor->MinXMeters;
	OutMinYMeters = Actor->MinYMeters;
	OutCellSizeMeters = Actor->CellSizeMeters;
	OutNumCols = Actor->NumCols;
	OutNumRows = Actor->NumRows;
	return true;
}

const TArray<float>& UBareEarthReference::GetOffsetGrid(UObject* WorldContextObject)
{
	const ABareEarthReferenceActor* Actor = Find(WorldContextObject);
	return Actor ? Actor->OffsetMeters : EmptyGrid();
}

const TArray<float>& UBareEarthReference::GetBareEarthDtmGrid(UObject* WorldContextObject)
{
	const ABareEarthReferenceActor* Actor = Find(WorldContextObject);
	return Actor ? Actor->BareEarthDtmMeters : EmptyGrid();
}
