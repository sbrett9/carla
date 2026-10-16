// Ported from Eclipse SUMO's reference TraCI client, tools/traci/_simulation.py -- the Collision
// class and _readCollisions.
//
//   Upstream:      https://github.com/eclipse-sumo/sumo
//   Pinned commit: e238ea04b7150ba23a348a285d3048919fa4830b (Eclipse SUMO 1.27.0)
//   Copyright (C) 2011-2026 German Aerospace Center (DLR) and others.
//   SPDX-License-Identifier: EPL-2.0 OR GPL-2.0-or-later

namespace CarlaNet.Sumo;

/// <summary>
/// One collision SUMO registered in the step just taken, as TraCI's <c>getCollisions</c> reports it.
/// </summary>
/// <param name="ColliderId">The vehicle SUMO holds responsible: the follower of a rear-end collision.</param>
/// <param name="VictimId">The other vehicle.</param>
/// <param name="ColliderTypeId">The collider's vehicle type.</param>
/// <param name="VictimTypeId">The victim's vehicle type.</param>
/// <param name="ColliderSpeedMetresPerSecond">The collider's speed when the collision was registered.</param>
/// <param name="VictimSpeedMetresPerSecond">The victim's speed when the collision was registered.</param>
/// <param name="Kind">
/// SUMO's own name for what happened -- <c>collision</c> for a rear-end one on a lane, <c>frontal</c>,
/// <c>junction</c> and the rest -- carried as SUMO wrote it.
/// </param>
/// <param name="LaneId">The lane it was registered on.</param>
/// <param name="LanePositionMetres">Where along that lane.</param>
/// <remarks>
/// <para><b>Reported for every step it lasts.</b> SUMO keeps a collision it has registered and reports
/// it again on each step the two vehicles are still in contact, with the collider and victim as first
/// registered, and forgets it on the first step they are not (<c>MSNet::registerCollision</c>,
/// <c>MSNet::removeOutdatedCollisions</c>). Under <c>collision.action warn</c> two vehicles can stay in
/// contact for many steps, so one collision is one span of steps, not one report.</para>
///
/// <para><b>What SUMO registers as a collision is SUMO's rule.</b> A gap below the follower's
/// <c>minGap</c> counts unless <c>collision.mingap-factor</c> lowers the threshold, and collisions on a
/// junction are checked only under <c>collision.check-junctions</c>.</para>
/// </remarks>
public readonly record struct SumoCollision(
    string ColliderId,
    string VictimId,
    string ColliderTypeId,
    string VictimTypeId,
    double ColliderSpeedMetresPerSecond,
    double VictimSpeedMetresPerSecond,
    string Kind,
    string LaneId,
    double LanePositionMetres);
