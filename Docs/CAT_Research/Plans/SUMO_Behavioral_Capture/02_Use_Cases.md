# 02 — Use cases

**Status:** Plan section. Behavioural specification, not implementation. No code was changed and no build
was run.
**Change history.**
- 2026-09-18: added simulated time of day and an operator control surface (brief §3a).
- 2026-09-18: scoped out model scoring; this pipeline labels, it never scores (brief §3b).
- 2026-09-18: restored the live exercise to primary status; added unattended regeneration (brief §3c).
- 2026-09-18: cyclic generation is driven and terminated externally, not judged by us (brief §3d).
**Owner role:** Systems architect. Companion section: [01 — Architecture](01_Architecture.md), whose
component names, modes and authority model this section uses without restating them.
**Scope:** The actors, the use cases each one drives, and the four flows that carry the most risk drawn as
activity diagrams with partitions — authoring (§4), capture (§5), the live exercise (§5a), a session an
external process starts, observes and stops (§5b) — plus the handover (§6).
**Audience:** An engineer implementing any section in this folder, and anyone deciding what the tooling's
command surface should be. A use case here is a contract about *what must be possible and what must fail
loudly*; it is not a user manual.
**Grounding:** Claims about existing behaviour are cited `path:line` against the working tree as read on
2026-09-17 and 2026-09-18, or carried forward from a Findings document and marked as such. Measurements
say how they were taken. Anything marked **inference** is reasoning, not a reading.

**Sibling sections this one leans on and does not duplicate.**
[11 — Time and illumination](11_Time_And_Illumination.md) owns the epoch contract, the civil-time
conversion, the rate semantics and the solar record's schema. [12 — Operator control
surface](12_Operator_Control_Surface.md) owns the design of the surface: what kind of program it is, what
it looks like, and what its commands are called. This section owns only the **actor-facing flow** over
both — what an actor must be able to do, in what order, and what must refuse. Where a use case needs a
property from either, the dependency is stated as a property, not as a design.
[06 — Truth and annotation](06_Truth_And_Annotation.md) owns the supervision-transfer rule, the export
split and the manifest's schema. [08 — Collection and EPoL](08_Collection_And_EPoL.md) owns **what we
emit and the guarantees on it** — the collection frame ([08 §7.2](08_Collection_And_EPoL.md)), the live
pacing property and the drop policy ([08 §11](08_Collection_And_EPoL.md)) — and the format of an
association-quality record. This section owns only **who hands what to whom, and what must refuse**.
For UC-13, [12](12_Operator_Control_Surface.md) owns the surface a non-interactive caller invokes and the
result record it reads; this section owns the **actor-facing flow** over it — being started, observed and
stopped by something outside — and what an unattended invocation must refuse. The dependency is stated in
UC-13 as a set of properties, not as a design.
[13 — Work breakdown](13_Work_Breakdown.md) sequences the build.

**Out of scope, deliberately.** Command-line syntax, screen layouts, file formats, and the internals of
any step. A step that says "validate every route" does not say how; [07](07_Scenario_Authoring.md) and
[04](04_Contracts.md) own that. Likewise a step that says "derive the civil time" does not say how;
[11](11_Time_And_Illumination.md) owns that.

**Out of scope by decision, which is different.** Nothing here scores a model. There is no use case for
running a detector, a tracker or an estimated-pattern-of-life model in order to measure it, no actor
whose job is to produce such a measurement, and no artifact that is one. Where a model appears it is
either a **live consumer being fed** (UC-8) or an **instrument used on our own data** (UC-11), and in
neither case is it the subject. The boundary and its reasoning are in
[`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3b; its consequence for this section is D2.23.

**Also out of scope by decision.** Nothing here specifies what *consumes* our output. UC-8 emits to an
attached chain and records what comes back; it does not describe that chain's interfaces, formats,
transports, report schemas or failure modes, and it designs no fusion stage, no normaliser and no schema
for anything the chain returns ([`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3c, D2.26).
Nothing here trains anything, either, and nothing here decides when a run has produced enough: UC-13 runs
for as long as an external process lets it and ends when that process ends it. Any cadence between runs,
what becomes of the output, and whatever trains on it are all outside and none of it is ours to model
(brief §3d, D2.29).

---

## 1. The actors

Derived from the workflow as it actually runs, not from roles invented for the diagram. Two of these are
the same person wearing different hats on different days, and they are kept apart because their
preconditions differ.

| Actor | Kind | What they are trying to do | Why they are a distinct actor |
|---|---|---|---|
| **Scenario author** | Human | Decide what pattern of life a scene depicts, what in it is anomalous, and **what civil time the scenario's simulated seconds mean** | Owns intent. The only source of behavioural truth ([20 §2.1](../../Findings/20_Behavioral_Annotation_And_Areas_Of_Interest.md)), and now the only source of the **epoch** — see §1.1 |
| **Authoring assistant** | Software agent, acting for the author | Turn a description in ordinary terms — street names, times, who goes where — into a scenario package that resolves | Needs machine-readable inputs and loud failures. It is fluent in SUMO and OpenSCENARIO and cannot know this fork's conventions unless they are written down and validated ([20 §5](../../Findings/20_Behavioral_Annotation_And_Areas_Of_Interest.md) preamble) |
| **World-builder operator** | Human | Turn an OSM extract into a world and its authoring reference set | Needs a GPU, a running server and a Cesium ion token; the only actor with that precondition |
| **Capture operator** | Human | Produce a corpus from a scenario and a world | Owns the session: mode, window, cameras, seed, and **illumination policy** — see §1.1 |
| **Live exercise operator** | Human | Drive an end-to-end demonstration: a live world, live imagery, an attached chain consuming it, and truth held alongside for the observers | Owns a session paced against a **wall clock** rather than run as fast as the machine allows, with an audience and an attached consumer — so a declared pacing band, an emission endpoint and a truth-feed decision are theirs, and nobody else's session has them (UC-8, §8a). UC-8 is a primary use case, not one that survives on sufferance |
| **TAK / CoT consumer** | External system | Display tracks | Receives CoT over UDP; ignores unknown `<detail>` children ([09 §5](../../Findings/09_Telemetry_CoT_Contract.md)) |
| **Attached exploitation chain** | External system, attached to a live session | Consume what we emit while the world is running, and — if it chooses to — return something | The single actor UC-8 associates with, and the only thing we honestly know about the far side: that something is connected. Its **internal composition is unspecified.** The brief's own picture of it — a detect-and-track stage feeding an EPoL model service that produces reports — is *one illustrative adapter*, and no step of UC-8 depends on it. Distinct from the external model team for the reason in §1.4: **its state is an input to our control flow.** Never given truth (D2.9) |
| **Detect-and-track stage** | External system, in the one role that is ours | Turn imagery into tracks | Named because the brief names it, and kept for the role we genuinely occupy with it: an **instrument** run on our own data in UC-11's corpus fitness probe, the way a thermometer checks an oven. Its other role — a stage inside an attached exploitation chain — is illustrative only, and nothing associates with it there. It is never the subject of a measurement ([`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3b) |
| **EPoL model service** | External system, illustrative | Assess tracks and produce reports, live | Named because the brief names it. **Nothing in this section associates with it directly.** It appears only inside an attached exploitation chain, and a chain that has no separate model service — one monolith, ten stages, a human analyst — changes no step of UC-8. Never given truth; anything it returns is recorded **as received** and is never compared against the truth the same session is holding (UC-8, D2.24) |
| **Automation process** | External software agent, non-interactive | Start a run with an explicit parameter set, observe it in progress through CarlaNet and the Python shim, and stop it — up to and including killing the SUMO or CARLA server, or its own client — the instant it decides it has seen enough | The primary actor of UC-13, and the one whose control over the system is most direct: unlike every other actor it can end a run at an arbitrary instant with no warning, and the system's own postconditions are written around that fact. Also distinct from the capture operator by the test §1.4 applies to the chain: **it cannot answer a question.** A prompt is a hang rather than a dialogue, and a default it did not choose is a silent corruption nobody is watching for — so its preconditions are *stricter* than a human operator's, not looser. Any cadence between invocations, the decision to stop, and whatever trains on what it produced are outside and none of them are ours (brief §3d) |
| **External model team** | External consumer, outside the boundary | Train and validate detect-and-track and EPoL models on a corpus we hand them | It receives a corpus at UC-10 and interacts with the pipeline in no other way — no session, no server, no configuration, no call. Everything it would otherwise have had to ask us must therefore travel **in writing** with the corpus: what it contains, what it does not, in what light, and the rule by which supervision transfers onto its own tracks |
| **Solar authority (`CesiumSunSky`)** | In-world system | Hold the sun's clock, date, time zone and resulting angles, and light the world from them | `CarlaServer.cpp:611-612` names it *the single sun and lighting authority for the georeferenced world*, with CARLA's own weather inert there. It is a distinct actor because it holds **state that outlives a session** (§1.2) and because it can refuse — every solar call returns false when no `CesiumSunSky` exists in the world (`CesiumHeightSampler.cpp:726`, `:830`) |
| **OpenStreetMap** | External data source | Supplies the extract | |
| **Cesium ion** | External data source | Supplies photoreal imagery and world terrain | |

A separate "illumination owner" actor was considered and rejected: it would be a role nobody occupies,
and it would put two people in the launch path for one decision that has to be made once per run. §1.1
sharpens what two existing actors — the scenario author and the capture operator — already own instead.

### 1.1 Who owns illumination policy

The requirement raises a genuine ownership question with two claimants, and answering it wrongly in
either direction produces a specific failure. The answer is a split, and the split is not a compromise —
the two halves are different kinds of thing.

| Half | Owner | Why | Scope | Where it is enforced |
|---|---|---|---|---|
| **The epoch** — the civil date, the civil UTC offset, and the civil instant that `t = 0` means | **Scenario author** | It is a statement about what the scenario *means*, without which its annotations are not interpretable. "Guards relieve at 07:00, 15:00 and 23:00" is an authored fact, not a run-time preference. It is also not a free choice at capture: nobody but the author can say whether `t = 82,800` is 23:00 or 11:00 | Scenario-scoped. One epoch per scenario package | Declared in UC-3, checked in UC-5, **refused at compile** |
| **The illumination policy** — frozen or advancing, the rate, and any deliberate departure from the derived civil time | **Capture operator** | It is a property of the *experiment*, not of the scenario. A sweep holds it constant to keep a behavioural comparison clean (UC-6); a replay deliberately varies it to make a second corpus of the same behaviour under different appearance (UC-9, [18 D4](../../Findings/18_Scenario_Fabrication_For_EPoL_Training.md)). The author is not in the loop for either | Run-scoped. One policy per run | Chosen in UC-12, applied in UC-7, recorded in the manifest |

**The operator owns the policy; the operator does not own the civil time.** The window's civil time is
*derived* from the epoch and the window's simulated begin instant. An operator who wants a different
light does not re-state the time — they record an **override**, which marks the corpus as one whose
illumination deliberately does not match its scenario's clock. The distinction matters because an
override is a fact a later reader needs, and a silently different time is indistinguishable from a bug.

**Why not give the author both.** The author cannot know the sweep. Handing the author the freeze/advance
choice would mean a run list could not vary illumination without editing the scenario, which would change
the scenario's identity and therefore the world-digest-and-recipe binding UC-5 step 1 rests on. Behaviour
and appearance would stop being separate axes, which is the property [18
D4](../../Findings/18_Scenario_Fabrication_For_EPoL_Training.md) exists to preserve.

**Why not give the operator both.** An operator handed a bare hour would be inventing the scenario's
meaning at launch. The measured state of the sizing scenario is exactly this failure already in progress:
its civil hours exist only in the generator's source and in identifier text (§3, UC-3), so any operator
today would be guessing.

This is the same shape as open question 2 (who chooses the capture *window*), and §8 now recommends they
be answered identically: **the scenario declares, the operator selects, the manifest records.**

### 1.2 One property of the solar authority that changes several use cases

The world's sun is spawned with `SolarTime = 12.0` — local solar noon — and daylight saving disabled
(`CesiumHeightSampler.cpp:409-410`), with the time zone derived from the origin longitude
(`:412`, `EstimateTimeZoneForLongitude`, which sets `TimeZone = longitude / 15`). That spawn happens
**only when no `ACesiumSunSky` already exists in the world** (`:396-402`, `if (!bHasSunSky)`).

**Inference, from that guard:** on a server that keeps a world loaded across successive sessions, the sun
is *not* re-defaulted between them. A second capture inherits whatever the first one set. So "the default
is noon" is true only of a freshly generated world, and no use case may rely on it. Every session that
renders must position the sun explicitly and read it back. This is why D2.15 exists.

A second reading, with the same consequence: the advancing controller wraps the solar clock at 24 h and
**never advances the calendar date** — `SolarTime = Fmod(Fmod(SolarTime + DeltaHours, 24) + 24, 24)`
(`CesiumTimeOfDayController.cpp:34-35`). A window that crosses midnight therefore keeps the date it
started on, and the date is what drives the seasonal sun angle and what the truth sidecar records
(`CotUdpEmitter.py:160` writes `date` into the `<_solar>` element). Over a seven-day scenario the
declination error is small; the *recorded* date being wrong is not small, because it is the same class of
internally contradictory record the whole requirement exists to prevent. Named as an open question in §8
for [11](11_Time_And_Illumination.md), and as a postcondition obligation in UC-7.

### 1.3 The model actors are outside the boundary

**"Model evaluator" is not an actor of this system. "Model trainer" is not an actor of this system
either.** Both are represented by a single external actor, the **External model team**, drawn outside
the boundary and associated with **exactly one** use case — UC-10, the handoff — and with no other. It
never starts a session, configures a run, loads a world, or calls anything.

**Three reasons, in increasing order of force.**

1. **Neither ever interacts with the pipeline.** Both only ever receive artifacts. An actor with no
   interaction with the system is a stakeholder drawn inside the boundary, which is the standard way a
   use-case model acquires scope it does not have.
2. **The pipeline cannot tell them apart, and must not try.** The same corpus serves training and
   validation; the only thing that differs between the two is *which export* is read, and that is a
   property of the artifact ([06 §10.2–10.3](06_Truth_And_Annotation.md)'s training export and full
   export, written as two separate artifacts rather than two views) and not of who is reading. A boundary
   drawn on the artifact holds; a boundary drawn on the reader's intention does not.
3. **Keeping "evaluator" as an actor keeps an evaluation use case.** An actor exists to drive a use case.
   Retaining this one would retain a case whose entire content is a model metric, which the scope
   decision removes. The actor and the use case go together.

**Their requirements on the corpus stay, enforced elsewhere:**

| Requirement | Where it lives |
|---|---|
| Know the corpus's illumination strata, to know what a training set is balanced over | Published corpus metadata: prevalence in three units **per illumination band**, with the band cut points recorded beside the numbers ([06 §5.3](06_Truth_And_Annotation.md)); handed over at UC-10 step 4; D2.19 |
| Compute a denominator the capture cannot silently distort | Observability accounting: observed intervals gated first on rendered spans, published per sensor and unioned, with admissions, releases, refusals and cap-bound spans alongside ([06 §5.1](06_Truth_And_Annotation.md), [§10.4](06_Truth_And_Annotation.md)); handed over at UC-10 steps 4 and 5; D2.8 |
| Read it per illumination stratum as well as per sensor | Both breakdowns are published, and both are refused as a pooled aggregate (UC-10 failure flows) |

The pattern is the same in all three rows: **we compute and publish the quantity a score would need; we
do not compute the score.**

**UC-11, the corpus audit, is not the model team's either.** The audit is a data-quality gate on **our
own** data, and the person best placed to adjudicate an accidental positive is the one who wrote the
annotations. Its actors are the **capture operator**, who owns the corpus, with the **scenario author**,
who owns what was and was not asserted. Nobody outside the boundary runs it, and its report is an input
to the handoff rather than something the recipient is left to reconstruct (UC-10 step 2).

### 1.4 The attached chain, the automation process and the handover recipient are three different actors

The **External model team** §1.3 names is right for the *handover*: it receives a corpus and interacts
with the pipeline in no other way. A live exercise is a different relationship — **the chain is running,
attached, and returning data**. Unattended invocation is different again — **the automation process is
the one thing in this whole actor list that can end a session outright, at any instant, on purpose.**
Are these the same actor wearing different hats, or three distinct actors?

**The ruling: three distinct actors.** The deciding test is not who they are — it may well be the same
organisation behind all three — but **whether the system's own behaviour depends on them.**

| | External model team (UC-10) | Attached exploitation chain (UC-8) | Automation process (UC-13) |
|---|---|---|---|
| When it exists, relative to a session | after it, and only after | during it | before it, and it may end it at any instant during it |
| Direction of interaction | receives, one way | receives a stream; may return data | issues the invocation; reads state through CarlaNet and the Python shim; may terminate the session or the server outright |
| Can it make the system refuse or degrade? | **No.** Every obligation to it is discharged before it appears | **Yes.** Unreachable is a session precondition failure; slow triggers the drop policy and changes what the coverage record says | **Yes, most directly of the three.** It can end the run with no warning and no closing step, at any tick, and every postcondition of UC-13 is written around that being normal rather than exceptional |
| Does anything it produces become an artifact we hold? | Nothing comes back | Yes — the exercise transcript, as received (UC-8 step 7) | No. It only ever reads; nothing it sends becomes part of the corpus |
| What the system has to know about it | its name, for the handoff record | its reachability, and whether it is keeping up | nothing about its identity or its intentions — only that a query or a termination can arrive at any tick, and that it will never answer a prompt |
| Where its failure is written | nowhere; there is no run to fail | the session's failure flows, the drop counter, the coverage record | nowhere — it is not the automation process that can fail here; a run stopped by it is a normal outcome, and what must hold instead is that whatever is on disk at that instant is valid (UC-13 postconditions) |

**An actor whose state is an input to our control flow and an actor that is a pure downstream sink
cannot be one actor**, and an actor that can terminate the system outright is a third thing again, more
tightly coupled to our behaviour than either. They carry different preconditions, different failure
flows and different lifetimes. Merging any two would draw the boundary on *who is reading* rather than
on *what the system must do*.

**The automation process is not the capture operator, and the discriminator sharpens the point rather
than softening it.** A human operator can be asked a question and can choose to stop a session
gracefully; the automation process can do neither — it cannot answer a prompt (a prompt is a hang, not a
courtesy), and its stop is not a request we service but an event we must already be safe against, up to
and including the SUMO or CARLA server being killed out from under the session. That is the strongest
form the discriminator takes anywhere in this table: for the model team the system's behaviour never
depends on the actor; for the chain it depends on the actor's reachability and speed; for the automation
process it depends on the actor's continued existence at all. The ruling holds, and holds more firmly
than it did before this was understood.

**That the same organisation may occupy more than one of these roles is irrelevant, and this section
already holds the rule that says so.** §1's own preamble: *"Two of these are the same person wearing
different hats on different days, and they are kept apart because their preconditions differ."* An actor
is a role, not a party. The scenario author and the capture operator are separated on exactly that basis;
so are the live chain, the automation process and the handover recipient.

**What this ruling does *not* license: drawing the chain's internals.** §1.3's finding — that a model is
never an actor *of this system* in any role where it is the subject — stands, in a second direction here:
the chain is **one** actor because one is all we can honestly claim to know. Associating UC-8 with a
detector *and* a model service, in that order, would assert a two-stage external topology this plan has
no basis for and the brief explicitly forbids: *"We know nothing about the external projects, and the
plan must not pretend otherwise."* Both remain in the actor table, named because the brief names them,
and marked as the composition of *one illustrative adapter* (D2.26).

**The falsifiable form of the rule, which is how to check this case is written correctly.** Substitute a
completely different detector and a completely different model service — or a single monolithic service,
or a chain of ten, or a human analyst, or a consumer that returns nothing at all. **No step of UC-8 may
change.** If a reader finds one that would have to, the step is wrong and UC-8 is the thing to fix.

## 2. The use-case diagram

```mermaid
flowchart LR
    AUTH["Scenario<br/>author"]
    ASSIST["Authoring<br/>assistant"]
    WOP["World-builder<br/>operator"]
    COP["Capture<br/>operator"]
    LOP["Live exercise<br/>operator"]
    AUTO["Automation<br/>process"]

    OSM["OpenStreetMap"]
    ION["Cesium ion"]
    SUN["Solar authority<br/>CesiumSunSky"]
    TAK["TAK / CoT<br/>consumer"]
    CHAIN["Attached exploitation chain<br/>composition unspecified"]
    ADPT["ONE ILLUSTRATIVE ADAPTER,<br/>not a specification:<br/>detect-and-track ➜ EPoL service ➜ reports.<br/>Substitute any other chain —<br/>no step of UC-8 changes"]
    INSTR["Stock detect-and-track,<br/>as an instrument"]
    MTEAM["External<br/>model team"]

    subgraph SYS["SUMO behavioural capture system"]
        direction TB
        UC1(["UC-1 Build a world<br/>from an OSM extract"])
        UC2(["UC-2 Publish a world's<br/>authoring reference set"])
        UC3(["UC-3 Author a SUMO scenario<br/>against a world<br/>incl. declaring the epoch"])
        UC4(["UC-4 Declare behavioural<br/>annotations on a scenario"])
        UC5(["UC-5 Validate a scenario<br/>before running it"])
        UC6(["UC-6 Expand a scenario<br/>into a run list"])
        UC12(["UC-12 Configure and<br/>launch a run"])
        UC7(["UC-7 Run a captured<br/>dataset collection"])
        UC8(["UC-8 Run a live exercise against<br/>an attached exploitation chain"])
        UC9(["UC-9 Replay a<br/>recorded run"])
        UC13(["UC-13 Run a session an external<br/>process starts, observes<br/>and stops"])
        UC11(["UC-11 Audit a corpus for<br/>accidental positives and<br/>illumination leakage"])
        UC10(["UC-10 Hand a corpus to an<br/>external model team"])
    end

    WOP --- UC1
    WOP --- UC2
    OSM --- UC1
    ION --- UC1
    UC1 -.->|"includes"| UC2

    AUTH --- UC3
    AUTH --- UC4
    ASSIST --- UC3
    ASSIST --- UC4
    ASSIST --- UC5
    UC2 -.->|"is an input to"| UC3
    UC3 -.->|"includes"| UC5
    UC4 -.->|"includes"| UC5

    AUTH --- UC6
    COP --- UC6
    COP --- UC12
    LOP --- UC12
    COP --- UC9
    UC3 -.->|"epoch and named windows<br/>are inputs to"| UC12
    UC6 -.->|"a run list is<br/>an input to"| UC12
    UC12 -.->|"includes"| UC5
    UC12 -->|"launches"| UC7
    UC12 -->|"launches"| UC8
    UC7 -.->|"includes"| UC5
    UC9 -.->|"extends"| UC7

    AUTO ---|"starts, with an explicit<br/>parameter set"| UC13
    UC13 -.->|"includes, invoked<br/>non-interactively"| UC12
    AUTO -.->|"queries state, mid-run,<br/>via CarlaNet + Python shim"| UC13
    AUTO ==>|"stops us — at will, up to<br/>and including a kill.<br/>Expected, not a failure"| UC13
    UC13 -.->|"whatever is on disk,<br/>honestly marked stopped<br/>or finished"| AUTO
    UC6 -.->|"varied parameters<br/>are an input to"| UC13

    UC7 --- SUN
    UC8 --- SUN
    UC9 --- SUN

    UC8 --- TAK
    UC7 --- TAK
    UC8 ==>|"collection frames, live,<br/>paced against the wall clock"| CHAIN
    CHAIN -.->|"anything it returns, recorded<br/>verbatim as an opaque transcript"| UC8
    CHAIN -.- ADPT

    COP --- UC10
    COP --- UC11
    AUTH --- UC11
    UC7 -.->|"produces a corpus for"| UC10
    UC9 -.->|"produces a corpus for"| UC10
    UC10 -.->|"includes"| UC11
    UC11 -.->|"uses a stock detector<br/>as an instrument"| INSTR
    UC10 ==>|"corpus, its statement<br/>of contents, its audit,<br/>and the transfer rule"| MTEAM

    style ADPT stroke-dasharray: 5 5
```

**The boundary is the point of this diagram, so read the model-side edges carefully.** There are exactly
four, and no fifth is permitted.

- **UC-8 ⇒ `CHAIN`, and one dashed edge back.** The session *feeds* whatever is attached, in real time,
  and *records* what comes back as received. **`CHAIN` is one node on purpose**: we know nothing about
  the external projects' composition, so no edge may assert a two-stage detector-then-model-service
  topology (§1.4, D2.26). Nothing in the system compares the returned material with the truth the same
  session holds (UC-8, D2.24).
- **`CHAIN` --- `ADPT`, dashed, associated with no use case.** The detector-then-model-service picture is
  drawn **once**, as a note, so a reader has something concrete to hold — and it is drawn outside every
  association so that it cannot be mistaken for the interface. Substitute a different chain and no edge
  in this diagram moves.
- **UC-11 --- `INSTR`, labelled as an instrument.** The corpus fitness probe runs a stock detector over
  *our* data to ask whether *our* data yields trackable targets. The detector is the thermometer, not the
  oven (brief §3b item 1; [08 §12](08_Collection_And_EPoL.md) owns the probe's design).
- **UC-10 ⇒ `MTEAM`, one way.** The corpus and its guarantees leave; nothing comes back into the system.
  A recipient's findings about a model are theirs, and there is no edge on this diagram for them to
  arrive on.

**The automation edges are the other thing to read, and the direction of the heavy arrow is the point.**
It runs from `AUTO` **into** `UC13`, not the other way round: the automation process is the one that acts,
by stopping us, and it may do so at any instant — a kill is drawn with the same weight as UC-10's handoff
edge because it is that central to the case. The two dashed edges either side of it are the rest of the
relationship: `AUTO` may query state mid-run, and whatever is on disk when it stops us travels back,
honestly labelled. There is no result to hand back beyond that — no verdict, no aggregate judgement of
the run, because none is ours to make (D2.29).
UC-13 goes **through** UC-12 rather than around it: an unattended run is composed and validated by the
same path a human uses, which is D2.22 and [12](12_Operator_Control_Surface.md) D12.5, and is why UC-13
needs no second validator.

**On the numbering.** UC-12 is the entry point to UC-7 and UC-8 and belongs before them in reading order,
but it is numbered last-but-one on purpose: UC-1 through UC-11 are cited by number from other sections in
this folder, and renumbering them to make the diagram read left to right would invalidate every one of
those citations for a cosmetic gain. UC-10 keeps its number for the same reason —
[01](01_Architecture.md) cites `02 UC-10` in its open question 8 — and UC-11 is drawn above it because
the audit is *included by* the handoff. UC-13 is numbered after every existing case so that none of those
citations ever has to move.

---

## 3. The use cases

Each is stated in the same shape. "Failure" means the system refuses and says why; "alternate" means a
legitimate different path through the same case.

### UC-1 — Build a world from an OSM extract

| | |
|---|---|
| **Primary actor** | World-builder operator |
| **Supporting** | OpenStreetMap, Cesium ion, CARLA server |

**Preconditions.** A headless CARLA server is running; `CESIUM_ION_TOKEN` is set; `netconvert` is on the
staged install (`run_SCTMV.py:60-78` resolves it from `CARLA_NETCONVERT` or the in-repo build); an OSM
extract exists with a `<bounds>` element; an optional `<extract>.aoi.geojson` sits beside it.

**Main flow.**
1. Operator names the extract, the height-align mode and the package destination.
2. `OsmClipper` cuts the extract to its bounds, preserving node ids **and OSM relations** — the turn
   restrictions [issue #12](https://github.com/sbrett9/carla/issues/12) records as discarded today, which
   [01 §2.5](01_Architecture.md) makes a prerequisite of this system.
3. `RoadNetworkConverter` runs `netconvert` **once**, emitting the OpenDRIVE and the SUMO network from the
   same invocation at the same pinned origin.
4. Elevation, sign and traffic-light injection run against the OpenDRIVE.
5. The world is generated on the server; Cesium streams; the drape samples true ground height per cell.
6. `WorldPackageWriter` writes `world.json`, `map.xodr`, **`map.net.xml`** and `bareearth.bin`.
7. `AreaOfInterestCompiler` validates any supplied areas against the OSM bounds, the sandbox rectangle and
   the road network, and resolves them to CARLA-local metres.
8. UC-2 runs as part of this case.

**Alternate flows.**
- *No areas supplied* — step 7 proceeds with an empty area table; area-relative authoring is then
  unavailable and UC-5 says so.
- *Secure site with a private interior* — the road filter is turned off so `access=private` roads survive,
  and the fence is applied during authoring rather than at build.

**Failure flows.**
- *Cesium does not stream* — height sampling has nothing to sample. Refuse the build rather than emit a
  world whose drape is a plane; a silently flat world produces vehicles floating or buried and is
  discovered much later.
- *An area's envelope is disjoint from the OSM bounds* — refuse, per
  [20 §8.3](../../Findings/20_Behavioral_Annotation_And_Areas_Of_Interest.md). An area that crosses the
  sandbox edge, or that sits inside the staging ring, or that has no drivable road near it, warns.
- *`netconvert` reports restriction relations it cannot apply* — warn and record the count in the build
  recipe, because under SUMO drive a discarded restriction becomes visible bad behaviour and must not be
  mistaken for a bridge defect ([23 §6.6](../../Findings/23_SUMO_Traffic_Integration.md)).

**Postconditions.** A world package exists; the server holds the world; the OpenDRIVE's digest is the
world's identity ([18 §5.5](../../Findings/18_Scenario_Fabrication_For_EPoL_Training.md)).

**Artifacts.** World package (`world.json`, `map.xodr`, `map.net.xml`, `bareearth.bin`), the resolved area
table, the build recipe.

---

### UC-2 — Publish a world's authoring reference set

| | |
|---|---|
| **Primary actor** | World-builder operator |
| **Supporting** | CARLA server |

**Preconditions.** A world is loaded on a running server.

**Main flow.**
1. `VehicleCatalogueGenerator` projects the server's blueprint definitions into a versioned vehicle
   catalogue carrying, per entry, the real bounding-box dimensions and the attributes truth already reads
   — `base_type`, `special_type`, `number_of_wheels`, `color`
   ([20 §5.6](../../Findings/20_Behavioral_Annotation_And_Areas_Of_Interest.md)).
2. A street-name-to-road index is extracted from the generated OpenDRIVE. This is what makes a description
   in street names resolvable: non-junction roads carry the real name, junction internals carry the
   converter's own edge identifier ([20 §5](../../Findings/20_Behavioral_Annotation_And_Areas_Of_Interest.md)).
3. The annotation vocabulary and its version are attached.
4. The area table from UC-1 is attached.
5. **The world's solar frame is attached** — the pinned origin latitude and longitude, and the time zone
   the engine will derive from that longitude (`longitude / 15`, `CesiumHeightSampler.cpp:411-412`). This
   is what lets UC-5 check an epoch's civil offset against the world **without a running server**, which
   is the property D2.2 protects. It is a published *fact about the world*, not a policy.
6. All of it is stamped with the world digest and published as the `AuthoringReferenceSet`.

**Alternate flow.** *A world built earlier, reopened* — the set is regenerated from the running server
rather than read from disk, so it can never describe a different content build from the one loaded.

**Failure flows.**
- *The catalogue is empty or the blueprint filter matched nothing* — refuse. An author given an empty
  catalogue writes a scenario that resolves to nothing.
- *The street index is degenerate* (every road named by an edge identifier) — warn loudly; the world is
  usable but cannot be authored against in ordinary language, which is the primary authoring path.
- *The world has no solar authority* — warn, and mark the reference set as one that cannot support a
  windowed capture. A world with no `CesiumSunSky` refuses every solar call
  (`CesiumHeightSampler.cpp:726`, `:830`), so a capture against it can only ever be a capture in unknown
  light. Finding that out at build time is much cheaper than finding it at session start.

**Postconditions.** Authoring can proceed without a running server.

**Artifacts.** Vehicle catalogue, street-name index, area table, vocabulary, solar frame, world digest —
versioned together.

---

### UC-3 — Author a SUMO scenario against a world

| | |
|---|---|
| **Primary actor** | Scenario author |
| **Supporting** | Authoring assistant, SUMO toolchain |

**Preconditions.** An `AuthoringReferenceSet` and a world package exist. No CARLA server is needed —
authoring runs on any machine with SUMO ([`sumo-traffic-scenarios/SKILL.md`](../../../../../.agents/skills/sumo-traffic-scenarios/SKILL.md)).

**Main flow.**
1. Author describes the scene in ordinary terms: where traffic comes from and goes, what the ordinary day
   looks like, who is present and when, and what is out of the ordinary.
2. **Author declares the scenario's epoch**: the civil date, the civil UTC offset, and the civil instant
   that `t = 0` corresponds to. This is a required part of the scenario contract, not an optional
   annotation, because without it no sun can be derived for any window and the scenario's own hours are
   not machine-readable. [11](11_Time_And_Illumination.md) owns the epoch's representation; this case owns
   only that the author states it and that the author is the one who states it (§1.1).
3. Assistant resolves every named place against the street index and the area table, and every named
   vehicle class against the catalogue.
4. Assistant reconnoitres the real network: finds the edges for every source, sink, gate and waypoint and
   their access class. **Routes are proved with `duarouter`, never with a graph walk** — a graph walk
   gives false positives by traversing one-way edges the real router refuses, and the scenario then fails
   at load with "no valid route" (measured gotcha, recorded in the skill).
5. Assistant writes the demand: flows for the ordinary population, scheduled vehicles for the ones with a
   story. Everything is merged onto **one departure-sorted timeline**, because SUMO silently drops
   out-of-order entries with only a warning (measured gotcha; `SumoPatternOfLifeBuilder` merges for
   exactly this reason). Every departure instant the author thinks of as an *hour* is written as a
   simulated second **through the epoch**, so the two can never disagree.
6. Assistant writes the configuration: step length, seed, end time, and the SUMO-side policies that affect
   reproducibility — the sizing scenario sets `time-to-teleport` to `-1` so a jam stays a jam
   (**measured** in `BahonarPatternOfLife.zip`'s `.sumocfg`, read 2026-09-17).
7. **Author declares the scenario's named windows**, if it has any: the spans of simulated time worth
   capturing, each with a name that says why. A window is the author's suggestion, not the operator's
   obligation (§1.1, open question 2).
8. UC-4 runs for anything the author asserts is a behaviour.
9. UC-5 runs; failures return to step 3.
10. Author reads back the validator's resolution report — which now prints each declared window's
    **derived civil date and time** beside its simulated span — and confirms it says what they meant.

#### Why step 2 is new, measured

The sizing scenario maps simulated seconds to civil hours, and nothing machine-readable says so.
Measured 2026-09-18 by reading `BahonarPatternOfLife.zip` and the live generator
`CarlaControl/scripts/make_bahonar_scenario.py`:

| Evidence | Reading |
|---|---|
| Guard-shift departures | 25,200 / 54,000 / 82,800 s and every 28,800 s after, i.e. **07:00, 15:00, 23:00** with `t = 0` at midnight of day 0 (measured: 335 `guard_*` trips in the `.rou.xml`, distinct depart values on an 8-hour cycle) |
| Where the hours live in the artifact | Only inside identifier text — `ferry_in_d0_h6` (`make_bahonar_scenario.py:190`), `shift_in_d0_h7` (`:206`), `guard_d0_h7_t3` (`:238`) |
| Where the hours live in the source | `HOUR = 3600`, `DAY = 24 * HOUR` (`make_bahonar_scenario.py:76-77`), `FERRY_HOURS = [6, 8, 10, 12, 14, 16, 18]` (`:162`), `SHIFT_HOURS = [7, 15, 23]` (`:164`), composed as `begin = day * DAY + hour * HOUR` (`:188`, `:204`) |
| An illumination assumption already baked into the demand | `# Ferry sailings (local hours) -- daylight only, none overnight.` (`make_bahonar_scenario.py:161`) — the author has *already* reasoned about light, and nothing carries that reasoning into the world |
| What the shipped scenario states | Nothing. The `.rou.xml` contains **zero** occurrences of `epoch`, `date`, `civil`, `timezone`, `time_zone` or `utc` (measured by string count over the whole file); the `.sumocfg` declares `begin`, `end` and `step-length` and no calendar at all |

The mapping is therefore in the generator's source and in the author's head. Neither is readable by the
thing that has to set a sun.

**Alternate flows.**
- *Author works by hand* — every convention the assistant uses is documented and validated, so a
  hand-written scenario passes or fails the same checks. This is a requirement, not a courtesy
  ([20 decision 13](../../Findings/20_Behavioral_Annotation_And_Areas_Of_Interest.md)). It applies to the
  epoch too: a hand-written scenario declares one or it does not compile.
- *Secure site* — private roads are kept, then fenced per vehicle class. The internal junction-connector
  lanes must be cleared too, or the restricted class cannot cross any junction and the interior fragments
  (the subtlest measured bug of the Bahonar build, recorded in the skill).
- *Author starts from an existing scenario* — it is copied and re-bound to a new world digest, and every
  reference is re-resolved rather than assumed to carry. **The epoch is re-examined explicitly**, because
  a scenario re-sited to a different longitude keeps its civil offset only if the site's civil offset is
  the same, and the site is exactly what changed.
- *Migrating a scenario that predates the epoch* — the epoch is supplied once, by the author, and the
  validator prints what every existing departure instant now means in civil time so the author can check
  it against what they originally intended. There is no inference path: the tooling never guesses an
  epoch from identifier text, because `guard_d0_h7_t3` is a convention nobody promised to keep.

**Failure flows.**
- *No epoch declared* — error. The scenario does not compile. Not a warning, and not a default of
  midnight: a defaulted epoch is indistinguishable in the artifact from a declared one, which reproduces
  the exact failure this requirement exists to remove.
- *An epoch the author cannot have meant* — a UTC offset outside `[-12, +14]`, an offset that is not a
  multiple of 15 minutes, an impossible date. Error, naming the field. Half-hour and quarter-hour offsets
  are real and must pass: the sizing scenario's site is Bandar Abbas, Iran (**measured**: the clipped
  extract's `<bounds>` is `minlat=27.13110 minlon=56.14426 maxlat=27.16914 maxlon=56.21704`), whose civil
  offset is **+03:30**.
- *A named street does not resolve* — error naming the street and offering the nearest matches. Never a
  silent nearest-match substitution.
- *A movement through a junction is named as a turn* — junction internals have no human name
  ([20 open question 8](../../Findings/20_Behavioral_Annotation_And_Areas_Of_Interest.md)), so the
  assistant must express it as the roads either side, and the error must say that rather than reporting
  the name as unknown.
- *A route no router can find* — error naming the origin, the destination and the vehicle class, because
  the usual cause is an access class the fence excluded.

**Postconditions.** A scenario exists that loads in SUMO, whose every reference resolves against one named
world, and **whose every simulated instant has an unambiguous civil time**.

**Artifacts.** `ScenarioPackage` — `map.net.xml`, `.rou.xml`, `.sumocfg`, the epoch declaration, any named
windows, the world digest binding, and, once UC-4 has run, the `AnnotationSet`.

---

### UC-4 — Declare behavioural annotations on a scenario

| | |
|---|---|
| **Primary actor** | Scenario author |
| **Supporting** | Authoring assistant |

**Preconditions.** A scenario in progress, **with an epoch declared** (UC-3 step 2); a declared annotation
vocabulary and its version.

**Main flow.**
1. Author names each phenomenon they are asserting: what it is, who takes part, in what role, over what
   spans.
2. Author marks the deliberately ordinary vehicles as `nominal` — the authored hard negatives that make
   duration alone stop being the signal ([20 §2.7](../../Findings/20_Behavioral_Annotation_And_Areas_Of_Interest.md)).
3. **An annotation whose definition includes an hour says so as an hour**, in civil time, resolved through
   the epoch to the simulated seconds the interval actually spans. Doc 20's pattern class 4 — *a heavy
   goods vehicle in a residential area at 03:00* — is a pattern the epoch makes expressible and whose
   absence made it unrenderable (brief §3a). The annotation names **the hour**, never the light.
4. `BehaviouralAnnotationCompiler` produces the `AnnotationSet`: pattern instances with participants,
   roles, intervals and parameters, and instance ids derived deterministically from the scenario and the
   authored name so a sweep's runs are joinable. Each interval carries its civil time alongside its
   simulated span, derived once, so no downstream consumer re-derives it differently.
5. **Any instance whose distinguishing feature is its hour is flagged for UC-11**, because its label will
   correlate with illumination by construction and someone has to have checked (UC-11, D2.20).
6. Every vehicle the author said nothing about is `unlabelled`, and that state is written explicitly
   rather than by omission ([20 decision 2](../../Findings/20_Behavioral_Annotation_And_Areas_Of_Interest.md)).

**Alternate flows.**
- *An anomaly that is an absence* — a guard who never arrives has no vehicle to attach to. The sizing
  scenario already carries one such case in its `anomaly_notes` (**measured**: a `guard_no_show` covering
  tower 3 over `begin_s = 370,800` to `end_s = 399,600`, which the epoch resolves to **day 4, 07:00 to
  15:00**). It is a pattern instance with a participant that is expected and does not appear, with a
  place and a window, and the compiler must be able to express it rather than relegating it to a note.
  Note that this instance is *defined* by a shift boundary, so it is a step 5 case.
- *An hour-defined pattern with no vehicle-level signature* — permitted, and precisely the class the epoch
  unlocks. It is also the class most exposed to the confound, so it is the class UC-11 must be strongest
  on.

**Failure flows.**
- *A label outside the vocabulary* — error. A corpus assembled from scenarios that each spelled `loiter`
  differently is not a corpus ([20 §6.2](../../Findings/20_Behavioral_Annotation_And_Areas_Of_Interest.md)).
- *A participant naming a vehicle the scenario does not contain* — error.
- *An annotation derived from geometry* — cannot happen, because no geometric predicate is an input to
  this case at all. This is stated as a failure to make the boundary visible where it is most likely to
  erode ([20 §8.5](../../Findings/20_Behavioral_Annotation_And_Areas_Of_Interest.md)).
- *An annotation that names an illumination state* — error, for the same reason and with the same force.
  "At night", "in darkness", "under low sun" are not labels; **03:00** is. Illumination is derived context
  computed identically for every capture, never a supervision signal (brief §3a, standing constraint).
  Sun elevation is not an input to this case at all, which is what makes the error statable.
- *An interval declared in civil time that the epoch cannot resolve* — error rather than a wrap. An
  interval at 25:30, or on a date outside the scenario's span, is a mistake and reads as one.

**Postconditions.** Every assertion the author made exists as a record; nothing the author did not assert
does; every interval is expressible in both simulated seconds and civil time.

**Artifacts.** `AnnotationSet`, the resolution report of what each annotation bound to, the list of
hour-defined instances handed to UC-11.

---

### UC-5 — Validate a scenario before running it

| | |
|---|---|
| **Primary actor** | Authoring assistant |
| **Supporting** | Scenario author, SUMO toolchain |

**Preconditions.** A `ScenarioPackage`; the `AuthoringReferenceSet` it was authored against.

**Main flow.**
1. **Bind.** The package's world digest matches the world it names. A mismatch in the build recipe is a
   refusal; a digest mismatch with a matching recipe is a warning requiring an explicit override
   ([18 §5.5](../../Findings/18_Scenario_Fabrication_For_EPoL_Training.md)'s two-tier gate).
2. **Frame.** The network's `convBoundary` equals the OpenDRIVE header's extent, and `netOffset` is zero —
   the check that the coordinate identity holds.
3. **Epoch.** Present; the date is real; the civil UTC offset is within `[-12, +14]` and a multiple of 15
   minutes; the instant `t = 0` maps to is stated. **A missing or absurd epoch fails here, at compile, not
   at capture** — which is the whole point of putting the check in this case rather than in UC-7.
4. **Epoch reach.** Every instant the scenario can reach — `t = 0` through the configured `end` — resolves
   to a representable civil date and time. This is a real check, not a formality: the sizing scenario's
   `end` is 604,800 s (**measured**, `<end value="604800"/>`), so `t` alone rolls the calendar over six
   times, while the engine's solar clock wraps at 24 h and carries no date
   (`CesiumHeightSampler.cpp:730`; `CesiumTimeOfDayController.cpp:34-35`). A validator that checks only
   the hour would pass a scenario whose day-6 window renders under day-0's sun.
5. **Solar frame.** The epoch's civil offset is compared against the time zone the engine will actually
   use, which the reference set carries from UC-2 step 5. The two are different kinds of quantity and they
   do not have to agree — but the difference must be computed, reported, and carried into the run record
   rather than silently absorbed. **Measured for the sizing scenario:** longitude spans 56.14426 to
   56.21704, so `longitude / 15` gives **3.743 – 3.748 h**, against Iran's civil **+03:30 = 3.5 h** — a
   standing offset of ≈ **0.245 h, or 14.7 minutes**, equivalent to ≈ 3.7° of solar hour angle. At a night
   window that is invisible; at the 07:00 window [10 §3.1.3](10_Scale_And_Performance.md) recommends it is
   a materially different sun. Whether the fix is a conversion in the run path or a settable time zone on
   the server is [11](11_Time_And_Illumination.md)'s decision and §8's open question 8; this case only
   insists the quantity exists and is never zero by assumption.
6. **Window sanity.** Each declared named window lies inside `[0, end]`, and its derived civil date and
   time are printed. A window whose derived civil time places the sun below the horizon is **not** an
   error — a night capture is a first-class goal (brief §3a) — but it is called out, because a night
   window authored by accident and a night window authored on purpose look identical in simulated seconds.
7. **Routes.** Every origin-destination and waypoint route is proved with `duarouter`.
8. **Order.** The route file is departure-sorted across flows and vehicles.
9. **Types.** Every `vType` binds to at least one catalogue entry within the dimension tolerance. The
   sizing scenario spans 4.4 m to 12.0 m across 14 types (**measured**), so this is a real matching check.
10. **Annotations.** Every label is in the vocabulary, every participant exists, every area reference
    resolves, every interval resolves in both simulated seconds and civil time, and no annotation names an
    illumination state.
11. **Clock.** The SUMO step length, the intended world delta and the intended capture rate are in integer
    ratio ([01 §6.1](01_Architecture.md)).
12. **Identifier convention, advisory.** Where flow and vehicle identifiers carry an hour by convention —
    `ferry_in_d0_h6`, `shift_in_d0_h7`, `guard_d0_h7_t3` (`make_bahonar_scenario.py:190`, `:206`,
    `:238`) — the
    validator checks the identifier against the hour the epoch derives, and **warns** on disagreement.
    Advisory and not a refusal, deliberately: the convention is a habit of one generator, not a contract,
    and a scenario that does not follow it is not wrong. It is still the cheapest available detector of an
    epoch declared off by a day or an hour, because it cross-checks the author's two independent
    expressions of the same intent.
13. **Report.** The validator prints what it *resolved*, not only what it rejected — the roads a street
    name bound to, the catalogue entries each type bound to, the areas each annotation bound to, **and the
    civil date and time of every declared window and every annotation interval**. A preview cannot check
    any of this ([20 §5.5](../../Findings/20_Behavioral_Annotation_And_Areas_Of_Interest.md)), so the
    report is the only place an author sees whether the scenario says what they meant.

**Alternate flow.** *Validation without a world* — steps 1, 5 and 9 need the reference set but not a
server, so the whole case runs offline. This matters because authoring is the part of the workflow that
does not need a GPU, and it is why UC-2 step 5 publishes the solar frame as a fact rather than making the
validator ask a running server for it.

**Failure flows.** Any of steps 2 through 11 failing refuses the package. Step 1 is the two-tier gate;
step 12 is advisory. A dry run whose population climbs monotonically rather than settling is a demand
error, not a validator error, and is reported as a warning with the numbers rather than as a pass.

**Postconditions.** The package is either refused with a named reason, or accepted and stamped with the
validator's version, the resolution report, and the computed solar-frame offset.

**Artifacts.** Validation report; a validated `ScenarioPackage`.

---

### UC-6 — Expand a scenario into a run list

| | |
|---|---|
| **Primary actor** | Scenario author, with the capture operator |

**Preconditions.** A validated `ScenarioPackage`.

**Main flow.**
1. Author sets the bounds of the behavioural parameters to sweep and the appearance parameters to cross
   them with — **illumination**, weather, camera track. Behaviour and appearance are separate axes
   ([18 D4](../../Findings/18_Scenario_Fabrication_For_EPoL_Training.md)).
2. The machine expands the cross product into a run list, each entry carrying a seed, a capture window, a
   camera set, the instance ids the variation touched, and **an illumination stratum**: the derived civil
   time, the policy (frozen or advancing), the rate, and any override.
3. Instance ids stay stable across the expansion, so one authored pattern's variants can be diffed.

#### 6a. Illumination is an axis, and it is a confounding axis

Stated separately because it is the one axis that changes the pixels without changing the behaviour, and
therefore the one most able to contaminate a comparison that looks clean.

**As an axis, it is the cheapest one available.** Varying it requires no re-authoring and no different
demand: the same scenario, the same seed, the same window, a different sun. It is also the axis that
matters most to the consumer, because illumination is the single largest covariate an electro-optical
detector faces (brief §3a).

**As a confound, it is the most dangerous one available.** A sweep whose purpose is to vary *behaviour*
must hold illumination **constant** — same derived civil time, same policy, same rate — or the difference
between two runs contains a lighting difference as well as a behavioural one, and nothing downstream can
separate them. The rule and its consequences:

| Sweep intent | Illumination must be | Because |
|---|---|---|
| Vary behaviour (which pattern fires, which parameter) | **Constant and frozen** | A frozen sun makes illumination identical frame-for-frame across the arm, so the only difference is the one being studied |
| Vary illumination (the appearance axis) | **The swept variable**, behaviour held by seed and window | This is the valid use of the axis, and it is nearly free |
| Both, deliberately | **Marked per entry** so the two can be stratified apart downstream | A consumer cannot stratify what the run list did not record, and the run list is the only place the two axes are still separable |

**Why "frozen" and not merely "the same policy".** An *advancing* sun is constant only if the two arms
occupy identical simulated spans and start identically. A behavioural variation that shifts a departure
by a few seconds, or that changes how long a run takes to reach its window, moves the sun with it. Frozen
removes the coupling entirely. This is an **inference**, from `CesiumTimeOfDayController.cpp:34-36`
advancing the clock by `DeltaSeconds × Rate` on every world tick: anything that changes the tick count
between window start and a given frame changes that frame's sun.

**Alternate flows.**
- *Counterfactual pairing* — the same seed and the same ambient population with one instance's behaviour
  switched off, which isolates the authored behaviour as the only difference between two runs.
  [20 open question 7](../../Findings/20_Behavioral_Annotation_And_Areas_Of_Interest.md) leaves this open;
  the run list is where it would be expressed, and it costs almost nothing here because the seed and the
  parameters are already run inputs. **A counterfactual pair whose two arms have different suns is worth
  nothing**, because the pixel difference the pairing exists to isolate is then partly a lighting
  difference. Frozen illumination is a precondition of the pairing, not a refinement of it.
- *An illumination-only sweep* — one window, one seed, the sun stepped across a set of civil times. The
  strongest available probe of a detector's illumination sensitivity and the natural companion to UC-9.

**Failure flows.**
- *A swept parameter that changes the network* — refused. A sweep that rebuilds the world is not a sweep
  over one scenario, and the world digest would differ between runs.
- *An expansion that varies behaviour and illumination together without marking the stratum* — refused.
  Not because the combination is illegitimate, but because an unmarked entry is unstratifiable later and
  the resulting joint number is uninterpretable. Marking is the whole cost.
- *A swept illumination value the scenario's epoch cannot reach* — refused at expansion rather than at
  launch.

**Postconditions.** A run list whose entries are individually runnable, collectively joinable, and
individually attributable to an illumination stratum.

**Artifacts.** Run list.

---

### UC-7 — Run a captured dataset collection

| | |
|---|---|
| **Primary actor** | Capture operator |
| **Supporting** | CARLA server, solar authority (`CesiumSunSky`), `sumo`, TAK consumer (optional) |

**Preconditions.** A running server with the named world loaded; a validated `ScenarioPackage` **carrying
an epoch**; the toolchain staged with `SUMO_HOME` set; one or more collection cameras configured; a
capture window and a seed; **an illumination policy for this run** — frozen at the window's start instant
or advancing with simulated time at a stated rate, plus any explicit override of the derived civil time.
The policy is supplied by UC-12; there is no default, because a default is how a night window silently
renders in daylight (brief §3a).

**Main flow.**
1. Operator starts a session naming the mode `SumoDrivenPlayback`, the package, the window, the cameras,
   the seed and the illumination policy.
2. `CaptureSession` acquires **population authority** from `WorldDriveAuthority`. If it is held, the
   session start fails naming the holder ([01 §5.3](01_Architecture.md)).
3. The world is put in synchronous mode at the fixed delta; the clock ratio is re-checked against the
   loaded world.
4. **The solar clock is bound to the window.** The session reads the scenario's epoch, derives the civil
   date and time of the window's **begin** instant, sets the date and then the time on the solar
   authority, and reads the result back. Four properties of this step matter and each is grounded:
   - The date is set **before** the time, and it is set explicitly on every run. The advancing controller
     never touches the date (`CesiumTimeOfDayController.cpp:34-35`), so a date left over from a previous
     session or from a window that crossed midnight would silently give the wrong seasonal sun.
   - The readback is **verified**, not assumed. `set_solar_time` returns false when the world has no
     `CesiumSunSky` (`CesiumHeightSampler.cpp:726`), and the value it stores is wrapped modulo 24
     (`:730`), so a derivation error shows up as a clean disagreement between request and readback.
   - The readback is **free**. `get_solar_state` reads the world-observer cache paired to the latest tick
     with no RPC, falling back to an RPC only before the cache is populated
     (`carlanet/__init__.py:1511-1533`; `CarlaClient.cs:1991`). This is the same publication mechanism
     [08](08_Collection_And_EPoL.md) D8.3 chose for world-scoped state, already working for this payload.
   - Nothing is **inherited**. The world's noon default is applied only when no `CesiumSunSky` exists
     (`CesiumHeightSampler.cpp:396-402`), so a world already holding a sun keeps the last session's one.
     §1.2 has the reasoning; D2.15 has the rule.
5. `CaptureSession` assigns one session identity and a stable `sensor_id` per camera, and hands both to
   every recorder — replacing the per-recorder default derived from its own start instant
   (`CarlaNet.Recording/FrameRecorder.cs:101-103`). It also supplies the `scenario_id`, which the
   recorder has always accepted and nothing has ever passed (`NativeRecorder.py:107-108` passes `run_id`
   and `seed` only; this carries forward
   [20 §4.2](../../Findings/20_Behavioral_Annotation_And_Areas_Of_Interest.md)'s gap to the current entry
   point).
6. `SumoSession` launches `sumo` and connects; the `AnnotationSet` is published as `WorldSupervisionState`.
7. The clock steps SUMO with nothing rendered until the capture window's start. **Advancement stays off
   through this pre-roll**, so the sun positioned in step 4 cannot drift before the first captured frame
   (the controller advances only on a world tick and only when advancing is enabled,
   `CesiumTimeOfDayController.cpp:14-37`).
8. **The illumination policy is applied**, immediately before the first cued frame: either advancement is
   left off (frozen), or `set_time_advance(true, rate)` is issued. The policy is then fixed for the run.
9. The capture window runs: per rendered instant, poses are applied, the world is cued, cameras deliver,
   recorders write. Per SUMO step, the render set is reconciled and interval state changes are published.
   The solar state travels with every capture, from the same tick-paired cache the rest of the frame's
   truth comes from — the truth sidecar already carries a `<_solar>` element with `solar_time`, `date`,
   `time_zone`, `sun_elevation_deg`, `sun_azimuth_deg`, `advancing` and `rate`
   (`CarlaControl/src/carlacontrol/CotUdpEmitter.py:154-165`).
10. `RunManifestWriter` writes incrementally throughout — instances, intervals, rendered spans, observed
    spans per sensor and unioned, admissions and refusals, SUMO collision warnings, **and the run's solar
    record**: the epoch used, the derived civil date and time at the window's begin, the policy and rate,
    the computed solar-frame offset from UC-5 step 5, any override, and the solar readback at window begin
    and at window end.
11. At the window's end the session stops: recorders flush, rendered vehicles are released, **advancement
    is disabled**, the manifest is closed, the lease is released, and the world is restored to
    asynchronous mode so a headless server is never left waiting for a tick
    (`run_SCTMV.py:329-335` already does this on shutdown). Advancement is disabled on stop because it is
    world state that outlives the session and would otherwise keep moving the sun under the next one.

**Alternate flows.**
- *Frozen sun* — the default choice for anything that will be compared against another run (UC-6 §6a), and
  the only choice that makes illumination an exact constant.
- *Advancing sun* — for a window long enough that the light changing is part of the point. Rate 1.0 is the
  case the design must guarantee end to end: **one sun-clock second per one simulated second**, so a
  window of *N* simulated seconds moves the sun by exactly *N* seconds. **Inference**, from
  `CesiumTimeOfDayController.cpp:34` advancing by `DeltaSeconds × Rate` on each world tick and from the
  controller's own documentation that it "advances in wall-clock time under asynchronous mode and in
  simulation time under synchronous ticking" (`CesiumTimeOfDayController.h`, header comment); pinning the
  exact second down is [11](11_Time_And_Illumination.md)'s job and is open question 9.
- *Deliberate illumination override* — the operator names a civil time other than the derived one, to
  capture the same behaviour at dusk. Permitted, and the manifest records it **as an override**, marking
  the corpus as one whose illumination deliberately does not match its scenario's clock. This is
  [18 D4](../../Findings/18_Scenario_Fabrication_For_EPoL_Training.md)'s appearance axis used on purpose
  rather than by accident, and the distinction between the two is exactly the record.
- *Live feed alongside* — CoT goes to a TAK consumer as it is written. It is diagnostic; the sidecar is
  authoritative ([20 §7.4](../../Findings/20_Behavioral_Annotation_And_Areas_Of_Interest.md)).
- *Several cameras* — extra cameras run in the default single process, or in tick-follower processes that
  never cue. Either way they read supervision, the render set, session identity **and solar state** from
  the server, so their sidecars agree at each tick ([01 §3.4](01_Architecture.md)). Solar state is
  world-scoped and tick-paired, so this costs nothing extra.
- *Engine recorder alongside* — the server-side log is started for the same window so UC-9 is possible.

**Failure flows.**
- ***The scenario declares no epoch, so no civil time can be derived.*** **Refuse at session start**,
  naming the scenario and the missing declaration. This is the failure flow that matters most, and it is a
  refusal rather than a fallback for a reason that is worth stating in full: every alternative is worse.
  Defaulting to noon reproduces the original defect. Defaulting to the window's `t` modulo 24 h invents an
  epoch nobody declared and would be right only for scenarios whose `t = 0` happens to be midnight.
  Inferring the epoch from identifier text would read `guard_d0_h7_t3` as a contract when it is a habit of
  one generator (UC-5 step 12). Warning and continuing produces exactly the corpus the requirement
  exists to prevent: the sidecar's `<_solar>` element (`CotUdpEmitter.py:154-165`) would faithfully record
  noon while the scenario asserts 23:00, the record would be internally contradictory, and **nothing
  downstream would flag it** — the imagery is well-formed, the truth is well-formed, and only the
  relationship between them is wrong. UC-5 step 3 is where this should normally be caught; this flow is
  the backstop for a package that reached a server without passing it.
- *The solar authority refuses* — `set_solar_date` or `set_solar_time` returns false, which means the
  world has no `CesiumSunSky` (`CesiumHeightSampler.cpp:726`, `:743`). Fail the session. A capture that
  could not position the sun is a capture in unknown light, and is worth less than no capture because it
  looks valid. UC-2 step 5's warning is the earlier detection of the same condition.
- *The readback disagrees with the request* — `get_solar_state` after step 4 does not match the derived
  civil time within tolerance. Fail the session naming both values. The likely causes are a civil-offset
  conversion error (UC-5 step 5) and a date that did not take, and both are silent otherwise.
- *An advancement rate the window cannot support* — a rate that moves the sun by more than the window's
  own span implies the operator meant something other than what they typed; a rate large enough that one
  tick moves the sun visibly implies a discontinuous sky in consecutive frames. Refuse with the numbers.
  The bound is [11](11_Time_And_Illumination.md)'s to set.
- *Population authority is held* — session start fails naming the holder. Ambient traffic cannot be
  running underneath a SUMO capture, and this is the mechanism rather than a warning.
- *A storyboard is also requested* — refused unless storyboard entities are mirrored into SUMO; an
  unmirrored entity is invisible to every SUMO vehicle ([01 §5.2](01_Architecture.md)).
- *The world does not deliver a cued frame* — session fault, stop, and the manifest records where. A
  capture that silently drops frames has a tick spacing its manifest misreports.
- *The actor cap binds* — admissions are refused by priority, ambient first, and every refusal is recorded.
  A demand-limited capture must be distinguishable afterwards from a quiet one.
- *A capture window falls outside the scenario's end time* — refuse at session start rather than
  discovering an empty world at hour 170.

**Postconditions.** A corpus exists whose imagery, truth sidecars and manifest share one session identity
and one tick base, bound to one world digest and one scenario, **and whose recorded illumination is the
illumination the scenario's own clock implies, or is explicitly marked as an override**. The manifest can
answer, for any frame, what civil time it depicts and where the sun was — without inference.

**Artifacts.** Per camera: PNG captures plus CoT sidecars carrying `<_solar>`. Per session: the run
manifest including the solar record, the session log, and optionally the engine recorder log.

---

### UC-8 — Run a live exercise against an attached exploitation chain

| | |
|---|---|
| **Primary actor** | Live exercise operator |
| **Supporting** | Attached exploitation chain (external), CARLA server, solar authority (`CesiumSunSky`), `sumo`, TAK / CoT consumer (optional) |

**This is a primary use case.** [`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3c: narrowing the scope in §3b
removed *scoring*, not *running the chain*, and the live exercise is wanted **far more** than the
handover (UC-10). It is written here at the same depth as UC-7 because it is wanted at the same depth,
and it is entered from UC-12 by the same composition and the same validation.

**The chain being exercised, and where our side of it stops.**

```
synthetic imagery generation  ->  something consumes it  ->  something assesses what that produced
                                                         ->  reports come out, live
```

Everything up to and including *synthetic imagery generation* is ours. **Everything after it is external
and unknown to us** — its interfaces, its formats, its transports, its report schemas, its latencies and
its failure modes. So this case specifies **what we emit, and the guarantees on it**, and specifies
nothing about what consumes it. Every numbered step below names an action **we** perform. The chain
appears exactly once, as an actor that consumes and may return.

**One illustrative adapter, and it is illustrative.** A concrete picture helps a reader hold the case: an
adapter subscribes to our emission, hands each frame to a detect-and-track implementation, forwards its
tracks to an EPoL model service, and puts that service's reports on a display beside the world. *That is
one possible adapter and it is not the interface.* Substitute a single monolithic service, a chain of ten
stages, a human analyst, or a consumer that returns nothing at all — **no step below changes.** That
substitution is the test this case is written to pass (§1.4, D2.26); it is also an alternate flow, so
that it is checkable rather than merely asserted.

**What the pipeline does here is feed and record. What it must never do is measure** — no association of
returned material to truth, no residual, no count of hits and misses, no verdict. Those are somebody
else's work and this pipeline never sees a track it is entitled to judge
([`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3b, D2.24).

**Preconditions.** Everything UC-7 requires — a running server with the named world loaded, a validated
`ScenarioPackage` carrying an epoch, the toolchain staged, cameras configured, a window, a seed, and an
explicit illumination policy — and, in addition:

- **a declared pacing band for the run** — a target factor and a floor factor, both session-fixed
  (§8a; [08 §11.3](08_Collection_And_EPoL.md) D8.40) — because a live exercise runs against a wall clock
  with a human watching rather than as fast as the machine allows. They are run-configuration fields like
  any other and have no fallback to host behaviour (D2.22);
- **an emission endpoint this session can open.** Consumers attach to us; we do not dial out and we
  require no handshake beyond a connection ([08 §11.5](08_Collection_And_EPoL.md)). So the precondition is
  that the endpoint can be **opened**, not that anyone is listening on it — **zero consumers is a normal
  state**, late join is legal, and detach and re-attach are unremarkable;
- **a recorded decision on the truth feed** — whether it runs at all, and to which endpoint. In a live
  exercise it is off by default, because truth on a feed an exercised operator sees is a leak
  ([08 §11.4](08_Collection_And_EPoL.md), [08](08_Collection_And_EPoL.md) D8.23);
- **a transcript sink, if anything the chain returns is to be kept.** Optional: a chain that returns
  nothing, or a run that keeps nothing, is an ordinary exercise.

**Main flow.** Every step is on our side of the boundary.

1. **Start the session exactly as UC-7 does.** Population authority is acquired and a denial fails the
   session naming the holder (D2.6); the world goes synchronous at the fixed delta and the clock ratio is
   re-checked; the solar clock is bound to the window with a verified readback (UC-7 step 4, all four of
   its properties, D2.15 and D2.16); one session identity and a stable `sensor_id` per camera are
   assigned (D2.7); `sumo` is launched and the `AnnotationSet` is published as `WorldSupervisionState`.
   **A live exercise gets no relief from any of it.** An exercise in unknown light is a demonstration of
   nothing in particular.
2. **Pace the world against the wall clock, inside the run's declared band.** This is the one respect in which
   the session differs from UC-7, which runs as fast as the machine allows. The pattern to copy already
   exists and is not to be reinvented: `SumoCotBridge.run`'s `real_time_factor`, where "1.0 makes a second
   of simulation take a second, 2.0 runs at twice that, and 0 — the default — steps as fast as the machine
   allows" (`CarlaControl/src/carlacontrol/SumoCotBridge.py:184-194`), with the sleep target computed
   **absolutely** as `started_at + (now - sim_start)/factor - monotonic()` so that "a step that overruns is
   absorbed by the next one instead of accumulating drift over a long run" (`SumoCotBridge.py:243-248`).
   That code paces SUMO alone with no CARLA in the loop; the property needed here — a real-time factor on
   the **world** tick, implemented against an absolute target in that manner, with the **achieved** factor
   reported — is [03](03_CoSimulation_Runtime.md)'s to provide and [08 §11.1](08_Collection_And_EPoL.md)
   states it as a dependency. This case needs only that the declared band and the achieved factor are both
   recorded, and that §8a's two properties hold of whatever moves the clock inside it.
3. **Run the window as UC-7 step 9 does.** Per rendered instant, poses are applied, the world is cued,
   cameras deliver, recorders write; per SUMO step, the render set is reconciled and interval state
   changes are published; solar state travels with every capture from the same tick-paired cache. Under an
   advancing policy at rate 1.0 and a pacing factor of 1.0, the sun moves in step with both simulated and
   wall-clock time, which is the one case where the distinction the controller draws between them
   (`CesiumTimeOfDayController.h`, header comment) has no observable consequence. Under a pacing factor
   *below* 1.0 the sun still tracks **simulated** time, because it advances on the world tick
   (`CesiumTimeOfDayController.cpp:34-36`) — so a deliberately slowed exercise does not desynchronise the
   sun from the scenario (carried forward from [08 §11.1](08_Collection_And_EPoL.md)).
4. **Emit one self-describing record per captured frame, to the configured endpoint.** The record is the
   **collection frame** of [08 §7.2](08_Collection_And_EPoL.md) — image, session and sensor identity, tick
   and simulated time, platform pose, pointing, motion, intrinsics, radiometry and achieved solar state.
   Three properties of it are the contract and nothing else is:
   - **every frame is self-describing**, so an image is interpretable without reference to any other file;
   - **the tick is the only join key**; filenames are local wall-clock stems and must never be used to pair
     anything (`FrameRecorder.cs:223-232`, `:179`);
   - **channels are frame-coherent**, because every channel of a session reads one world-observer snapshot
     per tick, so two channels stamping the same tick cannot disagree about the sun or the supervision
     state ([08](08_Collection_And_EPoL.md) D8.3a).

   Nothing in the record is derived from the scene ([08](08_Collection_And_EPoL.md) D8.12, D8.17). **The
   same record serves the corpus and the live feed** — [08](08_Collection_And_EPoL.md) D8.1 defines the
   handover as *a record, never a directory* — so an exercise emits exactly what a corpus writes, differing
   only in transport, and a consumer writes one reader for both. **What a consumer's input format is, we do
   not say**; the adapter that converts is thin and is theirs.

   **How a consumer attaches is stated entirely as properties of our own end**, which is what keeps the
   case generic ([08 §11.5](08_Collection_And_EPoL.md) owns them): one endpoint per capture session
   carrying every channel, each record tagged with its `sensor_id`; push, one direction, no
   acknowledgement, no request, no handshake beyond connecting; **late join is legal** and delivers from
   the moment of attachment; **detach and re-attach are legal and unremarkable**; **zero consumers is a
   normal state**; and there is no content negotiation, because we emit one record shape and that is our
   whole position. Every additional requirement placed on a consumer is one a substitute consumer might
   not meet.
5. **Never block the world on the chain.** If our own outbound queue deepens, the clock owner may slow
   within the declared band; **at the floor factor the policy switches to drop-oldest** and the oldest
   pending frame is dropped and counted per sensor. The world does not stall at any factor, and nothing a
   consumer *sends* is ever an input to the decision — only the depth of our own socket, which needs no
   cooperation from anybody (§8a; [08 §11.3](08_Collection_And_EPoL.md) D8.40 clauses 2 and 3). This is the policy `FrameRecorder` already applies to its own encode queue
   (`FrameRecorder.cs:118`, counter at `:46`, `:184-185`), and it is
   [08](08_Collection_And_EPoL.md) D8.22. The consequence is recorded rather than absorbed: the tick is
   marked in the coverage record as **covered but not delivered** — the camera saw it, the consumer never
   got it — which is a two-valued fact about the link and a judgement about nobody. §8a is the ruling and
   what the operator sees.
6. **Keep truth on its own channel, off by default.** Truth travels so an observer can watch both streams
   at once, on a separate endpoint from the emission, and in a live exercise it is off unless someone
   turned it on and the choice was recorded. The separation is structural rather than configured: two
   artifact roots with one writer each and no path between them ([06 §10.3](06_Truth_And_Annotation.md),
   [08](08_Collection_And_EPoL.md) D8.17 and D8.23, D2.9). If the operator is part of what is being
   exercised, truth on their feed is a leak; if they are not, it is still two events for one vehicle, one
   exact and one estimated.
7. **Record what came back, as received, or not at all.** If anything is pushed back to us, the session
   may keep it as a **transcript**. A transcript entry is an **opaque blob** and carries exactly four
   things beside the bytes: the **tick current when it arrived** and the wall time, a **source id** —
   which endpoint it came from, **assigned by us and never claimed by the sender** — and a **content
   type**, as declared by the sender, **recorded and not validated**. We do not parse it, normalise it,
   schematise it, index it by anything inside it, merge it, or act on it
   ([`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3c; [08 §11.6](08_Collection_And_EPoL.md) fixes the shape).

   **We do not classify what arrives, either.** Naming what comes back as "tracks" or "assessments" is
   one step further than we are entitled to go, **because we cannot tell them apart without parsing
   them**. A transcript full of blobs
   we cannot interpret is a complete and successful transcript, not a degraded one. Counting arrivals is
   not reading them — how many blobs turned up per source and when the last one did needs no blob opened,
   and the prohibition is on counting what is *inside* one.

   **The listener is off by default**, and the endpoint exists only when a run enables it. Receiving
   anything at all would otherwise require the far side to push to us, which is a protocol
   [08 §11.5](08_Collection_And_EPoL.md) declines to demand of anyone. **An exercise with no transcript is
   the normal case**, and the run is otherwise identical — which is itself a useful property, because it
   means the transcript is a convenience and never a dependency.
8. **Compute no agreement between the two streams.** Nothing in the session associates, differences,
   scores or summarises. The comparing is done by the people watching, which is what a demonstration is
   for.
9. **Close as UC-7 step 11 does.** Recorders flush, rendered vehicles are released, **advancement is
   disabled** because it is world state that outlives the session, the manifest is closed, the lease is
   released, and the world is restored to asynchronous mode so a headless server is never left waiting for
   a tick (`run_SCTMV.py:329-335` already does this on shutdown). The transcript sink is closed and
   digested with the rest of the session's artifacts.

#### 8a. When the chain cannot keep up — the actor-facing ruling

[`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3c names this "the one genuinely new engineering question" and asks
for a decision and for what the operator sees. **[08 §11.3](08_Collection_And_EPoL.md) owns the
mechanism, as D8.40.** This sub-section states the **actor-facing** half — what the operator declares,
what they see, and what must refuse — and adopts D8.40 rather than restating it.

**The ruling, as adopted.** A run declares a **target factor** and a **floor factor**, both session-fixed,
both defaulting to 1.0 — so *no slowing unless somebody asked for it*. Between them the clock owner may
slow, and **the only input to that choice is the depth of our own outbound queue**. At the floor the
policy switches to drop-oldest at the emission socket with a per-sensor counter, and the tick is recorded
in coverage as **covered but not delivered**. Running ahead and buffering is refused, and back-pressuring
the world is refused absolutely. [08 §11.3](08_Collection_And_EPoL.md) has the five clauses and the
clause-by-clause argument that slowing costs no truth.

**The two properties this case needs from that ruling, stated as properties:**

| Property | Why this case needs it |
|---|---|
| **The band is declared before the run and both its bounds are recorded** | An operator who wanted a real-time answer and got a slowed one has been shown a different exercise. Declaring target *and* floor is what makes "did it hold real time?" a question with an answer instead of a shrug |
| **Nothing a consumer *sends* is an input to our clock** | This is the one that makes the case generic. The moment the world's rate is a function of a consumer's acknowledgement, heartbeat or advertised rate, an external project's latency is an input to our simulation, the run stops being reproducible, and this plan has a protocol with a party it knows nothing about. Observing our own socket's queue depth requires no cooperation from anybody, which is exactly what makes an arbitrary consumer substitutable ([08 §11.3](08_Collection_And_EPoL.md) clause 2, [08 §11.5](08_Collection_And_EPoL.md)) |

**What the operator sees**, all of which already exist or fall out:

| Indicator | Where it comes from | Why it is the right one |
|---|---|---|
| **Achieved factor against target and floor**, plus cumulative simulated-versus-wall slip and time spent below target | the achieved factor the clock owner reports; `RunReport.achieved_real_time_factor` is the existing precedent (`SumoCotBridge.py:165-168`) | It distinguishes "the consumer is behind" from "we are behind", which are different problems with different remedies — and it is the figure that says whether a real-time claim survived |
| **Drop count per channel**, live and cumulative | `FrameRecorder.Dropped` is already incremented and, as [12 §7.3](12_Operator_Control_Surface.md) measures, **read nowhere** | It is the direct measure of what the chain missed, per channel, because a multi-channel exercise can starve one link and not another |
| **What is attached, and since when** | observation of our own socket — a connection exists or it does not — which needs no protocol ([08 §11.5](08_Collection_And_EPoL.md)) | Zero consumers is a *normal* state, so an operator needs to be told it is the state rather than left to infer it from a quiet display |
| **The sun falling behind the exercise's own wall clock**, under an advancing policy | free: `get_solar_state` reads the tick-paired world-observer cache with no RPC (`carlanet/__init__.py:1511-1533`) | It is the one indicator an observer reads **without instrumentation**, in the imagery itself, which is why an advancing sun makes a slowed exercise visible for nothing |

A coordination note rather than a decision: [12](12_Operator_Control_Surface.md) D12.15 makes a non-zero
`Dropped` on any channel one of exactly three conditions that **interrupt** the operator. In a captured
collection that is right. In a live exercise at the floor factor, drops are the *expected* degradation and
an interrupt per drop is noise. That tension is open question 13, and it is
[12](12_Operator_Control_Surface.md)'s to settle.

**Alternate flows.**

- *A different chain, swapped in* — **the defining alternate flow of this case, stated as one so that it is
  testable.** Point the emission endpoint somewhere else and the case runs unchanged: a different detector,
  a different model service, both replaced at once, a single monolith, ten stages, or a human analyst
  reading frames. The only thing that changes is a configured endpoint. If any step above would have to
  change, that step is wrong (§1.4, D2.26).
- *No chain attached* — truth-only live telemetry. This is the existing behaviour and must keep working; it
  is chosen deliberately, not arrived at by a failed connection (see the failure flows).
- *A chain that returns nothing* — the ordinary case, and it needs no transcript sink. The exercise is the
  demonstration that our imagery drives somebody's chain; what their chain says is theirs to show.
- *Integration test rather than demonstration* — the same flow run for shape rather than for an audience.
  Does the endpoint accept our collection frame at the offered rate; is the tick preserved end to end; does
  the round trip stay inside the pacing budget; does the drop counter stay at zero. **Every one of those is
  an assertion about an interface, not about a model**, and they are a large part of why this case is worth
  having.
- *Capture a corpus during the exercise* — permitted. It is UC-7's recorder running alongside, and what it
  produces is a corpus like any other, subject to UC-10's gates. The only thing that differs is pacing, and
  the manifest records the declared and achieved rates so a consumer knows the captures are evenly spaced
  in *simulated* time and not in wall clock.
- *Several channels* — extra cameras run in the default single process, or in tick-follower processes that
  never cue. Either way they read supervision, the render set, session identity and solar state from the
  server, so their emitted records agree at each tick ([01 §3.4](01_Architecture.md)). Whether a consumer
  fuses per-sensor streams before displaying them is their design problem; what is ours is that coverage is
  published both per sensor and unioned, so their choice does not change what our record means
  ([08](08_Collection_And_EPoL.md) D8.24).
- *The transcript handed on* — step 7's transcript is a legitimate thing to give the external model team,
  because it is *their* output beside *our* truth on a common tick base. Handing it over is UC-10, where it
  ships labelled as received data with its own provenance; what anyone concludes from it is theirs.
- *Illumination as legitimate context for the chain* — a live exercise is the natural place to demonstrate
  that a fielded system knows the time and its own location and is entitled to use them, which the standing
  constraint permits explicitly (brief §3a). The boundary is unchanged: the chain may be told the civil
  time and the site; it is never told a *label*, and the solar state it is given is the same derived
  quantity every capture computes, not a truth-sourced field.
- *A live TAK feed for the observers* — CoT to a TAK consumer as it is written, on the truth endpoint and
  therefore subject to step 6's default. It is diagnostic; the sidecar is authoritative
  ([20 §7.4](../../Findings/20_Behavioral_Annotation_And_Areas_Of_Interest.md)).

**Failure flows.**

- *The emission endpoint cannot be opened* — a port already in use, a permission refused, a host binding
  that does not exist. **Refuse at session start, naming the endpoint.** This is a failure of *our* end and
  is the only endpoint condition that refuses.
- *Nothing is attached* — **not a failure.** Zero consumers is a normal state, late join is legal, and the
  world does not notice ([08 §11.5](08_Collection_And_EPoL.md)). The session never refuses merely because
  no consumer is reachable, because under the attachment model we do not dial out to them. What the
  operator needs is not a refusal but a **display**: what is attached and since when, read from our own
  socket, so that a demonstration with nothing on the far end is visibly that rather than quietly that
  (§8a).
- *The chain goes away mid-exercise* — **do not stall and do not fail.** Keep running, count the
  undelivered frames, mark them in coverage as covered-but-not-delivered, and show that the attachment
  dropped. A live world stopping because a consumer crashed is a worse demonstration than a live world
  visibly running with nothing on the far end, and the second is at least honest about which half broke.
- *The chain cannot keep up* — the exercise degrades rather than stalls, per §8a: frames are dropped, the
  drop is counted and displayed, and the world keeps its declared pacing. **An advancing sun makes this
  visible for free**: under synchronous ticking the sun tracks simulated time, so a world falling behind
  wall clock shows a sun falling behind the exercise's own clock.
- *The world is stalled to wait for a consumer* — refused. It corrupts the run's own premise, it changes the
  relationship between simulated and wall-clock time that the declared band exists to control, and it moves
  the sun ([08](08_Collection_And_EPoL.md) D8.22).
- *Pacing driven by anything the consumer sends* — refused, without exception. An acknowledgement, a
  heartbeat, a back-off request or an
  advertised rate must never reach the clock owner. The moment the world's rate is a function of something
  the far side says, an external project's latency is an input to our simulation, the run stops being
  reproducible, and this plan has acquired a protocol with a party it knows nothing about. Slowing within
  the **declared band** on **our own queue depth** is permitted and is D8.40's first-choice response
  (§8a).
- *A pacing band that is not declared, or a floor that is not recorded* — refused. Undeclared slowing is
  the condition D2.12 objects to, and it is indistinguishable afterwards from a fast run.
- *Truth reaching the chain* — a configuration error that must be impossible by construction, not caught by
  review. What the chain is given is the collection frame of step 4, which carries no truth-sourced field,
  and the split is enforced at the writer ([08](08_Collection_And_EPoL.md) D8.17, D2.9).
- *A figure of merit for the model is asked for, live* — **refuse; it is not a capability this system
  has.** Stating this as a failure flow is deliberate rather than pedantic: this is the one place in the
  whole plan where scoring would be added by accident, because both streams are already in one process,
  on one tick base, with a common frame identity, and the join is a few lines away. An observer who wants
  a number takes the transcript and computes it outside, with the transfer rule UC-10 step 6 ships.
- *The transcript treated as truth, or merged into one* — refused. Received model output is **received
  data**: it lives in its own artifact with its own provenance and is never written into a truth sidecar,
  a supervision record or a run manifest's supervision block. A stream that is both an input and a record
  of what a model said about it is one refactor away from being a label.
- *The transcript parsed for meaning the session then acts on* — refused, and this is the sharper form of
  the flow above it. Reading a returned report to decide what to render, what to annotate, when to stop, or
  where to point a camera closes a loop from a model's output back into the data that model is looking at.
  That is the fastest available route to a corpus that is a model's own opinion, and it would be invisible
  afterwards, because every artifact would still be well-formed.
- *A normaliser, a fusion stage, or a schema designed for the chain's output* — refused
  ([`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3c). A container with a timestamp, a source id and a content type is
  the whole design. Anything more is a claim about formats we do not know, and it would go stale the first
  time a different chain was attached.
- *Anything else UC-7 refuses* — population authority held, no epoch declared, a solar authority that
  refuses, a readback that disagrees, an advancement rate the window cannot support, a window outside the
  scenario's end, an undelivered cued frame. **A live exercise gets no relief from any of them.**

**Postconditions.** A demonstration happened, and **nothing about the chain, or anything inside it, was
measured.** A session log records what was shown, under which illumination policy, at which declared and
achieved pacing factor, against the band that was declared. The emitted record stream is exactly what a corpus would have written, so what was
shown is reproducible. If a transcript sink was configured, a transcript records what was sent and what
came back on a common tick base, as received. If the run also captured, a corpus exists and is an ordinary
corpus. Coverage distinguishes, per sensor and per tick, what the camera saw from what the link delivered.

**Artifacts.** Session log; the emitted collection-frame stream (the same record a corpus writes); the
exercise transcript, if a sink was configured, as opaque received data with its own provenance; the
per-sensor drop counts and the coverage record that carries them; optionally a recorded CoT truth stream
for later review; optionally a corpus and its manifest.

---

### UC-9 — Replay a recorded run

| | |
|---|---|
| **Primary actor** | Capture operator |
| **Supporting** | CARLA server, solar authority |

**Preconditions.** An engine recorder log, its manifest **including the original run's solar record**, and
a loaded world whose digest matches.

**Main flow.**
1. Operator names the log; the tooling fetches the live OpenDRIVE, hashes it, and compares against the
   manifest.
2. `RecordedReplay` acquires population authority; anything else holding it must stop first.
3. **The original illumination is restored before anything is captured.** The epoch, the derived civil
   date and time, the policy and the rate are read from the original manifest, applied to the solar
   authority, and read back and verified — the same four properties as UC-7 step 4, for the same reasons.
   **Reproducing is the default; differing is an override.**
4. The replayer respawns and drives the actors from the log. Record and replay are verified working end to
   end in this fork ([18 §5.4](../../Findings/18_Scenario_Fabrication_For_EPoL_Training.md)).
5. New captures are taken under the restored illumination, or — when the operator has asked for it
   explicitly — **under a deliberately different appearance**: time of day, weather, camera track, while
   the behaviour is exactly the recorded behaviour
   ([18 D4](../../Findings/18_Scenario_Fabrication_For_EPoL_Training.md)). The replayed corpus's manifest
   records which of the two happened, cites the original run, and states the difference.
6. Supervision is recovered from the original run's manifest, joined by `entity_id` and normalised tick
   ([20 §7.7](../../Findings/20_Behavioral_Annotation_And_Areas_Of_Interest.md)); the static identity
   attributes survive because the recorder log preserves spawn attributes verbatim
   ([20 §4.5](../../Findings/20_Behavioral_Annotation_And_Areas_Of_Interest.md)).

**Why reproduce rather than inherit.** Replayed imagery whose light does not match the truth that travels
with it is the same internally contradictory corpus as UC-7's no-epoch failure, one generation removed —
and here it would be *harder* to spot, because the supervision is demonstrably correct (it came from a
validated original) and only the pixels disagree with it. The concrete mechanism is §1.2: a replay run on
a server whose world still holds the previous session's sun inherits that sun silently, because the noon
default is applied only when no `CesiumSunSky` exists (`CesiumHeightSampler.cpp:396-402`). Inheriting is
not a neutral default; it is a random one.

**Alternate flows.**
- *Replay for review rather than capture* — no recording, no manifest needed, and no illumination
  obligation. A human watching a replay to understand what happened is not producing a corpus.
- *Replay as the illumination axis* — the cheapest legitimate way to produce a second corpus of identical
  behaviour under different light, and the natural companion to UC-6's illumination-only sweep. The pair
  is what lets UC-11's separability test have something to compare against.

**Failure flows.**
- *No manifest* — refuse to treat the log as replayable
  ([18 §5.5](../../Findings/18_Scenario_Fabrication_For_EPoL_Training.md)).
- *The manifest carries no solar record* — refuse to replay **for capture**; permit replay for review. The
  original illumination is unrecoverable, so the new corpus could not state its relationship to the old
  one, and a corpus that cannot state that is not joinable to it. This is the one case where an older run,
  captured before the solar record existed, is not re-capturable — which is a correct outcome, not a
  regression: it was never capturable correctly, only silently.
- *Digest mismatch* — the two-tier gate: recipe mismatch refuses, digest-only mismatch warns and requires
  an override.
- *Relying on the engine's own map guard* — it is inert for generated worlds and must never be the check.
  The comparison is between a value and itself, and every generated world loads under one level name
  ([18 §5.3](../../Findings/18_Scenario_Fabrication_For_EPoL_Training.md)).
- *Kinematics in a replayed capture* — the SUMO bridge is not running, so the compensation of
  [01 §4.3](01_Architecture.md) is unavailable and replayed bodies are moved by the replayer. Whether
  replayed truth can carry speed at all is an open question, recorded in §8.

**Postconditions.** A second corpus of the same behaviour, under the same illumination as the first or
under a stated different one, joinable to the first by `entity_id` and normalised tick.

**Artifacts.** New captures and sidecars; a manifest that cites the original run and states whether its
illumination reproduced or departed from it.

---

### UC-10 — Hand a corpus to an external model team

| | |
|---|---|
| **Primary actor** | Capture operator, as the corpus's producer |
| **Supporting** | Scenario author (the annotations' author), external model team (recipient, outside the boundary) |

**What this case is, and what it is not.** Evaluating a model against a corpus — transferring supervision
onto detector tracks, joining assessments to supervision, computing a denominator, reporting a base rate,
stratifying and scoring — is out of scope ([`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3b). What **is** ours is
the step immediately before it: putting a corpus into a state where somebody else can do that work
**without having to ask us anything**.

The test for whether this case is doing its job is a blunt one. *Could the recipient, holding only what
UC-10 handed them, compute a figure and know what it means — the denominator, the strata, what is
missing and why, and the rule for attaching our labels to their tracks?* If yes, the corpus is complete
and nothing here has scored anything.

**Preconditions.** A closed corpus from UC-7 or UC-9, with its manifest **closed** (D2.10); UC-11's
audit run against it and its report in hand; the vocabulary version, manifest spec version and validator
version recorded; a named recipient.

**Main flow.**
1. **Close and freeze the identity.** The manifest is closed and digested; the corpus's identity is the
   tuple the manifest already carries — world digest, network, routes and configuration digests,
   `scenario_id`, `plan_id`, `session_id`, seed
   ([06 §8.4](06_Truth_And_Annotation.md)'s session block). A corpus is handed over as a named,
   immutable thing or not at all; an unnamed pile of PNGs and sidecars is the thing nobody can join to
   anything later.
2. **Audit it first.** UC-11 runs, and its report is part of the package rather than a separate errand.
   Handing over an unaudited corpus hands over unreviewed accidental positives and an unquantified
   illumination confound, and the recipient can find **neither**: they hold no scenario, no unlabelled
   population's provenance, and no record of what the author did and did not assert.
3. **Split the export.** Two artifacts, not two views of one: a **training export** carrying only what a
   fielded system could also have, and a **full export** carrying everything including truth. The split,
   its table and the conditional gate on solar fields are
   [06 §10.2–10.3](06_Truth_And_Annotation.md)'s, and this case does not restate them. Two properties it
   *does* require of that split: the recipient is told which artifact is which and why, and the
   supervision that ships is keyed to **truth** entities and intervals, because detector tracks are the
   one thing this pipeline never sees.
4. **State what the corpus contains**, in the units the manifest already carries, so that the recipient
   computes rather than estimates:
   - observability spans per sensor and unioned, and the five outcomes an interval can have
     ([06 §5.1](06_Truth_And_Annotation.md));
   - prevalence in **three** units — per vehicle, per vehicle-second, per interval — which differ by a
     factor of 372 on the sizing scenario, so an unlabelled number is a defect rather than a rounding
     matter (measured, carried forward from [06 §2.6 and §5.3](06_Truth_And_Annotation.md));
   - every one of those three **per illumination band**, with the band cut points recorded beside them,
     and with empty-numerator rows kept rather than suppressed — "we captured nothing anomalous at night"
     and "we did not capture at night" are different statements and only one of them is a gap
     ([06 §5.3](06_Truth_And_Annotation.md));
   - the run's solar record: the epoch, the derived civil date and time, the policy and rate, the
     solar-frame offset, any override, and the achieved sun (UC-7 step 10);
   - the render-set accounting: admissions, releases, refusals and the spans in which the actor cap bound
     ([06 §10.4](06_Truth_And_Annotation.md)).
5. **State what it does not contain**, as explicit rows rather than as an absence. This is the half a
   recipient cannot reconstruct and the half that decides whether their denominator is honest:
   - **intervals whose participants were never instantiated.** They are `not_rendered`, they are in the
     manifest, and they are not something anyone may count against a model. Only we can know which they
     were (D2.8);
   - **spans where the actor cap bound.** While the cap binds, "was rendered" correlates with "is
     annotated", so those spans are excluded from the training export and kept in the full one
     ([06 §10.4](06_Truth_And_Annotation.md));
   - **windows retained as truth-only.** The sizing scenario's 23:00 window is exactly this case: the sun
     there is 38.1° to 79.5° below the horizon at 23:00 **on every date of the year**, so the window is
     kept for truth and the imagery regime it was meant to sample is re-placed onto 17:00–18:00
     (measured by [10 §4.2.4](10_Scale_And_Performance.md), carried forward). A recipient who does not
     know this reads a hole in the corpus as a hole in reality;
   - **pedestrians**, which the world-generation pipeline cannot render believably and which are out of
     scope for the whole effort (brief §3, decision 5);
   - **vehicle dynamics**, and what was done about them: a pose-applied body's velocity is not the
     engine's ([01 §4.3](01_Architecture.md), [06 §4.2](06_Truth_And_Annotation.md)), and the truth record
     says which producer each kinematic field came from;
   - **any model output at all.** No corpus we hand over contains a detection, a track, an assessment or
     a score, except where UC-8's transcript is shipped explicitly and labelled as received data.
6. **Ship the rule, not the result.** The supervision-transfer rule travels with the corpus as a written
   contract the recipient applies to their own tracks: associate by position and time and **never by
   uid**; one truth entity maps to many tracks, so transfer is per (track, interval) and not per entity;
   transfer once **per sensor**; **clip** a track that spans an interval boundary rather than labelling it
   wholesale; record association quality per assignment ([06 §10.1](06_Truth_And_Annotation.md), whose
   rule this is; [08 §8.2–8.3](08_Collection_And_EPoL.md) for the assignment and the quality block's
   format). **We publish the rule and the format; we do not run them**, because running them requires
   detector tracks this pipeline never sees (brief §3b item 3). What the corpus must therefore be is
   *associable*: per tick, positioned, timed, boxed, and joinable on a tick base the recipient also holds.
7. **Name the version of everything.** Vocabulary version, manifest spec version, validator version,
   catalogue version, world digest, scenario digests, and the transfer-contract version. A recipient who
   cannot tell two corpora apart will pool them, and pooling is the failure the refusals below exist to
   prevent.
8. **Record the handoff.** Who received which corpus identity, which exports, which audit report, and
   which contract version. The record lives with the corpus, so a second recipient is given the same
   statement rather than a differently-remembered one.

**Alternate flows.**
- *A corpus assembled from several runs* — permitted only where every run shares world digest, scenario
  id and vocabulary version, and where the assembled metadata is **stratified rather than pooled**. A
  07:00 window and a 17:00 window are two populations; a number pooled over them describes neither
  (carried forward from [06 §5.3](06_Truth_And_Annotation.md)).
- *A truth-only corpus* — a window captured for truth with no viable imagery is a legitimate deliverable
  ([10 §4.2.4](10_Scale_And_Performance.md)'s 23:00 case), provided it is labelled as one and no
  imagery-derived metadata pretends to exist for it.
- *An unannotated corpus* — ordinary movement with an empty `AnnotationSet` (open question 3). It is the
  cleanest control a recipient can be given, and it must be shippable. What it needs is a statement that
  the set is empty **by intent** rather than by failure, which is a distinction only the author can make.
- *A recipient who wants different light, or the same behaviour again* — the answer is a **capture, not an
  edit**: UC-6's illumination-only sweep or UC-9's replay under a different appearance. This is what makes
  "it did not work at dusk" an actionable request without this pipeline ever scoring anything.
- *A recipient who wants the live exercise's transcript* — UC-8's artifact, shipped as received data with
  its own provenance, never merged into the truth.

**Failure flows.**
- *The manifest is absent or was never closed* — refuse the handoff. Supervision in interval form is the
  only thing a detector track can be clipped against, and the recipient is the one who will do the
  clipping (D2.10).
- *No audit report* — refuse. An unaudited corpus is one whose accidental positives and illumination
  confound are invisible to everyone who will ever hold it (UC-11, D2.11).
- *No solar record* — refuse to hand it over as an **imagery** corpus; permit it as a truth-only one,
  labelled. Without the record the recipient cannot stratify, so they will pool, and they will not know
  they did.
- *A vocabulary version the recipient's tooling does not understand* — refuse until it is resolved. A
  corpus assembled from scenarios that each spelled `loiter` differently is not a corpus
  ([20 §6.2](../../Findings/20_Behavioral_Annotation_And_Areas_Of_Interest.md)), and the boundary is the
  cheapest place left to catch it.
- *Corpora from different worlds or different scenarios bundled without saying so* — refuse; the world
  digest is part of the corpus identity.
- *Truth reaching a training input by the back door* — refuse. The export split is a **process** boundary
  and not a convention ([06 §10.3](06_Truth_And_Annotation.md)); the one conditional row is solar state,
  which is exported as a training input only when the corpus has annotated mass in more than one
  illumination band, or when the time of day *is* the annotated pattern
  ([06 §10.2](06_Truth_And_Annotation.md)).
- *A request for a score, a baseline, a model comparison or a pass/fail verdict* — **not a capability this
  system has, and the answer is not "later".** What the recipient gets instead is steps 4, 5 and 6: a
  denominator they can compute, strata they can condition on, an honest statement of what is missing, and
  the transfer rule they apply themselves. This failure flow exists to be cited, because this is the
  request that will arrive.

**Postconditions.** The recipient holds a corpus they can train on and validate against without asking us
a question; the statement of what it contains, what it omits and in what light travels with it; the
handoff is recorded on our side. **Nothing about any model has been measured, here or anywhere upstream
of here.**

**Artifacts.** The training export; the full export; the corpus statement (contents and omissions, in the
manifest's own units); the audit report from UC-11; the supervision-transfer rule and the
association-quality format, shipped as versioned contracts; the handoff record.

---

### UC-11 — Audit a corpus for accidental positives, and for illumination leakage

| | |
|---|---|
| **Primary actor** | Capture operator, who owns the corpus |
| **Supporting** | Scenario author, who owns what was and was not asserted; a stock detector, **as an instrument only**, in the alternate flow |

**This is a data-quality use case.** Auditing our own data for accidental positives and for illumination
leakage is an assertion about **the corpus**, never about a model
([`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3b items 1 and 2). Its actor is ours to choose, and giving it to
someone outside the boundary would put our own data-quality gate in the hands of whoever holds neither
the scenario, nor the unlabelled population's provenance, nor any record of what the author chose not to
assert — so it stays with the people who hold those things. It runs **before** a corpus leaves (UC-10
step 2), and its report ships with the corpus.

**Preconditions.** A corpus with its manifest, its solar record and derived area relations.

**Main flow.**
1. Every `unlabelled` vehicle's derived area relations and motion summary are compared against the
   profile of each annotated pattern class.
2. Candidates — an unlabelled vehicle that looks exactly like an annotated pattern — are surfaced for
   human review before they train as negatives
   ([20 §2.2](../../Findings/20_Behavioral_Annotation_And_Areas_Of_Interest.md)).
3. **The corpus is tested for illumination leakage.** For each annotated pattern class, the distribution
   of sun elevation over its intervals is computed and compared against the unlabelled population's
   distribution over the same corpus. If a decision rule on sun elevation alone separates the two, the
   corpus teaches the hour rather than the behaviour, and the finding is recorded with the corpus whether
   or not anything is done about it.
4. The hour-defined instances UC-4 step 5 flagged are checked explicitly, since they are leakage by
   construction and the audit's job for them is to quantify it, not to discover it.
5. The audit's own criteria are recorded with the corpus, so a later reader knows what was looked for.

#### 11a. Why leakage is the default state of a pattern of life, measured

A pattern of life is a rhythm of hours. Annotated behaviour in one therefore correlates with hour *by
construction* — guard shifts, night deliveries, pre-dawn movement — and the hour determines the
illumination exactly. Nobody has to make a mistake for this to happen.

Measured 2026-09-18, by reading the nine `marked_ids` out of
`BahonarPatternOfLife.zip`'s `.labels.json` and resolving each one's `depart` in the `.rou.xml` through
the epoch established in UC-3:

| Marked vehicle | `depart` (s) | Day | Civil hour | Light |
|---|---|---|---|---|
| `escort_0` … `escort_4` | 295,200 – 295,216 | 3 | 10:00 | day |
| `probe_d2` | 212,674 | 2 | 11:04 | day |
| `probe_d5` | 472,285 | 5 | 11:11 | day |
| `staybehind` | 115,200 | 1 | 08:00 | day |
| `shadow` | 527,400 | 6 | **02:30** | **night** |
| `guard_no_show` (an interval, not a vehicle) | 370,800 – 399,600 | 4 | 07:00 – 15:00 | dawn → day |

Three things follow, and none of them is a criticism of the scenario — it is a well-made scenario, and
that is the point.

- **The nocturnal hour is a designed property of one anomaly, not a coincidence.** The generator says so:
  *"Perimeter shadow (day 6, 02:30): a vehicle slowly follows the fence line when nothing else moves"*
  (`make_bahonar_scenario.py:280-281`), described in the module header as a *"temporal and spatial
  outlier"* (`:24`).
- **The ordinary population at that hour is nearly nothing.** [10 §3.1.3](10_Scale_And_Performance.md)
  measures the overnight floor at ~18–21 concurrent vehicles — *"almost entirely the 17 parked guards"* —
  against a 10:00 mean of 47.0 and a 07:00 mean of 92.1. So at 02:30 the anomalous vehicle is close to
  being the only thing moving, in the dark, and both facts are true of it alone.
- **The ordinary population is itself hour-shaped.** Ferry sailings are restricted to daylight by an
  explicit authoring decision — `# Ferry sailings (local hours) -- daylight only, none overnight.`
  (`make_bahonar_scenario.py:161-162`) — and guard shifts sit at 07:00, 15:00 and 23:00 (`:164`). A
  model trained on this corpus can separate the annotated class from the rest on "bright and busy" versus
  "dark and empty" alone, without learning any behaviour. That is a property of **the corpus**, and step
  3 measures it with no model in the room at all: the separability test needs the labels and the sun and
  nothing else.

**What the audit does about it, and what it must not do.** It does not remove the correlation: the
correlation is *real*, a fielded system sees it, and illumination is a legitimate input to one that knows
the time and its own location (brief §3a). It **measures** the correlation and records it with the corpus,
so that a recipient reads it **before** training on the data rather than discovering it afterwards.
Where the leakage is strong enough that the corpus would teach the hour instead of the behaviour, the
remedy is a capture, not an edit — UC-6's illumination-only sweep or UC-9's replay under a different
appearance gives the same behaviour in different light, and the pair is what makes the corpus fit for the
purpose it was built for. Whether a model trained on the unpaired corpus turns out to have learned the
hour is the recipient's finding to make; the audit's job is to make sure nobody is surprised by it.

**Alternate flows.**
- *Audit a run list rather than one run* — the same test across a sweep finds a class of accidental
  positive the seed makes common. It also finds illumination leakage that a single run cannot expose,
  because a single frozen run has no illumination variance to test against.
- *Audit across an illumination pair* — where UC-6 or UC-9 produced the same behaviour under two suns,
  the leakage test has a genuine control and becomes a measurement rather than an estimate.
- *Probe the corpus with a stock detector, as an instrument* — the corpus fitness question, *does an
  annotated interval survive contact with a detector at all?*, is an assertion about our data and not
  about the detector: a thermometer checking an oven. Its only admissible output is "this corpus does or
  does not yield trackable targets over the annotated intervals", which feeds camera altitude, field of
  view and channel count. It must emit **no** model metric (brief §3b item 1). The probe's design, its
  tiers and its illumination handling are [08 §12](08_Collection_And_EPoL.md)'s and
  [13](13_Work_Breakdown.md)'s; what this case owns is that its result is recorded as a property of the
  corpus and travels with it.

**Failure flows.**
- *The audit's output used as a label* — prohibited. A derived predicate never writes into supervision
  ([20 decision 3](../../Findings/20_Behavioral_Annotation_And_Areas_Of_Interest.md)). How a confirmed
  accidental positive is handled is
  [20 open question 5](../../Findings/20_Behavioral_Annotation_And_Areas_Of_Interest.md) and is not
  settled here.
- *The illumination stratum used as a label* — prohibited, with the same force and by the same rule.
  Illumination is derived context computed identically for every capture; it is a legitimate covariate for
  stratifying a corpus and never a supervision signal (brief §3a). This is stated here as well as in UC-4
  because UC-4 is where it would be written in by an author and UC-11 is where it would be written in by a
  tool, and the second is the likelier of the two.
- *A corpus with no solar record* — the leakage test cannot run. Report that it could not run rather than
  reporting a pass.
- *The audit's output reported as a result about a model* — prohibited, including in the alternate flow
  where a detector is the instrument. The audit's subject is the corpus in every branch; it emits no
  figure of merit for a detector or an EPoL model, and no pass/fail verdict on either (brief §3b, D2.23).

**Note on why this case matters more under this system than it did.** The traffic manager's idle cull
truncates the ambient stationary distribution at ninety seconds, which is also what suppresses accidental
positives today. Under `SumoDrivenPlayback` the traffic manager does not run, so long ambient stops become
possible — a gain ([01 §11](01_Architecture.md)) that raises the accidental-positive rate at the same
time. The realism gain and the audit are coupled and must not be sequenced apart
([20 §2.8](../../Findings/20_Behavioral_Annotation_And_Areas_Of_Interest.md)). The illumination coupling
arrives by the same route: correct time of day is a realism gain that simultaneously creates a leakage
channel, and the same rule applies — they ship together.

**Postconditions.** Candidates identified; illumination leakage quantified and recorded; the corpus is
either accepted, corrected, has spans excluded, or is paired with a counter-illumination capture. The
report is an artifact of the corpus and an input to UC-10, not a document that stops here.

**Artifacts.** Audit report, including the per-class illumination distributions and the separability
result.

---

### UC-12 — Configure and launch a run

| | |
|---|---|
| **Primary actor** | Capture operator |
| **Supporting** | Live exercise operator, scenario author (indirectly, through the epoch and named windows), CARLA server |

The tool suite needs a coherent operator surface over time of day and everything else a run depends on;
that is a first-class deliverable rather than a by-product (brief §3a).
[12 — Operator control surface](12_Operator_Control_Surface.md) owns its **design** — what kind of program
it is, what its commands are called, what it shows. This case owns the **actor-facing flow**: what an
operator must be able to do, in what order, and what must refuse.

**What already exists, and what it tells us.** The interactive viewer already exposes the sun to an
operator: `--time` (*"start local solar time as HH:MM or decimal hours (default: 12:00, local solar
noon)"*), `--date`, `--time-advance` and `--time-rate`
(`CarlaControl/src/carlacontrol/CarlaControlArgumentParser.py:244`, `:251`, `:257`, `:265`), plus a
runtime `K` toggle and a solar HUD polled from `get_solar_state`
(`CarlaControl/src/carlacontrol/PygameInterface.py:258-268`, `:296`, `:505-508`). **So the controls are
not the missing piece.** What is missing is that none of this is on the *capture* path, none of it is
derived from a scenario's clock, and all of it is optional. The work is putting the choice on the capture
path and making it impossible to skip — which is why this is a use case and not a feature request.

**Preconditions.** A validated `ScenarioPackage` carrying an epoch; a world package; either a run-list
entry from UC-6 or an operator composing one directly. A running server is needed to *launch*, and not to
*compose*.

**Main flow.**
1. Operator selects the scenario package and the world. The surface shows the binding — world digest,
   build recipe — and applies UC-5 step 1's two-tier gate here rather than letting a mismatched pair
   proceed to a server.
2. The surface shows the scenario's **epoch** and its **named windows**, rendering each window's derived
   civil date and time beside its simulated span. This is the point at which an operator sees
   `t = 82,800 s` as *"day 0, 23:00 local"* — the mapping that today exists only in the generator's source
   and in identifier text (UC-3, measured).
3. Operator chooses a window: a declared preset, or an arbitrary span. The surface re-derives the civil
   date and time for whatever was chosen and shows it immediately, before anything else is configured.
4. Operator chooses the **illumination policy**: frozen at the window's start instant, or advancing with
   simulated time at a stated rate; and, if they want one, an explicit override of the derived civil time.
   The surface shows the resulting sun elevation at the window's begin and, for an advancing policy, at
   its end — **so a night window is visibly a night window before a single actor is spawned**, and an
   override is visibly an override.
5. Operator chooses the camera set, the seed, the render caps, the recorders, and whether a live TAK feed
   and the engine recorder run alongside.
6. The surface composes one **run configuration** and validates it as a whole, re-running UC-5 including
   the epoch, reach, solar-frame and window-sanity checks against the values actually chosen — not against
   the scenario's declared defaults.
7. Operator launches. The run configuration is handed to UC-7 (or UC-8) intact and is copied verbatim into
   the manifest, so what was launched is recoverable from the corpus without reading anyone's shell
   history.
8. While the run is live, the surface shows health — render-set occupancy, refusal rate, frame delivery,
   SUMO step budget — and the live solar readback, which costs nothing because `get_solar_state` reads the
   tick-paired world-observer cache with no RPC (`carlanet/__init__.py:1511-1533`; `CarlaClient.cs:1991`).
   The instrumentation behind the health figures is [10](10_Scale_And_Performance.md)'s; the surface
   displays it and does not define it.

**Alternate flows.**
- *Launch a whole run list* — UC-6's list is the input and entries run in sequence, each with its own
  illumination stratum applied and recorded. The surface refuses a list whose entries differ in
  illumination without carrying the stratum (UC-6 failure flow), because it is the last place that is
  cheap to catch.
- *Scripted launch* — the same composition and the same validation, invoked as a library rather than
  driven by a human. The requirement is that the composition step cannot be bypassed, not that a person
  must sit in front of it. A script that builds a run configuration goes through the same validation and
  produces the same record.
- *Compose offline* — steps 1 through 6 with no server, producing a stored run configuration to be
  launched later. Same motivation as UC-5's offline alternate flow: the part a human iterates on should
  not need a GPU.
- *Live exercise* — the same composition with UC-8 as the launch target, differing only in pacing and in
  the downstream consumers configured.

**Failure flows.**
- *The scenario declares no epoch* — the surface will not compose a run at all, and says which scenario
  and what is missing. This is the same refusal UC-7 makes; making it here as well means it happens before
  a server, a world load and a SUMO pre-roll have been spent on it.
- *A window outside the scenario's end time* — refused at composition, not at session start.
- *An illumination policy that cannot be previewed* — composing offline, there is no server to ask for a
  sun elevation. Permitted, but the configuration is marked *unpreviewed* and UC-7 re-derives and
  re-verifies at session start regardless. **A preview is never a check**; it is the same rule UC-5 step
  13 and [20 §5.5](../../Findings/20_Behavioral_Annotation_And_Areas_Of_Interest.md) apply to annotations.
- *Population authority is already held* — surfaced at composition as a warning naming the holder, so an
  operator finds out before configuring a whole run. The hard refusal stays where D2.6 put it, at session
  start; showing it early is a courtesy and must not be mistaken for the gate.
- *The operator edits a configuration mid-run* — refused. The manifest asserts the configuration it opened
  with, so a run's configuration is immutable once the manifest opens. A change means a new run.
- *A configuration with any field unset* — refused. There is no field that falls back to "whatever the
  world happened to be in", and illumination is the field that makes this rule worth stating, because it
  is the one most likely to be left to a default nobody chose.

**Postconditions.** A run configuration exists, is validated, and is either launched or stored for later.
Every field that affects the corpus — illumination included — has an explicit, recorded value chosen by a
named actor.

**Artifacts.** The run configuration; the launch record; the live health and solar display, which is not
an artifact but is part of the contract.

---

### UC-13 — Run a session an external process starts, observes and stops

| | |
|---|---|
| **Primary actor** | Automation process (external software agent, non-interactive) |
| **Supporting** | Capture operator (who authored the parameter set), CARLA server, solar authority (`CesiumSunSky`), `sumo` |

Numbered after every existing case so that sibling citations to UC-1 through UC-12 hold
([01](01_Architecture.md) cites `02 UC-10`, [12](12_Operator_Control_Surface.md) cites open question 2).

**What this case is, and what it is not.** The user's words: *"Cyclic generation is not for us to
control. Our tools suite is used to create the synthetic imagery. The external processes that drive the
cyclic regeneration are in full control of when to terminate and what to do with the data generated and
what comes next... The external processes... have the ability to kill the SUMO and/or CARLA server at
their leisure or use the CarlaNet and Python shim to query data so as to decide when enough is enough."*
([`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3d). Nothing trains during a run, nothing in this pipeline trains at
all, and nothing here judges a run on the caller's behalf. What is ours is exactly four things:

1. **the invocation** — a run can be started by something that is not a person, with an explicit
   parameter set, and without anything on the path that expects a person;
2. **the determinism** — the same parameter set describes the same run, and a varied one varies only
   what was varied;
3. **observability while running** — the caller can find out, through CarlaNet and the Python shim, what
   a run has produced so far, so that it — never this system — can decide when it has seen enough;
4. **what survives an arbitrary stop** — whatever is on disk at the instant the caller ends the run is
   valid, self-describing, and honestly says it was stopped rather than finished.

The cadence between invocations, the decision that a run has produced enough, the retention of old
corpora, and whatever trains on any of it are **all outside** (D2.29). There is no training loop here, no
model lifecycle, no fitness verdict of ours to hand back, and no feedback of any kind from a model back
into what is generated — which would be UC-8's refused transcript-acting flow one level up, and is
refused here for the same reason.

#### 13a. Most of this already exists

| What being driven by an external process needs | What already provides it | What is still missing |
|---|---|---|
| **A parameter set that fully determines a run** | UC-12's run configuration, every field explicit, composed and validated in one place, with no field falling back to "whatever the world happened to be in" (D2.22); [12](12_Operator_Control_Surface.md) D12.3's `EffectiveRunConfiguration`, whose manifest copy *is itself a valid run configuration* | Nothing at the design level |
| **Non-interactive invocation** | [12 §3.8](12_Operator_Control_Surface.md)'s short command line and UC-12's scripted-launch alternate flow, which requires that the composition step cannot be bypassed rather than that a person perform it | Nothing beyond what UC-12 already requires |
| **Varied parameters across separate invocations** | UC-6's run list: entries individually runnable, collectively joinable, each carrying a seed, a window, a camera set, the instance ids the variation touched and an illumination stratum | Nothing. §6a's marking rule applies unchanged, and matters more here, because nobody is watching the arms diverge |
| **Reproducibility** | D2.15 (the sun is positioned explicitly and never inherited); D2.22 (no host-state fallback); [12](12_Operator_Control_Surface.md) D12.11 (seeds have no nondeterministic default — today `--seed` defaults to `None`, documented "nondeterministic", `CarlaControl/src/carlacontrol/CarlaControlArgumentParser.py:310-316`); [08](08_Collection_And_EPoL.md) D8.30 (a session never takes its illumination from the host clock) | Nothing at the design level. One measured hazard is in 13b |
| **Observability while a run is in progress** | `get_solar_state` is free and tick-paired (`carlanet/__init__.py:1511-1533`); `get_sim_time` reads the same cache (`carlanet/__init__.py:2017`); `get_actors`/`ActorList.find()` (`carlanet/__init__.py:2038-2049`, `:1043-1048`) read actor state with no extra RPC. All three are queries a second, non-authoritative client can make against the running server without contending with the session driving it — the pattern UC-7's own alternate flow already relies on for a tick-follower camera process reading "supervision, the render set, session identity and solar state from the server" alongside the session that owns the tick. The run manifest itself is written incrementally throughout a UC-7 session (UC-7 step 10) and is readable on disk while it grows | **A genuine gap, named rather than designed around.** Nothing on the CarlaNet/shim surface turns "how many annotated intervals have closed" or "how much of a declared area has been covered" into a single live query. Today that means reading the growing manifest directly, not calling an RPC for the number |
| **An honest record after an arbitrary stop** | The incremental manifest write already required by UC-7 step 10 means an artifact is valid before any closing step ever runs | **A gap.** Nothing today distinguishes a manifest closed because a window reached its own end from one left open because something ended the process first — see 13b |

#### 13b. What is measured, and what it establishes

Read from the tree on 2026-09-18. None of this is a defect in the sense of needing a bound of our own —
§3d rules that out explicitly. It is a defect in what the tooling's own signals say afterwards: today
they say "success" regardless of which of the things below actually happened, and that is the silent
corruption the whole requirement exists to prevent.

| Reading | Where | What it establishes |
|---|---|---|
| `main` has exactly two return paths: `return 1` when the world build fails, and `return 0` at the end, reached from every other exit of the loop below it | `run_SCTMV.py:113`, `:130`, `:339`, with `sys.exit(main())` at `:343` | An unattended caller can learn "the world build failed" and **nothing else**. A run that captured nothing, a run whose sun readback disagreed, a run that dropped every frame, and a run the caller deliberately ended are all indistinguishable from a normal finish |
| The loop condition is `while pg.running and not stop["flag"]`; no `--duration`, `--frames`, `--until` or headless switch exists among the parser's options | `run_SCTMV.py:251`; `CarlaControlArgumentParser.py` (enumerated 2026-09-18; the nearest thing is `--settle` at `:193`, which bounds a build, not a session) | There is no run-length bound of ours to reach — consistent with §3d, not a gap against it. What is missing is any way for a caller without a display attached to tell how far the run has got; [12](12_Operator_Control_Surface.md) owns whether that needs a headless mode |
| A `SIGINT` handler exists only to set `stop["flag"]`, installed because "pythonnet can swallow `KeyboardInterrupt`"; the loop's own exception handler then discards the interrupt a second time | `run_SCTMV.py:243-246` (handler and its own comment), `:251` (the flag in the loop condition), `:291-292` (`except KeyboardInterrupt: pass`) | **The interrupt is swallowed twice over.** Whether it survives pythonnet or is caught by the handler, it is discarded either way, and the loop falls into the same `finally` block and the same unconditional `return 0` at `:339` as `pg_quit` or as reaching the scenario's own end. A deliberate stop leaves exactly the same trace as a normal one |
| `RunReport` exists and is the closest thing to a machine-readable result in the tree: `events`, `updates`, `vehicles`, `sim_seconds`, `wall_seconds`, `sinks`, and a derived `achieved_real_time_factor` | `CarlaControl/src/carlacontrol/SumoCotBridge.py:155-168` | The *shape* is right and is worth reusing for the honest-record artifact this case needs |
| That report is logged as prose and written nowhere: `logging.info("wrote %d events across %d updates for %d vehicles", …)` | `CarlaControl/scripts/sumo_cot_telemetry.py:165-166` | **The result exists in memory and dies there.** An on-disk record of what a run produced — never a verdict about it — is the actual gap |
| Subsystem scheduling was deliberately moved onto the world's clock rather than the wall's, because "two runs of the same seed then spawn on different frames and diverge from there: measured across a pair of them, the nth vehicle appeared up to 558 frames apart by the end, while the traffic manager itself drove the early vehicles bit-identically for over a thousand frames" | `run_SCTMV.py:270-277` (comment), fix at `:278` | **Determinism under repeated invocation is a solved class of bug with a measured precedent.** Anything a run schedules must be a duration in *simulated* seconds. Under invocations nobody is watching side by side, there is nobody to notice two runs drifting apart |

**Inference**, labelled as such: the 558-frame divergence was measured on the ambient traffic manager,
which is locked out under `SumoDrivenPlayback` (brief §3, decision 4), so that specific defect cannot
recur here. What generalises is the *rule* — schedule on the world's clock — and it binds every new
component a run touches.

**Preconditions.**

- A validated `ScenarioPackage` carrying an epoch, and a world package, both bound and digested (UC-5
  step 1's two-tier gate).
- **A stored parameter set**: a run configuration composed and validated earlier (UC-12's compose-offline
  alternate flow), a run-list entry (UC-6), or a stored configuration plus a named set of fields to vary.
  The automation process **does not compose one interactively**, because it cannot.
- **Every field explicit** (D2.22). This is a precondition a human operator effectively satisfies by being
  present; an automation process does not, which is why the rule is load-bearing here rather than merely
  tidy.
- **A seed that is an explicit value, or an explicit request to draw one that is then recorded**
  ([12](12_Operator_Control_Surface.md) D12.11).
- **A reachable server, or a stated way to obtain one.** Whether the automation process starts a server or
  attaches to a standing one is [09](09_Toolchain_And_Packaging.md)'s and
  [12](12_Operator_Control_Surface.md)'s; this case requires only that it is a stated property of the
  invocation and not a local habit.
- **Nothing on the invocation path that expects a person**: no prompt, no confirmation, no display, no
  hotkey, no terminal.

**Main flow.**

1. **The automation process names a parameter set.** A stored run configuration, a run-list entry, or a
   stored configuration with a small set of overrides — window, seed, illumination stratum, camera set.
   Overrides go through the same composition and the same validation a human's would; there is no
   unattended shortcut past UC-12 (D2.22; [12](12_Operator_Control_Surface.md) D12.5).
2. **The invocation is non-interactive, and the surface is the same one.** Anything that would ask a
   question refuses instead, naming the field. An unattended run that blocks on a prompt is
   indistinguishable, from outside, from one that is merely slow, and nothing here assumes anybody is
   watching for that.
3. **The run executes as UC-7** (or UC-9 for a replay). **Every refusal UC-7 makes, this case makes.** An
   unattended run gets no relaxation of the epoch check, the solar readback, the authority gate, the clock
   ratio, or the window bounds. It gets *more* scrutiny, not less, because nobody will read a warning.
4. **While the session runs, the automation process may observe it — through CarlaNet and the Python
   shim — to decide for itself whether it has seen enough.** It reads the same free, tick-paired solar and
   simulation-time state UC-12 step 8's live display reads (`carlanet/__init__.py:1511-1533`, `:2017`),
   the actors currently in the world (`carlanet/__init__.py:2038-2049`), and the run manifest growing on
   disk — which already carries instances, intervals, rendered spans and observed spans, written
   incrementally throughout the session (UC-7 step 10). Nothing it reads is a verdict of ours; it is the
   same facts the manifest would eventually carry anyway, read early.
5. **The automation process ends the run whenever it decides it has, by whatever means it chooses.**
   Closing the client cleanly is one way; killing the SUMO process, the CARLA server, or the client
   outright is another, and **this case treats the second exactly as normally as the first.** The main
   flow of this case ends **here** — at whatever instant the caller chooses — and not at a window's
   declared end or at any bound of ours. A window that runs all the way to the end UC-7 step 11 describes
   is simply the case where the caller happened not to stop it first; reaching that end is not required by
   this case and nothing here depends on it happening.
6. **Whatever the session had written up to that instant is left on disk, valid and self-describing**
   (brief §3d item 1; [04 — Contracts](04_Contracts.md) owns the exact per-artifact guarantee). If the
   ending was graceful enough for a closing step to run, it marks what it closes as **stopped** if the
   caller ended the run before the window's own end, or as **finished** if the window reached its own end
   first (UC-7 step 11) — the two are recorded differently because they mean different things to whatever
   reads the corpus next. If the ending was not graceful — the server or the client was killed outright —
   no closing step runs at all, and the **absence** of a finished marker is itself the honest signal: a
   reader treats an unclosed manifest as stopped, never as damaged.
7. **Where the invoking process itself is still alive to produce one, its exit code reflects what could be
   determined at that point.** Nothing here may depend on the exit code carrying more than that, because
   the caller may end the run by a means — a server-level kill — that never gives the invoking process a
   chance to choose one (13b). The artifact is the source of truth; the exit code, where one exists, is a
   convenience on top of it, never the record itself.
8. **What happens next is outside.** Whether the automation process invokes another run, with the same or
   varied parameters, and what it does with what this one produced — keep, discard, train on it, archive
   it — is entirely its own business, decided from what it observed and from nothing this system asserts
   about fitness.

**Alternate flows.**

- *Walk a whole run list* — one session per entry, which is
  [12 open question 1](12_Operator_Control_Surface.md)'s recommendation, stopping on the first entry
  whose own checks observed a problem, so that a broken parameter set costs one window rather than twelve.
  Whether the walker exists is [12](12_Operator_Control_Surface.md)'s; what this case owns is that **each
  entry still produces its own record and its own corpus identity**, so a stopped list is legible rather
  than a partial something.
- *Invoked again with the same parameter set* — each invocation is a fresh session with its own identity;
  the scenario and world digests are shared, so the results are joinable. **Nothing is appended to,
  merged into, or edited in a previous corpus**: a closed corpus is immutable (D2.10; UC-10 open question
  12(c)'s frozen identity). Two runs of one parameter set are two joinable populations, not one growing
  one. Whether, when and how many times the automation process invokes again is entirely its own decision.
- *Vary parameters across invocations* — behaviour, appearance, or both. §6a's rule applies unchanged and
  binds harder here: an expansion that varies behaviour and illumination together without marking the
  stratum is refused, because an unmarked entry is unstratifiable later and there is no operator in the
  room to remember which invocation was which.
- *A composition-only invocation* — compose and validate with no server, no GPU and no SUMO (D2.2;
  [12](12_Operator_Control_Surface.md) D12.5's phase 0 covers 21 of 33 checks). Cheap enough to run ahead
  of the expensive one, and it catches a parameter set broken by a changed scenario before a world is
  loaded.
- *A run that produces no corpus by design* — a validation-only or dry-composition invocation is
  legitimate. It still writes a record, and the record says a corpus was not produced **and that this was
  intended**. "Produced nothing on purpose" and "produced nothing" must not look alike.
- *An operator inspecting a run's output afterwards* — ordinary UC-11 and UC-10 work. A corpus nobody
  watched being made is not exempt from the audit; if anything the audit matters more, because no human
  saw the run.

**Failure flows.**

- *Anything on the path would prompt, confirm or wait for input* — **refuse immediately with a named
  reason and a non-zero exit; never wait.** This is the failure mode unattended invocation creates and it
  has no analogue in any other case in this section.
- *A field is unset* — refused at composition (D2.22). Stated again here because unattended invocation is
  what makes it acute: there is nobody to notice that a default was taken, and a default that changes
  between invocations silently changes the corpus.
- *A nondeterministic seed* — refused, or resolved to a drawn value that is then recorded
  ([12](12_Operator_Control_Surface.md) D12.11). Runs that cannot be reproduced individually cannot be
  explained afterwards, by anyone.
- *A quality check observes a problem* — **this is a fact, not a crash.** It is recorded as what the check
  observed and against what it compared, exactly as it would be for a session someone watched (D2.29). No
  aggregate label is derived from it, because that would be a fitness verdict this system does not make.
- *An artifact is ambiguous about whether it reflects a complete run or a stopped one* — this must not
  happen, and is the one outcome to design against absolutely: an automation process that cannot tell a
  stopped run from a finished one, or a finished one from a damaged one, will hand whatever is on disk to
  whatever consumes it next with no way to know which it got. The measured state of the tree already shows
  the failure this guards against — every ending of the loop, deliberate or not, currently reaches the
  same `return 0` (13b) — which is exactly why the artifact, not the exit code, has to be where the
  distinction actually lives.
- *A closing step marks a stopped run as finished, or the reverse* — a defect, not a tolerance. The two
  words mean different things to whatever reads the corpus next, and getting them backwards is worse than
  not writing either.
- *Two invocations of one parameter set that are not reproducible* — a defect, not a tolerance. The
  recorded parameter set is the run's description ([12](12_Operator_Control_Surface.md) D12.3), and
  anything scheduled against wall-clock time rather than the world's clock breaks that description
  silently (`run_SCTMV.py:270-277`, measured).
- *Population authority is held by something left running from an earlier invocation* — the session fails
  naming the holder (D2.6), and the record says so. Worth naming separately because repeated invocation
  makes it likely: the most probable holder is an earlier run that was killed rather than closed.
- *The automation process asks for a model metric, a training verdict, or whether this run's corpus is
  "better" than another's* — **not a capability this system has** (D2.23). What a run emits is a corpus
  and a statement about that corpus, in the corpus's own units. Comparing corpora against a model is the
  work of whoever trains, and it happens outside.
- *Feedback from a model into what the next invocation generates* — refused, and this is UC-8's
  transcript-acting refusal one level up. Choosing the next run's parameters from a model's opinion of the
  last corpus is a training loop wearing an automation process's clothes, and it produces data whose
  distribution is a function of the model it will be used to train. Nothing here reads model output at
  all, so the refusal is structural rather than a rule to remember.

**Postconditions.** Whatever is on disk at the instant the caller ends the run is valid and
self-describing, however abruptly that instant arrived — a run interrupted mid-window is a shorter corpus,
never a corrupt one (brief §3d item 1). It states, without inference, how far the run got: the corpus
identity, the window in both simulated and civil time, and the spans and counts recorded up to that tick.
Where a closing step ran, it is marked **stopped** or **finished** according to which one actually
happened; where none ran, the absence of a finished marker is read as stopped, not as damaged. Exactly
which fields carry that distinction, per artifact type, is [04 — Contracts](04_Contracts.md)'s; this case
owns only that the distinction is recoverable regardless of when or how the caller ends the run. The
parameter set that produced whatever exists is recorded with it and is itself a valid parameter set, so
the same run can be repeated. **Nothing trained, nothing about any model was measured, and no aggregate
judgement of fitness is made on the caller's behalf** — only the individual facts whichever quality checks
ran were able to observe before the stop (D2.29).

**Artifacts.** The effective run configuration; whatever the manifest had incrementally written at the
instant the run ended, closed and marked stopped or finished, or left open and read as stopped; the
corpus, to whatever extent one was produced; and, for whichever quality checks ran, the individual facts
each one observed — never an aggregate verdict.

---

## 4. Activity diagram — authoring a scenario

Partitions are actors and system components. This is UC-3 and UC-4 with UC-5 inlined, because in practice
they are one sitting.

```mermaid
flowchart TB
    subgraph AUTHOR["Scenario author"]
        direction TB
        A1["Describe the area, the ordinary day,<br/>and what is out of the ordinary"]
        A2["Declare the epoch:<br/>civil date, civil UTC offset,<br/>the civil instant t = 0 means"]
        A3["Declare the named windows<br/>worth capturing, and why"]
        A4{"Does the resolution report<br/>say what I meant —<br/>places, types, hours?"}
        A5["Correct the description<br/>or the epoch"]
        A6["Accept"]
    end

    subgraph ASSISTANT["Authoring assistant"]
        direction TB
        B1["Resolve places against the street index<br/>and the area table"]
        B2["Resolve vehicle classes<br/>against the catalogue"]
        B3["Reconnoitre edges for every source,<br/>sink, gate and waypoint"]
        B4["Write flows and scheduled vehicles;<br/>every authored hour written as a<br/>simulated second THROUGH the epoch"]
        B5["Merge onto one<br/>departure-sorted timeline"]
        B6["Write the configuration"]
        B7["Declare pattern instances,<br/>participants, roles, intervals"]
        B8["Mark the deliberately ordinary<br/>vehicles as nominal"]
        B9["Flag every instance whose<br/>distinguishing feature is its hour,<br/>for the UC-11 audit"]
    end

    subgraph TOOLS["SUMO toolchain"]
        direction TB
        C1["duarouter proves every route"]
        C2["Dry run: is the population stable,<br/>are there route errors?"]
    end

    subgraph VALIDATOR["ScenarioValidator and BehaviouralAnnotationCompiler"]
        direction TB
        D1["Bind to the world digest"]
        D2["Check the epoch: present, real date,<br/>offset in range and a 15-minute multiple"]
        D3["Check epoch reach: every instant in<br/>0..end resolves to a civil date AND time<br/>(the solar clock wraps at 24 h<br/>and carries no date)"]
        D4["Compare the epoch's civil offset<br/>against the world's longitude/15;<br/>compute and record the difference"]
        D5["Check frame: convBoundary<br/>vs OpenDRIVE extent, netOffset zero"]
        D6["Check departure order"]
        D7["Bind every vType to a catalogue<br/>entry within dimension tolerance"]
        D8["Compile the AnnotationSet; resolve every<br/>label, participant, area, interval;<br/>reject any label naming a light state"]
        D9["Check the clock ratio"]
        D10["Advisory: cross-check hour-bearing<br/>identifiers against the derived hour"]
        D11["Emit the resolution report,<br/>including every window's and interval's<br/>civil date and time"]
    end

    A1 --> A2 --> A3 --> B1 --> B2 --> B3 --> C1
    C1 -->|"route refused"| B3
    C1 -->|"all routes proved"| B4 --> B5 --> B6 --> B7 --> B8 --> B9
    B9 --> D1 --> D2
    D2 -->|"missing or absurd:<br/>REFUSE, do not default"| A5
    D2 -->|"ok"| D3 --> D4 --> D5 --> D6 --> D7 --> D8 --> D9 --> D10 --> C2
    C2 -->|"population climbs,<br/>or route errors"| B4
    C2 -->|"stable"| D11 --> A4
    A4 -->|"no"| A5 --> B1
    A4 -->|"yes"| A6
```

Five things this diagram is asserting, each grounded:

- **`duarouter` sits inside the loop, not after it.** Route validation with a graph walk gives false
  positives that surface only at SUMO load (measured gotcha, recorded in the skill), so it belongs where
  a failure is cheap.
- **A dry run is part of authoring.** A population that climbs monotonically rather than settling is a
  demand error the validator cannot see statically; netconvert's guessed fixed-time 90 s signal programs
  are a known cause that a busy interchange cannot discharge (measured gotcha).
- **The epoch is declared before any demand is written, and its failure returns to the author.** It is
  written second only to the description because everything after it — every departure instant, every
  interval — is expressed through it. A missing epoch has no machine fallback (UC-7 failure flow), so the
  only edge out of `D2` on failure goes back to a human.
- **The epoch is checked for *reach*, not only for form.** `D3` exists because the solar clock wraps at 24
  hours and carries no calendar (`CesiumHeightSampler.cpp:730`; `CesiumTimeOfDayController.cpp:34-35`),
  while the sizing scenario spans seven days (**measured**, `<end value="604800"/>`). A validator that
  checked only the hour would pass a scenario whose day-6 window renders under day-0's sun.
- **The author's acceptance is of the resolution report, not of the scenario file.** An annotation carried
  in a vendor construct is invisible to a foreign previewer, so a preview cannot check that annotations
  are right — only the compile step can
  ([20 §5.5](../../Findings/20_Behavioral_Annotation_And_Areas_Of_Interest.md)). The report now carries
  the civil times as well, which is the only place an author can catch an epoch that is right in form and
  a day out in fact.

---

## 5. Activity diagram — running a capture

This is UC-7, entered from UC-12. Partitions are the components of [01 §2.3](01_Architecture.md), plus
the **solar clock binding** — a role, not a component name; [11](11_Time_And_Illumination.md) names the
component that performs it.

```mermaid
flowchart TB
    subgraph OP["Capture operator, through UC-12"]
        direction TB
        O1["Launch a validated run configuration:<br/>mode, package, window, cameras, seed,<br/>illumination policy"]
        O2["Read the failure reason"]
        O3["Stop the session"]
    end

    subgraph SESSION["CaptureSession"]
        direction TB
        S1["Acquire population authority"]
        S2{"Granted?"}
        S3["Assign session identity,<br/>scenario id, stable sensor ids"]
        S4["Publish the AnnotationSet<br/>as WorldSupervisionState"]
        S5["Disable advancement;<br/>close the manifest;<br/>release the lease;<br/>restore asynchronous mode"]
    end

    subgraph SUN["Solar clock binding"]
        direction TB
        T1{"Does the scenario<br/>declare an epoch?"}
        T2["Derive the civil date and time<br/>of the window's BEGIN instant"]
        T3["Set the date, then the time,<br/>on the solar authority"]
        T4{"Readback agrees<br/>within tolerance?"}
        T5["Apply the policy:<br/>leave frozen, or enable<br/>advancement at rate"]
        T6["Record epoch, derived civil time,<br/>policy, rate, solar-frame offset,<br/>override, readback at begin and end"]
    end

    subgraph CLOCK["PlaybackClock"]
        direction TB
        K1["Validate the clock ratio<br/>against the loaded world"]
        K2["Step SUMO with nothing rendered<br/>until the window starts<br/>(advancement still off)"]
        K3["Apply poses for this instant"]
        K4["Cue the world"]
        K5{"Frame delivered?"}
        K6{"SUMO step boundary?"}
        K7["Step SUMO; bulk-read all vehicles"]
        K8{"Window ended?"}
    end

    subgraph BRIDGE["RenderSetSelector and RenderedVehicleRegistry"]
        direction TB
        R1["Reconcile the render set:<br/>window, render volume,<br/>annotated participants first"]
        R2["Bind and spawn at full opacity;<br/>record the admission tick"]
        R3["Destroy;<br/>record the release tick"]
        R4["Record admissions and refusals"]
    end

    subgraph REC["FrameRecorder, per camera"]
        direction TB
        F1["Decimate against the frame timestamp"]
        F2["Positional truth from the snapshot<br/>of THIS frame"]
        F3["Merge SUMO speed and course"]
        F4["Merge the supervision snapshot<br/>stamped with this tick"]
        F5["Merge the solar state from the same<br/>tick-paired cache (no RPC)"]
        F6["Write PNG and CoT sidecar<br/>including the solar element"]
    end

    subgraph MAN["RunManifestWriter"]
        direction TB
        M1["Append intervals, rendered spans,<br/>observed spans per sensor,<br/>refusals, SUMO collision warnings"]
    end

    O1 --> S1 --> S2
    S2 -->|"no: holder named"| O2
    S2 -->|"yes"| K1 --> T1
    T1 -->|"no: REFUSE<br/>no civil time derivable"| O2
    T1 -->|"yes"| T2 --> T3 --> T4
    T4 -->|"no, or the sun refused:<br/>REFUSE"| O2
    T4 -->|"yes"| S3 --> S4 --> K2 --> T5 --> K3 --> K4 --> K5
    T5 --> T6 --> M1
    K5 -->|"no: session fault"| O2
    K5 -->|"yes"| F1 --> F2 --> F3 --> F4 --> F5 --> F6 --> M1
    F6 --> K6
    K6 -->|"yes"| K7 --> R1
    R1 --> R2
    R1 --> R3
    R1 --> R4 --> M1
    R2 --> K8
    R3 --> K8
    K6 -->|"no"| K8
    K8 -->|"no"| K3
    K8 -->|"yes"| T6
    T6 --> S5
    O3 --> S5
```

Six things this diagram is asserting:

- **Authority is acquired before anything else happens.** The lockout is the first node with an outcome,
  not a check somewhere in the middle ([01 §5.3](01_Architecture.md)).
- **The sun is bound before the session identity is assigned, and before the pre-roll.** Both refusals —
  no epoch, and a readback that disagrees — happen before the SUMO pre-roll is spent. The pre-roll is
  cheap (at most ~140 s to reach any instant in the seven-day scenario, [10
  §4.2.1](10_Scale_And_Performance.md), measured) but it is not free, and neither is a world load.
- **Advancement is enabled after the pre-roll and disabled on stop.** `T5` sits between `K2` and the first
  `K3` so the sun positioned at `T3` cannot drift before the first captured frame, and `S5` clears it
  because advancement is world state that outlives the session
  (`CesiumTimeOfDayController.cpp:14-37`: it advances on every world tick while enabled, and nothing in
  the session's teardown would otherwise turn it off).
- **Nothing is inherited.** `T3` runs on every session, including one against a world that already has a
  sun, because the noon default is applied only when no `CesiumSunSky` exists
  (`CesiumHeightSampler.cpp:396-402`) and a loaded world therefore keeps the last session's sun.
- **Truth is taken from the snapshot of the frame the pixels came from.** Today the recorder takes the
  tick from the image header (`FrameRecorder.cs:179`) but the vehicle truth from whatever the
  world-observer cache last held (`FrameRecorder.cs:148`), while the paired depth capture *is* tick-matched
  (`FrameRecorder.cs:156`). The two halves of one capture use different rules today;
  [03](03_CoSimulation_Runtime.md) owns closing that. `F5` joins the same queue: solar state comes from the
  tick-paired cache (`carlanet/__init__.py:1511-1533`), so it must be the cache for *this* frame's tick.
- **The manifest is written throughout, not at the end.** A run that fails at minute forty of forty-five
  otherwise keeps every capture and loses all its supervision
  ([20 §7.5](../../Findings/20_Behavioral_Annotation_And_Areas_Of_Interest.md)). The solar record is
  written at `T6` at the start and completed at the end, for the same reason.

**This diagram's per-tick mechanics also govern UC-13**, entered by an automation process through UC-12
exactly as a human enters it. What is not on this diagram at all is the thing UC-13 adds: an external stop
can arrive at **any** tick, not only once the window ends, and it can arrive as a kill that gives `S5` no
chance to run. §5b draws that.

---

## 5a. Activity diagram — running a live exercise

This is UC-8, entered from UC-12. It is drawn because the live exercise is a primary use case and
because **the boundary is the thing a reader most needs to see**: the last partition is outside this
system, it contains no step of ours, and the two arrows that cross into and out of it are the only
contact. Partitions are the components of [01 §2.3](01_Architecture.md), plus the **solar clock binding**
and the **emission boundary** — both roles, not component names.

Everything that UC-7's §5 diagram does before the window opens is elided here as one node (`L2`), because
it is identical and repeating it would suggest it is not. What differs starts at `L3`.

```mermaid
flowchart TB
    subgraph OP["Live exercise operator, through UC-12"]
        direction TB
        O1["Launch a validated run configuration:<br/>mode, package, window, cameras, seed,<br/>illumination policy, PACING BAND<br/>(target and floor), emission endpoint,<br/>truth-feed choice"]
        O2["Read the refusal"]
        O3["Watch: achieved factor vs target and floor,<br/>drop count per channel, what is attached<br/>and since when, the sun against the wall clock"]
        O4["Stop the exercise"]
    end

    subgraph SESSION["CaptureSession and solar clock binding"]
        direction TB
        L2["Everything UC-7 does before the window:<br/>authority, synchronous mode, clock ratio,<br/>epoch, sun set and READ BACK,<br/>session and sensor identity,<br/>SUMO connect, supervision published"]
        L3{"Emission endpoint<br/>can be OPENED?<br/>(not: is anyone listening)"}
        L4["Apply the illumination policy;<br/>open the window"]
        L9["Close: flush, release vehicles,<br/>disable advancement, close the manifest,<br/>release the lease, restore async mode,<br/>close and digest the transcript sink"]
    end

    subgraph CLOCK["PlaybackClock — paced"]
        direction TB
        P1["Step to the next instant against an<br/>ABSOLUTE wall-clock target, within the<br/>DECLARED BAND (never a per-step sleep).<br/>Only input: OUR OWN queue depth"]
        P2["Apply poses; cue the world"]
        P3{"Window ended?"}
    end

    subgraph CH["Collection channel, per sensor"]
        direction TB
        C1["Capture; decimate; decode"]
        C2["Compose ONE collection frame:<br/>image + pose + intrinsics + radiometry<br/>+ achieved solar. No scene-derived field"]
        C3["Write coverage for this (sensor, tick)"]
    end

    subgraph EMIT["Emission boundary — ours"]
        direction TB
        E1{"Our outbound queue<br/>draining, or are we<br/>at the FLOOR factor?"}
        E2["Send the frame"]
        E3["At the floor: DROP OLDEST;<br/>count per sensor; mark the tick<br/>COVERED BUT NOT DELIVERED"]
        E4{"Anything returned?"}
        E5{"Transcript sink<br/>configured?"}
        E6["Append an OPAQUE entry:<br/>payload, arrival wall time, arrival tick,<br/>source id, content type. Nothing else"]
        E7["Discard"]
    end

    subgraph TRUTHCH["Truth channel — separate, OFF by default"]
        direction TB
        T1["Truth sidecars and CoT<br/>to their own endpoint"]
    end

    subgraph EXT["Attached exploitation chain — OUTSIDE this system"]
        direction TB
        X1["Attaches, consumes frames.<br/>Composition unspecified:<br/>one illustrative adapter is<br/>detect-and-track ➜ EPoL ➜ reports.<br/>Late join legal. Detach legal.<br/>ZERO consumers is a normal state"]
        X2["May push something back.<br/>May push nothing.<br/>Either is a normal exercise"]
    end

    O1 --> L2 --> L3
    L3 -->|"no: REFUSE, name the endpoint.<br/>This is OUR end failing"| O2
    L3 -->|"yes"| L4 --> P1 --> P2 --> C1 --> C2 --> C3
    C3 --> E1
    C3 --> T1
    T1 --> O3
    E1 -->|"draining"| E2
    E1 -->|"at the floor"| E3 --> O3
    E1 -.->|"deepening, above the floor:<br/>slow within the band.<br/>NEVER on anything they send"| P1
    E2 ==>|"one arrow out"| X1
    X1 --> X2
    X2 -.->|"one arrow back"| E4
    E4 -->|"no"| P3
    E4 -->|"yes"| E5
    E5 -->|"yes"| E6 --> P3
    E5 -->|"no"| E7 --> P3
    E2 --> O3
    P3 -->|"no"| P1
    P3 -->|"yes"| L9
    O4 --> L9
```

Six things this diagram is asserting, each of which is a refusal somewhere in UC-8:

- **The external partition contains no step of ours, and exactly two arrows cross it.** `E2 ⇒ X1` and
  `X2 ⇢ E4`. There is no third. Nothing in the diagram reads inside `X1`, and `X1`'s own text says its
  composition is unspecified — the illustrative adapter is named there and nowhere else, so substituting a
  different chain redraws one node's label and no edge (§1.4, D2.26).
- **The reachability check is before the window, not inside it.** `L3` sits between the pre-roll and the
  first cued frame, so an unreachable endpoint costs a world load and not an audience. It is a **refusal**
  rather than a silent fall-back to the no-chain alternate, because the two look identical once running.
- **Nothing flows from `E4` back into `P1`, `C2` or `L4`.** The path from what came back goes only to a
  transcript or to a bin. A returned report never influences what is rendered, captured, annotated or
  stopped; if it did, there would be an edge here and there is not (UC-8 failure flow).
- **One edge reaches the clock, and it starts inside our own partition.** `E1 ⇢ P1` carries *our* queue
  depth and nothing else; there is no edge from `X1` or `X2` to `P1`, and its **absence** is the decision
  (§8a; [08 §11.3](08_Collection_And_EPoL.md) D8.40 clause 2). `P1` stays inside the band `O1` declared,
  and at the floor the response moves to `E3` rather than to the clock — so the world never blocks on a
  consumer at any factor.
- **Coverage is written by the channel, before the emission is attempted.** `C3` precedes `E1`, which is
  what makes a live exercise's coverage record trustworthy when the link was not: the camera's knowledge of
  what it saw does not depend on whether anybody received it ([08 §11.3](08_Collection_And_EPoL.md)).
- **Truth leaves on its own edge, to its own endpoint, and reaches only the operator.** `C3 → T1 → O3`
  never touches `EMIT`. The separation is two writers and two roots, not a filter
  ([08](08_Collection_And_EPoL.md) D8.17, D8.23, D2.9).

---

## 5b. Activity diagram — a session an external process starts, observes and stops

This is UC-13. The run itself is UC-7 unchanged (§5); what this case adds is at the edges — how a run is
entered without a person, how it can be watched while it runs, and the three different ways it can end.
**The stop edge is the one to read first**: it lands inside `RUN`, at any tick, not after it — because a
kill is the expected path, not a tidy exit the diagram can place at the end.

```mermaid
flowchart TB
    subgraph AUTO["Automation process — OUTSIDE this system"]
        direction TB
        N1["Choose a parameter set:<br/>a stored run configuration,<br/>a run-list entry, or one plus overrides"]
        N2["Observe, mid-run: query state via<br/>CarlaNet + the Python shim,<br/>or read the growing manifest"]
        N3["Decide it has seen enough,<br/>on its own terms"]
        N4["Stop the run — gracefully,<br/>or by killing the server<br/>or the client outright"]
        N5["Read whatever is on disk"]
        N6["Keep, discard, invoke again.<br/>Its own business —<br/>no verdict of ours to read"]
    end

    subgraph COMPOSE["UC-12 composition and validation — the same path a human uses"]
        direction TB
        V1["Compose the effective configuration:<br/>every field explicit, every value's layer<br/>and tool default recorded"]
        V2{"Any field unset,<br/>or a seed with no<br/>recorded value?"}
        V3{"Would anything<br/>prompt or wait<br/>for input?"}
        V4["Validate as a whole:<br/>epoch, reach, solar frame,<br/>window bounds, clock ratio"]
    end

    subgraph RUN["UC-7, unchanged"]
        direction TB
        R1["Run the window.<br/>Every UC-7 refusal applies.<br/>Manifest written INCREMENTALLY<br/>throughout — valid at every tick"]
    end

    subgraph HOWEND["How the session ended"]
        direction TB
        E1{"Window's own end reached,<br/>a graceful stop arrived first,<br/>or the process was killed outright?"}
        E2["Closing step runs:<br/>mark FINISHED"]
        E3["Closing step runs:<br/>mark STOPPED"]
        E4["No closing step runs at all.<br/>UNCLOSED is read as STOPPED —<br/>this is the guarantee, not a gap"]
    end

    subgraph RECORD["Record"]
        direction TB
        Z1["Record what each quality check<br/>observed, if any ran.<br/>NO aggregate verdict"]
        Z2["Exit code, only if the invoking<br/>process is still alive to set one.<br/>Never the sole record"]
    end

    N1 --> V1 --> V2
    V2 -->|"yes: REFUSE"| Z1
    V2 -->|"no"| V3
    V3 -->|"yes: REFUSE, never wait"| Z1
    V3 -->|"no"| V4
    V4 -->|"refused"| Z1
    V4 -->|"accepted"| R1
    R1 -->|"UC-7 failure flow"| Z1
    N2 -.->|"reads; never affects"| R1
    N3 --> N4
    N4 ==>|"at any tick —<br/>expected, not a failure"| R1
    R1 -->|"window's own end reached first"| E1
    N4 -.->|"or the stop<br/>arrives first"| E1
    E1 -->|"own end"| E2
    E1 -->|"graceful stop"| E3
    E1 -->|"killed outright"| E4
    E2 --> Z1
    E3 --> Z1
    E4 -.->|"nothing left to write —<br/>the artifact already says enough"| N5
    Z1 --> Z2 --> N5
    N5 --> N6
    N6 -.->|"invoke again? outside.<br/>Never a training loop"| N1
```

Five things this diagram is asserting:

- **The stop edge, `N4 ⇒ R1`, lands inside the run, not after it.** It can arrive at any tick, and the
  diagram treats it with the same weight as reaching the window's own end, because both are normal ways
  for this case to end (main flow step 5).
- **`E4` is a real, intended branch, not a missing edge.** A hard kill reaches no closing step and writes
  no record — nothing can, once the process is gone — and that is exactly why the manifest must already be
  valid at every tick (`R1`'s own note) rather than only once closed. **Not every path reaches `RECORD` any
  more**, and that is a deliberate consequence of the correction, not an omission: what the case guarantees
  is that whatever the manifest already holds is honest, whether or not anything closes it.
- **The entry goes through UC-12 and not around it.** `N1 → V1` and never `N1 → R1`. An unattended run is
  composed and validated by the same path a human's is, which is D2.22 and
  [12](12_Operator_Control_Surface.md) D12.5, and is why this case needs no second validator and adds no
  second set of checks.
- **`V3` is the only genuinely new gate at composition**, and it is a gate on *the invocation*, not on the
  run. Anything that would ask a question refuses instead. A prompt is not a slower success; it is a hang
  the automation process has no way to notice.
- **`N6 ⇢ N1` is dashed, outside, and carries no verdict.** Whether or when the automation process invokes
  again, and what it does with what this one produced, is computed from nothing this system asserts about
  fitness — there is no aggregate-verdict node anywhere on this diagram for it to read (D2.29).

---

## 6. Activity diagram — handing a corpus to an external model team

This is UC-10 with UC-11 inlined, because a corpus is audited on its way out rather than as a separate
errand. It is drawn because a swimlane is the only view that shows **where our partition stops**. The
last partition is outside this system; it is drawn to show what the recipient does with what we shipped,
and nothing in it is ours to build.

```mermaid
flowchart TB
    subgraph OP["Capture operator"]
        direction TB
        P1["Close the manifest;<br/>digest the corpus identity"]
        P2["Name the recipient and<br/>the contract version"]
        P3["Read the refusal"]
        P4["Record the handoff"]
    end

    subgraph AUD["UC-11 audit, operator with the scenario author"]
        direction TB
        A1["Surface unlabelled vehicles that<br/>look like an annotated pattern"]
        A2["Measure separability of each annotated<br/>class on sun elevation alone"]
        A3["Quantify the hour-defined instances<br/>UC-4 flagged"]
        A4["Audit report, carrying<br/>its own criteria"]
    end

    subgraph GATE["Handoff gate"]
        direction TB
        G1{"Manifest closed?"}
        G2{"Audit report present?"}
        G3{"Solar record present?"}
        G4{"One world digest, one scenario,<br/>one vocabulary version?"}
    end

    subgraph PKG["The package"]
        direction TB
        E1["Training export:<br/>nothing a fielded system<br/>could not also have"]
        E2["Full export: truth, residuals,<br/>render-set accounting"]
        E3["Contents: observability spans,<br/>prevalence in 3 units per band,<br/>band cut points, solar record,<br/>admissions and refusals"]
        E4["Omissions: not_rendered intervals,<br/>cap-bound spans, truth-only windows,<br/>no pedestrians, the kinematics caveat"]
        E5["The transfer rule and the<br/>association-quality format,<br/>as versioned contracts"]
    end

    subgraph EXT["External model team — outside this system"]
        direction TB
        X1["Runs its own detector<br/>on the imagery"]
        X2["Applies the shipped transfer rule<br/>to its own tracks, per sensor,<br/>clipping at interval bounds"]
        X3["Trains, validates, and computes<br/>whatever figures it needs"]
    end

    P1 --> G1
    G1 -->|"no: refuse"| P3
    G1 -->|"yes"| A1
    A1 --> A2 --> A3 --> A4 --> G2
    G2 -->|"no: refuse"| P3
    G2 -->|"yes"| G3
    G3 -->|"no: refuse as an imagery corpus,<br/>permit as truth-only and labelled"| P3
    G3 -->|"yes"| G4
    G4 -->|"no: refuse to pool"| P3
    G4 -->|"yes"| E1
    E1 --> E2 --> E3 --> E4 --> E5 --> P2 --> P4
    P4 ==>|"one way: corpus, statement,<br/>audit, contracts"| X1
    X1 --> X2 --> X3
```

Five things this diagram is asserting:

- **The audit is inside the flow, not beside it.** `G1` gates on the manifest and then routes straight
  into UC-11, and `G2` will not let an unaudited corpus past. An audit that is a separate errand is an
  audit that does not happen on the run that most needs it.
- **Every gate is a refusal to *hand over*, not a refusal to score.** The four conditions are the first
  draft's four refusals with their verb corrected; the conditions themselves were already right.
- **Contents and omissions are two artifacts, not two paragraphs of an email.** `E3` is what the manifest
  already carries; `E4` is the half nobody can reconstruct — `not_rendered` intervals, cap-bound spans, a
  window kept as truth-only because the imagery was not viable. A recipient without `E4` reads a hole in
  the corpus as a hole in reality.
- **The boundary is a single arrow and it points one way.** Nothing returns. There is no edge on which a
  recipient's findings about a model could arrive, which is the structural form of
  [`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3b.
- **The transfer happens in the last partition, not in ours.** `X2` applies our rule to *their* tracks.
  We ship the rule and the format at `E5` and we never run them, because running them needs a detector
  track this pipeline never sees. The one thing that makes `X2` possible is that the corpus is
  **associable** — per tick, positioned, timed, boxed — and that is a property of what we produce.

---

## 7. Decisions

**Numbering.** Decision numbers are stable and are cited by number from other sections
([01](01_Architecture.md) cites D2.10 twice; [12](12_Operator_Control_Surface.md) cites D2.2 and D2.6). A
decision that needs correcting is rewritten in place, keeping its number and its subject, never
renumbered.

| # | Decision |
|---|---|
| D2.1 | **The authoring assistant is a first-class actor, not a convenience.** Every convention it relies on is a published, machine-readable artifact of a build, and every unresolved reference is an error rather than a nearest match. Hand authoring stays possible against the same artifacts (UC-2, UC-3) |
| D2.2 | **Authoring needs no CARLA server.** UC-3, UC-4, UC-5 and UC-6 run against the `AuthoringReferenceSet` and the world package alone, so the part of the workflow a human iterates on does not contend for a GPU. UC-12 extends this: a run configuration can be *composed* offline and can only be *launched* against a server (UC-5 alternate flow, UC-12 alternate flow) |
| D2.3 | **Route validity is proved with `duarouter`, inside the authoring loop.** A graph walk gives false positives that surface at SUMO load, which is the wrong place to find them (UC-3, §4) |
| D2.4 | **A dry run is part of authoring, not part of capture.** A population that climbs rather than settles is a demand defect and is found before a server is involved (§4) |
| D2.5 | **The author accepts a resolution report, not a file.** The compile step reports what every name bound to — and what every window and interval means in civil time — because a preview cannot check an annotation and a silent nearest match is the failure mode that costs the most later (UC-5 step 13) |
| D2.6 | **Population authority is acquired at session start, and a denial fails the session naming the holder.** It is the first thing that happens in UC-7, and there is no path that proceeds past it with a warning (UC-7, §5) |
| D2.7 | **One capture session, one identity.** The session assigns the run identity, the scenario id and a stable `sensor_id` per camera, replacing the recorder's own wall-clock default and closing the never-supplied `scenario_id` gap at its current location (UC-7 step 5) |
| D2.8 | **The corpus publishes the denominator; it never applies it.** Observability is accounted as observed intervals, gated first on rendered spans, per sensor and unioned. An annotated interval whose participant was never instantiated is not something anyone may count against a model, and **only the manifest can say which those were** — which is why we compute and publish it and why an external consumer could not. What is divided by it happens outside this system (UC-10 steps 4 and 5) |
| D2.9 | **Nothing on the far side of the boundary is ever given truth, in any mode.** Live (UC-8) and by export (UC-10) alike, what leaves carries no truth-sourced field, and that is a structural property — two artifact roots with one writer each, two separate artifacts rather than two views of one ([06 §10.3](06_Truth_And_Annotation.md), [08](08_Collection_And_EPoL.md) D8.17) — not a configuration to get right. In a live exercise truth additionally rides its own endpoint and is off by default, because truth on a feed an exercised operator sees is a leak ([08](08_Collection_And_EPoL.md) D8.23) |
| D2.10 | **A corpus without a closed manifest is not handed over and not replayable.** Both UC-9 and UC-10 refuse it rather than degrading, because supervision in interval form is the only thing a detector track can be clipped against — and the recipient is the one who will do the clipping (UC-9, UC-10) |
| D2.11 | **Auditing for accidental positives is a required use case, not an optional one**, because this system removes the mechanism that was suppressing them. The realism gain and the audit ship together. It is **our** audit — the capture operator with the scenario author, never an external consumer — it runs before a corpus leaves, and its report ships with the corpus (UC-11, UC-10 step 2, §1.3) |
| D2.12 | **A live exercise degrades visibly rather than silently slowing the world.** An observer who cannot tell that the pipeline is behind is being shown something other than what they think. The operative word is **silently**. A slower world is legitimate — it costs no truth, because truth is stamped in simulated time — **when somebody declared the band it may slow within, and the slip is displayed and recorded**; an undeclared stall never is, because nothing distinguishes it afterwards from a fast run. The visible degradations are the achieved factor against target and floor, and, at the floor, **dropped frames counted per channel and recorded in coverage**. D2.27 and [08 §11.3](08_Collection_And_EPoL.md) D8.40 are the full ruling (UC-8 §8a) |
| D2.13 | **The scenario author owns the epoch; the capture operator owns the illumination policy.** The epoch — civil date, civil UTC offset, the civil instant `t = 0` means — is scenario-scoped and a required part of the scenario contract. Freeze-or-advance, the rate, and any override are run-scoped. Neither actor can perform the other's part: the operator cannot invent what a scenario's hours mean, and the author cannot know the sweep (§1.1) |
| D2.14 | **A window's civil time is derived, never chosen.** An operator who wants a different light records an **override**, which marks the corpus. The difference between a derived time and an override is a fact a later reader needs, and a silently different time is indistinguishable from a bug (§1.1, UC-7 alternate flow) |
| D2.15 | **The sun is positioned explicitly at every session start and never inherited.** The noon default is applied only when no `CesiumSunSky` exists (`CesiumHeightSampler.cpp:396-402`), so a loaded world keeps the previous session's sun. The date is set as well as the time, because the advancing controller never touches the date (`CesiumTimeOfDayController.cpp:34-35`) (§1.2, UC-7 step 4, UC-9 step 3) |
| D2.16 | **A run that cannot derive, set or verify its illumination fails rather than captures.** No epoch, a solar authority that refuses, or a readback that disagrees each stop the session. The alternative is a corpus whose imagery and truth disagree while both are well-formed, which nothing downstream detects (UC-7 failure flows) |
| D2.17 | **Illumination is an appearance axis and a confounding one.** A sweep that varies behaviour holds illumination constant *and frozen*; a sweep that varies both marks every entry with its illumination stratum or is refused. A counterfactual pair whose arms have different suns is worth nothing (UC-6 §6a) |
| D2.18 | **A replay reproduces the original illumination by default; departing from it is an explicit, recorded override.** A manifest with no solar record is replayable for review and not for capture, because the new corpus could not state its relationship to the old one (UC-9) |
| D2.19 | **Illumination stratification is corpus metadata, produced here and conditioned on elsewhere.** Observability, prevalence in all three units, and the band cut points are published **per illumination band** as well as unioned, and a pooled aggregate that hides the bands is refused at handover. A corpus spanning bands is two populations, and anything pooled over it describes neither ([06 §5.3](06_Truth_And_Annotation.md)). The pipeline stratifies its own data; it does not stratify anybody's results (UC-10 step 4) |
| D2.20 | **The audit tests whether the annotated class is separable by illumination alone, and records the result with the corpus.** In a pattern of life the label correlates with the hour by construction, so this is the default state and not an exceptional one. The remedy is a counter-illumination capture, never an edit to the labels (UC-11 §11a) |
| D2.21 | **Illumination is derived context and never a label.** No annotation may name a light state (UC-4 failure flow), and no audit output or stratum may be written into supervision (UC-11 failure flow). It is a legitimate covariate and a legitimate input to a fielded system that knows the time and its location; it is never a supervision signal (brief §3a, standing constraint) |
| D2.22 | **Every field of a run configuration has an explicit value, composed and validated in one place.** There is no field that falls back to "whatever the world happened to be in". A scripted launch composes and validates the same configuration through the same path; the requirement is that the step cannot be bypassed, not that a human performs it (UC-12) |
| D2.23 | **This pipeline labels; it never scores.** No component runs a detector, a tracker or an EPoL model in order to measure one, and no artifact it produces is a model metric, a baseline, a comparison or a verdict. Where a model appears it is a **live consumer being fed** (UC-8) or an **instrument used on our own data** (UC-11), and in neither role is it the subject. The positive form of the rule is the one to build to: **compute and publish everything a score would need; compute no score** ([`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3b) |
| D2.24 | **A live exercise feeds and records; it does not judge.** The session streams collection frames out, records what comes back verbatim and tick-stamped, and holds truth on a separate channel for a human observer. It computes no agreement between the two — no association, no residual, no count, no verdict — and the transcript is received data with its own provenance, never merged into truth or supervision. This is stated as a decision because both streams are already in one process on one tick base, and the join is a few lines away (UC-8) |
| D2.25 | **The external model team is an actor outside the boundary, associated with exactly one use case.** "Model trainer" and "model evaluator" are not actors of this system; both are represented by one external actor who receives a corpus at UC-10 and interacts with the pipeline in no other way. Their requirements on the corpus did not disappear with them: each became an obligation on the handoff and on the manifest — the strata, the denominator, and the statement of what is missing (§1.3, D2.8, D2.19) |
| D2.26 | **The live exercise is a primary use case, and it is generic past our boundary.** UC-8 specifies **what we emit and the guarantees on it** — one self-describing collection frame per capture, the tick as the only join key, channels frame-coherent, nothing derived from the scene — and specifies nothing about what consumes it. The far side is **one actor**, the attached exploitation chain, whose composition is unspecified; the detector-then-model-service picture is *one illustrative adapter* and is marked as such wherever it appears. The falsifiable form of the rule is the test to build to: **substitute an entirely different detector and an entirely different model service, and no step of UC-8 may change** ([`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3c, §1.4, UC-8) |
| D2.27 | **The pacing band is declared and session-fixed; within it, only our own queue depth may move the clock; at its floor the stream drops; the world never stalls.** This section adopts [08 §11.3](08_Collection_And_EPoL.md) **D8.40**, which owns the mechanism, and states the actor-facing half: a run declares a **target** and a **floor** factor, both recorded; slowing between them costs no truth, because truth is stamped in simulated time and the sun advances on the world tick; at the floor, drop-oldest with a per-sensor counter marks the tick *covered but not delivered* ([08](08_Collection_And_EPoL.md) D8.22). **Nothing a consumer sends is ever an input to our clock** — an acknowledgement, heartbeat or advertised rate reaching the clock owner would make an external project's latency an input to our simulation and end the run's reproducibility. The operator sees the achieved factor against target and floor, the per-channel drop count, what is attached and since when, and — under an advancing sun — the sun itself falling behind (UC-8 §8a) |
| D2.28 | **The attached exploitation chain and the external model team are distinct actors, and the discriminator is control flow, not identity.** The chain's state is an input to ours — it can be unreachable, it can be slow, and what it returns becomes an artifact we hold — while the model team's never is, because every obligation to it is discharged before it appears. That the same organisation may occupy both roles is irrelevant: an actor is a role, and this section already separates the scenario author from the capture operator on exactly that basis. The same test makes the **automation process** distinct from the capture operator: it cannot answer a question (§1.4) |
| D2.29 | **A session can be started, observed and stopped by an external process, and what we owe it is a record, never a verdict.** A run can be started by something that is not a person, with a fully explicit parameter set, through the same composition and validation a human uses. While it runs, the caller may observe it through CarlaNet and the Python shim and through the manifest growing on disk, and it ends the run whenever it decides it has seen enough — up to and including killing the server or the client — which is the expected path, not a failure. A path that runs a closing step writes one record of what each quality check observed, with the process exit code agreeing where the invoking process is still alive to set one; a path that ends in a hard kill writes no closing record at all, and an unclosed manifest is read as **stopped**, honestly, rather than as damaged. Anything that would prompt refuses instead. The cadence between invocations, the decision that a run has produced enough, retention, and whatever trains on the output are all outside, and no aggregate judgement of fitness is ours to make (UC-13, §5b, brief §3d) |
| D2.30 | **A repeated invocation produces a new corpus; it never edits an old one.** Each invocation has its own session and corpus identity and shares the scenario and world digests, so repeated invocations are joinable and distinguishable. Nothing is appended to, merged into or rewritten in a closed corpus (D2.10), and an invocation that would overwrite its predecessor refuses to open a manifest. Two invocations of one parameter set are two populations, not one growing one (UC-13 alternate flows) |

## 8. Open questions

1. **Can a replayed capture carry speed?** UC-9 replays bodies the replayer moves, with no SUMO session
   running, so [01 §4.3](01_Architecture.md)'s kinematic compensation is unavailable and the replayed
   truth would report the zero velocity of `WorldObserver.cpp:373` again. Options: carry the original
   run's kinematics in the manifest and re-attach them by `entity_id` and normalised tick; or accept that
   a replayed corpus has position and no speed and say so in its manifest. Recommend the first — the data
   already exists and the join already exists for supervision — but it needs
   [06](06_Truth_And_Annotation.md) to decide where it rides.
2. **Who chooses the capture window — the author or the operator?** Both have a legitimate claim: an
   annotated instance implies a window, and an operator may want an arbitrary hour of ordinary life.
   Recommend the scenario declaring named windows as presets with the operator free to give another, and
   the manifest recording which was used. **This is now the same question as illumination ownership, and
   §1.1 answers it in that shape** — the scenario declares, the operator selects, the manifest records.
   Recommend answering both identically and at once; it is also [01 open question
   3](01_Architecture.md), and all three should be settled together.
3. **Is there a use case for capturing a window with no annotations at all?** A corpus of purely ordinary
   movement is what an EPoL model most needs, and nothing above requires an `AnnotationSet` to be
   non-empty. If that is a first-class case it should be named, because it changes what UC-5 can insist on
   and what the corpus's published prevalence means when the numerator is zero — a real case, and one the
   manifest must state rather than omit ([06 §5.3](06_Truth_And_Annotation.md) already requires
   empty-numerator rows to be kept). It interacts with UC-11 §11a: an
   unannotated corpus is also the cleanest available control for an illumination-leakage test, because it
   has no labels for illumination to correlate with.
4. **How does an author preview a SUMO scenario?** [18 §8.3](../../Findings/18_Scenario_Fabrication_For_EPoL_Training.md)'s
   graphical canvas previews OpenSCENARIO storyboards, not SUMO demand. `sumo-gui` is the obvious
   candidate and is already built ([23 §1.2](../../Findings/23_SUMO_Traffic_Integration.md)), but it
   previews the microsimulation rather than the imagery. Whether that is enough, or whether a preview
   against the world's own geometry is wanted, is unsettled and affects how much UC-3's dry run has to
   carry. A preview that showed the *light* as well as the demand would answer part of UC-12 step 4, but
   `sumo-gui` cannot; a single rendered probe frame from the server could.
5. **What does the capture operator see while a capture runs?** Largely answered by UC-12 step 8, which
   makes the live health display part of the contract rather than an afterthought. What remains is which
   figures it shows and at what cost to the tick thread — that is
   [10](10_Scale_And_Performance.md)'s instrumentation and [12](12_Operator_Control_Surface.md)'s
   presentation, and the note here exists only so neither assumes the other owns it. The solar readback is
   the one figure already known to be free (`carlanet/__init__.py:1511-1533`).
6. **Is UC-6's counterfactual pairing worth building?** It is the strongest **control** a corpus can carry
   — the same seed, the same ambient population, one instance's behaviour switched off — and it is nearly
   free here, but it doubles the run list and its value depends on a modelling question outside this plan. Carried forward unresolved from
   [20 open question 7](../../Findings/20_Behavioral_Annotation_And_Areas_Of_Interest.md). Note that
   §6a adds a hard precondition to it: both arms must share a frozen sun, or the pairing measures two
   things at once.
7. **The civil time zone is not settable, and for the sizing scenario it is wrong by ~15 minutes.**
   The engine derives `TimeZone = longitude / 15` once, at sun spawn
   (`CesiumHeightSampler.cpp:411-412`), and **no setter exists anywhere on the surface** — measured
   2026-09-18 by searching the whole `CesiumCarlaBridge` plugin, `CarlaNet/src` and
   `CarlaNet/python` for `TimeZone`: the only hits are that spawn call, its comment, and the read-only
   value `GetSolarState` packs at `:779`. For the sizing scenario this is a standing error of ≈ 14.7
   minutes (UC-5 step 5, measured). Options: **(a)** convert civil time into the engine's
   solar-longitude frame inside the solar clock binding, and accept that the sidecar's `time_zone` field
   reports a longitude zone rather than the civil one; **(b)** add a `set_solar_time_zone` RPC so the
   engine holds the scenario's civil offset and `solar_time` means civil time throughout. **Recommend
   (b).** It makes the sidecar self-describing, it removes a conversion that every consumer would
   otherwise have to know about, and it is the only option under which a half-hour civil offset like
   Iran's +03:30 is representable as itself. Rebuilds are neutral. The decision is
   [11](11_Time_And_Illumination.md)'s.
8. **What does `rate` mean, exactly, under synchronous ticking?** The controller advances by
   `DeltaSeconds × Rate` on each world tick (`CesiumTimeOfDayController.cpp:34`) and both the header
   comment and `set_time_advance`'s docstring (`carlanet/__init__.py:1535-1541`) say it tracks simulation
   time under synchronous ticking. The property this section needs guaranteed is narrow and testable:
   **at rate 1.0, a window of *N* simulated seconds must move the sun by exactly *N* seconds, with no
   dependence on frame rate or on how long the run took in wall clock.** [11](11_Time_And_Illumination.md)
   should state it and say how it was verified. A second part is unresolved by reading alone: whether the
   controller's `DeltaSeconds` is the world's fixed delta under CARLA's synchronous cue, or something
   else — that is a measurement, not an argument.
9. **What happens to the date when an advancing window crosses midnight?** The controller wraps the solar
   clock modulo 24 h and never touches the calendar (`CesiumTimeOfDayController.cpp:34-35`), so a window
   spanning midnight ends on the date it began, and the sidecar records that date
   (`CotUdpEmitter.py:160`). Options: roll the date in the controller; have the solar clock binding detect
   the crossing and re-set the date; or forbid a single window from crossing midnight. Recommend the
   first — it is the only one that keeps the recorded date correct without a client in the loop every
   tick — but it is [11](11_Time_And_Illumination.md)'s to decide, and the third is a real option because
   a window crossing midnight is rare and splitting it costs nothing.
10. **Should a night capture drive vehicle lights?** The mechanism exists and is batchable:
    `Actor.set_light_state` / `get_light_state` (`carlanet/__init__.py:781`, `:786`) over
    `VehicleLightStateFlags`, with `SetVehicleLightStateCommand` among the batch commands (imported at
    `:487`, used at `:1147`), and SUMO exposes per-vehicle brake and indicator signals, so the state could
    ride the same batch as the pose at no extra round trip (brief §3a). The question is whether it
    *should*: at night it is the difference between a believable corpus and one in which vehicles are
    nearly invisible, but it adds a per-vehicle field to the write path that [10](10_Scale_And_Performance.md)
    budgets. Recommend yes for brake and indicator state, sourced from SUMO's own signal bitmask and
    batched with the pose; the decision is [03](03_CoSimulation_Runtime.md)'s and
    [10](10_Scale_And_Performance.md)'s jointly. Note it is a *rendering* question and not a supervision
    one — a lit brake light is derived context exactly as illumination is (D2.21).
11. **Should the camera configuration differ per illumination stratum?** A fixed exposure across a sweep
    under-exposes the night arm; an exposure adapted per stratum means the sensor model is not constant
    across the corpus, which is its own confound. The viewer already exposes `--ev`
    (`CarlaControlArgumentParser.py:238-242`), so the control exists. This is
    [08](08_Collection_And_EPoL.md)'s to decide; whichever way, the choice must be in the manifest, because
    a consumer conditioning on the published illumination strata (D2.19) needs to know whether the sensor
    changed too.

12. **What does a corpus handoff actually ship as?** UC-10 now exists and nothing in this folder names the
    package. Three parts are open. **(a) The container:** a directory tree whose manifest names relative
    paths, an archive with a digest over its contents, or a manifest plus a content-addressed store.
    Recommend the **directory tree with a digest manifest**, because it is what a world package already
    does (`WorldPackage` writes `world.json` beside its files) and because a corpus at sizing scale is
    large enough that re-archiving it to add an audit report would be the operation nobody performs.
    **(b) The transfer rule's form:** prose in a contract document, or a versioned machine-readable
    specification the recipient's tooling implements. Recommend the **versioned specification**, because
    the rule has five clauses that each change a number (association by position and time, per-sensor
    transfer, one-to-many, clipping at bounds, quality per assignment) and a prose clause that is
    implemented slightly differently is exactly the silent divergence the vocabulary-version refusal
    exists to prevent elsewhere. **(c) Whether the handoff record is a contract or a courtesy** — that is,
    whether a second recipient may be given a corpus that has changed since the first handoff, or whether
    corpus identity is frozen at first handover. Recommend **frozen**, consistent with D2.10 and with
    UC-12's immutable run configuration. [04](04_Contracts.md) owns (a) and (b);
    [09](09_Toolchain_And_Packaging.md) owns how it is built and staged.

13. **In a live exercise, is a dropped frame an interruption or a column?**
    [12](12_Operator_Control_Surface.md) D12.15 makes a non-zero `FrameRecorder.Dropped` on any channel
    one of exactly three conditions that interrupt the operator, and everything else a column. For a
    captured collection that is right: a thin corpus that looks normal is the failure it exists to catch.
    For a live exercise it is inverted — drops are the **expected** degradation when a consumer is slower
    than the world (UC-8 §8a), and an interrupt per drop is noise an operator learns to dismiss, which
    destroys the interrupt's value for the other two conditions as well. Options: **(a)** keep D12.15 as
    written and accept the noise; **(b)** make drops a column in the live mode and an interrupt in the
    capture mode, which is a mode-dependent rule and therefore something to be suspicious of; **(c)**
    interrupt **once**, on the first drop of a session, and carry a persistent counter thereafter.
    **Recommend (c)**: the first drop is the news, the hundredth is a statistic, and it is the same shape
    as the drop policy itself — record every one, announce the transition. This is
    [12](12_Operator_Control_Surface.md)'s to settle, and it should be settled with
    [12 open question 7](12_Operator_Control_Surface.md), which asks where the monitor lives.

14. **Where does a received transcript live, given there are exactly two artifact roots?**
    **Raised here and answered concurrently by [08 §11.6](08_Collection_And_EPoL.md); recorded rather than
    left open, because the reasoning is worth keeping and because the two sections reached it
    independently.** UC-8 step 7 may keep what an attached chain pushes back, as an opaque blob. But
    [08](08_Collection_And_EPoL.md) D8.17 fixes **two** roots — `OBSERVATION` and `TRUTH`, one writer
    each, non-nested — and a third, `roots.score`, was deliberately removed by the integration lead on
    2026-09-18 ([04](04_Contracts.md) D4.26). A transcript is neither: it is not our observation, and it
    is certainly not truth. The options were **(a)** a third root, which resurrects exactly what was
    removed and would invite everything else that was removed with it; **(b)** inside `OBSERVATION`, which
    is wrong in the most dangerous direction — it would put a model's output where a model's *input* is
    drawn from, and one careless glob later a chain would be reading its own previous opinions; and
    **(c)** a received-data artifact **outside both roots**. This section recommended (c);
    [08 §11.6](08_Collection_And_EPoL.md) decided (c), on the same grounds and with the same placement as
    its probe workspace. **What remains open is only the container's form**, which is
    [04](04_Contracts.md)'s. What this section asserts either way is unchanged: a transcript is **not**
    truth, **not** supervision, and **not** an observation.

15. **Does unattended invocation need its own entry point, or a flag on the operator surface?**
    UC-13 requires a run that can be started by something that is not a person, stopped by that same
    process at any instant of its own choosing, and left with a machine-readable record whenever a
    closing step gets the chance to run — and [12 §3.8](12_Operator_Control_Surface.md)'s `run_capture`
    line, [12](12_Operator_Control_Surface.md) D12.3's effective configuration and
    [12 §7.2](12_Operator_Control_Surface.md)'s closeout report already provide most of it. Options:
    **(a)** one entry point, with the record written whenever a closing step runs, whether or not anyone
    is watching; **(b)** a separate non-interactive entry point, which duplicates the composition path and
    is exactly what
    [12](12_Operator_Control_Surface.md) D12.17 and §9.3 warn against — *measured*, three `getattr`
    fallbacks already disagree with the parser because a second caller exists.
    **Recommend (a).** The result record is cheap, it is useful to a human too, and a second front end over
    the same shared definitions is how defaults diverge. This is closely tied to
    [12 open question 1](12_Operator_Control_Surface.md) — whether a run-list runner exists at all — and
    the two should be answered together: a run-list runner **is** an automation process wearing our own
    clothes, and if it exists, UC-13's requirements fall out of it rather than needing anything new.
