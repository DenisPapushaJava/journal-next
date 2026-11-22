"""Publish the per-user Windows server and its x86 Jet host."""

from hashlib import sha256
import json
from pathlib import Path
from subprocess import run


ROOT = Path(__file__).resolve().parents[1]
LOCAL = ROOT / ".local"
PANEL = "src/Jornal.Workflow.Server.Windows/Jornal.Workflow.Server.Windows.csproj"
ACCESS_HOST = "src/Jornal.AccessHost/Jornal.AccessHost.csproj"


def publish_app(app, version, settings=None):
    for project, runtime, folder in ((PANEL, "win-x64", app), (ACCESS_HOST, "win-x86", app / "AccessHost")):
        run(["dotnet", "publish", project, "-c", "Release", "-r", runtime,
             "--self-contained", "true", "-p:RestoreLockedMode=true", "-p:DebugType=None", "-p:DebugSymbols=false",
             "-o", str(folder)], cwd=ROOT, check=True)
    if settings is not None:
        (app / "appsettings.Production.json").write_text(json.dumps({"OfficeServer": settings}), encoding="ascii")
    checksum = sha256()
    for file in sorted(item for item in app.rglob("*") if item.is_file()):
        checksum.update(file.relative_to(app).as_posix().encode())
        checksum.update(file.read_bytes())
    (app / "install-build.txt").write_text(f"{version}\n{checksum.hexdigest()}\n", encoding="ascii")
