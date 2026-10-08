"""The schema of a level package's `world.json`: the manifest `PackageWorld` writes into a world's zip.

A level package is the add-on content one generated world ships as: a zip holding the world's cooked
plugin folder and, beside it, `world.json`. `Scripts/Windows/PackageWorld.ps1` and
`Scripts/Linux/PackageWorld.sh` write the manifest; `InstallWorld.ps1` and `InstallWorld.sh` read it
and refuse to install a world whose world interface version the target package does not provide.

This is a different file from the `world.json` inside a world package (`.cwp`), which
`WorldPackageSchemas` describes. Its keys are camelCase, and both scripts write every one of them.

The schema is published to `CarlaControl/schemas/` by `write` and held equal to this module by a test,
which also runs both scripts and checks what they write against it.
"""
from __future__ import annotations

from pathlib import Path

from carlacontrol.JsonSchemaFile import DRAFT, GIT_COMMIT_OR_EMPTY, UTC_INSTANT, JsonSchemaFile

LEVEL_MANIFEST_FORMAT_VERSION = 1
LEVEL_MANIFEST_SCHEMA = "level_package_manifest.schema.json"

# The configurations PackageWorld cooks in, and the target platform each script names.
CONFIGURATIONS = ("Development", "Shipping", "Debug")
PLATFORMS = ("Win64", "Linux")


class LevelPackageSchema:
    """The level package manifest's schema."""

    @staticmethod
    def schema() -> dict:
        """`world.json` in a level package, as `PackageWorld` writes it."""
        version_part = {"type": "integer", "minimum": 0}
        return {
            "$schema": DRAFT,
            "$id": JsonSchemaFile.identifier("level_package_manifest", LEVEL_MANIFEST_FORMAT_VERSION),
            "title": "Level package manifest (world.json in a PackageWorld zip)",
            "description": "What one separately delivered world is and what it needs from the CARLA "
                           "package it is installed into. An installer compares the world interface "
                           "version; the commit hashes identify the build and are never compared.",
            "type": "object",
            "additionalProperties": False,
            "required": ["formatVersion", "world", "mapPackage", "worldInterfaceMajor",
                         "worldInterfaceMinor", "basedOnRelease", "releaseVersion", "config",
                         "platform", "carlaGitHash", "contentGitHash", "unrealGitHash",
                         "packagedAtUtc"],
            "properties": {
                "formatVersion": {"const": LEVEL_MANIFEST_FORMAT_VERSION,
                                  "description": "The format of this file."},
                "world": {"type": "string", "minLength": 1,
                          "description": "The world's name: its plugin folder, which the zip "
                                         "holds beside this file."},
                "mapPackage": {"type": "string", "pattern": "^/[^/]+/Maps/[^/]+$",
                               "description": "The level's Unreal package path, /<world>/Maps/"
                                              "<world>, which loads it."},
                "worldInterfaceMajor": JsonSchemaFile.described(
                    version_part, "The Major of the world interface version the world was cooked "
                                  "against. The package installed into must declare the same Major."),
                "worldInterfaceMinor": JsonSchemaFile.described(
                    version_part, "The Minor of that version. The package installed into must "
                                  "declare this Minor or a later one."),
                "basedOnRelease": {"type": "string", "minLength": 1,
                                   "description": "The base release the world was cooked against: "
                                                  "the short CARLA commit the base cook named it "
                                                  "by, unless one was given."},
                "releaseVersion": {"type": "string", "pattern": r"^[0-9]+\.[0-9]+\.[0-9]+$",
                                   "description": "The CARLA release version of the checkout that "
                                                  "cooked the world, from CMakeLists.txt."},
                "config": {"enum": list(CONFIGURATIONS),
                           "description": "The build configuration the world was cooked in. It "
                                          "must match the package's."},
                "platform": {"enum": list(PLATFORMS),
                             "description": "The target platform: Win64 from PackageWorld.ps1, "
                                            "Linux from PackageWorld.sh."},
                "carlaGitHash": {"type": "string", "pattern": GIT_COMMIT_OR_EMPTY,
                                 "description": "The CARLA commit that cooked the world. Empty "
                                                "when git could not say."},
                "contentGitHash": {"type": "string", "pattern": GIT_COMMIT_OR_EMPTY,
                                   "description": "The commit of Unreal/CarlaUnreal/Content/Carla. "
                                                  "Empty when git could not say."},
                "unrealGitHash": {"type": "string", "pattern": GIT_COMMIT_OR_EMPTY,
                                  "description": "The Unreal Engine commit. Empty when the engine "
                                                 "folder is not a git checkout."},
                "packagedAtUtc": {"type": "string", "pattern": UTC_INSTANT,
                                  "description": "When the zip was made, ISO 8601 UTC: seven "
                                                 "decimals of a second from PackageWorld.ps1, "
                                                 "whole seconds from PackageWorld.sh."},
            },
        }

    @classmethod
    def write(cls, directory: str | Path) -> list[Path]:
        """Publish the schema into `directory`."""
        return [JsonSchemaFile.write(cls.schema(), Path(directory) / LEVEL_MANIFEST_SCHEMA)]
