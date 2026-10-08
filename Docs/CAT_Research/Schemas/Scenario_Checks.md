# Scenario compiler checks

| | |
|---|---|
| File | `checks.json`, beside the authoring skill's `SKILL.md` |
| Schema | `CarlaControl/schemas/scenario_checks.schema.json` |
| Schema id | `urn:carla-sumo-capture:schema:scenario-checks:1` |
| Format version | 1, in `checks_version` |

## What it is

`checks.json` lists every check the scenario compiler runs, by its stable id, with what it compares and what it can conclude.\
A finding in a compile's resolution report cites a check by this id.

An id is assigned once and never reused.\
A check that is removed stays in the list as retired, so a report written years ago still means what it said.\
The list is in the order the compiler runs the checks, which is why the ids are not in numeric order.

These are the compiler's checks.\
A capture run's checks are a separate list with their own numbers, the carla-capture run checks, which a run's findings cite.

## Who writes it and who reads it

- **`carla-compile-scenario --write-checks PATH`** writes it, generated from the compiler's own list.\
  It ships with the authoring skill.\
  A test holds the shipped copy equal to the compiler's list.
- **A scenario developer or the authoring skill reads it** to learn what a check id in a report means.\
  No tool reads it as input.

## Fields

| Field | Type | Required | Meaning |
|---|---|---|---|
| `checks_version` | constant `1` | yes | The format version of this file. |
| `outcomes.refuse` | string | yes | What a refusal means: the compile fails and nothing is emitted. |
| `outcomes.warn` | string | yes | What a warning means: the compile goes on and the warning is carried in full into the resolution report. |
| `checks` | array | yes | Every check, in the order the compiler runs them. |
| `checks[].id` | integer | yes | The check's stable id. |
| `checks[].group` | string | yes | The stage it belongs to: `specification`, `world_binding`, `references`, `routes`, `vehicles`, `annotation`, `areas`, `epoch_and_illumination`, `emission` or `dry_run`. |
| `checks[].title` | string | yes | What the check establishes. |
| `checks[].against` | string | yes | What it compares against. |
| `checks[].outcomes` | array of `refuse`, `warn` | yes | What it can conclude. Empty for a check that only states a fact, or one that is retired. |
| `checks[].status` | `compiler`, `world_build`, `not_built` or `retired` | yes | Where the check is carried out: by the compiler, by the world build, not yet anywhere, or no longer. |
| `checks[].prevents` | string | yes | The failure it exists to prevent. |

## Versions

This page describes version 1, the only version.\
No tool reads the file as input.\
In a reader of your own, read version 1 and refuse a newer version rather than read it in part.

## Example

```json
{
  "checks_version": 1,
  "outcomes": {
    "refuse": "the compile fails and nothing is emitted",
    "warn": "the compile goes on and the warning is carried in full into the resolution report"
  },
  "checks": [
    {"id": 53, "group": "specification",
     "title": "The specification is well formed at a spec_version this compiler implements: known fields only, each of its declared type",
     "against": "the specification schema", "outcomes": ["refuse"], "status": "compiler",
     "prevents": "A field nobody reads, which its author will later believe was honored"},
    {"id": 1, "group": "world_binding",
     "title": "The network fingerprint the specification names equals the world package's",
     "against": "the world package", "outcomes": ["refuse"], "status": "compiler",
     "prevents": "Authoring against a road graph other than the one that renders"}
  ]
}
```
