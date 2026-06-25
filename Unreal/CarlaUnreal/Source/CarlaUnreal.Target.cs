// Fill out your copyright notice in the Description page of Project Settings.

using UnrealBuildTool;
using System;
using EpicGames.Core;

public class CarlaUnrealTarget : TargetRules
{
    [CommandLine("-unity-build")]
    bool EnableUnityBuild = true;

    private static void LogFlagStatus(string name, bool value)
    {
        var state = value ? "enabled" : "disabled";
        Console.WriteLine(string.Format("{0} is {1}.", name, state));
    }

    public CarlaUnrealTarget(TargetInfo Target) :
        base(Target)
    {
        DefaultBuildSettings = BuildSettingsVersion.Latest;
        IncludeOrderVersion = EngineIncludeOrderVersion.Latest;
        Type = TargetType.Game;

        // Don't let compiler warnings fail the build. Linux/clang is stricter than MSVC,
        // so a build clean on Windows can otherwise hit warning-as-error failures on Linux.
        bWarningsAsErrors = false;
        // UE escalates shadowed-variable warnings to errors by default, independent of
        // bWarningsAsErrors. clang's -Wshadow is far stricter than MSVC's, so CARLA code that builds
        // clean on Windows trips it on Linux (e.g. MeshToLandscape.cpp). Downgrade to a warning.
        ShadowVariableWarningLevel = WarningLevel.Warning;

        ExtraModuleNames.Add("CarlaUnreal");

        LogFlagStatus("Unity build", EnableUnityBuild);

        if (!EnableUnityBuild)
        {
            bUseUnityBuild =
            bForceUnityBuild =
            bUseAdaptiveUnityBuild = false;
        }
    }
}
