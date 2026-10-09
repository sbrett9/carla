"""Point the links in a distribution's copied documentation at what the bundle holds.

The pages are written for the repository, where they live in Docs/CAT_Research/. A link that leaves
that folder names a file by its place in the repository, and in a bundle that place does not exist:
docs/ holds the pages, and the other repository folders are shipped under other names or not at all.
Both distribution scripts (Scripts/Windows/MakeDistribution.ps1, Scripts/Linux/MakeDistribution.sh)
run this once the pages and the skills are staged, so both bundles get the same bytes.

For every inline link, [text](target) or ![text](target), whose target leaves the documentation:

  * a target in a repository folder the bundle ships under another name (SHIPPED_AS below), and
    present in the bundle, gets the bundle-relative path to that copy, its #fragment kept;
  * a target in such a folder that is absent from the bundle, because the scripts leave it out
    (CarlaControl/skills/third-party/), becomes its link text, with the link dropped;
  * every other link is left as it is.

Whether a target ships is read from the staged bundle, with the exact case of every name, so the
answer is the same on Windows and Linux and cannot drift from what the scripts copy. Fenced code
blocks and code spans are never touched. A page is rewritten only where a link changes, byte for byte
everywhere else, and every change is printed.

Usage: python rewrite_doc_links.py <the bundle's docs folder>
"""
import os
import posixpath
import re
import sys

# Where the pages live in the repository, relative to its root.
REPOSITORY_DOCS = "Docs/CAT_Research"

# Repository folders the bundle ships under another name: (repository path, bundle path).
SHIPPED_AS = [("CarlaControl/skills", "skills")]

FENCE_OPEN = re.compile(r"^[ \t]*(`{3,}|~{3,})")
LINK = re.compile(r'(!?)\[((?:[^\[\]\n]|\[[^\[\]\n]*\])*)\]\(([^()\s]+)((?:[ \t]+"[^"\n]*")?)\)')
SCHEME = re.compile(r"^[A-Za-z][A-Za-z0-9+.-]*:")


def exists_exactly(root, relative):
    """True when root/relative exists, matching the case of every name on any file system."""
    here = root
    for name in relative.split("/"):
        try:
            if name not in os.listdir(here):
                return False
        except OSError:
            return False
        here = os.path.join(here, name)
    return True


def new_link(match, page_repository_dir, page_bundle_dir, bundle_root):
    """The replacement for one link, and a note of the change (None when the link stays)."""
    bang, text, target, title = match.groups()
    if target.startswith(("#", "/")) or SCHEME.match(target):
        return match.group(0), None
    cut = min((i for i in (target.find("#"), target.find("?")) if i >= 0), default=len(target))
    path, suffix = target[:cut], target[cut:]
    if not path:
        return match.group(0), None
    resolved = posixpath.normpath(posixpath.join(page_repository_dir, path))
    if resolved == ".." or resolved.startswith("../"):
        return match.group(0), None                      # outside the repository
    if resolved == REPOSITORY_DOCS or resolved.startswith(REPOSITORY_DOCS + "/"):
        return match.group(0), None                      # stays in the documentation
    for repository_path, bundle_path in SHIPPED_AS:
        if resolved == repository_path or resolved.startswith(repository_path + "/"):
            in_bundle = bundle_path + resolved[len(repository_path):]
            if exists_exactly(bundle_root, in_bundle):
                relative = posixpath.relpath(in_bundle, page_bundle_dir) + suffix
                return f"{bang}[{text}]({relative}{title})", f"{target} -> {relative}"
            return text, f"{target} does not ship; the link is dropped and its text kept"
    return match.group(0), None


def rewrite_line(line, rewrite):
    """The line with every link outside a code span passed through rewrite."""
    out, i, n = [], 0, len(line)
    while i < n:
        c = line[i]
        if c == "`":
            j = i
            while j < n and line[j] == "`":
                j += 1
            run, close, k = line[i:j], -1, j
            while True:                                   # a closing run of the same length
                k = line.find(run, k)
                if k < 0:
                    break
                end = k + len(run)
                if line[k - 1] != "`" and (end >= n or line[end] != "`"):
                    close = end
                    break
                while k < n and line[k] == "`":
                    k += 1
            if close < 0:                                 # no closing run: plain backticks
                out.append(run)
                i = j
            else:
                out.append(line[i:close])
                i = close
            continue
        if c == "[" or (c == "!" and line.startswith("[", i + 1)):
            match = LINK.match(line, i)
            if match:
                out.append(rewrite(match))
                i = match.end()
                continue
        out.append(c)
        i += 1
    return "".join(out)


def rewrite_page(path, relative_page, docs_name, bundle_root):
    """Rewrite one page in place; returns the notes of what changed."""
    with open(path, "rb") as f:
        original = f.read().decode("utf-8")
    page_dir = posixpath.dirname(relative_page)
    page_repository_dir = posixpath.join(REPOSITORY_DOCS, page_dir)
    page_bundle_dir = posixpath.join(docs_name, page_dir)
    notes, lines, fence = [], original.split("\n"), None
    for number, line in enumerate(lines, 1):
        if fence:
            if re.match(r"^[ \t]*" + re.escape(fence[0]) + "{" + str(len(fence)) + r",}[ \t]*\r?$", line):
                fence = None
            continue
        opened = FENCE_OPEN.match(line)
        if opened:
            fence = opened.group(1)
            continue

        def rewrite(match):
            replacement, note = new_link(match, page_repository_dir, page_bundle_dir, bundle_root)
            if note:
                notes.append(f"{docs_name}/{relative_page}:{number}: {note}")
            return replacement

        lines[number - 1] = rewrite_line(line, rewrite)
    if notes:
        with open(path, "wb") as f:
            f.write("\n".join(lines).encode("utf-8"))
    return notes


def main(argv):
    if len(argv) != 1 or not os.path.isdir(argv[0]):
        sys.exit("usage: python rewrite_doc_links.py <the bundle's docs folder>")
    docs = os.path.abspath(argv[0])
    docs_name, bundle_root = os.path.basename(docs), os.path.dirname(docs)
    notes, pages = [], 0
    for folder, subfolders, files in os.walk(docs):
        subfolders.sort()
        for name in sorted(files):
            if not name.endswith(".md"):
                continue
            pages += 1
            relative_page = os.path.relpath(os.path.join(folder, name), docs).replace(os.sep, "/")
            notes += rewrite_page(os.path.join(folder, name), relative_page, docs_name, bundle_root)
    for note in notes:
        print(f"[dist] docs link {note}")
    print(f"[dist] docs links: {len(notes)} rewritten in {pages} pages")


if __name__ == "__main__":
    main(sys.argv[1:])
