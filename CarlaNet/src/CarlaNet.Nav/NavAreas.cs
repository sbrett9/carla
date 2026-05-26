// Mirrors `enum NavAreas` and `enum SamplePolyFlags` from
// `LibCarla/source/carla/nav/Navigation.h:26-44`. These integer values are
// baked into the cooked .bin navmesh by `RecastBuilder.exe` at map cook
// time, so they MUST match the C++ side bit-for-bit.
namespace CarlaNet.Nav;

internal static class NavAreas
{
    public const int Block     = 0;
    public const int Sidewalk  = 1;
    public const int Crosswalk = 2;
    public const int Road      = 3;
    public const int Grass     = 4;
}

internal static class SamplePolyFlags
{
    public const int None      = 0x01;
    public const int Sidewalk  = 0x02;
    public const int Crosswalk = 0x04;
    public const int Road      = 0x08;
    public const int Grass     = 0x10;
    public const int All       = 0xffff;
    public const int Walkable  = Sidewalk | Crosswalk | Grass | Road;
}
