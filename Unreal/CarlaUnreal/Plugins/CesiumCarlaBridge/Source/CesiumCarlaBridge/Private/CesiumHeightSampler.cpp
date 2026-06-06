// Copyright (c) 2026 CARLA-Cesium digital-twin project.

#include "CesiumHeightSampler.h"

#include "Cesium3DTileset.h"
#include "Engine/Engine.h"
#include "Engine/World.h"
#include "EngineUtils.h" // TActorIterator

// Process-global sample state. One sample at a time, which is all the pipeline
// (and the de-risk probe) needs. The Cesium callback fires on the game thread,
// so no locking is required.
namespace
{
	ECesiumSampleState GState = ECesiumSampleState::Idle;
	TArray<FCesiumSampleHeightResult> GResults;
	TArray<FString> GWarnings;
	FString GStatus;

	void HandleHeightsSampled(
		ACesium3DTileset* /*Tileset*/,
		const TArray<FCesiumSampleHeightResult>& InResults,
		const TArray<FString>& InWarnings)
	{
		GResults = InResults;
		GWarnings = InWarnings;
		GState = ECesiumSampleState::Done;

		int32 Ok = 0;
		for (const FCesiumSampleHeightResult& R : InResults)
		{
			if (R.SampleSuccess)
			{
				++Ok;
			}
		}
		GStatus = FString::Printf(
			TEXT("Sampled %d point(s): %d succeeded, %d warning(s)."),
			InResults.Num(), Ok, InWarnings.Num());
		UE_LOG(LogTemp, Display, TEXT("[CesiumCarlaBridge] %s"), *GStatus);

		for (int32 i = 0; i < InResults.Num(); ++i)
		{
			const FCesiumSampleHeightResult& R = InResults[i];
			UE_LOG(LogTemp, Display,
				TEXT("[CesiumCarlaBridge]   [%d] lon=%.7f lat=%.7f h=%.3f m ok=%d"),
				i, R.LongitudeLatitudeHeight.X, R.LongitudeLatitudeHeight.Y,
				R.LongitudeLatitudeHeight.Z, R.SampleSuccess ? 1 : 0);
		}
		for (const FString& W : InWarnings)
		{
			UE_LOG(LogTemp, Warning, TEXT("[CesiumCarlaBridge]   warning: %s"), *W);
		}
	}
}

bool UCesiumHeightSampler::RequestSample(
	UObject* WorldContextObject,
	const TArray<FVector>& LonLatHeight,
	const FString& TilesetActorName)
{
	GResults.Reset();
	GWarnings.Reset();
	GStatus.Reset();
	GState = ECesiumSampleState::Failed; // pessimistic until we succeed in kicking off

	UWorld* World = GEngine
		? GEngine->GetWorldFromContextObject(WorldContextObject, EGetWorldErrorMode::ReturnNull)
		: nullptr;
	if (!World)
	{
		GStatus = TEXT("RequestSample: could not resolve a UWorld from the context object.");
		UE_LOG(LogTemp, Warning, TEXT("[CesiumCarlaBridge] %s"), *GStatus);
		return false;
	}

	if (LonLatHeight.Num() == 0)
	{
		GStatus = TEXT("RequestSample: empty LonLatHeight array.");
		UE_LOG(LogTemp, Warning, TEXT("[CesiumCarlaBridge] %s"), *GStatus);
		return false;
	}

	ACesium3DTileset* Tileset = nullptr;
	for (TActorIterator<ACesium3DTileset> It(World); It; ++It)
	{
		ACesium3DTileset* Candidate = *It;
		if (!IsValid(Candidate))
		{
			continue;
		}
		if (!TilesetActorName.IsEmpty() && !Candidate->GetName().Contains(TilesetActorName))
		{
			continue;
		}
		Tileset = Candidate;
		break;
	}

	if (!Tileset)
	{
		GStatus = TilesetActorName.IsEmpty()
			? TEXT("RequestSample: no ACesium3DTileset found in the world.")
			: FString::Printf(TEXT("RequestSample: no ACesium3DTileset whose name contains '%s'."), *TilesetActorName);
		UE_LOG(LogTemp, Warning, TEXT("[CesiumCarlaBridge] %s"), *GStatus);
		return false;
	}

	GState = ECesiumSampleState::InProgress;
	GStatus = FString::Printf(
		TEXT("Sampling %d point(s) against tileset '%s'..."),
		LonLatHeight.Num(), *Tileset->GetName());
	UE_LOG(LogTemp, Display, TEXT("[CesiumCarlaBridge] %s"), *GStatus);

	FCesiumSampleHeightMostDetailedCallback Callback;
	Callback.BindStatic(&HandleHeightsSampled);
	Tileset->SampleHeightMostDetailed(LonLatHeight, Callback);
	return true;
}

ECesiumSampleState UCesiumHeightSampler::GetState()
{
	return GState;
}

bool UCesiumHeightSampler::IsReady()
{
	return GState == ECesiumSampleState::Done || GState == ECesiumSampleState::Failed;
}

TArray<FCesiumSampleHeightResult> UCesiumHeightSampler::GetResults()
{
	return GResults;
}

TArray<FString> UCesiumHeightSampler::GetWarnings()
{
	return GWarnings;
}

FString UCesiumHeightSampler::GetStatusMessage()
{
	return GStatus;
}

int32 UCesiumHeightSampler::GetSuccessCount()
{
	int32 Ok = 0;
	for (const FCesiumSampleHeightResult& R : GResults)
	{
		if (R.SampleSuccess)
		{
			++Ok;
		}
	}
	return Ok;
}
