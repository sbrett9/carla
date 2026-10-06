// Copyright (c) 2026 Computer Vision Center (CVC) at the Universitat Autonoma
// de Barcelona (UAB).
//
// This work is licensed under the terms of the MIT license.
// For a copy, see <https://opensource.org/licenses/MIT>.

#pragma once

#include <cstdint>

/// The frames a co-simulation session's SUMO steps fall on, as the session declared them.
///
/// A session poses its bodies every world tick from SUMO steps a whole number of ticks apart: on the
/// frame a step falls on a body stands where SUMO put it, and on every other frame it stands between
/// two steps. The session declares the step once (update_pose_source), and the world observer carries
/// it on every snapshot, so any reader of any frame computes which of the two the frame shows from the
/// frame number alone, and nothing is sent per tick. A body whose pose follows neither on a frame is
/// named on the body itself (FRenderSetMembership::PoseSource).
///
/// Held on the episode, so a map load ends it, and withdrawn when the session that declared it says so.
struct FSumoStepPhase
{
  /// World ticks per SUMO step. Zero while no session has declared its step.
  uint32_t TicksPerStep = 0u;

  /// A frame a SUMO step falls on: the frame after the one in progress when the step was declared.
  /// Every frame a whole number of TicksPerStep after it falls on a step too. Zero while no step is
  /// declared.
  uint64_t StepFrame = 0u;

  /// Whether a session has declared its step and not withdrawn it.
  bool IsHeld() const
  {
    return TicksPerStep > 0u;
  }
};
