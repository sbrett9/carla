// Copyright (c) 2026 CARLA-Cesium digital-twin project.

#include "GeneratedWorld/GeneratedLevelExporter.h"

#include "CarlaTools.h"

#include <util/ue-header-guard-begin.h>
#include "EditorAssetLibrary.h"
#include "HAL/FileManager.h"
#include "Interfaces/IPluginManager.h"
#include "Misc/FileHelper.h"
#include "Misc/PackageName.h"
#include "Misc/Paths.h"
#include <util/ue-header-guard-end.h>

namespace
{

/**
 * A content-only plugin holding one world.
 *
 * ExplicitlyLoaded keeps the engine from mounting it during startup with everything else: a world is
 * loaded on demand, and a package may hold many of them. CanContainContent is what gives it a mount
 * root. There are no modules, so nothing has to be compiled to add a world to a build.
 */
FString PluginDescriptor(const FString& WorldName)
{
	return FString::Printf(TEXT(
		"{\n"
		"\t\"FileVersion\": 3,\n"
		"\t\"Version\": 1,\n"
		"\t\"VersionName\": \"1.0\",\n"
		"\t\"FriendlyName\": \"%s\",\n"
		"\t\"Description\": \"A generated world, exported as content so it can be added to a build.\",\n"
		"\t\"Category\": \"Generated Worlds\",\n"
		"\t\"CreatedBy\": \"CARLA world package importer\",\n"
		"\t\"CanContainContent\": true,\n"
		"\t\"IsBetaVersion\": false,\n"
		"\t\"Installed\": false,\n"
		"\t\"ExplicitlyLoaded\": true,\n"
		"\t\"NoCode\": true,\n"
		"\t\"Modules\": []\n"
		"}\n"), *WorldName);
}

/** Where the simulator looks for a plugin-hosted map's road network. */
FString RoadNetworkDestination(const FString& PluginDir, const FString& WorldName)
{
	return PluginDir / TEXT("Content") / TEXT("Maps") / TEXT("OpenDrive") / (WorldName + TEXT(".xodr"));
}

/** The road network an import placed beside the level it produced. */
FString RoadNetworkSource(const FString& SourceLevelPackageName, const FString& WorldName)
{
	const FString Folder = FPackageName::GetLongPackagePath(SourceLevelPackageName);
	const FString ContentRelative = Folder.RightChop(FString(TEXT("/Game/")).Len());
	return FPaths::ConvertRelativePathToFull(FPaths::ProjectContentDir())
		/ ContentRelative / TEXT("OpenDrive") / (WorldName + TEXT(".xodr"));
}

} // namespace

bool UGeneratedLevelExporter::IsExportedAsPlugin(const FString& WorldName)
{
	const FString Descriptor = FPaths::ConvertRelativePathToFull(FPaths::ProjectPluginsDir())
		/ TEXT("GeneratedWorlds") / WorldName / (WorldName + TEXT(".uplugin"));
	return FPaths::FileExists(Descriptor);
}

FGeneratedLevelExportResult UGeneratedLevelExporter::ExportLevelAsPlugin(
	const FString& SourceLevelPackageName,
	const FString& WorldName)
{
	FGeneratedLevelExportResult Result;

	const FString Name = WorldName.IsEmpty()
		? FPackageName::GetShortName(SourceLevelPackageName)
		: WorldName;
	if (Name.IsEmpty())
	{
		Result.FailureReason = TEXT("no world name to export under");
		return Result;
	}
	if (!UEditorAssetLibrary::DoesAssetExist(SourceLevelPackageName))
	{
		Result.FailureReason = FString::Printf(
			TEXT("there is no level at %s to export"), *SourceLevelPackageName);
		return Result;
	}

	// Grouped under one folder so generated worlds stay separable from the plugins the project is
	// built from, and can be excluded from source control as a body. Plugin discovery scans
	// recursively, and a plugin's mount root comes from its NAME rather than its location, so nesting
	// changes nothing about how the world is addressed: it is still /<Name>/Maps/<Name>.
	const FString PluginDir = FPaths::ConvertRelativePathToFull(FPaths::ProjectPluginsDir())
		/ TEXT("GeneratedWorlds") / Name;
	const FString DescriptorPath = PluginDir / (Name + TEXT(".uplugin"));
	Result.PluginDirectory = PluginDir;

	// The descriptor has to exist before the plugin can be mounted, and it has to be mounted before
	// anything can be written into its content root.
	if (!FFileHelper::SaveStringToFile(PluginDescriptor(Name), *DescriptorPath))
	{
		Result.FailureReason = FString::Printf(TEXT("could not write %s"), *DescriptorPath);
		return Result;
	}
	IFileManager::Get().MakeDirectory(*(PluginDir / TEXT("Content") / TEXT("Maps")), true);

	IPluginManager& Plugins = IPluginManager::Get();
	if (!Plugins.FindPlugin(Name))
	{
		FText FailReason;
		if (!Plugins.AddToPluginsList(DescriptorPath, &FailReason))
		{
			Result.FailureReason = FString::Printf(
				TEXT("could not register the plugin: %s"), *FailReason.ToString());
			return Result;
		}
		Plugins.MountNewlyCreatedPlugin(Name);
	}
	if (!FPackageName::MountPointExists(FString::Printf(TEXT("/%s/"), *Name)))
	{
		Result.FailureReason = FString::Printf(
			TEXT("the plugin %s has no mount root, so nothing can be written into it"), *Name);
		return Result;
	}

	// Copy the level and everything it needs into the plugin's own namespace. Duplicating rather than
	// moving leaves the imported world in place to keep editing; the copy is what ships.
	const FString SourceFolder = FPackageName::GetLongPackagePath(SourceLevelPackageName);
	const FString ShortName = FPackageName::GetShortName(SourceLevelPackageName);
	Result.LevelPackageName = FString::Printf(TEXT("/%s/Maps/%s"), *Name, *Name);

	struct FExportItem { FString Source; FString Destination; bool bRequired; };
	const TArray<FExportItem> Items = {
		{ SourceLevelPackageName, Result.LevelPackageName, true },
		{ SourceFolder / (ShortName + TEXT("_WorldSettings")),
		  FString::Printf(TEXT("/%s/%s_WorldSettings"), *Name, *Name), true },
		{ SourceFolder / (ShortName + TEXT("_BareEarthField")),
		  FString::Printf(TEXT("/%s/%s_BareEarthField"), *Name, *Name), false },
	};

	for (const FExportItem& Item : Items)
	{
		if (!UEditorAssetLibrary::DoesAssetExist(Item.Source))
		{
			if (Item.bRequired)
			{
				Result.FailureReason = FString::Printf(
					TEXT("the world is missing %s, which it cannot be loaded without"), *Item.Source);
				return Result;
			}
			// A world reconciled by a constant shift carries no per-cell field, and that is not a fault.
			continue;
		}
		if (UEditorAssetLibrary::DoesAssetExist(Item.Destination))
		{
			UEditorAssetLibrary::DeleteAsset(Item.Destination);
		}
		if (!UEditorAssetLibrary::DuplicateAsset(Item.Source, Item.Destination))
		{
			Result.FailureReason = FString::Printf(
				TEXT("could not copy %s into the plugin"), *Item.Source);
			return Result;
		}
		++Result.AssetsExported;
	}

	// The road network is a loose file the simulator reads from disk, so it is copied rather than
	// duplicated as an asset. Without it the world loads its geometry and has no road graph.
	const FString RoadSource = RoadNetworkSource(SourceLevelPackageName, ShortName);
	const FString RoadDestination = RoadNetworkDestination(PluginDir, Name);
	if (FPaths::FileExists(RoadSource))
	{
		if (IFileManager::Get().Copy(*RoadDestination, *RoadSource, true, true) != COPY_OK)
		{
			Result.FailureReason = FString::Printf(
				TEXT("could not place the road network at %s"), *RoadDestination);
			return Result;
		}
	}
	else
	{
		Result.FailureReason = FString::Printf(
			TEXT("no road network at %s; the exported world would have no road graph"), *RoadSource);
		return Result;
	}

	if (!UEditorAssetLibrary::SaveDirectory(FString::Printf(TEXT("/%s"), *Name), false, true))
	{
		UE_LOG(LogCarlaTools, Warning,
			TEXT("[GeneratedLevelExporter] some assets under /%s did not save."), *Name);
	}

	Result.bSucceeded = true;
	UE_LOG(LogCarlaTools, Display,
		TEXT("[GeneratedLevelExporter] exported %s as %s (%d asset(s)). Load it with -map=%s"),
		*SourceLevelPackageName, *DescriptorPath, Result.AssetsExported, *Result.LevelPackageName);
	return Result;
}
