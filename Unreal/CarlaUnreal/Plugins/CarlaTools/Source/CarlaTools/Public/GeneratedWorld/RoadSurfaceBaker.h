// Copyright (c) 2026 CARLA-Cesium digital-twin project.
//
// Turns the road surface described by an OpenDRIVE file into static mesh assets placed in a level.
//
// A generated world normally builds its road surface at play time into procedural mesh actors, which
// exist only while the simulation runs. That is enough to drive on and measure, but it leaves nothing
// in the level to look at or edit: opening it in the editor shows an empty map.
//
// Baking produces the same surface as saved assets instead -- one mesh per lane, carrying the texture
// coordinates the generator already computes, placed as static mesh actors under an asset path that
// gives them the right semantic label. The level then contains its roads, and the generator is told
// not to build a second set on top at play time.

#pragma once

#include <util/ue-header-guard-begin.h>
#include "CoreMinimal.h"
#include "Kismet/BlueprintFunctionLibrary.h"
#include <util/ue-header-guard-end.h>

#include "RoadSurfaceBaker.generated.h"

class UGeoreferencedWorldSettings;

/** What a bake produced, so a caller can report it without re-deriving anything. */
USTRUCT(BlueprintType)
struct CARLATOOLS_API FRoadSurfaceBakeResult
{
	GENERATED_BODY()

	UPROPERTY(BlueprintReadOnly, Category = "Generated World")
	bool bSucceeded = false;

	/** Static mesh assets written, one per lane piece. */
	UPROPERTY(BlueprintReadOnly, Category = "Generated World")
	int32 PiecesBaked = 0;

	UPROPERTY(BlueprintReadOnly, Category = "Generated World")
	int32 TrianglesBaked = 0;

	/** Empty on success; on failure, what went wrong in terms a person can act on. */
	UPROPERTY(BlueprintReadOnly, Category = "Generated World")
	FString FailureReason;
};

UCLASS()
class CARLATOOLS_API URoadSurfaceBaker : public UBlueprintFunctionLibrary
{
	GENERATED_BODY()

public:
	/**
	 * Bake the road surface into World and place it there.
	 *
	 * OpenDriveText is the road network; Settings supplies the parameters the surface was generated
	 * with and the extent to cover, so the baked geometry matches what the simulation would build.
	 * Assets are written under AssetRootFolder in a subfolder named for the map.
	 *
	 * Any previously baked surface for this map is removed first, so re-baking replaces rather than
	 * accumulates.
	 */
	UFUNCTION(BlueprintCallable, Category = "Generated World")
	static FRoadSurfaceBakeResult BakeIntoWorld(
		UWorld* World,
		const FString& OpenDriveText,
		const FString& MapName,
		const UGeoreferencedWorldSettings* Settings,
		const FString& AssetRootFolder = TEXT("/Game/Carla/Static"));
};
