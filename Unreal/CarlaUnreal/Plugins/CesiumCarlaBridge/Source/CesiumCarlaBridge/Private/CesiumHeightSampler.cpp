// Copyright (c) 2026 CARLA-Cesium digital-twin project.

#include "CesiumHeightSampler.h"

#include "Cesium3DTileset.h"
#include "CesiumGeoreference.h"
#include "CesiumCreditSystem.h"
#include "CesiumSunSky.h"
#include "CesiumSensorViewPublisher.h"
#include "CesiumTimeOfDayController.h"
#include "OriginPlacement.h"
#include "Engine/Engine.h"
#include "Engine/World.h"
#include "Engine/DirectionalLight.h"
#include "Engine/SkyLight.h"
#include "Components/LightComponent.h"
#include "Components/SkyLightComponent.h"
#include "EngineUtils.h" // TActorIterator
#include "UObject/UnrealType.h" // FDoubleProperty (reflection read of CesiumSunSky angles)

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

		// Per point, at Verbose. A drape grid is one point per cell over the whole map
		// area, so this loop runs millions of times on a large one: 2,664,983 points for
		// a 10.7 km2 area at 2 m, which at Display wrote 3.7 million lines and a 463 MB
		// log. The formatting and synchronous file write per point dominated the sample,
		// making a build that was progressing normally look like a hang.
		//
		// Enable with -LogCmds="LogTemp Verbose" when a specific point needs inspecting.
		for (int32 i = 0; i < InResults.Num(); ++i)
		{
			const FCesiumSampleHeightResult& R = InResults[i];
			UE_LOG(LogTemp, Verbose,
				TEXT("[CesiumCarlaBridge]   [%d] lon=%.7f lat=%.7f h=%.3f m ok=%d"),
				i, R.LongitudeLatitudeHeight.X, R.LongitudeLatitudeHeight.Y,
				R.LongitudeLatitudeHeight.Z, R.SampleSuccess ? 1 : 0);
		}

		// The points that failed are worth seeing without turning the rest on, since a
		// height that did not sample is a hole in the drape. Capped, because a tileset
		// that covers none of the area fails every point and would spam just as badly.
		constexpr int32 MaxFailuresLogged = 20;
		int32 Reported = 0;
		for (int32 i = 0; i < InResults.Num(); ++i)
		{
			const FCesiumSampleHeightResult& R = InResults[i];
			if (R.SampleSuccess)
			{
				continue;
			}
			if (Reported++ >= MaxFailuresLogged)
			{
				UE_LOG(LogTemp, Warning,
					TEXT("[CesiumCarlaBridge]   ... and %d more points that did not sample"),
					InResults.Num() - Ok - MaxFailuresLogged);
				break;
			}
			UE_LOG(LogTemp, Warning,
				TEXT("[CesiumCarlaBridge]   [%d] did not sample: lon=%.7f lat=%.7f"),
				i, R.LongitudeLatitudeHeight.X, R.LongitudeLatitudeHeight.Y);
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

	// Ensure the default credit system exists BEFORE any tileset loads. A tileset registers its
	// attribution credits as it streams; the editor auto-creates an ACesiumCreditSystem, but a
	// headless/packaged server building the world procedurally has none, and the missing credit
	// system crashes inside CesiumUtility::CreditSystem::createCredit. Find-or-create it here, the
	// same way the georeference is ensured above.
	if (!ACesiumCreditSystem::GetDefaultCreditSystem(World))
	{
		UE_LOG(LogTemp, Warning, TEXT("[CesiumCarlaBridge] could not get/create a CesiumCreditSystem."));
	}

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

	// The OpenDriveMap template ships a plain ADirectionalLight + ASkyLight for baseline lighting.
	// They are not georeferenced and are driven by nothing, so alongside the CesiumSunSky below they
	// show up as a SECOND, fixed sun disc (through the shared SkyAtmosphere) plus doubled ambient.
	// CesiumSunSky is the single lighting authority, so disable the level's lights here. Its own
	// sun/sky are COMPONENTS on that actor (not ADirectionalLight/ASkyLight actors, since
	// UseLevelDirectionalLight defaults false), so iterating those actor types never touches them.
	{
		int32 NumLevelLightsDisabled = 0;
		for (TActorIterator<ADirectionalLight> It(World); It; ++It)
		{
			if (!IsValid(*It)) continue;
			if (ULightComponent* LightComp = (*It)->GetLightComponent())
			{
				LightComp->SetVisibility(false); // removes its lighting and its SkyAtmosphere sun disc
				++NumLevelLightsDisabled;
			}
		}
		for (TActorIterator<ASkyLight> It(World); It; ++It)
		{
			if (!IsValid(*It)) continue;
			if (USkyLightComponent* SkyComp = (*It)->GetLightComponent())
			{
				SkyComp->SetVisibility(false);
				++NumLevelLightsDisabled;
			}
		}
		if (NumLevelLightsDisabled > 0)
		{
			UE_LOG(LogTemp, Display,
				TEXT("[CesiumCarlaBridge] disabled %d pre-existing level light(s) so CesiumSunSky is the sole sun."),
				NumLevelLightsDisabled);
		}
	}

	// The generated OpenDriveMap has no CARLA weather actor ("Missing weather class"), so nothing
	// drives lighting; spawn a CesiumSunSky for physically-based georeferenced lighting (and the
	// correct EO basis later: real solar angle / shadows).
	//
	// ACesiumSunSky computes the sun from the georeference latitude/longitude plus its SolarTime
	// and TimeZone. Its class defaults (SolarTime 13:00, TimeZone -5) assume a US-Eastern longitude;
	// applied to any other map they place the sun far from local noon (e.g. at longitude +56 the
	// -5 zone is ~8.75 h / ~131 deg off, pinning the sun near the horizon so the scene looks like
	// dusk). Derive the time zone from the origin longitude and start at local solar noon so the
	// world is correctly lit for wherever the OSM origin is. Disable DST for a deterministic clock.
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
			SunSky->SolarTime = 12.0;
			SunSky->UseDaylightSavingTime = false;
			// Sets TimeZone = longitude / 15 and calls UpdateSun() internally.
			SunSky->EstimateTimeZoneForLongitude(OriginLongitude);
			bSpawnedSunSky = true;
		}
		else
		{
			UE_LOG(LogTemp, Warning, TEXT("[CesiumCarlaBridge] failed to spawn ACesiumSunSky."));
		}
	}

	// Make the camera sensors themselves drive tile selection. Without this the tiles are chosen from
	// the spectator's pose and the game viewport's size, so a sensor whose aspect ratio is taller than
	// the viewport's renders bands of never-requested tiles, and tile detail is picked for the
	// viewport's pixel count rather than the sensor's. The publisher lives as long as this world, so a
	// client that later attaches without rebuilding inherits it.
	const bool bSpawnedPublisher = (ACesiumSensorViewPublisher::FindOrSpawn(World) != nullptr);
	if (!bSpawnedPublisher)
	{
		UE_LOG(LogTemp, Warning,
			TEXT("[CesiumCarlaBridge] failed to spawn ACesiumSensorViewPublisher; tiles will be selected "
				 "from the spectator view only and sensor resolutions other than the viewport's aspect "
				 "ratio may show gaps."));
	}

	UE_LOG(LogTemp, Display,
		TEXT("[CesiumCarlaBridge] Configured georeference (lat=%.7f lon=%.7f h=%.3f) + %d layer tileset(s) (photoreal asset=%lld, ground asset=%lld)%s%s."),
		OriginLatitude, OriginLongitude, OriginHeight, NumTilesets,
		static_cast<long long>(IonAssetId), static_cast<long long>(GroundIonAssetId),
		bSpawnedSunSky ? TEXT(" (spawned sun)") : TEXT(""),
		bSpawnedPublisher ? TEXT(" (sensor views published)") : TEXT(""));
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

int32 UCesiumHeightSampler::SetLayerVerticalOffset(UObject* WorldContextObject, const FString& LayerTag, double OffsetMeters)
{
	UWorld* World = GEngine
		? GEngine->GetWorldFromContextObject(WorldContextObject, EGetWorldErrorMode::ReturnNull)
		: nullptr;
	if (!World)
	{
		UE_LOG(LogTemp, Warning, TEXT("[CesiumCarlaBridge] SetLayerVerticalOffset: no world."));
		return -1;
	}

	// The TRUTH georeference (tagged DEFAULT_GEOREFERENCE; safe — a plain SpawnActor below is NOT
	// auto-tagged as default, so it can never be returned here). Read its origin so the offset
	// georeference shares the same lat/lon and only differs in height.
	ACesiumGeoreference* Default = ACesiumGeoreference::GetDefaultGeoreference(World);
	if (!Default)
	{
		UE_LOG(LogTemp, Warning, TEXT("[CesiumCarlaBridge] SetLayerVerticalOffset: no default georeference."));
		return -1;
	}
	const FVector O = Default->GetOriginLongitudeLatitudeHeight(); // (lon X, lat Y, height Z)

	// Target georeference for the layer: the default (undo) when offset ~0, else a dedicated
	// offset georeference. Raising a georeference's origin height renders its tiles LOWER, so to
	// move tiles by +OffsetMeters (up) the origin height is (default height − OffsetMeters).
	const FName OffsetGeorefTag(TEXT("carla_offset_georef"));
	ACesiumGeoreference* Target = Default;
	if (FMath::Abs(OffsetMeters) > KINDA_SMALL_NUMBER)
	{
		ACesiumGeoreference* OffsetGeoref = nullptr;
		for (TActorIterator<ACesiumGeoreference> It(World); It; ++It)
		{
			if (IsValid(*It) && (*It)->ActorHasTag(OffsetGeorefTag)) { OffsetGeoref = *It; break; }
		}
		if (!OffsetGeoref)
		{
			FActorSpawnParameters SpawnParams;
			SpawnParams.SpawnCollisionHandlingOverride = ESpawnActorCollisionHandlingMethod::AlwaysSpawn;
			OffsetGeoref = World->SpawnActor<ACesiumGeoreference>(SpawnParams);
			if (!OffsetGeoref)
			{
				UE_LOG(LogTemp, Warning, TEXT("[CesiumCarlaBridge] SetLayerVerticalOffset: spawn of offset georeference failed."));
				return -1;
			}
			OffsetGeoref->Tags.Add(OffsetGeorefTag);
		}
		OffsetGeoref->SetOriginPlacement(EOriginPlacement::CartographicOrigin);
		OffsetGeoref->SetOriginLongitudeLatitudeHeight(FVector(O.X, O.Y, O.Z - OffsetMeters));
		Target = OffsetGeoref;
	}

	const FName TagName(*LayerTag);
	int32 Count = 0;
	for (TActorIterator<ACesium3DTileset> It(World); It; ++It)
	{
		ACesium3DTileset* Tileset = *It;
		if (!IsValid(Tileset)) continue;
		if (!LayerTag.IsEmpty() && !Tileset->ActorHasTag(TagName)) continue;
		Tileset->SetGeoreference(TSoftObjectPtr<ACesiumGeoreference>(Target));
		Tileset->RefreshTileset();
		++Count;
	}
	UE_LOG(LogTemp, Display, TEXT("[CesiumCarlaBridge] SetLayerVerticalOffset('%s', %.3f m): %d tileset(s) %s"),
		*LayerTag, OffsetMeters, Count,
		(Target == Default) ? TEXT("-> default georeference") : TEXT("-> offset georeference"));
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

// Find the first valid CesiumSunSky in the world (ConfigureCesiumForOrigin spawns exactly one),
// or nullptr if none exists yet.
static ACesiumSunSky* FindCesiumSunSky(UWorld* World)
{
	if (!World)
	{
		return nullptr;
	}
	for (TActorIterator<ACesiumSunSky> It(World); It; ++It)
	{
		if (IsValid(*It))
		{
			return *It;
		}
	}
	return nullptr;
}

// The sun's computed Elevation/Azimuth are protected BlueprintReadOnly properties on ACesiumSunSky.
// Read them through the reflection system — the same public path Blueprints use for a BlueprintReadOnly
// property — so we depend only on the vendored plugin's declared contract, not on editing it. The
// FProperty lookup is cached (the class is fixed); returns 0 if a future Cesium build renames the
// field. Degrees: elevation above the horizon, azimuth clockwise from North.
static double GetSunElevationDeg(const ACesiumSunSky* SunSky)
{
	static const FDoubleProperty* Prop =
		CastField<FDoubleProperty>(ACesiumSunSky::StaticClass()->FindPropertyByName(TEXT("Elevation")));
	return (SunSky && Prop) ? Prop->GetPropertyValue_InContainer(SunSky) : 0.0;
}

static double GetSunAzimuthDeg(const ACesiumSunSky* SunSky)
{
	static const FDoubleProperty* Prop =
		CastField<FDoubleProperty>(ACesiumSunSky::StaticClass()->FindPropertyByName(TEXT("Azimuth")));
	return (SunSky && Prop) ? Prop->GetPropertyValue_InContainer(SunSky) : 0.0;
}

bool UCesiumHeightSampler::SetSolarTime(UObject* WorldContextObject, double SolarTimeHours)
{
	UWorld* World = GEngine
		? GEngine->GetWorldFromContextObject(WorldContextObject, EGetWorldErrorMode::ReturnNull)
		: nullptr;
	ACesiumSunSky* SunSky = FindCesiumSunSky(World);
	if (!SunSky)
	{
		UE_LOG(LogTemp, Warning, TEXT("[CesiumCarlaBridge] SetSolarTime: no CesiumSunSky in the world."));
		return false;
	}
	// Wrap into [0, 24) so callers can pass a freely-accumulating clock (e.g. an advancing time).
	SunSky->SolarTime = FMath::Fmod(FMath::Fmod(SolarTimeHours, 24.0) + 24.0, 24.0);
	SunSky->UpdateSun();
	return true;
}

bool UCesiumHeightSampler::SetSolarDate(UObject* WorldContextObject, int32 Year, int32 Month, int32 Day)
{
	UWorld* World = GEngine
		? GEngine->GetWorldFromContextObject(WorldContextObject, EGetWorldErrorMode::ReturnNull)
		: nullptr;
	ACesiumSunSky* SunSky = FindCesiumSunSky(World);
	if (!SunSky)
	{
		UE_LOG(LogTemp, Warning, TEXT("[CesiumCarlaBridge] SetSolarDate: no CesiumSunSky in the world."));
		return false;
	}
	SunSky->Year = Year;
	SunSky->Month = FMath::Clamp(Month, 1, 12);
	SunSky->Day = FMath::Clamp(Day, 1, 31);
	SunSky->UpdateSun();
	return true;
}

TArray<double> UCesiumHeightSampler::GetSolarState(UObject* WorldContextObject)
{
	TArray<double> Out;
	UWorld* World = GEngine
		? GEngine->GetWorldFromContextObject(WorldContextObject, EGetWorldErrorMode::ReturnNull)
		: nullptr;
	ACesiumSunSky* SunSky = FindCesiumSunSky(World);
	if (!SunSky)
	{
		return Out;   // empty => caller reports "no sun"
	}
	// Origin latitude/longitude drive the solar geometry; reuse the default georeference (the sun
	// resolves the same one). GetOriginLongitudeLatitudeHeight() is (lon X, lat Y, height Z).
	double Lat = 0.0, Lon = 0.0;
	if (ACesiumGeoreference* Georeference = ACesiumGeoreference::GetDefaultGeoreference(World))
	{
		const FVector O = Georeference->GetOriginLongitudeLatitudeHeight();
		Lon = O.X;
		Lat = O.Y;
	}
	// Layout mirrored by the Python shim's get_solar_state():
	// [solar_time, year, month, day, time_zone, lat, lon, elevation_deg, azimuth_deg, advancing, rate].
	Out.Add(SunSky->SolarTime);
	Out.Add(static_cast<double>(SunSky->Year));
	Out.Add(static_cast<double>(SunSky->Month));
	Out.Add(static_cast<double>(SunSky->Day));
	Out.Add(SunSky->TimeZone);
	Out.Add(Lat);
	Out.Add(Lon);
	Out.Add(GetSunElevationDeg(SunSky));
	Out.Add(GetSunAzimuthDeg(SunSky));
	// advancing/rate come from the time-of-day controller if one exists (set_time_advance spawns it).
	double Advancing = 0.0, Rate = 1.0;
	for (TActorIterator<ACesiumTimeOfDayController> It(World); It; ++It)
	{
		if (IsValid(*It))
		{
			Advancing = (*It)->bAdvancing ? 1.0 : 0.0;
			Rate = (*It)->Rate;
			break;
		}
	}
	Out.Add(Advancing);
	Out.Add(Rate);
	return Out;
}

// Find the time-of-day controller, or spawn one if none exists.
static ACesiumTimeOfDayController* FindOrSpawnTimeController(UWorld* World)
{
	if (!World)
	{
		return nullptr;
	}
	for (TActorIterator<ACesiumTimeOfDayController> It(World); It; ++It)
	{
		if (IsValid(*It))
		{
			return *It;
		}
	}
	FActorSpawnParameters Params;
	Params.SpawnCollisionHandlingOverride = ESpawnActorCollisionHandlingMethod::AlwaysSpawn;
	return World->SpawnActor<ACesiumTimeOfDayController>(Params);
}

bool UCesiumHeightSampler::SetTimeAdvance(UObject* WorldContextObject, bool bEnabled, double Rate)
{
	UWorld* World = GEngine
		? GEngine->GetWorldFromContextObject(WorldContextObject, EGetWorldErrorMode::ReturnNull)
		: nullptr;
	if (!World)
	{
		return false;
	}
	if (!FindCesiumSunSky(World))
	{
		UE_LOG(LogTemp, Warning, TEXT("[CesiumCarlaBridge] SetTimeAdvance: no CesiumSunSky in the world."));
		return false;
	}
	ACesiumTimeOfDayController* Controller = FindOrSpawnTimeController(World);
	if (!Controller)
	{
		return false;
	}
	Controller->bAdvancing = bEnabled;
	Controller->Rate = Rate;
	return true;
}
