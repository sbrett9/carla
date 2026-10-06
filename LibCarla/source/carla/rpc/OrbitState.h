// Copyright (c) 2026 Computer Vision Center (CVC) at the Universitat Autonoma
// de Barcelona (UAB).
//
// This work is licensed under the terms of the MIT license.
// For a copy, see <https://opensource.org/licenses/MIT>.

#pragma once

#include "carla/MsgPack.h"

namespace carla {
namespace rpc {

  /// Where an actor's orbit mover stands (get_orbit_state): the angle it holds the actor at, and
  /// whether it is moving the actor. An actor given no orbit answers zero, disabled, not paused.
  class OrbitState {
  public:

    /// Radians in [0, 2 pi); the angle the actor was last placed at.
    double angle_rad = 0.0;

    /// The mover owns the actor's transform: it places the actor on the circle every tick.
    bool enabled = false;

    /// The angle does not advance; the actor is held where it is on the circle while enabled.
    bool paused = false;

    MSGPACK_DEFINE_ARRAY(angle_rad, enabled, paused);
  };

} // namespace rpc
} // namespace carla
