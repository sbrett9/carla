// Copyright (c) 2026 CARLA-Cesium digital-twin project.
//
// Re-homes an imported world into a plugin of its own, so it can be added to a build after that build
// was made.
//
// A world is imported under /Game so it can be edited alongside the rest of the project's content. That
// is the wrong place for it to ship from: /Game is the base game's namespace, cooked into the package
// at build time, with no version identity of its own. A world that lives in a plugin gets its own mount
// root, is removable and replaceable by deleting or swapping a directory, and -- because the shipped
// package is loose cooked files with no plugin manifest -- is discovered by a directory scan when it is
// dropped in afterwards.
//
// The mount root is baked into every cooked package reference, so it is fixed at /<WorldName>/ and must
// stay there. Moving it later invalidates every world already exported.

#pragma once

#include <util/ue-header-guard-begin.h>
#include "CoreMinimal.h"
#include "Kismet/BlueprintFunctionLibrary.h"
#include <util/ue-header-guard-end.h>

#include "GeneratedLevelExporter.generated.h"

/** What an export produced, and what to do with it. */
USTRUCT(BlueprintType)
struct CARLATOOLS_API FGeneratedLevelExportResult
{
	GENERATED_BODY()

	UPROPERTY(BlueprintReadOnly, Category = "Export")
	bool bSucceeded = false;

	/** Directory holding the plugin, inside the project's own Plugins folder. */
	UPROPERTY(BlueprintReadOnly, Category = "Export")
	FString PluginDirectory;

	/** Long package name the world is now addressable by, e.g. /Area/Maps/Area. */
	UPROPERTY(BlueprintReadOnly, Category = "Export")
	FString LevelPackageName;

	/** Assets re-homed into the plugin, the level included. */
	UPROPERTY(BlueprintReadOnly, Category = "Export")
	int32 AssetsExported = 0;

	/** Empty on success; on failure, what went wrong in terms a person can act on. */
	UPROPERTY(BlueprintReadOnly, Category = "Export")
	FString FailureReason;
};

UCLASS()
class CARLATOOLS_API UGeneratedLevelExporter : public UBlueprintFunctionLibrary
{
	GENERATED_BODY()

public:
	/**
	 * Copy an imported world into a content-only plugin named after it.
	 *
	 * SourceLevelPackageName is the level an import produced, e.g.
	 * /Game/Carla/Maps/Generated/Area. The plugin is written to the project's Plugins folder, and the
	 * world becomes addressable as /<WorldName>/Maps/<WorldName>.
	 *
	 * The world's settings assets travel with it, as does the road network, which is placed where the
	 * simulator already looks for a plugin-hosted map's network. Exporting again replaces what a
	 * previous export produced.
	 *
	 * This produces the plugin as editable content. Cooking it is a separate step, because it is part
	 * of building a package rather than of authoring a world.
	 */
	UFUNCTION(BlueprintCallable, Category = "Generated World")
	static FGeneratedLevelExportResult ExportLevelAsPlugin(
		const FString& SourceLevelPackageName,
		const FString& WorldName = TEXT(""));

	/** True when a plugin of this name already exists in the project. */
	UFUNCTION(BlueprintCallable, Category = "Generated World")
	static bool IsExportedAsPlugin(const FString& WorldName);
};
