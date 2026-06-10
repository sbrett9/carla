@echo off
setlocal EnableDelayedExpansion

set skip_prerequisites=false
set launch=false
set interactive=false
set python_path=python
set python_root=
set vibeue_ssh_key=

rem -- PARSE COMMAND LINE ARGUMENTS --

:parse
    if "%1"=="" (
        goto main
    )
    if "%1"=="--interactive" (
        set interactive=true
    ) else if "%1"=="-i" (
        set interactive=true
    ) else if "%1"=="--skip-prerequisites" (
        set skip_prerequisites=true
    ) else if "%1"=="-p" (
        set skip_prerequisites=true
    ) else if "%1"=="--launch" (
        set launch=true
    ) else if "%1"=="-l" (
        set launch=true
    ) else (
        echo %1 | findstr /B /C:"--python-root=" >nul
        if not errorlevel 1 (
            set python_root="%1"
            set python_root="!python_root:--python-root=!"
        ) else (
            echo %1 | findstr /B /C:"--vibeue-ssh-key=" >nul
            if not errorlevel 1 (
                set "vibeue_ssh_key=%1"
                set "vibeue_ssh_key=!vibeue_ssh_key:--vibeue-ssh-key=!"
            ) else if "%1"=="--python-root" (
                set python_root=%2
                shift
            ) else if "%1"=="-pyroot" (
                set python_root=%2
                shift
            ) else if "%1"=="--vibeue-ssh-key" (
                set vibeue_ssh_key=%2
                shift
            ) else (
                echo Unknown argument "%1"
                exit /b
            )
        )
    )
    shift
    goto parse

rem -- MAIN --

:main

if not "%python_root%"=="" (
    set python_path=%python_root%\python
)

rem -- PREREQUISITES INSTALL STEP --

if %skip_prerequisites%==false (
    echo Installing prerequisites...
    call Util/SetupUtils/InstallPrerequisites.bat --python-path=%python_path% || exit /b
) else (
    echo Skipping prerequisites install step.
)

rem -- CLONE CONTENT --
if exist "%cd%\Unreal\CarlaUnreal\Content" (
    echo Found CARLA content.
) else (
    echo Could not find CARLA content. Downloading...
    mkdir %cd%\Unreal\CarlaUnreal\Content
    git ^
        -C %cd%\Unreal\CarlaUnreal\Content ^
        clone ^
        -b ue5-dev ^
        https://bitbucket.org/carla-simulator/carla-content.git ^
        Carla ^
    || exit /b
)

rem Activate VS terminal development environment:
rem Check if already in a VS developer command prompt
if defined VSINSTALLDIR (
    echo Already in Visual Studio developer command prompt, skipping vcvars64.bat activation.
    echo Using: %VSINSTALLDIR%
) else (
    set "vs_env_bat="
    if exist "%PROGRAMFILES%\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat" (
        set "vs_env_bat=%PROGRAMFILES%\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
    )
    if exist "%PROGRAMFILES%\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars64.bat" (
        set "vs_env_bat=%PROGRAMFILES%\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars64.bat"
    )
    if exist "%PROGRAMFILES%\Microsoft Visual Studio\2022\Enterprise\VC\Auxiliary\Build\vcvars64.bat" (
        set "vs_env_bat=%PROGRAMFILES%\Microsoft Visual Studio\2022\Enterprise\VC\Auxiliary\Build\vcvars64.bat"
    )

    if not "%vs_env_bat%"=="" (
        echo Activating "x64 Native Tools Command Prompt" terminal environment.
        call "%vs_env_bat%" || exit /b
    ) else (
        echo Could not find vcvars64.bat for VS 2022, aborting setup...
        echo.
        echo TIP: You can run this script from a Visual Studio Developer Command Prompt instead.
        echo      Open "x64 Native Tools Command Prompt for VS 2026" and run CarlaSetup.bat from there.
        exit /b 1
    )
)

rem -- DOWNLOAD + BUILD UNREAL ENGINE --
if exist "%CARLA_UNREAL_ENGINE_PATH%" (
    echo Found Unreal Engine 5 at "%CARLA_UNREAL_ENGINE_PATH%".
) else (
    echo ERROR: CARLA_UNREAL_ENGINE_PATH is not set or does not exist.
    echo Please set CARLA_UNREAL_ENGINE_PATH to the root of your UE 5.7.4 source build.
    echo Example: set CARLA_UNREAL_ENGINE_PATH=g:\Projects\CarlaUE_5_7_4\UE_5_7_4
    exit /b 1
)

rem -- BUILD SUMO netconvert (OSM -> OpenDRIVE converter, bundled for CarlaNet) --
rem CarlaNet shells out to stock SUMO `netconvert` at runtime to convert OSM maps
rem to OpenDRIVE, replacing CARLA's old in-tree osm2odr fork. We build ONLY the
rem `netconvert` target from SUMO release v1_27_0 (commit e238ea04). On Windows the
rem build deps (Xerces-C, PROJ, sqlite3, ...) come from the prebuilt DLR-TS
rem SUMOLibraries bundle; the build copies the needed DLLs next to netconvert.exe.
set "sumo_src=%cd%\Build\sumo-src"
set "sumo_build=%cd%\Build\sumo-build"
set "sumo_install=%cd%\Build\sumo-install"
set "sumo_libs=%cd%\Build\SUMOLibraries"

if exist "%sumo_install%\bin\netconvert.exe" (
    echo Found SUMO netconvert at "%sumo_install%\bin\netconvert.exe". Skipping SUMO build.
) else (
    echo Building SUMO netconvert...
    if not exist "%sumo_libs%" (
        echo Cloning SUMOLibraries prebuilt Windows deps ^(~3 GB, one-time^)...
        git clone --depth 1 https://github.com/DLR-TS/SUMOLibraries.git "%sumo_libs%" || exit /b
    )
    if not exist "%sumo_src%" (
        echo Cloning SUMO v1_27_0...
        git clone --depth 1 --branch v1_27_0 https://github.com/eclipse-sumo/sumo.git "%sumo_src%" || exit /b
    )
    rem Pin the exact commit (the tag already points here; this is an explicit guard).
    git -C "%sumo_src%" checkout e238ea04b7150ba23a348a285d3048919fa4830b || exit /b
    rem Configure + build ONLY the netconvert target (Release) with the VS generator.
    set "SUMO_LIBRARIES=%sumo_libs%"
    cmake ^
        -B "%sumo_build%" ^
        -S "%sumo_src%" ^
        -G "Visual Studio 17 2022" ^
        -A x64 ^
        -DCHECK_OPTIONAL_LIBS=false || exit /b
    cmake --build "%sumo_build%" --target netconvert --config Release -- -m || exit /b
    rem The build emits netconvert.exe + its runtime DLLs into Build\sumo-src\bin.
    rem Stage the binary, its DLLs, and the PROJ data (proj.db) for CarlaNet.
    if not exist "%sumo_install%\bin" mkdir "%sumo_install%\bin"
    copy /Y "%sumo_src%\bin\netconvert.exe" "%sumo_install%\bin\" || exit /b
    copy /Y "%sumo_src%\bin\*.dll" "%sumo_install%\bin\" || exit /b
    xcopy /E /I /Y "%sumo_libs%\proj-9.5.0\share\proj" "%sumo_install%\share\proj" || exit /b
    echo Staged netconvert at "%sumo_install%\bin\netconvert.exe".
)
rem CarlaNet locates the tool via env vars (see NETCONVERT_INTEGRATION.md):
rem   CARLA_NETCONVERT -> the netconvert binary
rem   PROJ_LIB / PROJ_DATA -> the directory containing proj.db
echo To use netconvert from CarlaNet, set:
echo   set CARLA_NETCONVERT=%sumo_install%\bin\netconvert.exe
echo   set PROJ_LIB=%sumo_install%\share\proj

rem -- VibeUE editor MCP plugin (OPTIONAL, private mirror, pinned) --
rem In-editor MCP bridge for digital-twin development: a PRIVATE mirror of
rem kevinpbuckley/VibeUE with the vibeue.com API-key validation removed (offline).
rem NOT referenced by the .uproject, so it is an optional auto-discovered plugin and
rem the CARLA build proceeds without it. Pinned to an exact commit; fetched over SSH
rem with a key from --vibeue-ssh-key=<path> or %VIBEUE_SSH_KEY%. Use a space-free,
rem forward-slash key path (e.g. C:/Users/you/.ssh/id_ed25519) to avoid quoting issues.
set "vibeue_dir=%cd%\Unreal\CarlaUnreal\Plugins\VibeUE"
set "vibeue_repo=git@github.com:sbrett9/VibeUE.git"
set "vibeue_pin=379373709e68ce7f2c4e3a26ff931f703d87b817"
set "vibeue_key=%vibeue_ssh_key%"
if "%vibeue_key%"=="" set "vibeue_key=%VIBEUE_SSH_KEY%"

if exist "%vibeue_dir%\.git" (
    if not "%vibeue_key%"=="" set "GIT_SSH_COMMAND=ssh -i %vibeue_key% -o IdentitiesOnly=yes"
    echo Pinning VibeUE to %vibeue_pin%...
    git -C "%vibeue_dir%" fetch origin
    git -C "%vibeue_dir%" checkout %vibeue_pin%
) else if not "%vibeue_key%"=="" (
    echo Cloning VibeUE private mirror ^(pinned %vibeue_pin%^)...
    if exist "%vibeue_dir%" rmdir /S /Q "%vibeue_dir%"
    set "GIT_SSH_COMMAND=ssh -i %vibeue_key% -o IdentitiesOnly=yes"
    git clone "%vibeue_repo%" "%vibeue_dir%"
    git -C "%vibeue_dir%" checkout %vibeue_pin%
) else if exist "%vibeue_dir%" (
    echo VibeUE present as a non-git copy; leaving as-is ^(no SSH key to convert it to a pinned clone^).
) else (
    echo VibeUE skipped ^(optional MCP plugin^). Pass --vibeue-ssh-key=^<path^> or set VIBEUE_SSH_KEY to fetch it.
)

rem -- BUILD CARLA --
echo Configuring the CARLA CMake project...
cmake ^
    -G Ninja ^
    -S . ^
    -B Build ^
    --toolchain=CMake/Toolchain.cmake ^
    -DPython_ROOT_DIR=%python_root% ^
    -DPython3_ROOT_DIR=%python_root% ^
    -DCMAKE_BUILD_TYPE=Release ^
    -DCARLA_UNREAL_ENGINE_PATH=%CARLA_UNREAL_ENGINE_PATH% || exit /b
echo Building CARLA...
cmake --build Build || exit /b
echo Installing Python API...
cmake --build Build --target carla-python-api-install || exit /b
echo CARLA Python API build+install succeeded.

rem -- POST-BUILD STEPS --

if %launch%==true (
    echo Launching Carla Unreal Editor...
    cmake --build Build --target launch || exit /b
)
