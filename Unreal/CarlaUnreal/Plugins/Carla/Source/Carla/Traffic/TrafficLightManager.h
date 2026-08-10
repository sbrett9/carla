// Copyright (c) 2026 Computer Vision Center (CVC) at the Universitat Autonoma
// de Barcelona (UAB).
//
// This work is licensed under the terms of the MIT license.
// For a copy, see <https://opensource.org/licenses/MIT>.

#pragma once

#include "TrafficLightComponent.h"
#include "TrafficLightGroup.h"
#include "TrafficSignBase.h"
#include "Carla/OpenDrive/OpenDrive.h"

#include "TrafficLightManager.generated.h"

/// Class In charge of creating and assigning traffic
/// light groups, controllers and components.
UCLASS()
class CARLA_API ATrafficLightManager : public AActor
{
  GENERATED_BODY()

public:

  ATrafficLightManager();

  UFUNCTION(BlueprintCallable, Category = "Traffic Light Manager")
  void RegisterLightComponentFromOpenDRIVE(UTrafficLightComponent * TrafficLight);

  UFUNCTION(BlueprintCallable, Category = "Traffic Light Manager")
  void RegisterLightComponentGenerated(UTrafficLightComponent * TrafficLight);

  const std::optional<carla::road::Map> &GetMap();

  UFUNCTION(BlueprintCallable, Category = "Traffic Light Manager")
  ATrafficLightGroup* GetTrafficGroup(int JunctionId);

  UFUNCTION(BlueprintCallable, Category = "Traffic Light Manager")
  UTrafficLightController* GetController(FString ControllerId);

  UFUNCTION(BlueprintCallable, Category = "Traffic Light Manager")
  USignComponent* GetTrafficSign(FString SignId);

  UFUNCTION(BlueprintCallable, Category = "Traffic Light Manager")
  void SetFrozen(bool InFrozen);

  UFUNCTION(BlueprintCallable, Category = "Traffic Light Manager")
  bool GetFrozen();

  UFUNCTION(CallInEditor, Category = "Traffic Light Manager")
  void GenerateSignalsAndTrafficLights();

  UFUNCTION(CallInEditor, Category = "Traffic Light Manager")
  void RemoveGeneratedSignalsAndTrafficLights();

  UFUNCTION(CallInEditor, Category = "Traffic Light Manager")
  void MatchTrafficLightActorsWithOpenDriveSignals();

  // Called when the game starts by the gamemode
  void InitializeTrafficLights();

  // Shows or hides the meshes of every signal actor this manager generated from OpenDRIVE (the
  // OpenDRIVE mast arms, stop, yield and speed-limit props). Rendering only: the actors, their sign
  // components and their trigger volumes are left in place, so a hidden light still holds traffic
  // at its stop line and the truth telemetry is unchanged. Signals matched to a hand-placed actor
  // are never touched, which keeps the shipped towns out of scope.
  void SetGeneratedSignalsVisible(bool bVisible);

private:

  void SpawnTrafficLights();

  void SpawnSignals();

  // Prepares a signal actor spawned from OpenDRIVE: makes its meshes non-blocking and records it,
  // so the visibility toggle can address exactly the generated props and nothing else.
  void RegisterGeneratedSignal(AActor *SignalActor);

  void RemoveRoadrunnerProps() const;

  void RemoveAttachedProps(TArray<AActor*> Actors) const;

  // Mapped references to ATrafficLightGroup (junction)
  UPROPERTY()
  TMap<int, ATrafficLightGroup *> TrafficGroups;

  // Mapped references to UTrafficLightController (controllers)
  UPROPERTY()
  TMap<FString, UTrafficLightController *> TrafficControllers;

  // Mapped references to individual TrafficLightComponents
  UPROPERTY()
  TMap<FString, USignComponent *> TrafficSignComponents;

  // Mapped references to TrafficSigns
  TArray<ATrafficSignBase*> TrafficSigns;

  // Signal actors generated from OpenDRIVE. Signals that matched a hand-placed actor are
  // deliberately absent, so anything driven off this list stays clear of the shipped towns.
  UPROPERTY()
  TArray<AActor*> GeneratedSignals;

  UPROPERTY()
  bool bGeneratedSignalsVisible = true;

  UPROPERTY(EditAnywhere, Category= "Traffic Light Manager")
  TSubclassOf<AActor> TrafficLightModel_RHT;

  UPROPERTY(EditAnywhere, Category= "Traffic Light Manager")
  TSubclassOf<AActor> TrafficLightModel_LHT;


  // Relates an OpenDRIVE type to a traffic sign blueprint
  UPROPERTY(EditAnywhere, Category= "Traffic Light Manager")
  TMap<FString, TSubclassOf<AActor>> TrafficSignsModels;

  UPROPERTY(EditAnywhere, Category= "Traffic Light Manager")
  TMap<FString, TSubclassOf<USignComponent>> SignComponentModels;

  UPROPERTY(EditAnywhere, Category= "Traffic Light Manager")
  TMap<FString, TSubclassOf<AActor>> SpeedLimitModels;

  UPROPERTY(Category = "Traffic Light Manager", VisibleDefaultsOnly, BlueprintReadOnly, meta = (AllowPrivateAccess = "true"))
  USceneComponent *SceneComponent;

  UPROPERTY(EditAnywhere, Category= "Traffic Light Manager")
  bool TrafficLightsGenerated = false;

  // Id for TrafficLightGroups without corresponding OpenDRIVE junction
  UPROPERTY()
  int TrafficLightGroupMissingId = -2;

  // Id for TrafficLightControllers without corresponding OpenDRIVE junction
  UPROPERTY()
  int TrafficLightControllerMissingId = -1;

  // Id for TrafficLightComponents without corresponding OpenDRIVE junction
  UPROPERTY()
  int TrafficLightComponentMissingId = -1;

  UPROPERTY()
  bool bTrafficLightsFrozen = false;

};
