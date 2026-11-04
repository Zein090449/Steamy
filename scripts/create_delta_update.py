"""Build an incremental app package from the immediately previous release.

The ZIP contains only files whose bytes changed plus delta-manifest.json with
the base version and per-file SHA-256 hashes. The app verifies both the package
checksum and every extracted file before it swaps anything, and it falls back
to the full ZIP whenever the base version does not match.
"""
import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import re
import sys
import urllib.error
import urllib.request
import zipfile
from publish_screenshots import GitHubApi

MANIFEST_NAME = 'delta-manifest.json'


def version_key(tag):
    return tuple(int(part) for part in tag[1:].split('.'))


def safe_name(name):
    parts = name.split('/')
    return bool(name) and not name.startswith(('/', '\\')) and '\\' not in name \
        and '..' not in parts and '' not in parts and not (len(name) > 1 and name[1] == ':')


def archive_hashes(source):
    hashes = {}
    with zipfile.ZipFile(source) as package:
        for entry in package.infolist():
            if entry.is_dir() or entry.filename == MANIFEST_NAME or not safe_name(entry.filename):
                continue
            with package.open(entry) as stream:
                hashes[entry.filename] = (hashlib.sha256(stream.read()).hexdigest(), entry.file_size)
    return hashes


def previous_release(api, tag):
    releases = api('GET', '/releases?per_page=30')
    current = version_key(tag)
    candidates = []
    for release in releases:
        candidate = release.get('tag_name', '')
        if release.get('draft') or release.get('prerelease') or not release.get('published_at'):
            continue
        if not re.fullmatch(r'v\d+\.\d+\.\d+(?:\.\d+)?', candidate) or version_key(candidate) >= current:
            continue
        full_name = f'Steamy-{candidate}.zip'
        for asset in release.get('assets', []):
            if asset.get('name') == full_name:
                candidates.append((version_key(candidate), release, asset))
                break
    if not candidates:
        return None
    _, release, asset = max(candidates, key=lambda item: item[0])
    return release, asset


def download_asset(repository, token, asset):
    request = urllib.request.Request(
        f'https://api.github.com/repos/{repository}/releases/assets/{asset["id"]}',
        headers={'Authorization': 'Bearer ' + token, 'Accept': 'application/octet-stream',
                 'User-Agent': 'Steamy-Updater'})
    with urllib.request.urlopen(request, timeout=600) as response:
        payload = response.read()
    if len(payload) != asset.get('size'):
        raise ValueError('The previous app ZIP was downloaded incompletely')
    return payload


def build(new_archive, old_archive_bytes, base_tag, tag, output):
    old_files = archive_hashes(io.BytesIO(old_archive_bytes))
    new_files = archive_hashes(new_archive)
    changed = sorted(path for path, meta in new_files.items() if old_files.get(path) != meta)
    deleted = sorted(path for path in old_files if path not in new_files)
    manifest = {
        'format': 1,
        'tag': tag,
        'baseTag': base_tag,
        'files': [{'path': path, 'sha256': new_files[path][0], 'size': new_files[path][1]} for path in changed],
        'deleted': deleted,
    }
    if output.exists():
        output.unlink()
    with zipfile.ZipFile(new_archive) as source, \
            zipfile.ZipFile(output, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as target:
        target.writestr(MANIFEST_NAME, json.dumps(manifest, indent=2) + '\n')
        for path in changed:
            target.writestr(source.getinfo(path), source.read(path))
    return manifest


def create(repository, token, version, archive, output):
    tag = 'v' + version
    base = previous_release(GitHubApi(repository, token), tag)
    if base is None:
        return None
    release, asset = base
    return build(archive, download_asset(repository, token, asset), release['tag_name'], tag, output)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--version', required=True)
    parser.add_argument('--archive', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    try:
        result = create(os.environ['GITHUB_REPOSITORY'], os.environ['GITHUB_TOKEN'],
                        args.version, args.archive, args.output)
    except (urllib.error.URLError, ValueError, KeyError, OSError, zipfile.BadZipFile, json.JSONDecodeError) as error:
        # A delta is a shortcut; the verified full ZIP is always published.
        print(f'Incremental package skipped: {error}', file=sys.stderr)
        sys.exit(0)
    if result is None:
        print('No earlier release with an app ZIP was found; only the full package will be published.')
    else:
        print(f"Incremental base {result['baseTag']}: {len(result['files'])} changed files, "
              f"{len(result['deleted'])} removed files.")
