# Authoring skills

Reference bundles that an AI coding assistant loads to work on this repository. Two kinds, kept
apart on purpose.

## Ours: `CarlaControl/skills/`

Skills that describe this fork's own tooling live in the repository, tracked and versioned with the
code they describe, and ship in the distribution under `skills/`.

| Skill | Covers |
|---|---|
| [`sumo-traffic-scenarios`](../CarlaControl/skills/sumo-traffic-scenarios/SKILL.md) | Building SUMO traffic scenarios and Cursor-on-Target telemetry datasets for a CARLA world generated from OpenStreetMap: the OSM to world-package to SUMO-network to routes to CoT pipeline, the netconvert flags, coordinate alignment, and the `make_*_scenario.py` / `sumo_cot_telemetry.py` tools |

They sit beside `CarlaControl/src/carlacontrol/`, the code that generates most of their contents, so
a skill cannot describe a tool the same commit changed. A copy outside the repository has no
version, no history and no reproducible source, and cannot be bundled into a distribution — which is
why the `.agents/skills/sumo-traffic-scenarios/` copy at the workspace root is now a stub pointing
here rather than a second, editable copy. It is deliberately not a directory junction: a junction is
invisible in `git status`, does not survive a fresh clone, and lets the two diverge unseen.

## Theirs: the Unreal Engine skills, cloned beside the repository

General Unreal Engine 5 reference skills — gameplay framework, Mass Entity, Niagara, materials,
replication and about twenty more — are a third-party MIT-licensed collection. They are **not
vendored into this repository**: the upstream clone already has a version, a history and an intact
licence, and copying 1.3 MB of somebody else's content in would add an attribution obligation for a
recipient with no use for it.

Clone them beside the `carla` checkout, at the workspace root:

```sh
git clone https://github.com/quodsoler/unreal-engine-skills.git
git -C unreal-engine-skills checkout 231c8571be6f3335685edc566a28ec6f9621361d
```

That commit is the version this project has been working against. The clone carries its own
`LICENSE` (MIT, Copyright (c) 2025 quodsoler); leave it in place.

The resulting workspace layout:

```
<workspace>/
  carla/                        this repository
  unreal-engine-skills/         the third-party clone, with its remote and LICENSE
  .agents/skills/               what an assistant discovers:
    sumo-traffic-scenarios/     a stub pointing at carla/CarlaControl/skills/
    ue-*/                       the Unreal Engine skills
```
