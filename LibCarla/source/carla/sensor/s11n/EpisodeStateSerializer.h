// Copyright (c) 2026 Computer Vision Center (CVC) at the Universitat Autonoma
// de Barcelona (UAB).
//
// This work is licensed under the terms of the MIT license.
// For a copy, see <https://opensource.org/licenses/MIT>.

#pragma once

#include "carla/Buffer.h"
#include "carla/Debug.h"
#include "carla/Memory.h"
#include "carla/geom/Transform.h"
#include "carla/geom/Vector3DInt.h"
#include "carla/sensor/RawData.h"
#include "carla/sensor/data/ActorDynamicState.h"

#include <cstdint>

namespace carla {
namespace sensor {

  class SensorData;

namespace s11n {

  /// Serializes the current state of the whole episode.
  class EpisodeStateSerializer {
  public:

    enum SimulationState {
      None               = (0x0 << 0),
      MapChange          = (0x1 << 0),
      PendingLightUpdate = (0x1 << 1),
      /// The solar fields below carry a sun that was actually measured this tick. Without this
      /// flag they cannot say "this world has no sun": their defaults are a well-formed reading
      /// -- midnight of year 0 at latitude 0, longitude 0 -- that a reader cannot tell from a
      /// real one, and would otherwise record as fact.
      SolarStateValid    = (0x1 << 2),
      /// The solar block below is twelve doubles wide: the sun's refraction-corrected elevation
      /// follows the rate. It describes the layout rather than the reading, so it is set on every
      /// snapshot, sun or no sun: the header's size is the offset of the actor array, and a reader
      /// has to know which size it is reading before it can read anything after it. A reader that
      /// does not know this flag reads the actors eight bytes early, so readers are rebuilt with
      /// the server that sets it.
      SolarCorrectedElevationCarried = (0x1 << 3)
    };

#pragma pack(push, 1)
    struct Header {
      uint64_t episode_id;
      double platform_timestamp;
      float delta_seconds;
      geom::Vector3DInt map_origin;
      SimulationState simulation_state = SimulationState::None;
      // Solar / time-of-day state (CesiumSunSky), appended so each streamed world snapshot carries
      // the sun in effect that tick — the recorder pairs frames with the sun straight from the
      // observer cache (no polling). Populated by FWorldObserver from UCesiumHeightSampler::GetSolarState;
      // left at these defaults (rate 1.0, rest 0) when the world has no CesiumSunSky, in which case
      // simulation_state does NOT carry SolarStateValid and these values must not be read. Stored as
      // doubles for a uniform block mirrored by the CarlaNet reader.
      double solar_time = 0.0;
      double solar_year = 0.0;
      double solar_month = 0.0;
      double solar_day = 0.0;
      double solar_time_zone = 0.0;
      double solar_lat = 0.0;
      double solar_lon = 0.0;
      double solar_elevation = 0.0;
      double solar_azimuth = 0.0;
      double solar_advancing = 0.0;
      double solar_rate = 1.0;
      // The elevation the sun's directional light is actually rotated by, with atmospheric
      // refraction applied. solar_elevation above is geometric; near the horizon the two differ by
      // up to a few tenths of a degree, which is a large fraction of a low sun. Carried every tick
      // so a frame's record states the sun it was lit by, not only the sun's geometric position.
      double solar_corrected_elevation = 0.0;
    };
#pragma pack(pop)

    constexpr static auto header_offset = sizeof(Header);

    static const Header &DeserializeHeader(const RawData &message) {
      return *reinterpret_cast<const Header *>(message.begin());
    }

    template <typename SensorT>
    static Buffer Serialize(const SensorT &, Buffer &&buffer) {
      return std::move(buffer);
    }

    static SharedPtr<SensorData> Deserialize(RawData &&data);
  };

} // namespace s11n
} // namespace sensor
} // namespace carla
