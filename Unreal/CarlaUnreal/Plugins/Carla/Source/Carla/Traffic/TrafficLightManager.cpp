// Copyright (c) 2026 Computer Vision Center (CVC) at the Universitat Autonoma
// de Barcelona (UAB).
//
// This work is licensed under the terms of the MIT license.
// For a copy, see <https://opensource.org/licenses/MIT>.

#include "TrafficLightManager.h"
#include "Game/CarlaStatics.h"
#include "StopSignComponent.h"
#include "YieldSignComponent.h"
#include "SpeedLimitComponent.h"
#include "Components/BoxComponent.h"
#include "Components/ShapeComponent.h"
#include "Runtime/CoreUObject/Public/UObject/ConstructorHelpers.h"
#include "OpenDrive/OpenDrive.h"
#include "OpenDrive/MapLogicParser.h"

#include "UObject/ConstructorHelpers.h"

#include <util/disable-ue4-macros.h>
#include <carla/rpc/String.h>
#include <carla/road/SignalType.h>
#include <carla/road/Lane.h>
#include <carla/road/LaneSection.h>
#include <carla/opendrive/OpenDriveParser.h>
#include <util/enable-ue4-macros.h>


#include <string>

ATrafficLightManager::ATrafficLightManager()
{
  PrimaryActorTick.bCanEverTick = false;
  SceneComponent = CreateDefaultSubobject<USceneComponent>(TEXT("RootComponent"));
  RootComponent = SceneComponent;

  // Hard coded default traffic light blueprint
  static ConstructorHelpers::FClassFinder<AActor> TrafficLightRHTFinder(
      TEXT( "/Game/Carla/Blueprints/TrafficLight/BP_TLOpenDrive_RHT" ) );
  if (TrafficLightRHTFinder.Succeeded())
  {
    TSubclassOf<AActor> Model = TrafficLightRHTFinder.Class;
    TrafficLightModel_RHT = Model;
  }

  // Hard coded default traffic light blueprint
  static ConstructorHelpers::FClassFinder<AActor> TrafficLightLHTFinder(
    TEXT( "/Game/Carla/Blueprints/TrafficLight/BP_TLOpenDrive_LHT" ) );
  if (TrafficLightLHTFinder.Succeeded())
  {
    TSubclassOf<AActor> Model = TrafficLightLHTFinder.Class;
    TrafficLightModel_LHT = Model;
  }
  // Default traffic signs models
  static ConstructorHelpers::FClassFinder<AActor> StopFinder(
      TEXT( "/Game/Carla/Static/TrafficSign/BP_Stop01" ) );
  if (StopFinder.Succeeded())
  {
    TSubclassOf<AActor> StopSignModel = StopFinder.Class;
    TrafficSignsModels.Add(carla::road::SignalType::StopSign().c_str(), StopSignModel);
    SignComponentModels.Add(carla::road::SignalType::StopSign().c_str(), UStopSignComponent::StaticClass());
  }
  static ConstructorHelpers::FClassFinder<AActor> YieldFinder(
      TEXT( "/Game/Carla/Static/TrafficSign/BP_Yield01" ) );
  if (YieldFinder.Succeeded())
  {
    TSubclassOf<AActor> YieldSignModel = YieldFinder.Class;
    TrafficSignsModels.Add(carla::road::SignalType::YieldSign().c_str(), YieldSignModel);
    SignComponentModels.Add(carla::road::SignalType::YieldSign().c_str(), UYieldSignComponent::StaticClass());
  }
  static ConstructorHelpers::FClassFinder<AActor> SpeedLimit30Finder(
      TEXT( "/Game/Carla/Static/TrafficSign/BP_SpeedLimit30" ) );
  if (SpeedLimit30Finder.Succeeded())
  {
    TSubclassOf<AActor> SpeedLimitModel = SpeedLimit30Finder.Class;
    SpeedLimitModels.Add("30", SpeedLimitModel);
  }
  static ConstructorHelpers::FClassFinder<AActor> SpeedLimit40Finder(
      TEXT( "/Game/Carla/Static/TrafficSign/BP_SpeedLimit40" ) );
  if (SpeedLimit40Finder.Succeeded())
  {
    TSubclassOf<AActor> SpeedLimitModel = SpeedLimit40Finder.Class;
    SpeedLimitModels.Add("40", SpeedLimitModel);
  }
  static ConstructorHelpers::FClassFinder<AActor> SpeedLimit50Finder(
      TEXT( "/Game/Carla/Static/TrafficSign/BP_SpeedLimit50" ) );
  if (SpeedLimit50Finder.Succeeded())
  {
    TSubclassOf<AActor> SpeedLimitModel = SpeedLimit50Finder.Class;
    SpeedLimitModels.Add("50", SpeedLimitModel);
  }
  static ConstructorHelpers::FClassFinder<AActor> SpeedLimit60Finder(
      TEXT( "/Game/Carla/Static/TrafficSign/BP_SpeedLimit60" ) );
  if (SpeedLimit60Finder.Succeeded())
  {
    TSubclassOf<AActor> SpeedLimitModel = SpeedLimit60Finder.Class;
    SpeedLimitModels.Add("60", SpeedLimitModel);
  }
  static ConstructorHelpers::FClassFinder<AActor> SpeedLimit70Finder(
      TEXT( "/Game/Carla/Static/TrafficSign/BP_SpeedLimit70" ) );
  if (SpeedLimit70Finder.Succeeded())
  {
    TSubclassOf<AActor> SpeedLimitModel = SpeedLimit70Finder.Class;
    SpeedLimitModels.Add("70", SpeedLimitModel);
  }
  static ConstructorHelpers::FClassFinder<AActor> SpeedLimit80Finder(
      TEXT( "/Game/Carla/Static/TrafficSign/BP_SpeedLimit80" ) );
  if (SpeedLimit80Finder.Succeeded())
  {
    TSubclassOf<AActor> SpeedLimitModel = SpeedLimit80Finder.Class;
    SpeedLimitModels.Add("80", SpeedLimitModel);
  }
  static ConstructorHelpers::FClassFinder<AActor> SpeedLimit90Finder(
      TEXT( "/Game/Carla/Static/TrafficSign/BP_SpeedLimit90" ) );
  if (SpeedLimit90Finder.Succeeded())
  {
    TSubclassOf<AActor> SpeedLimitModel = SpeedLimit90Finder.Class;
    SpeedLimitModels.Add("90", SpeedLimitModel);
  }
  static ConstructorHelpers::FClassFinder<AActor> SpeedLimit100Finder(
      TEXT( "/Game/Carla/Static/TrafficSign/BP_SpeedLimit100" ) );
  if (SpeedLimit100Finder.Succeeded())
  {
    TSubclassOf<AActor> SpeedLimitModel = SpeedLimit100Finder.Class;
    SpeedLimitModels.Add("100", SpeedLimitModel);
  }
  static ConstructorHelpers::FClassFinder<AActor> SpeedLimit110Finder(
      TEXT( "/Game/Carla/Static/TrafficSign/BP_SpeedLimit110" ) );
  if (SpeedLimit110Finder.Succeeded())
  {
    TSubclassOf<AActor> SpeedLimitModel = SpeedLimit110Finder.Class;
    SpeedLimitModels.Add("110", SpeedLimitModel);
  }
  static ConstructorHelpers::FClassFinder<AActor> SpeedLimit120Finder(
      TEXT( "/Game/Carla/Static/TrafficSign/BP_SpeedLimit120" ) );
  if (SpeedLimit120Finder.Succeeded())
  {
    TSubclassOf<AActor> SpeedLimitModel = SpeedLimit120Finder.Class;
    SpeedLimitModels.Add("120", SpeedLimitModel);
  }
  TrafficLightGroupMissingId = -2;
}

void ATrafficLightManager::RegisterLightComponentFromOpenDRIVE(UTrafficLightComponent * TrafficLightComponent)
{
  ACarlaGameModeBase *GM = UCarlaStatics::GetGameMode(GetWorld());
  check(GM);

  // Cast to std::string
  carla::road::SignId SignId(TCHAR_TO_UTF8(*(TrafficLightComponent->GetSignId())));

  ATrafficLightGroup* TrafficLightGroup;
  UTrafficLightController* TrafficLightController;

  const auto &Signal = GetMap()->GetSignals().at(SignId);
  if(Signal->GetControllers().size())
  {
    // Only one controller per signal
    auto ControllerId = *(Signal->GetControllers().begin());

    // Get controller
    const auto &Controller = GetMap()->GetControllers().at(ControllerId);
    if(Controller->GetJunctions().empty())
    {
      UE_LOG(LogCarla, Error, TEXT("No junctions in controller %d"), *(ControllerId.c_str()) );
      return;
    }
    // Get junction of the controller
    auto JunctionId = *(Controller->GetJunctions().begin());

    // Search/create TrafficGroup (junction traffic light manager)
    if(!TrafficGroups.Contains(JunctionId))
    {
      FActorSpawnParameters SpawnParams;
      SpawnParams.OverrideLevel = GM->GetULevelFromName("TrafficLights");
      auto * NewTrafficLightGroup =
          GetWorld()->SpawnActor<ATrafficLightGroup>(SpawnParams);
      NewTrafficLightGroup->JunctionId = JunctionId;
      TrafficGroups.Add(JunctionId, NewTrafficLightGroup);
    }
    TrafficLightGroup = TrafficGroups[JunctionId];

    // Search/create controller in the junction
    if(!TrafficControllers.Contains(ControllerId.c_str()))
    {
      auto *NewTrafficLightController = NewObject<UTrafficLightController>();
      NewTrafficLightController->SetControllerId(ControllerId.c_str());
      TrafficLightGroup->AddController(NewTrafficLightController);
      TrafficControllers.Add(ControllerId.c_str(), NewTrafficLightController);
    }
    TrafficLightController = TrafficControllers[ControllerId.c_str()];
  }
  else
  {
    FActorSpawnParameters SpawnParams;
    SpawnParams.OverrideLevel = GM->GetULevelFromName("TrafficLights");
    auto * NewTrafficLightGroup =
          GetWorld()->SpawnActor<ATrafficLightGroup>(SpawnParams);
    NewTrafficLightGroup->JunctionId = TrafficLightGroupMissingId;
    TrafficGroups.Add(NewTrafficLightGroup->JunctionId, NewTrafficLightGroup);
    TrafficLightGroup = NewTrafficLightGroup;

    auto *NewTrafficLightController = NewObject<UTrafficLightController>();
    NewTrafficLightController->SetControllerId(FString::FromInt(TrafficLightControllerMissingId));
    NewTrafficLightController->SetRedTime(10);
    TrafficLightGroup->GetControllers().Add(NewTrafficLightController);
    TrafficControllers.Add(NewTrafficLightController->GetControllerId(), NewTrafficLightController);
    TrafficLightController = NewTrafficLightController;

    --TrafficLightGroupMissingId;
    --TrafficLightControllerMissingId;
  }

  TrafficLightController->AddTrafficLight(TrafficLightComponent);
  TrafficLightController->ResetState();
  TrafficSignComponents.Add(TrafficLightComponent->GetSignId(), TrafficLightComponent);

  TrafficLightGroup->ResetGroup();

}

void ATrafficLightManager::RegisterLightComponentGenerated(UTrafficLightComponent * TrafficLight)
{
  auto* Controller = TrafficLight->GetController();
  auto* Group = TrafficLight->GetGroup();
  if (!Controller || !Group)
  {
    UE_LOG(LogCarla, Error, TEXT("Missing group or controller for traffic light"));
    return;
  }
  if (TrafficLight->GetSignId() == "")
  {
    TrafficLight->SetSignId(FString::FromInt(TrafficLightComponentMissingId));
    --TrafficLightComponentMissingId;
  }
  if (Controller->GetControllerId() == "")
  {
    Controller->SetControllerId(FString::FromInt(TrafficLightControllerMissingId));
    --TrafficLightControllerMissingId;
  }
  if (Group->GetJunctionId() == -1)
  {
    Group->JunctionId = TrafficLightGroupMissingId;
    --TrafficLightGroupMissingId;
  }

  if (!TrafficControllers.Contains(Controller->GetControllerId()))
  {
    TrafficControllers.Add(Controller->GetControllerId(), Controller);
  }
  if (!TrafficGroups.Contains(Group->GetJunctionId()))
  {
    TrafficGroups.Add(Group->GetJunctionId(), Group);
  }
  if (!TrafficSignComponents.Contains(TrafficLight->GetSignId()))
  {
    TrafficSignComponents.Add(TrafficLight->GetSignId(), TrafficLight);
  }
}

const std::optional<carla::road::Map>& ATrafficLightManager::GetMap()
{
  return UCarlaStatics::GetGameMode(GetWorld())->GetMap();
}

void ATrafficLightManager::GenerateSignalsAndTrafficLights()
{
  if(!TrafficLightsGenerated)
  {
    if(!TrafficLightModel_RHT || !TrafficLightModel_LHT )
    {
      UE_LOG(LogCarla, Error, TEXT("Missing TrafficLightModel"));
      return;
    }

    RemoveRoadrunnerProps();

    SpawnTrafficLights();

    SpawnSignals();

    TrafficLightsGenerated = true;
  }
}

void ATrafficLightManager::RemoveGeneratedSignalsAndTrafficLights()
{
  for(auto& Sign : TrafficSigns)
  {
    Sign->Destroy();
  }
  TrafficSigns.Empty();
  GeneratedSignals.Empty();

  for(auto& TrafficGroup : TrafficGroups)
  {
    TrafficGroup.Value->Destroy();
  }
  TrafficGroups.Empty();

  TrafficControllers.Empty();

  TrafficLightsGenerated = false;
}

void ATrafficLightManager::MatchTrafficLightActorsWithOpenDriveSignals()
{
  TArray<AActor*> Actors;
  UGameplayStatics::GetAllActorsOfClass(GetWorld(), ATrafficLightBase::StaticClass(), Actors);

  std::string opendrive_xml = carla::rpc::FromFString(UOpenDrive::GetXODR(GetWorld()));
  auto Map = carla::opendrive::OpenDriveParser::Load(opendrive_xml);

  if (!Map)
  {
    carla::log_warning("Map not found");
    return;
  }

  TArray<ATrafficLightBase*> TrafficLights;
  for (AActor* Actor : Actors)
  {
    ATrafficLightBase* TrafficLight = Cast<ATrafficLightBase>(Actor);
    if (TrafficLight)
    {
      TrafficLight->GetTrafficLightComponent()->SetSignId("");
      TrafficLights.Add(TrafficLight);
    }
  }

  if (!TrafficLights.Num())
  {
    carla::log_warning("No actors in the map");
    return;
  }

  const auto& Signals = Map->GetSignals();
  const auto& Controllers = Map->GetControllers();

  for(const auto& Signal : Signals) {
    const auto& ODSignal = Signal.second;
    const FTransform Transform = ODSignal->GetTransform();
    const FVector Location = Transform.GetLocation();
    if (ODSignal->GetName() == "")
    {
      continue;
    }
    ATrafficLightBase* ClosestActor = TrafficLights.Top();
    float MinDistance = FVector::DistSquaredXY(TrafficLights.Top()->GetActorLocation(), Location);
    for (ATrafficLightBase* Actor : TrafficLights)
    {
      float Distance = FVector::DistSquaredXY(Actor->GetActorLocation(), Location);
      if (Distance < MinDistance)
      {
        MinDistance = Distance;
        ClosestActor = Actor;
      }
    }
    ATrafficLightBase* TrafficLight = ClosestActor;
    auto* Component = TrafficLight->GetTrafficLightComponent();
    if (Component->GetSignId() == "")
    {
      Component->SetSignId(carla::rpc::ToFString(ODSignal->GetSignalId()));
    }
    else
    {
      carla::log_warning("Could not find a suitable traffic light for signal", ODSignal->GetSignalId(),
          "Closest traffic light has id", carla::rpc::FromFString(Component->GetSignId()));
    }

  }
}

void ATrafficLightManager::InitializeTrafficLights()
{
  if (!GetMap())
  {
    carla::log_warning("Coud not generate traffic lights: missing map.");
    return;
  }

  FString MapName = GetWorld()->GetMapName();
  FString XODRPath = UOpenDrive::FindPathToXODRFile(MapName);
  bool bHasMapLogic = false;

  if (!XODRPath.IsEmpty())
  {
    FString DirectoryPath;
    FString Filename, Extension;
    FPaths::Split(XODRPath, DirectoryPath, Filename, Extension);
    FString JsonFilePath = FPaths::Combine(DirectoryPath, TEXT("map_logic.json"));
    bHasMapLogic = FPlatformFileManager::Get().GetPlatformFile().FileExists(*JsonFilePath);
  }

  if (!TrafficLightsGenerated)
  {
    if (bHasMapLogic)
    {
      RemoveRoadrunnerProps();
      SpawnSignals();
      TrafficLightsGenerated = true;
    }
    else
    {
      GenerateSignalsAndTrafficLights();
    }
  }

  if (!XODRPath.IsEmpty() && bHasMapLogic)
  {
    UMapLogicParser::ApplyLaneIdsFromMapLogic(XODRPath, this);
  }
}

bool MatchSignalAndActor(const carla::road::Signal &Signal, ATrafficSignBase* ClosestTrafficSign)
{
  namespace cr = carla::road;
  if (ClosestTrafficSign)
  {
    if ((Signal.GetType() == cr::SignalType::StopSign()) &&
        ClosestTrafficSign->GetTrafficSignState() == ETrafficSignState::StopSign)
    {
      return true;
    }
    else if ((Signal.GetType() == cr::SignalType::YieldSign()) &&
        ClosestTrafficSign->GetTrafficSignState() == ETrafficSignState::YieldSign)
    {
      return true;
    }
    else if (cr::SignalType::IsTrafficLight(Signal.GetType()))
    {
      if (ClosestTrafficSign->GetTrafficSignState() == ETrafficSignState::TrafficLightRed ||
          ClosestTrafficSign->GetTrafficSignState() == ETrafficSignState::TrafficLightYellow ||
          ClosestTrafficSign->GetTrafficSignState() == ETrafficSignState::TrafficLightGreen)
        return true;
    }
    else if (Signal.GetType() == cr::SignalType::MaximumSpeed())
    {
      if (Signal.GetSubtype() == "30" &&
        ClosestTrafficSign->GetTrafficSignState() == ETrafficSignState::SpeedLimit_30)
      {
        return true;
      }
      else if (Signal.GetSubtype() == "40" &&
        ClosestTrafficSign->GetTrafficSignState() == ETrafficSignState::SpeedLimit_40)
      {
        return true;
      }
      else if (Signal.GetSubtype() == "50" &&
        ClosestTrafficSign->GetTrafficSignState() == ETrafficSignState::SpeedLimit_50)
      {
        return true;
      }
      else if (Signal.GetSubtype() == "60" &&
        ClosestTrafficSign->GetTrafficSignState() == ETrafficSignState::SpeedLimit_60)
      {
        return true;
      }
      else if (Signal.GetSubtype() == "70" &&
        ClosestTrafficSign->GetTrafficSignState() == ETrafficSignState::SpeedLimit_60)
      {
        return true;
      }
      else if (Signal.GetSubtype() == "80" &&
        ClosestTrafficSign->GetTrafficSignState() == ETrafficSignState::SpeedLimit_90)
      {
        return true;
      }
      else if (Signal.GetSubtype() == "90" &&
        ClosestTrafficSign->GetTrafficSignState() == ETrafficSignState::SpeedLimit_90)
      {
        return true;
      }
      else if (Signal.GetSubtype() == "100" &&
        ClosestTrafficSign->GetTrafficSignState() == ETrafficSignState::SpeedLimit_100)
      {
        return true;
      }
      else if (Signal.GetSubtype() == "120" &&
        ClosestTrafficSign->GetTrafficSignState() == ETrafficSignState::SpeedLimit_120)
      {
        return true;
      }
      else if (Signal.GetSubtype() == "130" &&
        ClosestTrafficSign->GetTrafficSignState() == ETrafficSignState::SpeedLimit_130)
      {
        return true;
      }
    }
  }
  return false;
}

template<typename T = ATrafficSignBase>
T * GetClosestTrafficSignActor(const carla::road::Signal &Signal, UWorld* World)
{
  auto CarlaTransform = Signal.GetTransform();
  FTransform UETransform(CarlaTransform);
  FVector Location = UETransform.GetLocation();
  // max distance to match 500cm
  constexpr float MaxDistanceMatchSqr = 250000.0;
  T * ClosestTrafficSign = nullptr;
  TArray<AActor*> Actors;
  UGameplayStatics::GetAllActorsOfClass(World, T::StaticClass(), Actors);
  float MinDistance = MaxDistanceMatchSqr;
  for (AActor* Actor : Actors)
  {
    float Dist = FVector::DistSquared(Actor->GetActorLocation(), Location);
    T * TrafficSign = Cast<T>(Actor);
    if (Dist < MinDistance && MatchSignalAndActor(Signal, TrafficSign))
    {
      ClosestTrafficSign = TrafficSign;
      MinDistance = Dist;
    }
  }
  return ClosestTrafficSign;
}

// Number of driving lanes on the same side of the road as the given lane. That is the span an
// approach's mast arm reaches over, and so the number of signal heads to hang from it (a real mast
// arm carries a head over each lane). Never returns less than one.
static int32 CountDrivingLanesOnSide(const carla::road::Lane &Lane)
{
  const carla::road::LaneSection *Section = Lane.GetLaneSection();
  if (Section == nullptr)
  {
    return 1;
  }
  const bool bRightSide = Lane.GetId() < 0; // OpenDRIVE: negative ids right of the reference line
  int32 Count = 0;
  for (const auto &Pair : Section->GetLanes())
  {
    const carla::road::Lane &Other = Pair.second;
    if (Other.GetId() == 0 || Other.GetType() != carla::road::Lane::LaneType::Driving)
    {
      continue;
    }
    if ((Other.GetId() < 0) == bRightSide)
    {
      ++Count;
    }
  }
  return FMath::Max(Count, 1);
}

// Lets traffic drive through a signal actor spawned from OpenDRIVE.
//
// These poles are placed from map geometry, so a mast arm can end up hanging low enough over the
// carriageway that a tall vehicle does not clear its heads, and the pole's ground base sits close
// enough to the kerb to catch a wheel. Either way a prop whose only jobs are to be seen and to
// trigger its stop line ends up wedging traffic.
//
// A vehicle touches the world in two independent ways, so both have to be dealt with:
//
//   - The chassis is a simulating rigid body. Query-only collision removes its contact with the
//     prop (Chaos generates none unless both bodies simulate), which is what clears the mast arm
//     hanging over the lane.
//   - The wheels are not. UChaosWheeledVehicleSimulation::PerformSuspensionTraces finds the surface
//     under each wheel with spatial queries, and query-only collision deliberately keeps a body in
//     the query tree - that is the whole point of the mode. So until the channel those queries run
//     on is ignored, every wheel still climbs the pole and its ground base even though the body of
//     the vehicle passes through.
//
// The rest of the response container is left as the blueprint set it, and the collision mode stays
// query-only rather than the NoCollision profile, which would drop the props out of every query at
// once - including the camera and visibility traces, and the walker capsule sweeps that let
// pedestrians path around a pole.
static void MakeSignalMeshesPassThrough(AActor *SignalActor)
{
  TArray<UPrimitiveComponent *> Primitives;
  SignalActor->GetComponents(Primitives);
  for (auto *Primitive : Primitives)
  {
    // Shape components are the sign's detection volumes: overlap-only already, and the sign stops
    // working without them.
    if (Primitive->IsA<UShapeComponent>())
    {
      continue;
    }
    Primitive->SetCollisionEnabled(ECollisionEnabled::QueryOnly);
    // PerformSuspensionTraces hard-codes WorldDynamic as its query channel.
    Primitive->SetCollisionResponseToChannel(ECC_WorldDynamic, ECR_Ignore);
  }
}

// Rendering-only visibility for one signal actor's meshes. Shape components are skipped: they are
// the stop-line trigger volumes, they never render, and the sign stops detecting vehicles if they
// are disturbed.
static void SetSignalMeshesVisible(AActor *SignalActor, bool bVisible)
{
  TArray<UPrimitiveComponent *> Primitives;
  SignalActor->GetComponents(Primitives);
  for (auto *Primitive : Primitives)
  {
    if (!Primitive->IsA<UShapeComponent>())
    {
      Primitive->SetVisibility(bVisible);
    }
  }
}

void ATrafficLightManager::RegisterGeneratedSignal(AActor *SignalActor)
{
  if (!IsValid(SignalActor))
  {
    return;
  }
  MakeSignalMeshesPassThrough(SignalActor);
  GeneratedSignals.Add(SignalActor);
  // Signals can be regenerated while the props are hidden; match the state the world is already in
  // rather than popping back into view.
  if (!bGeneratedSignalsVisible)
  {
    SetSignalMeshesVisible(SignalActor, false);
  }
}

void ATrafficLightManager::SetGeneratedSignalsVisible(bool bVisible)
{
  bGeneratedSignalsVisible = bVisible;
  for (AActor *Signal : GeneratedSignals)
  {
    if (IsValid(Signal))
    {
      SetSignalMeshesVisible(Signal, bVisible);
    }
  }
}

void ATrafficLightManager::SpawnTrafficLights()
{
  namespace cr = carla::road;
  const auto& Signals = GetMap()->GetSignals();
  std::unordered_set<std::string> SignalsToSpawn;
  for(const auto& ControllerPair : GetMap()->GetControllers())
  {
    const auto& Controller = ControllerPair.second;
    for(const auto& SignalId : Controller->GetSignals())
    {
      auto& Signal = Signals.at(SignalId);
      if (!cr::SignalType::IsTrafficLight(Signal->GetType()))
      {
        continue;
      }
      ATrafficLightBase * TrafficLight = GetClosestTrafficSignActor<ATrafficLightBase>(*Signal, GetWorld());
      if (TrafficLight)
      {
        UTrafficLightComponent *TrafficLightComponent = TrafficLight->GetTrafficLightComponent();
        TrafficLightComponent->SetSignId(SignalId.c_str());
      }
      else
      {
        SignalsToSpawn.insert(SignalId);
      }
    }
  }

  for(const auto& SignalPair : Signals)
  {
    const auto& SignalId = SignalPair.first;
    const auto& Signal = SignalPair.second;
    if(!Signal->GetControllers().size() &&
       !GetMap()->IsJunction(Signal->GetRoadId()) &&
       carla::road::SignalType::IsTrafficLight(Signal->GetType()) &&
       !SignalsToSpawn.count(SignalId))
    {
      ATrafficLightBase * TrafficLight = GetClosestTrafficSignActor<ATrafficLightBase>(*Signal, GetWorld());
      if (TrafficLight)
      {
        UTrafficLightComponent *TrafficLightComponent = TrafficLight->GetTrafficLightComponent();
        TrafficLightComponent->SetSignId(SignalId.c_str());
      }
      else
      {
        SignalsToSpawn.insert(SignalId);
      }
    }
  }

  ACarlaGameModeBase *GM = UCarlaStatics::GetGameMode(GetWorld());
  check(GM);
  for(auto &SignalId : SignalsToSpawn)
  {
    // TODO: should this be an assert?
    // RELEASE_ASSERT(
    //     Signals.count(SignalId) > 0,
    //     "Reference to inexistent signal. Possible OpenDRIVE error.");
    if (Signals.count(SignalId) == 0)
    {
      UE_LOG(LogCarla, Warning,
          TEXT("Possible OpenDRIVE error, reference to nonexistent signal id: %s"),
          *carla::rpc::ToFString(SignalId));
      continue;
    }
    const auto& Signal = Signals.at(SignalId);
    auto CarlaTransform = Signal->GetTransform();
    auto ClosestWaypointToSignal =
        GetMap()->GetClosestWaypointOnRoad(CarlaTransform.location);

    const auto &SignalLane = GetMap()->GetLane(ClosestWaypointToSignal.value());
    const bool IsRHT = SignalLane.GetRoad()->IsRHT();
    const int32 NumSignalHeads = CountDrivingLanesOnSide(SignalLane);

    FTransform SpawnTransform(CarlaTransform);

    FVector SpawnLocation = SpawnTransform.GetLocation();
    FRotator SpawnRotation(SpawnTransform.GetRotation());
    // Blueprints are all rotated by 90 degrees
    SpawnRotation.Yaw += 90;
    // Remove road inclination
    SpawnRotation.Roll = 0;
    SpawnRotation.Pitch = 0;

    FActorSpawnParameters SpawnParams;
    SpawnParams.Owner = this;
    SpawnParams.SpawnCollisionHandlingOverride =
        ESpawnActorCollisionHandlingMethod::AlwaysSpawn;
    SpawnParams.OverrideLevel = GM->GetULevelFromName("TrafficLights");
    // Hold the construction script until the per-approach head count is set, so the Blueprint builds
    // the mast arm with one head per lane rather than with its default.
    SpawnParams.bDeferConstruction = true;

    auto TrafficLightModel = IsRHT ? TrafficLightModel_RHT : TrafficLightModel_LHT;
    ATrafficLightBase * TrafficLight = GetWorld()->SpawnActor<ATrafficLightBase>(
        TrafficLightModel,
        SpawnLocation,
        SpawnRotation,
        SpawnParams);

    TrafficLight->NumSignalHeads = NumSignalHeads;
    TrafficLight->FinishSpawning(FTransform(SpawnRotation, SpawnLocation));

    TrafficSigns.Add(TrafficLight);

    UTrafficLightComponent *TrafficLightComponent = TrafficLight->GetTrafficLightComponent();
    TrafficLightComponent->SetSignId(SignalId.c_str());

    RegisterGeneratedSignal(TrafficLight);

    if (ClosestWaypointToSignal)
    {
      auto SignalDistanceToRoad =
          (GetMap()->ComputeTransform(ClosestWaypointToSignal.value()).location - CarlaTransform.location).Length();
      double LaneWidth = GetMap()->GetLaneWidth(ClosestWaypointToSignal.value());

      if(SignalDistanceToRoad < LaneWidth * 0.5)
      {
        carla::log_warning("Traffic light",
            TCHAR_TO_UTF8(*TrafficLightComponent->GetSignId()),
            "overlaps a driving lane.");
      }
    }

    RegisterLightComponentFromOpenDRIVE(TrafficLightComponent);
    TrafficLightComponent->InitializeSign(GetMap().value());
  }
}

void ATrafficLightManager::SpawnSignals()
{
  ACarlaGameModeBase *GM = UCarlaStatics::GetGameMode(GetWorld());
  check(GM);

  const auto &Signals = GetMap()->GetSignals();
  for (auto& SignalPair : Signals)
  {
    auto &Signal = SignalPair.second;
    FString SignalType = Signal->GetType().c_str();

    ATrafficSignBase * ClosestTrafficSign = GetClosestTrafficSignActor(*Signal, GetWorld());
    if (ClosestTrafficSign)
    {
      USignComponent *SignComponent;
      if (SignComponentModels.Contains(SignalType))
      {
          SignComponent =
              NewObject<USignComponent>(
              ClosestTrafficSign, SignComponentModels[SignalType]);
      }
      else
      {
        SignComponent =
              NewObject<USignComponent>(
              ClosestTrafficSign);
      }
      SignComponent->SetSignId(Signal->GetSignalId().c_str());
      SignComponent->RegisterComponent();
      SignComponent->AttachToComponent(
          ClosestTrafficSign->GetRootComponent(),
          FAttachmentTransformRules::KeepRelativeTransform);
      TrafficSignComponents.Add(SignComponent->GetSignId(), SignComponent);
      TrafficSigns.Add(ClosestTrafficSign);
    }
    else if (TrafficSignsModels.Contains(SignalType))
    {
      // We do not spawn stops painted in the ground
      if (Signal->GetName() == "Stencil_STOP")
      {
        continue;
      }
      auto CarlaTransform = Signal->GetTransform();
      FTransform SpawnTransform(CarlaTransform);
      FVector SpawnLocation = SpawnTransform.GetLocation();
      FRotator SpawnRotation(SpawnTransform.GetRotation());
      SpawnRotation.Yaw += 90;
      // Remove road inclination
      SpawnRotation.Roll = 0;
      SpawnRotation.Pitch = 0;

      FActorSpawnParameters SpawnParams;
      SpawnParams.Owner = this;
      SpawnParams.SpawnCollisionHandlingOverride =
          ESpawnActorCollisionHandlingMethod::AlwaysSpawn;
      SpawnParams.OverrideLevel = GM->GetULevelFromName("TrafficSigns");
      ATrafficSignBase * TrafficSign = GetWorld()->SpawnActor<ATrafficSignBase>(
          TrafficSignsModels[SignalType],
          SpawnLocation,
          SpawnRotation,
          SpawnParams);

      USignComponent *SignComponent =
          NewObject<USignComponent>(TrafficSign, SignComponentModels[SignalType]);
      SignComponent->SetSignId(Signal->GetSignalId().c_str());
      SignComponent->RegisterComponent();
      SignComponent->AttachToComponent(
          TrafficSign->GetRootComponent(),
          FAttachmentTransformRules::KeepRelativeTransform);
      SignComponent->InitializeSign(GetMap().value());

      RegisterGeneratedSignal(TrafficSign);

      auto ClosestWaypointToSignal =
          GetMap()->GetClosestWaypointOnRoad(CarlaTransform.location);
      if (ClosestWaypointToSignal)
      {
        auto SignalDistanceToRoad =
            (GetMap()->ComputeTransform(ClosestWaypointToSignal.value()).location - CarlaTransform.location).Length();
        double LaneWidth = GetMap()->GetLaneWidth(ClosestWaypointToSignal.value());

        if(SignalDistanceToRoad < LaneWidth * 0.5)
        {
          carla::log_warning("Traffic sign",
              TCHAR_TO_UTF8(*SignComponent->GetSignId()),
              "overlaps a driving lane.");
        }
      }
      TrafficSignComponents.Add(SignComponent->GetSignId(), SignComponent);
      TrafficSigns.Add(TrafficSign);
    }
    else if (Signal->GetType() == carla::road::SignalType::MaximumSpeed() &&
            SpeedLimitModels.Contains(Signal->GetSubtype().c_str()))
    {
      auto CarlaTransform = Signal->GetTransform();
      FTransform SpawnTransform(CarlaTransform);
      FVector SpawnLocation = SpawnTransform.GetLocation();
      FRotator SpawnRotation(SpawnTransform.GetRotation());
      SpawnRotation.Yaw += 90;
      // Remove road inclination
      SpawnRotation.Roll = 0;
      SpawnRotation.Pitch = 0;

      FActorSpawnParameters SpawnParams;
      SpawnParams.Owner = this;
      SpawnParams.SpawnCollisionHandlingOverride =
          ESpawnActorCollisionHandlingMethod::AlwaysSpawn;
      SpawnParams.OverrideLevel = GM->GetULevelFromName("TrafficSigns");
      ATrafficSignBase * TrafficSign = GetWorld()->SpawnActor<ATrafficSignBase>(
          SpeedLimitModels[Signal->GetSubtype().c_str()],
          SpawnLocation,
          SpawnRotation,
          SpawnParams);

      USpeedLimitComponent *SignComponent =
          NewObject<USpeedLimitComponent>(TrafficSign);
      SignComponent->SetSignId(Signal->GetSignalId().c_str());
      SignComponent->RegisterComponent();
      SignComponent->AttachToComponent(
          TrafficSign->GetRootComponent(),
          FAttachmentTransformRules::KeepRelativeTransform);
      SignComponent->InitializeSign(GetMap().value());
      SignComponent->SetSpeedLimit(Signal->GetValue());

      RegisterGeneratedSignal(TrafficSign);

      auto ClosestWaypointToSignal =
          GetMap()->GetClosestWaypointOnRoad(CarlaTransform.location);
      if (ClosestWaypointToSignal)
      {
        auto SignalDistanceToRoad =
            (GetMap()->ComputeTransform(ClosestWaypointToSignal.value()).location - CarlaTransform.location).Length();
        double LaneWidth = GetMap()->GetLaneWidth(ClosestWaypointToSignal.value());

        if(SignalDistanceToRoad < LaneWidth * 0.5)
        {
          carla::log_warning("Traffic sign",
              TCHAR_TO_UTF8(*SignComponent->GetSignId()),
              "overlaps a driving lane.");
        }
      }
      TrafficSignComponents.Add(SignComponent->GetSignId(), SignComponent);
      TrafficSigns.Add(TrafficSign);
    }
  }
}

void ATrafficLightManager::SetFrozen(bool InFrozen)
{
  bTrafficLightsFrozen = InFrozen;
  if (bTrafficLightsFrozen)
  {
    for (auto& TrafficGroupPair : TrafficGroups)
    {
      auto* TrafficGroup = TrafficGroupPair.Value;
      TrafficGroup->SetFrozenGroup(true);
    }
  }
  else
  {
    for (auto& TrafficGroupPair : TrafficGroups)
    {
      auto* TrafficGroup = TrafficGroupPair.Value;
      TrafficGroup->SetFrozenGroup(false);
    }
  }
}

bool ATrafficLightManager::GetFrozen()
{
  return bTrafficLightsFrozen;
}

ATrafficLightGroup* ATrafficLightManager::GetTrafficGroup(carla::road::JuncId JunctionId)
{
  if (TrafficGroups.Contains(JunctionId))
  {
    return TrafficGroups[JunctionId];
  }
  return nullptr;
}


UTrafficLightController* ATrafficLightManager::GetController(FString ControllerId)
{
  if (TrafficControllers.Contains(ControllerId))
  {
    return TrafficControllers[ControllerId];
  }
  return nullptr;
}

USignComponent* ATrafficLightManager::GetTrafficSign(FString SignId)
{
  if (!TrafficSignComponents.Contains(SignId))
  {
    return nullptr;
  }
  return TrafficSignComponents[SignId];
}

void ATrafficLightManager::RemoveRoadrunnerProps() const
{
    TArray<AActor*> Actors;
    UGameplayStatics::GetAllActorsOfClass(GetWorld(), AActor::StaticClass(), Actors);

    // Detect PropsNode Actor which is the father of all the Props imported from Roadrunner
    AActor* PropsNode = nullptr;
    for(AActor* Actor : Actors)
    {
      const FString Name = UKismetSystemLibrary::GetDisplayName(Actor);
      if(Name.Equals("PropsNode"))
      {
        PropsNode = Actor;
        break;
      }
    }

    if(PropsNode)
    {
      PropsNode->GetAttachedActors(Actors, true);
      RemoveAttachedProps(Actors);
      PropsNode->Destroy();
    }

}

void ATrafficLightManager::RemoveAttachedProps(TArray<AActor*> Actors) const
{
  for(AActor* Actor : Actors)
  {
    TArray<AActor*> AttachedActors;
    Actor->GetAttachedActors(AttachedActors, true);
    RemoveAttachedProps(AttachedActors);
    Actor->Destroy();
  }
}
