// Fill out your copyright notice in the Description page of Project Settings.

using EpicGames.Core;
using System;
using UnrealBuildTool;

public class CarlaUnreal : ModuleRules
{
	[CommandLine("-slate-ui")]
	bool EnableSlateUI = false;

    [CommandLine("-online-subsys")]
    bool EnableOnlineSubSys = false;

    private static void LogFlagStatus(string name, bool value)
    {
        var state = value ? "enabled" : "disabled";
        Console.WriteLine(string.Format("{0} is {1}.", name, state));
    }

    public CarlaUnreal(ReadOnlyTargetRules Target) : base(Target)
	{
		PrivatePCHHeaderFile = "CarlaUnreal.h";
        bEnableExceptions = true;
        bUseRTTI = true;
        // Downgrade clang's -Wshadow (escalated to error by UE) to a warning for this first-party
        // module so it builds on Linux/clang. Per-module, since the editor target can't change this
        // shared build-environment property.
#if UE_5_7_OR_LATER
        CppCompileWarningSettings.ShadowVariableWarningLevel = WarningLevel.Warning;
#else
        ShadowVariableWarningLevel = WarningLevel.Warning;
#endif

        PublicIncludePaths.Add(ModuleDirectory);
        PublicIncludePaths.Add(System.IO.Path.Combine(ModuleDirectory, "../../../../LibCarla/source"));

		PublicDependencyModuleNames.AddRange(new string[]
		{
			"Core",
			"CoreUObject",
			"Engine",
			"InputCore"
		});

		if (Target.Type == TargetType.Editor)
            PublicDependencyModuleNames.Add("UnrealEd");

        LogFlagStatus("Slate UI", EnableSlateUI);

        if (EnableSlateUI)
            PrivateDependencyModuleNames.AddRange(new string[] { "Slate", "SlateCore" });

        LogFlagStatus("Online Subsystem", EnableOnlineSubSys);

        if (EnableOnlineSubSys)
			PrivateDependencyModuleNames.Add("OnlineSubsystem");

        // To include OnlineSubsystemSteam, add it to the plugins section in your uproject file with the Enabled attribute set to true
    }
}
