// Copyright (c) 2026 Computer Vision Center (CVC) at the Universitat Autonoma
// de Barcelona (UAB).
//
// This work is licensed under the terms of the MIT license.
// For a copy, see <https://opensource.org/licenses/MIT>.

#include "Carla/Sensor/WorldObserver.h"
#include "Carla.h"
#include "Carla/Actor/ActorData.h"
#include "Carla/Actor/ActorRegistry.h"
#include "Carla/Actor/ActorSupervision.h"
#include "Carla/Actor/CarlaActor.h"
#include "Carla/Actor/RenderSetMembership.h"
#include "Carla/Game/CarlaEpisode.h"
#include "Carla/Game/WorldSupervisionState.h"
#include "Carla/Game/CarlaEngine.h"
#include "Carla/Traffic/TrafficLightBase.h"
#include "Carla/Traffic/TrafficLightComponent.h"
#include "Carla/Traffic/TrafficLightController.h"
#include "Carla/Traffic/TrafficLightGroup.h"
#include "Carla/Traffic/TrafficSignBase.h"
#include "Carla/Traffic/SignComponent.h"
#include "Carla/Walker/WalkerController.h"
#include "CesiumHeightSampler.h"

#include <util/disable-ue4-macros.h>
#include <carla/rpc/String.h>
#include <carla/sensor/SensorRegistry.h>
#include <carla/sensor/data/ActorDynamicState.h>
#include <carla/sensor/s11n/EpisodeStateSerializer.h>
#include <util/enable-ue4-macros.h>

#include <util/ue-header-guard-begin.h>
#include "CoreGlobals.h"
#include <util/ue-header-guard-end.h>

#include <algorithm>
#include <cstdint>
#include <cstring>
#include <limits>
#include <string>
#include <vector>

static auto FWorldObserver_GetActorState(const FCarlaActor &View, const FActorRegistry &Registry)
{
  using AType = FCarlaActor::ActorType;

  carla::sensor::data::ActorDynamicState::TypeDependentState state{};

  if (AType::Vehicle == View.GetActorType())
  {
    auto Vehicle = Cast<ACarlaWheeledVehicle>(View.GetActor());
    if (Vehicle != nullptr)
    {
      state.vehicle_data.control = carla::rpc::VehicleControl{Vehicle->GetVehicleControl()};
      auto Controller = Cast<AWheeledVehicleAIController>(Vehicle->GetController());
      if (Controller != nullptr)
      {
        using TLS = carla::rpc::TrafficLightState;
        state.vehicle_data.traffic_light_state = static_cast<TLS>(Controller->GetTrafficLightState());
        state.vehicle_data.speed_limit = Controller->GetSpeedLimit();
        auto TrafficLight = Controller->GetTrafficLight();
        if (TrafficLight != nullptr)
        {
          state.vehicle_data.has_traffic_light = true;
          auto* TrafficLightView = Registry.FindCarlaActor(TrafficLight);
          if(TrafficLightView)
          {
            state.vehicle_data.traffic_light_id = TrafficLightView->GetActorId();
          }
          else
          {
            state.vehicle_data.has_traffic_light = false;
          }
        }
        else
        {
          state.vehicle_data.has_traffic_light = false;
        }
      }
      // Get the failure state by checking the rollover one as it is the only one currently implemented.
      // This will have to be expanded once more states are added
      state.vehicle_data.failure_state = Vehicle->GetFailureState();
    }
  }

  else if (AType::Walker == View.GetActorType())
  {
    auto Walker = Cast<APawn>(View.GetActor());
    auto Controller = Walker != nullptr ? Cast<AWalkerController>(Walker->GetController()) : nullptr;
    if (Controller != nullptr)
    {
      state.walker_control = carla::rpc::WalkerControl{Controller->GetWalkerControl()};
    }
  }
  else if (AType::TrafficLight == View.GetActorType())
  {
    auto TrafficLight = Cast<ATrafficLightBase>(View.GetActor());
    if (TrafficLight != nullptr)
    {
      auto* TrafficLightComponent =
          TrafficLight->GetTrafficLightComponent();

      using TLS = carla::rpc::TrafficLightState;

      if(TrafficLightComponent == nullptr)
      {
        // Old way: traffic lights are actors
        state.traffic_light_data.sign_id[0] = '\0';
        state.traffic_light_data.state = static_cast<TLS>(TrafficLight->GetTrafficLightState());
        state.traffic_light_data.green_time = TrafficLight->GetGreenTime();
        state.traffic_light_data.yellow_time = TrafficLight->GetYellowTime();
        state.traffic_light_data.red_time = TrafficLight->GetRedTime();
        state.traffic_light_data.elapsed_time = TrafficLight->GetElapsedTime();
        state.traffic_light_data.time_is_frozen = TrafficLight->GetTimeIsFrozen();
        state.traffic_light_data.pole_index = TrafficLight->GetPoleIndex();
      }
      else
      {
        const UTrafficLightController* Controller =  TrafficLightComponent->GetController();
        const ATrafficLightGroup* Group = TrafficLightComponent->GetGroup();

        if (!Controller)
        {
          UE_LOG(LogCarla, Error, TEXT("TrafficLightComponent doesn't have any Controller assigned"));
        }
        else if (!Group)
        {
          UE_LOG(LogCarla, Error, TEXT("TrafficLightComponent doesn't have any Group assigned"));
        }
        else
        {
          const FString fstring_sign_id = TrafficLightComponent->GetSignId();
          const std::string sign_id = carla::rpc::FromFString(fstring_sign_id);
          constexpr size_t max_size = sizeof(state.traffic_light_data.sign_id);
          size_t sign_id_length = sign_id.length();
          if(max_size < sign_id_length)
          {
            UE_LOG(LogCarla, Warning, TEXT("The max size of a signal id is 32. %s (%d)"), *fstring_sign_id, sign_id.length());
            sign_id_length = max_size;
          }
          std::memset(state.traffic_light_data.sign_id, '\0', max_size);
          std::memcpy(state.traffic_light_data.sign_id, sign_id.c_str(), sign_id_length);
          state.traffic_light_data.state = static_cast<TLS>(TrafficLightComponent->GetLightState());
          state.traffic_light_data.green_time = Controller->GetGreenTime();
          state.traffic_light_data.yellow_time = Controller->GetYellowTime();
          state.traffic_light_data.red_time = Controller->GetRedTime();
          state.traffic_light_data.elapsed_time = Controller->GetElapsedTime();
          state.traffic_light_data.time_is_frozen = Group->IsFrozen();
          state.traffic_light_data.pole_index = TrafficLight->GetPoleIndex();
        }
      }
    }
  }
  else if (AType::TrafficSign == View.GetActorType())
  {
    auto TrafficSign = Cast<ATrafficSignBase>(View.GetActor());
    if (TrafficSign != nullptr)
    {
      USignComponent* TrafficSignComponent =
        Cast<USignComponent>(TrafficSign->FindComponentByClass<USignComponent>());

      if(TrafficSignComponent)
      {
        const FString fstring_sign_id = TrafficSignComponent->GetSignId();
        const std::string sign_id = carla::rpc::FromFString(fstring_sign_id);
        constexpr size_t max_size = sizeof(state.traffic_sign_data.sign_id);
        size_t sign_id_length = sign_id.length();
        if(max_size < sign_id_length)
        {
          UE_LOG(LogCarla, Warning, TEXT("The max size of a signal id is 32. %s (%d)"), *fstring_sign_id, sign_id.length());
          sign_id_length = max_size;
        }
        std::memset(state.traffic_light_data.sign_id, '\0', max_size);
        std::memcpy(state.traffic_sign_data.sign_id, sign_id.c_str(), sign_id_length);
      }
    }
  }
  return state;
}

static auto FWorldObserver_GetDormantActorState(const FCarlaActor &View, const FActorRegistry &Registry)
{
  using AType = FCarlaActor::ActorType;

  carla::sensor::data::ActorDynamicState::TypeDependentState state{};

  if (AType::Vehicle == View.GetActorType())
  {
      const FVehicleData* ActorData = View.GetActorData<FVehicleData>();
      state.vehicle_data.control = carla::rpc::VehicleControl{ActorData->Control};
      using TLS = carla::rpc::TrafficLightState;
      state.vehicle_data.traffic_light_state = TLS::Green;
      state.vehicle_data.speed_limit = ActorData->SpeedLimit;
      state.vehicle_data.has_traffic_light = false;
  }
  else if (AType::Walker == View.GetActorType())
  {
    const FWalkerData* ActorData = View.GetActorData<FWalkerData>();
    // auto Walker = Cast<APawn>(View.GetActor());
    // auto Controller = Walker != nullptr ? Cast<AWalkerController>(Walker->GetController()) : nullptr;
    // if (Controller != nullptr)
    // {
    //   state.walker_control = carla::rpc::WalkerControl{Controller->GetWalkerControl()};
    // }
    state.walker_control = ActorData->WalkerControl;
  }
  else if (AType::TrafficLight == View.GetActorType())
  {
    const FTrafficLightData* ActorData = View.GetActorData<FTrafficLightData>();
    const UTrafficLightController* Controller = ActorData->Controller;
    if(Controller)
    {
      using TLS = carla::rpc::TrafficLightState;
      const ATrafficLightGroup* Group = Controller->GetGroup();
      if(!Group)
      {
        UE_LOG(LogCarla, Error, TEXT("TrafficLight doesn't have any Group assigned"));
      }
      else
      {
        const FString fstring_sign_id = ActorData->SignId;
        const std::string sign_id = carla::rpc::FromFString(fstring_sign_id);
        constexpr size_t max_size = sizeof(state.traffic_light_data.sign_id);
        size_t sign_id_length = sign_id.length();
        if(max_size < sign_id_length)
        {
          UE_LOG(LogCarla, Warning, TEXT("The max size of a signal id is 32. %s (%d)"), *fstring_sign_id, sign_id.length());
          sign_id_length = max_size;
        }
        std::memset(state.traffic_light_data.sign_id, '\0', max_size);
        std::memcpy(state.traffic_light_data.sign_id, sign_id.c_str(), sign_id_length);
        state.traffic_light_data.state = static_cast<TLS>(Controller->GetCurrentState().State);
        state.traffic_light_data.green_time = Controller->GetGreenTime();
        state.traffic_light_data.yellow_time = Controller->GetYellowTime();
        state.traffic_light_data.red_time = Controller->GetRedTime();
        state.traffic_light_data.elapsed_time = Controller->GetElapsedTime();
        state.traffic_light_data.time_is_frozen = Group->IsFrozen();
        state.traffic_light_data.pole_index = ActorData->PoleIndex;
      }
    }
  }
  else if (AType::TrafficSign == View.GetActorType())
  {
    const FTrafficSignData* ActorData = View.GetActorData<FTrafficSignData>();
    const FString fstring_sign_id = ActorData->SignId;
    const std::string sign_id = carla::rpc::FromFString(fstring_sign_id);
    constexpr size_t max_size = sizeof(state.traffic_sign_data.sign_id);
    size_t sign_id_length = sign_id.length();
    if(max_size < sign_id_length)
    {
      UE_LOG(LogCarla, Warning, TEXT("The max size of a signal id is 32. %s (%d)"), *fstring_sign_id, sign_id.length());
      sign_id_length = max_size;
    }
    std::memset(state.traffic_light_data.sign_id, '\0', max_size);
    std::memcpy(state.traffic_sign_data.sign_id, sign_id.c_str(), sign_id_length);
  }
  return state;
}

static carla::geom::Vector3D FWorldObserver_GetAngularVelocity(const AActor &Actor)
{
  const auto RootComponent = Cast<UPrimitiveComponent>(Actor.GetRootComponent());
  const FVector AngularVelocity =
      RootComponent != nullptr ?
          RootComponent->GetPhysicsAngularVelocityInDegrees() :
          FVector{0.0f, 0.0f, 0.0f};
  return
  {
      (float)AngularVelocity.X,
      (float)AngularVelocity.Y,
      (float)AngularVelocity.Z
  };
}

static carla::geom::Vector3D FWorldObserver_GetAcceleration(
    const FCarlaActor &View,
    const FVector &Velocity,
    const float DeltaSeconds)
{
  FVector &PreviousVelocity = View.GetActorInfo()->Velocity;
  const FVector Acceleration = (Velocity - PreviousVelocity) / DeltaSeconds;
  PreviousVelocity = Velocity;
  return
  {
      (float)Acceleration.X,
      (float)Acceleration.Y,
      (float)Acceleration.Z
  };
}

/// The bytes of a name -- in the render set or the supervision block -- that fit its 16-bit length
/// prefix.
static uint16_t FWorldObserver_NameSize(const std::string &Name)
{
  constexpr size_t MaxSize = (std::numeric_limits<uint16_t>::max)();
  return static_cast<uint16_t>((std::min)(Name.size(), MaxSize));
}

/// How many items of a list fit its 16-bit count.
static uint16_t FWorldObserver_ListCount(size_t Count)
{
  constexpr size_t MaxCount = (std::numeric_limits<uint16_t>::max)();
  return static_cast<uint16_t>((std::min)(Count, MaxCount));
}

/// A name's size as written: its 16-bit length, then its bytes.
static size_t FWorldObserver_WrittenNameSize(const std::string &Name)
{
  return sizeof(uint16_t) + FWorldObserver_NameSize(Name);
}

/// A list of names' size as written: its 16-bit count, then each name that count covers.
static size_t FWorldObserver_WrittenNamesSize(const std::vector<std::string> &Names)
{
  size_t Size = sizeof(uint16_t);
  const uint16_t Count = FWorldObserver_ListCount(Names.size());
  for (uint16_t Index = 0u; Index < Count; ++Index)
  {
    Size += FWorldObserver_WrittenNameSize(Names[Index]);
  }
  return Size;
}

/// One render set entry's size: actor id, state, admitted frame, and the vehicle and its type, each
/// behind a 16-bit length and written only for a body that is lent
/// (EpisodeStateSerializer::RenderSetEntryState describes the layout).
static size_t FWorldObserver_RenderSetEntrySize(const FRenderSetMembership &Membership)
{
  const bool bLent = Membership.State == FRenderSetMembership::EState::Lent;
  return sizeof(uint32_t) + sizeof(uint8_t) + sizeof(uint64_t)
      + sizeof(uint16_t) + (bLent ? FWorldObserver_NameSize(Membership.VehicleId) : 0u)
      + sizeof(uint16_t) + (bLent ? FWorldObserver_NameSize(Membership.VehicleTypeId) : 0u);
}

/// One supervision row's size: actor id, state, and each annotation's instance, phase, role and
/// labels (EpisodeStateSerializer::SupervisionEntryState describes the layout).
static size_t FWorldObserver_SupervisionRowSize(const FActorSupervision &Supervision)
{
  size_t Size = sizeof(uint32_t) + sizeof(uint8_t) + sizeof(uint16_t);
  const uint16_t Count = FWorldObserver_ListCount(Supervision.Annotations.size());
  for (uint16_t Index = 0u; Index < Count; ++Index)
  {
    const FSupervisionAnnotation &Annotation = Supervision.Annotations[Index];
    Size += FWorldObserver_WrittenNameSize(Annotation.InstanceId)
        + FWorldObserver_WrittenNameSize(Annotation.Phase)
        + FWorldObserver_WrittenNameSize(Annotation.Role)
        + FWorldObserver_WrittenNamesSize(Annotation.Labels);
  }
  return Size;
}

static carla::Buffer FWorldObserver_Serialize(
    carla::Buffer &&buffer,
    const UCarlaEpisode &Episode,
    float DeltaSeconds,
    bool MapChange,
    bool PendingLightUpdates)
{
  TRACE_CPUPROFILER_EVENT_SCOPE_STR(__FUNCTION__);
  using Serializer = carla::sensor::s11n::EpisodeStateSerializer;
  using SimulationState = carla::sensor::s11n::EpisodeStateSerializer::SimulationState;
  using RenderSetEntryState = carla::sensor::s11n::EpisodeStateSerializer::RenderSetEntryState;
  using SupervisionEntryState = carla::sensor::s11n::EpisodeStateSerializer::SupervisionEntryState;
  using ActorDynamicState = carla::sensor::data::ActorDynamicState;


  const FActorRegistry &Registry = Episode.GetActorRegistry();

  // Every body a co-simulation session has named, and the supervision of every lent body whose
  // vehicle the author asserts something of, gathered before anything is written: the render set
  // block they make sits between the header and the first actor, so its size is part of the
  // buffer's. A world in which no session has named a body or bound a supervision plan carries no
  // block and is laid out as it always was.
  const FWorldSupervisionState &WorldSupervision = Episode.GetWorldSupervision();
  const bool bSupervisionCarried = WorldSupervision.IsHeld();
  TArray<const FCarlaActor *> RenderSetBodies;
  TArray<const FCarlaActor *> SupervisedBodies;
  size_t RenderSetEntriesSize = 0u;
  size_t SupervisionRowsSize = 0u;
  for (auto& Named : Registry)
  {
    const FCarlaActor* Body = Named.Value.Get();
    if (Body == nullptr)
    {
      continue;
    }
    const FRenderSetMembership &Membership = Body->GetRenderSetMembership();
    if (Membership.State != FRenderSetMembership::EState::None)
    {
      RenderSetBodies.Add(Body);
      RenderSetEntriesSize += FWorldObserver_RenderSetEntrySize(Membership);
    }
    // Only a lent body draws a vehicle the author can assert anything of, and one whose vehicle is
    // unlabelled has no row: the absence of a row is what unlabelled is.
    if (bSupervisionCarried &&
        Membership.State == FRenderSetMembership::EState::Lent &&
        Body->GetSupervision().State != FActorSupervision::EState::Unlabelled)
    {
      SupervisedBodies.Add(Body);
      SupervisionRowsSize += FWorldObserver_SupervisionRowSize(Body->GetSupervision());
    }
  }
  // The supervision block's own size, the plan, the vocabulary version and digest, then the row count
  // and rows. Every row is a lent body's: nothing is written for the world apart from the plan.
  size_t SupervisionBlockSize = 0u;
  if (bSupervisionCarried)
  {
    SupervisionBlockSize = sizeof(uint32_t)
        + FWorldObserver_WrittenNameSize(WorldSupervision.PlanId)
        + sizeof(uint32_t)
        + FWorldObserver_WrittenNameSize(WorldSupervision.VocabularyDigest)
        + sizeof(uint32_t) + SupervisionRowsSize;
  }
  // Written whenever either is carried: the supervision rides inside the render set block, whose
  // size counts it, so a reader that knows only the render set skips it unread.
  const bool bRenderSetCarried = RenderSetBodies.Num() > 0 || bSupervisionCarried;
  // The block's own size and its entry count, then the entries, then the supervision block.
  const size_t RenderSetBlockSize = bRenderSetCarried
      ? sizeof(uint32_t) + sizeof(uint32_t) + RenderSetEntriesSize + SupervisionBlockSize
      : 0u;

  auto total_size = sizeof(Serializer::Header) + RenderSetBlockSize +
      sizeof(ActorDynamicState) * Registry.Num();
  auto current_size = 0;
  // Set up buffer for writing.
  buffer.reset(total_size);
  auto write_data = [&current_size, &buffer](const auto &data)
  {
    auto begin = buffer.begin() + current_size;
    std::memcpy(begin, &data, sizeof(data));
    current_size += sizeof(data);
  };
  auto write_name = [&current_size, &buffer, &write_data](const std::string &Name)
  {
    const uint16_t Size = FWorldObserver_NameSize(Name);
    write_data(Size);
    if (Size > 0u)
    {
      std::memcpy(buffer.begin() + current_size, Name.data(), Size);
      current_size += Size;
    }
  };
  auto write_names = [&write_data, &write_name](const std::vector<std::string> &Names)
  {
    const uint16_t Count = FWorldObserver_ListCount(Names.size());
    write_data(Count);
    for (uint16_t Index = 0u; Index < Count; ++Index)
    {
      write_name(Names[Index]);
    }
  };

  constexpr float TO_METERS = 1e-2;

  // Write header.
  Serializer::Header header;
  header.episode_id = Episode.GetId();
  header.platform_timestamp = FPlatformTime::Seconds();
  header.delta_seconds = DeltaSeconds;
  FIntVector MapOrigin = Episode.GetCurrentMapOrigin();
  FIntVector MapOriginInMeters = MapOrigin / 100;
  header.map_origin = carla::geom::Vector3DInt{ MapOriginInMeters.X, MapOriginInMeters.Y, MapOriginInMeters.Z };

  uint8_t simulation_state = (SimulationState::MapChange * MapChange);
  simulation_state |= (SimulationState::PendingLightUpdate * PendingLightUpdates);
  // The layout, not the reading: the solar block is always twelve doubles wide, and a reader needs
  // to know that to find the actors that follow it.
  simulation_state |= SimulationState::SolarCorrectedElevationCarried;

  // Solar / time-of-day state, so each streamed snapshot carries the sun in effect this tick and the
  // recorder can pair frames with it straight from the observer cache (no polling). GetSolarState is
  // [solar_time, year, month, day, time_zone, lat, lon, elevation, azimuth, advancing, rate,
  // corrected_elevation], or empty when the world has no CesiumSunSky.
  //
  // A world with no sun is signalled by the SolarStateValid flag, not by the values: the header's
  // solar defaults are a well-formed reading -- midnight of year 0 at latitude 0, longitude 0 -- and
  // a reader that only checked "are there eleven numbers?" would write that non-reading into an
  // artifact as fact. GetSolarState appends the corrected elevation whenever it answers at all, so
  // a sun is valid only with all twelve: a reading missing its last value is reported as no reading
  // rather than published with a corrected elevation of zero.
  const TArray<double> Solar = UCesiumHeightSampler::GetSolarState(Episode.GetWorld());
  if (Solar.Num() >= 12)
  {
    simulation_state |= SimulationState::SolarStateValid;
    header.solar_time      = Solar[0];
    header.solar_year      = Solar[1];
    header.solar_month     = Solar[2];
    header.solar_day       = Solar[3];
    header.solar_time_zone = Solar[4];
    header.solar_lat       = Solar[5];
    header.solar_lon       = Solar[6];
    header.solar_elevation = Solar[7];
    header.solar_azimuth   = Solar[8];
    header.solar_advancing = Solar[9];
    header.solar_rate      = Solar[10];
    header.solar_corrected_elevation = Solar[11];
  }

  // Set only when the block is written, because it says where the actors start.
  if (bRenderSetCarried)
  {
    simulation_state |= SimulationState::RenderSetCarried;
  }
  // And only with it, because the supervision block lies inside it.
  if (bSupervisionCarried)
  {
    simulation_state |= SimulationState::SupervisionCarried;
  }

  header.simulation_state = static_cast<SimulationState>(simulation_state);

  write_data(header);

  // The render set: each named body, lent with the vehicle it is drawn for, or parked. What the
  // session last named is what this frame drew, because the session names a change before the tick
  // cue of the frame it is drawn in.
  if (bRenderSetCarried)
  {
    const uint32_t BlockSize = static_cast<uint32_t>(RenderSetBlockSize - sizeof(uint32_t));
    const uint32_t EntryCount = static_cast<uint32_t>(RenderSetBodies.Num());
    write_data(BlockSize);
    write_data(EntryCount);
    for (const FCarlaActor* Body : RenderSetBodies)
    {
      const FRenderSetMembership &Membership = Body->GetRenderSetMembership();
      const bool bLent = Membership.State == FRenderSetMembership::EState::Lent;
      const uint32_t EntryActorId = static_cast<uint32_t>(Body->GetActorId());
      const uint8_t EntryState = static_cast<uint8_t>(
          bLent ? RenderSetEntryState::Lent : RenderSetEntryState::Parked);
      const uint64_t EntryAdmittedFrame = bLent ? Membership.AdmittedFrame : 0u;
      write_data(EntryActorId);
      write_data(EntryState);
      write_data(EntryAdmittedFrame);
      write_name(bLent ? Membership.VehicleId : std::string());
      write_name(bLent ? Membership.VehicleTypeId : std::string());
    }

    // The supervision in force: the plan, and each lent body whose vehicle is annotated or nominal
    // with the instances in force for it. What the session last put in force is what holds on this
    // frame, because the session names a change before the tick cue of the frame it is drawn in.
    if (bSupervisionCarried)
    {
      const uint32_t SupervisionSize = static_cast<uint32_t>(SupervisionBlockSize - sizeof(uint32_t));
      write_data(SupervisionSize);
      write_name(WorldSupervision.PlanId);
      write_data(WorldSupervision.VocabularyVersion);
      write_name(WorldSupervision.VocabularyDigest);
      const uint32_t RowCount = static_cast<uint32_t>(SupervisedBodies.Num());
      write_data(RowCount);
      for (const FCarlaActor* Body : SupervisedBodies)
      {
        const FActorSupervision &Supervision = Body->GetSupervision();
        const uint32_t RowActorId = static_cast<uint32_t>(Body->GetActorId());
        const uint8_t RowState = static_cast<uint8_t>(
            Supervision.State == FActorSupervision::EState::Annotated
                ? SupervisionEntryState::Annotated
                : SupervisionEntryState::Nominal);
        const uint16_t AnnotationCount = FWorldObserver_ListCount(Supervision.Annotations.size());
        write_data(RowActorId);
        write_data(RowState);
        write_data(AnnotationCount);
        for (uint16_t Index = 0u; Index < AnnotationCount; ++Index)
        {
          const FSupervisionAnnotation &Annotation = Supervision.Annotations[Index];
          write_name(Annotation.InstanceId);
          write_name(Annotation.Phase);
          write_name(Annotation.Role);
          write_names(Annotation.Labels);
        }
      }
    }
  }

  // Write every actor.
  for (auto& It : Registry)
  {
    const FCarlaActor* View = It.Value.Get();
    const FActorInfo* ActorInfo = View->GetActorInfo();

    FTransform ActorTransform;
    FVector Velocity(0.0f);
    carla::geom::Vector3D AngularVelocity(0.0f, 0.0f, 0.0f);
    carla::geom::Vector3D Acceleration(0.0f, 0.0f, 0.0f);
    carla::sensor::data::ActorDynamicState::TypeDependentState State{};

    check(View);

    if(View->IsDormant())
    {
      const FActorData* ActorData = View->GetActorData();
      Velocity = TO_METERS * ActorData->Velocity;
      AngularVelocity = carla::geom::Vector3D
      {
          (float)ActorData->AngularVelocity.X,
          (float)ActorData->AngularVelocity.Y,
          (float)ActorData->AngularVelocity.Z
      };
      Acceleration = FWorldObserver_GetAcceleration(*View, Velocity, DeltaSeconds);
      State = FWorldObserver_GetDormantActorState(*View, Registry);
    }
    else
    {
      Velocity = TO_METERS * View->GetActor()->GetVelocity();
      AngularVelocity = FWorldObserver_GetAngularVelocity(*View->GetActor());
      Acceleration = FWorldObserver_GetAcceleration(*View, Velocity, DeltaSeconds);
      State = FWorldObserver_GetActorState(*View, Registry);
    }
    ActorTransform = View->GetActorGlobalTransform();

    ActorDynamicState info = {
      View->GetActorId(),
      View->GetActorState(),
      carla::geom::Transform(ActorTransform),
      carla::geom::Vector3D(Velocity.X, Velocity.Y, Velocity.Z),
      AngularVelocity,
      Acceleration,
      State
    };
    write_data(info);
  }

  // Shrink buffer
  buffer.resize(current_size);

  check(buffer.size() == current_size);

  return std::move(buffer);
}

void FWorldObserver::BroadcastTick(
    const UCarlaEpisode &Episode,
    float DeltaSecond,
    bool MapChange,
    bool PendingLightUpdates)
{
  TRACE_CPUPROFILER_EVENT_SCOPE_STR(__FUNCTION__);

  if (!Stream.IsStreamReady())
    return;

  auto AsyncStream = Stream.MakeAsyncDataStream(*this, Episode.GetElapsedGameTime());

  carla::Buffer buffer = FWorldObserver_Serialize(
      AsyncStream.PopBufferFromPool(),
      Episode,
      DeltaSecond,
      MapChange,
      PendingLightUpdates);

  AsyncStream.SerializeAndSend(*this, std::move(buffer));
}
