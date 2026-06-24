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

set (
	UE_THIRD_PARTY
	${UE_ROOT}/Engine/Source/ThirdParty CACHE PATH ""
)

# UE 5.7.4: ThirdParty/Unix/LibCxx no longer exists; LibCxx is now bundled inside the clang
# sysroot. Headers live under usr/include (libc++ at usr/include/c++/v1); the static libs are
# located separately below (their dir varies by bundle).
set (
	UE_INCLUDE
	${UE_SYSROOT}/usr/include CACHE PATH ""
)

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

add_compile_options (
	-fms-extensions
	-fno-math-errno
	-fdiagnostics-absolute-paths
	$<$<COMPILE_LANGUAGE:CXX>:-stdlib=libc++>
)

add_link_options (-stdlib=libc++ -L${UE_LIBS} )

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
	${UE_INCLUDE}/c++/v1 ${UE_INCLUDE}
)

endif ()
