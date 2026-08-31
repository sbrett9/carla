// Copyright (c) 2026 CARLA-Cesium digital-twin project.

#include "DrapedTerrain.h"

#include "Engine/Engine.h"
#include "Engine/World.h"
#include "EngineUtils.h"

#include "PhysicsPublic.h"
#include "Physics/PhysicsFiltering.h"
#include "Physics/PhysicsInterfaceCore.h"
#include "Physics/PhysicsInterfaceUtils.h"
#include "Physics/Experimental/PhysScene_Chaos.h"

#include "Chaos/Core.h"
#include "Chaos/Vector.h"
#include "Chaos/HeightField.h"
#include "Chaos/ImplicitObject.h"
#include "Chaos/ImplicitObjectTransformed.h"
#include "Chaos/ShapeInstance.h"
#include "Chaos/ParticleHandle.h"
#include "PhysicsProxy/SingleParticlePhysicsProxy.h"

// ── UDrapedTerrainComponent ──────────────────────────────────────────────────

UDrapedTerrainComponent::UDrapedTerrainComponent()
{
	PrimaryComponentTick.bCanEverTick = false;
	SetMobility(EComponentMobility::Static);
	bHiddenInGame = true;                 // collision-only; the photoreal renders visuals
	CastShadow = false;
	// Drive-on-able static world surface; vehicles (Pawn/Vehicle/WorldDynamic) block WorldStatic.
	SetCollisionEnabled(ECollisionEnabled::QueryAndPhysics);
	SetCollisionObjectType(ECC_WorldStatic);
	SetCollisionResponseToAllChannels(ECR_Block);
}

void UDrapedTerrainComponent::SetGrid(double InOriginXCm, double InOriginYCm, double InCellSizeCm,
	int32 InNumCols, int32 InNumRows, TArray<double>&& InHeightsCm)
{
	OriginXCm = InOriginXCm;
	OriginYCm = InOriginYCm;
	CellSizeCm = InCellSizeCm;
	NumCols = InNumCols;
	NumRows = InNumRows;
	HeightsCm = MoveTemp(InHeightsCm);
	RecomputeLocalBounds();
}

void UDrapedTerrainComponent::RecomputeLocalBounds()
{
	// World-space AABB (the actor sits at the world origin, so local == world here).
	LocalBox = FBox(ForceInit);
	if (NumCols >= 2 && NumRows >= 2 && HeightsCm.Num() == NumCols * NumRows)
	{
		double MinZ = TNumericLimits<double>::Max(), MaxZ = TNumericLimits<double>::Lowest();
		for (double Z : HeightsCm) { MinZ = FMath::Min(MinZ, Z); MaxZ = FMath::Max(MaxZ, Z); }
		LocalBox = FBox(
			FVector(OriginXCm, OriginYCm, MinZ),
			FVector(OriginXCm + (NumCols - 1) * CellSizeCm, OriginYCm + (NumRows - 1) * CellSizeCm, MaxZ));
	}
}

void UDrapedTerrainComponent::PostLoad()
{
	Super::PostLoad();
	// The grid deserialized with the component; the bounds it implies did not. Registration builds
	// the heightfield from these same members straight after this, so nothing else is needed to
	// bring a saved world's ground back.
	RecomputeLocalBounds();
}

void UDrapedTerrainComponent::OnCreatePhysicsState()
{
	// Route to the SceneComponent impl, skipping the PrimitiveComponent body init (we create the
	// heightfield body by hand, exactly like ULandscapeHeightfieldCollisionComponent).
	USceneComponent::OnCreatePhysicsState();

	if (NumCols < 2 || NumRows < 2 || HeightsCm.Num() != NumCols * NumRows)
	{
		return; // not configured yet (initial registration before SetGrid); RecreatePhysicsState rebuilds
	}
	if (BodyInstance.IsValidBodyInstance())
	{
		return;
	}

	UWorld* World = GetWorld();
	FPhysScene* PhysScene = World ? World->GetPhysicsScene() : nullptr;
	if (!PhysScene)
	{
		UE_LOG(LogTemp, Warning, TEXT("[DrapedTerrain] OnCreatePhysicsState: no physics scene."));
		return;
	}

	// Heightfield geometry: Scale = (cellX, cellY, zUnit). Heights are world Z in cm (zUnit = 1).
	// MaterialIndices of size 1 → that single material is used for every cell (FHeightField rule).
	TArray<Chaos::FReal> Heights;
	Heights.Reserve(HeightsCm.Num());
	for (double Z : HeightsCm) { Heights.Add(static_cast<Chaos::FReal>(Z)); }
	TArray<uint8> MaterialIndices;
	MaterialIndices.Add(0);

	Chaos::FImplicitObjectPtr HeightFieldGeom = MakeImplicitObjectPtr<Chaos::FHeightField>(
		MoveTemp(Heights), MoveTemp(MaterialIndices), NumRows, NumCols,
		Chaos::FVec3(CellSizeCm, CellSizeCm, 1.0));
	Chaos::FImplicitObjectPtr Implicit = MakeImplicitObjectPtr<Chaos::TImplicitObjectTransformed<Chaos::FReal, 3>>(
		HeightFieldGeom, Chaos::FRigidTransform3(FTransform::Identity));

	auto CreateActorAndShape = [&]() -> FPhysicsActorHandle
	{
		FActorCreationParams Params;
		Params.InitialTM = FTransform(FQuat::Identity, FVector(OriginXCm, OriginYCm, 0.0));
		Params.InitialTM.SetScale3D(FVector(0));   // all scale lives in the geometry (mirrors Landscape)
		Params.bQueryOnly = false;
		Params.bStatic = true;
		Params.Scene = PhysScene;

		FPhysicsActorHandle PhysHandle;
		FPhysicsInterface::CreateActor(Params, PhysHandle);
		Chaos::FRigidBodyHandle_External& Body_External = PhysHandle->GetGameThreadAPI();

		Chaos::FShapesArray ShapeArray;
		TArray<Chaos::FImplicitObjectPtr> Geoms;
		TUniquePtr<Chaos::FPerShapeData> NewShape = Chaos::FShapeInstanceProxy::Make(ShapeArray.Num(), Implicit);

		FCollisionFilterData QueryFilterData, SimFilterData;
		CreateShapeFilterData(
			static_cast<uint8>(GetCollisionObjectType()), FMaskFilter(0),
			GetOwner()->GetUniqueID(), GetCollisionResponseToChannels(), GetUniqueID(), 0,
			QueryFilterData, SimFilterData, /*bEnableSim*/ true, /*bDisableCCD*/ false, /*bEnableQuery*/ true);
		QueryFilterData.Word3 |= (EPDF_SimpleCollision | EPDF_ComplexCollision);
		SimFilterData.Word3 |= (EPDF_SimpleCollision | EPDF_ComplexCollision);
		NewShape->SetQueryData(QueryFilterData);
		NewShape->SetSimData(SimFilterData);

		Geoms.Emplace(MoveTemp(Implicit));
		ShapeArray.Emplace(MoveTemp(NewShape));

		Body_External.SetGeometry(Geoms[0]);
		for (auto& Shape : ShapeArray)
		{
			Chaos::FRigidTransform3 WorldTransform(Body_External.X(), Body_External.R());
			Shape->UpdateShapeBounds(WorldTransform);
		}
		Body_External.MergeShapesArray(MoveTemp(ShapeArray));

		BodyInstance.PhysicsUserData = FPhysicsUserData(&BodyInstance);
		BodyInstance.OwnerComponent = this;
		BodyInstance.SetPhysicsActor(PhysHandle);
		Body_External.SetUserData(&BodyInstance.PhysicsUserData);
		return PhysHandle;
	};

	FPhysicsActorHandle PhysHandle = CreateActorAndShape();
	FPhysicsCommand::ExecuteWrite(PhysScene, [PhysScene, &PhysHandle]()
	{
		TArray<FPhysicsActorHandle> Actors = { PhysHandle };
		PhysScene->AddActorsToScene_AssumesLocked(Actors, /*bImmediateAccelStructureInsertion*/ true);
	});
	PhysScene->AddToComponentMaps(this, PhysHandle);

	UE_LOG(LogTemp, Display, TEXT("[DrapedTerrain] heightfield body created: %dx%d cells, cell %.1f cm, origin (%.1f, %.1f) cm"),
		NumCols, NumRows, CellSizeCm, OriginXCm, OriginYCm);
}

void UDrapedTerrainComponent::OnDestroyPhysicsState()
{
	Super::OnDestroyPhysicsState();

	if (UWorld* World = GetWorld())
	{
		if (FPhysScene_Chaos* PhysScene = World->GetPhysicsScene())
		{
			FPhysicsActorHandle ActorHandle = BodyInstance.GetPhysicsActor();
			if (FPhysicsInterface::IsValid(ActorHandle))
			{
				PhysScene->RemoveFromComponentMaps(ActorHandle);
			}
		}
	}
}

FBoxSphereBounds UDrapedTerrainComponent::CalcBounds(const FTransform& LocalToWorld) const
{
	if (LocalBox.IsValid)
	{
		return FBoxSphereBounds(LocalBox).TransformBy(LocalToWorld);
	}
	return FBoxSphereBounds(LocalToWorld.GetLocation(), FVector::ZeroVector, 0.0f);
}

// ── ADrapedTerrainActor ──────────────────────────────────────────────────────

ADrapedTerrainActor::ADrapedTerrainActor()
{
	PrimaryActorTick.bCanEverTick = false;
	Terrain = CreateDefaultSubobject<UDrapedTerrainComponent>(TEXT("DrapedTerrain"));
	RootComponent = Terrain;
}

// ── UDrapedTerrain (builder) ─────────────────────────────────────────────────

ADrapedTerrainActor* UDrapedTerrain::Build(
	UObject* WorldContextObject,
	double OriginXMeters, double OriginYMeters, double CellSizeMeters,
	int32 NumCols, int32 NumRows, const TArray<double>& HeightsMeters)
{
	UWorld* World = GEngine
		? GEngine->GetWorldFromContextObject(WorldContextObject, EGetWorldErrorMode::ReturnNull)
		: nullptr;
	if (!World)
	{
		UE_LOG(LogTemp, Warning, TEXT("[DrapedTerrain] Build: no world."));
		return nullptr;
	}
	if (NumCols < 2 || NumRows < 2 || HeightsMeters.Num() != NumCols * NumRows)
	{
		UE_LOG(LogTemp, Warning, TEXT("[DrapedTerrain] Build: bad grid (cols=%d rows=%d heights=%d)."),
			NumCols, NumRows, HeightsMeters.Num());
		return nullptr;
	}

	// Replace any existing draped terrain.
	for (TActorIterator<ADrapedTerrainActor> It(World); It; ++It)
	{
		if (IsValid(*It)) { It->Destroy(); }
	}

	const double M2CM = 100.0;
	TArray<double> HeightsCm;
	HeightsCm.Reserve(HeightsMeters.Num());
	for (double Z : HeightsMeters) { HeightsCm.Add(Z * M2CM); }

	FActorSpawnParameters SpawnParams;
	SpawnParams.SpawnCollisionHandlingOverride = ESpawnActorCollisionHandlingMethod::AlwaysSpawn;
	ADrapedTerrainActor* Actor = World->SpawnActor<ADrapedTerrainActor>(
		ADrapedTerrainActor::StaticClass(), FTransform::Identity, SpawnParams);
	if (!Actor)
	{
		UE_LOG(LogTemp, Warning, TEXT("[DrapedTerrain] Build: spawn failed."));
		return nullptr;
	}
	Actor->Tags.Add(FName(TEXT("draped_terrain")));
	Actor->Terrain->SetGrid(OriginXMeters * M2CM, OriginYMeters * M2CM, CellSizeMeters * M2CM,
		NumCols, NumRows, MoveTemp(HeightsCm));
	// The grid wasn't set when the component first registered, so (re)create the physics body now.
	Actor->Terrain->RecreatePhysicsState();

	UE_LOG(LogTemp, Display, TEXT("[DrapedTerrain] Build: %dx%d grid, cell %.2f m, origin (%.2f, %.2f) m."),
		NumCols, NumRows, CellSizeMeters, OriginXMeters, OriginYMeters);
	return Actor;
}
