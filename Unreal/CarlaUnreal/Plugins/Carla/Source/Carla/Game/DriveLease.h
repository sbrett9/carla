// Copyright (c) 2026 Computer Vision Center (CVC) at the Universitat Autonoma
// de Barcelona (UAB).
//
// This work is licensed under the terms of the MIT license.
// For a copy, see <https://opensource.org/licenses/MIT>.

#pragma once

#include <cstdint>
#include <string>

/// The drive lease on a world: one client's exclusive claim to be the traffic that drives the
/// vehicles in it.
///
/// Held on the episode, so a map load ends it, and taken and given back by RPC
/// (take_drive_lease, release_drive_lease). While it is held, every RPC that would have another
/// traffic system drive a vehicle -- the autopilot flag, vehicle control, Ackermann control and
/// physics control, direct or in a batch -- is refused for every actor, with the holder named, so
/// nothing but the holder moves a vehicle in this world. A SUMO drive session takes it before SUMO
/// is started; a second session, or a traffic manager started against the same server, is refused
/// at the lease or at its first control write.
///
/// World-scoped because the thing it protects is the world's population: two traffic systems in one
/// world produce imagery carrying both and a truth record carrying one. The RPC server offers no
/// disconnect notification, so a holder that dies without releasing leaves the lease held until the
/// world is reloaded or break_drive_lease is called.
struct FDriveLease
{
  /// Who holds it, as the holder named itself. Empty while nobody does.
  std::string Holder;

  /// The frame the lease was taken on.
  uint64_t TakenFrame = 0u;

  /// Whether a holder has taken the lease and not given it back.
  bool IsHeld() const
  {
    return !Holder.empty();
  }
};
