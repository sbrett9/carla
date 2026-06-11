// Copyright (c) 2026 CARLA-Cesium digital-twin project.
//
// UCesiumHeightSampler — a BlueprintFunctionLibrary that wraps the C++-only
// ACesium3DTileset::SampleHeightMostDetailed into a poll-friendly, static,
// BlueprintCallable (and therefore Python-callable) surface.
//
// Why a function library (not an EngineSubsystem): an engine subsystem coming
// from a late-loaded project plugin module is not reliably instantiated, so
// unreal.get_engine_subsystem(...) returned None in -game. Static library
// functions are always callable as unreal.CesiumHeightSampler.request_sample(...)
// with no instance retrieval. State lives in file-static storage in the .cpp.
//
// Async contract: RequestSample() kicks off the sample and returns immediately;
// the caller pumps the world tick (the natural game loop) and polls IsReady().
// The Cesium callback fires on the game thread when ALL points are resolved.
// The same statics are the reusable sampler the future CarlaServer.cpp
// `sample_terrain_heights` RPC calls.

#pragma once

#include "CoreMinimal.h"
#include "Kismet/BlueprintFunctionLibrary.h"
#include "CesiumSampleHeightResult.h"
#include "CesiumHeightSampler.generated.h"

UENUM(BlueprintType)
enum class ECesiumSampleState : uint8
{
	Idle        UMETA(DisplayName = "Idle"),
	InProgress  UMETA(DisplayName = "InProgress"),
	Done        UMETA(DisplayName = "Done"),
	Failed      UMETA(DisplayName = "Failed")
};

UCLASS()
class CESIUMCARLABRIDGE_API UCesiumHeightSampler : public UBlueprintFunctionLibrary
{
	GENERATED_BODY()

public:
	/**
	 * Begin an asynchronous height query against the first ACesium3DTileset found
	 * in WorldContextObject's world. Each input FVector is (X = longitude deg,
	 * Y = latitude deg, Z = ignored). Returns false (and sets state to Failed) if
	 * there is no world, no tileset, or an empty input. On success the state goes
	 * to InProgress; poll IsReady()/GetState() while ticking, then read GetResults().
	 *
	 * Only one sample runs at a time (state is process-global). If TilesetActorName
	 * is non-empty, only a tileset whose actor NAME contains it OR whose actor TAGS
	 * contain it is used (so "ground" selects the bare-earth layer for road-Z sampling).
	 */
	UFUNCTION(BlueprintCallable, Category = "CesiumCarla", meta = (WorldContext = "WorldContextObject"))
	static bool RequestSample(UObject* WorldContextObject, const TArray<FVector>& LonLatHeight, const FString& TilesetActorName);

	UFUNCTION(BlueprintCallable, Category = "CesiumCarla")
	static ECesiumSampleState GetState();

	UFUNCTION(BlueprintCallable, Category = "CesiumCarla")
	static bool IsReady();

	UFUNCTION(BlueprintCallable, Category = "CesiumCarla")
	static TArray<FCesiumSampleHeightResult> GetResults();

	UFUNCTION(BlueprintCallable, Category = "CesiumCarla")
	static TArray<FString> GetWarnings();

	UFUNCTION(BlueprintCallable, Category = "CesiumCarla")
	static FString GetStatusMessage();

	/** Count of results whose SampleSuccess is true. */
	UFUNCTION(BlueprintCallable, Category = "CesiumCarla")
	static int32 GetSuccessCount();

	/**
	 * Configure the Cesium globe at runtime for a georeference origin, so a freshly
	 * (re)loaded world — e.g. OpenDriveMap.umap after generate_opendrive_world — lines
	 * up with the active .xodr. Sets the default ACesiumGeoreference's cartographic origin
	 * to (OriginLatitude, OriginLongitude, OriginHeight).
	 *
	 * Layer model (08_Layer_Architecture): ensures up to two tagged tilesets sharing the
	 * georeference — a "photoreal" visual tileset (IonAssetId, visible, no collision) and,
	 * when GroundIonAssetId &gt; 0, a "ground" bare-earth tileset (e.g. Cesium World Terrain
	 * asset 1) that is HIDDEN with collision ON and is the height-sample source. Each is
	 * found-by-tag-or-spawned (photoreal also adopts a pre-placed untagged tileset). The Ion
	 * token is applied when non-empty; tilesets refresh if bRefreshTileset. Returns true if a
	 * CesiumGeoreference was found/created.
	 */
	UFUNCTION(BlueprintCallable, Category = "CesiumCarla")
	static bool ConfigureCesiumForOrigin(
		UObject* WorldContextObject,
		double OriginLatitude,
		double OriginLongitude,
		double OriginHeight,
		const FString& IonAccessToken,
		int64 IonAssetId,
		int64 GroundIonAssetId,
		bool bRefreshTileset);

	/**
	 * Per-layer visibility: show/hide every ACesium3DTileset tagged LayerTag (empty = all).
	 * Tilesets are tagged by ConfigureCesiumForOrigin ("photoreal", "ground"). Rendering only;
	 * collision is independent (see SetLayerCollision). Returns the number toggled (-1 no world).
	 */
	UFUNCTION(BlueprintCallable, Category = "CesiumCarla")
	static int32 SetLayerVisible(UObject* WorldContextObject, const FString& LayerTag, bool bVisible);

	/**
	 * Per-layer physics: enable/disable collision on every ACesium3DTileset tagged LayerTag
	 * (empty = all). Calls SetCreatePhysicsMeshes then RefreshTileset. Independent of
	 * visibility. Returns the number changed (-1 no world).
	 */
	UFUNCTION(BlueprintCallable, Category = "CesiumCarla")
	static int32 SetLayerCollision(UObject* WorldContextObject, const FString& LayerTag, bool bEnabled);

	/**
	 * Show/hide every ACesium3DTileset in the world (the photogrammetry overlay), so a
	 * client can watch just the CARLA actors against an empty background. Returns the
	 * number of tilesets toggled (or -1 if there is no world).
	 */
	UFUNCTION(BlueprintCallable, Category = "CesiumCarla")
	static int32 SetCesiumTilesetsVisible(UObject* WorldContextObject, bool bVisible);

	/**
	 * Enable/disable physics collision on every ACesium3DTileset in the world. When
	 * bEnabled is false, vehicles pass through the photogrammetry surface (useful for
	 * A/B comparisons). Calls SetCreatePhysicsMeshes then RefreshTileset so the change
	 * takes effect immediately. Returns the number of tilesets changed (or -1 if there
	 * is no world). Collision is ON by default — this toggle never changes spawn defaults.
	 */
	UFUNCTION(BlueprintCallable, Category = "CesiumCarla")
	static int32 SetCesiumCollisionEnabled(UObject* WorldContextObject, bool bEnabled);

	/**
	 * Returns the default CesiumGeoreference's cartographic origin as
	 * FVector(Longitude, Latitude, Height-in-metres), or (0,0,0) if there is no
	 * georeference. Lets a client convert a local Unreal Z to a true elevation
	 * (origin height + local z).
	 */
	UFUNCTION(BlueprintCallable, Category = "CesiumCarla")
	static FVector GetCesiumOrigin(UObject* WorldContextObject);
};
