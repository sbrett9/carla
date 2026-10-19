# Rules for the pages in this folder

These rules are for an assistant that writes or changes a page here.\
A person follows them too.\
Every page describes one kind of file the capture tools write or read.\
Every page has the same shape.\
`Scripts/Docs/check_schema_pages.py` names every departure from it.\
The test `CarlaControl/test/test_schema_pages.py` holds the folder to it.\
The prose rules are in `.claude/CLAUDE.md` at the repository root.\
They apply to every sentence here.

## The header table

The page opens with its title.\
Then comes a two-column table with exactly these rows, in this order.

| Row | What goes in it |
|---|---|
| File | What the file is and where a run writes it: its name pattern and its folder. |
| Schema | The schema's path in a checkout, `CarlaControl/schemas/<kind>.schema.json`, `.xsd` or `.tableschema.json`, with the schema language in parentheses where the page states it. `none` for a kind with no schema, then what stands in for one. |
| Schema id | The URN, `urn:carla-sumo-capture:schema:<kind>:<version>`, or `none`. |
| Format version | The number alone. `none` only where Schema is `none`. |
| Version field | The field that carries the version, in backticks, with where it sits after a comma when that helps. For a kind whose file carries no version field, `none`, then why: a reader takes such a file as version 1. |

## The sections

The sections are level-two headings, in this order.

1. `Description`.\
   What the file is for and what it holds.\
   On every page.
2. `Writers and readers`.\
   Which tool writes the file, when it writes it and which tools read it.\
   Leave it out only when Description already says all of it.
3. The page's own sections, on topics only this kind has.\
   Name each for its topic.\
   They sit here and nowhere else.
4. `Fields`.\
   Every field, column, attribute or entry, with its type, its unit, whether it is required and what it means.\
   Divide groups with level-three headings.\
   On every page.
5. `Example`.\
   One real excerpt from a shipped file, shortened if it must be.\
   On every page.
6. `Checks`.\
   How to check a file against the schema, usually one `carla-validate` command.\
   Leave it out when nothing checks the kind.
7. `Versions`.\
   The page's own history: `1` and what it is, then each later version and what changed.\
   The README states the rule every reader follows.\
   Do not repeat it here.\
   On every page.
8. `Related pages`.\
   Optional.

Everything that describes the file's content goes under Fields, never in a section of its own.

## Writing a page

- Read the schema under `CarlaControl/schemas/` and the code that writes the file before you write a sentence.\
  Never invent a field, a default or a version.\
  A fact you cannot find is a gap to report.
- Write one sentence per line.\
  Every line of a paragraph except its last ends in a backslash.\
  No first person, no bold, no em dash, no clause tacked on after a comma with the word and.
- Never rename a heading without updating every link to it.\
  `git grep -n '#<old-anchor>'` finds the links.\
  The anchor is the old heading in lower case, with hyphens for spaces and the punctuation dropped.
- Before you finish, run the checker on the page:

  ```
  python Scripts/Docs/check_schema_pages.py Docs/CAT_Research/Schemas/<Page>.md
  ```

- A new page needs a row in `README.md`.
- The skill `schema-page`, under `.claude/skills/`, holds the workflow and a filled page to copy.

This file and `CLAUDE.md` beside it are for assistants.\
The distribution does not ship them.
