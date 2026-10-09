#[[

  Writes carla/BuildCommit.h: the CARLA commit this build is made from.

  The server reports it over get_build_identity when it runs from the editor, which has no package
  VERSION file to read its commits from (Unreal/CarlaUnreal/Plugins/Carla/Source/Carla/Carla.cpp,
  GetCarlaBuildIdentity). Run as a script by the carla-build-commit target on every LibCarla build,
  rather than at configure time, so it cannot go stale between configuring and building; written only
  when the commit has changed, so an unchanged commit rebuilds nothing.

  Expects CARLA_WORKSPACE_PATH and BUILD_COMMIT_HEADER.

]]

execute_process (
  COMMAND git rev-parse HEAD
  WORKING_DIRECTORY "${CARLA_WORKSPACE_PATH}"
  OUTPUT_VARIABLE BUILD_COMMIT
  OUTPUT_STRIP_TRAILING_WHITESPACE
  RESULT_VARIABLE GIT_RESULT
  ERROR_QUIET
)

# A tree git cannot name has an unknown commit, never a guessed one.
if (NOT GIT_RESULT EQUAL 0 OR NOT BUILD_COMMIT MATCHES "^[0-9a-f]+$")
  set (BUILD_COMMIT "unknown")
endif ()

set (
  BUILD_COMMIT_TEXT
  "// Written by LibCarla/WriteBuildCommit.cmake on every LibCarla build. Not edited by hand.\n#pragma once\n\n#define CARLA_BUILD_COMMIT \"${BUILD_COMMIT}\"\n"
)

set (BUILD_COMMIT_WRITTEN "")
if (EXISTS "${BUILD_COMMIT_HEADER}")
  file (READ "${BUILD_COMMIT_HEADER}" BUILD_COMMIT_WRITTEN)
endif ()

if (NOT BUILD_COMMIT_WRITTEN STREQUAL BUILD_COMMIT_TEXT)
  file (WRITE "${BUILD_COMMIT_HEADER}" "${BUILD_COMMIT_TEXT}")
  message (STATUS "CARLA build commit: ${BUILD_COMMIT}")
endif ()
