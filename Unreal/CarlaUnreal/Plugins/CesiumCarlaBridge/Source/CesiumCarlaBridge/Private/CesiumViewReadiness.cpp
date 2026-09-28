// Copyright (c) 2026 CARLA-Cesium digital-twin project.

#include "CesiumViewReadiness.h"

#include "Cesium3DTileset.h"
#include "CesiumSensorViewPublisher.h"
#include "Components/SceneCaptureComponent2D.h"
#include "Engine/World.h"
#include "EngineUtils.h" // TActorIterator
#include "GameFramework/Actor.h" // TInlineComponentArray

#include <Cesium3DTilesSelection/LoadedTileEnumerator.h>
#include <Cesium3DTilesSelection/Tile.h>
#include <Cesium3DTilesSelection/Tileset.h>
#include <Cesium3DTilesSelection/TilesetViewGroup.h>
#include <Cesium3DTilesSelection/ViewUpdateResult.h>

namespace
{
	/**
	 * A load that ended in failure. A Failed tile is never retried; a FailedTemporarily one is loaded
	 * again the next time a view needs it.
	 */
	bool IsFailedTileLoad(Cesium3DTilesSelection::TileLoadState State)
	{
		return State == Cesium3DTilesSelection::TileLoadState::Failed ||
			State == Cesium3DTilesSelection::TileLoadState::FailedTemporarily;
	}

	FCesiumTilesetReadiness ReadTilesetReadiness(const ACesium3DTileset& TilesetActor)
	{
		FCesiumTilesetReadiness Row;
		Row.IonAssetId = TilesetActor.GetTilesetSource() == ETilesetSource::FromCesiumIon
			? TilesetActor.GetIonAssetID()
			: 0;
		Row.bVisible = !TilesetActor.IsHidden();

		const Cesium3DTilesSelection::Tileset* NativeTileset = TilesetActor.GetTileset();
		if (!NativeTileset)
		{
			// Destroyed and not yet recreated: the actor keeps the progress of the tileset it had, but
			// nothing is loaded now.
			Row.LoadProgress = 0.0f;
			return Row;
		}
		Row.LoadProgress = TilesetActor.GetLoadProgress();

		// ACesium3DTileset::Tick updates the default view group with every registered view, then
		// computes LoadProgress from the same group, so these are the figures behind that number.
		const Cesium3DTilesSelection::ViewUpdateResult& Result =
			NativeTileset->getDefaultViewGroup().getViewUpdateResult();
		Row.WorkerThreadLoadQueueLength = Result.workerThreadTileLoadQueueLength;
		Row.MainThreadLoadQueueLength = Result.mainThreadTileLoadQueueLength;
		Row.TilesKicked = static_cast<int32>(Result.tilesKicked);

		// A Failed tile counts as drawable, so the traversal selects it and draws nothing: every one in
		// the render list is a hole in some registered view.
		for (const Cesium3DTilesSelection::Tile::ConstPointer& RenderedTile : Result.tilesToRenderThisFrame)
		{
			if (RenderedTile && IsFailedTileLoad(RenderedTile->getState()))
			{
				++Row.FailedInView;
			}
		}

		// Every tile not Unloaded is enumerated, reached through its ancestors, which a referenced
		// descendant keeps referenced; so every tile in the render list is counted here as well. A tile
		// becomes Failed or FailedTemporarily only in a main-thread continuation, and Cesium dispatches
		// those on the game thread; this runs on the game thread between ticks, so no state changes
		// under it.
		for (const Cesium3DTilesSelection::Tile& LoadedTile : NativeTileset->loadedTiles())
		{
			if (IsFailedTileLoad(LoadedTile.getState()))
			{
				++Row.FailedLoaded;
			}
		}
		return Row;
	}
}

bool FCesiumViewReadiness::Read(AActor* Camera, FCesiumViewReadiness& OutReadiness, FString& OutRefusal)
{
	OutReadiness = FCesiumViewReadiness();
	OutRefusal.Reset();

	if (!IsValid(Camera))
	{
		OutRefusal = TEXT("the actor has no live Unreal actor, so it has no view");
		return false;
	}
	UWorld* World = Camera->GetWorld();
	if (!World)
	{
		OutRefusal = TEXT("the actor is in no world");
		return false;
	}

	// A camera, for this purpose, is whatever the publisher can register: an actor carrying a scene
	// capture with a frustum Cesium accepts. Anything else can never be published, so waiting on it
	// would only run into a ceiling.
	int32 CaptureCount = 0;
	int32 EligibleCount = 0;
	TInlineComponentArray<USceneCaptureComponent2D*> Captures(Camera);
	for (USceneCaptureComponent2D* Capture : Captures)
	{
		if (!IsValid(Capture))
		{
			continue;
		}
		++CaptureCount;
		if (ACesiumSensorViewPublisher::IsEligibleCapture(Capture))
		{
			++EligibleCount;
		}
	}
	if (CaptureCount == 0)
	{
		OutRefusal = TEXT("it is not a camera: it carries no scene capture");
		return false;
	}
	if (EligibleCount == 0)
	{
		OutRefusal = TEXT("its scene capture has no frustum Cesium accepts (it needs a perspective "
			"projection, a render target of at least one pixel and a positive field of view)");
		return false;
	}

	for (TActorIterator<ACesium3DTileset> It(World); It; ++It)
	{
		const ACesium3DTileset* TilesetActor = *It;
		if (IsValid(TilesetActor))
		{
			OutReadiness.Tilesets.Add(ReadTilesetReadiness(*TilesetActor));
		}
	}

	ACesiumSensorViewPublisher* Publisher = ACesiumSensorViewPublisher::Find(World);
	if (!Publisher)
	{
		if (OutReadiness.Tilesets.Num() > 0)
		{
			OutRefusal = TEXT("the world has Cesium tilesets but no sensor-view publisher, so no "
				"camera's view drives their tile selection");
			OutReadiness = FCesiumViewReadiness();
			return false;
		}
		return true;
	}

	OutReadiness.bPublished = Publisher->IsPublished(Camera);
	if (!OutReadiness.bPublished)
	{
		Publisher->TrackCapturesOf(Camera);
	}
	return true;
}
