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
	 * is non-empty, only a tileset whose actor name contains it is used.
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
};
