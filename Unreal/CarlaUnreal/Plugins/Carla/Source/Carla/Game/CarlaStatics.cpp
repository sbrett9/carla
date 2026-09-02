// Copyright (c) 2026 Computer Vision Center (CVC) at the Universitat Autonoma
// de Barcelona (UAB).
//
// This work is licensed under the terms of the MIT license.
// For a copy, see <https://opensource.org/licenses/MIT>.

#include "Carla/Game/CarlaStatics.h"
#include "Carla.h"

#include <util/ue-header-guard-begin.h>
#include "Interfaces/IPluginManager.h"
#include "Misc/PackageName.h"
#include "Misc/Paths.h"
#include "Modules/ModuleManager.h"
#include "HAL/FileManagerGeneric.h"
#include <util/ue-header-guard-end.h>


TArray<FString> UCarlaStatics::GetAllPluginContentPaths()
{
  TArray<FString> OutContentDirs;
  const TArray<TSharedRef<IPlugin>> Plugins = IPluginManager::Get().GetDiscoveredPlugins();
  for (const TSharedRef<IPlugin>& Plugin : Plugins)
  {
      if (Plugin->GetLoadedFrom() == EPluginLoadedFrom::Engine)
      {
          continue;
      }

      FString ContentDir = Plugin->GetContentDir();
      if (FPaths::DirectoryExists(ContentDir))
      {
        OutContentDirs.Add(ContentDir);
      }
  }
  return OutContentDirs;
}


TArray<FString> UCarlaStatics::GetAllMapNames()
{
  TArray<FString> TmpStrList, MapNameList;
  TArray<FString> PathList;

  PathList.Add(FPaths::ProjectContentDir());
  PathList.Append(GetAllPluginContentPaths());

  for(const FString &Path : PathList) {
    if (FPaths::DirectoryExists(Path)) {
      UE_LOG(LogCarla, Log, TEXT("Path: %s"), *Path);
      IFileManager::Get().FindFilesRecursive(MapNameList, *Path, TEXT("*.umap"), true, false, false);
    }
  }

  // Filter out undesired maps
  MapNameList.RemoveAll([](const FString& Name) {
      return Name.Contains("TestMaps") || Name.Contains("OpenDriveMap") || Name.Contains("Sublevels");
  });

  // Report long package names rather than bare file names. A bare name can only be resolved through
  // the asset registry, which is written when the game is cooked, so any level added afterwards --
  // every exported world -- is invisible to it. A long package name bypasses the registry and loads
  // directly, and it is what clients already expect: the shipped utilities and smoke tests match
  // against paths like /Game/Carla/Maps/BaseMap/BaseMap.
  TArray<FString> PackageNames;
  PackageNames.Reserve(MapNameList.Num());
  for (const FString& MapFile : MapNameList) {
    FString LongPackageName;
    if (FPackageName::TryConvertFilenameToLongPackageName(MapFile, LongPackageName)) {
      PackageNames.Add(MoveTemp(LongPackageName));
    }
    else {
      // A map outside every mounted content root cannot be loaded by name at all. Name it here
      // rather than dropping it silently, so a staging mistake is visible.
      UE_LOG(LogCarla, Warning,
          TEXT("Map '%s' is not under a mounted content root, so it cannot be loaded by name."),
          *MapFile);
    }
  }
  return PackageNames;
}

FString UCarlaStatics::FindMapPath(const FString &MapName)
{
  // A long package name identifies exactly one file, so resolve it directly rather than searching for
  // something with a matching base name. This is the form GetAllMapNames reports, so listing the maps
  // and then loading one has to work; it is also the only form that can reach a level staged after the
  // game was cooked, including one mounted from its own plugin at /<Name>/.
  if (MapName.StartsWith(TEXT("/")))
  {
    FString Filename;
    const bool bConverted = FPackageName::TryConvertLongPackageNameToFilename(
        MapName, Filename, FPackageName::GetMapPackageExtension());
    if (bConverted && FPaths::FileExists(Filename))
    {
      return Filename;
    }

    // A caller who names a full package path means THAT package. Falling through to a base-name
    // search would quietly load a different map that happens to share the last component, which is
    // easy to do here: an imported world exists at /Game/Carla/Maps/Generated/<Name> and again at
    // /<Name>/Maps/<Name> once its plugin is mounted, and the base-name search finds project content
    // first. Loading the wrong one of those looks like success and differs only in what is actually
    // in the level.
    //
    // The one exception is a path under a root that is not mounted at all, which cannot be resolved
    // by anybody: say so rather than substituting.
    UE_LOG(LogCarla, Warning,
        TEXT("Map '%s' names a package that %s; not falling back to a name search."),
        *MapName,
        bConverted ? TEXT("has no file on disk") : TEXT("is not under a mounted content root"));
    return FString();
  }

  // Anything else is matched on base name, which is what clients passing a bare map name rely on.
  const FString SearchName = MapName.StartsWith(TEXT("/"))
      ? FPaths::GetBaseFilename(MapName)
      : MapName;

  TArray<FString> ContentPaths;

  ContentPaths.Add(FPaths::ProjectContentDir());
  ContentPaths.Append(GetAllPluginContentPaths());

  // Look for matching map files
  for (const FString& Path : ContentPaths)
  {
      TArray<FString> FoundFiles;
      IFileManager::Get().FindFilesRecursive(FoundFiles, *Path, TEXT("*.umap"), true, false);

      for (const FString& FilePath : FoundFiles)
      {
          FString FileName = FPaths::GetBaseFilename(FilePath); // just "MyMap", no path, no extension
          if (FileName.Equals(SearchName, ESearchCase::IgnoreCase))
          {
              // A file on disk is not the same as a loadable map. This search walks the content
              // directory of every DISCOVERED plugin, and a world that has been unmounted -- or was
              // never mounted -- still has its files there while its content root does not exist, so
              // the name cannot be resolved and loading it would fail after the caller was told it
              // had been found. Skip it and keep looking.
              FString LongPackageName;
              if (!FPackageName::TryConvertFilenameToLongPackageName(FilePath, LongPackageName))
              {
                  UE_LOG(LogCarla, Verbose,
                      TEXT("Map '%s' matches '%s' but is not under a mounted content root; skipping."),
                      *FilePath, *SearchName);
                  continue;
              }
              return FilePath; // Return the full path of the first matching map. Only one map is expected.
          }
      }
  }

  return FString();
}
