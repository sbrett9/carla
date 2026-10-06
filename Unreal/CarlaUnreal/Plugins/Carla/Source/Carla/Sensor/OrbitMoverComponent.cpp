// Copyright (c) 2026 Computer Vision Center (CVC) at the Universitat Autonoma
// de Barcelona (UAB).
//
// This work is licensed under the terms of the MIT license.
// For a copy, see <https://opensource.org/licenses/MIT>.

#include "OrbitMoverComponent.h"
#include "Carla/Game/CarlaStatics.h"
#include "Carla/MapGen/LargeMapManager.h"

#include <util/ue-header-guard-begin.h>
#include "GameFramework/Actor.h"
#include <util/ue-header-guard-end.h>

#include <cmath>

UOrbitMoverComponent::UOrbitMoverComponent()
{
  PrimaryComponentTick.bCanEverTick = true;
  PrimaryComponentTick.bStartWithTickEnabled = false;
  PrimaryComponentTick.TickGroup = TG_PrePhysics;
}

UOrbitMoverComponent *UOrbitMoverComponent::Find(AActor *Actor)
{
  return IsValid(Actor) ? Actor->FindComponentByClass<UOrbitMoverComponent>() : nullptr;
}

UOrbitMoverComponent *UOrbitMoverComponent::FindOrAdd(AActor *Actor)
{
  UOrbitMoverComponent *Mover = Find(Actor);
  if (Mover != nullptr)
  {
    return Mover;
  }
  Mover = NewObject<UOrbitMoverComponent>(Actor, TEXT("OrbitMover"));
  Actor->AddInstanceComponent(Mover);
  Mover->RegisterComponent();
  return Mover;
}

FTransform UOrbitMoverComponent::PoseAt(const FOrbitMoverParameters &P, double AngleRadians)
{
  // The client's rule, OrbitSensorController.orbit_transform, in centimetres: the angles are the
  // same whatever the unit.
  const double X = P.Centre.X + P.RadiusCm * std::cos(AngleRadians);
  const double Y = P.Centre.Y + P.RadiusCm * std::sin(AngleRadians);
  const double Z = P.Centre.Z + P.AltitudeCm;
  const double Dx = P.Centre.X - X;
  const double Dy = P.Centre.Y - Y;
  const double Dz = P.Centre.Z - Z;
  const double Horizontal = std::sqrt(Dx * Dx + Dy * Dy);
  const double Pitch = P.PitchDegrees.IsSet()
      ? P.PitchDegrees.GetValue()
      : FMath::RadiansToDegrees(std::atan2(Dz, Horizontal));
  const double Yaw = FMath::RadiansToDegrees(std::atan2(Dy, Dx));
  return FTransform(FRotator(Pitch, Yaw, 0.0), FVector(X, Y, Z), FVector::OneVector);
}

void UOrbitMoverComponent::Configure(const FOrbitMoverParameters &InParameters, bool bEnable)
{
  Parameters = InParameters;
  AngleRadians = FMath::Fmod(InParameters.StartAngleRadians, UE_DOUBLE_TWO_PI);
  if (AngleRadians < 0.0)
  {
    AngleRadians += UE_DOUBLE_TWO_PI;
  }
  bPaused = false;
  SetEnabled(bEnable);
}

void UOrbitMoverComponent::SetEnabled(bool bInEnabled)
{
  bEnabled = bInEnabled;
  SetComponentTickEnabled(bEnabled);
  if (bEnabled)
  {
    // On the circle from this frame, whatever pose the owner held before; the tick that follows in
    // this frame advances from here.
    Place();
  }
}

void UOrbitMoverComponent::SetPaused(bool bInPaused)
{
  bPaused = bInPaused;
}

void UOrbitMoverComponent::TickComponent(
    float DeltaTime,
    ELevelTick TickType,
    FActorComponentTickFunction *ThisTickFunction)
{
  TRACE_CPUPROFILER_EVENT_SCOPE(UOrbitMoverComponent::TickComponent);
  Super::TickComponent(DeltaTime, TickType, ThisTickFunction);
  if (!bEnabled)
  {
    return;
  }
  if (!bPaused && Parameters.PeriodSeconds > 0.0)
  {
    const double Rate = UE_DOUBLE_TWO_PI / Parameters.PeriodSeconds;
    const double Step = Rate * static_cast<double>(DeltaTime);
    AngleRadians = FMath::Fmod(AngleRadians + (Parameters.bClockwise ? Step : -Step), UE_DOUBLE_TWO_PI);
    if (AngleRadians < 0.0)
    {
      AngleRadians += UE_DOUBLE_TWO_PI;
    }
  }
  Place();
}

void UOrbitMoverComponent::Place()
{
  AActor *Owner = GetOwner();
  if (!IsValid(Owner))
  {
    return;
  }
  FTransform Pose = PoseAt(Parameters, AngleRadians);
  // The circle is given in the world's global frame; a large map holds its actors in a local one,
  // as set_actor_transform converts (FCarlaActor::SetActorGlobalTransform).
  ALargeMapManager *LargeMap = UCarlaStatics::GetLargeMapManager(GetWorld());
  if (LargeMap != nullptr)
  {
    Pose = LargeMap->GlobalToLocalTransform(Pose);
  }
  Owner->SetActorTransform(Pose, false, nullptr, ETeleportType::TeleportPhysics);
}
