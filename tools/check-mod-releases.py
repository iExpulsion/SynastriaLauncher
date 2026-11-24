"""Compares each pinned GitHub release download in mods.json with its author's latest release.

Downloads the files (never runs them), and where a release moved to a new tag or replaced its files under the same
tag, writes the new URLs, versions and hashes into mods.json with a higher revision. Writes a Markdown report to the
path given as the first argument, and changed/problems flags to GITHUB_OUTPUT when run in Actions.
"""
import hashlib
import io
import json
import os
import re
import sys
import urllib.error
import urllib.request
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
MODS = ROOT / "src" / "SynastriaLauncher.Core" / "Mods" / "mods.json"
RELEASE_URL = re.compile(r"^https://github\.com/([^/]+/[^/]+)/releases/download/([^/]+)/([^/]+)$")
BRANCH = "mod-updates"


def fetch(url, api=False):
    headers = {"User-Agent": "SynastriaLauncher-mod-check"}
    token = os.environ.get("GH_TOKEN")
    if api:
        headers["Accept"] = "application/vnd.github+json"
        if token:
            headers["Authorization"] = f"Bearer {token}"
    with urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=180) as response:
        return response.read()


def entry_bytes(archive, entry):
    with zipfile.ZipFile(io.BytesIO(archive)) as zf:
        for info in zf.infolist():
            if not info.is_dir() and info.filename.replace("\\", "/").rsplit("/", 1)[-1].lower() == entry.lower():
                return zf.read(info)
    return None


def mod_block(text, mod_id):
    start = text.index(f'"id": "{mod_id}"')
    following = re.compile(r'\n    \{\s*\n\s*"id":').search(text, start)
    return start, following.start() if following else len(text)


def check_mod(mod):
    """Returns (change or None, problem or None) for one mod."""
    linked = [(f, RELEASE_URL.match(f["url"])) for f in mod["files"] if f.get("url")]
    linked = [(f, m) for f, m in linked if m]
    pinned = [(f, m) for f, m in linked if f.get("sha256")]
    sources = {(m.group(1), m.group(2)) for _, m in linked}
    result = (None, None)
    if pinned and len(sources) > 1:
        result = (None, f"downloads come from more than one release ({', '.join(sorted(f'{r} {t}' for r, t in sources))})")
    elif pinned:
        repo, tag = sources.pop()
        try:
            latest = json.loads(fetch(f"https://api.github.com/repos/{repo}/releases/latest", api=True))["tag_name"]
            downloads = {}
            hashes = {}
            missing = []
            for file, match in pinned:
                url = f"https://github.com/{repo}/releases/download/{latest}/{match.group(3)}"
                if url not in downloads:
                    try:
                        downloads[url] = fetch(url)
                    except urllib.error.HTTPError as error:
                        downloads[url] = None
                        missing.append(f"{match.group(3)} (HTTP {error.code})")
                data = downloads[url]
                content = data if data is None or not file.get("entry") else entry_bytes(data, file["entry"])
                if data is not None and content is None:
                    missing.append(f"{file['entry']} in {match.group(3)}")
                if content is not None:
                    hashes[file["sha256"]] = (file["path"], hashlib.sha256(content).hexdigest())
            changed = {old: new for old, new in hashes.items() if old != new[1]}
            if missing:
                result = (None, f"{repo} {latest} is missing " + ", ".join(missing))
            elif latest != tag or changed:
                result = ({"mod": mod, "repo": repo, "old": tag, "new": latest, "hashes": changed}, None)
        except (urllib.error.URLError, KeyError, ValueError, zipfile.BadZipFile) as error:
            result = (None, f"couldn't check {repo}: {error}")
    return result


def apply(text, change):
    mod = change["mod"]
    start, end = mod_block(text, mod["id"])
    block = text[start:end]
    repo, old, new = change["repo"], change["old"], change["new"]
    block = block.replace(f"https://github.com/{repo}/releases/download/{old}/",
                          f"https://github.com/{repo}/releases/download/{new}/")
    for old_hash, (_, new_hash) in change["hashes"].items():
        block = block.replace(old_hash, new_hash)
    if mod.get("version") == old:
        block = block.replace(f'"version": "{old}"', f'"version": "{new}"')
    change["leftover"] = old != new and old in block
    return text[:start] + block + text[end:]


def describe(change):
    mod, repo, old, new = change["mod"], change["repo"], change["old"], change["new"]
    release = f"[{repo} {new}](https://github.com/{repo}/releases/tag/{new})"
    lines = [f"- **{mod['name']}**: " + (f"{old} → {release}" if old != new else f"{release} replaced its files under the same tag")]
    for path, new_hash in change["hashes"].values():
        lines.append(f"  - `{path.rsplit('/', 1)[-1]}` is now `{new_hash}`")
    if old != new and not change["hashes"]:
        lines.append("  - Same files as before, only the tag moved")
    if change["leftover"]:
        lines.append(f"  - Still mentions {old} elsewhere in its entry (details or homepage): check by hand")
    return lines


def main():
    report_path = Path(sys.argv[1]) if len(sys.argv) > 1 else None
    text = MODS.read_text(encoding="utf-8")
    manifest = json.loads(text)
    changes, problems = [], []
    for mod in manifest["mods"]:
        change, problem = check_mod(mod)
        if change:
            changes.append(change)
        if problem:
            problems.append(f"- **{mod['name']}**: {problem}")
    report = []
    if changes:
        for change in changes:
            text = apply(text, change)
        revision = manifest["revision"] + 1
        text = text.replace(f'"revision": {manifest["revision"]},', f'"revision": {revision},', 1)
        updated = json.loads(text)
        if updated["revision"] != revision:
            raise SystemExit("couldn't raise the list revision")
        MODS.write_text(text, encoding="utf-8", newline="")
        repository = os.environ.get("GITHUB_REPOSITORY", "iExpulsion/SynastriaLauncher")
        report += ["Pinned mod downloads changed upstream. Nothing reaches players until the list is merged into main.", ""]
        for change in changes:
            report += describe(change)
        report += [
            "",
            f"Branch [`{BRANCH}`](https://github.com/{repository}/compare/main...{BRANCH}) has mods.json with the new "
            f"pins (list revision {revision}); the Build workflow runs the tests on it.",
            "",
            "To try it first: save that branch's `src/SynastriaLauncher.Core/Mods/mods.json` as "
            "`%LOCALAPPDATA%\\SynastriaLauncher\\mods.json`, reinstall the mod in the launcher and play, then delete "
            f"that file and merge `{BRANCH}` into main.",
        ]
    if problems:
        report += ([""] if report else []) + ["Couldn't check:", ""] + problems
    output = "\n".join(report) + "\n" if report else ""
    if report_path:
        report_path.write_text(output, encoding="utf-8")
    print(output or "Every pinned download matches its author's latest release.")
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as out:
            out.write(f"changed={'true' if changes else 'false'}\nproblems={'true' if problems else 'false'}\n")


main()
