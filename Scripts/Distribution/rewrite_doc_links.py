"""Point the links in a distribution's copied pages at what the bundle holds.

The pages are written for the repository. The user documentation lives in Docs/ and the
example scenarios' READMEs in CarlaControl/Examples/Scenarios/. A link that leaves its own folder
names a file by its place in the repository, and in a bundle that place does not exist: the pages
are shipped as docs/ and Scenarios/, and the other repository folders are shipped under other names
or not at all. Both distribution scripts (Scripts/Windows/MakeDistribution.ps1,
Scripts/Linux/MakeDistribution.sh) run this once the pages, the skills and the example scenarios are
staged, so both bundles get the same bytes.

For every inline link, [text](target) or ![text](target), whose target leaves the page's own folder:

  * a target in a repository folder the bundle ships under another name (SHIPPED_AS below), and
    present in the bundle, gets the bundle-relative path to that copy, its #fragment kept;
  * a target in such a folder that is absent from the bundle, because the scripts leave it out
    (CarlaControl/skills/third-party/), becomes its link text, with the link dropped;
  * every other link is left as it is.

A link that stays inside the page's own folder is left alone: each folder is copied with its
structure kept, so those links resolve as they did. Whether a target ships is read from the staged
bundle, with the exact case of every name, so the answer is the same on Windows and Linux and cannot
drift from what the scripts copy. Fenced code blocks and code spans are never touched. A page is
rewritten only where a link changes, byte for byte everywhere else, and every change is printed.

Usage: python rewrite_doc_links.py <the bundle's folder>
"""
import os
import posixpath
import re
import sys

# The folders of pages the bundle copies, each with where it came from in the repository: (bundle
# path, repository path). A folder the bundle goes without is skipped.
PAGE_ROOTS = [
    ("docs", "Docs"),
    ("Scenarios", "CarlaControl/Examples/Scenarios"),
]

# Repository folders the bundle ships under another name: (repository path, bundle path).
SHIPPED_AS = [
    ("Docs", "docs"),
    ("CarlaControl/Examples/Scenarios", "Scenarios"),
    ("CarlaControl/skills", "skills"),
]

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


def within(path, folder):
    """True when a repository-relative path is the folder or lies under it."""
    return path == folder or path.startswith(folder + "/")


def new_link(match, page_repository_dir, page_bundle_dir, page_repository_root, bundle_root):
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
    if within(resolved, page_repository_root):
        return match.group(0), None                      # stays in the page's own folder
    for repository_path, bundle_path in SHIPPED_AS:
        if within(resolved, repository_path):
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


def rewrite_page(path, relative_page, bundle_name, repository_root, bundle_root):
    """Rewrite one page in place; returns the notes of what changed.

    Args:
        path: the page's file in the bundle.
        relative_page: the page's path inside its folder, with forward slashes.
        bundle_name: the folder's name in the bundle, such as `docs`.
        repository_root: where the folder came from in the repository, such as `Docs`.
        bundle_root: the bundle's folder, where what shipped is read from.
    """
    with open(path, "rb") as f:
        original = f.read().decode("utf-8")
    page_dir = posixpath.dirname(relative_page)
    page_repository_dir = posixpath.normpath(posixpath.join(repository_root, page_dir))
    page_bundle_dir = posixpath.normpath(posixpath.join(bundle_name, page_dir))
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

        def rewrite(match, number=number):
            replacement, note = new_link(match, page_repository_dir, page_bundle_dir,
                                         repository_root, bundle_root)
            if note:
                notes.append(f"{bundle_name}/{relative_page}:{number}: {note}")
            return replacement

        lines[number - 1] = rewrite_line(line, rewrite)
    if notes:
        with open(path, "wb") as f:
            f.write("\n".join(lines).encode("utf-8"))
    return notes


def rewrite_bundle(bundle_root):
    """Rewrite every page under the bundle's page folders; returns the notes and the page count."""
    notes, pages = [], 0
    for bundle_name, repository_root in PAGE_ROOTS:
        folder = os.path.join(bundle_root, bundle_name)
        if not os.path.isdir(folder):
            continue
        for here, subfolders, files in os.walk(folder):
            subfolders.sort()
            for name in sorted(files):
                if not name.endswith(".md"):
                    continue
                pages += 1
                relative_page = os.path.relpath(os.path.join(here, name), folder).replace(os.sep, "/")
                notes += rewrite_page(os.path.join(here, name), relative_page, bundle_name,
                                      repository_root, bundle_root)
    return notes, pages


def main(argv):
    if len(argv) != 1 or not os.path.isdir(argv[0]):
        sys.exit("usage: python rewrite_doc_links.py <the bundle's folder>")
    bundle_root = os.path.abspath(argv[0])
    notes, pages = rewrite_bundle(bundle_root)
    for note in notes:
        print(f"[dist] link {note}")
    folders = ", ".join(name for name, _ in PAGE_ROOTS if os.path.isdir(os.path.join(bundle_root, name)))
    print(f"[dist] links: {len(notes)} rewritten in {pages} pages ({folders})")


if __name__ == "__main__":
    main(sys.argv[1:])
