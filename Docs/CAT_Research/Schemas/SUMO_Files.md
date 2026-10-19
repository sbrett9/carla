# SUMO files

| | |
|---|---|
| File | the SUMO files of a compiled scenario, in `<scenario_id>/` beside its lock: the route, additional, configuration, network and type files |
| Schema | none from these tools: SUMO's own XSDs, in `data/xsd` of the SUMO installation (`tools/sumo/data/xsd` in a distribution) |
| Schema id | none |
| Format version | none |
| Version field | none: SUMO's formats carry no version of these tools |

## Description

Some of the files that a scenario developer or the compiler writes are in SUMO's own formats.\
Each follows the XML Schema that ships with SUMO, in `data/xsd` of the SUMO installation (`tools/sumo/data/xsd` in the distribution).\
These tools publish no schema of their own for them.

## Fields

### The files

| File | Written by | SUMO schema |
|---|---|---|
| `<scenario_id>.rou.xml` | `carla-compile-scenario` | `routes_file.xsd` |
| `<scenario_id>.add.xml`, only for a specification that declares lane closures | `carla-compile-scenario` | `additional_file.xsd` |
| `<scenario_id>.sumocfg` | `carla-compile-scenario` | `sumoConfiguration.xsd` |
| `<MapName>.net.xml`, the world's network copied byte for byte | `carla-compile-scenario`, from the world package | `net_file.xsd` |
| `<extract>.typ.xml`, the world's own road types | a scenario developer, beside the OpenStreetMap extract | `types_file.xsd` |

The `.rou.xml`, `.add.xml` and `.sumocfg` name their SUMO schema in `xsi:noNamespaceSchemaLocation`.

During the world build, netconvert reads SUMO's own OpenStreetMap type map first and the `<extract>.typ.xml` second.\
The world's types therefore take precedence over SUMO's.\
`Scenarios/Bahonar/Shahid_Bahonar_Port.typ.xml` is an example, in `Import/` in a checkout.\
It opens the service roads of a port to the vehicle classes `army` and `authority`.\
It leaves everything else as SUMO has it.

### The parameters these tools set on a vehicle type

A compiled route file binds each SUMO vehicle type (`<vType>`) to the CARLA body it is drawn with.\
It does this through three `<param>` keys: `carla:blueprint`, `carla:class_id` and `carla:catalogue_digest`.\
These are the same keys the vehicle catalog's `vehicles.vtypes.rou.xml` carries.\
These are the only parameters a compiled route file carries (check 52).\
[Vehicle types](Vehicle_Types.md) describes them and how a compiled type's id differs from the catalog's.

## Example

The compiled Bahonar scenario, `Scenarios/Bahonar/Shahid_Bahonar_Port_PatternOfLife/` in a distribution, holds these SUMO files beside its lock, plan and reports:

```
Shahid_Bahonar_Port.net.xml
Shahid_Bahonar_Port_PatternOfLife.rou.xml
Shahid_Bahonar_Port_PatternOfLife.sumocfg
```

It has no `.add.xml`, because the specification declares no lane closure.\
Its type map, `Shahid_Bahonar_Port.typ.xml`, sits beside the extract one folder up.

## Checks

Before it writes them, the compiler checks the route file against `routes_file.xsd` and the additional file against `additional_file.xsd` (check 51).

## Versions

These files carry no version of these tools.\
Each follows the SUMO release that wrote it.\
The compiler refuses a routing SUMO of a release other than the one that converted the world (check 6).
