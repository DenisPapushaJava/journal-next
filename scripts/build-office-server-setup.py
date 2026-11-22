"""Build the Windows x64 per-user office server installer with NSIS."""

import argparse
from pathlib import Path, PureWindowsPath
import shutil
from subprocess import run
from tempfile import TemporaryDirectory

from office_server_bundle import LOCAL, ROOT, publish_app


VERSION = 19
NAME = f"JournalOfficeServer-Setup-win-x64-v{VERSION}.exe"


def removal_script(app):
    files = sorted(item.relative_to(app) for item in app.rglob("*") if item.is_file())
    directories = sorted({parent for item in files for parent in item.parents if parent != Path(".")},
                         key=lambda path: (-len(path.parts), path.as_posix()))
    instructions = []
    for file in files:
        path = PureWindowsPath(file)
        instructions.append(f'Delete "$INSTDIR\\app\\{path}"')
    instructions.append("IfErrors remove_failed")
    for directory in directories:
        path = PureWindowsPath(directory)
        instructions.append(f'RMDir "$INSTDIR\\app\\{path}"')
    return "\n".join(instructions) + "\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--makensis", default=shutil.which("makensis"), help="Path to the NSIS compiler")
    args = parser.parse_args()
    if args.makensis is None:
        parser.error("Install NSIS or pass --makensis.")
    LOCAL.mkdir(exist_ok=True)
    with TemporaryDirectory(prefix="office-server-setup-", dir=LOCAL) as staging:
        stage = Path(staging)
        app = stage / "app"
        publish_app(app, f"v{VERSION}", {"EnableFilePilot": False})
        (app / "client").mkdir()
        shutil.copyfile(ROOT / "scripts/office-work.py", app / "client/office-work.py")
        shutil.copyfile(ROOT / "docs/office-server.md", app / "README.md")
        remove = stage / "remove-files.nsh"
        remove.write_text(removal_script(app), encoding="utf-8")
        setup = stage / NAME
        arguments = [args.makensis, "-INPUTCHARSET", "UTF8", "-WX", f"-DAPP_VERSION={VERSION}", f"-DPAYLOAD={app}",
                     f"-DREMOVE_FILES={remove}", f"-DSETUP_FILE={setup}", str(ROOT / "installer/windows/JournalOfficeServer.nsi")]
        run(arguments, cwd=ROOT, check=True)
        with setup.open("rb") as result:
            if result.read(2) != b"MZ":
                raise RuntimeError("The installer is not a Windows executable.")
        setup.replace(LOCAL / NAME)
    print(LOCAL / NAME)


if __name__ == "__main__":
    main()
