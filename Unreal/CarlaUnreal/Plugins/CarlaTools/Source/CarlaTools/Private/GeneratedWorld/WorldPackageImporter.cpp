// Copyright (c) 2026 CARLA-Cesium digital-twin project.

#include "GeneratedWorld/WorldPackageImporter.h"

#include "CarlaTools.h"
#include "GeneratedWorld/GeoreferencedWorldInitializer.h"
#include "Carla/OpenDrive/OpenDriveGenerator.h"
#include "GeneratedWorld/GeoreferencedWorldSettings.h"
#include "GeneratedWorld/RoadSurfaceBaker.h"
#include "CesiumHeightSampler.h"

#include <util/ue-header-guard-begin.h>
#include "AssetRegistry/AssetRegistryModule.h"
#include "Editor.h"
#include "EditorAssetLibrary.h"
#include "FileUtilities/ZipArchiveReader.h"
#include "EditorScriptingHelpers.h"
#include "Engine/World.h"
#include "Engine/PostProcessVolume.h"
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

	// A world package is one file: a zip holding the manifest, the road network and, when the world
	// was reconciled cell by cell, the grids. The entries are named for their role rather than for
	// the world, since the file itself already carries the name.
	const TCHAR* const ManifestEntry = TEXT("world.json");
	const TCHAR* const OpenDriveEntry = TEXT("map.xodr");
	const TCHAR* const GridEntry = TEXT("bareearth.bin");
	const TCHAR* const PackageExtension = TEXT(".cwp");

	/** The package a world of this name occupies inside a directory. */
	FString PackageFile(const FString& Dir, const FString& MapName)
	{
		return FPaths::Combine(Dir, MapName + PackageExtension);
	}

	/**
	 * Open a package for reading, or return null with a reason.
	 *
	 * The archive is deliberately uncompressed: FZipArchiveReader reads stored entries only, and the
	 * writer stores rather than deflates for exactly this reason.
	 */
	TUniquePtr<FZipArchiveReader> OpenPackage(const FString& PackagePath, FString& OutError)
	{
		if (!FPaths::FileExists(PackagePath))
		{
			OutError = FString::Printf(TEXT("no world package at %s"), *PackagePath);
			return nullptr;
		}
		IFileHandle* Handle = FPlatformFileManager::Get().GetPlatformFile().OpenRead(*PackagePath);
		if (!Handle)
		{
			OutError = FString::Printf(TEXT("could not open %s"), *PackagePath);
			return nullptr;
		}
		// The reader takes ownership of the handle, including when the archive turns out to be corrupt.
		TUniquePtr<FZipArchiveReader> Reader = MakeUnique<FZipArchiveReader>(Handle);
		if (!Reader->IsValid())
		{
			OutError = FString::Printf(TEXT("%s is not a readable world package"), *PackagePath);
			return nullptr;
		}
		return Reader;
	}

	/** Read one entry as text. */
	bool ReadPackageText(const FZipArchiveReader& Reader, const TCHAR* Entry, FString& OutText)
	{
		TArray<uint8> Bytes;
		if (!Reader.TryReadFile(Entry, Bytes))
		{
			return false;
		}
		FFileHelper::BufferToString(OutText, Bytes.GetData(), Bytes.Num());
		return true;
	}

	/** Load and parse a world manifest. Returns null and fills OutError when it cannot be read. */
	TSharedPtr<FJsonObject> LoadManifestJson(const FZipArchiveReader& Package, FString& OutError)
	{
		FString Text;
		if (!ReadPackageText(Package, ManifestEntry, Text))
		{
			OutError = TEXT("the world package carries no manifest");
			return nullptr;
		}
		TSharedPtr<FJsonObject> Json;
		TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Text);
		if (!FJsonSerializer::Deserialize(Reader, Json) || !Json.IsValid())
		{
			OutError = TEXT("the world manifest is not readable JSON");
			return nullptr;
		}
		return Json;
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
	return FPaths::FileExists(PackageFile(PackageDirectory, MapName));
}

TArray<FString> UWorldPackageImporter::ListWorldPackages(const FString& PackageDirectory)
{
	TArray<FString> Found;
	IFileManager::Get().FindFiles(
		Found, *FPaths::Combine(PackageDirectory, FString(TEXT("*")) + PackageExtension), true, false);
	TArray<FString> Names;
	Names.Reserve(Found.Num());
	for (const FString& File : Found)
	{
		Names.Add(FPaths::GetBaseFilename(File));
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

	TUniquePtr<FZipArchiveReader> Package =
		OpenPackage(PackageFile(PackageDirectory, MapName), OutFailureReason);
	if (!Package)
	{
		return nullptr;
	}

	TSharedPtr<FJsonObject> Json = LoadManifestJson(*Package, OutFailureReason);
	if (!Json.IsValid())
	{
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
		if (!Package->TryReadFile(GridEntry, Blob))
		{
			OutFailureReason = TEXT(
				"this world was reconciled point by point but the package carries no field");
			return nullptr;
		}

		const int32 Count = NumCols * NumRows;
		const int64 Header = sizeof(int32) + 6 * sizeof(double) + 2 * sizeof(int32);
		const int64 Expected = Header + static_cast<int64>(Count) * 2 * sizeof(float);
		if (Blob.Num() != Expected)
		{
			OutFailureReason = FString::Printf(
				TEXT("the field is %lld bytes, expected %lld for a %dx%d grid"),
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

FString UWorldPackageImporter::DescribeExistingImport(
	const FString& MapName, const FString& DestinationFolder)
{
	const FString SettingsPath = DestinationFolder / (MapName + TEXT("_WorldSettings"));
	if (!UEditorAssetLibrary::DoesAssetExist(DestinationFolder / MapName)
		|| !UEditorAssetLibrary::DoesAssetExist(SettingsPath))
	{
		return FString();
	}
	const UGeoreferencedWorldSettings* Existing =
		Cast<UGeoreferencedWorldSettings>(UEditorAssetLibrary::LoadAsset(SettingsPath));
	if (!Existing)
	{
		return FString();
	}
	return Existing->SourceOsmFileName.IsEmpty()
		? FString(TEXT("an earlier import of unrecorded origin"))
		: Existing->SourceOsmFileName;
}

namespace
{

/**
 * Establish the globe as saved level content, rather than leaving it to be rebuilt at play time.
 *
 * The world initializer performs the same configuration at BeginPlay, which is what lets a level
 * loaded by name stand up with no client. That is too late to edit against: opening the level in the
 * editor would show baked road geometry floating in empty space, because the georeference, the
 * imagery layers and the lighting would not exist until the level was played. Running the same
 * configuration here makes those actors part of the level, so the editor streams the world as soon
 * as the level is opened and a person can select and adjust the layers like any other content.
 *
 * The configuration is idempotent, so the initializer re-running it at BeginPlay is harmless and
 * remains the authority for a packaged run.
 */
void ConfigureGlobeAsLevelContent(
	UWorld* World, const UGeoreferencedWorldSettings* Settings, const FString& IonAccessToken)
{
	if (!World || !Settings || !Settings->HasUsableDatum())
	{
		return;
	}

	// An ion access token given here is written onto the tileset actors and saved into the level, so
	// it travels with the level -- into source control, and into anything built or shared from it.
	// That is worth doing on purpose, because it also makes the token editable afterwards: each
	// tileset exposes its own Ion Access Token, so a person can change it by selecting the layer in
	// the level. Left empty, nothing is written and the existing fallbacks apply -- the project's own
	// token while editing, and the environment's token at BeginPlay for a headless or packaged run.
	UCesiumHeightSampler::ConfigureCesiumForOrigin(
		World,
		Settings->OriginLatitude,
		Settings->OriginLongitude,
		Settings->OriginHeightMeters,
		IonAccessToken,
		Settings->PhotorealIonAssetId,
		Settings->GroundIonAssetId,
		/*bRefreshTileset=*/false);

	for (const FGeoreferencedWorldLayer& Layer : Settings->Layers)
	{
		if (Layer.Tag.IsEmpty())
		{
			continue;
		}
		if (Layer.VerticalOffsetMeters != 0.0)
		{
			UCesiumHeightSampler::SetLayerVerticalOffset(World, Layer.Tag, Layer.VerticalOffsetMeters);
		}
		UCesiumHeightSampler::SetLayerVisible(World, Layer.Tag, Layer.bVisible);
		UCesiumHeightSampler::SetLayerCollision(World, Layer.Tag, Layer.bCollision);
	}

	// Hiding the layers the simulation does not draw is deliberately NOT done here. The editor's
	// visibility flag lasts for the session rather than being saved, so it is applied when the level
	// is opened -- see FGeneratedWorldEditorPresentation.
}

/** Tags the volume this tool maintains, so re-importing replaces it rather than stacking another. */
const FName GeneratedWorldExposureTag(TEXT("generated_world_exposure"));

/**
 * Give the level an exposure it can be looked at with.
 *
 * A generated world is lit by CesiumSunSky, whose sun carries a physically real intensity -- about
 * 111,000 lux at midday. The project disables automatic exposure (r.DefaultFeature.AutoExposure) so
 * that camera sensors decide their own exposure and produce repeatable images, which leaves nothing
 * to map that intensity into a displayable range: the level opens correct in every respect and
 * renders as flat white, which reads as a broken import rather than an unexposed one.
 *
 * An unbound volume fixing exposure at a daylight value solves it for anyone opening the level, and
 * cannot disturb the sensors: every exposure property it sets is one that ASceneCaptureSensor
 * overrides on its own capture component, and a component's settings apply over any volume in the
 * scene.
 */
void EnsureExposureVolume(UWorld* World)
{
	if (!World || !World->PersistentLevel)
	{
		return;
	}

	for (int32 Index = World->PersistentLevel->Actors.Num() - 1; Index >= 0; --Index)
	{
		AActor* Existing = World->PersistentLevel->Actors[Index];
		if (IsValid(Existing) && Existing->ActorHasTag(GeneratedWorldExposureTag))
		{
			Existing->Destroy();
		}
	}

	FActorSpawnParameters SpawnParams;
	SpawnParams.SpawnCollisionHandlingOverride = ESpawnActorCollisionHandlingMethod::AlwaysSpawn;
	SpawnParams.OverrideLevel = World->PersistentLevel;
	APostProcessVolume* Volume = World->SpawnActor<APostProcessVolume>(
		APostProcessVolume::StaticClass(), FTransform::Identity, SpawnParams);
	if (!Volume)
	{
		UE_LOG(LogCarlaTools, Warning,
			TEXT("[WorldPackageImporter] could not add an exposure volume; the level will open "
			     "correct but overexposed."));
		return;
	}

	// Unbound, so it applies wherever the camera is rather than inside a box the world outgrows.
	Volume->bUnbound = true;
	Volume->BlendWeight = 1.0f;

	// Fixed rather than adaptive: a generated world is looked at from ground level and from altitude
	// in the same session, and an adapting exposure would change what the imagery looks like as the
	// camera moves. The values are the photographer's sunny-16 rule -- f/16 at 1/125 s, ISO 100 --
	// which is roughly EV100 15 and is what a clear midday sun actually meters at.
	Volume->Settings.bOverride_AutoExposureMethod = true;
	Volume->Settings.AutoExposureMethod = EAutoExposureMethod::AEM_Manual;
	Volume->Settings.bOverride_CameraISO = true;
	Volume->Settings.CameraISO = 100.0f;
	Volume->Settings.bOverride_CameraShutterSpeed = true;
	Volume->Settings.CameraShutterSpeed = 125.0f;
	Volume->Settings.bOverride_DepthOfFieldFstop = true;
	Volume->Settings.DepthOfFieldFstop = 16.0f;

	Volume->Tags.Add(GeneratedWorldExposureTag);
	Volume->SetActorLabel(TEXT("GeneratedWorldExposure"));
}

} // namespace

FWorldPackageImportResult UWorldPackageImporter::ImportWorldPackage(
	const FString& PackageDirectory,
	const FString& MapName,
	const FString& DestinationFolder,
	bool bReplaceDifferentSource,
	const FString& IonAccessToken)
{
	FWorldPackageImportResult Result;

	// Compare what is about to be written against what is already there, BEFORE anything is
	// overwritten. Re-importing the same area is a refresh and proceeds; importing a different
	// extract over an existing level would destroy it along with any hand editing, so that is
	// refused unless the caller has said to replace it.
	{
		FString ManifestError;
		TUniquePtr<FZipArchiveReader> Package =
			OpenPackage(PackageFile(PackageDirectory, MapName), ManifestError);
		if (!Package)
		{
			Result.FailureReason = ManifestError;
			return Result;
		}
		const TSharedPtr<FJsonObject> Incoming = LoadManifestJson(*Package, ManifestError);
		if (!Incoming.IsValid())
		{
			Result.FailureReason = ManifestError;
			return Result;
		}
		const FString IncomingSource = StringOr(Incoming, TEXT("SourceOsmFileName"));
		const FString IncomingHash = StringOr(Incoming, TEXT("SourceOsmSha256"));

		const FString SettingsPath = DestinationFolder / (MapName + TEXT("_WorldSettings"));
		if (UEditorAssetLibrary::DoesAssetExist(DestinationFolder / MapName)
			&& UEditorAssetLibrary::DoesAssetExist(SettingsPath))
		{
			if (const UGeoreferencedWorldSettings* Existing =
					Cast<UGeoreferencedWorldSettings>(UEditorAssetLibrary::LoadAsset(SettingsPath)))
			{
				const bool bKnownSources =
					!IncomingHash.IsEmpty() && !Existing->SourceOsmSha256.IsEmpty();
				const bool bDifferent = bKnownSources && IncomingHash != Existing->SourceOsmSha256;
				if (bDifferent && !bReplaceDifferentSource)
				{
					// Report the hashes, not the file names: the comparison is on content, and both
					// sides usually carry the same name, so naming them read as "built from X, but
					// built from X".
					auto ShortHash = [](const FString& Hash)
					{
						return Hash.IsEmpty() ? FString(TEXT("an unrecorded source")) : Hash.Left(12);
					};
					Result.FailureReason = FString::Printf(
						TEXT("%s already exists and was built from %s (%s), but this package was "
						     "built from %s (%s). Importing would replace that level and any editing "
						     "done to it. Tick 'replace a level built from a different source' to "
						     "go ahead."),
						*(DestinationFolder / MapName),
						*(Existing->SourceOsmFileName.IsEmpty()
							? FString(TEXT("an unrecorded source")) : Existing->SourceOsmFileName),
						*ShortHash(Existing->SourceOsmSha256),
						*(IncomingSource.IsEmpty()
							? FString(TEXT("an unrecorded source")) : IncomingSource),
						*ShortHash(IncomingHash));
					return Result;
				}
				UE_LOG(LogCarlaTools, Display,
					TEXT("[WorldPackageImporter] replacing %s, previously built from '%s'"),
					*(DestinationFolder / MapName), *Existing->SourceOsmFileName);
			}
		}
	}

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

	// Deleting a level does not free its name. UEditorAssetLibrary::DeleteAsset loads the World to
	// delete it, tears it down and removes the file, but leaves the UPackage resident -- so the next
	// import finds nothing on disk, nothing in the asset registry, and the name still taken. The
	// clone is then refused with "an asset already exists" while the delete reports "not a valid
	// asset": two contradictory errors describing the same residue.
	//
	// Collecting garbage does not reliably reclaim it, so the residue is renamed aside instead, which
	// frees the name outright. The renamed package is unreferenced and goes when the editor next
	// collects.
	if (UPackage* Stale = FindPackage(nullptr, *LevelPackageName))
	{
		const FName Aside = MakeUniqueObjectName(
			nullptr, UPackage::StaticClass(), FName(*(LevelPackageName + TEXT("_Replaced"))));
		Stale->Rename(*Aside.ToString(), nullptr,
			REN_DontCreateRedirectors | REN_NonTransactional | REN_DoNotDirty);
		UE_LOG(LogCarlaTools, Verbose,
			TEXT("[WorldPackageImporter] moved a resident %s aside as %s so it could be replaced."),
			*LevelPackageName, *Aside.ToString());
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

	FString OpenDriveText;
	{
		FString PackageError;
		TUniquePtr<FZipArchiveReader> Package =
			OpenPackage(PackageFile(PackageDirectory, MapName), PackageError);
		if (!Package || !ReadPackageText(*Package, OpenDriveEntry, OpenDriveText))
		{
			Result.FailureReason = PackageError.IsEmpty()
				? TEXT("the world package carries no road network") : PackageError;
			return Result;
		}
	}
	if (!FFileHelper::SaveStringToFile(OpenDriveText, *Result.OpenDriveFilePath,
			FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM))
	{
		Result.FailureReason = FString::Printf(
			TEXT("could not place the road network at %s"), *Result.OpenDriveFilePath);
		return Result;
	}

	// Generate the road surface into the level, so opening it shows the world rather than an empty
	// map, and tell the generator not to build a second surface over the top at play time.
	{
		const FRoadSurfaceBakeResult Bake =
			URoadSurfaceBaker::BakeIntoWorld(World, OpenDriveText, MapName, Settings);
		Result.RoadPiecesBaked = Bake.PiecesBaked;
		if (!Bake.bSucceeded)
		{
			// A level without its surface is still usable -- the generator rebuilds it at play time --
			// so this is reported rather than treated as a failed import.
			UE_LOG(LogCarlaTools, Warning,
				TEXT("[WorldPackageImporter] road surface not baked (%s); the level will build it at "
				     "play time instead."), *Bake.FailureReason);
		}
		for (AActor* Actor : World->PersistentLevel->Actors)
		{
			if (AOpenDriveGenerator* Generator = Cast<AOpenDriveGenerator>(Actor))
			{
				Generator->SetGeometryBaked(Bake.bSucceeded);
			}
		}
	}

	// Put the globe in the level itself, so opening it shows the world rather than bare geometry.
	ConfigureGlobeAsLevelContent(World, Settings, IonAccessToken);
	EnsureExposureVolume(World);

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
