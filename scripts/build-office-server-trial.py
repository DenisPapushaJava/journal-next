import argparse
from hashlib import sha256
from pathlib import Path
from tempfile import TemporaryDirectory
from zipfile import ZIP_DEFLATED, ZipFile

from office_server_bundle import ROOT, LOCAL, publish_app

VERSION = "v10"
NAME = f"JournalOfficeServer-ZeroTier-Test-win-x64-{VERSION}"

LAUNCHER = r"""@echo off
setlocal
set "panel=%~dp0app\JournalOfficeServer.exe"
if not exist "%panel%" (
    echo Extract the entire archive before running setup.
    pause
    exit /b 1
)
start "" "%panel%"
"""


def main():
    parser = argparse.ArgumentParser(description="Build the Windows office server test bundle.")
    parser.add_argument("--order-draft", type=Path, help="Enable publishing exactly these draft bytes in this bundle.")
    args = parser.parse_args()
    draft = args.order_draft.read_bytes() if args.order_draft else None
    if draft is not None and not 0 < len(draft) <= 4096:
        parser.error("The test draft must be between 1 and 4096 bytes.")

    LOCAL.mkdir(exist_ok=True)
    archive = LOCAL / f"{NAME}.zip"
    with TemporaryDirectory(prefix="office-server-trial-", dir=LOCAL) as staging:
        source = Path(staging)
        app = source / "app"
        options = {"EnableFilePilot": True}
        if draft is not None:
            options["OrderPilotSha256"] = sha256(draft).hexdigest()
        publish_app(app, VERSION, options)

        bundle_path = source / "bundle.zip"
        with ZipFile(bundle_path, "w", ZIP_DEFLATED, compresslevel=3) as bundle:
            bundle.writestr(f"{NAME}/Setup.cmd", LAUNCHER.replace("\n", "\r\n"))
            bundle.write(ROOT / "docs" / "office-server.md", f"{NAME}/README.md")
            bundle.write(ROOT / "scripts" / "office-work.py", f"{NAME}/client/office-work.py")
            for file in sorted(app.rglob("*")):
                if file.is_file():
                    bundle.write(file, f"{NAME}/{file.relative_to(source).as_posix()}")

        with ZipFile(bundle_path) as bundle:
            required = [
                "Setup.cmd",
                "app/JournalOfficeServer.exe",
                "app/JournalOfficeServer.runtimeconfig.json",
                "app/Jornal.Workflow.Server.dll",
                "app/install-build.txt",
                "app/AccessHost/Jornal.AccessHost.exe",
            ]
            if any(f"{NAME}/{file}" not in bundle.namelist() for file in required) or bundle.testzip() is not None:
                raise RuntimeError("The Windows test bundle is incomplete or damaged.")
        bundle_path.replace(archive)
    print(f"{archive} ({archive.stat().st_size / (1024 ** 2):.1f} MiB)")


if __name__ == "__main__":
    main()
