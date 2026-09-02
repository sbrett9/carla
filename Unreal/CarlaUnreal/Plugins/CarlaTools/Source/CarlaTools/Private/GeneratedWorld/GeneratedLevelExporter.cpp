// Copyright (c) 2026 CARLA-Cesium digital-twin project.

#include "GeneratedWorld/GeneratedLevelExporter.h"

#include "CarlaTools.h"
#include "GeneratedWorld/GeoreferencedWorldSettings.h"

#include <util/ue-header-guard-begin.h>
#include "AssetRegistry/AssetRegistryModule.h"
#include "Components/StaticMeshComponent.h"
#include "EditorAssetLibrary.h"
#include "Engine/StaticMesh.h"
#include "Engine/StaticMeshActor.h"
#include "Engine/World.h"
#include "HAL/FileManager.h"
#include "Interfaces/IPluginManager.h"
#include "Misc/FileHelper.h"
#include "Misc/PackageName.h"
#include "UObject/Package.h"
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

	// Mounting makes the plugin's content addressable; it does not tell the asset registry what is
	// already in there. Without this scan a re-export sees an empty namespace, skips deleting what a
	// previous export left, and is then refused by a file it did not know existed -- so the first
	// export of a world succeeds and every one after it fails.
	FAssetRegistryModule& Registry =
		FModuleManager::LoadModuleChecked<FAssetRegistryModule>(TEXT("AssetRegistry"));
	Registry.Get().ScanPathsSynchronous({ FString::Printf(TEXT("/%s"), *Name) }, /*bForceRescan=*/true);

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
		// Required: a world without its road network loads and looks correct while offering no
		// waypoints, no traffic manager and no telemetry, which is worse than failing to export.
		{ SourceFolder / (ShortName + TEXT("_RoadNetwork")),
		  FString::Printf(TEXT("/%s/%s_RoadNetwork"), *Name, *Name), true },
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

		// Deleting a level does not free its name: DeleteAsset loads the World to delete it, tears it
		// down and removes the file, but leaves the UPackage resident, so the copy below is refused
		// with "an asset already exists" over a name nothing on disk holds. Move the residue aside,
		// which frees the name outright; garbage collection does not reliably reclaim it.
		if (UPackage* Stale = FindPackage(nullptr, *Item.Destination))
		{
			const FName Aside = MakeUniqueObjectName(
				nullptr, UPackage::StaticClass(), FName(*(Item.Destination + TEXT("_Replaced"))));
			Stale->Rename(*Aside.ToString(), nullptr,
				REN_DontCreateRedirectors | REN_NonTransactional | REN_DoNotDirty);
		}

		if (!UEditorAssetLibrary::DuplicateAsset(Item.Source, Item.Destination))
		{
			Result.FailureReason = FString::Printf(
				TEXT("could not copy %s into the plugin"), *Item.Source);
			return Result;
		}
		++Result.AssetsExported;
	}

	// Duplicating assets one at a time does not rewire the references between them: the copied
	// settings still name the originals under /Game. That reads as working here, where both exist,
	// and produces a plugin that is not self-contained -- shipped on its own it would resolve its
	// road network and its field to assets that are not present. Repoint them at the copies.
	{
		const FString SettingsCopy = FString::Printf(TEXT("/%s/%s_WorldSettings"), *Name, *Name);
		if (UGeoreferencedWorldSettings* Copied =
				Cast<UGeoreferencedWorldSettings>(UEditorAssetLibrary::LoadAsset(SettingsCopy)))
		{
			const FString NetworkCopy = FString::Printf(TEXT("/%s/%s_RoadNetwork"), *Name, *Name);
			if (URoadNetworkAsset* Network =
					Cast<URoadNetworkAsset>(UEditorAssetLibrary::LoadAsset(NetworkCopy)))
			{
				Copied->RoadNetwork = Network;
			}

			const FString FieldCopy = FString::Printf(TEXT("/%s/%s_BareEarthField"), *Name, *Name);
			if (UEditorAssetLibrary::DoesAssetExist(FieldCopy))
			{
				Copied->OffsetField = TSoftObjectPtr<UBareEarthOffsetField>(FSoftObjectPath(FieldCopy));
			}

			UEditorAssetLibrary::SaveAsset(SettingsCopy, false);
		}
	}

	// Bring the road surface into the plugin as well, so the world is a complete unit rather than a
	// level pointing at meshes elsewhere. Shipped on its own, a plugin whose actors still name
	// /Game/Carla/Static/Road would load with no visible road; and a world cannot be cooked as DLC at
	// all while its geometry sits outside the plugin, because the cooker suppresses any package
	// outside the DLC's own content and errors once per asset.
	//
	// The semantic label survives the move because ATagger reads a POSITION in the path, not a
	// prefix: it splits on '/' and takes token 4, so /<World>/Carla/Static/Road/<Map>/SM_x gives
	// "Road" exactly as /Game/Carla/Static/Road/<Map>/SM_x does. The two folder levels between the
	// mount root and the lane-type folder are load-bearing for that reason and must not be flattened.
	{
		// Re-exporting: clear the previous copy in one pass. Deleting assets one at a time walks the
		// whole UObject graph per call, so 870 deletes cost far more than one directory delete does.
		const FString MeshRoot = FString::Printf(TEXT("/%s/Carla"), *Name);
		if (UEditorAssetLibrary::DoesDirectoryExist(MeshRoot))
		{
			UEditorAssetLibrary::DeleteDirectory(MeshRoot);
		}

		UWorld* CopiedWorld = Cast<UWorld>(UEditorAssetLibrary::LoadAsset(Result.LevelPackageName));
		if (!CopiedWorld || !CopiedWorld->PersistentLevel)
		{
			Result.FailureReason = TEXT("could not open the copied level to bring its road surface across");
			return Result;
		}

		// Keyed on the source package so a mesh shared by several actors is copied once.
		TMap<FString, UStaticMesh*> Copied;
		int32 Repointed = 0;
		for (AActor* Actor : CopiedWorld->PersistentLevel->Actors)
		{
			AStaticMeshActor* MeshActor = Cast<AStaticMeshActor>(Actor);
			if (!IsValid(MeshActor)) continue;
			UStaticMeshComponent* Component = MeshActor->GetStaticMeshComponent();
			if (!Component) continue;
			UStaticMesh* Mesh = Component->GetStaticMesh();
			if (!Mesh) continue;

			// Driven off what the level actually references rather than off a folder listing: this
			// copies exactly the meshes in use, whatever root they were baked under, and leaves
			// nothing orphaned behind. Anything already inside the plugin is left alone.
			const FString Source = Mesh->GetOutermost()->GetName();
			if (!Source.StartsWith(TEXT("/Game/"))) continue;

			UStaticMesh** Already = Copied.Find(Source);
			if (!Already)
			{
				const FString Destination = FString::Printf(
					TEXT("/%s/%s"), *Name, *Source.RightChop(FString(TEXT("/Game/")).Len()));
				UObject* Duplicate = UEditorAssetLibrary::DuplicateAsset(Source, Destination);
				UStaticMesh* DuplicatedMesh = Cast<UStaticMesh>(Duplicate);
				if (!DuplicatedMesh)
				{
					Result.FailureReason = FString::Printf(
						TEXT("could not copy the road surface mesh %s into the plugin"), *Source);
					return Result;
				}
				Already = &Copied.Add(Source, DuplicatedMesh);
				++Result.AssetsExported;
			}

			Component->SetStaticMesh(*Already);
			++Repointed;
		}

		if (Copied.Num() > 0)
		{
			// The level now names the copies, so it has to be written back; without this the plugin
			// holds the meshes and the level still points outside it.
			UEditorAssetLibrary::SaveAsset(Result.LevelPackageName, false);
			UE_LOG(LogCarlaTools, Display,
				TEXT("[GeneratedLevelExporter] brought %d road surface mesh(es) into %s, "
					 "repointing %d actor(s)"),
				Copied.Num(), *Name, Repointed);
		}
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
