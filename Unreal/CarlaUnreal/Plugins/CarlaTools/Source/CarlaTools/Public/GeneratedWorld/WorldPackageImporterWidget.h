// Copyright (c) 2026 CARLA-Cesium digital-twin project.
//
// The human-facing half of the world package importer: a small editor panel with a folder, a world
// name, a button and a result line.
//
// The behaviour lives here rather than in the widget's graph. The Blueprint supplies only the layout,
// and its widgets are bound to the properties below by name, so what the panel does is reviewable and
// diffable as source instead of as nodes inside a binary asset. This matches how the digital-twin
// widget in this plugin is already put together.

#pragma once

#include <util/ue-header-guard-begin.h>
#include "CoreMinimal.h"
#include "EditorUtilityWidget.h"
#include <util/ue-header-guard-end.h>

#include "WorldPackageImporterWidget.generated.h"

class UButton;
class UEditableTextBox;
class UTextBlock;

UCLASS()
class CARLATOOLS_API UWorldPackageImporterWidget : public UEditorUtilityWidget
{
	GENERATED_BODY()

public:
	/** Content path the imported level and its assets are written under. */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Generated World")
	FString DestinationFolder = TEXT("/Game/Carla/Maps/Generated");

	/**
	 * Run the import described by the panel's fields and report the outcome in it.
	 *
	 * Exposed so the panel can be driven without a person clicking, which is how it is tested.
	 */
	UFUNCTION(BlueprintCallable, Category = "Generated World")
	FString ImportFromFields();

protected:
	virtual void NativeConstruct() override;

	/** Fill the world name with the first world in the folder, when the field is empty. */
	UFUNCTION(BlueprintCallable, Category = "Generated World")
	void SuggestFirstWorld();

private:
	UFUNCTION()
	void HandleImportClicked();

	/** Show a line in the panel and mirror it to the log, so a headless run leaves a trace. */
	void Report(const FString& Message, bool bIsFailure);

	// Bound by name to widgets of the same name in the Blueprint. A Blueprint missing any of these
	// fails to compile, which is the intended contract: the panel's layout has to provide them.
	UPROPERTY(meta = (BindWidget))
	TObjectPtr<UEditableTextBox> PackageDirectory;

	UPROPERTY(meta = (BindWidget))
	TObjectPtr<UEditableTextBox> MapName;

	UPROPERTY(meta = (BindWidget))
	TObjectPtr<UButton> ImportButton;

	UPROPERTY(meta = (BindWidget))
	TObjectPtr<UTextBlock> StatusText;
};
