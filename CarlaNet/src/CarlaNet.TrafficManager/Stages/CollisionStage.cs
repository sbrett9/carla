// Source: carla/trafficmanager/CollisionStage.{h,cpp}
//
// Per-tick collision-hazard detector. For every registered vehicle:
//
//   1. Query TrackTraffic.GetOverlappingVehicles(ego) for the broad-phase
//      set of candidates — this uses the geodesic-grid inverted index so
//      we never iterate the full N² actor pairs.
//   2. Distance-filter candidates against a velocity-dependent collision
//      radius (and a vertical-overlap ceiling so vehicles on overpasses
//      do not collide with vehicles below them).
//   3. Sort survivors by squared distance, then run NegotiateCollision —
//      a polygon-vs-polygon comparison using the vehicle's bounding box
//      and a "geodesic boundary" polygon swept along its planned path.
//   4. The first survivor that yields a hazard wins; the result is
//      written into the per-tick CollisionFrame output map.
//
// The geodesic-boundary and pairwise-geometry caches are populated on
// demand inside a single tick and flushed via ClearCycleCache() at the
// end of every CollisionStage sweep (the orchestrator owns this call).
//
// Polygon math: instead of pulling in boost::geometry / Clipper, we use
// a small Polygon2D helper (defined below) that runs SAT-style
// minimum-distance queries between convex 2-D point lists. The geodesic
// boundary polygons CARLA builds are always topologically simple convex
// strips, so SAT is correct and faster than a general-purpose algorithm.
//
// Performance: at 50 vehicles, GetOverlappingVehicles typically returns
// 1-5 candidates per ego (the grid is 20 m wide), so the per-tick work
// is O(N × k × poly-vertex-count) with k ≈ 3. The geometry_cache makes
// each pair-comparison O(1) after the first hit, so an ego/other and
// its reverse share work.
//
// IMPORTANT: this stage does NOT depend on InMemoryMap. The constructor
// could accept one for symmetry with the spec but we skip it — Wave 4
// can re-introduce the parameter if the orchestrator needs uniform
// stage signatures.

#nullable enable

namespace CarlaNet.TrafficManager.Stages;

// BufferMap (Dictionary<ActorId, WaypointBuffer>) is declared by ALSM.cs
// (sibling Wave 3 agent). WaypointBuffer derives from List<SimpleWaypoint>
// so we get O(1) indexed access in the hot path.

internal sealed class CollisionStage : IStageWithRemoveActor
{
    private readonly SimulationState _simulationState;
    private readonly BufferMap _bufferMap;
    private readonly TrackTraffic _trackTraffic;
    private readonly Parameters _parameters;
    private readonly RandomGenerator _random;

    // Output map; allocated once, mutated in place per tick.
    private readonly Dictionary<ActorId, CollisionHazardData> _output = new();

    // Persistent collision-lock state (one entry per ego that is yielding
    // to a leader). Survives across ticks to dampen oscillation.
    private readonly Dictionary<ActorId, CollisionLock> _collisionLocks = new();

    // Per-tick caches. Cleared by ClearCycleCache().
    private readonly Dictionary<ActorId, Location[]> _geodesicBoundaryMap = new();
    private readonly Dictionary<ulong, GeometryComparison> _geometryCache = new();

    // Scratch list reused across actors to avoid per-tick allocation when
    // sorting collision candidates by distance.
    private readonly List<ActorId> _candidates = new(16);

    public CollisionStage(
        SimulationState simulationState,
        BufferMap bufferMap,
        TrackTraffic trackTraffic,
        Parameters parameters,
        RandomGenerator random)
    {
        _simulationState = simulationState;
        _bufferMap = bufferMap;
        _trackTraffic = trackTraffic;
        _parameters = parameters;
        _random = random;
    }

    public IReadOnlyDictionary<ActorId, CollisionHazardData> GetOutput() => _output;

    /// <summary>
    /// Compute and store the hazard verdict for a single ego vehicle.
    /// Mirrors <c>CollisionStage::Update(index)</c> exactly — only the
    /// outer indexing differs (we key by ActorId instead of a vector
    /// index because the orchestrator's frame indexing is wave-4 work).
    /// </summary>
    public void Update(ActorId egoActorId)
    {
        ActorId obstacleId = 0u;
        bool collisionHazard = false;
        float availableDistanceMargin = float.PositiveInfinity;

        if (_simulationState.ContainsActor(egoActorId)
            && _bufferMap.TryGetValue(egoActorId, out var egoBuffer)
            && egoBuffer.Count > 0)
        {
            Location egoLocation = _simulationState.GetLocation(egoActorId);
            ulong lookAheadIndex = LocalizationUtils
                .GetTargetWaypoint(egoBuffer, Constants.WaypointSelection.JUNCTION_LOOK_AHEAD).Index;

            Vector3D vel = _simulationState.GetVelocity(egoActorId);
            float velocity = MathF.Sqrt(vel.X * vel.X + vel.Y * vel.Y + vel.Z * vel.Z);

            // Velocity-dependent collision radius (Sq).
            float collisionRadiusSquare = SquareF(
                Constants.Collision.COLLISION_RADIUS_RATE * velocity
                + Constants.Collision.COLLISION_RADIUS_MIN);
            if (velocity < 2.0f)
            {
                float length = _simulationState.GetDimensions(egoActorId).X;
                float collisionRadiusStop = Constants.Collision.COLLISION_RADIUS_STOP + length;
                collisionRadiusSquare = SquareF(collisionRadiusStop);
            }
            float distanceToLeading = _parameters.GetDistanceToLeadingVehicle(egoActorId);
            if (distanceToLeading > collisionRadiusSquare)
                collisionRadiusSquare = SquareF(distanceToLeading);

            // Broad-phase filter.
            var overlapping = _trackTraffic.GetOverlappingVehicles(egoActorId);
            _candidates.Clear();
            foreach (var overlappingActorId in overlapping)
            {
                if (overlappingActorId == egoActorId) continue;
                if (!_simulationState.ContainsActor(overlappingActorId)) continue;

                Location otherLoc = _simulationState.GetLocation(overlappingActorId);
                float distSq = DistanceSquared(otherLoc, egoLocation);
                if (distSq < collisionRadiusSquare
                    && MathF.Abs(egoLocation.Z - otherLoc.Z)
                       < Constants.Collision.VERTICAL_OVERLAP_THRESHOLD)
                {
                    _candidates.Add(overlappingActorId);
                }
            }

            // Sort by ascending distance to ego (so the closest hazard wins).
            // We use a small in-place insertion sort — N is typically < 10
            // and List<T>.Sort with a comparer allocates a delegate.
            InsertionSortByDistance(_candidates, egoLocation);

            // Narrow-phase: first survivor that triggers wins.
            for (int i = 0; i < _candidates.Count && !collisionHazard; i++)
            {
                ActorId otherActorId = _candidates[i];
                ActorType otherActorType = _simulationState.GetType(otherActorId);

                if (_parameters.GetCollisionDetection(egoActorId, otherActorId)
                    && _bufferMap.ContainsKey(egoActorId)
                    && _simulationState.ContainsActor(otherActorId))
                {
                    (bool hazard, float margin) = NegotiateCollision(
                        egoActorId, otherActorId, lookAheadIndex);

                    if (hazard)
                    {
                        // Stochastic ignore (per Parameters): if the random
                        // draw is less than the ignore-% knob we drop the
                        // hazard. Vehicles and pedestrians have separate
                        // knobs.
                        bool keep =
                            (otherActorType == ActorType.Vehicle
                                && _parameters.GetPercentageIgnoreVehicles(egoActorId) <= _random.Next())
                            || (otherActorType == ActorType.Pedestrian
                                && _parameters.GetPercentageIgnoreWalkers(egoActorId) <= _random.Next());
                        if (keep)
                        {
                            collisionHazard = true;
                            obstacleId = otherActorId;
                            availableDistanceMargin = margin;
                        }
                    }
                }
            }
        }

        _output[egoActorId] = new CollisionHazardData(
            availableDistanceMargin, obstacleId, collisionHazard);
    }

    /// <summary>
    /// Remove all per-actor state. Called by the orchestrator when a
    /// vehicle is destroyed.
    /// </summary>
    public void RemoveActor(ActorId actorId)
    {
        _collisionLocks.Remove(actorId);
        _output.Remove(actorId);
    }

    /// <summary>Wipe persistent locks (the cycle caches are short-lived).</summary>
    public void Reset()
    {
        _collisionLocks.Clear();
        _output.Clear();
        ClearCycleCache();
    }

    /// <summary>
    /// Flush the per-tick boundary and geometry caches. The orchestrator
    /// calls this once between the CollisionStage sweep and the next
    /// frame's TrafficLightStage / MotionPlanStage to keep the cache hot
    /// for the rest of the tick.
    /// </summary>
    public void ClearCycleCache()
    {
        _geodesicBoundaryMap.Clear();
        _geometryCache.Clear();
    }

    // ═════════════════════════════════════════════════════════════════
    //                  Core negotiation algorithm
    // ═════════════════════════════════════════════════════════════════

    private (bool Hazard, float Margin) NegotiateCollision(
        ActorId referenceVehicleId,
        ActorId otherActorId,
        ulong referenceJunctionLookAheadIndex)
    {
        bool hazard = false;
        float availableDistanceMargin = float.PositiveInfinity;

        Location referenceLocation = _simulationState.GetLocation(referenceVehicleId);
        Location otherLocation = _simulationState.GetLocation(otherActorId);

        Vector3D referenceHeading = _simulationState.GetHeading(referenceVehicleId);
        Vector3D referenceToOther = MakeSafeUnitVector(
            new Vector3D(
                otherLocation.X - referenceLocation.X,
                otherLocation.Y - referenceLocation.Y,
                otherLocation.Z - referenceLocation.Z),
            Constants.Collision.EPSILON);

        Vector3D otherHeading = _simulationState.GetHeading(otherActorId);
        Vector3D otherToReference = MakeSafeUnitVector(
            new Vector3D(
                referenceLocation.X - otherLocation.X,
                referenceLocation.Y - otherLocation.Y,
                referenceLocation.Z - otherLocation.Z),
            Constants.Collision.EPSILON);

        float referenceVehicleLength =
            _simulationState.GetDimensions(referenceVehicleId).X * Constants.Collision.SQUARE_ROOT_OF_TWO;
        float otherVehicleLength =
            _simulationState.GetDimensions(otherActorId).X * Constants.Collision.SQUARE_ROOT_OF_TWO;

        float interVehicleDistance = DistanceSquared(referenceLocation, otherLocation);
        float egoBoundingBoxExtension = GetBoundingBoxExtension(referenceVehicleId);
        float otherBoundingBoxExtension = GetBoundingBoxExtension(otherActorId);
        float interVehicleLength = referenceVehicleLength + otherVehicleLength;
        float egoDetectionRange = SquareF(egoBoundingBoxExtension + interVehicleLength);
        float crossDetectionRange = SquareF(
            egoBoundingBoxExtension + interVehicleLength + otherBoundingBoxExtension);

        bool otherVehicleInEgoRange = interVehicleDistance < egoDetectionRange;
        bool otherVehiclesInCrossDetectionRange = interVehicleDistance < crossDetectionRange;
        float referenceHeadingToOtherDot = Dot(referenceHeading, referenceToOther);
        bool otherVehicleInFront = referenceHeadingToOtherDot > 0f;

        var referenceVehicleBuffer = _bufferMap[referenceVehicleId];
        SimpleWaypoint closestPoint = referenceVehicleBuffer[0];
        bool egoInsideJunction = closestPoint.CheckJunction();

        TrafficLightStateData referenceTlState = _simulationState.GetTLS(referenceVehicleId);
        bool egoAtTrafficLight = referenceTlState.AtTrafficLight;
        bool egoStoppedByLight =
            referenceTlState.TlState != TLS.Green && referenceTlState.TlState != TLS.Off;

        int lookAheadIdx = (int)referenceJunctionLookAheadIndex;
        if (lookAheadIdx < 0) lookAheadIdx = 0;
        if (lookAheadIdx >= referenceVehicleBuffer.Count) lookAheadIdx = referenceVehicleBuffer.Count - 1;
        SimpleWaypoint lookAheadPoint = referenceVehicleBuffer[lookAheadIdx];
        bool egoAtJunctionEntrance =
            !closestPoint.CheckJunction() && lookAheadPoint.CheckJunction();

        if (!(egoAtJunctionEntrance && egoAtTrafficLight && egoStoppedByLight)
            && ((egoInsideJunction && otherVehiclesInCrossDetectionRange)
                || (!egoInsideJunction && otherVehicleInFront && otherVehicleInEgoRange)))
        {
            GeometryComparison geometryComparison =
                GetGeometryBetweenActors(referenceVehicleId, otherActorId);

            bool geodesicPathBboxTouching =
                geometryComparison.InterGeodesicDistance < Constants.Collision.OVERLAP_THRESHOLD;
            bool vehicleBboxTouching =
                geometryComparison.InterBboxDistance < Constants.Collision.OVERLAP_THRESHOLD;
            bool egoPathClear =
                geometryComparison.OtherVehicleToReferenceGeodesic > Constants.Collision.OVERLAP_THRESHOLD;
            bool otherPathClear =
                geometryComparison.ReferenceVehicleToOtherGeodesic > Constants.Collision.OVERLAP_THRESHOLD;
            bool egoPathPriority =
                geometryComparison.ReferenceVehicleToOtherGeodesic
                < geometryComparison.OtherVehicleToReferenceGeodesic;
            bool otherPathPriority =
                geometryComparison.ReferenceVehicleToOtherGeodesic
                > geometryComparison.OtherVehicleToReferenceGeodesic;
            bool egoAngularPriority =
                referenceHeadingToOtherDot < Dot(otherHeading, otherToReference);

            bool lowerPriority = !egoPathPriority && (otherPathPriority || !egoAngularPriority);
            bool blockedByOtherOrLowerPriority = !egoPathClear || (otherPathClear && lowerPriority);
            bool yieldPreCrash = !vehicleBboxTouching && blockedByOtherOrLowerPriority;
            bool yieldPostCrash = vehicleBboxTouching && !egoAngularPriority;

            if (geodesicPathBboxTouching && (yieldPreCrash || yieldPostCrash))
            {
                hazard = true;

                float referenceLeadDistance = _parameters.GetDistanceToLeadingVehicle(referenceVehicleId);
                float specificDistanceMargin =
                    MathF.Max(referenceLeadDistance, Constants.Collision.MIN_REFERENCE_DISTANCE);
                availableDistanceMargin = (float)Math.Max(
                    geometryComparison.ReferenceVehicleToOtherGeodesic - specificDistanceMargin,
                    0.0);

                // Collision-lock bookkeeping (smooth lead-vehicle following).
                if (_collisionLocks.TryGetValue(referenceVehicleId, out var lockExisting))
                {
                    if (otherActorId == lockExisting.LeadVehicleId)
                    {
                        if (geometryComparison.OtherVehicleToReferenceGeodesic
                            < Constants.Collision.OVERLAP_THRESHOLD)
                        {
                            lockExisting.DistanceToLeadVehicle = geometryComparison.InterBboxDistance;
                        }
                        else
                        {
                            lockExisting.DistanceToLeadVehicle =
                                geometryComparison.ReferenceVehicleToOtherGeodesic;
                        }
                        _collisionLocks[referenceVehicleId] = lockExisting;
                    }
                    else
                    {
                        _collisionLocks[referenceVehicleId] = new CollisionLock
                        {
                            DistanceToLeadVehicle = geometryComparison.InterBboxDistance,
                            InitialLockDistance = geometryComparison.InterBboxDistance,
                            LeadVehicleId = otherActorId
                        };
                    }
                }
                else
                {
                    _collisionLocks.Add(referenceVehicleId, new CollisionLock
                    {
                        DistanceToLeadVehicle = geometryComparison.InterBboxDistance,
                        InitialLockDistance = geometryComparison.InterBboxDistance,
                        LeadVehicleId = otherActorId
                    });
                }
            }
        }

        // Flush stale lock if no hazard.
        if (!hazard)
            _collisionLocks.Remove(referenceVehicleId);

        return (hazard, availableDistanceMargin);
    }

    // ═════════════════════════════════════════════════════════════════
    //                 Bounding-box / geodesic boundary
    // ═════════════════════════════════════════════════════════════════

    private float GetBoundingBoxExtension(ActorId actorId)
    {
        Vector3D vel = _simulationState.GetVelocity(actorId);
        Vector3D heading = _simulationState.GetHeading(actorId);
        float velocity = Dot(vel, heading);

        float velocityExtension = Constants.Collision.VEL_EXT_FACTOR * velocity;
        float bboxExtension = Constants.Collision.BOUNDARY_EXTENSION_MINIMUM
                              + velocityExtension * velocityExtension;

        if (_collisionLocks.TryGetValue(actorId, out var lockEntry))
        {
            float lockBoundaryLength = (float)(
                lockEntry.DistanceToLeadVehicle + Constants.Collision.LOCKING_DISTANCE_PADDING);
            if (lockBoundaryLength - lockEntry.InitialLockDistance
                < Constants.Collision.MAX_LOCKING_EXTENSION)
            {
                bboxExtension = lockBoundaryLength;
            }
        }
        return bboxExtension;
    }

    /// <summary>
    /// Build the four-corner top-view bounding box for an actor; pedestrians
    /// get an extra forward extension proportional to their velocity so we
    /// brake for where they will be, not where they are.
    /// </summary>
    private Location[] GetBoundary(ActorId actorId)
    {
        ActorType actorType = _simulationState.GetType(actorId);
        Vector3D headingVector = _simulationState.GetHeading(actorId);

        float forwardExtension = 0f;
        if (actorType == ActorType.Pedestrian)
        {
            Vector3D vel = _simulationState.GetVelocity(actorId);
            float speed = MathF.Sqrt(vel.X * vel.X + vel.Y * vel.Y + vel.Z * vel.Z);
            forwardExtension = speed * Constants.Collision.WALKER_TIME_EXTENSION;
        }

        Vector3D dimensions = _simulationState.GetDimensions(actorId);
        float bboxX = dimensions.X;
        float bboxY = dimensions.Y;

        Vector3D xBoundaryVector = new(
            headingVector.X * (bboxX + forwardExtension),
            headingVector.Y * (bboxX + forwardExtension),
            headingVector.Z * (bboxX + forwardExtension));
        Vector3D perp = MakeSafeUnitVector(
            new Vector3D(-headingVector.Y, headingVector.X, 0f),
            Constants.Collision.EPSILON);
        Vector3D yBoundaryVector = new(
            perp.X * (bboxY + forwardExtension),
            perp.Y * (bboxY + forwardExtension),
            perp.Z * (bboxY + forwardExtension));

        Location loc = _simulationState.GetLocation(actorId);

        // Four corners, clockwise in top view (left-handed coords).
        return new[]
        {
            new Location(loc.X + xBoundaryVector.X - yBoundaryVector.X,
                         loc.Y + xBoundaryVector.Y - yBoundaryVector.Y,
                         loc.Z + xBoundaryVector.Z - yBoundaryVector.Z),
            new Location(loc.X - xBoundaryVector.X - yBoundaryVector.X,
                         loc.Y - xBoundaryVector.Y - yBoundaryVector.Y,
                         loc.Z - xBoundaryVector.Z - yBoundaryVector.Z),
            new Location(loc.X - xBoundaryVector.X + yBoundaryVector.X,
                         loc.Y - xBoundaryVector.Y + yBoundaryVector.Y,
                         loc.Z - xBoundaryVector.Z + yBoundaryVector.Z),
            new Location(loc.X + xBoundaryVector.X + yBoundaryVector.X,
                         loc.Y + xBoundaryVector.Y + yBoundaryVector.Y,
                         loc.Z + xBoundaryVector.Z + yBoundaryVector.Z),
        };
    }

    /// <summary>
    /// Build the planned-path "geodesic" boundary polygon: a strip along the
    /// vehicle's horizon buffer, widened by half the vehicle width. Cached
    /// per tick.
    /// </summary>
    private Location[] GetGeodesicBoundary(ActorId actorId)
    {
        if (_geodesicBoundaryMap.TryGetValue(actorId, out var cached))
            return cached;

        Location[] bbox = GetBoundary(actorId);
        Location[] geodesic;

        if (_bufferMap.TryGetValue(actorId, out var waypointBuffer) && waypointBuffer.Count > 0)
        {
            float bboxExtension = GetBoundingBoxExtension(actorId);
            float specificLeadDistance = _parameters.GetDistanceToLeadingVehicle(actorId);
            bboxExtension = MathF.Max(specificLeadDistance, bboxExtension);
            float bboxExtensionSquare = SquareF(bboxExtension);

            Vector3D dimensions = _simulationState.GetDimensions(actorId);
            float width = dimensions.Y;
            float length = dimensions.X;

            var targetWPInfo = LocalizationUtils.GetTargetWaypoint(waypointBuffer, length);
            SimpleWaypoint boundaryStart = targetWPInfo.Waypoint;
            int boundaryStartIndex = (int)targetWPInfo.Index;

            // Build left + right boundary strips.
            var leftBoundary = new List<Location>(8);
            var rightBoundary = new List<Location>(8);
            SimpleWaypoint? boundaryEnd = null;
            SimpleWaypoint currentPoint = waypointBuffer[boundaryStartIndex];

            bool reachedDistance = false;
            for (int j = boundaryStartIndex; !reachedDistance && j < waypointBuffer.Count; j++)
            {
                if (boundaryStart.DistanceSquared(currentPoint) > bboxExtensionSquare
                    || j == waypointBuffer.Count - 1)
                {
                    reachedDistance = true;
                }
                if (boundaryEnd is null
                    || Dot(boundaryEnd.GetForwardVector(), currentPoint.GetForwardVector())
                       < Constants.Collision.COS_10_DEGREES
                    || reachedDistance)
                {
                    Vector3D headingVector = currentPoint.GetForwardVector();
                    Location location = currentPoint.GetLocation();
                    Vector3D perpendicularVector = new(-headingVector.Y, headingVector.X, 0f);
                    perpendicularVector = MakeSafeUnitVector(perpendicularVector,
                        Constants.Collision.EPSILON);
                    Vector3D scaledPerpendicular = new(
                        perpendicularVector.X * width,
                        perpendicularVector.Y * width,
                        perpendicularVector.Z * width);
                    leftBoundary.Add(new Location(
                        location.X + scaledPerpendicular.X,
                        location.Y + scaledPerpendicular.Y,
                        location.Z + scaledPerpendicular.Z));
                    rightBoundary.Add(new Location(
                        location.X - scaledPerpendicular.X,
                        location.Y - scaledPerpendicular.Y,
                        location.Z - scaledPerpendicular.Z));
                    boundaryEnd = currentPoint;
                }
                currentPoint = waypointBuffer[j];
            }

            // Stitch the boundary: reverse(right) + bbox + left (forms a CCW
            // polygon usable by Polygon2D distance routines).
            geodesic = new Location[rightBoundary.Count + bbox.Length + leftBoundary.Count];
            int dst = 0;
            for (int i = rightBoundary.Count - 1; i >= 0; i--) geodesic[dst++] = rightBoundary[i];
            for (int i = 0; i < bbox.Length; i++) geodesic[dst++] = bbox[i];
            for (int i = 0; i < leftBoundary.Count; i++) geodesic[dst++] = leftBoundary[i];
        }
        else
        {
            geodesic = bbox;
        }

        _geodesicBoundaryMap[actorId] = geodesic;
        return geodesic;
    }

    /// <summary>
    /// Compute (and cache) the four polygon-distance values used by the
    /// negotiation rule. Symmetric in the pair, so the cache is keyed on
    /// (min, max) packed into a uint64.
    /// </summary>
    private GeometryComparison GetGeometryBetweenActors(
        ActorId referenceVehicleId, ActorId otherActorId)
    {
        ActorId a = referenceVehicleId < otherActorId ? referenceVehicleId : otherActorId;
        ActorId b = referenceVehicleId < otherActorId ? otherActorId : referenceVehicleId;
        ulong key = ((ulong)a << 32) | b;

        if (_geometryCache.TryGetValue(key, out var cached))
        {
            // The cache stored the comparison from a's POV; if we are asking
            // from b's POV we need to swap the directed distances.
            if (referenceVehicleId == a)
                return cached;
            return new GeometryComparison
            {
                ReferenceVehicleToOtherGeodesic = cached.OtherVehicleToReferenceGeodesic,
                OtherVehicleToReferenceGeodesic = cached.ReferenceVehicleToOtherGeodesic,
                InterGeodesicDistance = cached.InterGeodesicDistance,
                InterBboxDistance = cached.InterBboxDistance,
            };
        }

        Location[] referencePolygon = GetBoundary(referenceVehicleId);
        Location[] otherPolygon = GetBoundary(otherActorId);
        Location[] referenceGeodesic = GetGeodesicBoundary(referenceVehicleId);
        Location[] otherGeodesic = GetGeodesicBoundary(otherActorId);

        double refToOtherGeo = Polygon2D.Distance(referencePolygon, otherGeodesic);
        double otherToRefGeo = Polygon2D.Distance(otherPolygon, referenceGeodesic);
        double interGeoDist = Polygon2D.Distance(referenceGeodesic, otherGeodesic);
        double interBboxDist = Polygon2D.Distance(referencePolygon, otherPolygon);

        var result = new GeometryComparison
        {
            ReferenceVehicleToOtherGeodesic = refToOtherGeo,
            OtherVehicleToReferenceGeodesic = otherToRefGeo,
            InterGeodesicDistance = interGeoDist,
            InterBboxDistance = interBboxDist,
        };

        // Store using canonical (a, b) ordering — swap if reference != a.
        if (referenceVehicleId == a)
        {
            _geometryCache[key] = result;
        }
        else
        {
            _geometryCache[key] = new GeometryComparison
            {
                ReferenceVehicleToOtherGeodesic = otherToRefGeo,
                OtherVehicleToReferenceGeodesic = refToOtherGeo,
                InterGeodesicDistance = interGeoDist,
                InterBboxDistance = interBboxDist,
            };
        }
        return result;
    }

    // ═════════════════════════════════════════════════════════════════
    //                       Helpers (inline math)
    // ═════════════════════════════════════════════════════════════════

    private static float SquareF(float v) => v * v;

    private static float DistanceSquared(Location a, Location b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    private static float Dot(Vector3D a, Vector3D b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    private static Vector3D MakeSafeUnitVector(Vector3D v, float epsilon)
    {
        float length = MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        float k = length > MathF.Max(epsilon, 0f) ? 1f / length : 1f;
        return new Vector3D(v.X * k, v.Y * k, v.Z * k);
    }

    private void InsertionSortByDistance(List<ActorId> ids, Location pivot)
    {
        for (int i = 1; i < ids.Count; i++)
        {
            ActorId key = ids[i];
            float keyD = DistanceSquared(_simulationState.GetLocation(key), pivot);
            int j = i - 1;
            while (j >= 0)
            {
                float dj = DistanceSquared(_simulationState.GetLocation(ids[j]), pivot);
                if (dj <= keyD) break;
                ids[j + 1] = ids[j];
                j--;
            }
            ids[j + 1] = key;
        }
    }

    // ─── Internal POD types ─────────────────────────────────────────────

    private struct CollisionLock
    {
        public double DistanceToLeadVehicle;
        public double InitialLockDistance;
        public ActorId LeadVehicleId;
    }

    private struct GeometryComparison
    {
        public double ReferenceVehicleToOtherGeodesic;
        public double OtherVehicleToReferenceGeodesic;
        public double InterGeodesicDistance;
        public double InterBboxDistance;
    }
}

/// <summary>
/// Minimal 2-D polygon distance helper (replaces boost::geometry). All input
/// polygons are convex, simple, and small (4–12 vertices), so the
/// brute-force "closest point on edge" pass is O(n*m) and faster than any
/// fancy alternative.
/// </summary>
internal static class Polygon2D
{
    /// <summary>
    /// Minimum 2-D distance between two polygons. Returns 0 if they
    /// intersect or share any edge / vertex; otherwise the smallest
    /// segment-to-segment distance.
    /// </summary>
    public static double Distance(Location[] a, Location[] b)
    {
        if (a.Length == 0 || b.Length == 0) return double.PositiveInfinity;

        // First check: any vertex of one inside the other ⇒ distance 0.
        // This catches the bg::distance == 0 case for overlapping polygons.
        for (int i = 0; i < a.Length; i++)
            if (PointInPolygon(a[i], b)) return 0.0;
        for (int i = 0; i < b.Length; i++)
            if (PointInPolygon(b[i], a)) return 0.0;

        // Minimum edge-to-edge distance.
        double minDist = double.PositiveInfinity;
        int na = a.Length, nb = b.Length;
        for (int i = 0; i < na; i++)
        {
            Location ai = a[i];
            Location aj = a[(i + 1) % na];
            for (int k = 0; k < nb; k++)
            {
                Location bk = b[k];
                Location bl = b[(k + 1) % nb];
                double d = SegmentSegmentDistance(ai, aj, bk, bl);
                if (d < minDist) minDist = d;
                if (minDist <= 0.0) return 0.0;
            }
        }
        return minDist;
    }

    private static bool PointInPolygon(Location p, Location[] poly)
    {
        // Standard ray-casting; treats polygon as a closed loop in XY.
        bool inside = false;
        int n = poly.Length;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            float yi = poly[i].Y, yj = poly[j].Y;
            float xi = poly[i].X, xj = poly[j].X;
            bool intersect = ((yi > p.Y) != (yj > p.Y))
                && (p.X < (xj - xi) * (p.Y - yi) / (yj - yi + 1e-12f) + xi);
            if (intersect) inside = !inside;
        }
        return inside;
    }

    private static double SegmentSegmentDistance(
        Location p1, Location p2, Location p3, Location p4)
    {
        // 2-D only. If segments intersect, distance = 0.
        if (SegmentsIntersect(p1, p2, p3, p4)) return 0.0;
        double d1 = PointToSegmentDistance(p3, p1, p2);
        double d2 = PointToSegmentDistance(p4, p1, p2);
        double d3 = PointToSegmentDistance(p1, p3, p4);
        double d4 = PointToSegmentDistance(p2, p3, p4);
        return Math.Min(Math.Min(d1, d2), Math.Min(d3, d4));
    }

    private static bool SegmentsIntersect(Location p1, Location p2, Location p3, Location p4)
    {
        double d1 = Cross2(p4.X - p3.X, p4.Y - p3.Y, p1.X - p3.X, p1.Y - p3.Y);
        double d2 = Cross2(p4.X - p3.X, p4.Y - p3.Y, p2.X - p3.X, p2.Y - p3.Y);
        double d3 = Cross2(p2.X - p1.X, p2.Y - p1.Y, p3.X - p1.X, p3.Y - p1.Y);
        double d4 = Cross2(p2.X - p1.X, p2.Y - p1.Y, p4.X - p1.X, p4.Y - p1.Y);
        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0))
            && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
            return true;
        return false;
    }

    private static double Cross2(double ax, double ay, double bx, double by) => ax * by - ay * bx;

    private static double PointToSegmentDistance(Location p, Location a, Location b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double lengthSq = dx * dx + dy * dy;
        if (lengthSq < 1e-12) return Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));
        double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSq;
        if (t < 0) t = 0; else if (t > 1) t = 1;
        double cx = a.X + t * dx;
        double cy = a.Y + t * dy;
        return Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy));
    }
}
