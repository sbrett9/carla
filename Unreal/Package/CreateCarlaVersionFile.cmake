execute_process(
  COMMAND
    git log -1 --format=%H
  WORKING_DIRECTORY
    ${CARLA_WORKSPACE_PATH}
  OUTPUT_VARIABLE
    CARLA_GIT_HASH
  OUTPUT_STRIP_TRAILING_WHITESPACE
)

execute_process(
  COMMAND
    git log -1 --format=%H
  WORKING_DIRECTORY
    ${CARLA_WORKSPACE_PATH}/Unreal/CarlaUnreal/Content/Carla
  OUTPUT_VARIABLE
    CONTENT_GIT_HASH
  OUTPUT_STRIP_TRAILING_WHITESPACE
)

execute_process(
  COMMAND
    git log -1 --format=%H
  WORKING_DIRECTORY
    ${CARLA_UNREAL_ENGINE_PATH}
  OUTPUT_VARIABLE
    UNREAL_ENGINE_GIT_HASH
  OUTPUT_STRIP_TRAILING_WHITESPACE
)

# What a separately delivered world can rely on this build providing. Read from the declaration that
# ships with the package rather than derived from anything, because it states a promise: see
# Unreal/CarlaUnreal/Config/DefaultWorldInterface.ini. Reported here so a package describes itself
# without being started.
set (WORLD_INTERFACE_VERSION "unknown")
if (EXISTS "${CARLA_WORLD_INTERFACE_CONFIG}")
  file (READ "${CARLA_WORLD_INTERFACE_CONFIG}" WORLD_INTERFACE_TEXT)
  string (REGEX MATCH "\n[ \t]*Major[ \t]*=[ \t]*([0-9]+)" _ "\n${WORLD_INTERFACE_TEXT}")
  set (WORLD_INTERFACE_MAJOR "${CMAKE_MATCH_1}")
  string (REGEX MATCH "\n[ \t]*Minor[ \t]*=[ \t]*([0-9]+)" _ "\n${WORLD_INTERFACE_TEXT}")
  set (WORLD_INTERFACE_MINOR "${CMAKE_MATCH_1}")
  if (NOT WORLD_INTERFACE_MAJOR STREQUAL "" AND NOT WORLD_INTERFACE_MINOR STREQUAL "")
    set (WORLD_INTERFACE_VERSION "${WORLD_INTERFACE_MAJOR}.${WORLD_INTERFACE_MINOR}")
  endif ()
endif ()

# Two version numbers, deliberately independent. The CARLA version says which release this is; the
# world interface version says what a delivered world can rely on. Tying them together would make
# both lie: a CARLA release can change nothing a world depends on, and a patch release that renames
# one road material breaks every world in the field.
file (
  WRITE
    ${CARLA_PACKAGE_VERSION_FILE}
    "Carla version:          ${CARLA_VERSION}\n"
    "World interface:        ${WORLD_INTERFACE_VERSION}\n"
    "Carla git hash:         ${CARLA_GIT_HASH}\n"
    "Content git hash:       ${CONTENT_GIT_HASH}\n"
    "UnrealEngine git hash:  ${UNREAL_ENGINE_GIT_HASH}\n"
)
