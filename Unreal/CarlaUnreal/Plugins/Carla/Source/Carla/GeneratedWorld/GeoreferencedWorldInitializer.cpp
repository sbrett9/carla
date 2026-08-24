// Copyright (c) 2026 CARLA-Cesium digital-twin project.

#include "GeneratedWorld/GeoreferencedWorldInitializer.h"

#include "Carla.h"
#include "Carla/Game/CarlaStatics.h"

#include "BareEarthReference.h"
#include "CesiumHeightSampler.h"
#include "DrapedTerrain.h"
#include "StagingBounds.h"

#include "Engine/World.h"
#include "HAL/PlatformMisc.h"

#include <util/disable-ue4-macros.h>
#include <carla/rpc/OpendriveGenerationParameters.h>
#include <util/enable-ue4-macros.h>

// The Windows headers reached through the includes above rename GetEnvironmentVariable to its wide
// variant, which hides the engine's own single-argument overload. Same treatment CreateDirectory
// gets in Online/CustomFileDownloader.cpp.
#if defined(_WIN32) && defined(GetEnvironmentVariable)
#undef GetEnvironmentVariable
#endif

AGeoreferencedWorldInitializer::AGeoreferencedWorldInitializer()
{
	PrimaryActorTick.bCanEverTick = false;
	RootComponent = CreateDefaultSubobject<USceneComponent>(TEXT("Root"));
	SetHidden(true);
}

void AGeoreferencedWorldInitializer::BeginPlay()
{
	Super::BeginPlay();
	ApplyWorldSettings();
}

FString AGeoreferencedWorldInitializer::IonAccessTokenFromEnvironment()
{
	return FPlatformMisc::GetEnvironmentVariable(TEXT("CESIUM_ION_TOKEN"));
}

bool AGeoreferencedWorldInitializer::ApplyWorldSettings()
{
	UWorld* World = GetWorld();
	if (!World)
	{
		return false;
	}

	UGeoreferencedWorldSettings* WorldSettings = Settings.LoadSynchronous();
	if (!WorldSettings)
	{
		UE_LOG(LogCarla, Display,
			TEXT("[GeneratedWorld] no world settings assigned; leaving this world as authored."));
		return false;
	}
	if (!WorldSettings->HasUsableDatum())
	{
		UE_LOG(LogCarla, Warning,
			TEXT("[GeneratedWorld] world settings carry no origin; refusing to apply, because a world "
			     "placed at latitude/longitude 0 would look plausible and report nonsense."));
		return false;
	}

	// 1. The datum and the streamed layers. Everything below depends on this having run, and it must
	//    run before any per-layer offset: it reassigns tilesets to the default georeference, which
	//    would otherwise undo them.
	const FString IonToken = IonAccessTokenFromEnvironment();
	if (IonToken.IsEmpty())
	{
		UE_LOG(LogCarla, Warning,
			TEXT("[GeneratedWorld] no CESIUM_ION_TOKEN in the environment: the world is placed "
			     "correctly but its imagery layers will not stream."));
	}
	UCesiumHeightSampler::ConfigureCesiumForOrigin(
		World,
		WorldSettings->OriginLatitude,
		WorldSettings->OriginLongitude,
		WorldSettings->OriginHeightMeters,
		IonToken,
		WorldSettings->PhotorealIonAssetId,
		WorldSettings->GroundIonAssetId,
		/*bRefreshTileset=*/true);

	// 2. Per-layer vertical offsets, then presentation.
	for (const FGeoreferencedWorldLayer& Layer : WorldSettings->Layers)
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

	// 3. The draped collision surface, when the world was reconciled point by point. Its heights are
	//    local Z, so they are derived here from the two stored fields rather than kept as a third:
	//    driven surface = ground + offset, and local Z = that minus the origin height.
	UBareEarthOffsetField* Field = WorldSettings->OffsetField.LoadSynchronous();
	const bool bHaveField = WorldSettings->bDrapeActive && Field && Field->IsWellFormed();
	if (WorldSettings->bDrapeActive && !bHaveField)
	{
		UE_LOG(LogCarla, Error,
			TEXT("[GeneratedWorld] this world was reconciled point by point but its per-cell field is "
			     "missing or malformed: there will be no drivable ground, and reported altitude "
			     "would be the driven height rather than true ground height."));
	}
	if (bHaveField)
	{
		const int32 Count = Field->NodeCount();
		TArray<double> LocalHeights;
		LocalHeights.SetNumUninitialized(Count);
		for (int32 i = 0; i < Count; ++i)
		{
			LocalHeights[i] = static_cast<double>(Field->BareEarthDtmMeters[i])
				+ static_cast<double>(Field->OffsetMeters[i])
				- WorldSettings->OriginHeightMeters;
		}
		UDrapedTerrain::Build(
			World, Field->MinXMeters, Field->MinYMeters, Field->CellSizeMeters,
			Field->NumCols, Field->NumRows, LocalHeights);

		// The heightfield owns physics across the whole sandbox; the bare-earth layer stays as the
		// hidden height-sample source with its collision off, so the two cannot disagree underfoot.
		UCesiumHeightSampler::SetLayerCollision(World, TEXT("ground"), false);
	}

	// 4. The sandbox extent and its inward staging ring.
	if (WorldSettings->StagingMaxXMeters > WorldSettings->StagingMinXMeters
		&& WorldSettings->StagingMaxYMeters > WorldSettings->StagingMinYMeters)
	{
		UStagingBounds::Set(
			World,
			WorldSettings->StagingMinXMeters, WorldSettings->StagingMinYMeters,
			WorldSettings->StagingMaxXMeters, WorldSettings->StagingMaxYMeters,
			WorldSettings->StagingMarginMeters);
	}

	// 5. How to recover bare-earth height from the height a vehicle drives at. Published even when the
	//    shift is zero, so that a client reading it can tell "no shift" from "no record".
	UBareEarthReference::Set(
		World,
		WorldSettings->HeightAlignOffsetMeters,
		bHaveField,
		bHaveField ? Field->MinXMeters : 0.0,
		bHaveField ? Field->MinYMeters : 0.0,
		bHaveField ? Field->CellSizeMeters : 0.0,
		bHaveField ? Field->NumCols : 0,
		bHaveField ? Field->NumRows : 0,
		bHaveField ? Field->OffsetMeters : TArray<float>(),
		bHaveField ? Field->BareEarthDtmMeters : TArray<float>());

	// 6. The parameters any road regeneration must reproduce, so a world that rebuilds its surface
	//    gets the one it had rather than the library defaults.
	if (UCarlaGameInstance* GameInstance = UCarlaStatics::GetGameInstance(World))
	{
		const FGeneratedRoadMeshParameters& Road = WorldSettings->RoadMeshParameters;
		carla::rpc::OpendriveGenerationParameters Parameters;
		Parameters.vertex_distance = Road.VertexDistance;
		Parameters.max_road_length = Road.MaxRoadLength;
		Parameters.wall_height = Road.WallHeight;
		Parameters.additional_width = Road.AdditionalWidth;
		Parameters.smooth_junctions = Road.bSmoothJunctions;
		Parameters.enable_mesh_visibility = Road.bEnableMeshVisibility;
		Parameters.enable_pedestrian_navigation = Road.bEnablePedestrianNavigation;
		GameInstance->SetOpendriveGenerationParameters(Parameters);
	}

	bApplied = true;
	UE_LOG(LogCarla, Display,
		TEXT("[GeneratedWorld] applied: origin %.7f, %.7f at %.2f m | %s | %d layer(s) | sandbox %.1f x %.1f m"),
		WorldSettings->OriginLatitude, WorldSettings->OriginLongitude, WorldSettings->OriginHeightMeters,
		bHaveField
			? TEXT("per-cell surface field")
			: *FString::Printf(TEXT("constant surface shift %.3f m"), WorldSettings->HeightAlignOffsetMeters),
		WorldSettings->Layers.Num(),
		WorldSettings->StagingMaxXMeters - WorldSettings->StagingMinXMeters,
		WorldSettings->StagingMaxYMeters - WorldSettings->StagingMinYMeters);
	return true;
}
