// Copyright (c) 2026 CARLA-Cesium digital-twin project.
//
// Presents a generated world the way the simulation does when its level is opened for editing.
//
// A generated world hides layers that exist to be measured rather than looked at -- above all the
// bare-earth ground layer, which occupies the same space as the photoreal imagery and would hide the
// surface being edited. The simulation hides them at BeginPlay, but that flag governs rendering in
// game only: a level opened in the editor draws every layer regardless, so the editor shows something
// the simulation never does.
//
// The editor's own visibility flag lasts for the session rather than being saved with the level, so
// this applies the hiding each time a level is opened rather than once when it is imported. Hiding
// through that flag is deliberate: it is the one the eye icon in the outliner drives, so a layer
// hidden here can always be revealed again while editing.

#pragma once

#include <util/ue-header-guard-begin.h>
#include "CoreMinimal.h"
#include <util/ue-header-guard-end.h>

class FGeneratedWorldEditorPresentation
{
public:
	/** Begin applying layer presentation whenever a level is opened. */
	static void Register();

	/** Stop doing so. */
	static void Unregister();

	/**
	 * Apply the layer presentation described by the world's settings to the editor viewport now.
	 *
	 * Does nothing to a world that carries no generated-world settings, so opening a hand-authored
	 * map is unaffected. Returns the number of layers hidden.
	 */
	static int32 ApplyToWorld(UWorld* World);

private:
	static void OnMapOpened(const FString& Filename, bool bAsTemplate);

	static FDelegateHandle MapOpenedHandle;
};
