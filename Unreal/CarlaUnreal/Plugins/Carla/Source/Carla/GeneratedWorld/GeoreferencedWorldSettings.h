// Copyright (c) 2026 CARLA-Cesium digital-twin project.
//
// What a world generated from OpenStreetMap needs in order to be correct, stored as level content.
//
// A generated world is normally assembled by a client issuing a series of remote calls after the map
// loads: it configures the georeference, offsets and toggles the imagery layers, builds the draped
// collision surface, records the sandbox, and publishes how to recover bare-earth height. None of
// that is level content, so a world saved as a level would reload as geometry with no datum: it would
// look right, drive nearly right, and report altitude that is wrong by the amount the surface was
// shifted to sit on the imagery.
//
// These assets carry the same information as a world package on disk, in a form the engine can cook
// and load. AGeoreferencedWorldInitializer applies them, so a level standing on its own reaches the
// same state a client would have put it in.

#pragma once

#include "CoreMinimal.h"
#include "Engine/DataAsset.h"
#include "GeoreferencedWorldSettings.generated.h"

/** One streamed imagery layer: which asset it draws from, and how it is presented. */
USTRUCT(BlueprintType)
struct CARLA_API FGeoreferencedWorldLayer
{
	GENERATED_BODY()

	/** Layer tag, as used by the per-layer visibility, collision and offset operations. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Layer")
	FString Tag;

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Layer")
	bool bVisible = true;

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Layer")
	bool bCollision = false;

	/**
	 * Signed vertical shift in metres applied to this layer's tiles without moving the truth datum.
	 * Used to drop the collidable bare-earth layer onto a road surface that was raised to meet the
	 * imagery, so vehicles do not float on the road or fall through off it.
	 */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Layer")
	double VerticalOffsetMeters = 0.0;
};

/**
 * The parameters the road surface was generated with.
 *
 * Recorded because a world that regenerates its roads on load must reproduce the surface it had, not
 * the library defaults: those differ enough (walls, ten times as many pieces) to change how the map
 * drives and looks.
 */
USTRUCT(BlueprintType)
struct CARLA_API FGeneratedRoadMeshParameters
{
	GENERATED_BODY()

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Road Mesh")
	double VertexDistance = 2.0;

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Road Mesh")
	double MaxRoadLength = 500.0;

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Road Mesh")
	double WallHeight = 0.0;

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Road Mesh")
	double AdditionalWidth = 0.0;

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Road Mesh")
	bool bSmoothJunctions = true;

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Road Mesh")
	bool bEnableMeshVisibility = true;

	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Road Mesh")
	bool bEnablePedestrianNavigation = true;
};

/**
 * The per-cell fields that relate the surface vehicles drive on to the bare earth beneath it.
 *
 * Row-major [row * NumCols + col]; cell (0,0) sits at world (MinXMeters, MinYMeters), +col is +X,
 * +row is +Y, spacing CellSizeMeters. Held separately from the settings asset because it is bulk
 * data - hundreds of thousands of cells - and because a world reconciled by a single constant shift
 * has none of it.
 */
UCLASS(BlueprintType)
class CARLA_API UBareEarthOffsetField : public UDataAsset
{
	GENERATED_BODY()

public:
	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Grid")
	double MinXMeters = 0.0;

	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Grid")
	double MinYMeters = 0.0;

	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Grid")
	double CellSizeMeters = 0.0;

	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Grid")
	int32 NumCols = 0;

	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Grid")
	int32 NumRows = 0;

	// Not EditAnywhere: these run to hundreds of thousands of entries, and a details panel listing
	// them one per row is unusable and slow. They are still saved and cooked.

	/** Driven-surface height minus bare-earth height, metres. Subtract it to recover truth. */
	UPROPERTY()
	TArray<float> OffsetMeters;

	/** Bare-earth ground height, ellipsoidal metres. */
	UPROPERTY()
	TArray<float> BareEarthDtmMeters;

	int32 NodeCount() const { return NumCols * NumRows; }

	/** True when the grid is dimensionally sound and both fields are fully populated. */
	bool IsWellFormed() const
	{
		return NumCols >= 2 && NumRows >= 2 && CellSizeMeters > 0.0
			&& OffsetMeters.Num() == NodeCount() && BareEarthDtmMeters.Num() == NodeCount();
	}
};

/**
 * Everything about a generated world that is not geometry: where it is on the Earth, how its surface
 * was reconciled with the imagery, which layers it streams, and how large its sandbox is.
 */
UCLASS(BlueprintType)
class CARLA_API UGeoreferencedWorldSettings : public UDataAsset
{
	GENERATED_BODY()

public:
	// ── Datum ────────────────────────────────────────────────────────────────
	// World (0,0) is pinned to this latitude and longitude, and local Z 0 is this height.

	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Datum")
	double OriginLatitude = 0.0;

	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Datum")
	double OriginLongitude = 0.0;

	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Datum")
	double OriginHeightMeters = 0.0;

	/** The road network's geoReference projection string, verbatim, so it is never re-derived. */
	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Datum")
	FString GeoReferenceString;

	// ── Surface reconciliation ───────────────────────────────────────────────

	/** How the drivable surface was matched to the imagery, for the record. */
	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Surface")
	FString HeightAlignMode;

	/** True when OffsetField is authoritative and the constant below does not apply. */
	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Surface")
	bool bDrapeActive = false;

	/** Constant surface shift, metres. Zero when the surface was left on bare earth. */
	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Surface")
	double HeightAlignOffsetMeters = 0.0;

	/** The per-cell fields, when this world was reconciled point by point. */
	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Surface")
	TSoftObjectPtr<UBareEarthOffsetField> OffsetField;

	// ── Streamed layers ──────────────────────────────────────────────────────

	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Layers")
	int64 PhotorealIonAssetId = 0;

	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Layers")
	int64 GroundIonAssetId = 0;

	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Layers")
	TArray<FGeoreferencedWorldLayer> Layers;

	// ── Sandbox ──────────────────────────────────────────────────────────────

	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Sandbox")
	double StagingMinXMeters = 0.0;

	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Sandbox")
	double StagingMinYMeters = 0.0;

	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Sandbox")
	double StagingMaxXMeters = 0.0;

	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Sandbox")
	double StagingMaxYMeters = 0.0;

	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Sandbox")
	double StagingMarginMeters = 0.0;

	// ── Road generation ──────────────────────────────────────────────────────

	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Road Mesh")
	FGeneratedRoadMeshParameters RoadMeshParameters;

	// ── Provenance ───────────────────────────────────────────────────────────
	// Never read to configure anything; read by whoever asks how this world came to be.

	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Provenance")
	FString SourceOsmFileName;

	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Provenance")
	FString SourceOsmSha256;

	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Provenance")
	FString OpenDriveSha256;

	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Provenance")
	FString GeneratedAtUtc;

	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Provenance")
	FString GeneratorVersion;

	/** True when the datum is usable: a sandbox rectangle alone is not enough to place a world. */
	bool HasUsableDatum() const
	{
		return OriginLatitude != 0.0 || OriginLongitude != 0.0 || OriginHeightMeters != 0.0;
	}
};
