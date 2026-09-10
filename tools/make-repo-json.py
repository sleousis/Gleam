"""Writes the repository file Dalamud reads when someone adds Gleam as a custom plugin repository.

The build already produces a plugin manifest (Gleam.json). A repository file is that manifest plus the
things only the release knows: where to download the zip, where the icon lives, and when it was published.
Dalamud expects a JSON array, even for one plugin.

    python tools/make-repo-json.py --manifest src/Gleam/bin/Release/Gleam/Gleam.json \
        --tag v1.0.0 --repo sleousis/Gleam --changelog CHANGELOG.md --out repo.json

The version's section of CHANGELOG.md becomes the changelog Dalamud shows beside the update. With
--notes-out the same section is also written on its own, for the GitHub release page.
"""
import argparse
import json
import time

ASSET = "Gleam.zip"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--manifest", required=True, help="Gleam.json produced by the build")
    ap.add_argument("--tag", required=True, help="the release tag, e.g. v1.0.0")
    ap.add_argument("--repo", required=True, help="owner/name on GitHub")
    ap.add_argument("--branch", default="main", help="branch the icon is served from")
    ap.add_argument("--changelog", help="CHANGELOG.md with one '## <version>' section per release")
    ap.add_argument("--notes-out", help="write only this version's changelog section to this file")
    ap.add_argument("--out", help="the repository file to write")
    args = ap.parse_args()
    if not args.out and not args.notes_out:
        ap.error("give --out, --notes-out, or both")

    with open(args.manifest, encoding="utf-8") as f:
        plugin = json.load(f)

    base = f"https://github.com/{args.repo}"
    download = f"{base}/releases/download/{args.tag}/{ASSET}"

    plugin["IconUrl"] = f"https://raw.githubusercontent.com/{args.repo}/{args.branch}/src/Gleam/images/icon.png"
    plugin["RepoUrl"] = base
    # Install, update and testing all point at the same build: there is one channel here, not three.
    plugin["DownloadLinkInstall"] = download
    plugin["DownloadLinkUpdate"] = download
    plugin["DownloadLinkTesting"] = download
    plugin["IsHide"] = False
    plugin["IsTestingExclusive"] = False
    plugin["DownloadCount"] = 0
    plugin["LastUpdate"] = int(time.time())

    changelog = section(args.changelog, plugin["AssemblyVersion"]) if args.changelog else ""
    if changelog:
        plugin["Changelog"] = changelog

    if args.notes_out:
        with open(args.notes_out, "w", encoding="utf-8", newline="\n") as f:
            f.write(changelog + "\n")
        print(f"wrote {args.notes_out} ({len(changelog.splitlines())} lines)")

    if args.out:
        with open(args.out, "w", encoding="utf-8", newline="\n") as f:
            json.dump([plugin], f, indent=2, ensure_ascii=False)
            f.write("\n")
        print(f"wrote {args.out} for {plugin['Name']} {plugin['AssemblyVersion']} -> {download}")


def section(path, version):
    """The text under '## <version>', matching 0.9.5 as well as 0.9.5.0. Empty when there is none."""
    wanted = {version, ".".join(version.split(".")[:3])}
    body, inside = [], False
    with open(path, encoding="utf-8") as f:
        for line in f.read().splitlines():
            if line.startswith("## "):
                if inside:
                    break
                inside = line[3:].strip() in wanted
                continue
            if inside:
                body.append(line)
    return "\n".join(body).strip()


if __name__ == "__main__":
    main()
