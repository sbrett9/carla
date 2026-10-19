// Copyright (c) 2026 Computer Vision Center (CVC) at the Universitat Autonoma
// de Barcelona (UAB).
//
// This work is licensed under the terms of the MIT license.
// For a copy, see <https://opensource.org/licenses/MIT>.

#pragma once

#include "carla/MsgPack.h"

namespace carla {
namespace rpc {

  /// How the server flies an actor round a circle (set_orbit): the orbit mover it puts on the actor
  /// advances the angle on the simulation clock each tick and sets the actor on the circle with its
  /// boresight on the centre, so a client sends these once and no pose after.
  ///
  /// Every length is in CARLA metres, in CARLA's frame. The pose at an angle a is the client's rule
  /// (`OrbitSensorController.orbit_transform`): position (centre_x + radius cos a, centre_y +
  /// radius sin a, centre_z + altitude), yaw atan2(centre_y - y, centre_x - x) and pitch the
  /// depression to the centre, roll zero. With the angle increasing the actor goes from the
  /// centre's east through its south, which is clockwise seen from above in a world where -y is
  /// north: `clockwise` true is that, and false runs the angle the other way.
  class OrbitParameters {
  public:

    double centre_x_m = 0.0;

    double centre_y_m = 0.0;

    double centre_z_m = 0.0;

    /// Positive.
    double radius_m = 0.0;

    /// Above the centre's height; the actor flies at centre_z_m + altitude_m.
    double altitude_m = 0.0;

    /// Simulated seconds per revolution; positive. The angular rate is 2 pi over it.
    double period_s = 0.0;

    bool clockwise = true;

    /// Where the orbit begins, radians; zero is east of the centre.
    double start_angle_rad = 0.0;

    /// Hold the pitch at pitch_deg instead of the depression to the centre.
    bool pitch_overridden = false;

    double pitch_deg = 0.0;

    /// Start moving at once. False configures the mover and leaves the actor where it is until
    /// set_orbit_enabled; a client that holds an opening pose through a pre-roll sends that.
    bool enabled = true;

    MSGPACK_DEFINE_ARRAY(
        centre_x_m,
        centre_y_m,
        centre_z_m,
        radius_m,
        altitude_m,
        period_s,
        clockwise,
        start_angle_rad,
        pitch_overridden,
        pitch_deg,
        enabled);
  };

} // namespace rpc
} // namespace carla
