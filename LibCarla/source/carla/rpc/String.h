// Copyright (c) 2026 Computer Vision Center (CVC) at the Universitat Autonoma
// de Barcelona (UAB).
//
// This work is licensed under the terms of the MIT license.
// For a copy, see <https://opensource.org/licenses/MIT>.

#pragma once

#include <string>

#ifdef LIBCARLA_INCLUDED_FROM_UE4
#include <util/enable-ue4-macros.h>
#include <Containers/UnrealString.h>
#include <util/disable-ue4-macros.h>
#endif // LIBCARLA_INCLUDED_FROM_UE4

namespace carla {
namespace rpc {

#ifdef LIBCARLA_INCLUDED_FROM_UE4

  // Every std::string crossing the RPC boundary is UTF-8. A conversion is given the length of its
  // source and takes the length of its result from the converter, because a character outside
  // ASCII is more than one byte in UTF-8, so the two lengths differ.

  // Conversion from fstring
  static inline std::string FromFString(const FString &Str) {
    return TCHAR_TO_UTF8(*Str);
  }

  // Conversion to fstring
  static inline FString ToFString(const std::string &str) {
    FUTF8ToTCHAR Converted(str.data(), static_cast<int32>(str.size()));
    return FString(Converted.Length(), Converted.Get());
  }

  // Conversion from fstring for long text such as an OpenDRIVE, in one pass so that no character
  // is split between two pieces
  static inline std::string FromLongFString(const FString &Str) {
    if (Str.IsEmpty()) {
      return {};
    }
    FTCHARToUTF8 Converted(*Str, Str.Len());
    return std::string(reinterpret_cast<const char *>(Converted.Get()), Converted.Length());
  }

  // Conversion to fstring for long text. Decoded as UTF-8: taken a byte at a time, each byte of a
  // character outside ASCII became a character of its own, and an OpenDRIVE with Persian street
  // names came back from the server with twice as many non-ASCII characters as it was sent with.
  static inline FString ToLongFString(const std::string &str) {
    return ToFString(str);
  }

#endif // LIBCARLA_INCLUDED_FROM_UE4

} // namespace rpc
} // namespace carla
