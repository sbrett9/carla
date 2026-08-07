// Copyright (c) 2026 CARLA-Cesium digital-twin project.
//
// ACesiumSensorViewPublisher makes CARLA's camera sensors drive Cesium tile selection.
//
// Cesium picks and refines tiles from a list of registered views. It gathers those from player
// cameras, editor viewports, and scene captures — but its scene-capture pass enumerates
// ASceneCapture2D *actors*, and a CARLA camera sensor is an ASensor that merely OWNS a
// USceneCaptureComponent2D. It therefore never appears in that list, and with no view of its own
// registered, tiles were selected entirely from the spectator's pose and the game viewport's size.
//
// Two consequences, both fixed by publishing the sensors' real views:
//   * Culling used the game viewport's ASPECT RATIO to derive a vertical field of view. Any sensor
//     taller than that aspect (e.g. 1920x1280 against a 16:9 viewport) rendered bands at the top and
//     bottom of frame that lay outside the culling frustum, so tiles there were never requested and
//     drew as holes. Culled subtrees are not visited at all, so no request is issued and no amount of
//     request-cache sizing can recover them.
//   * Screen-space error used the game viewport's PIXEL COUNT, so tile detail was chosen for a 720p
//     view no matter how large the sensor actually was, under-refining every higher resolution.
//
// Discovery is by COMPONENT rather than by actor class, which is both the correct generalisation of
// the gap above and a hard requirement here: the Carla module already depends on CesiumCarlaBridge,
// so this plugin cannot depend on Carla and cannot name ASceneCaptureSensor. Every camera-family
// sensor (colour, depth, semantic segmentation, optical flow, DVS) carries a
// USceneCaptureComponent2D, so all of them qualify by construction with no type list to maintain.
//
// All of them SHOULD qualify. The depth camera in particular measures how much of each vehicle the
// photoreal tiles occlude, so that occluded vehicles are not trained on as cleanly visible ones
// (Docs/CAT_Research/Findings/17_Photoreal_Occlusion_Metric.md). A tile culled in the depth camera's
// frustum reads as empty space, which silently scores an occluded vehicle as visible. Registering
// views is monotonic — a tile survives if it is visible in ANY registered view, and refinement
// follows the largest screen-space error across views — so an extra view can only add geometry and
// detail, never remove either. Over-registering costs bandwidth; under-registering corrupts truth
// data invisibly.

#pragma once

#include "CoreMinimal.h"
#include "GameFramework/Actor.h"
#include "CesiumSensorViewPublisher.generated.h"

class USceneCaptureComponent2D;

UCLASS()
class CESIUMCARLABRIDGE_API ACesiumSensorViewPublisher : public AActor
{
	GENERATED_BODY()

public:
	ACesiumSensorViewPublisher();

	virtual void Tick(float DeltaSeconds) override;

	/**
	 * Find the publisher in this world, or spawn one. Called by ConfigureCesiumForOrigin so the
	 * publisher exists for the lifetime of the procedurally built level: a later client that attaches
	 * to that world without rebuilding it (SCTMV's --no-build) inherits the running publisher and its
	 * cameras are picked up by the next rescan, at whatever resolution that client asked for.
	 */
	static ACesiumSensorViewPublisher* FindOrSpawn(UWorld* World);

	/**
	 * Seconds between full sweeps for scene-capture components. Only the sweep is proportional to the
	 * actor count; the per-tick republish is proportional to the number of cameras. Keeping the sweep
	 * off the tick matters because under synchronous ticking the frame recorder and the telemetry
	 * emitter run inline between world ticks, so anything added to the tick competes with them
	 * directly. A sensor spawned mid-run starts driving tiles within this interval, which is far
	 * shorter than the time its tiles take to stream in any case.
	 */
	UPROPERTY()
	double RescanIntervalSeconds = 0.5;

private:
	/** Rebuild TrackedCaptures by sweeping the world for eligible scene-capture components. */
	void RescanCaptures(UWorld* World);

	/** Rewrite the camera manager's view list from the tracked captures' current transforms. */
	void PublishViews(UWorld* World);

	/**
	 * Weak so that a destroyed sensor drops out on the next republish without any bookkeeping: an
	 * unresolvable entry is skipped, the rebuilt list omits it, and its frustum stops pinning tiles.
	 */
	UPROPERTY()
	TArray<TWeakObjectPtr<USceneCaptureComponent2D>> TrackedCaptures;

	/** Seconds accumulated since the last sweep; compared against RescanIntervalSeconds. */
	double SecondsSinceRescan = 0.0;

	/** Logs the published view set once, and again whenever its shape changes. */
	int32 LastPublishedCount = -1;
};
