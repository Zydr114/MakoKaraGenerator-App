#!/usr/bin/env python3
"""Audit tracked Markdown links and a reviewed document archive manifest.

Requires markdown-it-py. This is an offline diagnostic, not a GitHub renderer.
"""

import argparse
import hashlib
import importlib.util
import json
import re
import subprocess
import unicodedata
from collections import Counter
from pathlib import Path
from urllib.parse import unquote, urlsplit

from markdown_it import MarkdownIt

ROOT = Path(__file__).resolve().parent.parent
PARSER = MarkdownIt("commonmark", {"html": True})
HTML_ID = re.compile(r'\b(?:id|name)\s*=\s*["\']([^"\']+)["\']', re.I)


def tracked_markdown():
    output = subprocess.check_output(["git", "ls-files", "--cached", "--others", "--exclude-standard", "-z", "--", "*.md"], cwd=ROOT)
    return sorted({ROOT / path for path in output.decode().split("\0") if path
                   and not path.startswith("tools/backend-project-image-export-probe/")
                   and (ROOT / path).is_file()})


def slug(text):
    text = re.sub(r"<[^>]*>", "", text).strip().lower()
    text = "".join(c for c in text if c in "-_ " or unicodedata.category(c)[0] in "LMN")
    return text.replace(" ", "-")


def parse_document(path):
    text = path.read_text(encoding="utf-8")
    tokens = PARSER.parse(text)
    anchors = set(HTML_ID.findall(text))
    counts = Counter()
    for i, token in enumerate(tokens):
        if token.type == "heading_open":
            inline = tokens[i + 1]
            heading = "".join(child.content for child in inline.children or [] if child.type in {"text", "code_inline", "image"})
            base = slug(heading)
            suffix = counts[base]
            counts[base] += 1
            anchors.add(base if not suffix else f"{base}-{suffix}")
    return tokens, anchors


def audit(paths):
    cache = {}
    issues = []
    count = 0
    for path in paths:
        tokens, _ = cache.setdefault(path, parse_document(path))
        for token in tokens:
            if token.type != "inline":
                continue
            for child in token.children or []:
                if child.type not in {"link_open", "image"}:
                    continue
                target = child.attrGet("href" if child.type == "link_open" else "src")
                if not target:
                    continue
                parts = urlsplit(target)
                if parts.scheme or parts.netloc or target.startswith("//"):
                    continue
                count += 1
                dest = (path.parent / unquote(parts.path)).resolve() if parts.path else path
                problem = None
                if not dest.exists():
                    problem = "missing-target"
                elif parts.fragment and dest.is_file() and dest.suffix.lower() == ".md":
                    if dest not in cache:
                        cache[dest] = parse_document(dest)
                    if unquote(parts.fragment) not in cache[dest][1]:
                        problem = "missing-anchor"
                if problem:
                    issues.append({"file": str(path.relative_to(ROOT)), "line": token.map[0] + 1,
                                   "target": target, "kind": problem})
    return {"files": len(paths), "localLinks": count, "issues": issues}


def evidence():
    return {str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest()
            for p in sorted((ROOT / "docs/performance").rglob("*")) if p.is_file()}


def sample_packages():
    output = subprocess.check_output(["git", "ls-files", "-z", "--", "samples/ScriptEffects/"], cwd=ROOT)
    return {name: hashlib.sha256((ROOT / name).read_bytes()).hexdigest() if (ROOT / name).is_file() else None
            for name in output.decode().split("\0") if name}


def verify_manifest(manifest):
    errors = []
    spec = importlib.util.spec_from_file_location("archive_organizer", ROOT / "tools/organize-document-archive.py")
    organizer = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(organizer)
    moves = {entry["source"]: entry["destination"] for entry in manifest["documents"]
             if entry["source"] != entry["destination"]}
    for entry in manifest["documents"]:
        if not (ROOT / entry["destination"]).is_file():
            errors.append(f"missing mapped document: {entry['destination']}")
            continue
        if entry.get("stub") and not (ROOT / entry["source"]).is_file():
            errors.append(f"missing navigation page: {entry['source']}")
        if entry["source"] in moves:
            try:
                content = subprocess.check_output(["git", "show", f"{manifest['baseline']}:{entry['source']}"], cwd=ROOT)
                blob = subprocess.check_output(["git", "rev-parse", f"{manifest['baseline']}:{entry['source']}"], cwd=ROOT, text=True).strip()
            except subprocess.CalledProcessError:
                errors.append(f"baseline unavailable: {entry['source']}")
                continue
            if hashlib.sha256(content).hexdigest() != entry.get("sourceSha256") or blob != entry.get("sourceBlob"):
                errors.append(f"baseline fingerprint mismatch: {entry['source']}")
            expected = (content.decode("utf-8") if entry.get("contentMode") == "snapshot" else
                        organizer.rebase(content.decode("utf-8"), entry["source"], entry["destination"], moves))
            if (ROOT / entry["destination"]).read_text(encoding="utf-8") != expected:
                errors.append(f"archived text differs beyond link relocation: {entry['destination']}")
    return errors


def normalized_issue(issue, moves):
    source = issue["file"]
    parts = urlsplit(issue["target"])
    dest = ((ROOT / source).parent / unquote(parts.path)).resolve() if parts.path else ROOT / source
    try:
        dest = str(dest.relative_to(ROOT))
    except ValueError:
        dest = str(dest)
    return (moves.get(source, source), moves.get(dest, dest), unquote(parts.fragment), issue["kind"])


def compare_issues(prior, current, manifest):
    moves = {entry["source"]: entry["destination"] for entry in manifest["documents"]
             if entry["source"] != entry["destination"]}
    snapshots = {entry["destination"] for entry in manifest["documents"] if entry.get("contentMode") == "snapshot"}
    current = [issue for issue in current if issue["file"] not in snapshots]
    old = Counter(normalized_issue(issue, moves) for issue in prior)
    new = Counter(normalized_issue(issue, moves) for issue in current)
    return {"existing": sum((old & new).values()), "fixed": sum((old - new).values()),
            "new": [{"source": key[0], "destination": key[1], "fragment": key[2], "kind": key[3], "count": count}
                    for key, count in sorted((new - old).items())]}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path)
    parser.add_argument("--manifest", type=Path)
    parser.add_argument("--evidence-baseline", type=Path)
    parser.add_argument("--link-baseline", type=Path)
    args = parser.parse_args()
    result = audit(tracked_markdown())
    result["evidenceSha256"] = evidence()
    result["samplePackageSha256"] = sample_packages()
    manifest = json.loads(args.manifest.read_text()) if args.manifest else None
    result["manifestErrors"] = verify_manifest(manifest) if manifest else []
    if args.evidence_baseline:
        prior = json.loads(args.evidence_baseline.read_text())
        result["evidenceUnchanged"] = prior["evidenceSha256"] == result["evidenceSha256"]
        result["samplePackagesUnchanged"] = prior["samplePackageSha256"] == result["samplePackageSha256"]
    if args.link_baseline:
        if manifest is None:
            parser.error("--link-baseline requires --manifest")
        prior = json.loads(args.link_baseline.read_text())["issues"]
        result["comparison"] = compare_issues(prior, result["issues"], manifest)
    payload = json.dumps(result, ensure_ascii=False, indent=2) + "\n"
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(payload, encoding="utf-8")
    print(f"Markdown files: {result['files']}; local links: {result['localLinks']}; "
          f"issues: {len(result['issues'])}; manifest errors: {len(result['manifestErrors'])}")
    if args.evidence_baseline:
        print(f"Performance evidence unchanged: {result['evidenceUnchanged']}")
        print(f"Sample package inputs unchanged: {result['samplePackagesUnchanged']}")
    if args.link_baseline:
        print(f"Prior issues remaining: {result['comparison']['existing']}; fixed: {result['comparison']['fixed']}; "
              f"new: {sum(item['count'] for item in result['comparison']['new'])}")
    if (result["manifestErrors"] or result.get("evidenceUnchanged") is False
            or result.get("samplePackagesUnchanged") is False or result.get("comparison", {}).get("new")):
        raise SystemExit(1)


if __name__ == "__main__":
    main()
