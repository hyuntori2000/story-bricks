#!/usr/bin/env python3
"""Read-only, standard-library policy check for design branches.

Run trusted base-branch code in CI. Never execute code from the pull request.
Both source and destination names of renames/copies remain subject to policy.
"""
from __future__ import annotations

import argparse
from dataclasses import dataclass
import os
import re
import subprocess
import sys

ALLOWED_PREFIXES = (
    "Assets/DesignerWorkspace/",
    "Dashboard/",
    "Design/",
)
GUID = re.compile(rb"(?m)^guid:\s*([0-9a-fA-F]{32})\s*$")


@dataclass(frozen=True)
class Change:
    status: str
    paths: tuple[str, ...]


def parse_name_status(data: bytes) -> list[Change]:
    """Parse git --name-status -z without splitting filenames at whitespace."""
    if not data:
        return []
    if not data.endswith(b"\0"):
        raise ValueError("Git diff output is not NUL-terminated")
    fields = data[:-1].split(b"\0")
    result, index = [], 0
    while index < len(fields):
        status = fields[index].decode("ascii")
        index += 1
        if not re.fullmatch(r"(?:[ADMUTXB]|[RC][0-9]{1,3})", status):
            raise ValueError("Unsupported Git diff status")
        count = 2 if status[0] in "RC" else 1
        if index + count > len(fields):
            raise ValueError("Git diff has an incomplete rename/copy record")
        paths = tuple(os.fsdecode(value) for value in fields[index:index + count])
        if any(not path for path in paths):
            raise ValueError("Git diff contains an empty path")
        result.append(Change(status, paths))
        index += count
    return result


def is_design_branch(branch: str) -> bool:
    return branch.removeprefix("refs/heads/").startswith("design/")


def allowed_path(path: str) -> bool:
    if path.startswith("/") or "\\" in path or any(part in ("", ".", "..") for part in path.split("/")):
        return False
    # A designer cannot weaken an instruction file inside an otherwise owned folder.
    if path.rsplit("/", 1)[-1] in ("AGENTS.md", "CODEOWNERS"):
        return False
    return path.startswith(ALLOWED_PREFIXES)


def check_changes(changes: list[Change]) -> list[str]:
    errors = []
    for change in changes:
        if change.status[0] in "UTXB":
            errors.append("Unsupported/unresolved change %s: %r" % (change.status, change.paths))
        for path in change.paths:
            if not allowed_path(path):
                errors.append("Protected path (%s): %r" % (change.status, path))
        if change.status == "D" and change.paths[0].endswith(".meta"):
            errors.append("Existing Unity metadata may not be deleted: %r" % change.paths[0])
        if change.status[0] == "R" and change.paths[0].endswith(".meta") != change.paths[1].endswith(".meta"):
            errors.append("A metadata rename must preserve the .meta file: %r" % (change.paths,))
    return errors


def check_meta_guids(changes: list[Change], read_blob) -> list[str]:
    errors = []
    for change in changes:
        if change.status[0] not in "MRC" or not change.paths[-1].endswith(".meta"):
            continue
        before = GUID.search(read_blob("base", change.paths[0]))
        after = GUID.search(read_blob("head", change.paths[-1]))
        if before is None or after is None:
            errors.append("Unity metadata GUID is unverifiable: %r" % (change.paths,))
        elif change.status[0] == "C":
            if before.group(1).lower() == after.group(1).lower():
                errors.append("A copied Unity asset must not reuse the source GUID: %r" % (change.paths,))
        elif before.group(1).lower() != after.group(1).lower():
            errors.append("Existing Unity metadata GUID changed or is unverifiable: %r" % (change.paths,))
    return errors


def git(*args: str) -> bytes:
    return subprocess.run(["git", *args], check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE).stdout


def commit(ref: str) -> str:
    value = git("rev-parse", "--verify", "--end-of-options", ref + "^{commit}").strip().decode("ascii")
    if not re.fullmatch(r"[0-9a-f]{40,64}", value):
        raise ValueError("Expected a Git commit identifier")
    return value


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--design", action="store_true", help="Enforce design scope regardless of branch name")
    parser.add_argument("--branch", help="PR source branch; defaults to the current Git branch")
    parser.add_argument("--base", help="Trusted base revision, normally origin/main or a PR base SHA")
    parser.add_argument("--head", default="HEAD", help="Revision being reviewed")
    args = parser.parse_args(argv)
    try:
        branch = args.branch if args.branch is not None else git("branch", "--show-current").decode().strip()
        if not args.design and not is_design_branch(branch):
            print("Design boundary: not a design/* branch. Protected changes still require project-owner review through CODEOWNERS.")
            return 0
        if not args.base:
            raise ValueError("Design checks require --base with the trusted base revision")
        base, head = commit(args.base), commit(args.head)
        ancestor = git("merge-base", base, head).strip().decode("ascii")
        changes = parse_name_status(git("diff", "--no-ext-diff", "--no-textconv", "--name-status", "-z", "--find-renames", "--find-copies", ancestor, head, "--"))
        errors = check_changes(changes)
        errors.extend(check_meta_guids(changes, lambda side, path: git("show", (ancestor if side == "base" else head) + ":" + path)))
        for change in changes:
            if change.status != "D":
                entry = git("ls-tree", "-z", head, "--", change.paths[-1])
                if entry.startswith((b"120000 ", b"160000 ")):
                    errors.append("Design changes may not introduce or modify symlinks or submodules: %r" % change.paths[-1])
        if errors:
            print("Design boundary FAILED. Keep the design change in its owned folders or request an owner-reviewed core change.")
            for error in errors:
                print("- " + error)
            return 1
        print("Design boundary passed: %d change(s) stay inside owned presentation paths; existing metadata GUIDs are preserved." % len(changes))
        return 0
    except (OSError, ValueError, UnicodeError, subprocess.CalledProcessError) as error:
        print("Design boundary could not verify the change (%s). Check the trusted base/head revisions and retry; this is a failure, not a bypass." % type(error).__name__, file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
