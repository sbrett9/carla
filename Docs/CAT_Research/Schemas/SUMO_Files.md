# SUMO files

Some of the files that a scenario developer or the compiler writes are in SUMO's own formats.\
Each follows the XML Schema that ships with SUMO, in `data/xsd` of the SUMO installation (`tools/sumo/data/xsd` in the distribution).\
These tools publish no schema of their own for them.

| File | Written by | SUMO schema |
|---|---|---|
| `<scenario_id>.rou.xml` | `carla-compile-scenario` | `routes_file.xsd` |
| `<scenario_id>.add.xml`, only when the specification declares lane closures | `carla-compile-scenario` | `additional_file.xsd` |
| `<scenario_id>.sumocfg` | `carla-compile-scenario` | `sumoConfiguration.xsd` |
| `<MapName>.net.xml`, the world's network copied byte for byte | `carla-compile-scenario`, from the world package | `net_file.xsd` |
| `<extract>.typ.xml`, the world's own road types | a scenario developer, beside the OpenStreetMap extract | `types_file.xsd` |

The compiler checks the route file against `routes_file.xsd`, and the additional file against `additional_file.xsd`, before it writes them (check 51).\
The `.rou.xml`, `.add.xml` and `.sumocfg` name their SUMO schema in `xsi:noNamespaceSchemaLocation`.

A type map is read when a world is built: netconvert reads SUMO's own OpenStreetMap type map first and the `<extract>.typ.xml` second, so the world's types take precedence over SUMO's.\
`Import/Shahid_Bahonar_Port.typ.xml` is an example: it opens the service roads of a port to the vehicle classes `army` and `authority` and leaves everything else as SUMO has it.

## The parameters these tools set on a vehicle type

A compiled route file binds each SUMO vehicle type (`<vType>`) to the CARLA body it is drawn with through three `<param>` keys, `carla:blueprint`, `carla:class_id` and `carla:catalogue_digest`: the same ones the vehicle catalog's `vehicles.vtypes.rou.xml` carries.\
[Vehicle types](Vehicle_Types.md) describes them, the only parameters a compiled route file carries (check 52), and how a compiled type's id differs from the catalog's.
