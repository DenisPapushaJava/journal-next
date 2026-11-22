"""Заявки и обмен проектами через частный сервер «Заявки»."""

import argparse
from contextlib import contextmanager
import getpass
from hashlib import sha256
from http.client import HTTPConnection
from ipaddress import IPv4Address
import json
import os
from pathlib import Path
import re
import shutil
import tempfile
from urllib.parse import urlencode, urlsplit
from uuid import UUID, uuid4


COPY_STATE = ".office-copy.json"
WORK = "/api/v1/work/orders"


class Client:
    def __init__(self, address):
        url = urlsplit(address)
        if url.scheme != "http" or url.username or url.password or url.path not in ("", "/") or url.query or url.fragment:
            raise ValueError("Укажите приватный HTTP-адрес сервера.")
        host = IPv4Address(url.hostname)
        if not host.is_private or url.port is None:
            raise ValueError("Укажите приватный IPv4 и порт из панели сервера.")
        self.address = f"http://{host}:{url.port}"
        self.host, self.port = str(host), url.port
        key = getpass.getpass("Ключ API: ").strip()
        if not re.fullmatch(r"[A-Fa-f0-9]{64}", key):
            raise ValueError("Проверьте ключ API.")
        self.authorization = "Bearer " + key

    @contextmanager
    def request(self, method, path, body=None, **headers):
        connection = HTTPConnection(self.host, self.port, timeout=100)
        try:
            connection.request(method, path, body, {"Authorization": self.authorization, **headers})
            with connection.getresponse() as response:
                if response.status != 200:
                    try:
                        problem = json.loads(response.read())
                        error = problem.get("error", "")
                        if backup := problem.get("backup"):
                            error += f". Проверьте копию .office-sync/{backup} на сервере"
                    except (ValueError, AttributeError):
                        error = ""
                    raise RuntimeError(f"Сервер вернул HTTP {response.status} {error}".rstrip())
                yield response
        finally:
            connection.close()

    def json(self, method, path, body=None):
        headers = {} if body is None else {"Content-Type": "application/json"}
        with self.request(method, path, None if body is None else json.dumps(body).encode(), **headers) as response:
            return json.load(response)


def file_name_allowed(name):
    return bool(name) and not name.endswith((".", " ")) and not re.search(r'[\\/:<>"|?*\x00-\x1f]', name) and not re.match(
        r'^(?:CON|PRN|AUX|NUL|(?:COM|LPT)[1-9¹²³]) *(?:\.|$)', name, re.I
    ) and not name.lower().startswith((".workflow-", ".journal-next-", ".office-", "~$")) and name.lower() != "office-pilot.txt"


def local_file(root, relative):
    parts = relative.split("/")
    if not all(file_name_allowed(part) for part in parts):
        raise ValueError(f"Недопустимое имя: {relative}")
    child = root
    for part in parts:
        child /= part
        if child.is_symlink() or getattr(child, "is_junction", lambda: False)():
            raise ValueError(f"В проекте обнаружена ссылка: {relative}")
    return child


def fingerprint(path):
    with path.open("rb") as stream:
        checksum = sha256()
        while chunk := stream.read(1024 * 1024):
            checksum.update(chunk)
        return checksum.hexdigest().upper()


def project_route(order, project):
    return f"{WORK}/{UUID(str(order))}/projects/{UUID(str(project))}"


def file_route(route, relative):
    return route + "/file?" + urlencode({"path": relative})


def save_state(root, state):
    with tempfile.NamedTemporaryFile("w", encoding="utf-8", prefix=".office-state-", dir=root, delete=False) as output:
        temporary = Path(output.name)
        try:
            json.dump(state, output, ensure_ascii=False, indent=2)
            output.flush()
            os.fsync(output.fileno())
        except BaseException:
            output.close()
            temporary.unlink(missing_ok=True)
            raise
    try:
        os.replace(temporary, root / COPY_STATE)
    finally:
        temporary.unlink(missing_ok=True)


def download(client, order, project, target):
    route = project_route(order, project)
    if target.exists() or target.is_symlink():
        raise ValueError("Выберите новое имя папки для локальной копии.")
    manifest = client.json("GET", route + "/files")
    baseline = {entry["path"]: entry["sha256"] for entry in manifest["files"]}
    if len({path.casefold() for path in baseline}) != len(manifest["files"]):
        raise ValueError("На сервере совпадают имена файлов без учёта регистра.")
    target.parent.mkdir(parents=True, exist_ok=True)
    stage = Path(tempfile.mkdtemp(prefix=".office-download-", dir=target.parent))
    try:
        for relative in manifest["directories"]:
            local_file(stage, relative).mkdir(parents=True, exist_ok=True)
        for entry in manifest["files"]:
            relative = entry["path"]
            path = local_file(stage, relative)
            path.parent.mkdir(parents=True, exist_ok=True)
            print(f"Скачиваю {relative}", flush=True)
            with client.request("GET", file_route(route, relative)) as response, path.open("xb") as output:
                shutil.copyfileobj(response, output, 1024 * 1024)
            if path.stat().st_size != entry["length"] or fingerprint(path) != entry["sha256"]:
                raise RuntimeError(f"Файл изменился при скачивании: {relative}. Повторите скачивание.")
        save_state(stage, {"address": client.address, "order": str(order), "project": str(project), "files": baseline})
        if target.exists() or target.is_symlink():
            raise ValueError("Папка для локальной копии уже существует.")
        stage.rename(target)
    finally:
        if stage.is_dir():
            shutil.rmtree(stage)
    print(f"Локальная копия готова: {target.absolute()}")


def publish(client, root):
    if root.is_symlink() or getattr(root, "is_junction", lambda: False)():
        raise ValueError("Выберите обычную папку локальной копии.")
    state = json.loads((root / COPY_STATE).read_text(encoding="utf-8"))
    if state["address"] != client.address:
        raise ValueError("Эта копия была скачана с другого сервера.")
    route = project_route(state["order"], state["project"])
    baseline = state["files"]
    manifest = client.json("GET", route + "/files")
    on_server = {entry["path"].casefold(): entry["sha256"] for entry in manifest["files"]}
    canonical = {path.casefold(): path for path in baseline}
    canonical.update((entry["path"].casefold(), entry["path"]) for entry in manifest["files"])
    previous = {path.casefold(): digest for path, digest in baseline.items()}
    local = {}
    for folder, directories, files in os.walk(root):
        directories[:] = [name for name in directories if file_name_allowed(name)]
        for name in directories + files:
            if not file_name_allowed(name):
                continue
            relative = (Path(folder) / name).relative_to(root).as_posix()
            path = local_file(root, relative)
            if name in files:
                if relative.casefold() in local:
                    raise ValueError(f"Совпадают имена без учёта регистра: {relative}")
                local[relative.casefold()] = (relative, path, fingerprint(path))
    changes = [item for key, item in local.items() if previous.get(key) != item[2]]
    conflicts = [relative for relative, _, checksum in changes
                 if on_server.get(relative.casefold()) not in (previous.get(relative.casefold()), checksum)]
    if conflicts:
        raise RuntimeError("На сервере изменились: " + ", ".join(conflicts) + ". Локальные копии сохранены.")
    for relative, path, checksum in changes:
        key = relative.casefold()
        relative = canonical.get(key, relative)
        if on_server.get(key) != checksum:
            expected = previous.get(key)
            condition = {"If-None-Match": "*"} if expected is None else {"If-Match": f'"{expected}"'}
            with path.open("rb") as stream:
                length = os.fstat(stream.fileno()).st_size
                try:
                    with client.request("PUT", file_route(route, relative), stream, **condition,
                                        **{"Content-Length": str(length)}) as response:
                        saved = json.load(response)
                except RuntimeError as error:
                    raise RuntimeError(f"{relative}: {error}") from error
            if saved["sha256"] != checksum:
                raise RuntimeError(f"Файл менялся во время отправки: {relative}. Сверьте обе версии.")
        baseline[relative] = checksum
        save_state(root, state)
        print(f"Подтверждён {relative}", flush=True)
    if not changes:
        print("Локальных изменений для отправки нет.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("address", help="Приватный адрес из панели сервера")
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("list", help="Показать последние заявки и их ID")
    detail = commands.add_parser("order", help="Показать реквизиты и ID проектов")
    detail.add_argument("order", type=UUID)
    create = commands.add_parser("new", help="Создать и отправить пустую заявку")
    create.add_argument("--manager", required=True)
    create.add_argument("--customer", required=True)
    create.add_argument("--draft", required=True, type=Path, help="Сохранить данные и ID для повторной отправки")
    draft = commands.add_parser("send-order", help="Отправить ранее сохранённую заявку")
    draft.add_argument("draft", type=Path)
    edit = commands.add_parser("edit", help="Обновить менеджера и БЛЦБ")
    edit.add_argument("order", type=UUID)
    edit.add_argument("--manager", required=True)
    edit.add_argument("--blcb")
    pull = commands.add_parser("download", help="Скачать проект в новую папку")
    pull.add_argument("order", type=UUID)
    pull.add_argument("project", type=UUID)
    pull.add_argument("folder", type=Path)
    push = commands.add_parser("publish", help="Отправить изменённые и добавленные файлы")
    push.add_argument("folder", type=Path)
    args = parser.parse_args()
    try:
        client = Client(args.address)
        match args.command:
            case "list":
                result = client.json("GET", WORK)["orders"]
            case "order":
                result = client.json("GET", f"{WORK}/{args.order}")
            case "new" | "send-order":
                if args.command == "new":
                    body = {"id": str(uuid4()), "manager": args.manager, "customer": args.customer}
                    with args.draft.open("x", encoding="utf-8") as output:
                        json.dump(body, output, ensure_ascii=False)
                else:
                    body = json.loads(args.draft.read_text(encoding="utf-8"))
                result = client.json("POST", WORK, body)
            case "edit":
                order = f"{WORK}/{args.order}"
                current = client.json("GET", order)
                blcb = current["blcb"] if args.blcb is None else args.blcb
                result = client.json("PUT", order, {"version": current["version"], "manager": args.manager, "blcb": blcb})
            case "download":
                return download(client, args.order, args.project, args.folder)
            case "publish":
                return publish(client, args.folder)
        print(json.dumps(result, ensure_ascii=False, indent=2))
    except (OSError, RuntimeError, ValueError) as error:
        parser.exit(1, f"Операция остановлена. {error}\n")


if __name__ == "__main__":
    main()
