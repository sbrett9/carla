# SUMO files

Some of the files a scenario developer writes or the compiler writes are SUMO's own formats. Each
follows the XML Schema that ships with SUMO, in `data/xsd` of the SUMO installation
(`tools/sumo/data/xsd` in the distribution). We publish no schema of our own for them.

| File | Written by | SUMO schema |
|---|---|---|
| `<scenario_id>.rou.xml` | `carla-compile-scenario` | `routes_file.xsd` |
| `<scenario_id>.add.xml`, only when the specification declares lane closures | `carla-compile-scenario` | `additional_file.xsd` |
| `<scenario_id>.sumocfg` | `carla-compile-scenario` | `sumoConfiguration.xsd` |
| `<MapName>.net.xml`, the world's network copied byte for byte | `carla-compile-scenario`, from the world package | `net_file.xsd` |
| `<extract>.typ.xml`, the world's own road types | a scenario developer, beside the OpenStreetMap extract | `types_file.xsd` |

The compiler checks the route file against `routes_file.xsd`, and the additional file against
`additional_file.xsd`, before it writes them (check 51). The `.rou.xml`, `.add.xml` and `.sumocfg`
name their SUMO schema in `xsi:noNamespaceSchemaLocation`.

A type map is read when a world is built: netconvert reads SUMO's own OpenStreetMap type map first
and the `<extract>.typ.xml` second, so the world's types are layered over SUMO's.
`Import/Shahid_Bahonar_Port.typ.xml` is an example: it opens the service roads of a port to the
vehicle classes `army` and `authority` and leaves everything else as SUMO has it.

## Our parameters on a vehicle type

A compiled route file binds each SUMO vehicle type (`<vType>`) to the CARLA body it is drawn with,
through SUMO's generic `<param key="..." value="..."/>` element. SUMO stores these and never acts on
them. These are the only parameters a compiled route file carries (check 52): labels never travel
here, only in the supervision plan.

| Key | Value | Written | Meaning |
|---|---|---|---|
| `carla:blueprint` | a CARLA blueprint id, such as `vehicle.lincoln.mkz` | always | The body the type is drawn with. Its length, width and height in the `<vType>` are that body's, measured by the vehicle catalogue (check 15). A type with no `carla:blueprint` is simulated by SUMO and never drawn. |
| `carla:class_id` | the specification's vehicle class id, such as `car` | always | The class the body was drawn for. A class draws several bodies, one type each; this tells a reader which population a vehicle belongs to. A display convention names populations by it. |
| `carla:catalogue_digest` | the vehicle catalogue's digest | when the catalogue has one | The catalogue the type was written from, so a reader holding a catalogue can tell whether it is that one. carla-cot-telemetry warns once about a type written from another catalogue. |

A type's id is `<class_id>.<blueprint>`, such as `car_quick.vehicle.ue4.audi.tt`. The id is for a
person reading the file; what binds the type to a body is its `carla:blueprint`.

```xml
<vType id="car_quick.vehicle.ue4.audi.tt" vClass="passenger" length="4.1812" width="1.9667" height="1.3853" maxSpeed="60" speedFactor="normc(1.18,0.06,1.05,1.35)" guiShape="passenger" color="#D9D9E6">
    <param key="carla:blueprint" value="vehicle.ue4.audi.tt"/>
    <param key="carla:class_id" value="car_quick"/>
    <param key="carla:catalogue_digest" value="6037e3bb2bde6f45de45e31925236593d16653989293fe414d9060a78bfbe90d"/>
</vType>
```
