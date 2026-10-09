// Copyright (c) 2026 CARLA-Cesium digital-twin project.
//
// What the server can say about whether a camera's photoreal tiles have arrived.
//
// A capture must not write its first frame while the camera's own tiles are still streaming, and
// only the server can see them stream. Two things have to hold. The camera must be among the views
// ACesiumSensorViewPublisher wrote to the Cesium camera manager on the last tick: until it is, no
// tileset selects tiles for its frustum, and a tileset reads fully loaded for a view it has never
// been given. And every tileset that is drawn must have finished loading what it selected, with no
// tile that failed: cesium-native never retries a Failed tile, draws it as empty and counts it as
// loaded, so a failure is a hole in the frame that LoadProgress reports as complete.
//
// Everything here is as of the end of the last world tick. The publisher ticks in TG_PrePhysics and
// every ACesium3DTileset in TG_PostUpdateWork, so the tilesets' last selection used the views the
// publisher wrote earlier in the same tick.
//
// LoadProgress, the load queues and the render list all belong to each tileset's default view
// group, which holds every registered view at once: the spectator, every published sensor, any
// other camera. While any of those views is moving the tileset is loading for it, and these figures
// cannot say which view is still waiting.
//
// This header carries no Cesium type, so the Carla module can include it without compiling
// cesium-native.

#pragma once

#include "CoreMinimal.h"

class AActor;

/** One tileset's load state after its last update. */
struct CESIUMCARLABRIDGE_API FCesiumTilesetReadiness
{
	/** The tileset's Cesium ion asset, or 0 when it is not streamed from ion. */
	int64 IonAssetId = 0;

	/**
	 * Drawn in game: the actor is not hidden. A hidden layer, such as the bare-earth ground that only
	 * supplies collision, still selects and streams tiles.
	 */
	bool bVisible = false;

	/**
	 * ACesium3DTileset::GetLoadProgress, 0 to 100, from the tileset's last tick. A tileset whose
	 * native tileset does not exist (destroyed, and not recreated until its next tick) has loaded
	 * nothing and reports 0 here, whatever its actor last recorded.
	 */
	float LoadProgress = 0.0f;

	/** Tiles the last update queued for loading on worker threads. */
	int32 WorkerThreadLoadQueueLength = 0;

	/** Tiles the last update queued for preparation on the main thread. */
	int32 MainThreadLoadQueueLength = 0;

	/**
	 * Tiles the last update selected and then replaced by an ancestor because something in their
	 * subtree could not yet be drawn.
	 */
	int32 TilesKicked = 0;

	/**
	 * Tiles the last update selected to draw whose load had failed (Failed or FailedTemporarily).
	 * Each is drawn as empty. Only a Failed tile can be counted here: a FailedTemporarily tile is not
	 * drawable, so it is re-queued and shows in the load queues instead.
	 */
	int32 FailedInView = 0;

	/**
	 * Tiles anywhere in the loaded tree whose load has failed (Failed or FailedTemporarily), in view
	 * or not. At least FailedInView; larger when a failure lies outside every current view or under
	 * a tile the last update refined past.
	 */
	int32 FailedLoaded = 0;
};

/** Whether one camera's view is published, and every tileset's load state, after the last tick. */
struct CESIUMCARLABRIDGE_API FCesiumViewReadiness
{
	/** The camera's scene capture was among the views the last tick wrote to the camera manager. */
	bool bPublished = false;

	/** One entry per ACesium3DTileset in the camera's world, hidden ones included. */
	TArray<FCesiumTilesetReadiness> Tilesets;

	/**
	 * Read Camera's readiness into OutReadiness. Returns false, with OutRefusal saying why, when the
	 * question has no answer that could ever become ready by waiting: the actor is gone, it carries
	 * no scene capture, its capture has no frustum Cesium accepts, or its world has tilesets but no
	 * sensor-view publisher to register the capture with them.
	 *
	 * A world with no tileset and no publisher answers true with no tilesets and bPublished false:
	 * there is nothing streaming to wait for, and whether that is acceptable is the caller's call.
	 *
	 * One side effect: when the camera was not published, its eligible captures are tracked by the
	 * publisher now, so the next tick publishes them instead of the next sweep. That is what the sweep
	 * would do within its interval, so it changes when the view starts driving tile selection, never
	 * whether it does. This call's answer still says not published.
	 */
	static bool Read(AActor* Camera, FCesiumViewReadiness& OutReadiness, FString& OutRefusal);
};
