"""Проверка обмена файлами и чтение журнала сервера «Заявки»."""

import argparse
from datetime import date
import getpass
from http.client import HTTPConnection, RemoteDisconnected
from ipaddress import IPv4Address
import json
from pathlib import Path
import re
import socket
import tempfile
from urllib.parse import urlsplit
from uuid import UUID


def run(address, requested_order, requested_date=None, diagnostics_only=False, draft_file=None):
    url = urlsplit(address)
    if url.scheme != "http" or url.path not in ("", "/") or url.query or url.fragment or url.username is not None:
        raise ValueError("Укажи приватный HTTP-адрес сервера из панели Windows.")
    host = IPv4Address(url.hostname)
    if not host.is_private or url.port is None:
        raise ValueError("Укажи приватный IPv4-адрес и порт сервера.")
    key = getpass.getpass("Ключ API: ").strip()
    if not re.fullmatch(r"[0-9a-fA-F]{64}", key):
        raise ValueError("Ключ API должен содержать 64 шестнадцатеричных символа.")
    auth = {"Authorization": "Bearer " + key}

    def request(method, path, body=None, version=None, status=200):
        headers = auth if version is None else {**auth, "If-Match": version}
        connection = HTTPConnection(str(host), url.port, timeout=100 if method == "POST" else 55)
        try:
            connection.request(method, path, body, headers)
            with connection.getresponse() as response:
                data = response.read()
                if response.status != status:
                    try:
                        error = json.loads(data).get("error", "")
                    except (AttributeError, ValueError):
                        error = ""
                    raise RuntimeError(f"{method} вернул HTTP {response.status} вместо {status} {error}".rstrip())
                return data, response.getheader("ETag")
        finally:
            connection.close()

    def download(path):
        data, version = request("GET", path)
        if version is None or not re.fullmatch(r'"[A-F0-9]{64}"', version):
            raise RuntimeError("Сервер не вернул версию файла.")
        return data, version

    def interrupt(path, version):
        connection = HTTPConnection(str(host), url.port, timeout=55)
        try:
            connection.putrequest("PUT", path)
            for name, value in auth.items():
                connection.putheader(name, value)
            connection.putheader("If-Match", version)
            connection.putheader("Content-Length", "8192")
            connection.endheaders(b"only the beginning of the file")
            connection.sock.shutdown(socket.SHUT_WR)
            try:
                with connection.getresponse() as response:
                    if response.status != 400:
                        raise RuntimeError(f"При обрыве передачи получен неожиданный HTTP {response.status}.")
            except (RemoteDisconnected, ConnectionResetError):
                pass
        finally:
            connection.close()

    if diagnostics_only:
        data, _ = request("GET", "/api/v1/diagnostics")
        print(json.dumps(json.loads(data), ensure_ascii=False, indent=2))
        return

    if draft_file is not None:
        body = draft_file.read_bytes()
        draft = json.loads(body)
        data, _ = request("POST", "/api/v1/order-pilot", body)
        receipt = json.loads(data)
        data, _ = request("GET", "/api/v1/orders")
        confirmed = [row for row in json.loads(data)["orders"] if row["id"] == draft["id"]]
        if receipt["id"] != draft["id"] or len(confirmed) != 1 or confirmed[0]["number"] != receipt["number"]:
            raise RuntimeError("Сервер принял запрос, но заявку пока не удалось подтвердить в списке.")
        print(f"Подтверждена заявка {receipt['number']} · {confirmed[0]['customer']}")
        return

    data, _ = request("GET", "/api/v1/orders")
    page = json.loads(data)
    orders = page["orders"]
    if requested_date is not None:
        if page.get("hasMore"):
            raise ValueError("Список заявок неполный. Укажи точный номер тестовой заявки.")
        orders = [row for row in orders if row["number"].startswith(f"{requested_date}-")]
    else:
        if not requested_order:
            print("Последние заявки: " + ", ".join(row["number"] for row in orders))
            requested_order = input("Номер тестовой заявки: ").strip()
        orders = [row for row in orders if row["number"] == requested_order]
    if not orders:
        raise ValueError("Тестовая заявка не найдена среди последних.")

    candidates = []
    for order in orders:
        pilot = f"/api/v1/orders/{UUID(order['id'])}/file-pilot"
        data, _ = request("GET", pilot)
        candidates.extend((order, pilot, project) for project in json.loads(data)["projects"])
    if len(candidates) != 1:
        raise ValueError(f"Подходящих проектов с office-pilot.txt: {len(candidates)}. Для проверки нужен один.")
    order, pilot, project = candidates[0]
    path = f"{pilot}/{UUID(project['id'])}"
    filename = "office-pilot.txt"
    print(f"Заявка {order['number']}, проект {project['name']}")

    original, original_version = download(path)
    local = Path(tempfile.mkdtemp(prefix="office-file-pilot-"))
    (local / "исходный.txt").write_bytes(original)
    print(f"Копии для сравнения: {local}")

    edited = (original.decode("utf-8-sig", errors="replace") + "\nПравка с другого компьютера.\n").encode("utf-8")
    (local / filename).write_bytes(edited)
    request("PUT", path, edited, original_version, 204)
    saved, current_version = download(path)
    if saved != edited or current_version == original_version:
        raise RuntimeError("Серверный файл не совпал с отправленным.")
    print("Скачивание и возврат правки: успешно.")

    stale = (local / "параллельная-правка.txt")
    stale.write_text("Правка, сделанная на старой версии файла.\n", encoding="utf-8")
    request("PUT", path, stale.read_bytes(), original_version, 412)
    if download(path) != (saved, current_version):
        raise RuntimeError("После конфликта изменился серверный файл.")
    print("Конфликт версий: серверный файл и локальная правка сохранены.")

    interrupt(path, current_version)
    if download(path) != (saved, current_version):
        raise RuntimeError("После обрыва изменился серверный файл.")
    retried = edited + "Повторная отправка после обрыва.\n".encode("utf-8")
    (local / filename).write_bytes(retried)
    request("PUT", path, retried, current_version, 204)
    if download(path)[0] != retried:
        raise RuntimeError("Не удалось подтвердить повторную отправку.")
    print("Обрыв и повторная отправка: успешно.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("address", help="Приватный HTTP-адрес сервера из панели Windows")
    selection = parser.add_mutually_exclusive_group()
    selection.add_argument("--order", help="Номер тестовой заявки")
    selection.add_argument("--date", type=date.fromisoformat, help="Дата тестовой заявки (ГГГГ-ММ-ДД)")
    selection.add_argument("--diagnostics", action="store_true", help="Показать версию сервера и последние ошибки")
    selection.add_argument("--draft", type=Path, help="Передать тестовую заявку из подготовленного JSON")
    args = parser.parse_args()
    try:
        run(args.address, args.order, args.date, args.diagnostics, args.draft)
    except (ValueError, RuntimeError, OSError) as error:
        parser.exit(1, f"Проверка остановлена. {error}\n")
