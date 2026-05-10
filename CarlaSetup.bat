@echo off
setlocal EnableDelayedExpansion

set skip_prerequisites=false
set launch=false
set interactive=false
set python_path=python
set python_root=

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
        ) else if "%1"=="--python-root" (
            set python_root=%2
            shift
        ) else if "%1"=="-pyroot" (
            set python_root=%2
            shift
        ) else (
            echo Unknown argument "%1"
            exit /b
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
    exit 1
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
