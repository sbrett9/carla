// Copyright 1998-2017 Epic Games, Inc. All Rights Reserved.

#include "Carla.h"
#include "Settings/CarlaSettings.h"

#include <util/ue-header-guard-begin.h>
#include "Developer/Settings/Public/ISettingsModule.h"
#include "Developer/Settings/Public/ISettingsSection.h"
#include "Developer/Settings/Public/ISettingsContainer.h"
#include "Interfaces/IPluginManager.h"
#include "Misc/ConfigCacheIni.h" // GConfig
#include "Misc/Paths.h"          // FPaths::ProjectConfigDir
#include <util/ue-header-guard-end.h>

#define LOCTEXT_NAMESPACE "FCarlaModule"

DEFINE_LOG_CATEGORY(LogCarla);
DEFINE_LOG_CATEGORY(LogCarlaServer);

FString GetCarlaWorldInterfaceVersion()
{
	// Its own file rather than a section of DefaultGame.ini, so that changing what this build
	// promises is a visible one-line commit instead of a line lost among cook settings. Read on
	// demand rather than cached: it is asked for once at startup and over RPC, and a stale cache
	// would be a way to report a promise the build is no longer making.
	const FString ConfigPath =
		FPaths::ProjectConfigDir() / TEXT("DefaultWorldInterface.ini");

	int32 Major = 0;
	int32 Minor = 0;
	if (!GConfig || !GConfig->GetInt(TEXT("WorldInterface"), TEXT("Major"), Major, ConfigPath)
		|| !GConfig->GetInt(TEXT("WorldInterface"), TEXT("Minor"), Minor, ConfigPath))
	{
		// Deliberately not a usable version. A build that cannot say what it supports must not have
		// worlds installed into it on the strength of a guess.
		UE_LOG(LogCarla, Warning,
			TEXT("[Carla] no world interface version declared in %s; reporting 0.0, which no "
				 "delivered world will match."), *ConfigPath);
		return TEXT("0.0");
	}
	return FString::Printf(TEXT("%d.%d"), Major, Minor);
}

void FCarlaModule::StartupModule()
{
	RegisterSettings();
	LoadChronoDll();
	MountExportedWorlds();

	// Logged at startup because that is where Unreal states versions, and because a packaged server
	// that will not accept a world should say what it does accept without being asked.
	UE_LOG(LogCarla, Display,
		TEXT("[Carla] world interface version %s"), *GetCarlaWorldInterfaceVersion());
}

void FCarlaModule::MountExportedWorlds()
{
	// A world exported as its own plugin is marked ExplicitlyLoaded, so the engine finds it but does
	// not mount it: a package holding many worlds should not pay for all of them, and a world is
	// wanted only when it is asked for. Nothing else mounts them, though, and until a plugin is
	// mounted its content root does not exist -- so /<World>/Maps/<World> resolves to nothing and the
	// engine offers to load the default map instead, which looks like the wrong world rather than a
	// missing one.
	//
	// Mounting only registers the content root; it does not load a level. Doing it here, before the
	// startup map is browsed, makes an exported world reachable both from -map= at launch and from
	// load_world once running.
	IPluginManager& Plugins = IPluginManager::Get();
	int32 Mounted = 0;
	const TArray<TSharedRef<IPlugin>> Discovered = Plugins.GetDiscoveredPlugins();
	UE_LOG(LogCarla, Display, TEXT("Looking for exported worlds among %d discovered plugin(s)."),
		Discovered.Num());
	for (const TSharedRef<IPlugin>& Plugin : Discovered)
	{
		const FPluginDescriptor& Descriptor = Plugin->GetDescriptor();
		// The category is what the exporter writes to mark its own output, so this cannot mount an
		// unrelated plugin the project happens to ship.
		if (Descriptor.Category != TEXT("Generated Worlds")
			|| !Descriptor.bExplicitlyLoaded
			|| !Plugin->CanContainContent())
		{
			UE_LOG(LogCarla, VeryVerbose,
				TEXT("Plugin '%s': category '%s', explicitly loaded %d, content %d -- not an exported world."),
				*Plugin->GetName(), *Descriptor.Category,
				Descriptor.bExplicitlyLoaded ? 1 : 0, Plugin->CanContainContent() ? 1 : 0);
			continue;
		}
		if (Plugins.MountExplicitlyLoadedPlugin(Plugin->GetName()))
		{
			++Mounted;
			UE_LOG(LogCarla, Display, TEXT("Mounted exported world '%s'."), *Plugin->GetName());
		}
		else
		{
			UE_LOG(LogCarla, Warning,
				TEXT("Exported world '%s' could not be mounted; it cannot be loaded by name."),
				*Plugin->GetName());
		}
	}
	if (Mounted > 0)
	{
		UE_LOG(LogCarla, Display, TEXT("Mounted %d exported world(s)."), Mounted);
	}
}

void FCarlaModule::LoadChronoDll()
{
	#if defined(WITH_CHRONO) && PLATFORM_WINDOWS
	const FString BaseDir = FPaths::Combine(*FPaths::ProjectPluginsDir(), TEXT("Carla"));
	const FString DllDir = FPaths::Combine(*BaseDir, TEXT("CarlaDependencies"), TEXT("dll"));
	FString ChronoEngineDll = FPaths::Combine(*DllDir, TEXT("ChronoEngine.dll"));
	FString ChronoVehicleDll = FPaths::Combine(*DllDir, TEXT("ChronoEngine_vehicle.dll"));
	FString ChronoModelsDll = FPaths::Combine(*DllDir, TEXT("ChronoModels_vehicle.dll"));
	FString ChronoRobotDll = FPaths::Combine(*DllDir, TEXT("ChronoModels_robot.dll"));
	UE_LOG(LogCarla, Log, TEXT("Loading Dlls from: %s"), *DllDir);
	auto ChronoEngineHandle = FPlatformProcess::GetDllHandle(*ChronoEngineDll);
	if (!ChronoEngineHandle)
	{
		UE_LOG(LogCarla, Warning, TEXT("Error: ChronoEngine.dll could not be loaded"));
	}
	auto ChronoVehicleHandle = FPlatformProcess::GetDllHandle(*ChronoVehicleDll);
	if (!ChronoVehicleHandle)
	{
		UE_LOG(LogCarla, Warning, TEXT("Error: ChronoEngine_vehicle.dll could not be loaded"));
	}
	auto ChronoModelsHandle = FPlatformProcess::GetDllHandle(*ChronoModelsDll);
	if (!ChronoModelsHandle)
	{
		UE_LOG(LogCarla, Warning, TEXT("Error: ChronoModels_vehicle.dll could not be loaded"));
	}
	auto ChronoRobotHandle = FPlatformProcess::GetDllHandle(*ChronoRobotDll);
	if (!ChronoRobotHandle)
	{
		UE_LOG(LogCarla, Warning, TEXT("Error: ChronoModels_robot.dll could not be loaded"));
	}
	#endif
}

void FCarlaModule::ShutdownModule()
{
	if (UObjectInitialized())
	{
		UnregisterSettings();
	}
}

void FCarlaModule::RegisterSettings()
{
	// Registering some settings is just a matter of exposing the default UObject of
	// your desired class, add here all those settings you want to expose
	// to your LDs or artists.

	if (ISettingsModule* SettingsModule = FModuleManager::GetModulePtr<ISettingsModule>("Settings"))
	{
		// Create the new category
		ISettingsContainerPtr SettingsContainer = SettingsModule->GetContainer("Project");

		SettingsContainer->DescribeCategory("CARLASettings",
			LOCTEXT("RuntimeWDCategoryName", "CARLA Settings"),
			LOCTEXT("RuntimeWDCategoryDescription", "CARLA plugin settings"));

		// Register the settings
		ISettingsSectionPtr SettingsSection = SettingsModule->RegisterSettings("Project", "CARLASettings", "General",
			LOCTEXT("RuntimeGeneralSettingsName", "General"),
			LOCTEXT("RuntimeGeneralSettingsDescription", "General configuration for the CARLA plugin"),
			GetMutableDefault<UCarlaSettings>()
		);

		// Register the save handler to your settings, you might want to use it to
		// validate those or just act to settings changes.
		if (SettingsSection.IsValid())
		{
			SettingsSection->OnModified().BindRaw(this, &FCarlaModule::HandleSettingsSaved);
		}
	}
}

void FCarlaModule::UnregisterSettings()
{
	// Ensure to unregister all of your registered settings here, hot-reload would
	// otherwise yield unexpected results.

	if (ISettingsModule* SettingsModule = FModuleManager::GetModulePtr<ISettingsModule>("Settings"))
	{
		SettingsModule->UnregisterSettings("Project", "CustomSettings", "General");
	}
}

bool FCarlaModule::HandleSettingsSaved()
{
	UCarlaSettings* Settings = GetMutableDefault<UCarlaSettings>();
	bool ResaveSettings = false;

	// Put any validation code in here and resave the settings in case an invalid
	// value has been entered

	if (ResaveSettings)
	{
		Settings->SaveConfig();
	}

	return true;
}

#undef LOCTEXT_NAMESPACE

IMPLEMENT_MODULE(FCarlaModule, Carla)

// =============================================================================
// -- Implement carla throw_exception ------------------------------------------
// =============================================================================

#ifdef LIBCARLA_NO_EXCEPTIONS
#include <util/disable-ue4-macros.h>
#include <carla/Exception.h>
#include <util/enable-ue4-macros.h>

#include <exception>
namespace carla {

  void throw_exception(const std::exception &e) {
    UE_LOG(LogCarla, Fatal, TEXT("Exception thrown: %s"), UTF8_TO_TCHAR(e.what()));
    // It should never reach this part.
    std::terminate();
  }

} // namespace carla
#endif
