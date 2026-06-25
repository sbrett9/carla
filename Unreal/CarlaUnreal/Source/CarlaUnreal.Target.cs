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
        // NB: the shadowed-variable warning (UE escalates -Wshadow to error) is downgraded at the
        // MODULE level (see the CARLA modules' Build.cs), not here. It is a shared build-environment
        // property, and CarlaUnreal shares its environment with the installed engine, so setting it
        // on the target is rejected by UBT.

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
