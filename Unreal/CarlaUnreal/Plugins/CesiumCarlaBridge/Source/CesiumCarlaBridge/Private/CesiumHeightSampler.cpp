// Copyright (c) 2026 CARLA-Cesium digital-twin project.

#include "CesiumHeightSampler.h"

#include "Cesium3DTileset.h"
#include "CesiumGeoreference.h"
#include "OriginPlacement.h"
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

bool UCesiumHeightSampler::ConfigureCesiumForOrigin(
	UObject* WorldContextObject,
	double OriginLatitude,
	double OriginLongitude,
	double OriginHeight,
	const FString& IonAccessToken,
	int64 IonAssetId,
	bool bRefreshTileset)
{
	UWorld* World = GEngine
		? GEngine->GetWorldFromContextObject(WorldContextObject, EGetWorldErrorMode::ReturnNull)
		: nullptr;
	if (!World)
	{
		UE_LOG(LogTemp, Warning, TEXT("[CesiumCarlaBridge] ConfigureCesiumForOrigin: no world."));
		return false;
	}

	// Find or CREATE the default georeference. This is what makes the digital-twin
	// pipeline fully procedural/headless: no pre-placed Cesium actors required — the
	// OpenDriveMap reloads on every generate_opendrive_world, so we (re)establish the
	// globe at runtime each time the client configures an origin.
	ACesiumGeoreference* Georeference = ACesiumGeoreference::GetDefaultGeoreference(World);
	if (!Georeference)
	{
		UE_LOG(LogTemp, Warning, TEXT("[CesiumCarlaBridge] ConfigureCesiumForOrigin: could not get/create a CesiumGeoreference."));
		return false;
	}

	Georeference->SetOriginPlacement(EOriginPlacement::CartographicOrigin);
	// SetOriginLongitudeLatitudeHeight takes (Longitude X, Latitude Y, Height Z).
	Georeference->SetOriginLongitudeLatitudeHeight(
		FVector(OriginLongitude, OriginLatitude, OriginHeight));

	TArray<ACesium3DTileset*> Tilesets;
	for (TActorIterator<ACesium3DTileset> It(World); It; ++It)
	{
		if (IsValid(*It)) Tilesets.Add(*It);
	}

	// Spawn a tileset if the world has none and we were given an Ion asset to point at.
	bool bSpawnedTileset = false;
	if (Tilesets.Num() == 0 && IonAssetId > 0)
	{
		FActorSpawnParameters SpawnParams;
		SpawnParams.SpawnCollisionHandlingOverride = ESpawnActorCollisionHandlingMethod::AlwaysSpawn;
		ACesium3DTileset* NewTileset = World->SpawnActor<ACesium3DTileset>(SpawnParams);
		if (NewTileset)
		{
			Tilesets.Add(NewTileset);
			bSpawnedTileset = true;
		}
		else
		{
			UE_LOG(LogTemp, Warning, TEXT("[CesiumCarlaBridge] ConfigureCesiumForOrigin: failed to spawn ACesium3DTileset."));
		}
	}

	for (ACesium3DTileset* Tileset : Tilesets)
	{
		Tileset->SetGeoreference(TSoftObjectPtr<ACesiumGeoreference>(Georeference));
		if (!IonAccessToken.IsEmpty())
		{
			Tileset->SetIonAccessToken(IonAccessToken);
		}
		if (IonAssetId > 0)
		{
			Tileset->SetTilesetSource(ETilesetSource::FromCesiumIon);
			Tileset->SetIonAssetID(IonAssetId);
		}
		if (bRefreshTileset)
		{
			Tileset->RefreshTileset();
		}
	}

	UE_LOG(LogTemp, Display,
		TEXT("[CesiumCarlaBridge] Configured georeference (lat=%.7f lon=%.7f h=%.3f) + %d tileset(s)%s."),
		OriginLatitude, OriginLongitude, OriginHeight, Tilesets.Num(),
		bSpawnedTileset ? TEXT(" (spawned 1)") : TEXT(""));
	return true;
}
