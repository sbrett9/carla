// Copyright (c) 2026 Computer Vision Center (CVC) at the Universitat Autonoma
// de Barcelona (UAB).
//
// This work is licensed under the terms of the MIT license.
// For a copy, see <https://opensource.org/licenses/MIT>.

#pragma once

#include <memory>
#include <vector>

#include <carla/geom/Mesh.h>
#include <carla/road/Road.h>
#include <carla/road/LaneSection.h>
#include <carla/road/Lane.h>
#include <carla/rpc/OpendriveGenerationParameters.h>

namespace carla {
namespace geom {

  /// Mesh helper generator
  class MeshFactory {
  public:

    MeshFactory(rpc::OpendriveGenerationParameters params =
        rpc::OpendriveGenerationParameters());

    // =========================================================================
    // -- Map Related ----------------------------------------------------------
    // =========================================================================

    // -- Basic --

    /// Generates a mesh that defines a road
    std::unique_ptr<Mesh> Generate(const road::Road &road) const;

    /// Generates a mesh that defines a lane section
    std::unique_ptr<Mesh> Generate(const road::LaneSection &lane_section) const;

    /// Generates a mesh that defines a lane from a given s start and end
    std::unique_ptr<Mesh> Generate(
        const road::Lane &lane, const double s_start, const double s_end) const;

    /// Generates a mesh that defines a lane from a given s start and end with bigger tesselation
    std::unique_ptr<Mesh> GenerateTesselated(
      const road::Lane& lane, const double s_start, const double s_end) const;

    /// Generates a mesh that defines the whole lane
    std::unique_ptr<Mesh> Generate(const road::Lane &lane) const;

    /// Generates a mesh that defines the whole lane with bigger tesselation
    std::unique_ptr<Mesh> GenerateTesselated(const road::Lane& lane) const;

    /// Generates a mesh that defines a lane section
    void GenerateLaneSectionOrdered(const road::LaneSection &lane_section,
        std::map<carla::road::Lane::LaneType , std::vector<std::unique_ptr<Mesh>>>& result ) const;

    std::unique_ptr<Mesh> GenerateSidewalk(const road::LaneSection &lane_section) const;
    std::unique_ptr<Mesh> GenerateSidewalk(const road::Lane &lane) const;
    std::unique_ptr<Mesh> GenerateSidewalk(const road::Lane &lane, const double s_start, const double s_end) const;
    // -- Walls --

    /// Genrates a mesh representing a wall on the road corners to avoid
    /// cars falling down
    std::unique_ptr<Mesh> GenerateWalls(const road::LaneSection &lane_section) const;

    /// Generates a wall-like mesh at the right side of the lane
    std::unique_ptr<Mesh> GenerateRightWall(
        const road::Lane &lane, const double s_start, const double s_end) const;

    /// Generates a wall-like mesh at the left side of the lane
    std::unique_ptr<Mesh> GenerateLeftWall(
        const road::Lane &lane, const double s_start, const double s_end) const;

    // -- Chunked --

    /// Generates a list of meshes that defines a road with a maximum length
    std::vector<std::unique_ptr<Mesh>> GenerateWithMaxLen(
        const road::Road &road) const;

    /// Generates a list of meshes that defines a lane_section with a maximum length
    std::vector<std::unique_ptr<Mesh>> GenerateWithMaxLen(
        const road::LaneSection &lane_section) const;

    /// Generates a list of meshes that defines a road with a maximum length
    std::map<carla::road::Lane::LaneType , std::vector<std::unique_ptr<Mesh>>> GenerateOrderedWithMaxLen(
        const road::Road &road) const;

    /// Generates a list of meshes that defines a lane_section with a maximum length
    std::map<carla::road::Lane::LaneType , std::vector<std::unique_ptr<Mesh>>> GenerateOrderedWithMaxLen(
        const road::LaneSection &lane_section) const;

    /// Generates a list of meshes that defines a road safety wall with a maximum length
    std::vector<std::unique_ptr<Mesh>> GenerateWallsWithMaxLen(
        const road::Road &road) const;

    /// Generates a list of meshes that defines a lane_section safety wall with a maximum length
    std::vector<std::unique_ptr<Mesh>> GenerateWallsWithMaxLen(
        const road::LaneSection &lane_section) const;

    // -- Util --

    /// Generates a chunked road with all the features needed for simulation
    std::vector<std::unique_ptr<Mesh>> GenerateAllWithMaxLen(
        const road::Road &road) const;


    void GenerateAllOrderedWithMaxLen(const road::Road &road,
         std::map<road::Lane::LaneType , std::vector<std::unique_ptr<Mesh>>>& roads) const;

    std::unique_ptr<Mesh> MergeAndSmooth(std::vector<std::unique_ptr<Mesh>> &lane_meshes) const;

    /// Resolves every drivable lane strip into one continuous surface per height layer,
    /// returned as tiles that share their vertices across tile boundaries.
    ///
    /// Meshing each lane separately leaves the network as hundreds of overlapping ribbons:
    /// inside a junction the turning paths overlap and disagree about height while the
    /// asphalt between them is never covered at all, and between roads every boundary is a
    /// pair of unwelded edges. Sampling the strips into a height field, resolving one
    /// height per plan position, paving the enclosed gaps and triangulating once removes
    /// all of it — the result shares vertices across every cell boundary, so it is
    /// continuous by construction rather than smoothed afterwards.
    ///
    /// Layers are kept apart so a grade separation is not collapsed: a road that passes
    /// beneath a deck must not be welded to it. A layer is a height function of plan
    /// position, so growth stops where the surface passes over itself.
    ///
    /// \param tile_size Edge length of the emitted tiles. Corner heights are resolved
    ///        across the whole layer first, so neighbouring tiles place identical
    ///        vertices on their shared edge.
    std::vector<std::unique_ptr<Mesh>> ResolveDrivableSurface(
        const std::vector<std::unique_ptr<Mesh>> &lane_meshes,
        float tile_size) const;

    // -- LaneMarks --
    void GenerateLaneMarkForRoad(const road::Road& road,
      std::vector<std::unique_ptr<Mesh>>& inout,
      std::vector<std::string>& outinfo ) const;

    // Generate for NOT center line AKA All lines but the one which id 0
    void GenerateLaneMarksForNotCenterLine(
      const road::LaneSection& lane_section,
      const road::Lane& lane,
      std::vector<std::unique_ptr<Mesh>>& inout,
      std::vector<std::string>& outinfo ) const;

    // Generate marks ONLY for line with ID 0
    void GenerateLaneMarksForCenterLine(
      const road::Road& road,
      const road::LaneSection& lane_section,
      const road::Lane& lane,
      std::vector<std::unique_ptr<Mesh>>& inout,
      std::vector<std::string>& outinfo ) const;
    // =========================================================================
    // -- Generation parameters ------------------------------------------------
    // =========================================================================

    /// Parameters for the road generation
    struct RoadParameters {
      float resolution                  =  2.0f;
      float max_road_len                = 50.0f;
      float extra_lane_width            =  1.0f;
      float wall_height                 =  0.6f;
      float vertex_width_resolution     =  4.0f;
      // Road mesh smoothness:
      float max_weight_distance         =  5.0f;
      float same_lane_weight_multiplier =  2.0f;
      float lane_ends_multiplier        =  2.0f;
      // Junction surface resolution. The cell size is the knee of the measured
      // curve: halving it quadruples the vertex count for no gain in boundary
      // accuracy, while doubling it stops the gap filling resolving the narrow
      // spaces between connector paths.
      float junction_cell_size          =  0.5f;
      // Height difference above which two overlapping surfaces are separate
      // layers rather than one surface sampled twice. Above the worst
      // same-layer disagreement measured and below the shallowest clearance.
      float junction_layer_separation   =  3.0f;
      // Largest enclosed gap that is paved, in square metres. A gap the
      // surface closes around is a hole a wheel drops into; a gap that opens
      // outward is the space beside the road, and is left alone whatever its
      // size. Among enclosed gaps, size is the only separator available and it
      // is not a clean one: on Arapahoe_I25 the holes confirmed against the
      // photoreal run from 11.2 to 28.2 m2 and the enclosed ground confirmed to
      // need leaving alone starts at 521.5 m2, but ten regions lie between and
      // none has been checked. This sits above every confirmed hole with room
      // to spare and well below every confirmed island, so the unchecked middle
      // is left unpaved rather than guessed at.
      float junction_max_gap_area       = 100.0f;
      // How far a paved gap may sit from the surface around it. The layer
      // separation is the wrong tolerance: it asks whether two cells belong to
      // one sheet, which a deck and the ramp beside it can, while this asks
      // whether bridging them would invent a slope.
      float junction_fill_tolerance     =  0.5f;
      // Widest sliver between two lane quads that is paved over. Wide enough for
      // the cracks measured, which are a cell or two across, and far narrower
      // than the gap between a deck and the road beneath it.
      float junction_crack_span         =  1.0f;
      // Neighbour-averaging passes over the resolved height field, removing the
      // flips left where the lower of two overlapping surfaces changes from cell
      // to cell. Four is the measured knee: more barely reduces the remaining
      // roughness while moving the surface further from its samples.
      int   junction_relax_passes       =  4;
    };

    RoadParameters road_param;

  private:

    // Calculate the points on both sides of the lane mark for the specified s_current
    std::pair<geom::Vector3D, geom::Vector3D> ComputeEdgesForLanemark(
      const road::LaneSection& lane_section,
      const road::Lane& lane,
      const double s_current,
      const double lanemark_width,
      const float extra_width) const;

  };

} // namespace geom
} // namespace carla
