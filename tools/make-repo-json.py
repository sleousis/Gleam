"""Writes the repository file Dalamud reads when someone adds Gleam as a custom plugin repository.

The build already produces a plugin manifest (TidyUp.json). A repository file is that manifest plus the
things only the release knows: where to download the zip, where the icon lives, and when it was published.
Dalamud expects a JSON array, even for one plugin.

    python tools/make-repo-json.py --manifest src/TidyUp/bin/Release/TidyUp/TidyUp.json \
        --tag v1.0.0 --repo savvasleousis/TidyUp --out repo.json
"""
import argparse
import json
import os
import time

ASSET = "Gleam.zip"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--manifest", required=True, help="TidyUp.json produced by the build")
    ap.add_argument("--tag", required=True, help="the release tag, e.g. v1.0.0")
    ap.add_argument("--repo", required=True, help="owner/name on GitHub")
    ap.add_argument("--branch", default="main", help="branch the icon is served from")
    ap.add_argument("--out", required=True)
    args = ap.parse_args()

    with open(args.manifest, encoding="utf-8") as f:
        plugin = json.load(f)

    base = f"https://github.com/{args.repo}"
    download = f"{base}/releases/download/{args.tag}/{ASSET}"

    plugin["IconUrl"] = f"https://raw.githubusercontent.com/{args.repo}/{args.branch}/src/TidyUp/images/icon.png"
    plugin["RepoUrl"] = base
    # Install, update and testing all point at the same build: there is one channel here, not three.
    plugin["DownloadLinkInstall"] = download
    plugin["DownloadLinkUpdate"] = download
    plugin["DownloadLinkTesting"] = download
    plugin["IsHide"] = False
    plugin["IsTestingExclusive"] = False
    plugin["DownloadCount"] = 0
    plugin["LastUpdate"] = int(time.time())

    changelog = os.environ.get("GLEAM_CHANGELOG", "").strip()
    if changelog:
        plugin["Changelog"] = changelog

    with open(args.out, "w", encoding="utf-8", newline="\n") as f:
        json.dump([plugin], f, indent=2, ensure_ascii=False)
        f.write("\n")

    print(f"wrote {args.out} for {plugin['Name']} {plugin['AssemblyVersion']} -> {download}")


if __name__ == "__main__":
    main()
