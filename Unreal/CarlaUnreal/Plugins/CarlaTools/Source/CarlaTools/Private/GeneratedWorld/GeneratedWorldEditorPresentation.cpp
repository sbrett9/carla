// Copyright (c) 2026 CARLA-Cesium digital-twin project.

#include "GeneratedWorld/GeneratedWorldEditorPresentation.h"

#include "CarlaTools.h"
#include "CesiumHeightSampler.h"
#include "GeneratedWorld/GeoreferencedWorldInitializer.h"
#include "GeneratedWorld/GeoreferencedWorldSettings.h"

#include <util/ue-header-guard-begin.h>
#include "Editor.h"
#include "Engine/World.h"
#include "EngineUtils.h"
#include <util/ue-header-guard-end.h>

FDelegateHandle FGeneratedWorldEditorPresentation::MapOpenedHandle;

void FGeneratedWorldEditorPresentation::Register()
{
	if (!MapOpenedHandle.IsValid())
	{
		MapOpenedHandle = FEditorDelegates::OnMapOpened.AddStatic(
			&FGeneratedWorldEditorPresentation::OnMapOpened);
	}
}

void FGeneratedWorldEditorPresentation::Unregister()
{
	if (MapOpenedHandle.IsValid())
	{
		FEditorDelegates::OnMapOpened.Remove(MapOpenedHandle);
		MapOpenedHandle.Reset();
	}
}

void FGeneratedWorldEditorPresentation::OnMapOpened(const FString& /*Filename*/, bool /*bAsTemplate*/)
{
	if (!GEditor)
	{
		return;
	}
	ApplyToWorld(GEditor->GetEditorWorldContext().World());
}

int32 FGeneratedWorldEditorPresentation::ApplyToWorld(UWorld* World)
{
	if (!World)
	{
		return 0;
	}

	// The settings travel with the level, on the same actor that applies them when the world is
	// played. A map that has none is not a generated world and is left exactly as authored.
	UGeoreferencedWorldSettings* Settings = nullptr;
	for (TActorIterator<AGeoreferencedWorldInitializer> It(World); It; ++It)
	{
		if (IsValid(*It))
		{
			Settings = It->Settings.LoadSynchronous();
			if (Settings)
			{
				break;
			}
		}
	}
	if (!Settings)
	{
		return 0;
	}

	int32 Hidden = 0;
	for (const FGeoreferencedWorldLayer& Layer : Settings->Layers)
	{
		if (Layer.Tag.IsEmpty() || Layer.bVisible)
		{
			continue;
		}
		const int32 Changed = UCesiumHeightSampler::SetLayerHiddenInEditor(World, Layer.Tag, true);
		if (Changed > 0)
		{
			Hidden += Changed;
		}
	}

	if (Hidden > 0)
	{
		UE_LOG(LogCarlaTools, Display,
			TEXT("[GeneratedWorld] hid %d layer tileset(s) that the simulation does not draw. "
			     "Use the eye icon in the outliner to show them while editing."),
			Hidden);
	}
	return Hidden;
}
