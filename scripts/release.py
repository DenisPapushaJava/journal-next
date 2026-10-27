#!/usr/bin/env python3
"""Windows-only Заявки packaging; all product and repository identities are fixed."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parent.parent
PACKAGE = "DenisPapusha.JournalWorkflow"
REPOSITORY = "https://github.com/DenisPapushaJava/journal-next"
CHANNEL = "workflow-win-x64"
EXECUTABLE = "Jornal.Workflow.Desktop.exe"
TAG_PREFIX = "workflow-v"
FIRST_VERSION = "2026.1.0-alpha.1"


def product(root=ROOT):
    value = json.loads((root / "workflow-product.json").read_text(encoding="utf-8"))
    required = {"productId": PACKAGE, "settingsFolder": "JournalWorkflow", "channelPrefix": "workflow",
                "tagPrefix": TAG_PREFIX, "mainExecutable": EXECUTABLE, "updatesRepository": REPOSITORY}
    if any(value.get(k) != v for k, v in required.items()):
        raise ValueError("Заявки identity/repository differs; refusing another product's release")
    if value.get("publicationEnabled") is not True:
        raise ValueError("Заявки publication is disabled")
    actual = os.environ.get("GITHUB_REPOSITORY")
    if actual and actual.lower() != "denispapushajava/journal-next":
        raise ValueError("This workflow may run only in the Заявки repository")
    return value


def version(value):
    if value.startswith(TAG_PREFIX):
        value = value[len(TAG_PREFIX):]
    if not re.fullmatch(r"202[6-9]\.[1-9]\d*\.(?:0|[1-9]\d*)(?:-(?:alpha|beta|rc)\.[1-9]\d*)?", value):
        raise ValueError("Expected a Заявки calendar version")
    return value


def plan(value, runtime="win-x64", root=ROOT):
    p = product(root)
    value = version(value)
    if runtime != "win-x64":
        raise ValueError("Заявки releases are Windows x64 only")
    return dict(productId=PACKAGE, version=value, tag=TAG_PREFIX + value, channel=CHANNEL,
                feed=f"releases.{CHANNEL}.json", mainExecutable=EXECUTABLE,
                repository=REPOSITORY, displayName=p["displayName"])


def sha(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def release_notes(value, root=ROOT):
    value = version(value)
    if value == FIRST_VERSION:
        return ""  # The published first release intentionally has no notes.
    archived = root / 'docs/releases' / (value + '.md')
    text = (archived if archived.is_file() else root / 'docs/release-notes.md').read_text(encoding='utf-8')
    # The desktop bell displays plain text. Keep the approved wording and numbering.
    text = '\n'.join(line for line in text.splitlines() if not line.startswith('#')).strip().replace('**', '')
    if not text:
        raise ValueError("Release notes must be provided for the next release")
    return text + '\n'


def verify_publish(path):
    required = [EXECUTABLE, "Jornal.Workflow.Desktop.dll", "Jornal.Workflow.Desktop.deps.json",
                "Jornal.Workflow.Desktop.runtimeconfig.json", "AccessHost/Jornal.AccessHost.exe"]
    if any(not (path / p).is_file() for p in required):
        raise ValueError("Published application or x86 AccessHost is incomplete")
    if (path / "Jornal.Desktop.exe").exists() or (path / "Jornal.Desktop.runtimeconfig.json").exists():
        raise ValueError("Legacy Journal entry point must not be shipped")


def verify_release(path, value):
    p = plan(value)
    path = Path(path)
    package_name = f"{PACKAGE}-{p['version']}-{CHANNEL}-full.nupkg"
    setup_name = f"JournalNext-{p['version']}-win-x64-OfflineSetup.exe"
    expected = {package_name, setup_name, p['feed'], "SHA256SUMS"}
    if {f.name for f in path.iterdir()} != expected:
        raise ValueError("Release must contain exactly the Next offline installer, full package, feed and hashes")
    if any(not (path / name).is_file() or (path / name).stat().st_size == 0 for name in expected):
        raise ValueError("Empty or missing release file")
    package = path / package_name
    with zipfile.ZipFile(package) as archive:
        nuspecs = [n for n in archive.namelist() if n.endswith('.nuspec')]
        if len(nuspecs) != 1:
            raise ValueError("Expected one package manifest")
        doc = ET.fromstring(archive.read(nuspecs[0]))
        values = {node.tag.split('}')[-1]: node.text for node in doc.iter()}
        if values.get('id') != PACKAGE or values.get('version') != p['version']:
            raise ValueError("Foreign package ID/version")
        names = archive.namelist()
        if not any(n.endswith('/' + EXECUTABLE) for n in names) or any(n.endswith('/Jornal.Desktop.exe') for n in names):
            raise ValueError("Wrong application entry point in the update package")
    feed = json.loads((path / p['feed']).read_text(encoding="utf-8"))
    assets = feed.get('Assets')
    if not isinstance(assets, list) or len(assets) != 1:
        raise ValueError("Expected one full update asset")
    asset = assets[0]
    expected_asset = dict(PackageId=PACKAGE, Version=p['version'], Type="Full", FileName=package_name,
                          Size=package.stat().st_size)
    if any(asset.get(k) != v for k, v in expected_asset.items()) or str(asset.get('SHA256', '')).lower() != sha(package):
        raise ValueError("Feed identity, channel content or package hash differs")
    if (asset.get('NotesMarkdown') or '').replace('\r\n', '\n').strip() != release_notes(value).strip():
        raise ValueError("Feed release notes differ from the approved text")
    hashes = {}
    for line in (path / "SHA256SUMS").read_text(encoding="utf-8").splitlines():
        digest, name = line.split('  ', 1)
        if name not in expected - {'SHA256SUMS'} or name in hashes or not re.fullmatch('[0-9a-f]{64}', digest):
            raise ValueError("Invalid checksum manifest")
        hashes[name] = digest
    if set(hashes) != expected - {'SHA256SUMS'} or any(sha(path / name) != digest for name, digest in hashes.items()):
        raise ValueError("Release checksum differs")
    return p


def package(value):
    p = plan(value)
    if os.name != 'nt':
        raise ValueError("Windows packaging must run on Windows")
    output = ROOT / 'artifacts/release'
    if output.exists() and any(output.iterdir()):
        raise ValueError("Use a clean release directory; published assets are never silently replaced")
    output.mkdir(parents=True, exist_ok=True)
    commit = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()
    subprocess.run(['dotnet', 'tool', 'restore'], cwd=ROOT, check=True)
    with tempfile.TemporaryDirectory(prefix='journal-next-package-') as temporary:
        temp = Path(temporary)
        staging, updates = temp / 'publish', temp / 'updates'
        common = ['-c', 'Release', '--self-contained', 'false', f"-p:Version={p['version']}",
                  f'-p:SourceRevisionId={commit}', '-p:ContinuousIntegrationBuild=true',
                  '-p:RestoreLockedMode=true', '-p:DebugType=None', '-p:DebugSymbols=false']
        subprocess.run(['dotnet', 'publish', 'src/Jornal.Workflow.Desktop/Jornal.Workflow.Desktop.csproj',
                        '-r', 'win-x64', *common, '-o', str(staging)], cwd=ROOT, check=True)
        subprocess.run(['dotnet', 'publish', 'src/Jornal.AccessHost/Jornal.AccessHost.csproj',
                        '-r', 'win-x86', *common, '-o', str(staging / 'AccessHost')], cwd=ROOT, check=True)
        verify_publish(staging)
        notes = release_notes(value)
        notes_args = []
        if notes:
            notes_path = temp / 'release-notes.md'
            notes_path.write_text(notes, encoding='utf-8', newline='\n')
            notes_args = ['--releaseNotes', str(notes_path)]
        subprocess.run(['dotnet', 'tool', 'run', 'vpk', 'pack', '--packId', PACKAGE,
                        '--packTitle', p['displayName'], '--packAuthors', 'DenisPapusha',
                        '--packVersion', p['version'], '--packDir', str(staging), '--mainExe', EXECUTABLE,
                        '--runtime', 'win-x64', '--channel', CHANNEL, '--outputDir', str(updates),
                        '--noPortable', '--framework', 'net10.0-x64-runtime,net10.0-x86-runtime',
                        '--icon', str(ROOT / 'docs/design/assets/journal-next-icon.ico'), *notes_args], cwd=ROOT, check=True)
        from windows_installer import build_offline_installer
        package_path = updates / f"{PACKAGE}-{p['version']}-{CHANNEL}-full.nupkg"
        feed_path = updates / p['feed']
        for expected in (package_path, feed_path):
            if not expected.is_file():
                raise ValueError(f"Velopack did not create {expected.name}; produced: {sorted(f.name for f in updates.iterdir())}")
        setups = list(updates.glob('*Setup.exe'))
        if len(setups) != 1:
            raise ValueError("Expected one Velopack bootstrap installer")
        (temp / 'installer').mkdir()
        offline = build_offline_installer(setups[0], staging / 'AccessHost', temp / 'installer', p['version'])
        names = [offline, package_path, feed_path]
        for item in names:
            shutil.copy2(item, output / item.name)
        (output / 'SHA256SUMS').write_text(''.join(f'{sha(output / n.name)}  {n.name}\n' for n in sorted(names, key=lambda n: n.name)), encoding='utf-8', newline='\n')
    verify_release(output, value)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=['plan', 'package', 'verify', 'notes'])
    parser.add_argument('--version', required=True)
    parser.add_argument('--directory', default=str(ROOT / 'artifacts/release'))
    parser.add_argument('--github-output')
    parser.add_argument('--notes-output')
    args = parser.parse_args()
    try:
        p = plan(args.version)
        if args.command == 'package': package(args.version)
        if args.command == 'verify': verify_release(args.directory, args.version)
        if args.command == 'notes':
            if not args.notes_output: raise ValueError("--notes-output is required")
            Path(args.notes_output).write_text(release_notes(args.version), encoding='utf-8', newline='\n')
        if args.github_output:
            with open(args.github_output, 'a', encoding='utf-8') as stream:
                stream.write(f"version={p['version']}\ntag={p['tag']}\n")
        print(json.dumps(p, ensure_ascii=True, indent=2))
    except (ValueError, OSError, subprocess.CalledProcessError, zipfile.BadZipFile) as error:
        parser.exit(1, str(error) + '\n')


if __name__ == '__main__': main()
