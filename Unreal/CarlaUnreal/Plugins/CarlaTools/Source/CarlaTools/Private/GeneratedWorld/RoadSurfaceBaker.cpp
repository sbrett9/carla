// Copyright (c) 2026 CARLA-Cesium digital-twin project.

#include "GeneratedWorld/RoadSurfaceBaker.h"

#include "CarlaTools.h"
#include "Carla/OpenDrive/OpenDriveGenerator.h"
#include "Carla/Util/ProceduralCustomMesh.h"
#include "GeneratedWorld/GeoreferencedWorldSettings.h"

#include <util/ue-header-guard-begin.h>
#include "AssetRegistry/AssetRegistryModule.h"
#include "EditorAssetLibrary.h"
#include "Engine/StaticMesh.h"
#include "Engine/StaticMeshActor.h"
#include "Engine/World.h"
#include "Materials/MaterialInterface.h"
#include "PhysicsEngine/BodySetup.h"
#include "ProceduralMeshComponent.h"
#include "ProceduralMeshConversion.h"
#include "StaticMeshAttributes.h"
#include "UObject/Package.h"
#include "UObject/SavePackage.h"
#include <util/ue-header-guard-end.h>

#include <util/disable-ue4-macros.h>
#include <carla/opendrive/OpenDriveParser.h>
#include <carla/road/Map.h>
#include <carla/rpc/OpendriveGenerationParameters.h>
#include <carla/rpc/String.h>
#include <util/enable-ue4-macros.h>

namespace
{
	/**
	 * Where a lane type's meshes are written, and what they are surfaced with.
	 *
	 * The folder is not cosmetic: the semantic tagger derives an object's label from the fifth
	 * element of its asset path, so "Road" and "SideWalk" are what make a baked road report as a road
	 * to segmentation sensors. Spelling them differently silently labels the whole surface as scenery.
	 */
	struct FLaneTypeBake
	{
		const TCHAR* Folder;
		const TCHAR* MaterialPath;
		const TCHAR* NamePrefix;
	};

	const FLaneTypeBake DrivingBake{
		TEXT("Road"),
		TEXT("/Game/Carla/Static/GenericMaterials/Roads/MI_Road_Asphalt_A"),
		// Not "DrivingLane": a piece of driving surface is a lane along a road or the whole of a
		// junction, and naming every piece a lane made junctions hard to recognise in the outliner.
		TEXT("SM_RoadSurface") };

	const FLaneTypeBake SidewalkBake{
		TEXT("SideWalk"),
		TEXT("/Game/Carla/Static/GenericMaterials/Gutters_Curbs/Curb/MI_CurbDirty01"),
		TEXT("SM_Sidewalk") };

	/** Which lane types are worth baking, and how. Others are left to the runtime generator. */
	bool BakeForLaneType(carla::road::Lane::LaneType LaneType, FLaneTypeBake& OutBake)
	{
		switch (LaneType)
		{
			case carla::road::Lane::LaneType::Driving:
				OutBake = DrivingBake;
				return true;
			case carla::road::Lane::LaneType::Sidewalk:
				OutBake = SidewalkBake;
				return true;
			default:
				return false;
		}
	}

	/**
	 * Write one procedural mesh out as a static mesh asset.
	 *
	 * Follows the cook the procedural-building tools in this plugin use: a source model so the result
	 * can be inspected and rebuilt in the editor, lightmap UVs, and an explicit material slot. The
	 * mesh-creation helper in the runtime module is deliberately not used -- it leaves no source
	 * model, allocates a single material slot, and does not save.
	 */
	UStaticMesh* CookToStaticMesh(
		UProceduralMeshComponent* Source, const FString& PackageName, const FString& AssetName)
	{
		FMeshDescription MeshDescription = BuildMeshDescription(Source);
		if (MeshDescription.Polygons().Num() == 0)
		{
			return nullptr;
		}

		UPackage* Package = CreatePackage(*PackageName);
		if (!Package)
		{
			return nullptr;
		}
		Package->FullyLoad();
		if (UObject* Stale = StaticFindObject(nullptr, Package, *AssetName))
		{
			Stale->Rename(nullptr, GetTransientPackage(),
				REN_DontCreateRedirectors | REN_DoNotDirty | REN_NonTransactional);
		}

		UStaticMesh* StaticMesh = NewObject<UStaticMesh>(
			Package, *AssetName, RF_Public | RF_Standalone);
		StaticMesh->InitResources();
		StaticMesh->SetLightingGuid(FGuid::NewGuid());

		FStaticMeshSourceModel& SourceModel = StaticMesh->AddSourceModel();
		SourceModel.BuildSettings.bRecomputeNormals = false;
		SourceModel.BuildSettings.bRecomputeTangents = true;
		SourceModel.BuildSettings.bRemoveDegenerates = true;
		SourceModel.BuildSettings.bUseHighPrecisionTangentBasis = false;
		SourceModel.BuildSettings.bUseFullPrecisionUVs = false;
		SourceModel.BuildSettings.bGenerateLightmapUVs = true;
		SourceModel.BuildSettings.SrcLightmapIndex = 0;
		SourceModel.BuildSettings.DstLightmapIndex = 1;
		StaticMesh->CreateMeshDescription(0, MoveTemp(MeshDescription));
		StaticMesh->CommitMeshDescription(0);

		const int32 SectionCount = Source->GetNumSections();
		for (int32 Section = 0; Section < SectionCount; ++Section)
		{
			StaticMesh->GetStaticMaterials().Add(FStaticMaterial(Source->GetMaterial(Section)));
		}

		// The road is driven on, so its collision follows the rendered surface exactly rather than a
		// simplified hull, matching what the runtime generator produces.
		StaticMesh->CreateBodySetup();
		if (UBodySetup* BodySetup = StaticMesh->GetBodySetup())
		{
			BodySetup->BodySetupGuid = FGuid::NewGuid();
			BodySetup->CollisionTraceFlag = CTF_UseComplexAsSimple;
		}

		StaticMesh->SetImportVersion(EImportStaticMeshVersion::LastVersion);
		StaticMesh->Build(false);
		StaticMesh->PostEditChange();
		FAssetRegistryModule::AssetCreated(StaticMesh);

		const FString FileName = FPackageName::LongPackageNameToFilename(
			PackageName, FPackageName::GetAssetPackageExtension());
		FSavePackageArgs SaveArgs;
		SaveArgs.TopLevelFlags = RF_Public | RF_Standalone;
		SaveArgs.SaveFlags = SAVE_NoError;
		SaveArgs.Error = GError;
		return UPackage::SavePackage(Package, StaticMesh, *FileName, SaveArgs) ? StaticMesh : nullptr;
	}
}

FRoadSurfaceBakeResult URoadSurfaceBaker::BakeIntoWorld(
	UWorld* World,
	const FString& OpenDriveText,
	const FString& MapName,
	const UGeoreferencedWorldSettings* Settings,
	const FString& AssetRootFolder)
{
	FRoadSurfaceBakeResult Result;
	if (!World || !World->PersistentLevel)
	{
		Result.FailureReason = TEXT("no world to bake into");
		return Result;
	}
	if (!Settings)
	{
		Result.FailureReason = TEXT("no world settings, so the surface parameters are unknown");
		return Result;
	}

	const auto ParsedMap = carla::opendrive::OpenDriveParser::Load(
		carla::rpc::FromLongFString(OpenDriveText));
	if (!ParsedMap.has_value())
	{
		Result.FailureReason = TEXT("the road network could not be parsed");
		return Result;
	}

	// Reproduce the surface the simulation would build, so the baked geometry and the driven geometry
	// are the same shape. The two fields below never cross the network boundary, so the running
	// server always uses their defaults; here, in process, they can be set to the tool's values.
	const FGeneratedRoadMeshParameters& Road = Settings->RoadMeshParameters;
	carla::rpc::OpendriveGenerationParameters Params;
	Params.vertex_distance = Road.VertexDistance;
	Params.max_road_length = Road.MaxRoadLength;
	Params.wall_height = Road.WallHeight;
	Params.additional_width = Road.AdditionalWidth;
	Params.smooth_junctions = Road.bSmoothJunctions;
	Params.enable_mesh_visibility = Road.bEnableMeshVisibility;
	Params.enable_pedestrian_navigation = Road.bEnablePedestrianNavigation;
	Params.vertex_width_resolution = 8.0;
	Params.simplification_percentage = 0.0f;

	// Cover the whole sandbox, with a margin so roads sitting exactly on the boundary are not dropped
	// by the strict inequalities in the road filter. A tall vertical span keeps grade separations in,
	// since the extent is recorded in plan only.
	//
	// The filter's two axes are not symmetrical: it accepts a road when
	//     minpos.x < x < maxpos.x   AND   minpos.y > y > maxpos.y
	// so Y has to be given DESCENDING while X is given ascending. Passing both ascending matches no
	// road at all and the bake silently produces nothing, which is exactly what it did.
	constexpr float BoundsMarginMeters = 50.0f;
	const float MinX = static_cast<float>(
		FMath::Min(Settings->StagingMinXMeters, Settings->StagingMaxXMeters)) - BoundsMarginMeters;
	const float MaxX = static_cast<float>(
		FMath::Max(Settings->StagingMinXMeters, Settings->StagingMaxXMeters)) + BoundsMarginMeters;
	const float HigherY = static_cast<float>(
		FMath::Max(Settings->StagingMinYMeters, Settings->StagingMaxYMeters)) + BoundsMarginMeters;
	const float LowerY = static_cast<float>(
		FMath::Min(Settings->StagingMinYMeters, Settings->StagingMaxYMeters)) - BoundsMarginMeters;

	const carla::geom::Vector3D MinPosition(MinX, HigherY, -1.0e5f);
	const carla::geom::Vector3D MaxPosition(MaxX, LowerY, 1.0e5f);

	// Deliberately NOT GenerateOrderedChunkedMeshInLocations, which rebuilds every junction of more
	// than two connections from a signed distance field on a half-metre grid. That discards the lane
	// geometry, so the junction neither meets the roads it joins -- a metre out, typically -- nor
	// carries texture coordinates the road material can use. The simulation has never used it; it
	// builds junctions by merging the connecting roads' lanes, and this asks for the same thing, so
	// that a level shows the surface the simulation would drive on.
	const auto MeshesByLaneType =
		ParsedMap->GenerateOrderedMeshWithLaneJunctions(Params, MinPosition, MaxPosition);

	// Replace any surface a previous bake left behind, so re-importing does not stack road on road.
	for (int32 Index = World->PersistentLevel->Actors.Num() - 1; Index >= 0; --Index)
	{
		AActor* Existing = World->PersistentLevel->Actors[Index];
		if (IsValid(Existing) && Existing->IsA<AStaticMeshActor>()
			&& Existing->ActorHasTag(AOpenDriveGenerator::RoadSurfaceTag))
		{
			Existing->Destroy();
		}
	}

	// Clear the meshes a previous generation left, rather than writing over the ones whose names
	// happen to repeat. The piece count varies with the road network and with how the surface is
	// built, so anything not overwritten would stay behind unreferenced: a stale copy of an older
	// surface, indistinguishable in the content browser from the current one.
	for (const FLaneTypeBake* Bake : { &DrivingBake, &SidewalkBake })
	{
		const FString Folder = AssetRootFolder / Bake->Folder / MapName;
		if (UEditorAssetLibrary::DoesDirectoryExist(Folder))
		{
			UEditorAssetLibrary::DeleteDirectory(Folder);
		}
	}

	FActorSpawnParameters SpawnParams;
	SpawnParams.SpawnCollisionHandlingOverride = ESpawnActorCollisionHandlingMethod::AlwaysSpawn;
	SpawnParams.OverrideLevel = World->PersistentLevel;

	int32 PieceIndex = 0;
	int32 SkippedEmpty = 0;
	int32 FailedToCook = 0;
	FBox2D BakedExtent(ForceInit);
	for (const auto& LaneTypePair : MeshesByLaneType)
	{
		FLaneTypeBake Bake;
		if (!BakeForLaneType(LaneTypePair.first, Bake))
		{
			UE_LOG(LogCarlaTools, Verbose,
				TEXT("[RoadSurfaceBaker] lane type %d produced %d mesh(es), which this bake does not "
					 "cover; that surface is built at play time instead."),
				static_cast<int32>(LaneTypePair.first), static_cast<int32>(LaneTypePair.second.size()));
			continue;
		}
		const int32 MeshesOffered = static_cast<int32>(LaneTypePair.second.size());
		int32 MeshesTaken = 0;
		UMaterialInterface* Material = LoadObject<UMaterialInterface>(nullptr, Bake.MaterialPath);
		if (!Material)
		{
			UE_LOG(LogCarlaTools, Warning,
				TEXT("[RoadSurfaceBaker] no material at %s; that surface is baked untextured."),
				Bake.MaterialPath);
		}

		for (const auto& Mesh : LaneTypePair.second)
		{
			if (!Mesh || Mesh->GetVertices().empty())
			{
				++SkippedEmpty;
				continue;
			}
			const FProceduralCustomMesh MeshData = *Mesh;

			// A transient procedural mesh is the shortest route to a mesh description, and reuses the
			// conversion the runtime path already relies on. Note the texture coordinates are carried
			// through here; the runtime generator discards them at its own call site.
			UProceduralMeshComponent* Scratch = NewObject<UProceduralMeshComponent>();
			Scratch->bUseComplexAsSimpleCollision = true;
			Scratch->CreateMeshSection_LinearColor(
				0, MeshData.Vertices, MeshData.Triangles, MeshData.Normals,
				MeshData.UV0, TArray<FLinearColor>(), TArray<FProcMeshTangent>(), true);
			Scratch->SetMaterial(0, Material);

			const FString AssetName = FString::Printf(TEXT("%s_%d"), Bake.NamePrefix, PieceIndex);
			const FString PackageName =
				AssetRootFolder / Bake.Folder / MapName / AssetName;
			UStaticMesh* Baked = CookToStaticMesh(Scratch, PackageName, AssetName);
			Scratch->MarkAsGarbage();
			if (!Baked)
			{
				++FailedToCook;
				UE_LOG(LogCarlaTools, Warning,
					TEXT("[RoadSurfaceBaker] could not cook %s; that stretch of road has no baked "
						 "surface."), *PackageName);
				continue;
			}

			AStaticMeshActor* Actor = World->SpawnActor<AStaticMeshActor>(
				AStaticMeshActor::StaticClass(), FTransform::Identity, SpawnParams);
			if (!Actor)
			{
				continue;
			}
			Actor->GetStaticMeshComponent()->SetStaticMesh(Baked);
			if (Material)
			{
				Actor->GetStaticMeshComponent()->SetMaterial(0, Material);
			}
			Actor->SetActorLabel(AssetName);
			// Tagged so the layer operations that show, hide and un-collide the road keep finding it
			// now that it is static mesh geometry rather than procedural.
			Actor->Tags.Add(AOpenDriveGenerator::RoadSurfaceTag);

			for (const FVector& Vertex : MeshData.Vertices)
			{
				BakedExtent += FVector2D(Vertex.X, Vertex.Y);
			}

			Result.TrianglesBaked += MeshData.Triangles.Num() / 3;
			++MeshesTaken;
			++PieceIndex;
		}

		UE_LOG(LogCarlaTools, Display,
			TEXT("[RoadSurfaceBaker] %s: baked %d of %d mesh(es) offered."),
			Bake.NamePrefix, MeshesTaken, MeshesOffered);
	}

	Result.PiecesBaked = PieceIndex;
	Result.bSucceeded = PieceIndex > 0;
	if (!Result.bSucceeded)
	{
		Result.FailureReason = TEXT("the road network produced no drivable surface to bake");
	}
	// Report the plan extent alongside the sandbox it was asked to cover. A baked surface that spans
	// noticeably less than the sandbox means roads were dropped somewhere upstream of the cooking,
	// which is otherwise invisible -- the level still loads and still drives, just not everywhere.
	UE_LOG(LogCarlaTools, Display,
		TEXT("[RoadSurfaceBaker] baked %d piece(s), %d triangle(s) for %s "
			 "(%d empty mesh(es) skipped, %d failed to cook)"),
		Result.PiecesBaked, Result.TrianglesBaked, *MapName, SkippedEmpty, FailedToCook);
	if (BakedExtent.bIsValid)
	{
		UE_LOG(LogCarlaTools, Display,
			TEXT("[RoadSurfaceBaker] baked surface spans x %.0f..%.0f y %.0f..%.0f m; "
				 "sandbox is x %.0f..%.0f y %.0f..%.0f m"),
			BakedExtent.Min.X / 100.0, BakedExtent.Max.X / 100.0,
			BakedExtent.Min.Y / 100.0, BakedExtent.Max.Y / 100.0,
			FMath::Min(Settings->StagingMinXMeters, Settings->StagingMaxXMeters),
			FMath::Max(Settings->StagingMinXMeters, Settings->StagingMaxXMeters),
			FMath::Min(Settings->StagingMinYMeters, Settings->StagingMaxYMeters),
			FMath::Max(Settings->StagingMinYMeters, Settings->StagingMaxYMeters));
	}
	return Result;
}
