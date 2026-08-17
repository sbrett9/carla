// Copyright (c) 2026 Computer Vision Center (CVC) at the Universitat Autonoma
// de Barcelona (UAB).
//
// This work is licensed under the terms of the MIT license.
// For a copy, see <https://opensource.org/licenses/MIT>.

#include "Carla/Sensor/DepthCamera.h"
#include "Carla.h"
#include "Carla/Actor/ActorBlueprintFunctionLibrary.h"

FActorDefinition ADepthCamera::GetSensorDefinition()
{
  FActorDefinition Definition =
      UActorBlueprintFunctionLibrary::MakeCameraDefinition(TEXT("depth"));

  // The greatest range this camera can report, in metres. Each pixel carries its range as a 24-bit
  // value spread linearly over this, so a surface beyond it saturates and becomes indistinguishable
  // from sky, while one inside it is resolved to a small fraction of a millimetre at any setting
  // worth using. Only the depth camera encodes a range, so the attribute belongs to it rather than
  // to cameras in general. The default matches the value the depth material already carries, so a
  // camera that does not ask for anything behaves exactly as it did before this attribute existed.
  FActorVariation MaxRange;
  MaxRange.Id = TEXT("max_range");
  MaxRange.Type = EActorAttributeType::Float;
  MaxRange.RecommendedValues = {TEXT("1000.0")};
  MaxRange.bRestrictToRecommended = false;
  Definition.Variations.Add(MaxRange);

  return Definition;
}

void ADepthCamera::Set(const FActorDescription &Description)
{
  Super::Set(Description);

  // Hand the requested range to the depth material, which divides the scene's depth by it. Shader
  // index 1 is that material: the lens-distortion material is added first in the constructor. The
  // scene's depth is in centimetres, so the metres asked for are converted. Shader parameters set
  // here are applied when the capture component is built, which happens after this call.
  const float MaxRangeMetres = UActorBlueprintFunctionLibrary::RetrieveActorAttributeToFloat(
      "max_range", Description.Variations, 1000.0f);
  SetFloatShaderParameter(1, TEXT("Far_1"), MaxRangeMetres * 100.0f);
}

ADepthCamera::ADepthCamera(const FObjectInitializer &ObjectInitializer)
  : Super(ObjectInitializer)
{
  AddPostProcessingMaterial(
      TEXT("Material'/Carla/PostProcessingMaterials/PhysicLensDistortion.PhysicLensDistortion'"));
  AddPostProcessingMaterial(
#if PLATFORM_LINUX
      TEXT("Material'/Carla/PostProcessingMaterials/DepthEffectMaterial_GLSL.DepthEffectMaterial_GLSL'")
#else
      TEXT("Material'/Carla/PostProcessingMaterials/DepthEffectMaterial.DepthEffectMaterial'")
#endif
  );
}

void ADepthCamera::PostPhysTick(UWorld *World, ELevelTick TickType, float DeltaSeconds)
{
  TRACE_CPUPROFILER_EVENT_SCOPE(ADepthCamera::PostPhysTick);
  Super::PostPhysTick(World, TickType, DeltaSeconds);

  if (!AreClientsListening())
      return;

  auto FrameIndex = FCarlaEngine::GetFrameCounter();
  ImageUtil::ReadSensorImageDataAsyncFColor(*this, [this, FrameIndex](
    TArrayView<const FColor> Pixels,
    FIntPoint Size) -> bool
  {
    SendDataToClient(*this, Pixels, FrameIndex);
    return true;
  });
}
