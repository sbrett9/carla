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
#include "Framework/Application/SlateApplication.h"
#include "IDesktopPlatform.h"
#include "DesktopPlatformModule.h"
#include "Components/CheckBox.h"
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
				     "A build run with --emit-world-package writes one .cwp file per world: the road "
				     "network, the grids that relate the driven surface to true ground, and where on the "
				     "Earth it sits. Importing one produces a level plus its settings assets, which "
				     "configure themselves when the level is loaded - no client needed.\n\n"
				     "Re-importing the same world replaces what a previous import produced. A package "
				     "built from different source data is refused unless you allow it below.")));
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

	// A world is one file, so the panel asks for one. The folder-and-name fields an older layout may
	// still carry are hidden rather than removed, so that layout keeps compiling.
	for (UWidget* Legacy : { static_cast<UWidget*>(PackageDirectory), static_cast<UWidget*>(MapName) })
	{
		if (Legacy)
		{
			Legacy->SetVisibility(ESlateVisibility::Collapsed);
		}
	}

	if (Layout && WidgetTree && !PackagePath)
	{
		PackagePathLabel = WidgetTree->ConstructWidget<UTextBlock>(
			UTextBlock::StaticClass(), TEXT("PackagePathLabel"));
		PackagePath = WidgetTree->ConstructWidget<UEditableTextBox>(
			UEditableTextBox::StaticClass(), TEXT("PackagePath"));
		BrowseButton = WidgetTree->ConstructWidget<UButton>(
			UButton::StaticClass(), TEXT("BrowseButton"));
		UTextBlock* BrowseLabel = WidgetTree->ConstructWidget<UTextBlock>(
			UTextBlock::StaticClass(), TEXT("BrowseButtonLabel"));

		if (PackagePathLabel && PackagePath && BrowseButton && BrowseLabel)
		{
			PackagePathLabel->SetText(FText::FromString(
				TEXT("World package (the .cwp a build wrote with --emit-world-package)")));
			FSlateFontInfo LabelFont = PackagePathLabel->GetFont();
			LabelFont.Size = 10;
			PackagePathLabel->SetFont(LabelFont);

			PackagePath->SetHintText(FText::FromString(
				TEXT("choose a world package, or paste its full path")));

			BrowseLabel->SetText(FText::FromString(TEXT("Choose...")));
			BrowseButton->AddChild(BrowseLabel);
			BrowseButton->OnClicked.AddDynamic(this, &UWorldPackageImporterWidget::OnBrowseClicked);

			Layout->AddChildToVerticalBox(PackagePathLabel);
			Layout->AddChildToVerticalBox(PackagePath);
			Layout->AddChildToVerticalBox(BrowseButton);
			Layout->ShiftChild(0, PackagePathLabel);
			Layout->ShiftChild(1, PackagePath);
			Layout->ShiftChild(2, BrowseButton);
			if (UVerticalBoxSlot* BrowseSlot = Cast<UVerticalBoxSlot>(BrowseButton->Slot))
			{
				BrowseSlot->SetHorizontalAlignment(HAlign_Left);
			}
		}
	}

	// The one way past the guard that refuses to overwrite a level built from different source data.
	// Without it the refusal names a remedy the panel cannot offer.
	if (Layout && WidgetTree && !ReplaceDifferentSource)
	{
		ReplaceDifferentSource = WidgetTree->ConstructWidget<UCheckBox>(
			UCheckBox::StaticClass(), TEXT("ReplaceDifferentSource"));
		ReplaceDifferentSourceLabel = WidgetTree->ConstructWidget<UTextBlock>(
			UTextBlock::StaticClass(), TEXT("ReplaceDifferentSourceLabel"));
		if (ReplaceDifferentSource && ReplaceDifferentSourceLabel)
		{
			ReplaceDifferentSourceLabel->SetText(FText::FromString(
				TEXT("Replace a level built from a different source (discards edits made to it)")));
			FSlateFontInfo CheckFont = ReplaceDifferentSourceLabel->GetFont();
			CheckFont.Size = 10;
			ReplaceDifferentSourceLabel->SetFont(CheckFont);
			ReplaceDifferentSource->AddChild(ReplaceDifferentSourceLabel);
			Layout->AddChildToVerticalBox(ReplaceDifferentSource);
			if (ImportButton)
			{
				Layout->ShiftChild(Layout->GetChildIndex(ImportButton), ReplaceDifferentSource);
			}
		}
	}

	// An optional place to put a Cesium ion token.
	if (Layout && WidgetTree && !IonAccessToken)
	{
		IonAccessTokenLabel = WidgetTree->ConstructWidget<UTextBlock>(
			UTextBlock::StaticClass(), TEXT("IonAccessTokenLabel"));
		IonAccessToken = WidgetTree->ConstructWidget<UEditableTextBox>(
			UEditableTextBox::StaticClass(), TEXT("IonAccessToken"));
		if (IonAccessTokenLabel && IonAccessToken)
		{
			IonAccessTokenLabel->SetText(FText::FromString(
				TEXT("Cesium ion token (optional - saved into the level, and editable afterwards on "
				     "each imagery layer)")));
			FSlateFontInfo LabelFont = IonAccessTokenLabel->GetFont();
			LabelFont.Size = 10;
			IonAccessTokenLabel->SetFont(LabelFont);
			IonAccessToken->SetHintText(FText::FromString(
				TEXT("leave empty to use the editor's own token, and CESIUM_ION_TOKEN when played")));

			Layout->AddChildToVerticalBox(IonAccessTokenLabel);
			Layout->AddChildToVerticalBox(IonAccessToken);
			// AddChild appends, which would put these after the button. Put them directly below the
			// world name, which is the field they follow on the panel.
			// Below the package it applies to, and above the action it affects.
			const int32 BeforeImport = ImportButton
				? FMath::Max(0, Layout->GetChildIndex(ImportButton)) : Layout->GetChildrenCount();
			Layout->ShiftChild(BeforeImport, IonAccessTokenLabel);
			Layout->ShiftChild(BeforeImport + 1, IonAccessToken);
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
			TEXT("Read the chosen world package and write it out as a level, together "
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
	if (!PackagePath || !PackagePath->GetText().IsEmpty())
	{
		return;
	}
	// Offer whatever the last build left in the usual place, so the common case needs no typing at
	// all and the file dialog is there for everything else.
	const FString Usual = FPaths::ConvertRelativePathToFull(
		FPaths::ProjectDir() / TEXT("..") / TEXT("..") / TEXT("Build") / TEXT("world-packages"));
	const TArray<FString> Worlds = UWorldPackageImporter::ListWorldPackages(Usual);
	if (Worlds.Num() > 0)
	{
		PackagePath->SetText(FText::FromString(Usual / (Worlds[0] + TEXT(".cwp"))));
		Report(FString::Printf(TEXT("%d world package(s) where builds put them."), Worlds.Num()), false);
	}
}

void UWorldPackageImporterWidget::OnBrowseClicked()
{
	IDesktopPlatform* Desktop = FDesktopPlatformModule::Get();
	if (!Desktop || !PackagePath)
	{
		return;
	}
	const void* ParentWindow = FSlateApplication::IsInitialized()
		? FSlateApplication::Get().FindBestParentWindowHandleForDialogs(nullptr) : nullptr;

	FString StartIn = FPaths::GetPath(PackagePath->GetText().ToString());
	if (StartIn.IsEmpty() || !FPaths::DirectoryExists(StartIn))
	{
		StartIn = FPaths::ConvertRelativePathToFull(
			FPaths::ProjectDir() / TEXT("..") / TEXT("..") / TEXT("Build") / TEXT("world-packages"));
	}

	TArray<FString> Chosen;
	if (Desktop->OpenFileDialog(ParentWindow, TEXT("Choose a world package"), StartIn, FString(),
			TEXT("World package (*.cwp)|*.cwp|Zip archive (*.zip)|*.zip"),
			EFileDialogFlags::None, Chosen)
		&& Chosen.Num() > 0)
	{
		PackagePath->SetText(FText::FromString(FPaths::ConvertRelativePathToFull(Chosen[0])));
		Report(FString::Printf(TEXT("Chose %s."), *FPaths::GetCleanFilename(Chosen[0])), false);
	}
}

void UWorldPackageImporterWidget::HandleImportClicked()
{
	ImportFromFields();
}

FString UWorldPackageImporterWidget::ImportFromFields()
{
	if (!PackagePath)
	{
		const FString Message = TEXT("The panel is missing its input field.");
		Report(Message, true);
		return Message;
	}

	const FString Chosen = PackagePath->GetText().ToString().TrimStartAndEnd();
	if (Chosen.IsEmpty())
	{
		const FString Message = TEXT("Choose a world package to import.");
		Report(Message, true);
		return Message;
	}
	if (!FPaths::FileExists(Chosen))
	{
		const FString Message = FString::Printf(TEXT("There is no file at %s."), *Chosen);
		Report(Message, true);
		return Message;
	}

	// The world is named by the package that carries it, so there is nothing to keep in step.
	const FString Directory = FPaths::GetPath(Chosen);
	const FString World = FPaths::GetBaseFilename(Chosen);

	// Say what is about to be replaced, if anything, so the outcome is not a surprise.
	const FString Existing = UWorldPackageImporter::DescribeExistingImport(World, DestinationFolder);
	Report(Existing.IsEmpty()
		? FString::Printf(TEXT("Importing %s..."), *World)
		: FString::Printf(TEXT("Importing %s, replacing the level already there (built from '%s')..."),
			*World, *Existing), false);

	const FString Token =
		IonAccessToken ? IonAccessToken->GetText().ToString().TrimStartAndEnd() : FString();

	const FWorldPackageImportResult Result = UWorldPackageImporter::ImportWorldPackage(
		Directory, World, DestinationFolder,
		ReplaceDifferentSource ? ReplaceDifferentSource->IsChecked()
		                       : bReplaceLevelBuiltFromADifferentSource,
		Token);
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
