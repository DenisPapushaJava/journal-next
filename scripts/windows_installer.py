"""Build a pinned offline prerequisite bundle around the per-user Velopack installer."""

import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import urllib.request

ROOT = Path(__file__).resolve().parent.parent
MANIFEST = ROOT / "installer/windows/dependencies.json"


def file_hash(path, algorithm):
    with Path(path).open("rb") as source:
        return hashlib.file_digest(source, algorithm).hexdigest()


def fetch(asset, cache):
    path = Path(cache) / asset["name"]
    if path.name != asset["name"] or not asset["url"].startswith("https://"):
        raise ValueError("Invalid dependency location")
    path.parent.mkdir(parents=True, exist_ok=True)
    if not path.exists() or file_hash(path, asset["algorithm"]) != asset["hash"]:
        temporary = path.with_suffix(".download")
        try:
            with urllib.request.urlopen(asset["url"], timeout=90) as source, temporary.open("wb") as target:
                shutil.copyfileobj(source, target)
            with temporary.open("rb") as source:
                if hashlib.file_digest(source, asset["algorithm"]).hexdigest() != asset["hash"]:
                    raise ValueError("Dependency checksum mismatch: " + path.name)
            temporary.replace(path)
        finally:
            temporary.unlink(missing_ok=True)
    return path


def verify_signature(path, publisher):
    # Перед запуском скачанного компилятора/включением Runtime проверяем и подпись издателя.
    environment = dict(os.environ, JOURNAL_VERIFY_FILE=str(path), JOURNAL_VERIFY_PUBLISHER=publisher)
    # Не смешиваем модули разных версий PowerShell при вложенном запуске из pwsh.
    environment = {key: value for key, value in environment.items() if key.casefold() != "psmodulepath"}
    powershell = shutil.which("pwsh.exe") or "powershell.exe"
    subprocess.run([powershell, "-NoProfile", "-NonInteractive", "-Command",
        "$ErrorActionPreference='Stop'; $s=Get-AuthenticodeSignature -LiteralPath $env:JOURNAL_VERIFY_FILE; "
        "if ($s.Status -ne 'Valid' -or -not $s.SignerCertificate.Subject.Contains($env:JOURNAL_VERIFY_PUBLISHER)) "
        "{ throw 'Invalid dependency signature or publisher' }"], env=environment, check=True)


def prepare_dependencies():
    if os.name != "nt":
        raise ValueError("The Windows offline installer must be built on Windows")
    metadata = json.loads(MANIFEST.read_text(encoding="utf-8"))
    cache = ROOT / "artifacts/installer-dependencies"
    assets = {}
    for key, asset in metadata["files"].items():
        assets[key] = fetch(asset, cache)
        verify_signature(assets[key], asset["publisher"])
    return metadata, assets, cache


def build_offline_installer(app_setup, access_host, output_dir, version):
    metadata, assets, cache = prepare_dependencies()
    compiler_dir = cache / "inno-6.7.3"
    compiler = compiler_dir / "ISCC.exe"
    if not compiler.exists():
        subprocess.run([str(assets["inno"]), "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-",
            "/CURRENTUSER", "/DIR=" + str(compiler_dir)], check=True)
    command = [str(compiler), "/Qp", "/DJournalVersion=" + version,
        "/DAppSetup=" + str(Path(app_setup).resolve()), "/DAccessHost=" + str(Path(access_host).resolve()),
        "/DRuntimeX64=" + str(assets["runtime_x64"].resolve()), "/DRuntimeX86=" + str(assets["runtime_x86"].resolve()),
        "/DRuntimePatch=" + str(metadata["runtime_patch"]), "/O" + str(Path(output_dir).resolve()),
        str(ROOT / "installer/windows/JournalNext.iss")]
    subprocess.run(command, check=True)
    result = Path(output_dir) / f"JournalNext-{version}-win-x64-OfflineSetup.exe"
    if not result.exists():
        raise ValueError("Offline installer was not created")
    result.with_suffix(".exe.sha256").write_text(hashlib.sha256(result.read_bytes()).hexdigest() + "  " + result.name + "\n", encoding="utf-8")
    return result


if __name__ == "__main__":
    import argparse
    parser = argparse.ArgumentParser(description="Подготовить проверенные зависимости OfflineSetup без сборки.")
    parser.add_argument("--download-only", action="store_true", required=True)
    parser.parse_args()
    prepare_dependencies()
