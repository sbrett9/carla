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
      RenderSetCarried = (0x1 << 4),
      /// A supervision block follows the render set's entries, inside the render set block (see
      /// SupervisionEntryState). Set only on a snapshot that carries one -- once a co-simulation
      /// session has bound a supervision plan -- and always with RenderSetCarried, whose block size
      /// counts it, so a reader that knows the render set and not this flag finds the actors and the
      /// render set as before and skips the supervision unread.
      SupervisionCarried = (0x1 << 5),
      /// Every vehicle's VehicleData carries the lights commanded on for it on this frame
      /// (detail::VehicleData::light_state). Set on every snapshot from a server that fills the field,
      /// lights or no lights, because it says what the field means rather than what it holds: zero is
      /// every light off, and a server built before it leaves the same bytes zero, so a reader that
      /// finds this flag clear knows nothing of any vehicle's lights and writes none.
      VehicleLightStateCarried = (0x1 << 6),
      /// A pose source block follows the supervision block, or the render set's entries where there is
      /// no supervision block, inside the render set block (see PoseSourceEntryState). Set only on a
      /// snapshot that carries one -- once a co-simulation session has declared the frames its SUMO
      /// steps fall on, or named a body whose pose follows none -- and always with RenderSetCarried,
      /// whose block size counts it, so a reader that knows neither this flag nor the block finds the
      /// actors, the render set and the supervision as before.
      PoseSourceCarried = (0x1 << 7)
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
    ///   the supervision block, where simulation_state carries SupervisionCarried
    ///   the pose source block, where simulation_state carries PoseSourceCarried
    ///
    /// The block is written whenever any part is carried, so a world whose session has bound a
    /// supervision plan, or declared its SUMO step, and lent no body yet carries it with no entries.
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

    /// What the author asserts of the vehicle one lent body draws, in the supervision block.
    ///
    /// The block follows the render set's last entry when simulation_state carries
    /// SupervisionCarried, inside the render set block's size, little-endian and unpadded:
    ///
    ///   uint32 size              bytes after this field, to the end of the supervision block
    ///   uint16 n, then n bytes   the plan every row is bound from, UTF-8
    ///   uint32 vocabulary_version
    ///   uint16 n, then n bytes   the vocabulary digest, UTF-8
    ///   uint32 count             rows that follow, one per body whose vehicle is annotated or nominal
    ///   count rows, each:
    ///     uint32 actor_id         a body the render set names lent
    ///     uint8  state            a SupervisionEntryState
    ///     uint16 count            annotations that follow
    ///     count annotations, each:
    ///       uint16 n, then n bytes  the pattern instance, UTF-8
    ///       uint16 n, then n bytes  the phase of its interval in force, UTF-8
    ///       uint16 n, then n bytes  the role the vehicle plays in it, UTF-8
    ///       uint16 count, then count labels, each a uint16 n and n bytes of UTF-8
    ///
    /// The supervision in force is held on the server, so every client of the world reads the same
    /// truth paired to the same frame. A lent body with no row draws a vehicle the author asserts
    /// nothing of -- unlabelled, which costs no bytes -- and an actor the render set does not name lent
    /// is no subject of the plan at all. Every row is a body's: the block holds nothing for the world
    /// apart from the plan, because a label follows the vehicle it is about (06 §3.5).
    enum class SupervisionEntryState : uint8_t {
      /// Executing the named pattern over the interval in force.
      Annotated = 1u,
      /// Executing no target pattern: an authored negative.
      Nominal   = 2u
    };

    /// Where the pose a lent body is drawn at on a frame came from, in the pose source block.
    ///
    /// The block follows the supervision block, or the render set's last entry where there is none,
    /// when simulation_state carries PoseSourceCarried, inside the render set block's size,
    /// little-endian and unpadded:
    ///
    ///   uint32 size              bytes after this field, to the end of the pose source block
    ///   uint32 ticks_per_step    world ticks per SUMO step; 0 where no session has declared its step
    ///   uint64 step_frame        a frame a SUMO step falls on; 0 where ticks_per_step is 0
    ///   uint32 count             entries that follow, one per lent body whose pose follows no step
    ///   count entries, each:
    ///     uint32 actor_id         a body the render set names lent
    ///     uint8  state            a PoseSourceEntryState
    ///
    /// A co-simulation session poses its bodies every world tick from SUMO steps a whole number of
    /// ticks apart: on the tick a step falls on a body stands where SUMO put it, and on every other it
    /// stands between two steps. So the session declares the step once (update_pose_source), and the
    /// server carries it on every snapshot: any reader takes a frame f at or after step_frame to show
    /// SUMO's own step where (f - step_frame) is a multiple of ticks_per_step and an interpolated pose
    /// otherwise, at no cost per tick. A body whose pose follows neither on a frame is named by the
    /// session as its case begins and ends, and carried as an entry while it lasts: one held where the
    /// session could not place it, or one placed at SUMO's own later step across a discontinuity. A lent
    /// body with no entry follows the step; an actor the render set does not name lent has no pose
    /// source at all.
    enum class PoseSourceEntryState : uint8_t {
      /// Standing where SUMO put it at one of its steps, whichever frame it is.
      Simulated = 1u,
      /// Left where its last pose put it, because the session could not place it on this frame.
      Held      = 2u
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
    /// where the snapshot carries one, the supervision block inside it included.
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
