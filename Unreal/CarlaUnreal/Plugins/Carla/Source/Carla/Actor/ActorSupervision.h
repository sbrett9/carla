// Copyright (c) 2026 Computer Vision Center (CVC) at the Universitat Autonoma
// de Barcelona (UAB).
//
// This work is licensed under the terms of the MIT license.
// For a copy, see <https://opensource.org/licenses/MIT>.

#pragma once

#include <cstdint>
#include <string>
#include <vector>

/// One pattern instance in force for the vehicle a pooled body draws: the scenario author's instance,
/// the terms it is labelled with, the phase of its interval in force, and the role the vehicle plays
/// in it. Every string is as the session named it, UTF-8.
struct FSupervisionAnnotation
{
  std::string InstanceId;

  std::vector<std::string> Labels;

  std::string Phase;

  std::string Role;
};

/// What the scenario's author asserts of the vehicle a pooled body draws, as a co-simulation session
/// put it in force (update_supervision).
///
/// Supervision is held on the server, not in the session's process, so every client of the world
/// reads the same truth for the same frame: the world observer carries every body's on each snapshot
/// beside the render set. It is held on the actor's own record, so it ends with the actor, and it is
/// cleared whenever the body is given back or lent to another vehicle, so a body never carries one
/// vehicle's supervision while it draws another.
struct FActorSupervision
{
  enum class EState : uint8_t
  {
    /// No assertion either way: the default, and what a body carries when nothing is held for it.
    /// Never a negative, and never written to a snapshot: a lent body with no row is unlabelled.
    Unlabelled,
    /// Executing the named pattern over the interval in force.
    Annotated,
    /// Executing no target pattern: an authored negative.
    Nominal
  };

  EState State = EState::Unlabelled;

  /// Every pattern instance in force for the vehicle. None when unlabelled.
  std::vector<FSupervisionAnnotation> Annotations;
};
