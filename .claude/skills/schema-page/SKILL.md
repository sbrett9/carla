---
name: schema-page
description: The page template for the reference pages under Docs/CAT_Research/Schemas, which describe one kind of file the capture tools write or read (its schema, fields, example and versions), with the checker that holds a page to it. Use this skill whenever you write, add, restructure, review or copy-edit any page in that folder, whenever a schema file under CarlaControl/schemas or a file format changes and its page must follow, whenever a new kind of output file needs a page, and whenever the user asks about the schema pages' layout, headings, header table, consistency or template, even if they do not say "template".
---

# Schema page

Every page under `Docs/CAT_Research/Schemas` describes one kind of file: what writes it, what reads
it, every field, and its format version. Forty-one such pages exist. A reader who has learnt one
must find the same things in the same places on the next, which is what this template is for.
The sentence rules are separate and already enforced (`.claude/writing_rules.md` in the workspace
root, with `scratchpad/copyedit/check_layout.py`); this skill is about the shape of the page.

## The template

The contract is `Docs/CAT_Research/Schemas/AGENTS.md`: the five header rows, the section order,
the two forms of the version cell and the writing rules. Claude Code loads it through the
`CLAUDE.md` beside it whenever a page in that folder is touched; read it first all the same, so
the rules are in front of you before the page is. `references/template.md` is a filled page that
follows the contract. Copy that, rather than describing a page from the rules.

## Why these names

The older pages opened with "What it is" and "Who writes it and who reads it" and stated the
version as "1, in `epoch_version`", a bold lead or a section of its own. The folder is read by
engineers who expect a reference manual, where a heading is a short noun and a fact is in a table
cell of its own. `Description` and `Writers and readers` are such nouns; the version number and
the field that carries it are two facts, so they are two rows.

The checker names the template's heading for each older one, so renaming is mechanical. A rename
moves an anchor: every link into the renamed heading from another page must be updated in the same
change, which `scratchpad/copyedit/check_technical.py` lists.

## Writing a page

1. Read the schema the page describes, under `CarlaControl/schemas/`, and the code that writes the
   file. The schema says what the writer writes; the page says what it means. Never invent a field,
   a default or a version. A fact you cannot find is a gap to report, not a sentence to write.
2. Copy `references/template.md` and fill it, in the sentence style of the writing rules: one
   sentence per line, each line of a paragraph except its last ending in a backslash, no first
   person, no bold, no tacked-on clauses.
3. Run the checker on the page:

   ```
   python Scripts/Docs/check_schema_pages.py Docs/CAT_Research/Schemas/<Page>.md
   ```

   It prints every departure with the line and the template's name for the heading. Exit 0 means
   the page fits. The test `CarlaControl/test/test_schema_pages.py` runs it over the folder, so a
   page that departs fails the build.
4. Run the sentence checks, `check_layout.py --base <commit>` and `check_technical.py <commit>`,
   then the watermark remover, as the writing rules ask.
5. If the page is new, add its row to the folder's `README.md`.

## When a format changes

A shape change to a file bumps its format version. Then: change the schema, bump the number in the
Format version row, add the version to `Versions` with what changed, update `Fields` and the
`Example`, and run the checker. The README's rule means an old reader refuses the new file, so the
Versions entry is what tells a reader what to upgrade.
