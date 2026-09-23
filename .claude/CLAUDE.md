# Project rules (carla)

## Never say something "landed"

Do not use **"landed"**, **"lands"**, **"landing"**, **"has landed"** or any variant of that figure of
speech to mean that a change, commit, merge, push, file, message, request, frame, fix or feature has
arrived, taken effect, been applied, been merged, or been written. This is a permanent rule with no
exceptions.

It applies to everything Claude writes in this repository: replies to the user, commit messages,
pull request descriptions, code comments, docstrings, documentation, help text, log messages.

Say what actually happened, with a plain word:

- **completed**, **finished**, **done**
- **in place**, **is now in**, **is now on `<branch>`**
- **merged**, **committed**, **pushed**, **applied**, **written**, **installed**
- **arrived**, **delivered**, **took effect**, **came back**, **was served**

Examples:

- ❌ "the fix has landed on ue5-dev" → ✅ "the fix is merged into ue5-dev"
- ❌ "once the frame lands" → ✅ "once the frame arrives"
- ❌ "the RPC lands in the next slice" → ✅ "the RPC is served in the next slice"
- ❌ "verify the push landed" → ✅ "verify the push completed"

**Why:** the phrasing is a recognisable AI-generation signature and it makes the text markedly
harder to read. The user has asked for it to stop, permanently.

This sits alongside the "no conversational jargon in committed code" rule in the repository's root
`CLAUDE.md`: both exist so that what gets written reads as plain, self-contained prose.
