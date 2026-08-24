// Copyright (c) 2026 CARLA-Cesium digital-twin project.

#include "GeneratedWorld/WorldPackageImporterWidget.h"

#include "CarlaTools.h"
#include "GeneratedWorld/WorldPackageImporter.h"

#include <util/ue-header-guard-begin.h>
#include "Components/Button.h"
#include "Components/EditableTextBox.h"
#include "Components/TextBlock.h"
#include "Misc/Paths.h"
#include "Styling/SlateColor.h"
#include <util/ue-header-guard-end.h>

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
	SuggestFirstWorld();
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

	Report(FString::Printf(TEXT("Importing %s..."), *World), false);

	const FWorldPackageImportResult Result =
		UWorldPackageImporter::ImportWorldPackage(Directory, World, DestinationFolder);
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
