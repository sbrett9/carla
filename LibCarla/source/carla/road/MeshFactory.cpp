// Copyright (c) 2026 Computer Vision Center (CVC) at the Universitat Autonoma
// de Barcelona (UAB).
//
// This work is licensed under the terms of the MIT license.
// For a copy, see <https://opensource.org/licenses/MIT>.

#include <carla/road/MeshFactory.h>

#include <algorithm>
#include <array>
#include <cmath>
#include <limits>
#include <unordered_map>
#include <vector>

#include <carla/geom/Vector3D.h>
#include <carla/geom/Rtree.h>
#include <carla/road/element/LaneMarking.h>
#include <carla/road/element/RoadInfoMarkRecord.h>
#include <carla/road/Map.h>
#include <carla/road/Deformation.h>

namespace carla {
namespace geom {

  MeshFactory::MeshFactory(rpc::OpendriveGenerationParameters params) {
    road_param.resolution = static_cast<float>(params.vertex_distance);
    road_param.max_road_len = static_cast<float>(params.max_road_length);
    road_param.extra_lane_width = static_cast<float>(params.additional_width);
    road_param.wall_height = static_cast<float>(params.wall_height);
    road_param.vertex_width_resolution = static_cast<float>(params.vertex_width_resolution);
  }

  /// We use this epsilon to shift the waypoints away from the edges of the lane
  /// sections to avoid floating point precision errors.
  static constexpr double EPSILON = 10.0 * std::numeric_limits<double>::epsilon();
  static constexpr double MESH_EPSILON = 50.0 * std::numeric_limits<double>::epsilon();

  std::unique_ptr<Mesh> MeshFactory::Generate(const road::Road &road) const {
    Mesh out_mesh;
    for (auto &&lane_section : road.GetLaneSections()) {
      out_mesh += *Generate(lane_section);
    }
    return std::make_unique<Mesh>(out_mesh);
  }

  std::unique_ptr<Mesh> MeshFactory::Generate(const road::LaneSection &lane_section) const {
    Mesh out_mesh;
    for (auto &&lane_pair : lane_section.GetLanes()) {
      out_mesh += *Generate(lane_pair.second);
    }
    return std::make_unique<Mesh>(out_mesh);
  }

  std::unique_ptr<Mesh> MeshFactory::Generate(const road::Lane &lane) const {
    const double s_start = lane.GetDistance() + EPSILON;
    const double s_end = lane.GetDistance() + lane.GetLength() - EPSILON;
    return Generate(lane, s_start, s_end);
  }

  std::unique_ptr<Mesh> MeshFactory::GenerateTesselated(const road::Lane& lane) const {
    const double s_start = lane.GetDistance() + EPSILON;
    const double s_end = lane.GetDistance() + lane.GetLength() - EPSILON;
    return GenerateTesselated(lane, s_start, s_end);
  }

  std::unique_ptr<Mesh> MeshFactory::Generate(
      const road::Lane &lane, const double s_start, const double s_end) const {
    RELEASE_ASSERT(road_param.resolution > 0.0);
    DEBUG_ASSERT(s_start >= 0.0);
    DEBUG_ASSERT(s_end <= lane.GetDistance() + lane.GetLength());
    DEBUG_ASSERT(s_end >= EPSILON);
    DEBUG_ASSERT(s_start < s_end);
    // The lane with lane_id 0 have no physical representation in OpenDRIVE
    Mesh out_mesh;
    if (lane.GetId() == 0) {
      return std::make_unique<Mesh>(out_mesh);
    }
    double s_current = s_start;

    std::vector<geom::Vector3D> vertices;
    if (lane.IsStraight()) {
      // Mesh optimization: If the lane is straight just add vertices at the
      // begining and at the end of it
      const auto edges = lane.GetCornerPositions(s_current, road_param.extra_lane_width);
      vertices.push_back(edges.first);
      vertices.push_back(edges.second);
    } else {
      // Iterate over the lane's 's' and store the vertices based on it's width
      do {
        // Get the location of the edges of the current lane at the current waypoint
        const auto edges = lane.GetCornerPositions(s_current, road_param.extra_lane_width);
        vertices.push_back(edges.first);
        vertices.push_back(edges.second);

        // Update the current waypoint's "s"
        s_current += road_param.resolution;
      } while(s_current < s_end);
    }

    // This ensures the mesh is constant and have no gaps between roads,
    // adding geometry at the very end of the lane
    if (s_end - (s_current - road_param.resolution) > EPSILON) {
      const auto edges = lane.GetCornerPositions(s_end - MESH_EPSILON, road_param.extra_lane_width);
      vertices.push_back(edges.first);
      vertices.push_back(edges.second);
    }

    // Add the adient material, create the strip and close the material
    out_mesh.AddMaterial(
        lane.GetType() == road::Lane::LaneType::Sidewalk ? "sidewalk" : "road");
    out_mesh.AddTriangleStrip(vertices);
    out_mesh.EndMaterial();
    return std::make_unique<Mesh>(out_mesh);
  }

  std::unique_ptr<Mesh> MeshFactory::GenerateTesselated(
    const road::Lane& lane, const double s_start, const double s_end) const {
    RELEASE_ASSERT(road_param.resolution > 0.0);
    DEBUG_ASSERT(s_start >= 0.0);
    DEBUG_ASSERT(s_end <= lane.GetDistance() + lane.GetLength());
    DEBUG_ASSERT(s_end >= EPSILON);
    DEBUG_ASSERT(s_start < s_end);
    // The lane with lane_id 0 have no physical representation in OpenDRIVE
    Mesh out_mesh;
    if (lane.GetId() == 0) {
      return std::make_unique<Mesh>(out_mesh);
    }
    double s_current = s_start;

    std::vector<geom::Vector3D> vertices;
    // Ensure minimum vertices in width are two
    const size_t vertices_in_width = road_param.vertex_width_resolution >= 2 ? static_cast<size_t>(road_param.vertex_width_resolution) : size_t{2};
    const size_t segments_number = vertices_in_width - 1;

    std::vector<geom::Vector2D> uvs;
    int uvx = 0;
    int uvy = 0;
    // Iterate over the lane's 's' and store the vertices based on it's width
    do {
      // Get the location of the edges of the current lane at the current waypoint
      std::pair<geom::Vector3D, geom::Vector3D> edges = lane.GetCornerPositions(s_current, road_param.extra_lane_width);
      const geom::Vector3D segments_size = ( edges.second - edges.first ) / static_cast<float>(segments_number);
      geom::Vector3D current_vertex = edges.first;
      uvx = 0;
      for (size_t i = 0; i < vertices_in_width; ++i) {
        uvs.push_back(geom::Vector2D(static_cast<float>(uvx), static_cast<float>(uvy)));
        vertices.push_back(current_vertex);
        current_vertex = current_vertex + segments_size;
        uvx++;
      }
      uvy++;
      // Update the current waypoint's "s"
      s_current += road_param.resolution;
    } while (s_current < s_end);

    // This ensures the mesh is constant and have no gaps between roads,
    // adding geometry at the very end of the lane

    if (s_end - (s_current - road_param.resolution) > EPSILON) {
      std::pair<carla::geom::Vector3D, carla::geom::Vector3D> edges =
        lane.GetCornerPositions(s_end - MESH_EPSILON, road_param.extra_lane_width);
      const geom::Vector3D segments_size = (edges.second - edges.first) / static_cast<float>(segments_number);
      geom::Vector3D current_vertex = edges.first;
      uvx = 0;
      for (size_t i = 0; i < vertices_in_width; ++i)
      {
        uvs.push_back(geom::Vector2D(static_cast<float>(uvx), static_cast<float>(uvy)));
        vertices.push_back(current_vertex);
        current_vertex = current_vertex + segments_size;
        uvx++;
      }
    }
    out_mesh.AddVertices(vertices);
    out_mesh.AddUVs(uvs);

    // Add the adient material, create the strip and close the material
    out_mesh.AddMaterial(
      lane.GetType() == road::Lane::LaneType::Sidewalk ? "sidewalk" : "road");

    const size_t number_of_rows = (vertices.size() / vertices_in_width);

    for (size_t i = 0; i < (number_of_rows - 1); ++i) {
      for (size_t j = 0; j < vertices_in_width - 1; ++j) {
        out_mesh.AddIndex(   j       + (   i       * vertices_in_width ) + 1);
        out_mesh.AddIndex( ( j + 1 ) + (   i       * vertices_in_width ) + 1);
        out_mesh.AddIndex(   j       + ( ( i + 1 ) * vertices_in_width ) + 1);

        out_mesh.AddIndex( ( j + 1 ) + (   i       * vertices_in_width ) + 1);
        out_mesh.AddIndex( ( j + 1 ) + ( ( i + 1 ) * vertices_in_width ) + 1);
        out_mesh.AddIndex(   j       + ( ( i + 1 ) * vertices_in_width ) + 1);
      }
    }
    out_mesh.EndMaterial();
    return std::make_unique<Mesh>(out_mesh);
  }


  void MeshFactory::GenerateLaneSectionOrdered(
    const road::LaneSection &lane_section,
    std::map<carla::road::Lane::LaneType , std::vector<std::unique_ptr<Mesh>>>& result) const {

    const size_t vertices_in_width = road_param.vertex_width_resolution >= 2 ? static_cast<size_t>(road_param.vertex_width_resolution) : size_t{2};
    std::vector<size_t> redirections;
    for (auto &&lane_pair : lane_section.GetLanes()) {
      auto it = std::find(redirections.begin(), redirections.end(), static_cast<size_t>(lane_pair.first));
      if ( it == redirections.end() ) {
        redirections.push_back(static_cast<size_t>(lane_pair.first));
        it = std::find(redirections.begin(), redirections.end(), static_cast<size_t>(lane_pair.first));
      }
      size_t PosToAdd = static_cast<size_t>(it - redirections.begin());

      Mesh out_mesh;
      if(lane_pair.second.GetType() == road::Lane::LaneType::Driving ){
        out_mesh += *GenerateTesselated(lane_pair.second);
      }else{
        out_mesh += *GenerateSidewalk(lane_pair.second);
      }

      if( result[lane_pair.second.GetType()].size() <= PosToAdd ){
        result[lane_pair.second.GetType()].push_back(std::make_unique<Mesh>(out_mesh));
      } else {
        uint32_t verticesinwidth  = 0;
        if(lane_pair.second.GetType() == road::Lane::LaneType::Driving) {
          verticesinwidth = static_cast<uint32_t>(vertices_in_width);
        }else if(lane_pair.second.GetType() == road::Lane::LaneType::Sidewalk){
          verticesinwidth = 6;
        }else{
          verticesinwidth = 2;
        }
        (result[lane_pair.second.GetType()][PosToAdd])->ConcatMesh(out_mesh, static_cast<int>(verticesinwidth));
      }
    }
  }


  std::unique_ptr<Mesh> MeshFactory::GenerateSidewalk(const road::LaneSection &lane_section) const{
    Mesh out_mesh;
    for (auto &&lane_pair : lane_section.GetLanes()) {
      const double s_start = lane_pair.second.GetDistance() + EPSILON;
      const double s_end = lane_pair.second.GetDistance() + lane_pair.second.GetLength() - EPSILON;
      out_mesh += *GenerateSidewalk(lane_pair.second, s_start, s_end);
    }
    return std::make_unique<Mesh>(out_mesh);
  }
  std::unique_ptr<Mesh> MeshFactory::GenerateSidewalk(const road::Lane &lane) const{
    const double s_start = lane.GetDistance() + EPSILON;
    const double s_end = lane.GetDistance() + lane.GetLength() - EPSILON;
    return GenerateSidewalk(lane, s_start, s_end);
  }
  std::unique_ptr<Mesh> MeshFactory::GenerateSidewalk(
    const road::Lane &lane, const double s_start,
    const double s_end ) const {

    RELEASE_ASSERT(road_param.resolution > 0.0);
    DEBUG_ASSERT(s_start >= 0.0);
    DEBUG_ASSERT(s_end <= lane.GetDistance() + lane.GetLength());
    DEBUG_ASSERT(s_end >= EPSILON);
    DEBUG_ASSERT(s_start < s_end);
    // The lane with lane_id 0 have no physical representation in OpenDRIVE
    Mesh out_mesh;
    if (lane.GetId() == 0) {
      return std::make_unique<Mesh>(out_mesh);
    }
    double s_current = s_start;

    std::vector<geom::Vector3D> vertices;
    // Ensure minimum vertices in width are two
    const size_t vertices_in_width = 6;
    std::vector<geom::Vector2D> uvs;
    int uvy = 0;

    // Iterate over the lane's 's' and store the vertices based on it's width
    do {
      // Get the location of the edges of the current lane at the current waypoint
      std::pair<geom::Vector3D, geom::Vector3D> edges =
        lane.GetCornerPositions(s_current, road_param.extra_lane_width);

      geom::Vector3D low_vertex_first = edges.first - geom::Vector3D(0,0,1);
      geom::Vector3D low_vertex_second = edges.second - geom::Vector3D(0,0,1);
      vertices.push_back(low_vertex_first);
      uvs.push_back(geom::Vector2D(0.0f, static_cast<float>(uvy)));

      vertices.push_back(edges.first);
      uvs.push_back(geom::Vector2D(1.0f, static_cast<float>(uvy)));

      vertices.push_back(edges.first);
      uvs.push_back(geom::Vector2D(1.0f, static_cast<float>(uvy)));

      vertices.push_back(edges.second);
      uvs.push_back(geom::Vector2D(2.0f, static_cast<float>(uvy)));

      vertices.push_back(edges.second);
      uvs.push_back(geom::Vector2D(2.0f, static_cast<float>(uvy)));

      vertices.push_back(low_vertex_second);
      uvs.push_back(geom::Vector2D(3.0f, static_cast<float>(uvy)));

      // Update the current waypoint's "s"
      s_current += road_param.resolution;
      uvy++;
    } while (s_current < s_end);

    // This ensures the mesh is constant and have no gaps between roads,
    // adding geometry at the very end of the lane

    if (s_end - (s_current - road_param.resolution) > EPSILON) {
      std::pair<carla::geom::Vector3D, carla::geom::Vector3D> edges =
        lane.GetCornerPositions(s_end - MESH_EPSILON, road_param.extra_lane_width);

      geom::Vector3D low_vertex_first = edges.first - geom::Vector3D(0,0,1);
      geom::Vector3D low_vertex_second = edges.second - geom::Vector3D(0,0,1);

      vertices.push_back(low_vertex_first);
      uvs.push_back(geom::Vector2D(0.0f, static_cast<float>(uvy)));

      vertices.push_back(edges.first);
      uvs.push_back(geom::Vector2D(1.0f, static_cast<float>(uvy)));

      vertices.push_back(edges.first);
      uvs.push_back(geom::Vector2D(1.0f, static_cast<float>(uvy)));

      vertices.push_back(edges.second);
      uvs.push_back(geom::Vector2D(2.0f, static_cast<float>(uvy)));

      vertices.push_back(edges.second);
      uvs.push_back(geom::Vector2D(2.0f, static_cast<float>(uvy)));

      vertices.push_back(low_vertex_second);
      uvs.push_back(geom::Vector2D(3.0f, static_cast<float>(uvy)));

    }

    out_mesh.AddVertices(vertices);
    out_mesh.AddUVs(uvs);
    // Add the adient material, create the strip and close the material
    out_mesh.AddMaterial(
      lane.GetType() == road::Lane::LaneType::Sidewalk ? "sidewalk" : "road");

    const size_t number_of_rows = (vertices.size() / vertices_in_width);

    for (size_t i = 0; i < (number_of_rows - 1); ++i) {
      for (size_t j = 0; j < vertices_in_width - 1; ++j) {

        if(j == 1 || j == 3){
          continue;
        }

        out_mesh.AddIndex(   j       + (   i       * vertices_in_width ) + 1);
        out_mesh.AddIndex( ( j + 1 ) + (   i       * vertices_in_width ) + 1);
        out_mesh.AddIndex(   j       + ( ( i + 1 ) * vertices_in_width ) + 1);

        out_mesh.AddIndex( ( j + 1 ) + (   i       * vertices_in_width ) + 1);
        out_mesh.AddIndex( ( j + 1 ) + ( ( i + 1 ) * vertices_in_width ) + 1);
        out_mesh.AddIndex(   j       + ( ( i + 1 ) * vertices_in_width ) + 1);

      }
    }
    out_mesh.EndMaterial();
    return std::make_unique<Mesh>(out_mesh);
  }
  std::unique_ptr<Mesh> MeshFactory::GenerateWalls(const road::LaneSection &lane_section) const {
    Mesh out_mesh;

    const auto min_lane = lane_section.GetLanes().begin()->first == 0 ?
        1 : lane_section.GetLanes().begin()->first;
    const auto max_lane = lane_section.GetLanes().rbegin()->first == 0 ?
        -1 : lane_section.GetLanes().rbegin()->first;

    for (auto &&lane_pair : lane_section.GetLanes()) {
      const auto &lane = lane_pair.second;
      const double s_start = lane.GetDistance() + EPSILON;
      const double s_end = lane.GetDistance() + lane.GetLength() - EPSILON;
      if (lane.GetId() == max_lane) {
        out_mesh += *GenerateLeftWall(lane, s_start, s_end);
      }
      if (lane.GetId() == min_lane) {
        out_mesh += *GenerateRightWall(lane, s_start, s_end);
      }
    }
    return std::make_unique<Mesh>(out_mesh);
  }

  std::unique_ptr<Mesh> MeshFactory::GenerateRightWall(
      const road::Lane &lane, const double s_start, const double s_end) const {
    RELEASE_ASSERT(road_param.resolution > 0.0);
    DEBUG_ASSERT(s_start >= 0.0);
    DEBUG_ASSERT(s_end <= lane.GetDistance() + lane.GetLength());
    DEBUG_ASSERT(s_end >= EPSILON);
    DEBUG_ASSERT(s_start < s_end);
    // The lane with lane_id 0 have no physical representation in OpenDRIVE
    Mesh out_mesh;
    if (lane.GetId() == 0) {
      return std::make_unique<Mesh>(out_mesh);
    }
    double s_current = s_start;
    const geom::Vector3D height_vector = geom::Vector3D(0.f, 0.f, road_param.wall_height);

    std::vector<geom::Vector3D> r_vertices;
    if (lane.IsStraight()) {
      // Mesh optimization: If the lane is straight just add vertices at the
      // begining and at the end of it
      const auto edges = lane.GetCornerPositions(s_current, road_param.extra_lane_width);
      r_vertices.push_back(edges.first + height_vector);
      r_vertices.push_back(edges.first);
    } else {
      // Iterate over the lane's 's' and store the vertices based on it's width
      do {
        // Get the location of the edges of the current lane at the current waypoint
        const auto edges = lane.GetCornerPositions(s_current, road_param.extra_lane_width);
        r_vertices.push_back(edges.first + height_vector);
        r_vertices.push_back(edges.first);

        // Update the current waypoint's "s"
        s_current += road_param.resolution;
      } while(s_current < s_end);
    }

    // This ensures the mesh is constant and have no gaps between roads,
    // adding geometry at the very end of the lane
    if (s_end - (s_current - road_param.resolution) > EPSILON) {
      const auto edges = lane.GetCornerPositions(s_end - MESH_EPSILON, road_param.extra_lane_width);
      r_vertices.push_back(edges.first + height_vector);
      r_vertices.push_back(edges.first);
    }

    // Add the adient material, create the strip and close the material
    out_mesh.AddMaterial(
        lane.GetType() == road::Lane::LaneType::Sidewalk ? "sidewalk" : "road");
    out_mesh.AddTriangleStrip(r_vertices);
    out_mesh.EndMaterial();
    return std::make_unique<Mesh>(out_mesh);
  }

  std::unique_ptr<Mesh> MeshFactory::GenerateLeftWall(
      const road::Lane &lane, const double s_start, const double s_end) const {
    RELEASE_ASSERT(road_param.resolution > 0.0);
    DEBUG_ASSERT(s_start >= 0.0);
    DEBUG_ASSERT(s_end <= lane.GetDistance() + lane.GetLength());
    DEBUG_ASSERT(s_end >= EPSILON);
    DEBUG_ASSERT(s_start < s_end);
    // The lane with lane_id 0 have no physical representation in OpenDRIVE
    Mesh out_mesh;
    if (lane.GetId() == 0) {
      return std::make_unique<Mesh>(out_mesh);
    }
    double s_current = s_start;
    const geom::Vector3D height_vector = geom::Vector3D(0.f, 0.f, road_param.wall_height);

    std::vector<geom::Vector3D> l_vertices;
    if (lane.IsStraight()) {
      // Mesh optimization: If the lane is straight just add vertices at the
      // begining and at the end of it
      const auto edges = lane.GetCornerPositions(s_current, road_param.extra_lane_width);
      l_vertices.push_back(edges.second);
      l_vertices.push_back(edges.second + height_vector);
    } else {
      // Iterate over the lane's 's' and store the vertices based on it's width
      do {
        // Get the location of the edges of the current lane at the current waypoint
        const auto edges = lane.GetCornerPositions(s_current, road_param.extra_lane_width);
        l_vertices.push_back(edges.second);
        l_vertices.push_back(edges.second + height_vector);

        // Update the current waypoint's "s"
        s_current += road_param.resolution;
      } while(s_current < s_end);
    }

    // This ensures the mesh is constant and have no gaps between roads,
    // adding geometry at the very end of the lane
    if (s_end - (s_current - road_param.resolution) > EPSILON) {
      const auto edges = lane.GetCornerPositions(s_end - MESH_EPSILON, road_param.extra_lane_width);
      l_vertices.push_back(edges.second);
      l_vertices.push_back(edges.second + height_vector);
    }

    // Add the adient material, create the strip and close the material
    out_mesh.AddMaterial(
        lane.GetType() == road::Lane::LaneType::Sidewalk ? "sidewalk" : "road");
    out_mesh.AddTriangleStrip(l_vertices);
    out_mesh.EndMaterial();
    return std::make_unique<Mesh>(out_mesh);
  }

  std::vector<std::unique_ptr<Mesh>> MeshFactory::GenerateWithMaxLen(
      const road::Road &road) const {
    std::vector<std::unique_ptr<Mesh>> mesh_uptr_list;
    for (auto &&lane_section : road.GetLaneSections()) {
      auto section_uptr_list = GenerateWithMaxLen(lane_section);
      mesh_uptr_list.insert(
          mesh_uptr_list.end(),
          std::make_move_iterator(section_uptr_list.begin()),
          std::make_move_iterator(section_uptr_list.end()));
    }
    return mesh_uptr_list;
  }

  std::vector<std::unique_ptr<Mesh>> MeshFactory::GenerateWithMaxLen(
      const road::LaneSection &lane_section) const {
    std::vector<std::unique_ptr<Mesh>> mesh_uptr_list;
    if (lane_section.GetLength() < road_param.max_road_len) {
      mesh_uptr_list.emplace_back(Generate(lane_section));
    } else {
      double s_current = lane_section.GetDistance() + EPSILON;
      const double s_end = lane_section.GetDistance() + lane_section.GetLength() - EPSILON;
      while(s_current + road_param.max_road_len < s_end) {
        const auto s_until = s_current + road_param.max_road_len;
        Mesh lane_section_mesh;
        for (auto &&lane_pair : lane_section.GetLanes()) {
          lane_section_mesh += *Generate(lane_pair.second, s_current, s_until);
        }
        mesh_uptr_list.emplace_back(std::make_unique<Mesh>(lane_section_mesh));
        s_current = s_until;
      }
      if (s_end - s_current > EPSILON) {
        Mesh lane_section_mesh;
        for (auto &&lane_pair : lane_section.GetLanes()) {
          lane_section_mesh += *Generate(lane_pair.second, s_current, s_end);
        }
        mesh_uptr_list.emplace_back(std::make_unique<Mesh>(lane_section_mesh));
      }
    }
    return mesh_uptr_list;
  }

std::map<road::Lane::LaneType , std::vector<std::unique_ptr<Mesh>>> MeshFactory::GenerateOrderedWithMaxLen(
      const road::Road &road) const {
    std::map<road::Lane::LaneType , std::vector<std::unique_ptr<Mesh>>> mesh_uptr_list;
    for (auto &&lane_section : road.GetLaneSections()) {
      std::map<road::Lane::LaneType , std::vector<std::unique_ptr<Mesh>>> section_uptr_list = GenerateOrderedWithMaxLen(lane_section);
      mesh_uptr_list.insert(
        std::make_move_iterator(section_uptr_list.begin()),
        std::make_move_iterator(section_uptr_list.end()));
    }
    return mesh_uptr_list;
  }

  std::map<road::Lane::LaneType , std::vector<std::unique_ptr<Mesh>>> MeshFactory::GenerateOrderedWithMaxLen(
    const road::LaneSection &lane_section) const {
      const size_t vertices_in_width = road_param.vertex_width_resolution >= 2 ? static_cast<size_t>(road_param.vertex_width_resolution) : size_t{2};
      std::map<road::Lane::LaneType , std::vector<std::unique_ptr<Mesh>>> mesh_uptr_list;

      if (lane_section.GetLength() < road_param.max_road_len) {
        GenerateLaneSectionOrdered(lane_section, mesh_uptr_list);
      } else {
        double s_current = lane_section.GetDistance() + EPSILON;
        const double s_end = lane_section.GetDistance() + lane_section.GetLength() - EPSILON;
        std::vector<size_t> redirections;
        while(s_current + road_param.max_road_len < s_end) {
          const auto s_until = s_current + road_param.max_road_len;

          for (auto &&lane_pair : lane_section.GetLanes()) {
            Mesh lane_section_mesh;
            if(lane_pair.second.GetType() == road::Lane::LaneType::Driving ){
              lane_section_mesh += *GenerateTesselated(lane_pair.second, s_current, s_until);
            }else{
              lane_section_mesh += *GenerateSidewalk(lane_pair.second, s_current, s_until);
            }

            auto it = std::find(redirections.begin(), redirections.end(), static_cast<size_t>(lane_pair.first));
            if (it == redirections.end()) {
              redirections.push_back(static_cast<size_t>(lane_pair.first));
              it = std::find(redirections.begin(), redirections.end(), static_cast<size_t>(lane_pair.first));
            }

            size_t PosToAdd = static_cast<size_t>(it - redirections.begin());
            if (mesh_uptr_list[lane_pair.second.GetType()].size() <= PosToAdd) {
              mesh_uptr_list[lane_pair.second.GetType()].push_back(std::make_unique<Mesh>(lane_section_mesh));
            } else {
              uint32_t verticesinwidth  = 0;
              if(lane_pair.second.GetType() == road::Lane::LaneType::Driving) {
                verticesinwidth = static_cast<uint32_t>(vertices_in_width);
              }else if(lane_pair.second.GetType() == road::Lane::LaneType::Sidewalk){
                verticesinwidth = 6;
              }else{
                verticesinwidth = 2;
              }
              (mesh_uptr_list[lane_pair.second.GetType()][PosToAdd])->ConcatMesh(lane_section_mesh, static_cast<int>(verticesinwidth));
            }
          }
          s_current = s_until;
        }
        if (s_end - s_current > EPSILON) {
          for (auto &&lane_pair : lane_section.GetLanes()) {
            Mesh lane_section_mesh;
            if(lane_pair.second.GetType() == road::Lane::LaneType::Driving ){
              lane_section_mesh += *GenerateTesselated(lane_pair.second, s_current, s_end);
            }else{
              lane_section_mesh += *GenerateSidewalk(lane_pair.second, s_current, s_end);
            }

            auto it = std::find(redirections.begin(), redirections.end(), static_cast<size_t>(lane_pair.first));
            if (it == redirections.end()) {
              redirections.push_back(static_cast<size_t>(lane_pair.first));
              it = std::find(redirections.begin(), redirections.end(), static_cast<size_t>(lane_pair.first));
            }

            size_t PosToAdd = static_cast<size_t>(it - redirections.begin());

            if (mesh_uptr_list[lane_pair.second.GetType()].size() <= PosToAdd) {
              mesh_uptr_list[lane_pair.second.GetType()].push_back(std::make_unique<Mesh>(lane_section_mesh));
            } else {
              uint32_t verticesinwidth  = 0;
              if(lane_pair.second.GetType() == road::Lane::LaneType::Driving) {
                verticesinwidth = static_cast<uint32_t>(vertices_in_width);
              }else if(lane_pair.second.GetType() == road::Lane::LaneType::Sidewalk){
                verticesinwidth = 6;
              }else{
                verticesinwidth = 2;
              }
              *(mesh_uptr_list[lane_pair.second.GetType()][PosToAdd]) += lane_section_mesh;
            }
          }
        }
      }
      return mesh_uptr_list;
  }

  std::vector<std::unique_ptr<Mesh>> MeshFactory::GenerateWallsWithMaxLen(
      const road::Road &road) const {
    std::vector<std::unique_ptr<Mesh>> mesh_uptr_list;
    for (auto &&lane_section : road.GetLaneSections()) {
      auto section_uptr_list = GenerateWallsWithMaxLen(lane_section);
      mesh_uptr_list.insert(
          mesh_uptr_list.end(),
          std::make_move_iterator(section_uptr_list.begin()),
          std::make_move_iterator(section_uptr_list.end()));
    }
    return mesh_uptr_list;
  }

  std::vector<std::unique_ptr<Mesh>> MeshFactory::GenerateWallsWithMaxLen(
      const road::LaneSection &lane_section) const {
    std::vector<std::unique_ptr<Mesh>> mesh_uptr_list;

    const auto min_lane = lane_section.GetLanes().begin()->first == 0 ?
        1 : lane_section.GetLanes().begin()->first;
    const auto max_lane = lane_section.GetLanes().rbegin()->first == 0 ?
        -1 : lane_section.GetLanes().rbegin()->first;

    if (lane_section.GetLength() < road_param.max_road_len) {
      mesh_uptr_list.emplace_back(GenerateWalls(lane_section));
    } else {
      double s_current = lane_section.GetDistance() + EPSILON;
      const double s_end = lane_section.GetDistance() + lane_section.GetLength() - EPSILON;
      while(s_current + road_param.max_road_len < s_end) {
        const auto s_until = s_current + road_param.max_road_len;
        Mesh lane_section_mesh;
        for (auto &&lane_pair : lane_section.GetLanes()) {
          const auto &lane = lane_pair.second;
          if (lane.GetId() == max_lane) {
            lane_section_mesh += *GenerateLeftWall(lane, s_current, s_until);
          }
          if (lane.GetId() == min_lane) {
            lane_section_mesh += *GenerateRightWall(lane, s_current, s_until);
          }
        }
        mesh_uptr_list.emplace_back(std::make_unique<Mesh>(lane_section_mesh));
        s_current = s_until;
      }
      if (s_end - s_current > EPSILON) {
        Mesh lane_section_mesh;
        for (auto &&lane_pair : lane_section.GetLanes()) {
          const auto &lane = lane_pair.second;
          if (lane.GetId() == max_lane) {
            lane_section_mesh += *GenerateLeftWall(lane, s_current, s_end);
          }
          if (lane.GetId() == min_lane) {
            lane_section_mesh += *GenerateRightWall(lane, s_current, s_end);
          }
        }
        mesh_uptr_list.emplace_back(std::make_unique<Mesh>(lane_section_mesh));
      }
    }
    return mesh_uptr_list;
  }

  std::vector<std::unique_ptr<Mesh>> MeshFactory::GenerateAllWithMaxLen(
      const road::Road &road) const {
    std::vector<std::unique_ptr<Mesh>> mesh_uptr_list;

    // Get road meshes
    auto roads = GenerateWithMaxLen(road);
    mesh_uptr_list.insert(
        mesh_uptr_list.end(),
        std::make_move_iterator(roads.begin()),
        std::make_move_iterator(roads.end()));

    // Get wall meshes only if is not a junction
    if (!road.IsJunction()) {
      auto walls = GenerateWallsWithMaxLen(road);

      if (roads.size() == walls.size()) {
        for (size_t i = 0; i < walls.size(); ++i) {
          *mesh_uptr_list[i] += *walls[i];
        }
      } else {
        mesh_uptr_list.insert(
            mesh_uptr_list.end(),
            std::make_move_iterator(walls.begin()),
            std::make_move_iterator(walls.end()));
      }
    }

    return mesh_uptr_list;
  }

  void MeshFactory::GenerateAllOrderedWithMaxLen(
      const road::Road &road,
      std::map<road::Lane::LaneType , std::vector<std::unique_ptr<Mesh>>>& roads
      ) const {

    // Get road meshes
    std::map<road::Lane::LaneType , std::vector<std::unique_ptr<Mesh>>> result = GenerateOrderedWithMaxLen(road);
    for (auto &pair_map : result)
    {
      std::vector<std::unique_ptr<Mesh>>& origin = roads[pair_map.first];
      std::vector<std::unique_ptr<Mesh>>& source = pair_map.second;
      std::move(source.begin(), source.end(), std::back_inserter(origin));
    }
  }

  void MeshFactory::GenerateLaneMarkForRoad(
    const road::Road& road,
    std::vector<std::unique_ptr<Mesh>>& inout,
    std::vector<std::string>& outinfo ) const
  {
    for (auto&& lane_section : road.GetLaneSections()) {
      for (auto&& lane : lane_section.GetLanes()) {
        if (lane.first != 0) {
          if(lane.second.GetType() == road::Lane::LaneType::Driving ){
            GenerateLaneMarksForNotCenterLine(lane_section, lane.second, inout, outinfo);
            outinfo.push_back("white");
          }
        } else {
          if(lane.second.GetType() == road::Lane::LaneType::None ){
            GenerateLaneMarksForCenterLine(road, lane_section, lane.second, inout, outinfo);
            outinfo.push_back("yellow");
          }
        }
      }
    }
  }

  void MeshFactory::GenerateLaneMarksForNotCenterLine(
    const road::LaneSection& lane_section,
    const road::Lane& lane,
    std::vector<std::unique_ptr<Mesh>>& inout,
    std::vector<std::string>& /*outinfo*/ ) const {
    Mesh out_mesh;
    const double s_start = lane_section.GetDistance();
    const double s_end = lane_section.GetDistance() + lane_section.GetLength();
    double s_current = s_start;
    std::vector<geom::Vector3D> vertices;
    std::vector<size_t> indices;

    do {
      //Get Lane info
      const carla::road::element::RoadInfoMarkRecord* road_info_mark = lane.GetInfo<carla::road::element::RoadInfoMarkRecord>(s_current);
      if (road_info_mark != nullptr) {
        carla::road::element::LaneMarking lane_mark_info(*road_info_mark);

        switch (lane_mark_info.type) {
          case carla::road::element::LaneMarking::Type::Solid: {
            size_t currentIndex = out_mesh.GetVertices().size() + 1;

            std::pair<geom::Vector3D, geom::Vector3D> edges =
              ComputeEdgesForLanemark(lane_section, lane, s_current, lane_mark_info.width, 0.0f);

            out_mesh.AddVertex(edges.first);
            out_mesh.AddVertex(edges.second);

            out_mesh.AddIndex(currentIndex);
            out_mesh.AddIndex(currentIndex + 1);
            out_mesh.AddIndex(currentIndex + 2);

            out_mesh.AddIndex(currentIndex + 1);
            out_mesh.AddIndex(currentIndex + 3);
            out_mesh.AddIndex(currentIndex + 2);

            s_current += road_param.resolution;
            break;
          }
          case carla::road::element::LaneMarking::Type::Broken: {
            size_t currentIndex = out_mesh.GetVertices().size() + 1;

            std::pair<geom::Vector3D, geom::Vector3D> edges =
              ComputeEdgesForLanemark(lane_section, lane, s_current, lane_mark_info.width, road_param.extra_lane_width);

            out_mesh.AddVertex(edges.first);
            out_mesh.AddVertex(edges.second);

            s_current += road_param.resolution * 3;
            if (s_current > s_end)
            {
              s_current = s_end;
            }

            edges = ComputeEdgesForLanemark(lane_section, lane, s_current, lane_mark_info.width, road_param.extra_lane_width);

            out_mesh.AddVertex(edges.first);
            out_mesh.AddVertex(edges.second);

            out_mesh.AddIndex(currentIndex);
            out_mesh.AddIndex(currentIndex + 1);
            out_mesh.AddIndex(currentIndex + 2);

            out_mesh.AddIndex(currentIndex + 1);
            out_mesh.AddIndex(currentIndex + 3);
            out_mesh.AddIndex(currentIndex + 2);

            s_current += road_param.resolution * 3;

            break;
          }
          case carla::road::element::LaneMarking::Type::SolidSolid: {
            s_current += road_param.resolution;
            break;
          }
          case carla::road::element::LaneMarking::Type::SolidBroken: {
            s_current += road_param.resolution;
            break;
          }
          case carla::road::element::LaneMarking::Type::BrokenSolid: {
            s_current += road_param.resolution;
            break;
          }
          case carla::road::element::LaneMarking::Type::BrokenBroken: {
            s_current += road_param.resolution;
            break;
          }
          case carla::road::element::LaneMarking::Type::BottsDots: {
            s_current += road_param.resolution;
            break;
          }
          case carla::road::element::LaneMarking::Type::Grass: {
            s_current += road_param.resolution;
            break;
          }
          case carla::road::element::LaneMarking::Type::Curb: {
            s_current += road_param.resolution;
            break;
          }
          case carla::road::element::LaneMarking::Type::Other: {
            s_current += road_param.resolution;
            break;
          }
          default: {
            s_current += road_param.resolution;
            break;
          }
        }
      }
    } while (s_current < s_end);

    if (out_mesh.IsValid()) {
      const carla::road::element::RoadInfoMarkRecord* road_info_mark = lane.GetInfo<carla::road::element::RoadInfoMarkRecord>(s_current);
      if (road_info_mark != nullptr) {
        carla::road::element::LaneMarking lane_mark_info(*road_info_mark);

        std::pair<geom::Vector3D, geom::Vector3D> edges =
              ComputeEdgesForLanemark(lane_section, lane, s_end, lane_mark_info.width, 0.0f);

        out_mesh.AddVertex(edges.first);
        out_mesh.AddVertex(edges.second);
      }
      inout.push_back(std::make_unique<Mesh>(out_mesh));
    }
  }

  void MeshFactory::GenerateLaneMarksForCenterLine(
    const road::Road& road,
    const road::LaneSection& lane_section,
    const road::Lane& lane,
    std::vector<std::unique_ptr<Mesh>>& inout,
    std::vector<std::string>& /*outinfo*/ ) const
  {
    Mesh out_mesh;
    const double s_start = lane_section.GetDistance();
    const double s_end = lane_section.GetDistance() + lane_section.GetLength();
    double s_current = s_start;
    std::vector<geom::Vector3D> vertices;
    std::vector<size_t> indices;

    do {
      //Get Lane info
      const carla::road::element::RoadInfoMarkRecord* road_info_mark = lane.GetInfo<carla::road::element::RoadInfoMarkRecord>(s_current);
      if (road_info_mark != nullptr) {
        carla::road::element::LaneMarking lane_mark_info(*road_info_mark);

        switch (lane_mark_info.type) {
          case carla::road::element::LaneMarking::Type::Solid: {
            size_t currentIndex = out_mesh.GetVertices().size() + 1;

            carla::road::element::DirectedPoint rightpoint = road.GetDirectedPointIn(s_current);
            carla::road::element::DirectedPoint leftpoint = rightpoint;

            rightpoint.ApplyLateralOffset(static_cast<float>(lane_mark_info.width * 0.5));
            leftpoint.ApplyLateralOffset(static_cast<float>(lane_mark_info.width * -0.5));

            // Unreal's Y axis hack
            rightpoint.location.y *= -1;
            leftpoint.location.y *= -1;

            out_mesh.AddVertex(rightpoint.location);
            out_mesh.AddVertex(leftpoint.location);

            out_mesh.AddIndex(currentIndex);
            out_mesh.AddIndex(currentIndex + 1);
            out_mesh.AddIndex(currentIndex + 2);

            out_mesh.AddIndex(currentIndex + 1);
            out_mesh.AddIndex(currentIndex + 3);
            out_mesh.AddIndex(currentIndex + 2);

            s_current += road_param.resolution;
            break;
          }
          case carla::road::element::LaneMarking::Type::Broken: {
            size_t currentIndex = out_mesh.GetVertices().size() + 1;

            std::pair<geom::Vector3D, geom::Vector3D> edges =
              ComputeEdgesForLanemark(lane_section, lane, s_current, lane_mark_info.width, road_param.extra_lane_width);

            out_mesh.AddVertex(edges.first);
            out_mesh.AddVertex(edges.second);

            s_current += road_param.resolution * 3;
            if (s_current > s_end) {
              s_current = s_end;
            }

            edges = ComputeEdgesForLanemark(lane_section, lane, s_current, lane_mark_info.width, road_param.extra_lane_width);

            out_mesh.AddVertex(edges.first);
            out_mesh.AddVertex(edges.second);

            out_mesh.AddIndex(currentIndex);
            out_mesh.AddIndex(currentIndex + 1);
            out_mesh.AddIndex(currentIndex + 2);

            out_mesh.AddIndex(currentIndex + 1);
            out_mesh.AddIndex(currentIndex + 3);
            out_mesh.AddIndex(currentIndex + 2);

            s_current += road_param.resolution * 3;

            break;
          }
          case carla::road::element::LaneMarking::Type::SolidSolid: {
            s_current += road_param.resolution;
            break;
          }
          case carla::road::element::LaneMarking::Type::SolidBroken: {
            s_current += road_param.resolution;
            break;
          }
          case carla::road::element::LaneMarking::Type::BrokenSolid: {
            s_current += road_param.resolution;
            break;
          }
          case carla::road::element::LaneMarking::Type::BrokenBroken: {
            s_current += road_param.resolution;
            break;
          }
          case carla::road::element::LaneMarking::Type::BottsDots: {
            s_current += road_param.resolution;
            break;
          }
          case carla::road::element::LaneMarking::Type::Grass: {
            s_current += road_param.resolution;
            break;
          }
          case carla::road::element::LaneMarking::Type::Curb: {
            s_current += road_param.resolution;
            break;
          }
          case carla::road::element::LaneMarking::Type::Other: {
            s_current += road_param.resolution;
            break;
          }
          default: {
            s_current += road_param.resolution;
            break;
          }
        }
      }
    } while (s_current < s_end);

    if (out_mesh.IsValid()) {
      const carla::road::element::RoadInfoMarkRecord* road_info_mark = lane.GetInfo<carla::road::element::RoadInfoMarkRecord>(s_current);
      if (road_info_mark != nullptr)
      {
        carla::road::element::LaneMarking lane_mark_info(*road_info_mark);
        carla::road::element::DirectedPoint rightpoint = road.GetDirectedPointIn(s_current);
        carla::road::element::DirectedPoint leftpoint = rightpoint;

        rightpoint.ApplyLateralOffset(static_cast<float>(lane_mark_info.width * 0.5));
        leftpoint.ApplyLateralOffset(static_cast<float>(lane_mark_info.width * -0.5));

        // Unreal's Y axis hack
        rightpoint.location.y *= -1;
        leftpoint.location.y *= -1;

        out_mesh.AddVertex(rightpoint.location);
        out_mesh.AddVertex(leftpoint.location);

      }
      inout.push_back(std::make_unique<Mesh>(out_mesh));
    }
  }

  struct VertexWeight {
    Mesh::vertex_type* vertex;
    double weight;
  };
  struct VertexNeighbors {
    Mesh::vertex_type* vertex;
    std::vector<VertexWeight> neighbors;
  };
  struct VertexInfo {
    Mesh::vertex_type * vertex;
    size_t lane_mesh_idx;
    bool is_static;
  };

  // Helper function to compute the weight of neighboring vertices
  static VertexWeight ComputeVertexWeight(
      const MeshFactory::RoadParameters &road_param,
      const VertexInfo &vertex_info,
      const VertexInfo &neighbor_info) {
    const float distance3D = geom::Math::Distance(*vertex_info.vertex, *neighbor_info.vertex);
    // Ignore vertices beyond a certain distance
    if(distance3D > road_param.max_weight_distance) {
      return {neighbor_info.vertex, 0};
    }
    if(abs(distance3D) < EPSILON) {
      return {neighbor_info.vertex, 0};
    }
    float weight = geom::Math::Clamp<float>(1.0f / distance3D, 0.0f, 100000.0f);

    // Additional weight to vertices in the same lane
    if(vertex_info.lane_mesh_idx == neighbor_info.lane_mesh_idx) {
      weight *= road_param.same_lane_weight_multiplier;
      // Further additional weight for fixed verices
      if(neighbor_info.is_static) {
        weight *= road_param.lane_ends_multiplier;
      }
    }
    return {neighbor_info.vertex, weight};
  }

  // Helper function to compute neighborhoord of vertices and their weights
  std::vector<VertexNeighbors> GetVertexNeighborhoodAndWeights(
      const MeshFactory::RoadParameters &road_param,
      std::vector<std::unique_ptr<Mesh>> &lane_meshes) {
    // Build rtree for neighborhood queries
    using Rtree = geom::PointCloudRtree<VertexInfo>;
    using Point = Rtree::BPoint;
    Rtree rtree;
    for (size_t lane_mesh_idx = 0; lane_mesh_idx < lane_meshes.size(); ++lane_mesh_idx) {
      auto& mesh = lane_meshes[lane_mesh_idx];
      for(size_t i = 0; i < mesh->GetVerticesNum(); ++i) {
        auto& vertex = mesh->GetVertices()[i];
        Point point(vertex.x, vertex.y, vertex.z);
        if (i < 2 || i >= mesh->GetVerticesNum() - 2) {
          rtree.InsertElement({point, {&vertex, lane_mesh_idx, true}});
        } else {
          rtree.InsertElement({point, {&vertex, lane_mesh_idx, false}});
        }
      }
    }

    // Find neighbors for each vertex and compute their weight
    std::vector<VertexNeighbors> vertices_neighborhoods;
    for (size_t lane_mesh_idx = 0; lane_mesh_idx < lane_meshes.size(); ++lane_mesh_idx) {
      auto& mesh = lane_meshes[lane_mesh_idx];
      for(size_t i = 0; i < mesh->GetVerticesNum(); ++i) {
        if (i > 2 && i < mesh->GetVerticesNum() - 2) {
          auto& vertex = mesh->GetVertices()[i];
          Point point(vertex.x, vertex.y, vertex.z);
          auto closest_vertices = rtree.GetNearestNeighbours(point, 20);
          VertexNeighbors vertex_neighborhood;
          vertex_neighborhood.vertex = &vertex;
          for(auto& close_vertex : closest_vertices) {
            auto &vertex_info = close_vertex.second;
            if(&vertex == vertex_info.vertex) {
              continue;
            }
            auto vertex_weight = ComputeVertexWeight(
                road_param, {&vertex, lane_mesh_idx, false}, vertex_info);
            if(vertex_weight.weight > 0)
              vertex_neighborhood.neighbors.push_back(vertex_weight);
          }
          vertices_neighborhoods.push_back(vertex_neighborhood);
        }
      }
    }
    return vertices_neighborhoods;
  }

  std::unique_ptr<Mesh> MeshFactory::MergeAndSmooth(std::vector<std::unique_ptr<Mesh>> &lane_meshes) const {
    geom::Mesh out_mesh;

    auto vertices_neighborhoods = GetVertexNeighborhoodAndWeights(road_param, lane_meshes);

    // Laplacian function
    auto Laplacian = [&](const Mesh::vertex_type* vertex, const std::vector<VertexWeight> &neighbors) -> double {
      double sum = 0;
      double sum_weight = 0;
      for(auto &element : neighbors) {
        sum += (element.vertex->z - vertex->z)*element.weight;
        sum_weight += element.weight;
      }
      if(sum_weight > 0)
        return sum / sum_weight;
      else
        return 0;
    };
    // Run iterative algorithm
    double lambda = 0.5;
    int iterations = 100;
    for(int iter = 0; iter < iterations; ++iter) {
      for (auto& vertex_neighborhood : vertices_neighborhoods) {
        auto * vertex = vertex_neighborhood.vertex;
        vertex->z += static_cast<float>(lambda*Laplacian(vertex, vertex_neighborhood.neighbors));
      }
    }

    for(auto &mesh : lane_meshes) {
      out_mesh += *mesh;
    }

    return std::make_unique<Mesh>(out_mesh);
  }


namespace {

  /// A cell of the junction height field, addressed by integer grid coordinates.
  struct Cell {
    int col;
    int row;
    bool operator==(const Cell &rhs) const { return col == rhs.col && row == rhs.row; }
  };

  struct CellHash {
    size_t operator()(const Cell &c) const {
      return (static_cast<size_t>(static_cast<uint32_t>(c.col)) << 32) ^
             static_cast<uint32_t>(c.row);
    }
  };

  /// True when a point lies inside a convex quad given in order.
  bool InsideQuad(const std::array<geom::Vector2D, 4> &quad, float x, float y) {
    int sign = 0;
    for (size_t i = 0; i < quad.size(); ++i) {
      const auto &a = quad[i];
      const auto &b = quad[(i + 1) % quad.size()];
      const float cross = (b.x - a.x) * (y - a.y) - (b.y - a.y) * (x - a.x);
      if (std::abs(cross) < 1e-9f) {
        continue;
      }
      const int side = cross > 0.0f ? 1 : -1;
      if (sign == 0) {
        sign = side;
      } else if (side != sign) {
        return false;
      }
    }
    return true;
  }


  /// True when a height fits every neighbour the layer already holds, diagonals
  /// included — those share a corner vertex just as edge neighbours do.
  bool AgreesWithNeighbours(
      const std::unordered_map<Cell, float, CellHash> &layer,
      const Cell &cell,
      const float height,
      const float separation) {
    for (int dc = -1; dc <= 1; ++dc) {
      for (int dr = -1; dr <= 1; ++dr) {
        if (dc == 0 && dr == 0) {
          continue;
        }
        const auto held = layer.find(Cell{cell.col + dc, cell.row + dr});
        if (held != layer.end() && std::abs(held->second - height) > separation) {
          return false;
        }
      }
    }
    return true;
  }

  /// True when paving lies within `reach` cells of `cell` in all four directions.
  bool Surrounded(
      const std::unordered_map<Cell, float, CellHash> &layer,
      const Cell &cell,
      const int reach) {
    const std::array<Cell, 4> steps = {Cell{1, 0}, Cell{-1, 0}, Cell{0, 1}, Cell{0, -1}};
    for (const auto &step : steps) {
      bool found = false;
      for (int distance = 1; distance <= reach; ++distance) {
        if (layer.count(Cell{cell.col + step.col * distance,
                             cell.row + step.row * distance}) != 0) {
          found = true;
          break;
        }
      }
      if (!found) {
        return false;
      }
    }
    return true;
  }

  /// Pave the gaps a junction's turning paths leave between them.
  ///
  /// OpenDRIVE models a junction as turning paths — a u-turn, some left turns, the
  /// straight-throughs, the rights — and between them sits asphalt no lane ever covers.
  /// A vehicle drops through it, since collision uses these triangles directly.
  ///
  /// A gap counts as interior when paving lies within reach in all four directions. That
  /// separates an intersection from a median: the interior of a junction is ringed by
  /// turning paths so every ray hits one, while a median between two approach
  /// carriageways runs away down the road and the ray along it finds nothing. A convex
  /// hull of the junction cannot tell them apart — measured on Arapahoe_I25, the median
  /// beside junction 114 lies inside that junction's own hull.
  ///
  /// The test is local, costing a bounded ray rather than a flood across the empty space
  /// around the network, which is most of a road map's bounding box.
  ///
  /// Filling uses its own tolerance rather than the layer separation. That asks whether
  /// two cells belong to one sheet, which a deck and the ramp beside it can, while this
  /// asks whether bridging a gap would invent a slope.
  void PaveEnclosedGaps(
      std::unordered_map<Cell, float, CellHash> &layer,
      const float cell,
      const float max_gap_span,
      const float fill_tolerance) {
    const int reach = std::max(1, static_cast<int>(std::lround(max_gap_span / cell)));
    const std::array<Cell, 4> steps = {Cell{1, 0}, Cell{-1, 0}, Cell{0, 1}, Cell{0, -1}};

    std::vector<Cell> frontier;
    for (const auto &entry : layer) {
      for (const auto &step : steps) {
        const Cell key{entry.first.col + step.col, entry.first.row + step.row};
        if (layer.count(key) == 0) {
          frontier.push_back(key);
        }
      }
    }

    std::unordered_map<Cell, bool, CellHash> checked;
    std::vector<Cell> interior;
    while (!frontier.empty()) {
      const Cell current = frontier.back();
      frontier.pop_back();
      if (checked.count(current) != 0 || layer.count(current) != 0) {
        continue;
      }
      checked[current] = true;
      if (!Surrounded(layer, current, reach)) {
        continue;
      }
      interior.push_back(current);
      // A gap is usually more than one cell wide, so its neighbours are candidates too
      // even though they do not touch paving themselves.
      for (const auto &step : steps) {
        const Cell key{current.col + step.col, current.row + step.row};
        if (layer.count(key) == 0 && checked.count(key) == 0) {
          frontier.push_back(key);
        }
      }
    }

    // Work inwards from the surface around each gap, so every cell takes the height of
    // what it already touches.
    std::vector<Cell> remaining = std::move(interior);
    while (!remaining.empty()) {
      std::vector<Cell> still_open;
      bool progressed = false;
      for (const auto &key : remaining) {
        float total = 0.0f;
        int count = 0;
        for (const auto &step : steps) {
          const auto found = layer.find(Cell{key.col + step.col, key.row + step.row});
          if (found != layer.end()) {
            total += found->second;
            ++count;
          }
        }
        if (count == 0) {
          still_open.push_back(key);
          continue;
        }
        progressed = true;
        const float height = total / static_cast<float>(count);
        if (AgreesWithNeighbours(layer, key, height, fill_tolerance)) {
          layer[key] = height;
        }
      }
      if (!progressed) {
        break;
      }
      remaining.swap(still_open);
    }
  }

  /// Take the flips out of the resolved height field.
  ///
  /// Where two connectors overlap and disagree, the lower of the two wins, and which one
  /// is lower can change from cell to cell — leaving a field that jumps by the amount the
  /// two disagreed, measured at up to 0.47 m across a single 0.5 m cell. Averaging each
  /// cell against its neighbours removes those flips.
  ///
  /// This is not the junction smoothing it replaces. That one blended separate
  /// overlapping ribbons into each other and left the mesh disagreeing with the profile
  /// the waypoints follow. This runs inside one already single-valued surface, so it
  /// cannot reintroduce a stack, and it stays within a layer, so a deck is never pulled
  /// towards the road beneath it.
  void RelaxLayer(
      std::unordered_map<Cell, float, CellHash> &layer,
      const int passes) {
    const std::array<Cell, 4> neighbours = {
        Cell{1, 0}, Cell{-1, 0}, Cell{0, 1}, Cell{0, -1}};
    for (int pass = 0; pass < passes; ++pass) {
      std::unordered_map<Cell, float, CellHash> updated;
      updated.reserve(layer.size());
      for (const auto &entry : layer) {
        float total = entry.second;
        int count = 1;
        for (const auto &step : neighbours) {
          const auto found = layer.find(
              Cell{entry.first.col + step.col, entry.first.row + step.row});
          if (found != layer.end()) {
            total += found->second;
            ++count;
          }
        }
        updated[entry.first] = total / static_cast<float>(count);
      }
      layer.swap(updated);
    }
  }

  /// Emit one layer as tiles of two triangles per cell.
  ///
  /// Corner heights are resolved across the whole layer before any tile is built, and
  /// each corner averages the cells meeting there. Neighbouring tiles therefore compute
  /// an identical position and height for every vertex on their shared edge, so the
  /// tiling introduces no seam — and within a tile, neighbouring quads share the vertex
  /// itself, so the surface is continuous by construction.
  void AppendLayerTiles(
      std::vector<std::unique_ptr<Mesh>> &out_tiles,
      const std::unordered_map<Cell, float, CellHash> &layer,
      const float cell,
      const float tile_size) {
    std::unordered_map<Cell, std::pair<float, int>, CellHash> corners;
    const std::array<Cell, 4> offsets = {Cell{0, 0}, Cell{1, 0}, Cell{0, 1}, Cell{1, 1}};
    for (const auto &entry : layer) {
      for (const auto &offset : offsets) {
        auto &corner = corners[Cell{entry.first.col + offset.col, entry.first.row + offset.row}];
        corner.first += entry.second;
        corner.second += 1;
      }
    }

    const int cells_per_tile = std::max(1, static_cast<int>(std::lround(tile_size / cell)));
    std::unordered_map<Cell, std::vector<Cell>, CellHash> tiles;
    for (const auto &entry : layer) {
      tiles[Cell{
          static_cast<int>(std::floor(static_cast<float>(entry.first.col) / cells_per_tile)),
          static_cast<int>(std::floor(static_cast<float>(entry.first.row) / cells_per_tile))}]
          .push_back(entry.first);
    }

    for (const auto &tile : tiles) {
      Mesh mesh;
      mesh.AddMaterial("road");
      std::unordered_map<Cell, size_t, CellHash> index_of;
      for (const auto &key : tile.second) {
        for (const auto &offset : offsets) {
          const Cell corner{key.col + offset.col, key.row + offset.row};
          if (index_of.count(corner) != 0) {
            continue;
          }
          const size_t next = index_of.size();
          index_of[corner] = next;
          const auto &sum = corners.at(corner);
          mesh.AddVertex(Mesh::vertex_type(
              corner.col * cell,
              corner.row * cell,
              sum.first / static_cast<float>(sum.second)));
        }
      }
      for (const auto &key : tile.second) {
        const auto a = index_of.at(key);
        const auto b = index_of.at(Cell{key.col + 1, key.row});
        const auto c = index_of.at(Cell{key.col + 1, key.row + 1});
        const auto d = index_of.at(Cell{key.col, key.row + 1});
        mesh.AddIndex(static_cast<Mesh::index_type>(a + 1));
        mesh.AddIndex(static_cast<Mesh::index_type>(b + 1));
        mesh.AddIndex(static_cast<Mesh::index_type>(c + 1));
        mesh.AddIndex(static_cast<Mesh::index_type>(a + 1));
        mesh.AddIndex(static_cast<Mesh::index_type>(c + 1));
        mesh.AddIndex(static_cast<Mesh::index_type>(d + 1));
      }
      mesh.EndMaterial();
      out_tiles.push_back(std::make_unique<Mesh>(std::move(mesh)));
    }
  }

} // namespace


  std::vector<std::unique_ptr<Mesh>> MeshFactory::ResolveDrivableSurface(
      const std::vector<std::unique_ptr<Mesh>> &lane_meshes,
      const float tile_size) const {
    const float cell = road_param.junction_cell_size;
    const float separation = road_param.junction_layer_separation;

    // 1. Sample every lane strip into the height field. A lane mesh stores its
    //    vertices as consecutive right/left pairs along the lane, so each pair of
    //    stations is one quad.
    std::unordered_map<Cell, std::vector<float>, CellHash> samples;
    for (const auto &mesh : lane_meshes) {
      const auto &vertices = mesh->GetVertices();
      for (size_t i = 0; i + 3 < vertices.size(); i += 2) {
        const std::array<geom::Vector2D, 4> quad = {
            geom::Vector2D(vertices[i].x, vertices[i].y),
            geom::Vector2D(vertices[i + 1].x, vertices[i + 1].y),
            geom::Vector2D(vertices[i + 3].x, vertices[i + 3].y),
            geom::Vector2D(vertices[i + 2].x, vertices[i + 2].y)};
        const float height =
            (vertices[i].z + vertices[i + 1].z + vertices[i + 2].z + vertices[i + 3].z) / 4.0f;

        float min_x = quad[0].x, max_x = quad[0].x, min_y = quad[0].y, max_y = quad[0].y;
        for (const auto &v : quad) {
          min_x = std::min(min_x, v.x); max_x = std::max(max_x, v.x);
          min_y = std::min(min_y, v.y); max_y = std::max(max_y, v.y);
        }
        for (int col = static_cast<int>(std::floor(min_x / cell)) - 1;
             col <= static_cast<int>(std::floor(max_x / cell)) + 1; ++col) {
          for (int row = static_cast<int>(std::floor(min_y / cell)) - 1;
               row <= static_cast<int>(std::floor(max_y / cell)) + 1; ++row) {
            if (InsideQuad(quad, col * cell, row * cell)) {
              samples[Cell{col, row}].push_back(height);
            }
          }
        }
      }
    }
    std::vector<std::unique_ptr<Mesh>> out_tiles;
    if (samples.empty()) {
      return out_tiles;
    }

    // 2. Reduce each cell to one height per layer. A surface model can place a
    //    sample above the ground but never below it, so a cluster is represented by
    //    its lowest sample.
    std::unordered_map<Cell, std::vector<float>, CellHash> clustered;
    for (auto &entry : samples) {
      auto heights = entry.second;
      std::sort(heights.begin(), heights.end());
      std::vector<float> representatives;
      float lowest = heights.front();
      float previous = heights.front();
      for (size_t i = 1; i < heights.size(); ++i) {
        if (heights[i] - previous > separation) {
          representatives.push_back(lowest);
          lowest = heights[i];
        }
        previous = heights[i];
      }
      representatives.push_back(lowest);
      clustered[entry.first] = std::move(representatives);
    }

    // 3. Grow layers across neighbouring cells, so a ramp climbing away from the
    //    ground stays attached to the deck it leads to rather than the road it crosses.
    std::unordered_map<Cell, std::vector<char>, CellHash> claimed;
    for (const auto &entry : clustered) {
      claimed[entry.first].assign(entry.second.size(), 0);
    }
    const std::array<Cell, 4> neighbours = {
        Cell{1, 0}, Cell{-1, 0}, Cell{0, 1}, Cell{0, -1}};

    for (const auto &seed : clustered) {
      for (size_t index = 0; index < seed.second.size(); ++index) {
        if (claimed.at(seed.first)[index] != 0) {
          continue;
        }
        std::unordered_map<Cell, float, CellHash> layer;
        std::vector<std::pair<Cell, size_t>> frontier{{seed.first, index}};
        claimed.at(seed.first)[index] = 1;
        while (!frontier.empty()) {
          const auto current = frontier.back();
          frontier.pop_back();
          const float height = clustered.at(current.first).at(current.second);
          // A layer is a height function of plan position: one value per cell. A ramp
          // climbing to a deck is continuously connected to the road it crosses, so
          // growing purely by connectivity would claim both and the crossing cell could
          // keep only one of them, burying the underpass. Reaching a cell this layer
          // already holds means the surface has passed over itself, so it stops there.
          if (layer.count(current.first) != 0) {
            continue;
          }
          // Growth is checked against the cell it came from, but two branches — one along
          // the ground, one climbing a ramp — can meet as neighbours without ever being
          // compared. A cell joins only if it agrees with every neighbour already held.
          //
          // All eight, not just the four edges: every cell touching a corner shares that
          // corner's vertex, so two cells that are only diagonal neighbours still share
          // one. Comparing edges alone lets a deck cell sit diagonally against a road
          // cell, and their shared corner then averages between the two while the
          // triangles stretch from one height to the other — a vertical fin in the road.
          if (!AgreesWithNeighbours(layer, current.first, height, separation)) {
            continue;
          }
          layer[current.first] = height;
          for (const auto &step : neighbours) {
            const Cell key{current.first.col + step.col, current.first.row + step.row};
            const auto found = clustered.find(key);
            if (found == clustered.end()) {
              continue;
            }
            for (size_t other = 0; other < found->second.size(); ++other) {
              if (claimed.at(key)[other] == 0 &&
                  std::abs(found->second[other] - height) <= separation) {
                claimed.at(key)[other] = 1;
                frontier.push_back({key, other});
              }
            }
          }
        }
        PaveEnclosedGaps(layer, cell, road_param.junction_max_gap_span,
                         road_param.junction_fill_tolerance);
        RelaxLayer(layer, road_param.junction_relax_passes);
        AppendLayerTiles(out_tiles, layer, cell, tile_size);
      }
    }
    return out_tiles;
  }


  std::pair<geom::Vector3D, geom::Vector3D> MeshFactory::ComputeEdgesForLanemark(
      const road::LaneSection& lane_section,
      const road::Lane& lane,
      const double s_current,
      const double lanemark_width,
      const float extra_width) const {
    std::pair<geom::Vector3D, geom::Vector3D> edges =
      lane.GetCornerPositions(s_current, extra_width);

    geom::Vector3D director;
    if (edges.first != edges.second) {
      director = edges.second - edges.first;
      director /= director.Length();
    } else {
      const std::map<road::LaneId, road::Lane> & lanes = lane_section.GetLanes();
      for (const auto& lane_pair : lanes) {
        std::pair<geom::Vector3D, geom::Vector3D> another_edge =
          lane_pair.second.GetCornerPositions(s_current, extra_width);
        if (another_edge.first != another_edge.second) {
          director = another_edge.second - another_edge.first;
          director /= director.Length();
          break;
        }
      }
    }
    geom::Vector3D endmarking = edges.first + director * static_cast<float>(lanemark_width);
    return std::make_pair(edges.first, endmarking);
  }

} // namespace geom
} // namespace carla
