// Copyright (c) 2026 CARLA-Cesium digital-twin project.

#include "GeneratedWorld/WorldPackageImporter.h"

#include "CarlaTools.h"
#include "GeneratedWorld/GeoreferencedWorldInitializer.h"
#include "GeneratedWorld/GeoreferencedWorldSettings.h"

#include <util/ue-header-guard-begin.h>
#include "AssetRegistry/AssetRegistryModule.h"
#include "Editor.h"
#include "EditorAssetLibrary.h"
#include "EditorScriptingHelpers.h"
#include "Engine/World.h"
#include "EngineUtils.h"
#include "FileHelpers.h"
#include "HAL/FileManager.h"
#include "LevelEditorSubsystem.h"
#include "Misc/FileHelper.h"
#include "Misc/PackageName.h"
#include "Misc/Paths.h"
#include "Serialization/MemoryReader.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "UObject/Package.h"
#include "UObject/SavePackage.h"
#include <util/ue-header-guard-end.h>

namespace
{
	/** Magic at the head of a world package's per-cell field file ("CWP1"). */
	constexpr int32 BareEarthGridMagic = 0x43575031;

	/** The level a generated world is cloned from: a road generator, a player start and lighting. */
	const TCHAR* GeneratedWorldTemplate = TEXT("/Game/Carla/Maps/OpenDriveMap");

	FString ManifestFile(const FString& Dir, const FString& MapName)
	{
		return FPaths::Combine(Dir, MapName + TEXT(".world.json"));
	}

	FString OpenDriveFile(const FString& Dir, const FString& MapName)
	{
		return FPaths::Combine(Dir, MapName + TEXT(".xodr"));
	}

	FString GridFile(const FString& Dir, const FString& MapName)
	{
		return FPaths::Combine(Dir, MapName + TEXT(".bareearth.bin"));
	}

	/** Read a number that the manifest may legitimately omit, leaving the default in place. */
	double NumberOr(const TSharedPtr<FJsonObject>& Json, const TCHAR* Field, double Fallback)
	{
		double Value = Fallback;
		return Json->TryGetNumberField(Field, Value) ? Value : Fallback;
	}

	bool BoolOr(const TSharedPtr<FJsonObject>& Json, const TCHAR* Field, bool Fallback)
	{
		bool Value = Fallback;
		return Json->TryGetBoolField(Field, Value) ? Value : Fallback;
	}

	FString StringOr(const TSharedPtr<FJsonObject>& Json, const TCHAR* Field)
	{
		FString Value;
		return Json->TryGetStringField(Field, Value) ? Value : FString();
	}

	/** Create (or replace) an asset of the given class at a long package path, ready to be filled. */
	template <typename TAsset>
	TAsset* CreateAsset(const FString& PackageName, const FString& AssetName)
	{
		UPackage* Package = CreatePackage(*PackageName);
		if (!Package)
		{
			return nullptr;
		}
		Package->FullyLoad();
		// Retarget any existing object of this name so re-importing replaces rather than collides.
		if (UObject* Existing = StaticFindObject(nullptr, Package, *AssetName))
		{
			Existing->Rename(nullptr, GetTransientPackage(),
				REN_DontCreateRedirectors | REN_DoNotDirty | REN_NonTransactional);
		}
		return NewObject<TAsset>(Package, TAsset::StaticClass(), *AssetName,
			RF_Public | RF_Standalone);
	}

	/** Register a freshly created asset and write it to disk. */
	bool SaveAsset(UObject* Asset)
	{
		if (!Asset)
		{
			return false;
		}
		UPackage* Package = Asset->GetOutermost();
		FAssetRegistryModule::AssetCreated(Asset);
		Package->MarkPackageDirty();

		const FString FileName = FPackageName::LongPackageNameToFilename(
			Package->GetName(), FPackageName::GetAssetPackageExtension());
		FSavePackageArgs SaveArgs;
		SaveArgs.TopLevelFlags = RF_Public | RF_Standalone;
		SaveArgs.SaveFlags = SAVE_NoError;
		SaveArgs.Error = GError;
		return UPackage::SavePackage(Package, Asset, *FileName, SaveArgs);
	}
}

bool UWorldPackageImporter::IsWorldPackagePresent(const FString& PackageDirectory, const FString& MapName)
{
	return FPaths::FileExists(ManifestFile(PackageDirectory, MapName));
}

TArray<FString> UWorldPackageImporter::ListWorldPackages(const FString& PackageDirectory)
{
	TArray<FString> Found;
	IFileManager::Get().FindFiles(Found, *FPaths::Combine(PackageDirectory, TEXT("*.world.json")), true, false);
	TArray<FString> Names;
	Names.Reserve(Found.Num());
	for (const FString& File : Found)
	{
		Names.Add(File.LeftChop(FString(TEXT(".world.json")).Len()));
	}
	Names.Sort();
	return Names;
}

UGeoreferencedWorldSettings* UWorldPackageImporter::CreateWorldSettingsAssets(
	const FString& PackageDirectory,
	const FString& MapName,
	const FString& DestinationFolder,
	FString& OutFailureReason)
{
	OutFailureReason.Empty();
	if (!EditorScriptingHelpers::CheckIfInEditorAndPIE())
	{
		OutFailureReason = TEXT("this can only run in the editor, and not while a play session is active");
		return nullptr;
	}

	FString ManifestText;
	if (!FFileHelper::LoadFileToString(ManifestText, *ManifestFile(PackageDirectory, MapName)))
	{
		OutFailureReason = FString::Printf(
			TEXT("no world manifest at %s"), *ManifestFile(PackageDirectory, MapName));
		return nullptr;
	}

	TSharedPtr<FJsonObject> Json;
	TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(ManifestText);
	if (!FJsonSerializer::Deserialize(Reader, Json) || !Json.IsValid())
	{
		OutFailureReason = TEXT("the world manifest is not readable JSON");
		return nullptr;
	}

	// A world placed at latitude/longitude zero looks plausible and reports nonsense, so an absent
	// datum is refused here rather than baked into an asset.
	const double OriginLatitude = NumberOr(Json, TEXT("OriginLatitude"), 0.0);
	const double OriginLongitude = NumberOr(Json, TEXT("OriginLongitude"), 0.0);
	const double OriginHeight = NumberOr(Json, TEXT("OriginHeightMeters"), 0.0);
	if (OriginLatitude == 0.0 && OriginLongitude == 0.0 && OriginHeight == 0.0)
	{
		OutFailureReason = TEXT("the world manifest carries no origin");
		return nullptr;
	}

	const bool bDrapeActive = BoolOr(Json, TEXT("DrapeActive"), false);
	const int32 NumCols = static_cast<int32>(NumberOr(Json, TEXT("GridNumCols"), 0.0));
	const int32 NumRows = static_cast<int32>(NumberOr(Json, TEXT("GridNumRows"), 0.0));

	// The per-cell fields, when this world has them.
	UBareEarthOffsetField* Field = nullptr;
	if (bDrapeActive)
	{
		TArray<uint8> Blob;
		if (!FFileHelper::LoadFileToArray(Blob, *GridFile(PackageDirectory, MapName)))
		{
			OutFailureReason = FString::Printf(
				TEXT("this world was reconciled point by point but its field file is missing: %s"),
				*GridFile(PackageDirectory, MapName));
			return nullptr;
		}

		const int32 Count = NumCols * NumRows;
		const int64 Header = sizeof(int32) + 6 * sizeof(double) + 2 * sizeof(int32);
		const int64 Expected = Header + static_cast<int64>(Count) * 2 * sizeof(float);
		if (Blob.Num() != Expected)
		{
			OutFailureReason = FString::Printf(
				TEXT("field file is %lld bytes, expected %lld for a %dx%d grid"),
				static_cast<int64>(Blob.Num()), Expected, NumCols, NumRows);
			return nullptr;
		}

		FMemoryReader Ar(Blob);
		int32 Magic = 0;
		Ar << Magic;
		if (Magic != BareEarthGridMagic)
		{
			OutFailureReason = TEXT("field file does not carry the world-package marker");
			return nullptr;
		}
		double Ignored = 0.0;
		for (int32 i = 0; i < 3; ++i) { Ar << Ignored; }   // origin, restated for self-description
		double MinX = 0.0, MinY = 0.0, CellSize = 0.0;
		Ar << MinX; Ar << MinY; Ar << CellSize;
		int32 FileCols = 0, FileRows = 0;
		Ar << FileCols; Ar << FileRows;
		if (FileCols != NumCols || FileRows != NumRows)
		{
			OutFailureReason = FString::Printf(
				TEXT("field file says %dx%d but the manifest says %dx%d"),
				FileCols, FileRows, NumCols, NumRows);
			return nullptr;
		}

		const FString FieldName = MapName + TEXT("_BareEarthField");
		Field = CreateAsset<UBareEarthOffsetField>(
			DestinationFolder / FieldName, FieldName);
		if (!Field)
		{
			OutFailureReason = TEXT("could not create the per-cell field asset");
			return nullptr;
		}
		Field->MinXMeters = MinX;
		Field->MinYMeters = MinY;
		Field->CellSizeMeters = CellSize;
		Field->NumCols = NumCols;
		Field->NumRows = NumRows;
		Field->OffsetMeters.SetNumUninitialized(Count);
		Field->BareEarthDtmMeters.SetNumUninitialized(Count);
		for (int32 i = 0; i < Count; ++i) { Ar << Field->OffsetMeters[i]; }
		for (int32 i = 0; i < Count; ++i) { Ar << Field->BareEarthDtmMeters[i]; }

		if (!Field->IsWellFormed())
		{
			OutFailureReason = TEXT("the per-cell field read back malformed");
			return nullptr;
		}
		SaveAsset(Field);
	}

	const FString SettingsName = MapName + TEXT("_WorldSettings");
	UGeoreferencedWorldSettings* Settings =
		CreateAsset<UGeoreferencedWorldSettings>(DestinationFolder / SettingsName, SettingsName);
	if (!Settings)
	{
		OutFailureReason = TEXT("could not create the world settings asset");
		return nullptr;
	}

	Settings->OriginLatitude = OriginLatitude;
	Settings->OriginLongitude = OriginLongitude;
	Settings->OriginHeightMeters = OriginHeight;
	Settings->GeoReferenceString = StringOr(Json, TEXT("GeoReferenceString"));
	Settings->HeightAlignMode = StringOr(Json, TEXT("HeightAlignMode"));
	Settings->bDrapeActive = bDrapeActive;
	Settings->HeightAlignOffsetMeters = NumberOr(Json, TEXT("HeightAlignOffsetMeters"), 0.0);
	Settings->OffsetField = Field;
	Settings->PhotorealIonAssetId = static_cast<int64>(NumberOr(Json, TEXT("PhotorealIonAssetId"), 0.0));
	Settings->GroundIonAssetId = static_cast<int64>(NumberOr(Json, TEXT("GroundIonAssetId"), 0.0));
	Settings->StagingMinXMeters = NumberOr(Json, TEXT("StagingMinXMeters"), 0.0);
	Settings->StagingMinYMeters = NumberOr(Json, TEXT("StagingMinYMeters"), 0.0);
	Settings->StagingMaxXMeters = NumberOr(Json, TEXT("StagingMaxXMeters"), 0.0);
	Settings->StagingMaxYMeters = NumberOr(Json, TEXT("StagingMaxYMeters"), 0.0);
	Settings->StagingMarginMeters = NumberOr(Json, TEXT("StagingMarginMeters"), 0.0);
	Settings->SourceOsmFileName = StringOr(Json, TEXT("SourceOsmFileName"));
	Settings->SourceOsmSha256 = StringOr(Json, TEXT("SourceOsmSha256"));
	Settings->OpenDriveSha256 = StringOr(Json, TEXT("OpenDriveSha256"));
	Settings->GeneratedAtUtc = StringOr(Json, TEXT("GeneratedAtUtc"));
	Settings->GeneratorVersion = StringOr(Json, TEXT("GeneratorVersion"));

	// The layers the runtime has to arrange. The collidable bare-earth layer carries the constant
	// shift when there is one, and gives up its collision to the heightfield when there is a field.
	Settings->Layers.Reset();
	FGeoreferencedWorldLayer Photoreal;
	Photoreal.Tag = TEXT("photoreal");
	Photoreal.bVisible = true;
	Photoreal.bCollision = false;
	Settings->Layers.Add(Photoreal);
	if (Settings->GroundIonAssetId > 0)
	{
		FGeoreferencedWorldLayer Ground;
		Ground.Tag = TEXT("ground");
		Ground.bVisible = false;
		Ground.bCollision = !bDrapeActive;
		Ground.VerticalOffsetMeters = bDrapeActive ? 0.0 : Settings->HeightAlignOffsetMeters;
		Settings->Layers.Add(Ground);
	}

	if (!SaveAsset(Settings))
	{
		OutFailureReason = TEXT("could not save the world settings asset");
		return nullptr;
	}
	return Settings;
}

FWorldPackageImportResult UWorldPackageImporter::ImportWorldPackage(
	const FString& PackageDirectory,
	const FString& MapName,
	const FString& DestinationFolder)
{
	FWorldPackageImportResult Result;

	UGeoreferencedWorldSettings* Settings =
		CreateWorldSettingsAssets(PackageDirectory, MapName, DestinationFolder, Result.FailureReason);
	if (!Settings)
	{
		return Result;
	}
	Result.SettingsPackageName = Settings->GetOutermost()->GetName();
	if (UBareEarthOffsetField* Field = Settings->OffsetField.LoadSynchronous())
	{
		Result.OffsetFieldPackageName = Field->GetOutermost()->GetName();
	}

	// Clone the level a generated world normally runs in, rather than starting from an empty map:
	// it already carries the road generator, a player start and lighting, and deliberately carries
	// no large-map manager, whose presence would switch on origin rebasing and strand the sandbox.
	const FString LevelPackageName = DestinationFolder / MapName;

	// Refuse to rewrite the level the editor currently has open. Replacing a world out from under
	// the editor tears down everything that lives in it, including whatever invoked this.
	if (const UWorld* OpenWorld = GEditor ? GEditor->GetEditorWorldContext().World() : nullptr)
	{
		if (OpenWorld->GetOutermost()->GetName() == LevelPackageName)
		{
			Result.FailureReason = FString::Printf(
				TEXT("%s is the level currently open; open a different level and import again"),
				*LevelPackageName);
			return Result;
		}
	}

	if (UEditorAssetLibrary::DoesAssetExist(LevelPackageName))
	{
		UEditorAssetLibrary::DeleteAsset(LevelPackageName);
	}
	if (!UEditorAssetLibrary::DuplicateAsset(GeneratedWorldTemplate, LevelPackageName))
	{
		Result.FailureReason = FString::Printf(
			TEXT("could not clone %s into %s"), GeneratedWorldTemplate, *LevelPackageName);
		return Result;
	}

	// Edit the cloned level as an asset rather than opening it. Opening it would swap the editor's
	// current world, which destroys everything owned by the outgoing one -- including the panel that
	// started the import -- and the engine treats the resulting dangling references as fatal. The
	// asset-cooking commandlets in this project populate levels the same way, without opening them.
	UWorld* World = Cast<UWorld>(UEditorAssetLibrary::LoadAsset(LevelPackageName));
	if (!World || !World->PersistentLevel)
	{
		Result.FailureReason = FString::Printf(TEXT("could not load %s"), *LevelPackageName);
		return Result;
	}
	UPackage* LevelPackage = World->GetOutermost();
	LevelPackage->FullyLoad();

	// One initializer per level: replace any the template or a previous import left behind. The
	// level's own actor list is used rather than an actor iterator, which expects a world the engine
	// has initialised for play or editing.
	for (int32 Index = World->PersistentLevel->Actors.Num() - 1; Index >= 0; --Index)
	{
		AActor* Existing = World->PersistentLevel->Actors[Index];
		if (IsValid(Existing) && Existing->IsA<AGeoreferencedWorldInitializer>())
		{
			Existing->Destroy();
		}
	}
	FActorSpawnParameters SpawnParams;
	SpawnParams.SpawnCollisionHandlingOverride = ESpawnActorCollisionHandlingMethod::AlwaysSpawn;
	AGeoreferencedWorldInitializer* Initializer =
		World->SpawnActor<AGeoreferencedWorldInitializer>(
			AGeoreferencedWorldInitializer::StaticClass(), FTransform::Identity, SpawnParams);
	if (!Initializer)
	{
		Result.FailureReason = TEXT("could not place the world initializer");
		return Result;
	}
	Initializer->Settings = Settings;
	Initializer->SetActorLabel(TEXT("GeneratedWorldInitializer"));

	// The road network goes where the runtime looks for it: an OpenDrive folder beside the level,
	// named after the level.
	const FString MapContentDir = FPaths::ConvertRelativePathToFull(FPaths::ProjectContentDir())
		/ DestinationFolder.RightChop(FString(TEXT("/Game/")).Len());
	Result.OpenDriveFilePath = MapContentDir / TEXT("OpenDrive") / (MapName + TEXT(".xodr"));
	if (IFileManager::Get().Copy(*Result.OpenDriveFilePath,
			*OpenDriveFile(PackageDirectory, MapName), true, true) != COPY_OK)
	{
		Result.FailureReason = FString::Printf(
			TEXT("could not place the road network at %s"), *Result.OpenDriveFilePath);
		return Result;
	}

	// Save the level package directly. SaveCurrentLevel would act on whatever the editor has open,
	// which is deliberately not this level.
	LevelPackage->MarkPackageDirty();
	const FString LevelFileName = FPackageName::LongPackageNameToFilename(
		LevelPackageName, FPackageName::GetMapPackageExtension());
	FSavePackageArgs LevelSaveArgs;
	LevelSaveArgs.TopLevelFlags = RF_Public | RF_Standalone;
	LevelSaveArgs.SaveFlags = SAVE_NoError;
	LevelSaveArgs.Error = GError;
	if (!UPackage::SavePackage(LevelPackage, World, *LevelFileName, LevelSaveArgs))
	{
		Result.FailureReason = FString::Printf(TEXT("could not save %s"), *LevelFileName);
		return Result;
	}

	Result.LevelPackageName = LevelPackageName;
	Result.bSucceeded = true;
	UE_LOG(LogCarlaTools, Display,
		TEXT("[WorldPackageImporter] imported %s -> %s (settings %s, field %s)"),
		*MapName, *Result.LevelPackageName, *Result.SettingsPackageName,
		Result.OffsetFieldPackageName.IsEmpty() ? TEXT("none") : *Result.OffsetFieldPackageName);
	return Result;
}
