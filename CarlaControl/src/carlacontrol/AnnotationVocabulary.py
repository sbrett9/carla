"""The vocabulary a scenario's labels resolve against: the closed core, and the author's namespaces.

`06_Truth_And_Annotation.md` §3.7 splits the vocabulary on one test -- **does the pipeline's own code
branch on this term?** The core is the set of terms whose misspelling makes the pipeline behave
differently; it is closed, versioned and never declared by an author. Everything else -- what a label
means, what a role signifies, what an area kind is -- is the author's, carried **opaquely but
self-describingly**: this class never interprets an author term, it only requires that each one is
declared, with its definition, where a consumer who has never met the author can read it.

Labelling is a contract between the scenario author and the model trainer, and this pipeline carries
it without adjudicating it. So this class invents no terms and refuses none on grounds of meaning.
What it refuses is only what it can establish from the declarations in front of it:

* check 46 -- a namespace used in a label, role, phase or area kind that nothing declared or imported;
* check 18 -- a label that is not a term of its namespace, a role other than `subject` that is not
  declared, a namespace declared twice, a term whose prefix is not its namespace, a relation
  (`broader`, `contrast_with`, `hard_negative_for`, `superseded_by`, a `term` counterfactual) that does
  not resolve inside the published document, and a `broader` chain with a cycle;
* check 45 -- a label whose `applies_to` excludes the kind of subject it is attached to, or whose
  `realisation` excludes the instance's;
* check 56 -- a parameter that none of the subject's labels declares in its `parameters{}`, a value
  not of the declared type, and one key that two of its labels declare differently.

It also projects a term's `hard_negative_for` onto the subjects that carry it (06 §3.9(d)): the term is
the authority, and the record carries a copy so neither the plan nor a sidecar has to be read against
the vocabulary to be usable.

**The core is not written here.** D6.30 generates it from the enumerations the pipeline's code
switches on, and this module reads them through `carlanet` from `CarlaNet.Types.Supervision`'s
`CoreVocabulary`, as `IlluminationBand` reads the bands: every family, its terms in their published
order, the core's version and the reserved role and phase. So a term the code gains reaches every plan
compiled after it, and the vocabulary, the band in every capture's truth and the render state the world
truth track writes are spelled from one table. The published document is resolved and
import-flattened, and `digest` is over exactly what is published, so a consumer can bind it
(`04_Contracts.md` C3 V3.15).
"""
from __future__ import annotations

import hashlib
import json
from pathlib import Path

import carlanet  # noqa: F401  -- loads the CarlaNet assemblies the next import names
from CarlaNet.Types.Supervision import CoreVocabulary

from carlacontrol.CompileFindings import CompileFindings
from carlacontrol.ScenarioSchema import ScenarioSchema

CORE_VOCABULARY_VERSION = int(CoreVocabulary.Version)
CORE_SOURCE = str(CoreVocabulary.Source)

TERM_CHECK = 18
APPLIES_CHECK = 45
NAMESPACE_CHECK = 46
PARAMETER_CHECK = 56

SUBJECT_ROLE = str(CoreVocabulary.SubjectRole)
VACANCY_PHASE = str(CoreVocabulary.VacancyPhase)

# 06 §3.7, the closed core: the terms the pipeline's own code branches on, family by family.
CORE_TERMS: dict[str, list[str]] = {str(family.Family): [str(term) for term in family.Terms]
                                    for family in CoreVocabulary.Families}


class AnnotationVocabulary:
    """A scenario's resolved vocabulary: the core, plus every author namespace declared or imported."""

    def __init__(self, namespaces: dict[str, dict], findings: CompileFindings) -> None:
        self.namespaces = namespaces
        self.findings = findings
        self.terms: dict[str, dict] = {}
        self.roles: dict[str, dict] = {}
        self.area_kinds: dict[str, dict] = {}
        for name, block in namespaces.items():
            for term in block.get("terms", []):
                self.terms[term["term"]] = term
            for role in block.get("roles", []):
                self.roles[role["role"]] = role
            for kind in block.get("area_kinds", []):
                self.area_kinds[kind["kind"]] = kind
        self._check_declarations()

    @classmethod
    def from_specification(cls, block: dict | None, base: Path,
                           findings: CompileFindings) -> AnnotationVocabulary:
        """Gather the namespaces a specification declares inline and imports, refusing duplicates."""
        block = block or {}
        gathered: dict[str, dict] = {}
        sources: dict[str, str] = {}
        documents: list[tuple[str, dict]] = []
        for relative in block.get("import", []):
            path = (base / relative).resolve()
            try:
                document = json.loads(path.read_text(encoding="utf-8"))
            except (OSError, ValueError) as problem:
                findings.refuse(NAMESPACE_CHECK, f"vocabulary import {relative}",
                                f"cannot be read: {problem}")
                continue
            problems = ScenarioSchema.validate_definition(document, "namespace", f"import {relative}")
            if problems:
                for problem in problems:
                    findings.refuse(53, f"vocabulary import {relative}", problem)
                continue
            documents.append((f"import {relative}", document))
        for index, document in enumerate(block.get("namespaces", [])):
            documents.append((f"vocabulary.namespaces[{index}]", document))
        for where, document in documents:
            name = document["namespace"]
            if name in gathered:
                findings.refuse(TERM_CHECK, where, f"declares namespace '{name}', which "
                                f"{sources[name]} already declares; one namespace has one "
                                "definition in a scenario")
                continue
            gathered[name] = document
            sources[name] = where
        return cls(gathered, findings)

    # -- checks on the declarations themselves ----------------------------------------------------

    def _check_declarations(self) -> None:
        for name, block in self.namespaces.items():
            where = f"namespace {name}"
            version = block.get("version", 0)
            for term in block.get("terms", []):
                spelled = term["term"]
                if spelled.split(":", 1)[0] != name:
                    self.findings.refuse(TERM_CHECK, where, f"declares '{spelled}', whose prefix is "
                                         f"not '{name}'")
                if term.get("since", 0) > version:
                    self.findings.refuse(TERM_CHECK, where, f"'{spelled}' appeared at version "
                                         f"{term['since']}, after the namespace's own {version}")
                if term.get("status") == "deprecated" and "superseded_by" not in term:
                    self.findings.refuse(TERM_CHECK, where, f"'{spelled}' is deprecated with no "
                                         "superseded_by; retiring a term names its successor")
                for field in ("broader", "superseded_by"):
                    self._require_term(term.get(field), where, f"'{spelled}' {field}")
                for field in ("contrast_with", "hard_negative_for"):
                    for other in term.get(field, []):
                        self._require_term(other, where, f"'{spelled}' {field}")
                counterfactual = term.get("counterfactual")
                if counterfactual and counterfactual["kind"] == "term":
                    self._require_term(counterfactual["ref"], where, f"'{spelled}' counterfactual")
            for role in block.get("roles", []):
                if role["role"].split(":", 1)[0] != name:
                    self.findings.refuse(TERM_CHECK, where, f"declares role '{role['role']}', whose "
                                         f"prefix is not '{name}'")
        self._check_broader_cycles()

    def _require_term(self, spelled: str | None, where: str, what: str) -> None:
        if spelled is not None and spelled not in self.terms:
            self.findings.refuse(TERM_CHECK, where, f"{what} names '{spelled}', which no namespace in "
                                 "this scenario declares; a relation resolves inside the published "
                                 "vocabulary")

    def _check_broader_cycles(self) -> None:
        for start in self.terms:
            seen = [start]
            current = self.terms[start].get("broader")
            while current is not None and current in self.terms:
                if current in seen:
                    self.findings.refuse(TERM_CHECK, f"namespace {start.split(':', 1)[0]}",
                                         f"'broader' is circular: {' -> '.join([*seen, current])}")
                    break
                seen.append(current)
                current = self.terms[current].get("broader")

    # -- checks on use ----------------------------------------------------------------------------

    def check_labels(self, labels: list[str], subject_kind: str, realisation: str,
                     where: str) -> None:
        """Each label is a declared term applying to this kind of subject and realisation."""
        for label in labels:
            if not self._namespace_declared(label, where):
                continue
            term = self.terms.get(label)
            if term is None:
                self.findings.refuse(TERM_CHECK, where, f"label '{label}' is not a term of namespace "
                                     f"'{label.split(':', 1)[0]}'")
                continue
            if subject_kind not in term["applies_to"]:
                self.findings.refuse(APPLIES_CHECK, where,
                                     f"label '{label}' applies to {term['applies_to']}, and is "
                                     f"attached to a {subject_kind}")
            if realisation not in term["realisation"]:
                self.findings.refuse(APPLIES_CHECK, where,
                                     f"label '{label}' is declared for realisation "
                                     f"{term['realisation']}, and the subject is {realisation}")

    def check_role(self, role: str, where: str) -> None:
        """`subject`, or a role declared in its namespace."""
        if role == SUBJECT_ROLE:
            return
        if ":" not in role:
            self.findings.refuse(TERM_CHECK, where, f"role '{role}' is neither the reserved "
                                 f"'{SUBJECT_ROLE}' nor a namespaced role")
            return
        if self._namespace_declared(role, where) and role not in self.roles:
            self.findings.refuse(TERM_CHECK, where, f"role '{role}' is not declared in namespace "
                                 f"'{role.split(':', 1)[0]}'")

    def check_namespaced(self, value: str, what: str, where: str) -> None:
        """A phase or area kind is free, but a namespace it names must be declared."""
        if ":" in value:
            self._namespace_declared(value, where, what)

    def check_parameters(self, parameters: dict, labels: list[str], where: str) -> None:
        """Check 56: each parameter is a key one of the labels' terms declares, of the declared type.

        A parameter is read against the declaration of every label declaring its key, so two such
        labels must agree on its type and unit: a value carries one meaning (06 §3.8). A label that
        is not a term was refused by `check_labels` and declares nothing here.
        """
        declared = {label: self.terms[label].get("parameters", {}) for label in labels
                    if label in self.terms}
        for key, value in parameters.items():
            declaring = [(label, keys[key]) for label, keys in declared.items() if key in keys]
            if not declaring:
                known = sorted({name for keys in declared.values() for name in keys})
                self.findings.refuse(
                    PARAMETER_CHECK, where,
                    f"carries parameter '{key}', which none of its labels declares"
                    + (f" (they declare {', '.join(known)})" if known
                       else " (they declare none)" if labels else ", and it carries no label")
                    + ". A parameter is a key its label's term declares in parameters{} with a "
                    "type, a unit and a definition (06 §3.8): declare it there, or remove it")
                continue
            for label, declaration in declaring:
                if ScenarioSchema.validate_against(value, {"type": declaration["type"]}):
                    self.findings.refuse(
                        PARAMETER_CHECK, where,
                        f"parameter '{key}' is {json.dumps(value)}, and '{label}' declares it "
                        f"{declaration['type']} ({declaration['definition']})")
            meanings = {(d["type"], d.get("unit", "")) for _, d in declaring}
            if len(meanings) > 1:
                self.findings.refuse(
                    PARAMETER_CHECK, where,
                    f"parameter '{key}' is declared differently by its labels: "
                    + "; ".join(f"'{label}' as {d['type']}" + (f" in {d['unit']}" if d.get("unit")
                                                              else "")
                                for label, d in declaring)
                    + ". One value cannot be read in two ways; declare the key alike or rename one")

    def hard_negatives_of(self, labels: list[str]) -> list[str]:
        """The terms a subject carrying these labels is a matched negative for: every label's
        `hard_negative_for`, in declaration order, once each (06 §3.9(d))."""
        found: list[str] = []
        for label in labels:
            for other in self.terms.get(label, {}).get("hard_negative_for", []):
                if other not in found:
                    found.append(other)
        return found

    def _namespace_declared(self, spelled: str, where: str, what: str = "") -> bool:
        name = spelled.split(":", 1)[0]
        if name in self.namespaces:
            return True
        self.findings.refuse(NAMESPACE_CHECK, where,
                             f"{what + ' ' if what else ''}'{spelled}' uses namespace '{name}', "
                             "which the specification neither declares nor imports")
        return False

    # -- what is published ------------------------------------------------------------------------

    def to_document(self) -> dict:
        """The resolved, import-flattened vocabulary: the core, and every namespace by name."""
        return {
            "core": {"vocabulary_version": CORE_VOCABULARY_VERSION, "source": CORE_SOURCE,
                     "terms": CORE_TERMS},
            "namespaces": [self.namespaces[name] for name in sorted(self.namespaces)],
        }

    @property
    def namespace_versions(self) -> list[dict]:
        return [{"namespace": name, "version": self.namespaces[name]["version"]}
                for name in sorted(self.namespaces)]

    @property
    def digest(self) -> str:
        canonical = json.dumps(self.to_document(), sort_keys=True, indent=2, ensure_ascii=False)
        return hashlib.sha256(canonical.encode("utf-8")).hexdigest()
