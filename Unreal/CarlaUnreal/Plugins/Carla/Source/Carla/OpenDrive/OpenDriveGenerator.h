// Copyright (c) 2026 Computer Vision Center (CVC) at the Universitat Autonoma
// de Barcelona (UAB).
//
// This work is licensed under the terms of the MIT license.
// For a copy, see <https://opensource.org/licenses/MIT>.
#pragma once

#include "Carla/Vehicle/VehicleSpawnPoint.h"

#include <util/disable-ue4-macros.h>
#include "carla/road/Map.h"
#include <util/enable-ue4-macros.h>

#include <util/ue-header-guard-begin.h>
#include "CoreMinimal.h"
#include "GameFramework/Actor.h"
#include "ProceduralMeshComponent.h"
#include <util/ue-header-guard-end.h>

#include <optional>

#include "OpenDriveGenerator.generated.h"

UCLASS()
class CARLA_API AProceduralMeshActor : public AActor
{
  GENERATED_BODY()
public:
  AProceduralMeshActor();

  UPROPERTY(Category = "Procedural Mesh Actor", VisibleDefaultsOnly, BlueprintReadOnly, meta = (AllowPrivateAccess = "true"))
  UProceduralMeshComponent* MeshComponent;
};

UCLASS()
class CARLA_API AOpenDriveGenerator : public AActor
{
  GENERATED_BODY()

public:

  /// Tag carried by every actor forming the generated road surface, whatever its representation:
  /// AProceduralMeshActor while the road is generated at runtime, AStaticMeshActor once it is baked
  /// into a saved level. Layer operations select on this tag rather than on a concrete class, so
  /// changing the representation does not silently take the road out of their reach.
  static const FName RoadSurfaceTag;

  /// Record that this level already contains its road surface, so BeginPlay does not build a second
  /// one over the top. Set by whatever baked the geometry in.
  UFUNCTION(BlueprintCallable, Category = "OpenDrive")
  void SetGeometryBaked(bool bBaked) { bGeometryBaked = bBaked; }


  AOpenDriveGenerator(const FObjectInitializer &ObjectInitializer);

  /// Set the OpenDRIVE information as string and generates the
  /// queryable map structure.
  bool LoadOpenDrive(const FString &OpenDrive);

  /// Get the OpenDRIVE information as string.
  const FString &GetOpenDrive() const;

  /// Checks if the OpenDrive has been loaded and it's valid.
  bool IsOpenDriveValid() const;

  /// Generates the road and sidewalk mesh based on the OpenDRIVE information.
  void GenerateRoadMesh();

  /// Generates pole meshes based on the OpenDRIVE information.
  void GeneratePoles();

  /// Generates spawn points along the road.
  void GenerateSpawnPoints();

  void GenerateAll();

protected:

  virtual void BeginPlay() override;

  /// Determine the height where the spawners will be placed, relative to each
  /// RoutePlanner
  UPROPERTY(Category = "Spawners", EditAnywhere)
  float SpawnersHeight = 300.f;

  UPROPERTY(Category = "Spawners", EditAnywhere)
  TArray<AVehicleSpawnPoint *> VehicleSpawners;

  UPROPERTY(EditAnywhere)
  FString OpenDriveData;

  UPROPERTY(EditAnywhere)
  TArray<AActor *> ActorMeshList;

  /// Set once the road geometry for this world has been produced and saved as level content, so
  /// BeginPlay does not generate a second copy on top of it.
  ///
  /// The generated actors and spawn points are UPROPERTY, so a world persisted as a level reloads
  /// already carrying them. Regenerating would append a duplicate road surface -- a second set of
  /// collision geometry occupying the same space -- and duplicate spawn points, while the map looks
  /// superficially correct.
  UPROPERTY(EditAnywhere)
  bool bGeometryBaked = false;

};
