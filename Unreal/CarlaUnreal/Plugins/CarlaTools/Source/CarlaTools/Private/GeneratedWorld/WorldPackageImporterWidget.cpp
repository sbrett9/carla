// Copyright (c) 2026 CARLA-Cesium digital-twin project.

#include "GeneratedWorld/WorldPackageImporterWidget.h"

#include "CarlaTools.h"
#include "GeneratedWorld/WorldPackageImporter.h"

#include <util/ue-header-guard-begin.h>
#include "Blueprint/WidgetTree.h"
#include "Editor.h"
#include "LevelEditorSubsystem.h"
#include "TimerManager.h"
#include "Components/Button.h"
#include "Components/ButtonSlot.h"
#include "Components/CanvasPanel.h"
#include "Components/CanvasPanelSlot.h"
#include "Components/EditableTextBox.h"
#include "Components/TextBlock.h"
#include "Components/VerticalBox.h"
#include "Components/VerticalBoxSlot.h"
#include "Misc/Paths.h"
#include "Styling/SlateColor.h"
#include "Styling/SlateTypes.h"
#include <util/ue-header-guard-end.h>

namespace
{
	/** Colours for the import button, in the order normal, hovered, pressed. */
	const FLinearColor ImportBlue(0.06f, 0.32f, 0.68f);
	const FLinearColor ImportBlueHovered(0.10f, 0.42f, 0.85f);
	const FLinearColor ImportBluePressed(0.04f, 0.24f, 0.52f);

	/** Corner radius giving the button a chamfered square edge rather than a pill. */
	constexpr float ImportButtonCornerRadius = 3.0f;

	/** Breathing room between the panel's edge and its contents. */
	constexpr float PanelMargin = 6.0f;
}

void UWorldPackageImporterWidget::NativeConstruct()
{
	Super::NativeConstruct();

	if (ImportButton)
	{
		// Rebind rather than add: the panel can be constructed more than once in a session, and a
		// second binding would run the import twice per click.
		ImportButton->OnClicked.RemoveDynamic(this, &UWorldPackageImporterWidget::HandleImportClicked);
		ImportButton->OnClicked.AddDynamic(this, &UWorldPackageImporterWidget::HandleImportClicked);
	}
	ApplyLayoutAndStyle();
	SuggestFirstWorld();
}

void UWorldPackageImporterWidget::ApplyLayoutAndStyle()
{
	// Inset the whole panel from the window edge, and let it grow with the window.
	if (Layout)
	{
		if (UCanvasPanelSlot* CanvasSlot = Cast<UCanvasPanelSlot>(Layout->Slot))
		{
			CanvasSlot->SetAnchors(FAnchors(0.0f, 0.0f, 1.0f, 1.0f));
			CanvasSlot->SetOffsets(FMargin(PanelMargin));
			CanvasSlot->SetAlignment(FVector2D::ZeroVector);
		}
	}

	// Say what the panel is for. Built here and inserted at the top, so the layout asset does not
	// have to carry it and the wording stays in source.
	if (Layout && WidgetTree && !Description)
	{
		Description = WidgetTree->ConstructWidget<UTextBlock>(
			UTextBlock::StaticClass(), TEXT("Description"));
		if (Description)
		{
			Description->SetText(FText::FromString(
				TEXT("Turns a generated world into a level you can open and edit.\n\n"
				     "A build run with --emit-world-package writes a folder describing the world it "
				     "made: the road network, the grids that relate the driven surface to true "
				     "ground, and where on the Earth it sits. Importing one produces a level plus "
				     "its settings assets, which configure themselves when the level is loaded - no "
				     "client needed.\n\n"
				     "Re-importing the same world replaces what a previous import produced.")));
			Description->SetAutoWrapText(true);
			// A text block built in code carries the class default font, which is far larger than the
			// field labels placed in the layout asset. Match those instead, so the explanation reads as
			// supporting text rather than a headline, and soften it so the fields stay dominant.
			FSlateFontInfo DescriptionFont = Description->GetFont();
			DescriptionFont.Size = 10;
			Description->SetFont(DescriptionFont);
			Description->SetColorAndOpacity(FSlateColor(FLinearColor(0.72f, 0.72f, 0.72f, 1.0f)));
			Layout->AddChildToVerticalBox(Description);
			// AddChild appends, and the explanation belongs before the fields it explains.
			Layout->ShiftChild(0, Description);
		}
	}

	// Let every row span the panel, so the paths in the fields are readable, and give the rows a
	// little vertical separation.
	const TArray<UWidget*> Rows = Layout ? Layout->GetAllChildren() : TArray<UWidget*>();
	for (UWidget* Row : Rows)
	{
		if (UVerticalBoxSlot* RowSlot = Cast<UVerticalBoxSlot>(Row->Slot))
		{
			RowSlot->SetHorizontalAlignment(HAlign_Fill);
			RowSlot->SetPadding(FMargin(0.0f, 0.0f, 0.0f, 4.0f));
		}
	}

	// The button is the one action here, so it reads as one: a compact blue control rather than a
	// full-width bar, with the detail in a tooltip instead of on the face.
	if (ImportButton)
	{
		FButtonStyle Style = ImportButton->GetStyle();
		Style.SetNormal(FSlateRoundedBoxBrush(ImportBlue, ImportButtonCornerRadius));
		Style.SetHovered(FSlateRoundedBoxBrush(ImportBlueHovered, ImportButtonCornerRadius));
		Style.SetPressed(FSlateRoundedBoxBrush(ImportBluePressed, ImportButtonCornerRadius));
		ImportButton->SetStyle(Style);
		ImportButton->SetToolTipText(FText::FromString(
			TEXT("Read the named world from the folder above and write it out as a level, together "
			     "with the assets describing its origin, its imagery layers and its ground, and the "
			     "road network placed where the simulator looks for it.\n\n"
			     "The level is written but not opened. Importing over the level you currently have "
			     "open is refused.")));

		if (UVerticalBoxSlot* ButtonSlot = Cast<UVerticalBoxSlot>(ImportButton->Slot))
		{
			ButtonSlot->SetHorizontalAlignment(HAlign_Left);
			ButtonSlot->SetPadding(FMargin(0.0f, 6.0f, 0.0f, 8.0f));
		}
	}

	if (ImportButtonLabel)
	{
		ImportButtonLabel->SetText(FText::FromString(TEXT("Import")));
		if (UPanelSlot* LabelSlot = ImportButtonLabel->Slot)
		{
			if (UButtonSlot* ButtonContentSlot = Cast<UButtonSlot>(LabelSlot))
			{
				ButtonContentSlot->SetPadding(FMargin(18.0f, 5.0f));
			}
		}
	}
}

void UWorldPackageImporterWidget::SuggestFirstWorld()
{
	if (!PackageDirectory || !MapName || !MapName->GetText().IsEmpty())
	{
		return;
	}
	const FString Directory = PackageDirectory->GetText().ToString().TrimStartAndEnd();
	if (Directory.IsEmpty())
	{
		return;
	}
	const TArray<FString> Worlds = UWorldPackageImporter::ListWorldPackages(Directory);
	if (Worlds.Num() > 0)
	{
		MapName->SetText(FText::FromString(Worlds[0]));
		Report(FString::Printf(TEXT("%d world(s) in this folder."), Worlds.Num()), false);
	}
	else
	{
		Report(TEXT("No worlds in this folder. Build one with --emit-world-package first."), false);
	}
}

void UWorldPackageImporterWidget::HandleImportClicked()
{
	ImportFromFields();
}

FString UWorldPackageImporterWidget::ImportFromFields()
{
	if (!PackageDirectory || !MapName)
	{
		const FString Message = TEXT("The panel is missing its input fields.");
		Report(Message, true);
		return Message;
	}

	const FString Directory = PackageDirectory->GetText().ToString().TrimStartAndEnd();
	const FString World = MapName->GetText().ToString().TrimStartAndEnd();
	if (Directory.IsEmpty() || World.IsEmpty())
	{
		const FString Message = TEXT("Give both a world package folder and a world name.");
		Report(Message, true);
		return Message;
	}
	if (!UWorldPackageImporter::IsWorldPackagePresent(Directory, World))
	{
		const FString Message = FString::Printf(
			TEXT("No world called '%s' in that folder."), *World);
		Report(Message, true);
		return Message;
	}

	// Say what is about to be replaced, if anything, so the outcome is not a surprise.
	const FString Existing = UWorldPackageImporter::DescribeExistingImport(World, DestinationFolder);
	Report(Existing.IsEmpty()
		? FString::Printf(TEXT("Importing %s..."), *World)
		: FString::Printf(TEXT("Importing %s, replacing the level already there (built from '%s')..."),
			*World, *Existing), false);

	const FWorldPackageImportResult Result = UWorldPackageImporter::ImportWorldPackage(
		Directory, World, DestinationFolder, bReplaceLevelBuiltFromADifferentSource);
	if (!Result.bSucceeded)
	{
		const FString Message = FString::Printf(TEXT("Import failed: %s"), *Result.FailureReason);
		Report(Message, true);
		return Message;
	}

	const FString Message = FString::Printf(
		TEXT("Imported to %s. Open it, or load it by name with -map=%s"),
		*Result.LevelPackageName, *Result.LevelPackageName);
	Report(Message, false);

	if (bOpenLevelAfterImport)
	{
		// Deferred to a later tick, and capturing only the path: opening a level tears down the world
		// this panel lives in, and doing that while the click handler is still on the stack is what
		// makes it fatal rather than merely disruptive.
		const FString LevelToOpen = Result.LevelPackageName;
		if (GEditor)
		{
			GEditor->GetTimerManager()->SetTimerForNextTick(FTimerDelegate::CreateLambda(
				[LevelToOpen]()
				{
					if (ULevelEditorSubsystem* LevelEditor =
							GEditor->GetEditorSubsystem<ULevelEditorSubsystem>())
					{
						LevelEditor->LoadLevel(LevelToOpen);
					}
				}));
		}
	}
	return Message;
}

void UWorldPackageImporterWidget::Report(const FString& Message, bool bIsFailure)
{
	if (StatusText)
	{
		StatusText->SetText(FText::FromString(Message));
		StatusText->SetColorAndOpacity(bIsFailure
			? FSlateColor(FLinearColor(1.0f, 0.4f, 0.4f))
			: FSlateColor(FLinearColor::White));
	}
	if (bIsFailure)
	{
		UE_LOG(LogCarlaTools, Warning, TEXT("[WorldPackageImporter] %s"), *Message);
	}
	else
	{
		UE_LOG(LogCarlaTools, Display, TEXT("[WorldPackageImporter] %s"), *Message);
	}
}
