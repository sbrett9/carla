# Project conventions

## No conversational jargon in committed code

Code comments, docstrings, identifiers, and any text shipped in the repo must be **self-explanatory
to a reader who has no access to the conversation (or chat with an AI assistant) that produced them.**

**Never commit transient labels that only make sense in a live discussion**, e.g.:
`Option A` / `Option B` / `Option C`, `Phase 2b` (or any `Phase N` / `fork B` / `step N` plan label),
`the approach we discussed`, `the new way`, `as agreed`, `TODO per chat`, ticket-less `(see above)`.
These mean nothing to the next reader (human or AI) six months later.

Instead, **describe the actual concept**: what it does and why. Examples:
- ❌ `// Option A: shift the ground layer` → ✅ `// Shift the collidable ground by the height-align offset so it lines up with the road`
- ❌ `"""Phase 2b per-cell offset lookup"""` → ✅ `"""Per-cell drape offset lookup (the 'drape' height-align mode)"""`
- ❌ `# fork B heightfield path` → ✅ `# Chaos heightfield collision path`

**This is NOT a ban on domain terms or stable references.** Keep things that are meaningful on their
own: real algorithm step numbers from a cited source, protocol/standard names, a traffic-light
`Phase 2`, `RoadOption`, persistent doc names (e.g. `Docs/CAT_Research/Findings/08_*.md`), issue IDs.
The test is simply: *would a new reader understand this without the originating conversation?* If not,
reword it before committing.

Applies to user-facing text too (CLI `--help`, printed output): prefer plain language over internal
shorthand (`DSM`/`DTM`/`GSD`/`hae` etc. — spell out or briefly gloss on first use in help text).

## Commit messages: objective, self-contained

A commit message must make sense to someone reading the history later with **no access to the
conversation that produced it**. State *what changed and why*, factually.

- **No conversational transients:** `as discussed`, `per our chat`, `as agreed`, `as requested`,
  `like we talked about`, `finally`, `oops`.
- **No live-plan labels:** `Option A/B`, `Phase 2b`, `step N`, `fork B`, `the new approach`. Name the
  actual change instead (e.g. `Add per-point draped heightfield collision` not `Phase 2b step 4`).
- Keep it concrete: imperative subject ≤ ~72 chars describing the change; body explains the reasoning,
  trade-offs, and any validation. Domain terms and issue IDs are fine.

(Forward-only — existing history is grandfathered, don't rewrite it.)

@AGENTS.md