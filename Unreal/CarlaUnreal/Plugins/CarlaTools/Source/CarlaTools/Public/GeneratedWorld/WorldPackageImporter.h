// Copyright (c) 2026 CARLA-Cesium digital-twin project.
//
// Turns a world package on disk into a level that can be opened, edited by hand, and loaded by name.
//
// A world package is what a build writes out: the road network, the per-cell fields relating the
// driven surface to bare earth, and a manifest describing the datum, the imagery layers and the
// sandbox. This reads one and produces the level equivalent -- the two data assets, a level carrying
// an initializer that applies them, and the road network placed where the runtime looks for it.
//
// Every entry point is BlueprintCallable and static, so the editor utility widget a person uses and
// the headless script that tests it drive exactly the same code.

#pragma once

#include <util/ue-header-guard-begin.h>
#include "CoreMinimal.h"
#include "Kismet/BlueprintFunctionLibrary.h"
#include <util/ue-header-guard-end.h>

#include "WorldPackageImporter.generated.h"

class UGeoreferencedWorldSettings;

/** What an import produced, so a caller can report or verify it without re-deriving anything. */
USTRUCT(BlueprintType)
struct CARLATOOLS_API FWorldPackageImportResult
{
	GENERATED_BODY()

	UPROPERTY(BlueprintReadOnly, Category = "Import")
	bool bSucceeded = false;

	/** Long package name of the level that was written, e.g. /Game/Carla/Maps/Generated/Area. */
	UPROPERTY(BlueprintReadOnly, Category = "Import")
	FString LevelPackageName;

	/** Long package name of the settings asset the level's initializer points at. */
	UPROPERTY(BlueprintReadOnly, Category = "Import")
	FString SettingsPackageName;

	/** Long package name of the per-cell field asset, empty when the world has no per-cell field. */
	UPROPERTY(BlueprintReadOnly, Category = "Import")
	FString OffsetFieldPackageName;

	/** Where the road network was placed, which is where the runtime looks for it. */
	UPROPERTY(BlueprintReadOnly, Category = "Import")
	FString OpenDriveFilePath;

	/** Empty on success; on failure, what went wrong in terms a person can act on. */
	UPROPERTY(BlueprintReadOnly, Category = "Import")
	FString FailureReason;
};

UCLASS()
class CARLATOOLS_API UWorldPackageImporter : public UBlueprintFunctionLibrary
{
	GENERATED_BODY()

public:
	/**
	 * Import a world package into a saved, editable level.
	 *
	 * PackageDirectory is the folder a build wrote with --emit-world-package. MapName selects which
	 * world in it. DestinationFolder is a content path such as /Game/Carla/Maps/Generated; the level
	 * is written inside it under MapName, with its assets beside it.
	 *
	 * Re-importing the same world overwrites what it produced before, so a rebuilt area can be
	 * refreshed in place.
	 */
	UFUNCTION(BlueprintCallable, Category = "Generated World")
	static FWorldPackageImportResult ImportWorldPackage(
		const FString& PackageDirectory,
		const FString& MapName,
		const FString& DestinationFolder = TEXT("/Game/Carla/Maps/Generated"));

	/**
	 * Create just the settings and per-cell field assets, without touching any level.
	 *
	 * Useful on its own to refresh the description of a world whose level already exists, and it is
	 * the half that can be checked without opening a map.
	 */
	UFUNCTION(BlueprintCallable, Category = "Generated World")
	static UGeoreferencedWorldSettings* CreateWorldSettingsAssets(
		const FString& PackageDirectory,
		const FString& MapName,
		const FString& DestinationFolder,
		FString& OutFailureReason);

	/** True when the folder holds a readable manifest for MapName. Cheap; for a widget to gate on. */
	UFUNCTION(BlueprintCallable, Category = "Generated World")
	static bool IsWorldPackagePresent(const FString& PackageDirectory, const FString& MapName);

	/** Every world name a package folder holds, for a widget to offer as a choice. */
	UFUNCTION(BlueprintCallable, Category = "Generated World")
	static TArray<FString> ListWorldPackages(const FString& PackageDirectory);
};
