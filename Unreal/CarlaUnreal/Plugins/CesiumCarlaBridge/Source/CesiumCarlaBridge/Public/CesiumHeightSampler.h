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
	 * Per-layer visibility in the EDITOR viewport, which is a separate flag from the one
	 * SetLayerVisible drives. A layer hidden in the simulation is still drawn while editing, and the
	 * bare-earth ground layer occupies the same space as the photoreal imagery, so left drawn it
	 * hides the surface being edited.
	 *
	 * This drives the same flag as the eye icon in the outliner, so a person can always reveal a
	 * hidden layer while editing. That flag lasts for the editor session rather than being saved,
	 * which is why hiding is applied each time the level is opened rather than once at import.
	 *
	 * Returns the number of tilesets changed (-1 when there is no world). Does nothing outside the
	 * editor, where the flag has no meaning.
	 */
	/**
	 * How many ion-backed tilesets in this world have no access token of their own.
	 *
	 * A tileset streams on the token it carries, on the project's token, or not at all. This reports
	 * the first of those, so a caller can tell the difference between a world that will stream and one
	 * that will silently show nothing, and say so while there is still someone reading the log.
	 */
	UFUNCTION(BlueprintCallable, Category = "CesiumCarla")
	static int32 CountLayersWithoutIonToken(UObject* WorldContextObject);

	UFUNCTION(BlueprintCallable, Category = "CesiumCarla")
	static int32 SetLayerHiddenInEditor(
		UObject* WorldContextObject, const FString& LayerTag, bool bHidden);

	/**
	 * Per-layer physics: enable/disable collision on every ACesium3DTileset tagged LayerTag
	 * (empty = all). Calls SetCreatePhysicsMeshes then RefreshTileset. Independent of
	 * visibility. Returns the number changed (-1 no world).
	 */
	UFUNCTION(BlueprintCallable, Category = "CesiumCarla")
	static int32 SetLayerCollision(UObject* WorldContextObject, const FString& LayerTag, bool bEnabled);

	/**
	 * Per-layer VERTICAL OFFSET (decouple a layer's collision/render height from the truth datum).
	 * Shifts every ACesium3DTileset tagged LayerTag up/down by OffsetMeters (signed, +up) WITHOUT
	 * moving the truth georeference: it assigns the tagged tileset(s) to a DEDICATED
	 * CesiumGeoreference whose origin height = (default origin height − OffsetMeters), so the tiles
	 * render/collide OffsetMeters from their true position while everything else (and the default
	 * georeference / GetCesiumOrigin) stays truthed to the OSM origin. Used to drop the hidden,
	 * collidable bare-earth "ground" layer by the height-align offset so its collision mesh
	 * coincides with the offset road mesh (no on-road float, off-road still supported). Height
	 * SAMPLING is geodetic and is unaffected by this. OffsetMeters == 0 reassigns the layer back to
	 * the default georeference (undo). Returns the number of tilesets moved (-1 if no world).
	 *
	 * NOTE: apply this AFTER the final ConfigureCesiumForOrigin — that call reassigns tilesets to
	 * the default georeference, which would undo the offset.
	 */
	UFUNCTION(BlueprintCallable, Category = "CesiumCarla")
	static int32 SetLayerVerticalOffset(UObject* WorldContextObject, const FString& LayerTag, double OffsetMeters);

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

	/**
	 * Set the CesiumSunSky solar clock (local hours in the sun's time zone; wrapped into [0,24))
	 * and refresh the sun. CesiumSunSky is the single time-of-day / lighting authority for the
	 * georeferenced world. Returns false if no ACesiumSunSky exists in the world.
	 */
	UFUNCTION(BlueprintCallable, Category = "CesiumCarla")
	static bool SetSolarTime(UObject* WorldContextObject, double SolarTimeHours);

	/**
	 * Set the CesiumSunSky calendar date (drives the seasonal sun declination), clamped to valid
	 * month/day, and refresh the sun. Returns false if no ACesiumSunSky exists in the world.
	 */
	UFUNCTION(BlueprintCallable, Category = "CesiumCarla")
	static bool SetSolarDate(UObject* WorldContextObject, int32 Year, int32 Month, int32 Day);

	/**
	 * Bind the whole solar epoch in one call: the civil calendar date, the civil clock
	 * (`SolarTimeHours`, wrapped into [0,24)) and the UTC offset in force at that instant
	 * (`UtcOffsetHours`, clamped to the sun's -12..14 range; half-hour zones are representable).
	 * Daylight saving is left off, because the offset already expresses it.
	 *
	 * Setting the time zone is what makes `SolarTimeHours` a CIVIL clock. Without it the zone stays
	 * at longitude/15 from EstimateTimeZoneForLongitude, so the clock is local MEAN SOLAR time at
	 * the map longitude -- at longitude 56.18 that is +03:44.7 against Iran's civil +03:30, nearly
	 * fifteen minutes, which near sunrise or sunset is the difference between a sun above and below
	 * the horizon. Binding the declared civil offset also makes the solar state read back the
	 * instant that was declared, rather than a converted one every reader would have to undo.
	 *
	 * One UpdateSun for the whole epoch, so no frame can observe the new time on the old date the
	 * way a SetSolarTime/SetSolarDate pair allows. Returns false if there is no ACesiumSunSky, or if
	 * Year/Month/Day is not a calendar date -- an out-of-calendar date makes the sun-position solver
	 * return its zeroed default, which reads back as an elevation of -180 degrees.
	 */
	UFUNCTION(BlueprintCallable, Category = "CesiumCarla")
	static bool SetSolarEpoch(
		UObject* WorldContextObject, int32 Year, int32 Month, int32 Day,
		double SolarTimeHours, double UtcOffsetHours);

	/**
	 * Read the current solar clock/date/origin/angles from the ACesiumSunSky, packed as
	 * [solar_time, year, month, day, time_zone, origin_lat, origin_lon, elevation_deg, azimuth_deg,
	 * advancing(0/1), rate, corrected_elevation_deg]. Empty array if no ACesiumSunSky exists.
	 * elevation/azimuth are the sun geometry from the last UpdateSun; advancing/rate come from the
	 * ACesiumTimeOfDayController (0/1.0 if none).
	 *
	 * corrected_elevation_deg is the same elevation with atmospheric refraction applied, and it is
	 * what the sun's directional light is actually rotated by. It is appended last so the first
	 * eleven entries keep the positions their readers index by.
	 */
	UFUNCTION(BlueprintCallable, Category = "CesiumCarla")
	static TArray<double> GetSolarState(UObject* WorldContextObject);

	/**
	 * Enable/disable automatic advancement of the CesiumSunSky solar clock (the sun moves as the
	 * scene runs). Finds-or-spawns an ACesiumTimeOfDayController that ticks with the world, so it
	 * advances in wall-clock time under asynchronous mode and in sim time under synchronous ticking.
	 * `Rate` is sun-clock seconds per real/sim second (1.0 = real time; >1 accelerates). Returns
	 * false if no ACesiumSunSky exists in the world.
	 */
	UFUNCTION(BlueprintCallable, Category = "CesiumCarla")
	static bool SetTimeAdvance(UObject* WorldContextObject, bool bEnabled, double Rate);
};
