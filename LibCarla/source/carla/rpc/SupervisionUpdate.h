// Copyright (c) 2026 Computer Vision Center (CVC) at the Universitat Autonoma
// de Barcelona (UAB).
//
// This work is licensed under the terms of the MIT license.
// For a copy, see <https://opensource.org/licenses/MIT>.

#pragma once

#include "carla/MsgPack.h"
#include "carla/rpc/ActorId.h"

#include <cstdint>
#include <string>
#include <vector>

namespace carla {
namespace rpc {

  /// One pattern instance in force for the vehicle a lent body draws: the author's instance, the terms
  /// it is labelled with, the phase of its interval in force, and the role the vehicle plays in it.
  class SupervisionUpdateAnnotation {
  public:

    std::string instance_id;

    std::vector<std::string> labels;

    std::string phase;

    std::string role;

    MSGPACK_DEFINE_ARRAY(instance_id, labels, phase, role);
  };

  /// What the author asserts, from now on, of the vehicle one lent body draws: its state, spelled as
  /// the annotation vocabulary's core spells it -- annotated, nominal or unlabelled -- and every
  /// annotation in force, which replace whatever the body carried before. Unlabelled carries none, and
  /// clears the body.
  class SupervisionUpdateActor {
  public:

    ActorId actor_id = 0u;

    std::string state;

    std::vector<SupervisionUpdateAnnotation> annotations;

    MSGPACK_DEFINE_ARRAY(actor_id, state, annotations);
  };

  /// One change to the supervision a co-simulation session holds on the server (update_supervision):
  /// the plan it is bound from and the bodies whose supervision changes. Every row is a vehicle's;
  /// nothing is held for the world apart from the plan, because SUMO reports vehicles, not places,
  /// and a label follows the vehicle it is about (06_Truth_And_Annotation.md §3.5).
  class SupervisionUpdate {
  public:

    /// Drop every row held before this change is applied: a session's first change, and the first
    /// after it binds another plan.
    bool fresh = false;

    /// The plan every row is bound from. Empty withdraws all supervision, and then the change carries
    /// nothing else.
    std::string plan_id;

    /// The version of the annotation vocabulary's core the plan's terms were resolved against.
    uint32_t vocabulary_version = 0u;

    /// The digest over the plan's resolved terms.
    std::string vocabulary_digest;

    std::vector<SupervisionUpdateActor> actors;

    MSGPACK_DEFINE_ARRAY(
        fresh,
        plan_id,
        vocabulary_version,
        vocabulary_digest,
        actors);
  };

} // namespace rpc
} // namespace carla
