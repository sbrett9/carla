// Ported from Eclipse SUMO's reference TraCI client, tools/traci/constants.py, which SUMO itself
// generates from src/libsumo/TraCIConstants.h with tools/traci/rebuildConstants.py.
//
//   Upstream:      https://github.com/eclipse-sumo/sumo
//   Pinned commit: e238ea04b7150ba23a348a285d3048919fa4830b (Eclipse SUMO 1.27.0)
//   Copyright (C) 2009-2026 German Aerospace Center (DLR) and others.
//   SPDX-License-Identifier: EPL-2.0 OR GPL-2.0-or-later
//
// Generated from that file, not hand-written. Regenerate it when the SUMO pin moves.

namespace CarlaNet.Sumo;

/// <summary>
/// TraCI's command, variable and value-type identifiers, for the pinned SUMO release.
/// </summary>
/// <remarks>
/// <para>The names are SUMO's own, in SUMO's own casing, rather than the PascalCase the rest of
/// CarlaNet uses. That is deliberate, and it is the point of the file: this table has to stay
/// diffable line for line against <c>constants.py</c> and against <c>TraCIConstants.h</c> -- by a
/// person or by <c>TraCIConstantsTests</c>, which does exactly that against the staged SUMO --
/// because one wrong number here decodes a frame into plausible nonsense instead of into an error.
/// Renaming them would make the only cheap check impossible.</para>
///
/// <para>The whole table is carried rather than the subset in use. It costs nothing at run time,
/// these being compile-time constants, and it means the next variable a bridge needs is already
/// here and already checked instead of being copied in by hand later.</para>
/// </remarks>
public static class TraCIConstants
{
    // ---- VERSION ----

    /// <summary>TraCI identifier <c>TRACI_VERSION</c>.</summary>
    public const int TRACI_VERSION = 22;

    // ---- COMMANDS ----

    /// <summary>command: get version</summary>
    public const int CMD_GETVERSION = 0x00;

    /// <summary>command: load</summary>
    public const int CMD_LOAD = 0x01;

    /// <summary>command: execute move (half step)</summary>
    public const int CMD_EXECUTEMOVE = 0x7d;

    /// <summary>command: simulation step</summary>
    public const int CMD_SIMSTEP = 0x02;

    /// <summary>command: set connection priority (execution order)</summary>
    public const int CMD_SETORDER = 0x03;

    /// <summary>command: stop vehicle</summary>
    public const int CMD_STOP = 0x12;

    /// <summary>command: reroute to parking area</summary>
    public const int CMD_REROUTE_TO_PARKING = 0xc2;

    /// <summary>command: Resume from parking</summary>
    public const int CMD_RESUME = 0x19;

    /// <summary>command: set lane</summary>
    public const int CMD_CHANGELANE = 0x13;

    /// <summary>command: slow down</summary>
    public const int CMD_SLOWDOWN = 0x14;

    /// <summary>command: set sublane (vehicle)</summary>
    public const int CMD_CHANGESUBLANE = 0x15;

    /// <summary>command: open gap</summary>
    public const int CMD_OPENGAP = 0x16;

    /// <summary>command: replace vehicle stop and update route</summary>
    public const int CMD_REPLACE_STOP = 0x17;

    /// <summary>command: insert vehicle stop and update route</summary>
    public const int CMD_INSERT_STOP = 0x18;

    /// <summary>command: retrieve information about the current taxi fleet and their status</summary>
    public const int VAR_TAXI_FLEET = 0x20;

    /// <summary>command: send dispatch request for the given taxi</summary>
    public const int CMD_TAXI_DISPATCH = 0x21;

    /// <summary>command: change target</summary>
    public const int CMD_CHANGETARGET = 0x31;

    /// <summary>command: close sumo</summary>
    public const int CMD_CLOSE = 0x7F;

    /// <summary>command: add subscription filter</summary>
    public const int CMD_ADD_SUBSCRIPTION_FILTER = 0x7e;

    /// <summary>command: subscribe induction loop (e1) context</summary>
    public const int CMD_SUBSCRIBE_INDUCTIONLOOP_CONTEXT = 0x80;

    /// <summary>response: subscribe induction loop (e1) context</summary>
    public const int RESPONSE_SUBSCRIBE_INDUCTIONLOOP_CONTEXT = 0x90;

    /// <summary>command: get induction loop (e1) variable</summary>
    public const int CMD_GET_INDUCTIONLOOP_VARIABLE = 0xa0;

    /// <summary>response: get induction loop (e1) variable</summary>
    public const int RESPONSE_GET_INDUCTIONLOOP_VARIABLE = 0xb0;

    /// <summary>command: set induction loop (e1) variable, not used yet</summary>
    public const int CMD_SET_INDUCTIONLOOP_VARIABLE = 0xc0;

    /// <summary>command: subscribe induction loop (e1) variable</summary>
    public const int CMD_SUBSCRIBE_INDUCTIONLOOP_VARIABLE = 0xd0;

    /// <summary>response: subscribe induction loop (e1) variable</summary>
    public const int RESPONSE_SUBSCRIBE_INDUCTIONLOOP_VARIABLE = 0xe0;

    /// <summary>command: subscribe multi-entry/multi-exit detector (e3) context</summary>
    public const int CMD_SUBSCRIBE_MULTIENTRYEXIT_CONTEXT = 0x81;

    /// <summary>response: subscribe multi-entry/multi-exit detector (e3) context</summary>
    public const int RESPONSE_SUBSCRIBE_MULTIENTRYEXIT_CONTEXT = 0x91;

    /// <summary>command: get multi-entry/multi-exit detector (e3) variable</summary>
    public const int CMD_GET_MULTIENTRYEXIT_VARIABLE = 0xa1;

    /// <summary>response: get multi-entry/multi-exit detector (e3) variable</summary>
    public const int RESPONSE_GET_MULTIENTRYEXIT_VARIABLE = 0xb1;

    /// <summary>command: set multi-entry/multi-exit detector (e3) variable, not used yet</summary>
    public const int CMD_SET_MULTIENTRYEXIT_VARIABLE = 0xc1;

    /// <summary>command: subscribe multi-entry/multi-exit detector (e3) variable</summary>
    public const int CMD_SUBSCRIBE_MULTIENTRYEXIT_VARIABLE = 0xd1;

    /// <summary>response: subscribe multi-entry/multi-exit detector (e3) variable</summary>
    public const int RESPONSE_SUBSCRIBE_MULTIENTRYEXIT_VARIABLE = 0xe1;

    /// <summary>command: subscribe traffic lights context</summary>
    public const int CMD_SUBSCRIBE_TL_CONTEXT = 0x82;

    /// <summary>response: subscribe traffic lights context</summary>
    public const int RESPONSE_SUBSCRIBE_TL_CONTEXT = 0x92;

    /// <summary>command: get traffic lights variable</summary>
    public const int CMD_GET_TL_VARIABLE = 0xa2;

    /// <summary>response: get traffic lights variable</summary>
    public const int RESPONSE_GET_TL_VARIABLE = 0xb2;

    /// <summary>command: set traffic lights variable</summary>
    public const int CMD_SET_TL_VARIABLE = 0xc2;

    /// <summary>command: subscribe traffic lights variable</summary>
    public const int CMD_SUBSCRIBE_TL_VARIABLE = 0xd2;

    /// <summary>response: subscribe traffic lights variable</summary>
    public const int RESPONSE_SUBSCRIBE_TL_VARIABLE = 0xe2;

    /// <summary>command: subscribe lane context</summary>
    public const int CMD_SUBSCRIBE_LANE_CONTEXT = 0x83;

    /// <summary>response: subscribe lane context</summary>
    public const int RESPONSE_SUBSCRIBE_LANE_CONTEXT = 0x93;

    /// <summary>command: get lane variable</summary>
    public const int CMD_GET_LANE_VARIABLE = 0xa3;

    /// <summary>response: get lane variable</summary>
    public const int RESPONSE_GET_LANE_VARIABLE = 0xb3;

    /// <summary>command: set lane variable</summary>
    public const int CMD_SET_LANE_VARIABLE = 0xc3;

    /// <summary>command: subscribe lane variable</summary>
    public const int CMD_SUBSCRIBE_LANE_VARIABLE = 0xd3;

    /// <summary>response: subscribe lane variable</summary>
    public const int RESPONSE_SUBSCRIBE_LANE_VARIABLE = 0xe3;

    /// <summary>command: subscribe vehicle context</summary>
    public const int CMD_SUBSCRIBE_VEHICLE_CONTEXT = 0x84;

    /// <summary>response: subscribe vehicle context</summary>
    public const int RESPONSE_SUBSCRIBE_VEHICLE_CONTEXT = 0x94;

    /// <summary>command: get vehicle variable</summary>
    public const int CMD_GET_VEHICLE_VARIABLE = 0xa4;

    /// <summary>response: get vehicle variable</summary>
    public const int RESPONSE_GET_VEHICLE_VARIABLE = 0xb4;

    /// <summary>command: set vehicle variable</summary>
    public const int CMD_SET_VEHICLE_VARIABLE = 0xc4;

    /// <summary>command: subscribe vehicle variable</summary>
    public const int CMD_SUBSCRIBE_VEHICLE_VARIABLE = 0xd4;

    /// <summary>response: subscribe vehicle variable</summary>
    public const int RESPONSE_SUBSCRIBE_VEHICLE_VARIABLE = 0xe4;

    /// <summary>command: subscribe vehicle type context</summary>
    public const int CMD_SUBSCRIBE_VEHICLETYPE_CONTEXT = 0x85;

    /// <summary>response: subscribe vehicle type context</summary>
    public const int RESPONSE_SUBSCRIBE_VEHICLETYPE_CONTEXT = 0x95;

    /// <summary>command: get vehicle type variable</summary>
    public const int CMD_GET_VEHICLETYPE_VARIABLE = 0xa5;

    /// <summary>response: get vehicle type variable</summary>
    public const int RESPONSE_GET_VEHICLETYPE_VARIABLE = 0xb5;

    /// <summary>command: set vehicle type variable</summary>
    public const int CMD_SET_VEHICLETYPE_VARIABLE = 0xc5;

    /// <summary>command: subscribe vehicle type variable</summary>
    public const int CMD_SUBSCRIBE_VEHICLETYPE_VARIABLE = 0xd5;

    /// <summary>response: subscribe vehicle type variable</summary>
    public const int RESPONSE_SUBSCRIBE_VEHICLETYPE_VARIABLE = 0xe5;

    /// <summary>command: subscribe route context</summary>
    public const int CMD_SUBSCRIBE_ROUTE_CONTEXT = 0x86;

    /// <summary>response: subscribe route context</summary>
    public const int RESPONSE_SUBSCRIBE_ROUTE_CONTEXT = 0x96;

    /// <summary>command: get route variable</summary>
    public const int CMD_GET_ROUTE_VARIABLE = 0xa6;

    /// <summary>response: get route variable</summary>
    public const int RESPONSE_GET_ROUTE_VARIABLE = 0xb6;

    /// <summary>command: set route variable</summary>
    public const int CMD_SET_ROUTE_VARIABLE = 0xc6;

    /// <summary>command: subscribe route variable</summary>
    public const int CMD_SUBSCRIBE_ROUTE_VARIABLE = 0xd6;

    /// <summary>response: subscribe route variable</summary>
    public const int RESPONSE_SUBSCRIBE_ROUTE_VARIABLE = 0xe6;

    /// <summary>command: subscribe poi context</summary>
    public const int CMD_SUBSCRIBE_POI_CONTEXT = 0x87;

    /// <summary>response: subscribe poi context</summary>
    public const int RESPONSE_SUBSCRIBE_POI_CONTEXT = 0x97;

    /// <summary>command: get poi variable</summary>
    public const int CMD_GET_POI_VARIABLE = 0xa7;

    /// <summary>response: get poi variable</summary>
    public const int RESPONSE_GET_POI_VARIABLE = 0xb7;

    /// <summary>command: set poi variable</summary>
    public const int CMD_SET_POI_VARIABLE = 0xc7;

    /// <summary>command: subscribe poi variable</summary>
    public const int CMD_SUBSCRIBE_POI_VARIABLE = 0xd7;

    /// <summary>response: subscribe poi variable</summary>
    public const int RESPONSE_SUBSCRIBE_POI_VARIABLE = 0xe7;

    /// <summary>command: subscribe polygon context</summary>
    public const int CMD_SUBSCRIBE_POLYGON_CONTEXT = 0x88;

    /// <summary>response: subscribe polygon context</summary>
    public const int RESPONSE_SUBSCRIBE_POLYGON_CONTEXT = 0x98;

    /// <summary>command: get polygon variable</summary>
    public const int CMD_GET_POLYGON_VARIABLE = 0xa8;

    /// <summary>response: get polygon variable</summary>
    public const int RESPONSE_GET_POLYGON_VARIABLE = 0xb8;

    /// <summary>command: set polygon variable</summary>
    public const int CMD_SET_POLYGON_VARIABLE = 0xc8;

    /// <summary>command: subscribe polygon variable</summary>
    public const int CMD_SUBSCRIBE_POLYGON_VARIABLE = 0xd8;

    /// <summary>response: subscribe polygon variable</summary>
    public const int RESPONSE_SUBSCRIBE_POLYGON_VARIABLE = 0xe8;

    /// <summary>command: subscribe junction context</summary>
    public const int CMD_SUBSCRIBE_JUNCTION_CONTEXT = 0x89;

    /// <summary>response: subscribe junction context</summary>
    public const int RESPONSE_SUBSCRIBE_JUNCTION_CONTEXT = 0x99;

    /// <summary>command: get junction variable</summary>
    public const int CMD_GET_JUNCTION_VARIABLE = 0xa9;

    /// <summary>response: get junction variable</summary>
    public const int RESPONSE_GET_JUNCTION_VARIABLE = 0xb9;

    /// <summary>command: set junction variable</summary>
    public const int CMD_SET_JUNCTION_VARIABLE = 0xc9;

    /// <summary>command: subscribe junction variable</summary>
    public const int CMD_SUBSCRIBE_JUNCTION_VARIABLE = 0xd9;

    /// <summary>response: subscribe junction variable</summary>
    public const int RESPONSE_SUBSCRIBE_JUNCTION_VARIABLE = 0xe9;

    /// <summary>command: subscribe edge context</summary>
    public const int CMD_SUBSCRIBE_EDGE_CONTEXT = 0x8a;

    /// <summary>response: subscribe edge context</summary>
    public const int RESPONSE_SUBSCRIBE_EDGE_CONTEXT = 0x9a;

    /// <summary>command: get edge variable</summary>
    public const int CMD_GET_EDGE_VARIABLE = 0xaa;

    /// <summary>response: get edge variable</summary>
    public const int RESPONSE_GET_EDGE_VARIABLE = 0xba;

    /// <summary>command: set edge variable</summary>
    public const int CMD_SET_EDGE_VARIABLE = 0xca;

    /// <summary>command: subscribe edge variable</summary>
    public const int CMD_SUBSCRIBE_EDGE_VARIABLE = 0xda;

    /// <summary>response: subscribe edge variable</summary>
    public const int RESPONSE_SUBSCRIBE_EDGE_VARIABLE = 0xea;

    /// <summary>command: subscribe simulation context</summary>
    public const int CMD_SUBSCRIBE_SIM_CONTEXT = 0x8b;

    /// <summary>response: subscribe simulation context</summary>
    public const int RESPONSE_SUBSCRIBE_SIM_CONTEXT = 0x9b;

    /// <summary>command: get simulation variable</summary>
    public const int CMD_GET_SIM_VARIABLE = 0xab;

    /// <summary>response: get simulation variable</summary>
    public const int RESPONSE_GET_SIM_VARIABLE = 0xbb;

    /// <summary>command: set simulation variable</summary>
    public const int CMD_SET_SIM_VARIABLE = 0xcb;

    /// <summary>command: subscribe simulation variable</summary>
    public const int CMD_SUBSCRIBE_SIM_VARIABLE = 0xdb;

    /// <summary>response: subscribe simulation variable</summary>
    public const int RESPONSE_SUBSCRIBE_SIM_VARIABLE = 0xeb;

    /// <summary>command: subscribe GUI context</summary>
    public const int CMD_SUBSCRIBE_GUI_CONTEXT = 0x8c;

    /// <summary>response: subscribe GUI context</summary>
    public const int RESPONSE_SUBSCRIBE_GUI_CONTEXT = 0x9c;

    /// <summary>command: get GUI variable</summary>
    public const int CMD_GET_GUI_VARIABLE = 0xac;

    /// <summary>response: get GUI variable</summary>
    public const int RESPONSE_GET_GUI_VARIABLE = 0xbc;

    /// <summary>command: set GUI variable</summary>
    public const int CMD_SET_GUI_VARIABLE = 0xcc;

    /// <summary>command: subscribe GUI variable</summary>
    public const int CMD_SUBSCRIBE_GUI_VARIABLE = 0xdc;

    /// <summary>response: subscribe GUI variable</summary>
    public const int RESPONSE_SUBSCRIBE_GUI_VARIABLE = 0xec;

    /// <summary>command: subscribe lane area detector (e2) context</summary>
    public const int CMD_SUBSCRIBE_LANEAREA_CONTEXT = 0x8d;

    /// <summary>response: subscribe lane area detector (e2) context</summary>
    public const int RESPONSE_SUBSCRIBE_LANEAREA_CONTEXT = 0x9d;

    /// <summary>command: get lane area detector (e2) variable</summary>
    public const int CMD_GET_LANEAREA_VARIABLE = 0xad;

    /// <summary>response: get lane area detector (e2) variable</summary>
    public const int RESPONSE_GET_LANEAREA_VARIABLE = 0xbd;

    /// <summary>command: set lane area detector (e2) variable, not used yet</summary>
    public const int CMD_SET_LANEAREA_VARIABLE = 0xcd;

    /// <summary>command: subscribe lane area detector (e2) variable</summary>
    public const int CMD_SUBSCRIBE_LANEAREA_VARIABLE = 0xdd;

    /// <summary>response: subscribe lane area detector (e2) variable</summary>
    public const int RESPONSE_SUBSCRIBE_LANEAREA_VARIABLE = 0xed;

    /// <summary>command: subscribe person context</summary>
    public const int CMD_SUBSCRIBE_PERSON_CONTEXT = 0x8e;

    /// <summary>response: subscribe person context</summary>
    public const int RESPONSE_SUBSCRIBE_PERSON_CONTEXT = 0x9e;

    /// <summary>command: get person variable</summary>
    public const int CMD_GET_PERSON_VARIABLE = 0xae;

    /// <summary>response: get person variable</summary>
    public const int RESPONSE_GET_PERSON_VARIABLE = 0xbe;

    /// <summary>command: set person variable</summary>
    public const int CMD_SET_PERSON_VARIABLE = 0xce;

    /// <summary>command: subscribe person variable</summary>
    public const int CMD_SUBSCRIBE_PERSON_VARIABLE = 0xde;

    /// <summary>response: subscribe person variable</summary>
    public const int RESPONSE_SUBSCRIBE_PERSON_VARIABLE = 0xee;

    /// <summary>command: subscribe busstop context</summary>
    public const int CMD_SUBSCRIBE_BUSSTOP_CONTEXT = 0x8f;

    /// <summary>response: subscribe busstop context</summary>
    public const int RESPONSE_SUBSCRIBE_BUSSTOP_CONTEXT = 0x9f;

    /// <summary>command: get busstop variable</summary>
    public const int CMD_GET_BUSSTOP_VARIABLE = 0xaf;

    /// <summary>response: get busstop variable</summary>
    public const int RESPONSE_GET_BUSSTOP_VARIABLE = 0xbf;

    /// <summary>command: set busstop variable, not used yet</summary>
    public const int CMD_SET_BUSSTOP_VARIABLE = 0xcf;

    /// <summary>command: subscribe busstop variable</summary>
    public const int CMD_SUBSCRIBE_BUSSTOP_VARIABLE = 0xdf;

    /// <summary>response: subscribe busstop variable</summary>
    public const int RESPONSE_SUBSCRIBE_BUSSTOP_VARIABLE = 0xef;

    /// <summary>command: subscribe parkingarea context</summary>
    public const int CMD_SUBSCRIBE_PARKINGAREA_CONTEXT = 0x04;

    /// <summary>response: subscribe parkingarea context</summary>
    public const int RESPONSE_SUBSCRIBE_PARKINGAREA_CONTEXT = 0x14;

    /// <summary>command: get parkingarea variable</summary>
    public const int CMD_GET_PARKINGAREA_VARIABLE = 0x24;

    /// <summary>response: get parkingarea variable</summary>
    public const int RESPONSE_GET_PARKINGAREA_VARIABLE = 0x34;

    /// <summary>command: set parkingarea variable</summary>
    public const int CMD_SET_PARKINGAREA_VARIABLE = 0x44;

    /// <summary>command: subscribe parkingarea variable</summary>
    public const int CMD_SUBSCRIBE_PARKINGAREA_VARIABLE = 0x54;

    /// <summary>response: subscribe parkingarea variable</summary>
    public const int RESPONSE_SUBSCRIBE_PARKINGAREA_VARIABLE = 0x64;

    /// <summary>command: subscribe chargingstation context</summary>
    public const int CMD_SUBSCRIBE_CHARGINGSTATION_CONTEXT = 0x05;

    /// <summary>response: subscribe chargingstation context</summary>
    public const int RESPONSE_SUBSCRIBE_CHARGINGSTATION_CONTEXT = 0x15;

    /// <summary>command: get chargingstation variable</summary>
    public const int CMD_GET_CHARGINGSTATION_VARIABLE = 0x25;

    /// <summary>response: get chargingstation variable</summary>
    public const int RESPONSE_GET_CHARGINGSTATION_VARIABLE = 0x35;

    /// <summary>command: set chargingstation variable</summary>
    public const int CMD_SET_CHARGINGSTATION_VARIABLE = 0x45;

    /// <summary>command: subscribe chargingstation variable</summary>
    public const int CMD_SUBSCRIBE_CHARGINGSTATION_VARIABLE = 0x55;

    /// <summary>response: subscribe chargingstation variable</summary>
    public const int RESPONSE_SUBSCRIBE_CHARGINGSTATION_VARIABLE = 0x65;

    /// <summary>command: subscribe routeprobe context</summary>
    public const int CMD_SUBSCRIBE_ROUTEPROBE_CONTEXT = 0x06;

    /// <summary>response: subscribe routeprobe context</summary>
    public const int RESPONSE_SUBSCRIBE_ROUTEPROBE_CONTEXT = 0x16;

    /// <summary>command: get routeprobe variable</summary>
    public const int CMD_GET_ROUTEPROBE_VARIABLE = 0x26;

    /// <summary>response: get routeprobe variable</summary>
    public const int RESPONSE_GET_ROUTEPROBE_VARIABLE = 0x36;

    /// <summary>command: set routeprobe variable</summary>
    public const int CMD_SET_ROUTEPROBE_VARIABLE = 0x46;

    /// <summary>command: subscribe routeprobe variable</summary>
    public const int CMD_SUBSCRIBE_ROUTEPROBE_VARIABLE = 0x56;

    /// <summary>response: subscribe routeprobe variable</summary>
    public const int RESPONSE_SUBSCRIBE_ROUTEPROBE_VARIABLE = 0x66;

    /// <summary>command: subscribe calibrator context</summary>
    public const int CMD_SUBSCRIBE_CALIBRATOR_CONTEXT = 0x07;

    /// <summary>response: subscribe calibrator context</summary>
    public const int RESPONSE_SUBSCRIBE_CALIBRATOR_CONTEXT = 0x17;

    /// <summary>command: get calibrator variable</summary>
    public const int CMD_GET_CALIBRATOR_VARIABLE = 0x27;

    /// <summary>response: get calibrator variable</summary>
    public const int RESPONSE_GET_CALIBRATOR_VARIABLE = 0x37;

    /// <summary>command: set calibrator variable</summary>
    public const int CMD_SET_CALIBRATOR_VARIABLE = 0x47;

    /// <summary>command: subscribe calibrator variable</summary>
    public const int CMD_SUBSCRIBE_CALIBRATOR_VARIABLE = 0x57;

    /// <summary>response: subscribe calibrator variable</summary>
    public const int RESPONSE_SUBSCRIBE_CALIBRATOR_VARIABLE = 0x67;

    /// <summary>command: subscribe rerouter context</summary>
    public const int CMD_SUBSCRIBE_REROUTER_CONTEXT = 0x08;

    /// <summary>response: subscribe rerouter context</summary>
    public const int RESPONSE_SUBSCRIBE_REROUTER_CONTEXT = 0x18;

    /// <summary>command: get rerouter variable</summary>
    public const int CMD_GET_REROUTER_VARIABLE = 0x28;

    /// <summary>response: get rerouter variable</summary>
    public const int RESPONSE_GET_REROUTER_VARIABLE = 0x38;

    /// <summary>command: set rerouter variable</summary>
    public const int CMD_SET_REROUTER_VARIABLE = 0x48;

    /// <summary>command: subscribe rerouter variable</summary>
    public const int CMD_SUBSCRIBE_REROUTER_VARIABLE = 0x58;

    /// <summary>response: subscribe rerouter variable</summary>
    public const int RESPONSE_SUBSCRIBE_REROUTER_VARIABLE = 0x68;

    /// <summary>command: subscribe variablespeedsign context</summary>
    public const int CMD_SUBSCRIBE_VARIABLESPEEDSIGN_CONTEXT = 0x09;

    /// <summary>response: subscribe variablespeedsign context</summary>
    public const int RESPONSE_SUBSCRIBE_VARIABLESPEEDSIGN_CONTEXT = 0x19;

    /// <summary>command: get variablespeedsign variable</summary>
    public const int CMD_GET_VARIABLESPEEDSIGN_VARIABLE = 0x29;

    /// <summary>response: get variablespeedsign variable</summary>
    public const int RESPONSE_GET_VARIABLESPEEDSIGN_VARIABLE = 0x39;

    /// <summary>command: set variablespeedsign variable</summary>
    public const int CMD_SET_VARIABLESPEEDSIGN_VARIABLE = 0x49;

    /// <summary>command: subscribe variablespeedsign variable</summary>
    public const int CMD_SUBSCRIBE_VARIABLESPEEDSIGN_VARIABLE = 0x59;

    /// <summary>response: subscribe variablespeedsign variable</summary>
    public const int RESPONSE_SUBSCRIBE_VARIABLESPEEDSIGN_VARIABLE = 0x69;

    /// <summary>command: subscribe meandata context</summary>
    public const int CMD_SUBSCRIBE_MEANDATA_CONTEXT = 0x0a;

    /// <summary>response: subscribe meandata context</summary>
    public const int RESPONSE_SUBSCRIBE_MEANDATA_CONTEXT = 0x1a;

    /// <summary>command: get meandata variable</summary>
    public const int CMD_GET_MEANDATA_VARIABLE = 0x2a;

    /// <summary>response: get meandata variable</summary>
    public const int RESPONSE_GET_MEANDATA_VARIABLE = 0x3a;

    /// <summary>command: set meandata variable, not used yet</summary>
    public const int CMD_SET_MEANDATA_VARIABLE = 0x4a;

    /// <summary>command: subscribe meandata variable</summary>
    public const int CMD_SUBSCRIBE_MEANDATA_VARIABLE = 0x5a;

    /// <summary>response: subscribe meandata variable</summary>
    public const int RESPONSE_SUBSCRIBE_MEANDATA_VARIABLE = 0x6a;

    /// <summary>command: subscribe overheadwire context</summary>
    public const int CMD_SUBSCRIBE_OVERHEADWIRE_CONTEXT = 0x0b;

    /// <summary>response: subscribe overheadwire context</summary>
    public const int RESPONSE_SUBSCRIBE_OVERHEADWIRE_CONTEXT = 0x1b;

    /// <summary>command: get overheadwire variable</summary>
    public const int CMD_GET_OVERHEADWIRE_VARIABLE = 0x2b;

    /// <summary>response: get overheadwire variable</summary>
    public const int RESPONSE_GET_OVERHEADWIRE_VARIABLE = 0x3b;

    /// <summary>command: set overheadwire variable</summary>
    public const int CMD_SET_OVERHEADWIRE_VARIABLE = 0x4b;

    /// <summary>command: subscribe overheadwire variable</summary>
    public const int CMD_SUBSCRIBE_OVERHEADWIRE_VARIABLE = 0x5b;

    /// <summary>response: subscribe overheadwire variable</summary>
    public const int RESPONSE_SUBSCRIBE_OVERHEADWIRE_VARIABLE = 0x6b;

    // ---- POSITION REPRESENTATIONS ----

    /// <summary>Position in geo-coordinates</summary>
    public const int POSITION_LON_LAT = 0x00;

    /// <summary>2D cartesian coordinates</summary>
    public const int POSITION_2D = 0x01;

    /// <summary>Position in geo-coordinates with altitude</summary>
    public const int POSITION_LON_LAT_ALT = 0x02;

    /// <summary>3D cartesian coordinates</summary>
    public const int POSITION_3D = 0x03;

    /// <summary>Position on road map</summary>
    public const int POSITION_ROADMAP = 0x04;

    // ---- DATA TYPES ----

    /// <summary>Polygon (2*n doubles)</summary>
    public const int TYPE_POLYGON = 0x06;

    /// <summary>unsigned byte</summary>
    public const int TYPE_UBYTE = 0x07;

    /// <summary>signed byte</summary>
    public const int TYPE_BYTE = 0x08;

    /// <summary>32 bit signed integer</summary>
    public const int TYPE_INTEGER = 0x09;

    /// <summary>double precision float</summary>
    public const int TYPE_DOUBLE = 0x0B;

    /// <summary>8 bit ASCII string</summary>
    public const int TYPE_STRING = 0x0C;

    /// <summary>list of strings</summary>
    public const int TYPE_STRINGLIST = 0x0E;

    /// <summary>compound object</summary>
    public const int TYPE_COMPOUND = 0x0F;

    /// <summary>list of double precision floats</summary>
    public const int TYPE_DOUBLELIST = 0x10;

    /// <summary>color (four ubytes)</summary>
    public const int TYPE_COLOR = 0x11;

    // ---- RESULT TYPES ----

    /// <summary>result type: Ok</summary>
    public const int RTYPE_OK = 0x00;

    /// <summary>result type: not implemented</summary>
    public const int RTYPE_NOTIMPLEMENTED = 0x01;

    /// <summary>result type: error</summary>
    public const int RTYPE_ERR = 0xFF;

    // ---- special return or parameter values ----

    /// <summary>return value for invalid queries (especially vehicle is not on the road), see Position::INVALID</summary>
    public const double INVALID_DOUBLE_VALUE = -1073741824.0;

    /// <summary>return value for invalid queries (especially vehicle is not on the road), see Position::INVALID</summary>
    public const int INVALID_INT_VALUE = -1073741824;

    /// <summary>maximum value for client ordering (2 ^ 30)</summary>
    public const int MAX_ORDER = 1073741824;

    /// <summary>default number of connection attempts</summary>
    public const int DEFAULT_NUM_RETRIES = 60;

    // ---- DIFFERENT DISTANCE REQUESTS ----

    /// <summary>air distance</summary>
    public const int REQUEST_AIRDIST = 0x00;

    /// <summary>driving distance</summary>
    public const int REQUEST_DRIVINGDIST = 0x01;

    // ---- VEHICLE REMOVAL REASONS ----

    /// <summary>vehicle started teleport</summary>
    public const int REMOVE_TELEPORT = 0x00;

    /// <summary>vehicle removed while parking</summary>
    public const int REMOVE_PARKING = 0x01;

    /// <summary>vehicle arrived</summary>
    public const int REMOVE_ARRIVED = 0x02;

    /// <summary>vehicle was vaporized</summary>
    public const int REMOVE_VAPORIZED = 0x03;

    /// <summary>vehicle finished route during teleport</summary>
    public const int REMOVE_TELEPORT_ARRIVED = 0x04;

    // ---- VEHICLE MOVE REASONS ----

    /// <summary>infer reason from move distance</summary>
    public const int MOVE_AUTOMATIC = 0x00;

    /// <summary>vehicle teleports to another location</summary>
    public const int MOVE_TELEPORT = 0x01;

    /// <summary>vehicle moved normally</summary>
    public const int MOVE_NORMAL = 0x02;

    // ---- PERSON/CONTAINER STAGES ----

    /// <summary>person / container stopping</summary>
    public const int STAGE_WAITING_FOR_DEPART = 0x00;

    /// <summary>person / container stopping</summary>
    public const int STAGE_WAITING = 0x01;

    /// <summary>person walking</summary>
    public const int STAGE_WALKING = 0x02;

    /// <summary>person riding / container being transported</summary>
    public const int STAGE_DRIVING = 0x03;

    /// <summary>person accessing stopping place</summary>
    public const int STAGE_ACCESS = 0x04;

    /// <summary>stage for encoding abstract travel demand</summary>
    public const int STAGE_TRIP = 0x05;

    /// <summary>person / container transhiping</summary>
    public const int STAGE_TRANSHIP = 0x06;

    // ---- Stop Flags ----

    /// <summary>TraCI identifier <c>STOP_DEFAULT</c>.</summary>
    public const int STOP_DEFAULT = 0x00;

    /// <summary>TraCI identifier <c>STOP_PARKING</c>.</summary>
    public const int STOP_PARKING = 0x01;

    /// <summary>TraCI identifier <c>STOP_TRIGGERED</c>.</summary>
    public const int STOP_TRIGGERED = 0x02;

    /// <summary>TraCI identifier <c>STOP_CONTAINER_TRIGGERED</c>.</summary>
    public const int STOP_CONTAINER_TRIGGERED = 0x04;

    /// <summary>TraCI identifier <c>STOP_BUS_STOP</c>.</summary>
    public const int STOP_BUS_STOP = 0x08;

    /// <summary>TraCI identifier <c>STOP_CONTAINER_STOP</c>.</summary>
    public const int STOP_CONTAINER_STOP = 0x10;

    /// <summary>TraCI identifier <c>STOP_CHARGING_STATION</c>.</summary>
    public const int STOP_CHARGING_STATION = 0x20;

    /// <summary>TraCI identifier <c>STOP_PARKING_AREA</c>.</summary>
    public const int STOP_PARKING_AREA = 0x40;

    /// <summary>TraCI identifier <c>STOP_OVERHEAD_WIRE</c>.</summary>
    public const int STOP_OVERHEAD_WIRE = 0x80;

    // ---- Departure Flags (corresponding value from DepartDefinition, DepartLaneDefinition with a minus) ----

    /// <summary>TraCI identifier <c>DEPARTFLAG_TRIGGERED</c>.</summary>
    public const int DEPARTFLAG_TRIGGERED = -0x01;

    /// <summary>TraCI identifier <c>DEPARTFLAG_CONTAINER_TRIGGERED</c>.</summary>
    public const int DEPARTFLAG_CONTAINER_TRIGGERED = -0x02;

    /// <summary>TraCI identifier <c>DEPARTFLAG_NOW</c>.</summary>
    public const int DEPARTFLAG_NOW = -0x03;

    /// <summary>TraCI identifier <c>DEPARTFLAG_SPLIT</c>.</summary>
    public const int DEPARTFLAG_SPLIT = -0x04;

    /// <summary>TraCI identifier <c>DEPARTFLAG_BEGIN</c>.</summary>
    public const int DEPARTFLAG_BEGIN = -0x05;

    /// <summary>TraCI identifier <c>DEPARTFLAG_SPEED_RANDOM</c>.</summary>
    public const int DEPARTFLAG_SPEED_RANDOM = -0x02;

    /// <summary>TraCI identifier <c>DEPARTFLAG_SPEED_MAX</c>.</summary>
    public const int DEPARTFLAG_SPEED_MAX = -0x03;

    /// <summary>TraCI identifier <c>DEPARTFLAG_LANE_RANDOM</c>.</summary>
    public const int DEPARTFLAG_LANE_RANDOM = -0x02;

    /// <summary>TraCI identifier <c>DEPARTFLAG_LANE_FREE</c>.</summary>
    public const int DEPARTFLAG_LANE_FREE = -0x03;

    /// <summary>TraCI identifier <c>DEPARTFLAG_LANE_ALLOWED_FREE</c>.</summary>
    public const int DEPARTFLAG_LANE_ALLOWED_FREE = -0x04;

    /// <summary>TraCI identifier <c>DEPARTFLAG_LANE_BEST_FREE</c>.</summary>
    public const int DEPARTFLAG_LANE_BEST_FREE = -0x05;

    /// <summary>TraCI identifier <c>DEPARTFLAG_LANE_FIRST_ALLOWED</c>.</summary>
    public const int DEPARTFLAG_LANE_FIRST_ALLOWED = -0x06;

    /// <summary>TraCI identifier <c>DEPARTFLAG_POS_RANDOM</c>.</summary>
    public const int DEPARTFLAG_POS_RANDOM = -0x02;

    /// <summary>TraCI identifier <c>DEPARTFLAG_POS_FREE</c>.</summary>
    public const int DEPARTFLAG_POS_FREE = -0x03;

    /// <summary>TraCI identifier <c>DEPARTFLAG_POS_BASE</c>.</summary>
    public const int DEPARTFLAG_POS_BASE = -0x04;

    /// <summary>TraCI identifier <c>DEPARTFLAG_POS_LAST</c>.</summary>
    public const int DEPARTFLAG_POS_LAST = -0x05;

    /// <summary>TraCI identifier <c>DEPARTFLAG_POS_RANDOM_FREE</c>.</summary>
    public const int DEPARTFLAG_POS_RANDOM_FREE = -0x06;

    /// <summary>TraCI identifier <c>ARRIVALFLAG_LANE_CURRENT</c>.</summary>
    public const int ARRIVALFLAG_LANE_CURRENT = -0x02;

    /// <summary>TraCI identifier <c>ARRIVALFLAG_SPEED_CURRENT</c>.</summary>
    public const int ARRIVALFLAG_SPEED_CURRENT = -0x02;

    /// <summary>TraCI identifier <c>ARRIVALFLAG_POS_RANDOM</c>.</summary>
    public const int ARRIVALFLAG_POS_RANDOM = -0x02;

    /// <summary>TraCI identifier <c>ARRIVALFLAG_POS_MAX</c>.</summary>
    public const int ARRIVALFLAG_POS_MAX = -0x03;

    // ---- Routing modes ----

    /// <summary>use custom weights if available, fall back to loaded weights and then to free-flow speed</summary>
    public const int ROUTING_MODE_DEFAULT = 0x00;

    /// <summary>use aggregated travel times from device.rerouting</summary>
    public const int ROUTING_MODE_AGGREGATED = 0x01;

    /// <summary>use loaded efforts</summary>
    public const int ROUTING_MODE_EFFORT = 0x02;

    /// <summary>use combined costs</summary>
    public const int ROUTING_MODE_COMBINED = 0x03;

    /// <summary>use aggregated travel times from device.rerouting enriched with custom weights</summary>
    public const int ROUTING_MODE_AGGREGATED_CUSTOM = 0x04;

    /// <summary>when this bit is set, routing does not consider temporary permission changes (i.e. from rerouters) note: can be combined with either one of the other modes (bitwise)</summary>
    public const int ROUTING_MODE_IGNORE_TRANSIENT_PERMISSIONS = 0x08;

    // ---- Traffic light types ----

    /// <summary>TraCI identifier <c>TRAFFICLIGHT_TYPE_STATIC</c>.</summary>
    public const int TRAFFICLIGHT_TYPE_STATIC = 0x00;

    /// <summary>TraCI identifier <c>TRAFFICLIGHT_TYPE_ACTUATED</c>.</summary>
    public const int TRAFFICLIGHT_TYPE_ACTUATED = 0x03;

    /// <summary>TraCI identifier <c>TRAFFICLIGHT_TYPE_NEMA</c>.</summary>
    public const int TRAFFICLIGHT_TYPE_NEMA = 0x04;

    /// <summary>TraCI identifier <c>TRAFFICLIGHT_TYPE_DELAYBASED</c>.</summary>
    public const int TRAFFICLIGHT_TYPE_DELAYBASED = 0x05;

    // ---- Lane change directions ----

    /// <summary>TraCI identifier <c>LANECHANGE_LEFT</c>.</summary>
    public const int LANECHANGE_LEFT = 0x01;

    /// <summary>TraCI identifier <c>LANECHANGE_RIGHT</c>.</summary>
    public const int LANECHANGE_RIGHT = -0x01;

    // ---- FILTER TYPES (for context subscription filters) ----

    /// <summary>Reset all filters</summary>
    public const int FILTER_TYPE_NONE = 0x00;

    /// <summary>Filter by list of lanes relative to ego vehicle</summary>
    public const int FILTER_TYPE_LANES = 0x01;

    /// <summary>Exclude vehicles on opposite (and other) lanes from context subscription result</summary>
    public const int FILTER_TYPE_NOOPPOSITE = 0x02;

    /// <summary>Specify maximal downstream distance for vehicles in context subscription result</summary>
    public const int FILTER_TYPE_DOWNSTREAM_DIST = 0x03;

    /// <summary>Specify maximal upstream distance for vehicles in context subscription result</summary>
    public const int FILTER_TYPE_UPSTREAM_DIST = 0x04;

    /// <summary>Only return leader and follower on the specified lanes in context subscription result</summary>
    public const int FILTER_TYPE_LEAD_FOLLOW = 0x05;

    /// <summary>Only return foes on upcoming junctions in context subscription result</summary>
    public const int FILTER_TYPE_TURN = 0x07;

    /// <summary>Only return vehicles of the given vClass in context subscription result</summary>
    public const int FILTER_TYPE_VCLASS = 0x08;

    /// <summary>Only return vehicles of the given vType in context subscription result</summary>
    public const int FILTER_TYPE_VTYPE = 0x09;

    /// <summary>Only return vehicles within field of vision in context subscription result</summary>
    public const int FILTER_TYPE_FIELD_OF_VISION = 0x0A;

    /// <summary>Only return vehicles within the given lateral distance in context subscription result</summary>
    public const int FILTER_TYPE_LATERAL_DIST = 0x0B;

    // ---- VARIABLE TYPES (for CMD_GET_*_VARIABLE) ----

    /// <summary>list of instances' ids (get: all)</summary>
    public const int TRACI_ID_LIST = 0x00;

    /// <summary>count of instances (get: all)</summary>
    public const int ID_COUNT = 0x01;

    /// <summary>subscribe object variables (get: all)</summary>
    public const int AUTOMATIC_VARIABLES_SUBSCRIPTION = 0x02;

    /// <summary>subscribe context variables (get: all)</summary>
    public const int AUTOMATIC_CONTEXT_SUBSCRIPTION = 0x03;

    /// <summary>generic attributes (get/set: all)</summary>
    public const int GENERIC_ATTRIBUTE = 0x03;

    /// <summary>last step vehicle number (get: induction loops, multi-entry/multi-exit detector, lanes, edges)</summary>
    public const int LAST_STEP_VEHICLE_NUMBER = 0x10;

    /// <summary>last step vehicle number (get: induction loops, multi-entry/multi-exit detector, lanes, edges)</summary>
    public const int LAST_STEP_MEAN_SPEED = 0x11;

    /// <summary>last step vehicle list (get: induction loops, multi-entry/multi-exit detector, lanes, edges)</summary>
    public const int LAST_STEP_VEHICLE_ID_LIST = 0x12;

    /// <summary>last step occupancy (get: e1, e2, lanes, edges)</summary>
    public const int LAST_STEP_OCCUPANCY = 0x13;

    /// <summary>last step vehicle halting number (get: e2, e3, lanes, edges)</summary>
    public const int LAST_STEP_VEHICLE_HALTING_NUMBER = 0x14;

    /// <summary>upstream junction (edges)</summary>
    public const int FROM_JUNCTION = 0x7b;

    /// <summary>downstream junction (edges)</summary>
    public const int TO_JUNCTION = 0x7c;

    /// <summary>incoming edges (junction)</summary>
    public const int INCOMING_EDGES = 0x7b;

    /// <summary>outgoing edges (junction)</summary>
    public const int OUTGOING_EDGES = 0x7c;

    /// <summary>get bidi object (edges, lanes)</summary>
    public const int VAR_BIDI = 0x7f;

    /// <summary>last step mean vehicle length (get: induction loops, lanes, edges)</summary>
    public const int LAST_STEP_LENGTH = 0x15;

    /// <summary>last step time since last detection (get: induction loops)</summary>
    public const int LAST_STEP_TIME_SINCE_DETECTION = 0x16;

    /// <summary>entry times (get: inductionloop)</summary>
    public const int LAST_STEP_VEHICLE_DATA = 0x17;

    /// <summary>get aggregated occupancy (get: inductionloop, e2)</summary>
    public const int VAR_INTERVAL_OCCUPANCY = 0x23;

    /// <summary>get aggregated speed (get: inductionloop, e2)</summary>
    public const int VAR_INTERVAL_SPEED = 0x24;

    /// <summary>get aggregated vehicle count (get: inductionloop, e2)</summary>
    public const int VAR_INTERVAL_NUMBER = 0x25;

    /// <summary>get aggregated vehicle ids (get: inductionloop)</summary>
    public const int VAR_INTERVAL_IDS = 0x26;

    /// <summary>get aggregated vehicle ids (get: inductionloop)</summary>
    public const int VAR_INTERVAL_TIMELOSS = 0x34;

    /// <summary>get aggregated speed of last written interval (get: inductionloop, e2)</summary>
    public const int VAR_LAST_INTERVAL_OCCUPANCY = 0x27;

    /// <summary>get aggregated occupancy of last written interval (get: inductionloop, e2)</summary>
    public const int VAR_LAST_INTERVAL_SPEED = 0x28;

    /// <summary>get aggregated vehicle count of last written interval (get: inductionloop, e2)</summary>
    public const int VAR_LAST_INTERVAL_NUMBER = 0x29;

    /// <summary>get aggregated vehicle ids of last written interval (get: inductionloop)</summary>
    public const int VAR_LAST_INTERVAL_IDS = 0x2a;

    /// <summary>last step jam length in vehicles (get: e2)</summary>
    public const int JAM_LENGTH_VEHICLE = 0x18;

    /// <summary>last step jam length in meters (get: e2)</summary>
    public const int JAM_LENGTH_METERS = 0x19;

    /// <summary>get aggregated jam length (e2)</summary>
    public const int VAR_INTERVAL_MAX_JAM_LENGTH_METERS = 0x32;

    /// <summary>get prior aggregated jam length (e2)</summary>
    public const int VAR_LAST_INTERVAL_MAX_JAM_LENGTH_METERS = 0x33;

    /// <summary>last interval travel time (get: e3)</summary>
    public const int VAR_LAST_INTERVAL_TRAVELTIME = 0x58;

    /// <summary>last step vehicle halting number (get: e3)</summary>
    public const int VAR_LAST_INTERVAL_MEAN_HALTING_NUMBER = 0x20;

    /// <summary>last interval vehicle count(get: e3)</summary>
    public const int VAR_LAST_INTERVAL_VEHICLE_NUMBER = 0x21;

    /// <summary>last interval vehicle count(get: e2, e3)</summary>
    public const int VAR_LAST_INTERVAL_TIMELOSS = 0x35;

    /// <summary>last interval vehicle count(set, get: e1, e2)</summary>
    public const int VAR_VIRTUAL_DETECTION = 0x22;

    /// <summary>last step person list (get: edges, vehicles)</summary>
    public const int LAST_STEP_PERSON_ID_LIST = 0x1a;

    /// <summary>full name (get: edges, simulation, trafficlight)</summary>
    public const int VAR_NAME = 0x1b;

    /// <summary>carFollowModel function followSpeed (get: vehicle)</summary>
    public const int VAR_FOLLOW_SPEED = 0x1c;

    /// <summary>carFollowModel function stopSpeed (get: vehicle)</summary>
    public const int VAR_STOP_SPEED = 0x1d;

    /// <summary>carFollowModel function getSecureGap (get: vehicle)</summary>
    public const int VAR_SECURE_GAP = 0x1e;

    /// <summary>estimated (depart) delay for next stop (get: vehicle)</summary>
    public const int VAR_STOP_DELAY = 0x1f;

    /// <summary>estimated arrival delay for next stop (get: vehicle)</summary>
    public const int VAR_STOP_ARRIVALDELAY = 0x22;

    /// <summary>collected timeLoss since departure (get: vehicle, e3)</summary>
    public const int VAR_TIMELOSS = 0x8c;

    /// <summary>begin time(get: calibrator)</summary>
    public const int VAR_BEGIN = 0x1c;

    /// <summary>end time(get: calibrator, simulation)</summary>
    public const int VAR_END = 0x1d;

    /// <summary>vtype list (get: calibrator)</summary>
    public const int VAR_VTYPES = 0x1e;

    /// <summary>vehicles per hour (get: calibrator)</summary>
    public const int VAR_VEHSPERHOUR = 0x13;

    /// <summary>passed vehicle count (get: calibrator)</summary>
    public const int VAR_PASSED = 0x14;

    /// <summary>inserted vehicle count (get: calibrator)</summary>
    public const int VAR_INSERTED = 0x15;

    /// <summary>removed vehicle count (get: calibrator)</summary>
    public const int VAR_REMOVED = 0x16;

    /// <summary>routeProbe id (get: calibrator)</summary>
    public const int VAR_ROUTE_PROBE = 0x17;

    /// <summary>routeProbe id (get: calibrator)</summary>
    public const int CMD_SET_FLOW = 0x18;

    /// <summary>traffic light states, encoded as rRgGyYoO tuple (get: traffic lights)</summary>
    public const int TL_RED_YELLOW_GREEN_STATE = 0x20;

    /// <summary>index of the phase (set: traffic lights)</summary>
    public const int TL_PHASE_INDEX = 0x22;

    /// <summary>traffic light program (set: traffic lights)</summary>
    public const int TL_PROGRAM = 0x23;

    /// <summary>phase duration (set: traffic lights)</summary>
    public const int TL_PHASE_DURATION = 0x24;

    /// <summary>vehicles that block passing the given signal (get: traffic lights)</summary>
    public const int TL_BLOCKING_VEHICLES = 0x25;

    /// <summary>controlled lanes (get: traffic lights)</summary>
    public const int TL_CONTROLLED_LANES = 0x26;

    /// <summary>controlled links (get: traffic lights)</summary>
    public const int TL_CONTROLLED_LINKS = 0x27;

    /// <summary>index of the current phase (get: traffic lights)</summary>
    public const int TL_CURRENT_PHASE = 0x28;

    /// <summary>name of the current program (get: traffic lights)</summary>
    public const int TL_CURRENT_PROGRAM = 0x29;

    /// <summary>vehicles that also wish to pass the given signal (get: traffic lights)</summary>
    public const int TL_RIVAL_VEHICLES = 0x30;

    /// <summary>vehicles that also wish to pass the given signal and have higher priority (get: traffic lights)</summary>
    public const int TL_PRIORITY_VEHICLES = 0x31;

    /// <summary>controlled junctions (get: traffic lights)</summary>
    public const int TL_CONTROLLED_JUNCTIONS = 0x2a;

    /// <summary>complete definition (get: traffic lights)</summary>
    public const int TL_COMPLETE_DEFINITION_RYG = 0x2b;

    /// <summary>complete program (set: traffic lights)</summary>
    public const int TL_COMPLETE_PROGRAM_RYG = 0x2c;

    /// <summary>assumed time to next switch (get: traffic lights)</summary>
    public const int TL_NEXT_SWITCH = 0x2d;

    /// <summary>add/get rail signal constraints</summary>
    public const int TL_CONSTRAINT = 0x2f;

    /// <summary>switch order of trains encoded in rail signal constraints (set: traffic lights)</summary>
    public const int TL_CONSTRAINT_SWAP = 0x32;

    /// <summary>add/get rail signal constraints by foeSignal (get: traffic lights)</summary>
    public const int TL_CONSTRAINT_BYFOE = 0x34;

    /// <summary>add/get rail signal constraints by foeSignal (set: traffic lights)</summary>
    public const int TL_CONSTRAINT_REMOVE = 0x35;

    /// <summary>update rail signal constraints by vehID (set: traffic lights)</summary>
    public const int TL_CONSTRAINT_UPDATE = 0x36;

    /// <summary>add rail signal constraint (set: traffic lights)</summary>
    public const int TL_CONSTRAINT_ADD = 0x37;

    /// <summary>retrieve duration spent in the current phase (get: traffic lights)</summary>
    public const int TL_SPENT_DURATION = 0x38;

    /// <summary>outgoing link number (get: lanes)</summary>
    public const int LANE_LINK_NUMBER = 0x30;

    /// <summary>id of parent edge (get: lanes)</summary>
    public const int LANE_EDGE_ID = 0x31;

    /// <summary>outgoing link definitions (get: lanes)</summary>
    public const int LANE_LINKS = 0x33;

    /// <summary>list of allowed vehicle classes (get&amp;set: lanes)</summary>
    public const int LANE_ALLOWED = 0x34;

    /// <summary>list of not allowed vehicle classes (get&amp;set: lanes)</summary>
    public const int LANE_DISALLOWED = 0x35;

    /// <summary>list of allowed vehicle classes for lane changes (get&amp;set: lanes)</summary>
    public const int LANE_CHANGES = 0x3c;

    /// <summary>list of foe lanes (get: lane, vehicle)</summary>
    public const int VAR_FOES = 0x37;

    /// <summary>slope (get: edge, lane, vehicle, person)</summary>
    public const int VAR_SLOPE = 0x36;

    /// <summary>speed (get: vehicle)</summary>
    public const int VAR_SPEED = 0x40;

    /// <summary>adapt previous speed (set: vehicle)</summary>
    public const int VAR_PREV_SPEED = 0x3c;

    /// <summary>friction coefficient (set&amp;get: lanes, set: edges)</summary>
    public const int VAR_FRICTION = 0x3d;

    /// <summary>lateral speed (get: vehicle)</summary>
    public const int VAR_SPEED_LAT = 0x32;

    /// <summary>maximum allowed/possible speed (get: vehicle types, lanes, set: edges, lanes)</summary>
    public const int VAR_MAXSPEED = 0x41;

    /// <summary>position (2D) (get: vehicle, poi, inductionloop, lane area detector, multi-entry/multi-exit detector; set: poi)</summary>
    public const int VAR_POSITION = 0x42;

    /// <summary>position (2D) (get: multi-entry/multi-exit detector)</summary>
    public const int VAR_EXIT_POSITIONS = 0x43;

    /// <summary>position (3D) (get: vehicle, poi, set: poi)</summary>
    public const int VAR_POSITION3D = 0x39;

    /// <summary>angle (get: vehicle, edge, lane, poi, gui; set: poi, gui)</summary>
    public const int VAR_ANGLE = 0x43;

    /// <summary>length (get: vehicle types, lanes, lane area detector, set: lanes)</summary>
    public const int VAR_LENGTH = 0x44;

    /// <summary>color (get: vehicles, vehicle types, polygons, pois)</summary>
    public const int VAR_COLOR = 0x45;

    /// <summary>max. acceleration (get: vehicles, vehicle types)</summary>
    public const int VAR_ACCEL = 0x46;

    /// <summary>max. comfortable deceleration (get: vehicles, vehicle types)</summary>
    public const int VAR_DECEL = 0x47;

    /// <summary>max. (physically possible) deceleration (get: vehicles, vehicle types)</summary>
    public const int VAR_EMERGENCY_DECEL = 0x7b;

    /// <summary>apparent deceleration (get: vehicles, vehicle types)</summary>
    public const int VAR_APPARENT_DECEL = 0x7c;

    /// <summary>action step length (get: vehicles, vehicle types)</summary>
    public const int VAR_ACTIONSTEPLENGTH = 0x7d;

    /// <summary>last action time (get: vehicles)</summary>
    public const int VAR_LASTACTIONTIME = 0x7f;

    /// <summary>driver's desired headway (get: vehicle types)</summary>
    public const int VAR_TAU = 0x48;

    /// <summary>vehicle class (get: vehicle types)</summary>
    public const int VAR_VEHICLECLASS = 0x49;

    /// <summary>emission class (get: vehicle types)</summary>
    public const int VAR_EMISSIONCLASS = 0x4a;

    /// <summary>shape class (get: vehicle types)</summary>
    public const int VAR_SHAPECLASS = 0x4b;

    /// <summary>minimum gap (get: vehicle types)</summary>
    public const int VAR_MINGAP = 0x4c;

    /// <summary>width (get: vehicle types, lanes, polygons, poi)</summary>
    public const int VAR_WIDTH = 0x4d;

    /// <summary>shape (get: polygons)</summary>
    public const int VAR_SHAPE = 0x4e;

    /// <summary>type id (get: vehicles, polygons, pois)</summary>
    public const int VAR_TYPE = 0x4f;

    /// <summary>road id (get: vehicles)</summary>
    public const int VAR_ROAD_ID = 0x50;

    /// <summary>lane id (get: vehicles, inductionloop, lane area detector)</summary>
    public const int VAR_LANE_ID = 0x51;

    /// <summary>lane index (get: vehicle, edge)</summary>
    public const int VAR_LANE_INDEX = 0x52;

    /// <summary>segment id (get: vehicle)</summary>
    public const int VAR_SEGMENT_ID = 0xa1;

    /// <summary>segment index (get: vehicle)</summary>
    public const int VAR_SEGMENT_INDEX = 0xa2;

    /// <summary>route id (get &amp; set: vehicles)</summary>
    public const int VAR_ROUTE_ID = 0x53;

    /// <summary>edges (get: routes, vehicles)</summary>
    public const int VAR_EDGES = 0x54;

    /// <summary>filled? (set: vehicles)</summary>
    public const int VAR_STOP_PARAMETER = 0x55;

    /// <summary>lanes (get: variablespeedsign, multi-entry/multi-exit detector)</summary>
    public const int VAR_LANES = 0x30;

    /// <summary>exit lanes (get: multi-entry/multi-exit detector)</summary>
    public const int VAR_EXIT_LANES = 0x31;

    /// <summary>update bestLanes (set: vehicle)</summary>
    public const int VAR_UPDATE_BESTLANES = 0x6a;

    /// <summary>filled? (get: polygons)</summary>
    public const int VAR_FILL = 0x55;

    /// <summary>get/set image file (poi, poly, vehicle, person, simulation)</summary>
    public const int VAR_IMAGEFILE = 0x93;

    /// <summary>position (1D along lane) (get: vehicle)</summary>
    public const int VAR_LANEPOSITION = 0x56;

    /// <summary>route (set: vehicles)</summary>
    public const int VAR_ROUTE = 0x57;

    /// <summary>travel time information (get&amp;set: vehicle)</summary>
    public const int VAR_EDGE_TRAVELTIME = 0x58;

    /// <summary>effort information (get&amp;set: vehicle)</summary>
    public const int VAR_EDGE_EFFORT = 0x59;

    /// <summary>last step travel time (get: edge, lane, e3)</summary>
    public const int VAR_CURRENT_TRAVELTIME = 0x5a;

    /// <summary>signals state (get/set: vehicle)</summary>
    public const int VAR_SIGNALS = 0x5b;

    /// <summary>vehicle: new lane/position along (set: vehicle)</summary>
    public const int VAR_MOVE_TO = 0x5c;

    /// <summary>polygon: add dynamics (set: polygon)</summary>
    public const int VAR_ADD_DYNAMICS = 0x5c;

    /// <summary>vehicle: highlight (set: vehicle, poi)</summary>
    public const int VAR_HIGHLIGHT = 0x6c;

    /// <summary>driver imperfection (set: vehicle)</summary>
    public const int VAR_IMPERFECTION = 0x5d;

    /// <summary>speed factor (set: vehicle)</summary>
    public const int VAR_SPEED_FACTOR = 0x5e;

    /// <summary>speed deviation (set: vehicle)</summary>
    public const int VAR_SPEED_DEVIATION = 0x5f;

    /// <summary>routing mode (get/set: vehicle)</summary>
    public const int VAR_ROUTING_MODE = 0x89;

    /// <summary>speed without TraCI influence (get: vehicle)</summary>
    public const int VAR_SPEED_WITHOUT_TRACI = 0xb1;

    /// <summary>best lanes (get: vehicle)</summary>
    public const int VAR_BEST_LANES = 0xb2;

    /// <summary>how speed is set (set: vehicle)</summary>
    public const int VAR_SPEEDSETMODE = 0xb3;

    /// <summary>move vehicle to explicit (remote controlled) position (set: vehicle)</summary>
    public const int MOVE_TO_XY = 0xb4;

    /// <summary>is the vehicle stopped, and if so parked and/or triggered? value = stopped + 2 * parking + 4 * triggered</summary>
    public const int VAR_STOPSTATE = 0xb5;

    /// <summary>how lane changing is performed (get/set: vehicle)</summary>
    public const int VAR_LANECHANGE_MODE = 0xb6;

    /// <summary>maximum speed regarding max speed on the current lane and speed factor (get: vehicle)</summary>
    public const int VAR_ALLOWED_SPEED = 0xb7;

    /// <summary>position (1D lateral position relative to center of the current lane) (get: vehicle)</summary>
    public const int VAR_LANEPOSITION_LAT = 0xb8;

    /// <summary>get/set prefered lateral alignment within the lane (vehicle)</summary>
    public const int VAR_LATALIGNMENT = 0xb9;

    /// <summary>get/set maximum lateral speed (vehicle, vtypes)</summary>
    public const int VAR_MAXSPEED_LAT = 0xba;

    /// <summary>get/set minimum lateral gap (vehicle, vtypes)</summary>
    public const int VAR_MINGAP_LAT = 0xbb;

    /// <summary>get/set vehicle height (vehicle, vtypes, poi)</summary>
    public const int VAR_HEIGHT = 0xbc;

    /// <summary>get/set mass (vehicle, vtype)</summary>
    public const int VAR_MASS = 0xc8;

    /// <summary>get/set vehicle line</summary>
    public const int VAR_LINE = 0xbd;

    /// <summary>get/set vehicle via</summary>
    public const int VAR_VIA = 0xbe;

    /// <summary>get (lane change relevant) neighboring vehicles (vehicles)</summary>
    public const int VAR_NEIGHBORS = 0xbf;

    /// <summary>current CO2 emission of a node (get: vehicle, lane, edge)</summary>
    public const int VAR_CO2EMISSION = 0x60;

    /// <summary>current CO emission of a node (get: vehicle, lane, edge)</summary>
    public const int VAR_COEMISSION = 0x61;

    /// <summary>current HC emission of a node (get: vehicle, lane, edge)</summary>
    public const int VAR_HCEMISSION = 0x62;

    /// <summary>current PMx emission of a node (get: vehicle, lane, edge)</summary>
    public const int VAR_PMXEMISSION = 0x63;

    /// <summary>current NOx emission of a node (get: vehicle, lane, edge)</summary>
    public const int VAR_NOXEMISSION = 0x64;

    /// <summary>current fuel consumption of a node (get: vehicle, lane, edge)</summary>
    public const int VAR_FUELCONSUMPTION = 0x65;

    /// <summary>current noise emission of a node (get: vehicle, lane, edge)</summary>
    public const int VAR_NOISEEMISSION = 0x66;

    /// <summary>current person number (get: vehicle, trafficlight)</summary>
    public const int VAR_PERSON_NUMBER = 0x67;

    /// <summary>person capacity (vehicle, vehicle type)</summary>
    public const int VAR_PERSON_CAPACITY = 0x38;

    /// <summary>departure time (vehicle, person)</summary>
    public const int VAR_DEPARTURE = 0x3a;

    /// <summary>departure delay (vehicle, person)</summary>
    public const int VAR_DEPART_DELAY = 0x3b;

    /// <summary>boarding time (get: vehicle type, vehicle, person)</summary>
    public const int VAR_BOARDING_DURATION = 0x2f;

    /// <summary>impatience (get,set: vehicle type, vehicle, person)</summary>
    public const int VAR_IMPATIENCE = 0x26;

    /// <summary>TraCI identifier <c>VAR_BUS_STOP_ID_LIST</c>.</summary>
    public const int VAR_BUS_STOP_ID_LIST = 0x9f;

    /// <summary>number of persons waiting at a defined bus stop (get: simulation)</summary>
    public const int VAR_BUS_STOP_WAITING = 0x67;

    /// <summary>ids of persons waiting at a defined bus stop (get: simulation)</summary>
    public const int VAR_BUS_STOP_WAITING_IDS = 0xef;

    /// <summary>current leader together with gap (get: vehicle)</summary>
    public const int VAR_LEADER = 0x68;

    /// <summary>current leader together with gap (get: vehicle)</summary>
    public const int VAR_FOLLOWER = 0x78;

    /// <summary>edge index in current route (get: vehicle)</summary>
    public const int VAR_ROUTE_INDEX = 0x69;

    /// <summary>current waiting time (get: vehicle, lane)</summary>
    public const int VAR_WAITING_TIME = 0x7a;

    /// <summary>current waiting time (get: vehicle)</summary>
    public const int VAR_ACCUMULATED_WAITING_TIME = 0x87;

    /// <summary>upcoming traffic lights (get: vehicle)</summary>
    public const int VAR_NEXT_TLS = 0x70;

    /// <summary>upcoming stops (get: vehicle)</summary>
    public const int VAR_NEXT_STOPS = 0x73;

    /// <summary>upcoming stops with selection (get: vehicle)</summary>
    public const int VAR_NEXT_STOPS2 = 0x74;

    /// <summary>upcoming links(get: vehicle)</summary>
    public const int VAR_NEXT_LINKS = 0x33;

    /// <summary>current acceleration (get,set: vehicle)</summary>
    public const int VAR_ACCELERATION = 0x72;

    /// <summary>arrival position (get,set: vehicle)</summary>
    public const int VAR_ARRIVALPOS = 0x75;

    /// <summary>arrival lane (get,set: vehicle)</summary>
    public const int VAR_ARRIVALLANE = 0x76;

    /// <summary>arrival speed (get,set: vehicle)</summary>
    public const int VAR_ARRIVALSPEED = 0x77;

    /// <summary>add log message (set: simulation)</summary>
    public const int CMD_MESSAGE = 0x65;

    /// <summary>current time in seconds (get: simulation)</summary>
    public const int VAR_TIME = 0x66;

    /// <summary>current time step (get: simulation)</summary>
    public const int VAR_TIME_STEP = 0x70;

    /// <summary>current electricity consumption of a node (get: vehicle, lane, edge)</summary>
    public const int VAR_ELECTRICITYCONSUMPTION = 0x71;

    /// <summary>number of loaded vehicles (get: simulation)</summary>
    public const int VAR_LOADED_VEHICLES_NUMBER = 0x71;

    /// <summary>loaded vehicle ids (get: simulation)</summary>
    public const int VAR_LOADED_VEHICLES_IDS = 0x72;

    /// <summary>number of departed vehicle (get: simulation)</summary>
    public const int VAR_DEPARTED_VEHICLES_NUMBER = 0x73;

    /// <summary>departed vehicle ids (get: simulation)</summary>
    public const int VAR_DEPARTED_VEHICLES_IDS = 0x74;

    /// <summary>number of vehicles starting to teleport (get: simulation)</summary>
    public const int VAR_TELEPORT_STARTING_VEHICLES_NUMBER = 0x75;

    /// <summary>ids of vehicles starting to teleport (get: simulation)</summary>
    public const int VAR_TELEPORT_STARTING_VEHICLES_IDS = 0x76;

    /// <summary>number of vehicles ending to teleport (get: simulation)</summary>
    public const int VAR_TELEPORT_ENDING_VEHICLES_NUMBER = 0x77;

    /// <summary>ids of vehicles ending to teleport (get: simulation)</summary>
    public const int VAR_TELEPORT_ENDING_VEHICLES_IDS = 0x78;

    /// <summary>number of arrived vehicles (get: simulation)</summary>
    public const int VAR_ARRIVED_VEHICLES_NUMBER = 0x79;

    /// <summary>ids of arrived vehicles (get: simulation)</summary>
    public const int VAR_ARRIVED_VEHICLES_IDS = 0x7a;

    /// <summary>delta t (get: simulation)</summary>
    public const int VAR_DELTA_T = 0x7b;

    /// <summary>bounding box (get: simulation)</summary>
    public const int VAR_NET_BOUNDING_BOX = 0x7c;

    /// <summary>minimum number of expected vehicles (get: simulation)</summary>
    public const int VAR_MIN_EXPECTED_VEHICLES = 0x7d;

    /// <summary>number of departed persons (get: simulation)</summary>
    public const int VAR_DEPARTED_PERSONS_NUMBER = 0x24;

    /// <summary>departed person ids (get: simulation)</summary>
    public const int VAR_DEPARTED_PERSONS_IDS = 0x25;

    /// <summary>number of arrived persons (get: simulation)</summary>
    public const int VAR_ARRIVED_PERSONS_NUMBER = 0x26;

    /// <summary>ids of arrived persons (get: simulation)</summary>
    public const int VAR_ARRIVED_PERSONS_IDS = 0x27;

    /// <summary>number of vehicles starting to park (get: simulation)</summary>
    public const int VAR_STOP_STARTING_VEHICLES_NUMBER = 0x68;

    /// <summary>ids of vehicles starting to park (get: simulation)</summary>
    public const int VAR_STOP_STARTING_VEHICLES_IDS = 0x69;

    /// <summary>number of vehicles ending to park (get: simulation)</summary>
    public const int VAR_STOP_ENDING_VEHICLES_NUMBER = 0x6a;

    /// <summary>ids of vehicles ending to park (get: simulation)</summary>
    public const int VAR_STOP_ENDING_VEHICLES_IDS = 0x6b;

    /// <summary>number of vehicles starting to park (get: simulation)</summary>
    public const int VAR_PARKING_STARTING_VEHICLES_NUMBER = 0x6c;

    /// <summary>ids of vehicles starting to park (get: simulation)</summary>
    public const int VAR_PARKING_STARTING_VEHICLES_IDS = 0x6d;

    /// <summary>number of vehicles maneuvering (get: simulation)</summary>
    public const int VAR_PARKING_MANEUVERING_VEHICLES_NUMBER = 0x3a;

    /// <summary>ids of vehicles maneuvering (get: simulation)</summary>
    public const int VAR_PARKING_MANEUVERING_VEHICLES_IDS = 0x3b;

    /// <summary>number of vehicles ending to park (get: simulation)</summary>
    public const int VAR_PARKING_ENDING_VEHICLES_NUMBER = 0x6e;

    /// <summary>ids of vehicles ending to park (get: simulation)</summary>
    public const int VAR_PARKING_ENDING_VEHICLES_IDS = 0x6f;

    /// <summary>number of vehicles involved in a collision (get: simulation)</summary>
    public const int VAR_COLLIDING_VEHICLES_NUMBER = 0x80;

    /// <summary>ids of vehicles involved in a collision (get: simulation)</summary>
    public const int VAR_COLLIDING_VEHICLES_IDS = 0x81;

    /// <summary>number of vehicles involved in a collision (get: simulation)</summary>
    public const int VAR_EMERGENCYSTOPPING_VEHICLES_NUMBER = 0x89;

    /// <summary>ids of vehicles involved in a collision (get: simulation)</summary>
    public const int VAR_EMERGENCYSTOPPING_VEHICLES_IDS = 0x8a;

    /// <summary>scale traffic (set, get: simulation, vehicle)</summary>
    public const int VAR_SCALE = 0x8e;

    /// <summary>clears the simulation of all not inserted vehicles (set: simulation)</summary>
    public const int CMD_CLEAR_PENDING_VEHICLES = 0x94;

    /// <summary>retrieve number of not inserted  vehicles (get: simulation, edge, lane)</summary>
    public const int VAR_PENDING_VEHICLES = 0x94;

    /// <summary>retrieve global option value (get: simulation)</summary>
    public const int VAR_OPTION = 0x32;

    /// <summary>triggers saving simulation state (set: simulation)</summary>
    public const int CMD_SAVE_SIMSTATE = 0x95;

    /// <summary>triggers saving simulation state (set: simulation)</summary>
    public const int CMD_LOAD_SIMSTATE = 0x96;

    /// <summary>retrieve detail data for each collision</summary>
    public const int VAR_COLLISIONS = 0x23;

    /// <summary>return loaded vehicles regardless of visibility (excluding arrived)</summary>
    public const int VAR_LOADED_LIST = 0x24;

    /// <summary>return teleporting vehicles</summary>
    public const int VAR_TELEPORTING_LIST = 0x25;

    /// <summary>sets/retrieves abstract parameter</summary>
    public const int VAR_PARAMETER = 0x7e;

    /// <summary>retrieves abstract parameter and returns (key, value) tuple</summary>
    public const int VAR_PARAMETER_WITH_KEY = 0x3e;

    /// <summary>add an instance (poi, polygon, vehicle, person, route, gui)</summary>
    public const int ADD = 0x80;

    /// <summary>remove an instance (poi, polygon, vehicle, person, gui, route)</summary>
    public const int REMOVE = 0x81;

    /// <summary>copy an instance (vehicle type, other TBD.)</summary>
    public const int COPY = 0x88;

    /// <summary>convert coordinates</summary>
    public const int POSITION_CONVERSION = 0x82;

    /// <summary>distance between points or vehicles</summary>
    public const int DISTANCE_REQUEST = 0x83;

    /// <summary>the current driving distance</summary>
    public const int VAR_DISTANCE = 0x84;

    /// <summary>add a fully specified instance (vehicle)</summary>
    public const int ADD_FULL = 0x85;

    /// <summary>find a car based route</summary>
    public const int FIND_ROUTE = 0x86;

    /// <summary>find an intermodal route</summary>
    public const int FIND_INTERMODAL_ROUTE = 0x87;

    /// <summary>force rerouting based on travel time (vehicles)</summary>
    public const int CMD_REROUTE_TRAVELTIME = 0x90;

    /// <summary>force rerouting based on effort (vehicles)</summary>
    public const int CMD_REROUTE_EFFORT = 0x91;

    /// <summary>validates current route (vehicles)</summary>
    public const int VAR_ROUTE_VALID = 0x92;

    /// <summary>retrieve distance along linear reference system (vehicle, edge)</summary>
    public const int VAR_REFERENCE_DISTANCE = 0x95;

    /// <summary>retrieve information regarding the current person/container stage</summary>
    public const int VAR_STAGE = 0xc0;

    /// <summary>retrieve information regarding the next edge including crossings and walkingAreas (pedestrians only)</summary>
    public const int VAR_NEXT_EDGE = 0xc1;

    /// <summary>retrieve information regarding the number of remaining stages</summary>
    public const int VAR_STAGES_REMAINING = 0xc2;

    /// <summary>retrieve the current vehicle id for the driving stage (person, container)</summary>
    public const int VAR_VEHICLE = 0xc3;

    /// <summary>append a person stage (person)</summary>
    public const int APPEND_STAGE = 0xc4;

    /// <summary>replace a person stage (person)</summary>
    public const int REPLACE_STAGE = 0xcd;

    /// <summary>append a person stage (person)</summary>
    public const int REMOVE_STAGE = 0xc5;

    /// <summary>retrieve taxi reservation (person)</summary>
    public const int VAR_TAXI_RESERVATIONS = 0xc6;

    /// <summary>manipulate taxi reservation (person)</summary>
    public const int SPLIT_TAXI_RESERVATIONS = 0xc7;

    /// <summary>sample last route (routeprobe)</summary>
    public const int VAR_SAMPLE_LAST = 0x20;

    /// <summary>sample current route (routeprobe)</summary>
    public const int VAR_SAMPLE_CURRENT = 0x21;

    /// <summary>zoom</summary>
    public const int VAR_VIEW_ZOOM = 0xa0;

    /// <summary>view position</summary>
    public const int VAR_VIEW_OFFSET = 0xa1;

    /// <summary>view schema</summary>
    public const int VAR_VIEW_SCHEMA = 0xa2;

    /// <summary>view by boundary</summary>
    public const int VAR_VIEW_BOUNDARY = 0xa3;

    /// <summary>select/deselect object (gui)</summary>
    public const int VAR_SELECT = 0xa4;

    /// <summary>screenshot</summary>
    public const int VAR_SCREENSHOT = 0xa5;

    /// <summary>track vehicle</summary>
    public const int VAR_TRACK_VEHICLE = 0xa6;

    /// <summary>presence of view</summary>
    public const int VAR_HAS_VIEW = 0xa7;

    /// <summary>charging station power</summary>
    public const int VAR_CS_POWER = 0x97;

    /// <summary>charging station power</summary>
    public const int VAR_CS_EFFICIENCY = 0x98;

    /// <summary>charging station power</summary>
    public const int VAR_CS_CHARGE_IN_TRANSIT = 0x99;

    /// <summary>charging station power</summary>
    public const int VAR_CS_CHARGE_DELAY = 0x9a;

    /// <summary>parking area access permissions</summary>
    public const int VAR_ACCESS_BADGE = 0x9b;

    /// <summary>charging station total power</summary>
    public const int VAR_CS_TOTAL_POWER = 0x9c;

    /// <summary>@name currently wanted lane-change action @{ @brief No action desired</summary>
    public const int LCA_NONE = 0;

    /// <summary>@brief Needs to stay on the current lane</summary>
    public const int LCA_STAY = 1 << 0;

    /// <summary>@brief Wants go to the left</summary>
    public const int LCA_LEFT = 1 << 1;

    /// <summary>@brief Wants go to the right</summary>
    public const int LCA_RIGHT = 1 << 2;

    /// <summary>@brief The action is needed to follow the route (navigational lc)</summary>
    public const int LCA_STRATEGIC = 1 << 3;

    /// <summary>@brief The action is done to help someone else</summary>
    public const int LCA_COOPERATIVE = 1 << 4;

    /// <summary>@brief The action is due to the wish to be faster (tactical lc)</summary>
    public const int LCA_SPEEDGAIN = 1 << 5;

    /// <summary>@brief The action is due to the default of keeping right "Rechtsfahrgebot"</summary>
    public const int LCA_KEEPRIGHT = 1 << 6;

    /// <summary>@brief The action is due to a TraCI request</summary>
    public const int LCA_TRACI = 1 << 7;

    /// <summary>@brief The action is urgent (to be defined by lc-model)</summary>
    public const int LCA_URGENT = 1 << 8;

    /// <summary>@brief The action has not been determined</summary>
    public const int LCA_UNKNOWN = 1 << 30;

    /// <summary>@} @name External state @{ @brief The vehicle is blocked by left leader</summary>
    public const int LCA_BLOCKED_BY_LEFT_LEADER = 1 << 9;

    /// <summary>@brief The vehicle is blocked by left follower</summary>
    public const int LCA_BLOCKED_BY_LEFT_FOLLOWER = 1 << 10;

    /// <summary>@brief The vehicle is blocked by right leader</summary>
    public const int LCA_BLOCKED_BY_RIGHT_LEADER = 1 << 11;

    /// <summary>@brief The vehicle is blocked by right follower</summary>
    public const int LCA_BLOCKED_BY_RIGHT_FOLLOWER = 1 << 12;

    /// <summary>@brief The vehicle is blocked being overlapping</summary>
    public const int LCA_OVERLAPPING = 1 << 13;

    /// <summary>@brief The vehicle does not have enough space to complete a continuous change before the next turn</summary>
    public const int LCA_INSUFFICIENT_SPACE = 1 << 14;

    /// <summary>@brief used by the sublane model</summary>
    public const int LCA_SUBLANE = 1 << 15;

    /// <summary>@brief Vehicle is too slow to complete a continuous lane change (in case that maxSpeedLatStanding==0)</summary>
    public const int LCA_INSUFFICIENT_SPEED = 1 << 28;

    /// <summary>@brief lane can change</summary>
    public const int LCA_WANTS_LANECHANGE = LCA_LEFT | LCA_RIGHT;

    /// <summary>@brief lane can change or stay</summary>
    public const int LCA_WANTS_LANECHANGE_OR_STAY = LCA_WANTS_LANECHANGE | LCA_STAY;

    /// <summary>@brief blocked left</summary>
    public const int LCA_BLOCKED_LEFT = LCA_BLOCKED_BY_LEFT_LEADER | LCA_BLOCKED_BY_LEFT_FOLLOWER;

    /// <summary>@brief blocked right</summary>
    public const int LCA_BLOCKED_RIGHT = LCA_BLOCKED_BY_RIGHT_LEADER | LCA_BLOCKED_BY_RIGHT_FOLLOWER;

    /// <summary>@brief blocked by leader</summary>
    public const int LCA_BLOCKED_BY_LEADER = LCA_BLOCKED_BY_LEFT_LEADER | LCA_BLOCKED_BY_RIGHT_LEADER;

    /// <summary>@brief blocker by follower</summary>
    public const int LCA_BLOCKED_BY_FOLLOWER = LCA_BLOCKED_BY_LEFT_FOLLOWER | LCA_BLOCKED_BY_RIGHT_FOLLOWER;

    /// <summary>@brief blocked in all directions</summary>
    public const int LCA_BLOCKED = LCA_BLOCKED_LEFT | LCA_BLOCKED_RIGHT | LCA_INSUFFICIENT_SPACE | LCA_INSUFFICIENT_SPEED;

    /// <summary>@brief reasons of lane change</summary>
    public const int LCA_CHANGE_REASONS = LCA_STRATEGIC | LCA_COOPERATIVE | LCA_SPEEDGAIN | LCA_KEEPRIGHT | LCA_SUBLANE | LCA_TRACI;

    /// <summary>LCA_BLOCKED_BY_CURRENT_LEADER = 1 &lt;&lt; 28 LCA_BLOCKED_BY_CURRENT_FOLLOWER = 1 &lt;&lt; 29 @} @name originally model specific states (migrated here since they were duplicated in all current models) @{</summary>
    public const int LCA_AMBLOCKINGLEADER = 1 << 16;

    /// <summary>TraCI identifier <c>LCA_AMBLOCKINGFOLLOWER</c>.</summary>
    public const int LCA_AMBLOCKINGFOLLOWER = 1 << 17;

    /// <summary>TraCI identifier <c>LCA_MRIGHT</c>.</summary>
    public const int LCA_MRIGHT = 1 << 18;

    /// <summary>TraCI identifier <c>LCA_MLEFT</c>.</summary>
    public const int LCA_MLEFT = 1 << 19;

    /// <summary>!!! never set LCA_UNBLOCK = 1 &lt;&lt; 20,</summary>
    public const int LCA_AMBLOCKINGFOLLOWER_DONTBRAKE = 1 << 21;

    /// <summary>!!! never used LCA_AMBLOCKINGSECONDFOLLOWER = 1 &lt;&lt; 22,</summary>
    public const int LCA_CHANGE_TO_HELP = 1 << 23;

    /// <summary>!!! never read LCA_KEEP1 = 1 &lt;&lt; 24, !!! never used LCA_KEEP2 = 1 &lt;&lt; 25,</summary>
    public const int LCA_AMBACKBLOCKER = 1 << 26;

    /// <summary>TraCI identifier <c>LCA_AMBACKBLOCKER_STANDING</c>.</summary>
    public const int LCA_AMBACKBLOCKER_STANDING = 1 << 27;
}
