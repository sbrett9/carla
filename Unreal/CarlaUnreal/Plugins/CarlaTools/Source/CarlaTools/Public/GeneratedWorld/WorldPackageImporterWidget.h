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
	 * Allow replacing a level that was built from a different source extract.
	 *
	 * Off by default. Re-importing the same area always proceeds; this only governs the case where
	 * the existing level came from different source data, where replacing it would discard that
	 * level and any editing done to it.
	 */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Generated World")
	bool bReplaceLevelBuiltFromADifferentSource = false;

	/**
	 * Open the level once it has been imported.
	 *
	 * Off by default, and not merely as a preference: opening a level closes the world this panel
	 * belongs to, which destroys the panel. It is done on a later tick so that does not happen while
	 * the click that started it is still running, but the panel will still disappear when the level
	 * changes. Leave it off to keep the panel open and load the level yourself.
	 */
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Generated World")
	bool bOpenLevelAfterImport = false;

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

	/**
	 * Lay the panel out and style it.
	 *
	 * Done here rather than in the Blueprint because the layout properties that matter -- slot
	 * padding, fill alignment, the panel margin -- live on slots, which the editor's scripting
	 * surface does not expose. Keeping them in code also means the panel's appearance is reviewable
	 * as source rather than buried in a binary asset.
	 */
	void ApplyLayoutAndStyle();

	// Bound by name to widgets of the same name in the Blueprint. A Blueprint missing any of these
	// fails to compile, which is the intended contract: the panel's layout has to provide them.
	UPROPERTY(meta = (BindWidget))
	TObjectPtr<class UCanvasPanel> RootPanel;

	UPROPERTY(meta = (BindWidget))
	TObjectPtr<class UVerticalBox> Layout;

	/** Explains what the panel does. Created here, so the layout does not have to supply it. */
	UPROPERTY(Transient)
	TObjectPtr<UTextBlock> Description;

	UPROPERTY(meta = (BindWidget))
	TObjectPtr<UEditableTextBox> PackageDirectory;

	UPROPERTY(meta = (BindWidget))
	TObjectPtr<UEditableTextBox> MapName;

	/**
	 * Optional Cesium ion token to write into the level's imagery layers.
	 *
	 * Created here rather than bound to the layout, so a panel authored before this field existed
	 * still compiles and still works.
	 */
	UPROPERTY(Transient)
	TObjectPtr<UEditableTextBox> IonAccessToken;

	UPROPERTY(Transient)
	TObjectPtr<UTextBlock> IonAccessTokenLabel;

	UPROPERTY(meta = (BindWidget))
	TObjectPtr<UButton> ImportButton;

	UPROPERTY(meta = (BindWidget))
	TObjectPtr<UTextBlock> ImportButtonLabel;

	UPROPERTY(meta = (BindWidget))
	TObjectPtr<UTextBlock> StatusText;
};
