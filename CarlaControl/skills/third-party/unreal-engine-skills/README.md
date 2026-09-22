# Unreal Engine Skills for AI Agents

A collection of 27 AI agent skills for Unreal Engine C++ development. Built for game developers who want AI coding agents to help write correct, production-quality UE5 C++ code. Works with Claude Code, Cursor, Windsurf, and any agent that supports the [Agent Skills spec](https://agentskills.io).

Every skill has been audited against Unreal Engine source code to ensure API accuracy — correct function signatures, valid class hierarchies, and real method names. No hallucinated APIs.

**Contributions welcome!** Found an inaccuracy or want to improve a skill? [Open a PR](#contributing).

## What are Skills?

Skills are markdown files that give AI agents specialized knowledge and workflows for specific tasks. When you add these to your project, your agent can recognize when you're working on an Unreal Engine task and apply the right patterns, APIs, and best practices.

## How Skills Work Together

Skills reference each other and build on shared context. The `ue-project-context` skill is the foundation — it captures your project's modules, target platforms, and conventions so other skills can give relevant advice.

```
                          ┌──────────────────────────────────────┐
                          │          ue-project-context           │
                          │    (read by all other skills first)   │
                          └──────────────────┬───────────────────┘
                                             │
    ┌─────────────┬────────────┬─────────────┼────────────┬─────────────┬──────────────┐
    ▼             ▼            ▼             ▼            ▼             ▼              ▼
┌────────┐ ┌──────────┐ ┌──────────┐ ┌───────────┐ ┌──────────┐ ┌──────────┐ ┌───────────┐
│Core C++│ │Gameplay  │ │Rendering │ │  World &  │ │  AI &    │ │  UI &    │ │  Build &  │
│        │ │          │ │& VFX     │ │ Streaming │ │  Logic   │ │  Input   │ │  Tools    │
├────────┤ ├──────────┤ ├──────────┤ ├───────────┤ ├──────────┤ ├──────────┤ ├───────────┤
│cpp-    │ │gameplay- │ │materials │ │world-lvl  │ │ai-nav    │ │ui-umg    │ │module-    │
│ found  │ │ framework│ │niagara   │ │procedural │ │state-tree│ │input-sys │ │ build     │
│actor-  │ │abilities │ │audio     │ │physics    │ │mass-     │ │          │ │editor-    │
│ comp   │ │animation │ │sequencer │ │serial-    │ │ entity   │ │          │ │ tools     │
│        │ │char-move │ │          │ │ savegame  │ │          │ │          │ │testing    │
│        │ │game-feat │ │          │ │data-asset │ │          │ │          │ │async-     │
│        │ │net-repl  │ │          │ │           │ │          │ │          │ │ thread    │
└───┬────┘ └────┬─────┘ └────┬─────┘ └─────┬─────┘ └────┬─────┘ └────┬─────┘ └─────┬─────┘
    │           │            │             │            │             │              │
    └───────────┴─────┬──────┴─────────────┴────────────┴─────────────┴──────────────┘
                      │
       Skills cross-reference each other:
         cpp-foundations ↔ actor-component ↔ gameplay-framework
         gameplay-abilities ↔ animation ↔ character-movement
         ai-navigation ↔ state-trees ↔ mass-entity
```

See each skill's **Related Skills** section for the full dependency map.

## Available Skills

<!-- SKILLS:START -->
| Skill | Description |
|-------|-------------|
| [ue-actor-component-architecture](skills/ue-actor-component-architecture/) | Actor and component design — BeginPlay, Tick, component attachment, ownership, child actors |
| [ue-ai-navigation](skills/ue-ai-navigation/) | AI controllers, behavior trees, blackboards, AI perception, NavMesh, EQS |
| [ue-animation-system](skills/ue-animation-system/) | AnimInstance, montages, blend spaces, state machines, anim notifies, linked anim graphs |
| [ue-async-threading](skills/ue-async-threading/) | Async operations, threading, parallel execution, tasks, FRunnable, AsyncTask |
| [ue-audio-system](skills/ue-audio-system/) | UAudioComponent, SoundCue, MetaSound, attenuation, concurrency, audio analysis |
| [ue-character-movement](skills/ue-character-movement/) | CharacterMovementComponent, movement modes, root motion, network prediction |
| [ue-cpp-foundations](skills/ue-cpp-foundations/) | UPROPERTY, UFUNCTION, UCLASS, TArray, TMap, delegates, FString, garbage collection, smart pointers |
| [ue-data-assets-tables](skills/ue-data-assets-tables/) | DataAsset, DataTable, soft references, TSoftObjectPtr, async loading, Asset Manager |
| [ue-editor-tools](skills/ue-editor-tools/) | Editor utility widgets, Blutility, detail customization, property editors, editor subsystems |
| [ue-game-features](skills/ue-game-features/) | Game Feature plugins, modular gameplay, GameFeatureAction, GameFrameworkComponentManager |
| [ue-gameplay-abilities](skills/ue-gameplay-abilities/) | GAS, Gameplay Abilities, Gameplay Effects, Attribute Sets, Gameplay Tags, Gameplay Cues |
| [ue-gameplay-framework](skills/ue-gameplay-framework/) | GameMode, GameState, PlayerController, PlayerState, Pawn, HUD |
| [ue-input-system](skills/ue-input-system/) | Enhanced Input system — Input Actions, Input Mapping Contexts, modifiers, triggers |
| [ue-mass-entity](skills/ue-mass-entity/) | Mass Entity framework, MassProcessor, MassFragment, MassTag, MassObserver (UE 5.5+) |
| [ue-materials-rendering](skills/ue-materials-rendering/) | Materials, shaders, dynamic material instances, post-process, render targets, Nanite, Lumen |
| [ue-module-build-system](skills/ue-module-build-system/) | Build.cs, Target.cs, module creation, plugin setup, build configuration |
| [ue-networking-replication](skills/ue-networking-replication/) | Multiplayer networking, replication, RPCs, net roles, server/client authority |
| [ue-niagara-effects](skills/ue-niagara-effects/) | Niagara particle systems, VFX, emitters, data interfaces, GPU simulation |
| [ue-physics-collision](skills/ue-physics-collision/) | Collision detection, traces, physics simulation, physical interactions, Chaos physics |
| [ue-procedural-generation](skills/ue-procedural-generation/) | PCG framework, ProceduralMesh, instanced mesh, runtime generation, noise |
| [ue-project-context](skills/ue-project-context/) | Project context document — modules, target platforms, conventions, team standards |
| [ue-sequencer-cinematics](skills/ue-sequencer-cinematics/) | Sequencer, LevelSequence, cutscenes, cinematics, camera tracks, Movie Render Queue |
| [ue-serialization-savegames](skills/ue-serialization-savegames/) | Save/load systems, player progress persistence, data serialization, FArchive |
| [ue-state-trees](skills/ue-state-trees/) | State Tree, state machines, StateTreeTask, StateTreeCondition, StateTreeEvaluator |
| [ue-testing-debugging](skills/ue-testing-debugging/) | Automation tests, functional tests, UE_LOG, visual logger, debug drawing |
| [ue-ui-umg-slate](skills/ue-ui-umg-slate/) | UMG, Slate, UserWidget, HUD, BindWidget, Common UI, MVVM |
| [ue-world-level-streaming](skills/ue-world-level-streaming/) | World Partition, level streaming, level travel, data layers, world subsystems |
<!-- SKILLS:END -->

## Installation

### Option 1: CLI Install (Recommended)

Use [npx skills](https://github.com/vercel-labs/skills) to install skills directly:

```bash
# Install all skills
npx skills add quodsoler/unreal-engine-skills

# Install specific skills
npx skills add quodsoler/unreal-engine-skills --skill ue-cpp-foundations ue-gameplay-abilities

# List available skills
npx skills add quodsoler/unreal-engine-skills --list
```

This automatically installs to your `.agents/skills/` directory (and symlinks into `.claude/skills/` for Claude Code compatibility).

### Option 2: Clone and Copy

Clone the entire repo and copy the skills folder:

```bash
git clone https://github.com/quodsoler/unreal-engine-skills.git
cp -r unreal-engine-skills/skills/* .agents/skills/
```

### Option 3: Git Submodule

Add as a submodule for easy updates:

```bash
git submodule add https://github.com/quodsoler/unreal-engine-skills.git .agents/unreal-engine-skills
```

Then reference skills from `.agents/unreal-engine-skills/skills/`.

### Option 4: Fork and Customize

1. Fork this repository
2. Customize skills for your specific project
3. Clone your fork into your projects

### Option 5: SkillKit (Multi-Agent)

Use [SkillKit](https://github.com/rohitg00/skillkit) to install skills across multiple AI agents (Claude Code, Cursor, Copilot, etc.):

```bash
# Install all skills
npx skillkit install quodsoler/unreal-engine-skills

# Install specific skills
npx skillkit install quodsoler/unreal-engine-skills --skill ue-cpp-foundations ue-gameplay-abilities

# List available skills
npx skillkit install quodsoler/unreal-engine-skills --list
```

## Usage

Once installed, just ask your agent to help with Unreal Engine tasks:

```
"Add a replicated health attribute with GAS"
→ Uses ue-gameplay-abilities skill

"Set up World Partition streaming for my open world"
→ Uses ue-world-level-streaming skill

"Create a Niagara system driven by C++ parameters"
→ Uses ue-niagara-effects skill

"Write an automation test for my inventory system"
→ Uses ue-testing-debugging skill
```

You can also invoke skills directly:

```
/ue-cpp-foundations
/ue-gameplay-abilities
/ue-networking-replication
```

## Skill Categories

### Core C++
- `ue-cpp-foundations` — UPROPERTY, UFUNCTION, containers, delegates, GC, smart pointers
- `ue-actor-component-architecture` — Actor lifecycle, component design, attachment, spawning
- `ue-module-build-system` — Build.cs, Target.cs, modules, plugins, build configuration

### Gameplay Systems
- `ue-gameplay-framework` — GameMode, GameState, PlayerController, PlayerState, Pawn
- `ue-gameplay-abilities` — GAS: abilities, effects, attributes, tags, cues
- `ue-animation-system` — AnimInstance, montages, blend spaces, anim notifies
- `ue-character-movement` — CMC, movement modes, root motion, network prediction
- `ue-game-features` — Game Feature plugins, modular gameplay
- `ue-networking-replication` — Replication, RPCs, net roles, prediction

### Rendering & VFX
- `ue-materials-rendering` — Materials, shaders, MIDs, post-process, Nanite, Lumen
- `ue-niagara-effects` — Niagara particles, emitters, data interfaces, GPU sim
- `ue-audio-system` — Audio components, SoundCue, MetaSound, spatial audio
- `ue-sequencer-cinematics` — Sequencer, cinematics, camera, Movie Render Queue

### World & Data
- `ue-world-level-streaming` — World Partition, level streaming, data layers
- `ue-procedural-generation` — PCG framework, procedural mesh, runtime generation
- `ue-physics-collision` — Traces, collision, Chaos physics, physical interactions
- `ue-serialization-savegames` — Save systems, FArchive, serialization
- `ue-data-assets-tables` — DataAsset, DataTable, soft references, async loading

### AI & Logic
- `ue-ai-navigation` — AI controllers, behavior trees, EQS, NavMesh, perception
- `ue-state-trees` — State Tree, tasks, conditions, evaluators
- `ue-mass-entity` — Mass Entity framework, processors, fragments, observers

### UI & Input
- `ue-ui-umg-slate` — UMG, Slate, Common UI, MVVM, widget binding
- `ue-input-system` — Enhanced Input, actions, mapping contexts, modifiers

### Build & Tools
- `ue-editor-tools` — Editor utility widgets, detail customization, editor subsystems
- `ue-testing-debugging` — Automation tests, functional tests, logging, debug tools
- `ue-async-threading` — Async tasks, threading, parallel execution, concurrency

### Project Setup
- `ue-project-context` — Project context document capturing your specific setup and conventions

## API Accuracy

These skills have gone through multiple rounds of source-level auditing against Unreal Engine headers to fix incorrect or hallucinated API calls. Over 160 inaccuracies have been identified and corrected, including:

- Wrong function signatures and return types
- Non-existent methods (common in LLM-generated UE code)
- Deprecated APIs replaced with current alternatives
- Incorrect class hierarchies and inheritance chains

If you find an API call that doesn't match the engine source, please [open an issue](https://github.com/quodsoler/unreal-engine-skills/issues).

## Contributing

Found a way to improve a skill? Have a new skill to suggest? PRs and issues welcome!

### Guidelines

- `name` must match directory name exactly (lowercase, hyphens only)
- `description` should include trigger phrases for skill discovery
- `SKILL.md` must be under 500 lines (move details to `references/`)
- Verify API calls against Unreal Engine source before submitting

## License

[MIT](LICENSE) — Use these however you want.
