// Copyright (c) 2026 Computer Vision Center (CVC) at the Universitat Autonoma
// de Barcelona (UAB).
//
// This work is licensed under the terms of the MIT license.
// For a copy, see <https://opensource.org/licenses/MIT>.

#pragma once

#include <cstdint>
#include <string>

/// Where a co-simulation session's body pool holds one body: lent to the vehicle it draws, or
/// parked out of sight between loans.
///
/// A pooled body is an ordinary vehicle actor, and between loans it stands parked below the ground,
/// so the actor list alone says neither which vehicles a frame drew nor which vehicle a body is
/// drawing. The session names each body as it lends it and as it gives it back (update_render_set),
/// and the world observer carries every named body on each snapshot, so the truth telemetry of any
/// client lists the bodies a frame drew, named by their vehicles, and leaves the parked ones out.
///
/// Held on the actor's own record, so it ends with the actor: a session that destroys its bodies
/// leaves nothing behind, and an actor no session named is never affected.
struct FRenderSetMembership
{
  enum class EState : uint8_t
  {
    /// Not named by any session: reported as it always was.
    None,
    /// Lent to a vehicle and drawn for it.
    Lent,
    /// Given back and parked out of sight: drawn for nobody.
    Parked
  };

  EState State = EState::None;

  /// The vehicle the body is lent to, as the session named it, UTF-8. Empty unless lent.
  std::string VehicleId;

  /// That vehicle's declared type, UTF-8. Empty unless lent.
  std::string VehicleTypeId;

  /// The first frame the body is drawn for this vehicle: the frame after the one in progress when
  /// it was lent. Zero unless lent.
  uint64_t AdmittedFrame = 0u;

  /// Where the body's pose comes from while it does not follow the SUMO step the session declared
  /// (FSumoStepPhase), as the session named it (update_pose_source).
  enum class EPoseSource : uint8_t
  {
    /// Follows the step: SUMO's own pose on a frame a step falls on, interpolated on every other.
    FollowsStep,
    /// Standing where SUMO put it at one of its steps, whichever frame it is: placed at SUMO's later
    /// step across a discontinuity rather than interpolated.
    Simulated,
    /// Left where its last pose put it, because the session could not place it.
    Held
  };

  /// Part of the loan: a body given back, or lent to another vehicle, follows the step again.
  EPoseSource PoseSource = EPoseSource::FollowsStep;
};
