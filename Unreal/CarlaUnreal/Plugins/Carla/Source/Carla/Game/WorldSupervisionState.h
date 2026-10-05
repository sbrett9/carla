// Copyright (c) 2026 Computer Vision Center (CVC) at the Universitat Autonoma
// de Barcelona (UAB).
//
// This work is licensed under the terms of the MIT license.
// For a copy, see <https://opensource.org/licenses/MIT>.

#pragma once

#include <cstdint>
#include <string>
#include <vector>

/// An absence in force: an occasion the scenario's author declared, which passed with no vehicle.
/// Every string is as the session named it, UTF-8.
struct FSupervisionAbsence
{
  std::string InstanceId;

  std::vector<std::string> Labels;

  /// The areas of interest the absence is sited at.
  std::vector<std::string> Areas;

  /// The phase of its interval, which is the vacancy.
  std::string Phase;
};

/// The supervision a co-simulation session holds for the world as a whole: the plan every row is
/// bound from, with the vocabulary version and digest that pin what its terms mean, and the absences
/// in force.
///
/// World-scoped because an absence has no vehicle to be held on: nothing may stand for a vehicle that
/// does not exist. Held on the episode, so a map load ends it, and withdrawn when the session that put
/// it in force says so. While a plan is held the world observer writes a supervision block on every
/// snapshot, and each lent body's own supervision (FActorSupervision) rides in it.
struct FWorldSupervisionState
{
  /// The plan every row and absence is bound from. Empty while no supervision is held.
  std::string PlanId;

  uint32_t VocabularyVersion = 0u;

  std::string VocabularyDigest;

  /// The absences in force, in the order they opened, one per instance.
  std::vector<FSupervisionAbsence> Absences;

  /// Whether a session has bound a plan and not withdrawn it.
  bool IsHeld() const
  {
    return !PlanId.empty();
  }
};
