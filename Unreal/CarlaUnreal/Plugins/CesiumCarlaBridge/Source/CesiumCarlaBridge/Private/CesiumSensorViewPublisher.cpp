// Copyright (c) 2026 CARLA-Cesium digital-twin project.

#include "CesiumSensorViewPublisher.h"

#include "Camera/CameraTypes.h" // ECameraProjectionMode
#include "CesiumCamera.h"
#include "CesiumCameraManager.h"
#include "Components/SceneCaptureComponent2D.h"
#include "Engine/SceneCapture2D.h"
#include "Engine/TextureRenderTarget2D.h"
#include "Engine/World.h"
#include "EngineUtils.h" // TActorIterator
#include "GameFramework/Actor.h" // TInlineComponentArray

ACesiumSensorViewPublisher::ACesiumSensorViewPublisher()
{
	PrimaryActorTick.bCanEverTick = true;
	PrimaryActorTick.bStartWithTickEnabled = true;
}

ACesiumSensorViewPublisher* ACesiumSensorViewPublisher::FindOrSpawn(UWorld* World)
{
	if (!World)
	{
		return nullptr;
	}
	for (TActorIterator<ACesiumSensorViewPublisher> It(World); It; ++It)
	{
		if (IsValid(*It))
		{
			return *It;
		}
	}
	FActorSpawnParameters Params;
	Params.SpawnCollisionHandlingOverride = ESpawnActorCollisionHandlingMethod::AlwaysSpawn;
	return World->SpawnActor<ACesiumSensorViewPublisher>(Params);
}

void ACesiumSensorViewPublisher::RescanCaptures(UWorld* World)
{
	TrackedCaptures.Reset();
	if (!World)
	{
		return;
	}

	for (TActorIterator<AActor> It(World); It; ++It)
	{
		AActor* Actor = *It;
		if (!IsValid(Actor))
		{
			continue;
		}

		// Cesium already gathers scene captures that live on an ASceneCapture2D actor, so publishing
		// those again would register the same view twice.
		if (Actor->IsA<ASceneCapture2D>())
		{
			continue;
		}

		TInlineComponentArray<USceneCaptureComponent2D*> Captures(Actor);
		for (USceneCaptureComponent2D* Capture : Captures)
		{
			if (!IsValid(Capture))
			{
				continue;
			}
			// The same eligibility tests Cesium applies to the scene captures it does find. A
			// non-perspective or target-less capture has no frustum worth selecting tiles for, and a
			// non-positive field of view would be rejected by the camera manager anyway.
			if (Capture->ProjectionType != ECameraProjectionMode::Type::Perspective)
			{
				continue;
			}
			UTextureRenderTarget2D* RenderTarget = Capture->TextureTarget;
			if (!IsValid(RenderTarget) || RenderTarget->SizeX < 1 || RenderTarget->SizeY < 1)
			{
				continue;
			}
			if (Capture->FOVAngle <= 0.0f)
			{
				continue;
			}
			TrackedCaptures.Emplace(Capture);
		}
	}
}

void ACesiumSensorViewPublisher::PublishViews(UWorld* World)
{
	ACesiumCameraManager* CameraManager = ACesiumCameraManager::GetDefaultCameraManager(World);
	if (!CameraManager)
	{
		return;
	}

	// The publisher owns this list outright: it is rewritten from the live sensor set every tick, so
	// a sensor destroyed since the last sweep simply is not re-added and stops holding its tiles
	// resident. Nothing else in the project writes AdditionalCameras.
	CameraManager->AdditionalCameras.Reset();

	for (const TWeakObjectPtr<USceneCaptureComponent2D>& Tracked : TrackedCaptures)
	{
		USceneCaptureComponent2D* Capture = Tracked.Get();
		if (!IsValid(Capture))
		{
			continue;
		}
		UTextureRenderTarget2D* RenderTarget = Capture->TextureTarget;
		if (!IsValid(RenderTarget) || RenderTarget->SizeX < 1 || RenderTarget->SizeY < 1)
		{
			continue;
		}

		// Cesium derives the vertical field of view from ViewportSize's aspect ratio and the screen
		// space error from its pixel count, so this must be the sensor's own render target rather
		// than any window size. FOVAngle is the horizontal field of view, which is what
		// FCesiumCamera expects.
		CameraManager->AdditionalCameras.Emplace(
			FVector2D(static_cast<double>(RenderTarget->SizeX), static_cast<double>(RenderTarget->SizeY)),
			Capture->GetComponentLocation(),
			Capture->GetComponentRotation(),
			static_cast<double>(Capture->FOVAngle));
	}

	const int32 PublishedCount = CameraManager->AdditionalCameras.Num();
	if (PublishedCount != LastPublishedCount)
	{
		LastPublishedCount = PublishedCount;
		UE_LOG(LogTemp, Display,
			TEXT("[CesiumCarlaBridge] Publishing %d sensor view(s) to the Cesium camera manager."),
			PublishedCount);
	}
}

void ACesiumSensorViewPublisher::Tick(float DeltaSeconds)
{
	Super::Tick(DeltaSeconds);

	UWorld* World = GetWorld();
	if (!World)
	{
		return;
	}

	SecondsSinceRescan += static_cast<double>(DeltaSeconds);
	if (LastPublishedCount < 0 || SecondsSinceRescan >= RescanIntervalSeconds)
	{
		SecondsSinceRescan = 0.0;
		RescanCaptures(World);
	}

	// Republish every tick regardless of the sweep cadence: the sensors move continuously (the EO
	// observer's orbit re-aims the camera every frame) and tiles must be selected for where the
	// camera is now, not for where it was at the last sweep.
	PublishViews(World);
}
