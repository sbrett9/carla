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

#include <cstddef>
#include <cstdint>
#include <cstring>

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
      SolarCorrectedElevationCarried = (0x1 << 3),
      /// A render set block follows the header, before the first actor (see
      /// RenderSetEntryState). Set only on a snapshot that carries one -- once a co-simulation
      /// session has named a body of its pool -- so a world no session has named a body in is laid
      /// out exactly as before. Like the flag above it describes where the actors start, and a
      /// reader that does not know it reads the block as actors, so readers are rebuilt with the
      /// server that sets it.
      RenderSetCarried = (0x1 << 4)
    };

    /// How one body in the render set block is held.
    ///
    /// The block follows the header when simulation_state carries RenderSetCarried, little-endian
    /// and unpadded:
    ///
    ///   uint32 size              bytes after this field, up to the first actor
    ///   uint32 count             entries that follow
    ///   count entries, each:
    ///     uint32 actor_id
    ///     uint8  state            a RenderSetEntryState
    ///     uint64 admitted_frame   the first frame the body was drawn for its vehicle; 0 if parked
    ///     uint16 n, then n bytes  the vehicle the body is lent to, UTF-8; empty if parked
    ///     uint16 n, then n bytes  that vehicle's declared type, UTF-8; empty if parked
    ///
    /// A pooled body is an ordinary vehicle actor, and between loans it stands parked out of sight
    /// below the ground, so the actor array alone says neither which vehicles a frame drew nor who
    /// any of them is. The session that lends the bodies names each one as it lends it and as it
    /// gives it back, and the block carries what it named, paired to the frame like everything else
    /// in the snapshot. An actor with no entry is one no session named, and reads as it always did.
    enum class RenderSetEntryState : uint8_t {
      /// Lent to a vehicle and drawn for it on this frame.
      Lent   = 1u,
      /// Given back and parked out of sight: drawn for nobody on this frame.
      Parked = 2u
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

    /// Where the first actor starts: straight after the header, or after the render set block
    /// where the snapshot carries one.
    static size_t ActorsOffset(const RawData &message) {
      if (message.size() < header_offset + sizeof(uint32_t)) {
        return header_offset;
      }
      const Header &header = DeserializeHeader(message);
      const uint32_t flags = static_cast<uint32_t>(header.simulation_state);
      if ((flags & static_cast<uint32_t>(RenderSetCarried)) == 0u) {
        return header_offset;
      }
      uint32_t block_size = 0u;
      std::memcpy(&block_size, message.begin() + header_offset, sizeof(block_size));
      const size_t offset = header_offset + sizeof(block_size) + block_size;
      return offset <= message.size() ? offset : message.size();
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
