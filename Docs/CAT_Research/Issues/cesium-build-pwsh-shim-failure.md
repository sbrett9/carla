# Cesium Build Failure: vcpkg pwsh.exe Shim Cannot Find dotnet

**Status**: Open  
**Date**: 2026-06-15  
**Commit**: 891dc108a (Add Cesium for Unreal source build)  
**Branch**: ue5-dev

## Problem

After commit 891dc108a ("Add Cesium for Unreal source build"), running `CarlaSetup.ps1` fails during the Cesium native library build when vcpkg attempts to build the `draco` package.

## Environment

- **OS**: Windows 11
- **Shell**: Windows PowerShell 5.1.26100.8457 (Desktop edition)
- **Visual Studio**: 2026 (MSVC 14.44.35207)
- **CMake**: 4.3.2
- **Workspace**: `D:\Projects\CAT\CarlaUE\`
- **Commit**: 891dc108a (ue5-dev branch)

**PATH configuration**:
- `C:\Users\110191\.dotnet\tools` (contains pwsh.exe shim)
- `C:\Program Files\dotnet\` (dotnet SDK)
- `DOTNET_ROOT` environment variable is set to `C:\Program Files\dotnet`

## Steps to Reproduce

```powershell
.\CarlaSetup.ps1 -Vs 2026 -p
```

## Error Output

```
-- EZVCPKG Building/Verifying package draco using triplet x64-windows-unreal
CMake Error at cesium-native/cmake/ezvcpkg/ezvcpkg.cmake:83 (message):
  EZVCPKG failed with error 1

*** The output from the command was:
Installing 1/1 draco:x64-windows-unreal@1.5.7...
Building draco:x64-windows-unreal@1.5.7...
-- Building x64-windows-unreal-dbg
-- Building x64-windows-unreal-rel
CMake Error at scripts/cmake/vcpkg_execute_required_process.cmake:127 (message):
    Command failed: C:/Users/110191/.dotnet/tools/pwsh.exe -noprofile -executionpolicy Bypass -nologo -file D:/.ezvcpkg/afc0a2e01ae104a2474216a2df0e8d78516fd5af/scripts/buildsystems/msbuild/applocal.ps1 -targetBinary D:/.ezvcpkg/afc0a2e01ae104a2474216a2df0e8d78516fd5af/packages/draco_x64-windows-unreal/tools/draco/draco_decoder.exe -installedDir D:/.ezvcpkg/afc0a2e01ae104a2474216a2df0e8d78516fd5af/packages/draco_x64-windows-unreal/bin -verbose
    Working Directory: D:/.ezvcpkg/afc0a2e01ae104a2474216a2df0e8d78516fd5af
    Error code: -532462766
```

**Error log** (`D:\.ezvcpkg\...\buildtrees\draco\copy-tool-dependencies-0-err.log`):
```
Unhandled exception. System.ComponentModel.Win32Exception (2): An error occurred trying to start process 'dotnet' with working directory 'D:\.ezvcpkg\afc0a2e01ae104a2474216a2df0e8d78516fd5af'. The system cannot find the file specified.
   at System.Diagnostics.Process.StartWithCreateProcess(ProcessStartInfo startInfo)
   at System.Diagnostics.Process.Start(ProcessStartInfo startInfo)
   at Microsoft.PowerShell.GlobalTool.Shim.EntryPoint.Main(String[] args)
```

## Diagnosis

### What's Happening

1. vcpkg searches for PowerShell and finds `C:/Users/110191/.dotnet/tools/pwsh.exe` first in PATH
2. This is a **.NET tool shim** (not PowerShell Core itself), which must launch `dotnet.exe` to run the actual PowerShell
3. vcpkg spawns the shim with working directory: `D:/.ezvcpkg/afc0a2e01ae104a2474216a2df0e8d78516fd5af`
4. **The shim fails to locate `dotnet.exe`**, even though:
   - `C:\Program Files\dotnet\dotnet.exe` exists
   - `C:\Program Files\dotnet\` is in system PATH
   - `DOTNET_ROOT` environment variable is set to `C:\Program Files\dotnet`
   - Running `pwsh.exe` manually from the same shell works fine
   - Running a fresh `pwsh -NoProfile -Command "dotnet --version"` succeeds

### Verification

**Current shell has dotnet available**:
```powershell
PS> $PSVersionTable.PSVersion
5.1.26100.8457

PS> Get-Command dotnet | Select-Object -ExpandProperty Source
C:\Program Files\dotnet\dotnet.exe

PS> $env:DOTNET_ROOT
C:\Program Files\dotnet
```

**Fresh pwsh subprocess can find dotnet**:
```powershell
PS> pwsh -NoProfile -Command { Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source }
C:\Program Files\dotnet\dotnet.exe
```

**But the vcpkg-spawned pwsh shim cannot**, despite identical environment variables.

## Root Cause

The .NET tool shim at `C:\Users\110191\.dotnet\tools\pwsh.exe` fails to resolve `dotnet.exe` when spawned by vcpkg as a subprocess with:
- Working directory on D:\ drive
- Cross-drive path resolution required (D:\ → C:\Program Files\dotnet\)

The shim's dotnet resolution logic does not properly utilize PATH or DOTNET_ROOT in this subprocess context.

## Impact

Cannot complete Cesium build via CarlaSetup.ps1 on Windows systems where:
- PowerShell Core is installed as a .NET tool (common via `dotnet tool install -g PowerShell`)
- `.dotnet\tools\pwsh.exe` appears before system PowerShell in PATH
- Workspace is on a different drive than dotnet SDK

## Related Information

- vcpkg version (from ezvcpkg): commit `afc0a2e01ae104a2474216a2df0e8d78516fd5af`
- The error is reproducible across multiple clean runs with `-CleanCesium` flag
- Manually running the same pwsh command from the D:\ drive works fine when invoked from a shell
- Current shell is Windows PowerShell 5.1, not PowerShell Core, but vcpkg discovers and prefers pwsh.exe

## Potential Workarounds (Not Tested)

1. Temporarily remove `.dotnet\tools` from PATH during Cesium build
2. Uninstall PowerShell as a .NET tool and install native PowerShell Core
3. Force vcpkg to use Windows PowerShell instead of pwsh
4. Modify vcpkg scripts to use `powershell.exe` instead of searching for `pwsh.exe`
