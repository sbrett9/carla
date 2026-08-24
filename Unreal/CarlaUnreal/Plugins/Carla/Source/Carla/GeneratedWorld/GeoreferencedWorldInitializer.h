// Copyright (c) 2026 CARLA-Cesium digital-twin project.
//
// Applies a generated world's settings when the level stands on its own.
//
// A world assembled by a client reaches its correct state through a series of remote calls made after
// the map loads. A level loaded by name has no client to make them. This actor replays the same calls
// from level content, in the order they have to happen, so opening a generated world directly gives
// the same datum, the same layer arrangement, the same collision surface and the same bare-earth
// truth as generating it would have.
//
// Placed in the level next to the settings asset it applies. Harmless in a world that has no
// settings: it reports that there is nothing to apply and leaves the world untouched.

#pragma once

#include "CoreMinimal.h"
#include "GameFramework/Actor.h"
#include "GeneratedWorld/GeoreferencedWorldSettings.h"
#include "GeoreferencedWorldInitializer.generated.h"

UCLASS()
class CARLA_API AGeoreferencedWorldInitializer : public AActor
{
	GENERATED_BODY()

public:
	AGeoreferencedWorldInitializer();

	/** The world description to apply. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Generated World")
	TSoftObjectPtr<UGeoreferencedWorldSettings> Settings;

	/**
	 * Apply the settings to this actor's world now.
	 *
	 * Idempotent: every operation it performs replaces rather than accumulates, so applying twice
	 * leaves the same world as applying once. Returns false when there is nothing to apply, or when
	 * the settings carry no usable datum.
	 */
	UFUNCTION(BlueprintCallable, Category = "Generated World")
	bool ApplyWorldSettings();

	/** True once the settings have been applied to this world. */
	UFUNCTION(BlueprintPure, Category = "Generated World")
	bool HasApplied() const { return bApplied; }

protected:
	virtual void BeginPlay() override;

private:
	/**
	 * The Cesium ion access token is a credential, so it is never stored in level content. It is read
	 * from the environment at apply time, matching how a client supplies it.
	 */
	static FString IonAccessTokenFromEnvironment();

	/** Set on a successful apply, so a second BeginPlay in the same world does not redo the work. */
	UPROPERTY(Transient)
	bool bApplied = false;
};
