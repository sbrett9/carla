// Copyright (c) 2026 CARLA-Cesium digital-twin project.

#include "CesiumHeightSampler.h"

#include "Cesium3DTileset.h"
#include "CesiumGeoreference.h"
#include "CesiumSunSky.h"
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

	// Find-by-tag-or-spawn a tileset for a named layer (08_Layer_Architecture), then
	// (re)configure it: georeference, ion token/asset, hidden, collision, tag, refresh.
	// The "photoreal" layer additionally ADOPTS a pre-placed untagged tileset (preserving
	// the old "use the first existing tileset" behaviour); "ground" is always its own.
	ACesium3DTileset* EnsureTileset(
		UWorld* World, ACesiumGeoreference* Georef, const FString& Tag,
		int64 AssetId, const FString& Token, bool bHidden, bool bCollision, bool bRefresh)
	{
		const FName TagName(*Tag);
		ACesium3DTileset* Found = nullptr;
		ACesium3DTileset* Untagged = nullptr;
		for (TActorIterator<ACesium3DTileset> It(World); It; ++It)
		{
			ACesium3DTileset* T = *It;
			if (!IsValid(T)) continue;
			if (T->ActorHasTag(TagName)) { Found = T; break; }
			if (!Untagged && T->Tags.Num() == 0) Untagged = T;
		}

		ACesium3DTileset* Tileset = Found;
		if (!Tileset && Tag == TEXT("photoreal")) Tileset = Untagged; // adopt a pre-placed tileset
		if (!Tileset)
		{
			FActorSpawnParameters SpawnParams;
			SpawnParams.SpawnCollisionHandlingOverride = ESpawnActorCollisionHandlingMethod::AlwaysSpawn;
			Tileset = World->SpawnActor<ACesium3DTileset>(SpawnParams);
			if (!Tileset)
			{
				UE_LOG(LogTemp, Warning, TEXT("[CesiumCarlaBridge] EnsureTileset('%s'): spawn failed."), *Tag);
				return nullptr;
			}
		}

		if (!Tileset->ActorHasTag(TagName)) Tileset->Tags.Add(TagName);
		Tileset->SetGeoreference(TSoftObjectPtr<ACesiumGeoreference>(Georef));
		if (!Token.IsEmpty()) Tileset->SetIonAccessToken(Token);
		if (AssetId > 0)
		{
			Tileset->SetTilesetSource(ETilesetSource::FromCesiumIon);
			Tileset->SetIonAssetID(AssetId);
		}
		Tileset->SetActorHiddenInGame(bHidden);
		Tileset->SetCreatePhysicsMeshes(bCollision);
		if (bRefresh) Tileset->RefreshTileset();
		return Tileset;
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
		if (!TilesetActorName.IsEmpty())
		{
			// Match the selector against the actor NAME or its TAGS (so "ground" picks the
			// bare-earth layer tagged by ConfigureCesiumForOrigin).
			const bool bNameMatch = Candidate->GetName().Contains(TilesetActorName);
			const bool bTagMatch  = Candidate->ActorHasTag(FName(*TilesetActorName));
			if (!bNameMatch && !bTagMatch)
			{
				continue;
			}
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
	int64 GroundIonAssetId,
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

	// Ensure the layered tilesets (08_Layer_Architecture): a visual "photoreal" tileset and,
	// when a ground asset is given, a HIDDEN collidable bare-earth "ground" tileset (the
	// height-sample source). EnsureTileset find-by-tag-or-spawns and (re)configures each so
	// this is idempotent across the world reload in generate_opendrive_world.
	int32 NumTilesets = 0;
	if (IonAssetId > 0)
	{
		if (EnsureTileset(World, Georeference, TEXT("photoreal"), IonAssetId, IonAccessToken,
				/*bHidden*/ false, /*bCollision*/ false, bRefreshTileset)) ++NumTilesets;
	}
	if (GroundIonAssetId > 0)
	{
		if (EnsureTileset(World, Georeference, TEXT("ground"), GroundIonAssetId, IonAccessToken,
				/*bHidden*/ true, /*bCollision*/ true, bRefreshTileset)) ++NumTilesets;
	}
	// Align any other pre-existing tilesets to the same georeference.
	for (TActorIterator<ACesium3DTileset> It(World); It; ++It)
	{
		if (IsValid(*It)) (*It)->SetGeoreference(TSoftObjectPtr<ACesiumGeoreference>(Georeference));
	}

	// The generated OpenDriveMap has no weather/sun actor ("Missing weather class"), so the
	// scene is unlit. Spawn a CesiumSunSky for physically-based georeferenced lighting
	// (defaults: SolarTime 13:00, TimeZone -5 = Chicago daytime). Also the correct EO basis
	// later (real solar angle / shadows).
	bool bHasSunSky = false;
	for (TActorIterator<ACesiumSunSky> It(World); It; ++It)
	{
		if (IsValid(*It)) { bHasSunSky = true; break; }
	}
	bool bSpawnedSunSky = false;
	if (!bHasSunSky)
	{
		FActorSpawnParameters SunParams;
		SunParams.SpawnCollisionHandlingOverride = ESpawnActorCollisionHandlingMethod::AlwaysSpawn;
		ACesiumSunSky* SunSky = World->SpawnActor<ACesiumSunSky>(SunParams);
		if (SunSky)
		{
			SunSky->UpdateSun();
			bSpawnedSunSky = true;
		}
		else
		{
			UE_LOG(LogTemp, Warning, TEXT("[CesiumCarlaBridge] failed to spawn ACesiumSunSky."));
		}
	}

	UE_LOG(LogTemp, Display,
		TEXT("[CesiumCarlaBridge] Configured georeference (lat=%.7f lon=%.7f h=%.3f) + %d layer tileset(s) (photoreal asset=%lld, ground asset=%lld)%s."),
		OriginLatitude, OriginLongitude, OriginHeight, NumTilesets,
		static_cast<long long>(IonAssetId), static_cast<long long>(GroundIonAssetId),
		bSpawnedSunSky ? TEXT(" (spawned sun)") : TEXT(""));
	return true;
}

int32 UCesiumHeightSampler::SetLayerVisible(UObject* WorldContextObject, const FString& LayerTag, bool bVisible)
{
	UWorld* World = GEngine
		? GEngine->GetWorldFromContextObject(WorldContextObject, EGetWorldErrorMode::ReturnNull)
		: nullptr;
	if (!World)
	{
		UE_LOG(LogTemp, Warning, TEXT("[CesiumCarlaBridge] SetLayerVisible: no world."));
		return -1;
	}
	const FName TagName(*LayerTag);
	int32 Count = 0;
	for (TActorIterator<ACesium3DTileset> It(World); It; ++It)
	{
		ACesium3DTileset* Tileset = *It;
		if (!IsValid(Tileset)) continue;
		if (!LayerTag.IsEmpty() && !Tileset->ActorHasTag(TagName)) continue;
		Tileset->SetActorHiddenInGame(!bVisible);
		++Count;
	}
	UE_LOG(LogTemp, Display, TEXT("[CesiumCarlaBridge] SetLayerVisible('%s', %d): %d tileset(s)"),
		*LayerTag, bVisible ? 1 : 0, Count);
	return Count;
}

int32 UCesiumHeightSampler::SetLayerCollision(UObject* WorldContextObject, const FString& LayerTag, bool bEnabled)
{
	UWorld* World = GEngine
		? GEngine->GetWorldFromContextObject(WorldContextObject, EGetWorldErrorMode::ReturnNull)
		: nullptr;
	if (!World)
	{
		UE_LOG(LogTemp, Warning, TEXT("[CesiumCarlaBridge] SetLayerCollision: no world."));
		return -1;
	}
	const FName TagName(*LayerTag);
	int32 Count = 0;
	for (TActorIterator<ACesium3DTileset> It(World); It; ++It)
	{
		ACesium3DTileset* Tileset = *It;
		if (!IsValid(Tileset)) continue;
		if (!LayerTag.IsEmpty() && !Tileset->ActorHasTag(TagName)) continue;
		Tileset->SetCreatePhysicsMeshes(bEnabled);
		Tileset->RefreshTileset();
		++Count;
	}
	UE_LOG(LogTemp, Display, TEXT("[CesiumCarlaBridge] SetLayerCollision('%s', %d): %d tileset(s)"),
		*LayerTag, bEnabled ? 1 : 0, Count);
	return Count;
}

int32 UCesiumHeightSampler::SetCesiumTilesetsVisible(UObject* WorldContextObject, bool bVisible)
{
	UWorld* World = GEngine
		? GEngine->GetWorldFromContextObject(WorldContextObject, EGetWorldErrorMode::ReturnNull)
		: nullptr;
	if (!World)
	{
		UE_LOG(LogTemp, Warning, TEXT("[CesiumCarlaBridge] SetCesiumTilesetsVisible: no world."));
		return -1;
	}

	int32 Count = 0;
	for (TActorIterator<ACesium3DTileset> It(World); It; ++It)
	{
		ACesium3DTileset* Tileset = *It;
		if (!IsValid(Tileset)) continue;
		Tileset->SetActorHiddenInGame(!bVisible);
		++Count;
	}
	UE_LOG(LogTemp, Display, TEXT("[CesiumCarlaBridge] set %d tileset(s) visible=%d"), Count, bVisible ? 1 : 0);
	return Count;
}

int32 UCesiumHeightSampler::SetCesiumCollisionEnabled(UObject* WorldContextObject, bool bEnabled)
{
	UWorld* World = GEngine
		? GEngine->GetWorldFromContextObject(WorldContextObject, EGetWorldErrorMode::ReturnNull)
		: nullptr;
	if (!World)
	{
		UE_LOG(LogTemp, Warning, TEXT("[CesiumCarlaBridge] SetCesiumCollisionEnabled: no world."));
		return -1;
	}

	int32 Count = 0;
	for (TActorIterator<ACesium3DTileset> It(World); It; ++It)
	{
		ACesium3DTileset* Tileset = *It;
		if (!IsValid(Tileset)) continue;
		Tileset->SetCreatePhysicsMeshes(bEnabled);
		Tileset->RefreshTileset();
		++Count;
	}
	UE_LOG(LogTemp, Display, TEXT("[CesiumCarlaBridge] set %d tileset(s) collision=%d"), Count, bEnabled ? 1 : 0);
	return Count;
}

FVector UCesiumHeightSampler::GetCesiumOrigin(UObject* WorldContextObject)
{
	UWorld* World = GEngine
		? GEngine->GetWorldFromContextObject(WorldContextObject, EGetWorldErrorMode::ReturnNull)
		: nullptr;
	if (!World)
	{
		return FVector::ZeroVector;
	}
	ACesiumGeoreference* Georeference = ACesiumGeoreference::GetDefaultGeoreference(World);
	if (!Georeference)
	{
		return FVector::ZeroVector;
	}
	// FVector(Longitude X, Latitude Y, Height Z).
	return Georeference->GetOriginLongitudeLatitudeHeight();
}
