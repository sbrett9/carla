#[[

  Copyright (c) 2026 Computer Vision Center (CVC) at the Universitat Autonoma
  de Barcelona (UAB).
  
  This work is licensed under the terms of the MIT license.
  For a copy, see <https://opensource.org/licenses/MIT>.

]]

if (LINUX)

set (UE_ROOT $ENV{CARLA_UNREAL_ENGINE_PATH})

if (NOT UE_ROOT)
	set (UE_ROOT $ENV{UE_ROOT})
endif ()

if ("${UE_ROOT}" STREQUAL "")
	set (UE_ROOT ${CARLA_UNREAL_ENGINE_PATH})
	set (ENV{UE_ROOT} ${UE_ROOT}) # @TODO
endif ()

if (NOT EXISTS ${UE_ROOT})
	message (FATAL_ERROR "The specified Carla Unreal Engine 5 path does not exist (\"${UE_ROOT}\").")
endif ()

set (ARCH ${CMAKE_HOST_SYSTEM_PROCESSOR})

if	(${ARCH} STREQUAL "x86_64")
	set	(CMAKE_SYSTEM_PROCESSOR x86_64 CACHE STRING "")
	set	(TARGET_TRIPLE "x86_64-unknown-linux-gnu" CACHE STRING "")
elseif (${ARCH} STREQUAL "aarch64")
	set (CMAKE_SYSTEM_PROCESSOR aarch64 CACHE STRING "")
	set (TARGET_TRIPLE "aarch64-unknown-linux-gnueabi" CACHE STRING "")
endif()

file (
	GLOB
	UE_SYSROOT_CANDIDATES
	${UE_ROOT}/Engine/Extras/ThirdPartyNotUE/SDKs/HostLinux/Linux_x64/v*_clang-*.*.*-*/${TARGET_TRIPLE}
	LIST_DIRECTORIES TRUE
	FOLLOW_SYMLINKS
)

set (UE_SYSROOT_CANDIDATE)
foreach (CANDIDATE ${UE_SYSROOT_CANDIDATES})
	if (IS_DIRECTORY ${CANDIDATE})
		set (UE_SYSROOT_CANDIDATE ${CANDIDATE})
		break ()
	endif ()
endforeach ()

if (NOT UE_SYSROOT_CANDIDATE)
	message (FATAL_ERROR "Could not find Unreal Engine clang sysroot.")
endif ()

set (
	UE_SYSROOT
	${UE_SYSROOT_CANDIDATE}
	CACHE PATH ""
)

# Anchor the build to the engine's bundled toolchain sysroot. Without --sysroot, clang links
# against whatever default sysroot it was built with, so the C runtime, libc and libm are
# resolved inconsistently: a trivial executable links, but anything pulling in libm (Eigen's
# "Can't link to the standard math library" check) or extra libc symbols (Boost.Filesystem's
# statx / dirent / POSIX at-API probes) fails to link. Pointing --sysroot at the bundle makes
# clang take its headers, crt objects, libc and libm all from one consistent place, so the
# build no longer depends on host -devel packages for the C runtime.
set (CMAKE_SYSROOT ${UE_SYSROOT})

# Skip Eigen's standard-math-library probe deterministically. Eigen is used header-only here, so
# its FindStandardMathLibrary detection only gates Eigen's own (unbuilt) tests/examples -- it is
# irrelevant to anything CARLA links. The probe's -lm link cannot resolve the bundle's libm.so /
# libm.a, which are GNU ld linker scripts whose GROUP() absolute paths (e.g. /lib64/libm.so.6) do
# not exist at the sysroot-prefixed location, so it fails even with --sysroot and aborts configure
# with "Can't link to the standard math library".
#
# FindStandardMathLibrary runs find_package unconditionally and RECOMPUTES STANDARD_MATH_LIBRARY_FOUND
# with a plain set() each run, so pre-seeding that variable is not reliable (a plain set shadows the
# cache). Instead seed the probe's OWN result cache variable: CHECK_CXX_SOURCE_COMPILES skips the
# test when its result variable is already cached, which then drives the module to set
# STANDARD_MATH_LIBRARY_FOUND=TRUE / STANDARD_MATH_LIBRARY="" itself. This is deterministic
# regardless of prior cache state, and the CACHE INTERNAL value is inherited by FetchContent
# subprojects.
set (standard_math_library_linked_to_automatically TRUE CACHE INTERNAL "Eigen math-library probe: pre-seeded; libm is provided by the UE toolchain")

set (
	UE_THIRD_PARTY
	${UE_ROOT}/Engine/Source/ThirdParty CACHE PATH ""
)

# UE 5.7.4: ThirdParty/Unix/LibCxx no longer exists; LibCxx is now bundled inside the clang
# sysroot. The C headers are under usr/include; the libc++ headers (cmath, etc.) are in a
# c++/v1 dir whose location VARIES by bundle -- the clang-20/rockylinux8 bundle puts them under
# include/c++/v1 (a sibling of usr/), older bundles under usr/include/c++/v1. The static libs are
# located separately below (also bundle-dependent).
set (
	UE_INCLUDE
	${UE_SYSROOT}/usr/include CACHE PATH ""
)

# Detect the libc++ header dir (must be searched before the C headers; see the note on
# CMAKE_CXX_STANDARD_INCLUDE_DIRECTORIES below). Probe for a known libc++ header (cmath).
set (UE_CXX_INCLUDE "")
foreach (_ue_cxx_candidate
	${UE_SYSROOT}/include/c++/v1
	${UE_SYSROOT}/usr/include/c++/v1)
	if (EXISTS ${_ue_cxx_candidate}/cmath)
		set (UE_CXX_INCLUDE ${_ue_cxx_candidate})
		break ()
	endif ()
endforeach ()
if (NOT UE_CXX_INCLUDE)
	message (FATAL_ERROR "Could not find libc++ headers (cmath) under the Unreal clang toolchain at \"${UE_SYSROOT}\" (checked include/c++/v1, usr/include/c++/v1).")
endif ()

# Locate the static libc++ / libc++abi. The directory varies between bundled toolchains:
# older clang bundles put them under usr/lib, the clang-20/rockylinux8 bundle uses usr/lib64,
# and some use lib/lib64. Search the candidates and use the first that actually has libc++.a
# instead of assuming usr/lib (which fails with "no such file or directory: .../libc++.a").
set (UE_LIBS "")
foreach (_ue_lib_candidate
	${UE_SYSROOT}/usr/lib64
	${UE_SYSROOT}/usr/lib
	${UE_SYSROOT}/lib64
	${UE_SYSROOT}/lib)
	if (EXISTS ${_ue_lib_candidate}/libc++.a)
		set (UE_LIBS ${_ue_lib_candidate})
		break ()
	endif ()
endforeach ()
if (NOT UE_LIBS)
	message (FATAL_ERROR "Could not find libc++.a under the Unreal clang toolchain at \"${UE_SYSROOT}\" (checked usr/lib64, usr/lib, lib64, lib).")
endif ()

set (
	UE_OPENSSL_INCLUDE
	${UE_THIRD_PARTY}/OpenSSL/1.1.1t/include/Unix CACHE PATH ""
)

set (
	UE_OPENSSL_LIBS
	${UE_THIRD_PARTY}/OpenSSL/1.1.1t/lib/Unix/x86_64-unknown-linux-gnu CACHE PATH ""
)

# Set compile/link flags via the *_INIT variables, NOT add_compile_options/add_link_options.
# add_*_options set directory-scope properties that try_compile() and CHECK_CXX_SOURCE_COMPILES
# do NOT inherit, so dependency configure checks (Eigen's standard-math-library check,
# Boost.Filesystem's statx/dirent/at-API probes, etc.) would compile/link WITHOUT these flags and
# fail. The *_INIT variables seed the corresponding cache flags and ARE propagated into those checks.
string (APPEND CMAKE_C_FLAGS_INIT   " -fms-extensions -fno-math-errno -fdiagnostics-absolute-paths")
string (APPEND CMAKE_CXX_FLAGS_INIT " -fms-extensions -fno-math-errno -fdiagnostics-absolute-paths -stdlib=libc++")

# Link flags. --sysroot MUST be on the link command (not only the compile): the bundle's libm.so /
# libm.a / libc.so are GNU ld linker SCRIPTS whose GROUP()/INPUT() entries are absolute paths
# (e.g. /lib64/libm.so.6). ld only rewrites those relative to the sysroot when it receives
# --sysroot itself; clang forwards --sysroot to ld, and ld then resolves them inside the bundle.
# Without it, ld takes the script paths literally (outside the bundle) and Eigen reports
# "Can't link to the standard math library". CMAKE_SYSROOT covers the compile but does not reliably
# reach the link here, so set it explicitly. -L/-B add the bundle's lib dirs (usr/lib64 holds the
# bundled libc++/libm; ld's default sysroot search may only cover usr/lib) and let ld find the crt
# objects and the libm/libc scripts. Applied to exe/shared/module so every link kind -- including
# the configure checks -- receives them.
set (UE_LINK_FLAGS "-stdlib=libc++ --sysroot=${UE_SYSROOT} -L${UE_LIBS} -L${UE_SYSROOT}/usr/lib64 -L${UE_SYSROOT}/usr/lib -B${UE_SYSROOT}/usr/lib64 -B${UE_SYSROOT}/usr/lib")
string (APPEND CMAKE_EXE_LINKER_FLAGS_INIT    " ${UE_LINK_FLAGS}")
string (APPEND CMAKE_SHARED_LINKER_FLAGS_INIT " ${UE_LINK_FLAGS}")
string (APPEND CMAKE_MODULE_LINKER_FLAGS_INIT " ${UE_LINK_FLAGS}")

set (
	CMAKE_AR
	${UE_SYSROOT}/bin/llvm-ar
	CACHE FILEPATH ""
)

set (
	CMAKE_ASM_COMPILER
	${UE_SYSROOT}/bin/clang
	CACHE FILEPATH ""
)

set (
	CMAKE_C_COMPILER
	${UE_SYSROOT}/bin/clang
	CACHE FILEPATH ""
)

set (
	CMAKE_C_COMPILER_AR
	${UE_SYSROOT}/bin/llvm-ar
	CACHE FILEPATH ""
)

set (
	CMAKE_CXX_COMPILER
	${UE_SYSROOT}/bin/clang++
	CACHE FILEPATH ""
)

set (
	CMAKE_CXX_COMPILER_AR
	${UE_SYSROOT}/bin/llvm-ar
	CACHE FILEPATH ""
)

set (
	CMAKE_OBJCOPY
	${UE_SYSROOT}/bin/llvm-objcopy
	CACHE FILEPATH ""
)

set (
	CMAKE_ADDR2LINE
	${UE_SYSROOT}/bin/${TARGET_TRIPLE}-addr2line
	CACHE FILEPATH ""
)

set (
	CMAKE_C_COMPILER_RANLIB
	${UE_SYSROOT}/bin/${TARGET_TRIPLE}-ranlib
	CACHE FILEPATH ""
)

set (
	CMAKE_CXX_COMPILER_RANLIB
	${UE_SYSROOT}/bin/${TARGET_TRIPLE}-ranlib
	CACHE FILEPATH ""
)

set (
	CMAKE_LINKER
	${UE_SYSROOT}/bin/${TARGET_TRIPLE}-ld
	CACHE FILEPATH ""
)

set (
	CMAKE_NM
	${UE_SYSROOT}/bin/${TARGET_TRIPLE}-nm
	CACHE FILEPATH ""
)

set (
	CMAKE_OBJDUMP
	${UE_SYSROOT}/bin/${TARGET_TRIPLE}-objdump
	CACHE FILEPATH ""
)

set (
	CMAKE_RANLIB
	${UE_SYSROOT}/bin/${TARGET_TRIPLE}-ranlib
	CACHE FILEPATH ""
)

set (
	CMAKE_READELF
	${UE_SYSROOT}/bin/${TARGET_TRIPLE}-readelf
	CACHE FILEPATH ""
)

set (
	CMAKE_STRIP
	${UE_SYSROOT}/bin/${TARGET_TRIPLE}-strip
	CACHE FILEPATH ""
)

set (
	COVERAGE_COMMAND
	${UE_SYSROOT}/bin/${TARGET_TRIPLE}-gcov
	CACHE FILEPATH ""
)

set (
	CMAKE_CXX_STANDARD_LIBRARIES
	"${UE_LIBS}/libc++.a ${UE_LIBS}/libc++abi.a"
)

set (
	# libc++'s c++/v1 headers MUST be searched BEFORE the C sysroot headers. libc++'s <cstdlib>
	# includes its own <stdlib.h> wrapper (which then #include_next's the C <stdlib.h>); if the C
	# include dir comes first, <cstdlib> resolves <stdlib.h> straight to the C header, never finds
	# the libc++ wrapper, and clang aborts with "<cstdlib> tried including <stdlib.h> but didn't
	# find libc++'s <stdlib.h> header" (and a cascade of the same for every C++ std header).
	CMAKE_CXX_STANDARD_INCLUDE_DIRECTORIES
	${UE_CXX_INCLUDE} ${UE_INCLUDE}
)

endif ()
