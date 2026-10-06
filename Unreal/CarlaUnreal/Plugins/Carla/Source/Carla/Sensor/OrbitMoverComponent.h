// Copyright (c) 2026 Computer Vision Center (CVC) at the Universitat Autonoma
// de Barcelona (UAB).
//
// This work is licensed under the terms of the MIT license.
// For a copy, see <https://opensource.org/licenses/MIT>.

#pragma once

#include <util/ue-header-guard-begin.h>
#include "Components/ActorComponent.h"
#include "CoreMinimal.h"
#include "Misc/Optional.h"
#include <util/ue-header-guard-end.h>

#include "OrbitMoverComponent.generated.h"

/// The circle an orbit mover flies its actor round, in Unreal units: centimetres, the world's frame.
struct CARLA_API FOrbitMoverParameters
{
  /// The point the actor circles and looks at.
  FVector Centre = FVector::ZeroVector;

  /// Positive.
  double RadiusCm = 0.0;

  /// Above the centre; the actor flies at Centre.Z + AltitudeCm.
  double AltitudeCm = 0.0;

  /// Simulated seconds per revolution; positive.
  double PeriodSeconds = 0.0;

  /// The angle increases: from the centre's +X through its +Y, which is clockwise seen from above.
  bool bClockwise = true;

  /// Where the orbit begins, radians; zero is on the centre's +X side.
  double StartAngleRadians = 0.0;

  /// Set, the pitch held instead of the depression to the centre, degrees.
  TOptional<double> PitchDegrees;
};

/// Flies its owner round a circle on the simulation clock, so a client that wants an orbiting
/// camera sends the circle once and no pose after it (set_orbit).
///
/// The angle advances by the tick's delta -- the fixed delta under synchronous ticking, the frame's
/// under free running -- so the orbit covers the declared angle per simulated second whatever pace
/// the run holds, where a client advancing it on its own wall clock turned the camera as far per
/// captured frame as the pace was high. The pose is set in TG_PrePhysics: before physics, before the
/// sensors capture the frame in PostPhysTick, and before the world observer reports the frame, so a
/// camera's image, its header and the snapshot of its frame agree on where it was. Anything attached
/// to the owner -- a depth camera spawned rigidly on a colour camera -- rides with it.
///
/// While enabled the mover owns the owner's transform: it places the owner on the circle every tick,
/// paused or not, so a client that wants to move the actor itself disables the orbit first. The
/// pose rule is `PoseAt`, the client's `OrbitSensorController.orbit_transform` in centimetres, so a
/// client predicts the camera's angle from the parameters and the simulated clock and never asks.
UCLASS(ClassGroup=(Custom))
class CARLA_API UOrbitMoverComponent : public UActorComponent
{
  GENERATED_BODY()

public:

  UOrbitMoverComponent();

  /// The mover on an actor, or null where it carries none.
  static UOrbitMoverComponent *Find(AActor *Actor);

  /// The mover on an actor, added and registered where it carries none.
  static UOrbitMoverComponent *FindOrAdd(AActor *Actor);

  /// The pose on the circle at an angle: on the circle at the centre's height plus the altitude,
  /// yaw to the centre, pitch the depression to it unless overridden, roll zero.
  static FTransform PoseAt(const FOrbitMoverParameters &Parameters, double AngleRadians);

  /// Take a circle, stand at its start angle, and begin moving where bEnable is set; otherwise hold
  /// everything until SetEnabled. Replaces whatever circle the mover had and clears a pause.
  void Configure(const FOrbitMoverParameters &InParameters, bool bEnable);

  /// Own the owner's transform and advance the angle each tick, or let the owner go where it is.
  /// Enabling places the owner at the held angle at once.
  void SetEnabled(bool bInEnabled);

  /// Stop the angle advancing, or let it advance again; the owner stays on the circle meanwhile.
  void SetPaused(bool bInPaused);

  bool IsEnabled() const
  {
    return bEnabled;
  }

  bool IsPaused() const
  {
    return bPaused;
  }

  /// Radians in [0, 2 pi).
  double GetAngleRadians() const
  {
    return AngleRadians;
  }

  const FOrbitMoverParameters &GetParameters() const
  {
    return Parameters;
  }

  virtual void TickComponent(
      float DeltaTime,
      ELevelTick TickType,
      FActorComponentTickFunction *ThisTickFunction) override;

private:

  /// Put the owner at the held angle, through the large map's frame where the world has one.
  void Place();

  FOrbitMoverParameters Parameters;

  double AngleRadians = 0.0;

  bool bEnabled = false;

  bool bPaused = false;
};
