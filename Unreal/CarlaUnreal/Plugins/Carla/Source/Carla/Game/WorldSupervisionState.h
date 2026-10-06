// Copyright (c) 2026 Computer Vision Center (CVC) at the Universitat Autonoma
// de Barcelona (UAB).
//
// This work is licensed under the terms of the MIT license.
// For a copy, see <https://opensource.org/licenses/MIT>.

#pragma once

#include <cstdint>
#include <string>

/// The supervision a co-simulation session holds for the world as a whole: the plan every row is
/// bound from, with the vocabulary version and digest that pin what its terms mean.
///
/// Nothing else is held for the world. Every row of supervision is a vehicle's, carried on the lent
/// body that draws it (FActorSupervision), because SUMO reports vehicles, not places, and a label
/// follows the vehicle it is about (06_Truth_And_Annotation.md §3.5). Held on the episode, so a map
/// load ends it, and withdrawn when the session that put it in force says so. While a plan is held the
/// world observer writes a supervision block on every snapshot, and each lent body's own supervision
/// rides in it.
struct FWorldSupervisionState
{
  /// The plan every row is bound from. Empty while no supervision is held.
  std::string PlanId;

  uint32_t VocabularyVersion = 0u;

  std::string VocabularyDigest;

  /// Whether a session has bound a plan and not withdrawn it.
  bool IsHeld() const
  {
    return !PlanId.empty();
  }
};
