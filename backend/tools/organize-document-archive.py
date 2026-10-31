#!/usr/bin/env python3
"""Apply the reviewed 2026-10-29 document moves and rebase Markdown links once."""

import importlib.util
import json
import os
import re
from pathlib import Path
from urllib.parse import unquote, urlsplit

ROOT = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("archive_check", ROOT / "tools/check-document-archive.py")
check = importlib.util.module_from_spec(spec)
spec.loader.exec_module(check)

# Each list was reviewed against the document's scope and status. Ambiguous or
# still-active plans and runtime contracts stay at their existing location.
GROUPS = {
    "plans": """
advanced-material-render-graph-plan.md
backend-api-completion-plan.md
backend-api-v1-draft.md
backend-b3-completion-plan.md
backend-b4-plan.md
backend-basic-business-completion-plan.md
backend-extraction-plan.md
backend-g16-g17-plan.md
backend-g20-g24-completion-plan.md
backend-g20-g24-plan.md
backend-g20b-node-creation-plan.md
backend-gpu-performance-optimization-plan-20261027.md
backend-gpu-structural-performance-optimization-plan-20261028.md
backend-package-resource-closure-continuous-preview-plan.md
backend-package-resource-closure-plan.md
backend-package-validation-plan.md
backend-project-image-export-plan.md
backend-project-image-preview-plan.md
backend-project-image-validation-plan.md
backend-public-package-preview-plan.md
backend-render-backend-api-plan.md
backend-render-performance-baseline-plan-20261027.md
backend-review-and-web-contract-plan.md
backend-ruby-spacing-plan.md
backend-static-style-api-plan.md
backend-template-library-assignment-plan.md
backend-ttml-ruby-timeline-plan.md
backend-w4-orchestration-plan.md
backend-web-editing-plan.md
code-review-remediation-plan.md
gpu-render-plan.md
karaoke-lead-in-plan.md
lyric-exclusive-display-case-plan.md
lyrics-import-and-splitting.md
scripting-effects-sdk-package-plan.md
scripting-system-architecture-plan.md
scripting-system-implementation-contract.md
scripting-system-implementation-roadmap.md
scripting-system-next-work-plan.md
style-animation-system-plan.md
style-data-model-unification-plan.md
style-engine-next-phase-plan.md
unified-scene-element-plan.md
""".split(),
    "research": """
backend-api-p5-closure-audit-20261025.md
backend-baseline-change-report.md
backend-g19b-gap-analysis.md
backend-gpu-performance-optimization-research-20261027.md
backend-gpu-structural-performance-research-20261028.md
backend-render-performance-baseline-20261027.md
backend-structure-audit-20261024.md
backend-ttml-ruby-independent-audit.md
backend-web-feasibility-report.md
code-review-remediation-final-audit.md
scripting-r1-capability-report.md
scripting-r2-sdk-geometry-progress.md
scene-container-composition-review.md
web-g15-orchestration-audit.md
""".split(),
    "acceptance": """
backend-basic-business-bugfix-0941.md
backend-gpu-performance-optimization-acceptance-20261027.md
backend-gpu-structural-performance-optimization-acceptance-20261028.md
code-review-remediation-acceptance.md
code-review-remediation-user-acceptance.md
gpu-render-acceptance-20261026.md
render-backend-api-acceptance-20261026.md
scripting-persistent-closure-acceptance.md
scripting-r6-acceptance.md
scripting-r7-acceptance.md
scripting-r8-acceptance.md
scripting-r8-public-packages.md
scripting-session-combination-acceptance.md
scripting-session-revision-acceptance.md
""".split(),
    "legacy-ui": """
authoring-module-and-bindings-contract.md
color-picker-interaction-plan.md
control-wiring-test-contract.md
export-entry-contract.md
main-window-material-you-refinement-plan.md
new-project-startup-contract.md
preset-input-popup-motion-contract.md
scene-container-editor-plan.md
scene-editor-implementation-contract.md
scene-editor-ux-optimization-plan.md
scripting-r7-binding-entry-contract.md
scripting-r7-recipe-editing-contract.md
scripting-r7-scene-preview-context-contract.md
scripting-r7-template-recipe-contract.md
scripting-workspace-preview-contract.md
scripting-workspace-source-editing-contract.md
style-editor-ux-redesign-plan.md
style-system-redesign.md
template-application-ux-plan.md
temporary-style-line-editor-optimization.md
timeline-ui.md
ui-0496-feature-migration.md
ui-0496-refactoring-plan.md
ui-0496-refactoring-progress.md
ui-preview-cache-validation.md
ui-redesign-acceptance.md
ui-redesign-and-suki-migration-plan.md
ui-suki-compatibility.md
ui-workspace-guide.md
ui-workspace-ownership-acceptance.md
ui-workspace-ownership-plan.md
""".split(),
    "history": """
backend-branch-development-records-20261025.md
backend-extraction-progress.md
backend-main-integration-20261027.md
backend-main-integration-20261028.md
backend-repository-split.md
current-architecture.md
development.md
roadmap.md
""".split(),
    "decisions": ["decisions.md"],
}
EXTRA = {"AGENTS.md": "docs/archive/history/AGENTS-20261029-history.md"}
PROTECTED = {"web/api-contract.md", "web/api-index.md", "docs/architecture/backend-api.md",
             "docs/runtime-validation-and-layout-contract.md"}
LINK = re.compile(r'(\]\(\s*<?)([^\s)>]+)(>?)(?=\s|\))')
REFERENCE = re.compile(r'^(\s{0,3}\[[^\]]+\]:\s*<?)([^\s>]+)(>?)')
INLINE_CODE = re.compile(r'(`+)(.*?)(\1)')


def make_mapping():
    moves = {f"docs/{name}": f"docs/archive/{group}/{name}"
             for group, names in GROUPS.items() for name in names}
    moves.update(EXTRA)
    if len(moves) != sum(map(len, GROUPS.values())) + len(EXTRA):
        raise ValueError("duplicate archive source")
    return moves


def rebase(text, old, new, moves):
    def replace(match):
        raw = match.group(2)
        parts = urlsplit(raw)
        if parts.scheme or parts.netloc or raw.startswith("//") or not parts.path:
            return match.group(0)
        original = (ROOT / old).parent.joinpath(unquote(parts.path)).resolve()
        try:
            relative = str(original.relative_to(ROOT))
        except ValueError:
            if old == new:
                return match.group(0)
            relative = None
        final = ROOT / moves.get(relative, relative) if relative is not None else original
        if old == new and relative not in moves:
            return match.group(0)
        path = os.path.relpath(final, (ROOT / new).parent)
        if parts.path.endswith("/"):
            path += "/"
        suffix = ("?" + parts.query if parts.query else "") + ("#" + parts.fragment if parts.fragment else "")
        return match.group(1) + path + suffix + match.group(3)

    result = []
    fence = None
    for line in text.splitlines(keepends=True):
        marker = re.match(r'^\s{0,3}(`{3,}|~{3,})', line)
        if marker:
            if fence is None:
                fence = marker.group(1)[0]
            elif marker.group(1)[0] == fence:
                fence = None
            result.append(line)
            continue
        if fence:
            result.append(line)
            continue
        pieces = INLINE_CODE.split(line)
        for index in range(0, len(pieces), 4):
            pieces[index] = REFERENCE.sub(replace, LINK.sub(replace, pieces[index]))
        result.append("".join(pieces))
    return "".join(result)


def main():
    moves = make_mapping()
    if (ROOT / "docs/archive/document-map-20261029.json").exists():
        extend(moves)
        return
    paths = check.tracked_markdown()
    originals = {str(p.relative_to(ROOT)): p.read_text(encoding="utf-8") for p in paths}
    if any(source not in originals for source in moves):
        raise SystemExit("archive source missing")
    protected = {p for p in originals if p in PROTECTED or p.startswith(("docs/performance/", "samples/ScriptEffects/"))}
    needed = {"docs/roadmap.md": set(), "docs/development.md": set(), "docs/decisions.md": set()}
    for name in protected:
        tokens, _ = check.parse_document(ROOT / name)
        for token in tokens:
            for child in token.children or []:
                if child.type not in {"link_open", "image"}:
                    continue
                value = child.attrGet("href" if child.type == "link_open" else "src")
                if not value:
                    continue
                parts = urlsplit(value)
                if parts.scheme or parts.netloc or not parts.path:
                    continue
                target = ((ROOT / name).parent / unquote(parts.path)).resolve()
                try:
                    relative = str(target.relative_to(ROOT))
                except ValueError:
                    continue
                if relative in moves:
                    needed.setdefault(relative, set())
                    if parts.fragment:
                        needed[relative].add(unquote(parts.fragment))
    for old, new in moves.items():
        destination = ROOT / new
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_text(rebase(originals[old], old, new, moves), encoding="utf-8")
        (ROOT / old).unlink()
    for name, text in originals.items():
        if name in moves or name in protected:
            continue
        revised = rebase(text, name, name, moves)
        if revised != text:
            (ROOT / name).write_text(revised, encoding="utf-8")
    for source, fragments in needed.items():
        destination = moves[source]
        path = os.path.relpath(ROOT / destination, (ROOT / source).parent)
        lines = ["# 历史文档导航", "", f"正文已归档至 [{Path(source).name}]({path})。",
                 "原路径为历史合同、证据或源码注释保留；归档材料维持原有版本和验收归属。", ""]
        for fragment in sorted(fragments):
            lines += [f'<a id="{fragment}"></a>', f"[历史章节]({path}#{fragment})", ""]
        (ROOT / source).write_text("\n".join(lines), encoding="utf-8")
    entries = []
    for source in sorted(originals):
        if source != "AGENTS.md" and not (source.startswith("docs/") and source.count("/") == 1):
            continue
        dest = moves.get(source, source)
        title = originals[source].splitlines()[0].lstrip("# ")
        category = Path(dest).parts[2] if source in moves else "retained"
        reason = ("历史材料已归档，原版本及证据归属不变" if source in moves else
                  "当前入口、业务合同、未完成计划或仍需核实的业务资料，保留原位置")
        entries.append({"source": source, "destination": dest, "category": category,
                        "title": title, "reason": reason, "stub": source in needed})
    target = ROOT / "docs/archive/document-map-20261029.json"
    target.write_text(json.dumps({"baseline": "4e70334", "documents": entries}, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"Archived {len(moves)} documents; retained {len(entries) - len(moves)}; created {len(needed)} navigation pages")


def extend(moves):
    import hashlib
    import subprocess

    target = ROOT / "docs/archive/document-map-20261029.json"
    manifest = json.loads(target.read_text(encoding="utf-8"))
    entries = {entry["source"]: entry for entry in manifest["documents"]}
    snapshots = {entry["destination"] for entry in manifest["documents"] if entry.get("contentMode") == "snapshot"}
    new_moves = {source: dest for source, dest in moves.items()
                 if entries[source]["destination"] == source}
    if not new_moves:
        raise SystemExit("archive already applied")
    paths = check.tracked_markdown()
    originals = {str(path.relative_to(ROOT)): path.read_text(encoding="utf-8") for path in paths}
    needed = {}
    for name in originals:
        if name not in PROTECTED and not name.startswith(("docs/performance/", "samples/ScriptEffects/")):
            continue
        tokens, _ = check.parse_document(ROOT / name)
        for token in tokens:
            for child in token.children or []:
                if child.type not in {"link_open", "image"}:
                    continue
                value = child.attrGet("href" if child.type == "link_open" else "src")
                if not value:
                    continue
                parts = urlsplit(value)
                if parts.scheme or parts.netloc or not parts.path:
                    continue
                try:
                    relative = str(((ROOT / name).parent / unquote(parts.path)).resolve().relative_to(ROOT))
                except ValueError:
                    continue
                if relative in new_moves:
                    needed.setdefault(relative, set())
                    if parts.fragment:
                        needed[relative].add(unquote(parts.fragment))
    for old, new in new_moves.items():
        destination = ROOT / new
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_text(rebase(originals[old], old, new, new_moves), encoding="utf-8")
        (ROOT / old).unlink()
        baseline = subprocess.check_output(["git", "show", f"{manifest['baseline']}:{old}"], cwd=ROOT)
        blob = subprocess.check_output(["git", "rev-parse", f"{manifest['baseline']}:{old}"], cwd=ROOT, text=True).strip()
        category = Path(new).parts[2]
        entries[old].update(destination=new, category=category, stub=old in needed, sourceBlob=blob,
                            sourceSha256=hashlib.sha256(baseline).hexdigest(),
                            reason=("桌面 UI 已移出本仓库，原设计和验收作为历史保留" if category == "legacy-ui"
                                    else "该阶段历史设计或实施记录保留，现行行为以合同和源码为准"))
    for name, text in originals.items():
        if name in new_moves or name in PROTECTED or name in snapshots or name.startswith(("docs/performance/", "samples/ScriptEffects/")):
            continue
        revised = rebase(text, name, name, new_moves)
        if revised != text:
            (ROOT / name).write_text(revised, encoding="utf-8")
    for source, fragments in needed.items():
        destination = new_moves[source]
        path = os.path.relpath(ROOT / destination, (ROOT / source).parent)
        lines = ["# 历史文档导航", "", f"正文已归档至 [{Path(source).name}]({path})。",
                 "原路径为历史合同、证据或源码注释保留；归档材料维持原有版本和验收归属。", ""]
        for fragment in sorted(fragments):
            lines += [f'<a id="{fragment}"></a>', f"[历史章节]({path}#{fragment})", ""]
        (ROOT / source).write_text("\n".join(lines), encoding="utf-8")
    target.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"Archived {len(new_moves)} additional desktop documents")


if __name__ == "__main__":
    main()
