from transport.legacy.unity_connection import UnityConnection
from transport.legacy.port_discovery import PortDiscovery
from core.bridge_handshake import BRIDGE_PROTOCOL_VERSION, BridgeCompatibilityError
from core.telemetry import get_package_version
import sys
import json
import struct
import socket
import threading
import time
import select
from pathlib import Path

import pytest

# locate server src dynamically to avoid hardcoded layout assumptions
ROOT = Path(__file__).resolve().parents[2]  # tests/integration -> tests -> Server
candidates = [
    ROOT / "src",
]
SRC = next((p for p in candidates if p.exists()), None)
if SRC is None:
    searched = "\n".join(str(p) for p in candidates)
    pytest.skip(
        "MCP for Unity server source not found. Tried:\n" + searched,
        allow_module_level=True,
    )
# Tests can now import directly from parent package


UNITY_PACKAGE = {
    "version": get_package_version(),
    "registered_resources": ["editor_state", "tool_states"],
    "tool_groups": ["core", "testing"],
}


def compatible_greeting(extra: str = "") -> bytes:
    return (
        f"WELCOME UNITY-MCP {BRIDGE_PROTOCOL_VERSION} FRAMING=1 "
        f"BRIDGE_PROTOCOL={BRIDGE_PROTOCOL_VERSION} "
        f"UNITY_PACKAGE={get_package_version()} "
        "RESOURCES=editor_state,tool_states TOOL_GROUPS=core,testing "
        f"{extra}\n"
    ).encode("ascii")


def start_dummy_server(greeting: bytes, respond_ping: bool = False):
    """Start a minimal TCP server for handshake tests."""
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    sock.bind(("127.0.0.1", 0))
    sock.listen(1)
    port = sock.getsockname()[1]
    ready = threading.Event()

    def _run():
        ready.set()
        conn, _ = sock.accept()
        conn.settimeout(1.0)
        if greeting:
            conn.sendall(greeting)
        if b"BRIDGE_PROTOCOL=" in greeting:
            try:
                # Read exactly n bytes helper
                def _read_exact(n: int) -> bytes:
                    buf = b""
                    while len(buf) < n:
                        chunk = conn.recv(n - len(buf))
                        if not chunk:
                            break
                        buf += chunk
                    return buf

                header = _read_exact(8)
                if len(header) == 8:
                    length = struct.unpack(">Q", header)[0]
                    payload = _read_exact(length)
                    request = json.loads(payload.decode("utf-8"))
                    server_handshake = request["params"]["handshake"]
                    response = json.dumps({
                        "status": "success",
                        "result": {
                            "bridge_protocol_version": BRIDGE_PROTOCOL_VERSION,
                            "python_server": server_handshake["python_server"],
                            "unity_package": UNITY_PACKAGE,
                        },
                    }).encode("utf-8")
                    conn.sendall(struct.pack(">Q", len(response)) + response)

                if respond_ping:
                    header = _read_exact(8)
                    length = struct.unpack(">Q", header)[0]
                    payload = _read_exact(length)
                    if payload == b'{"type":"ping"}':
                        resp = b'{"type":"pong"}'
                        conn.sendall(struct.pack(">Q", len(resp)) + resp)
                    elif payload == b"ping":
                        resp = b'{"status":"success","result":{"message":"pong"}}'
                        conn.sendall(struct.pack(">Q", len(resp)) + resp)
            except Exception:
                pass
        time.sleep(0.1)
        try:
            conn.close()
        except Exception:
            pass
        finally:
            sock.close()

    threading.Thread(target=_run, daemon=True).start()
    ready.wait()
    return port


def start_handshake_enforcing_server():
    """Server that drops connection if client sends data before handshake."""
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    sock.bind(("127.0.0.1", 0))
    sock.listen(1)
    port = sock.getsockname()[1]
    ready = threading.Event()

    def _run():
        ready.set()
        conn, _ = sock.accept()
        # If client sends any data before greeting, disconnect (poll briefly)
        try:
            conn.setblocking(False)
            deadline = time.time() + 0.15  # short, reduces race with legitimate clients
            while time.time() < deadline:
                r, _, _ = select.select([conn], [], [], 0.01)
                if r:
                    try:
                        peek = conn.recv(1, socket.MSG_PEEK)
                    except BlockingIOError:
                        peek = b""
                    except Exception:
                        peek = b"\x00"
                    if peek:
                        conn.close()
                        sock.close()
                        return
            # No pre-handshake data observed; send greeting
            conn.setblocking(True)
            conn.sendall(b"MCP/0.1 FRAMING=1\n")
            time.sleep(0.1)
        finally:
            try:
                conn.close()
            finally:
                sock.close()

    threading.Thread(target=_run, daemon=True).start()
    ready.wait()
    return port


def test_handshake_requires_framing():
    port = start_dummy_server(b"MCP/0.1\n")
    conn = UnityConnection(host="127.0.0.1", port=port)
    assert conn.connect() is False
    assert conn.sock is None


def test_handshake_rejects_legacy_bridge_protocol_clearly():
    greeting = (
        "WELCOME UNITY-MCP 1 FRAMING=1 BRIDGE_PROTOCOL=1 "
        f"UNITY_PACKAGE={get_package_version()} "
        "RESOURCES=editor_state TOOL_GROUPS=core\n"
    ).encode("ascii")
    port = start_dummy_server(greeting)
    conn = UnityConnection(host="127.0.0.1", port=port)
    assert conn.connect() is False
    assert "Bridge protocol mismatch" in conn.last_error
    assert "requires 2" in conn.last_error


def test_handshake_rejects_wrong_project_instance():
    # A stale port registry can point at a different project's editor (or a
    # reused port). The banner's PROJECT token must be checked against the
    # instance we were aimed at, and the mismatch must fail the connection.
    port = start_dummy_server(
        compatible_greeting("PROJECT=deadbeef PORT=6400"))
    conn = UnityConnection(host="127.0.0.1", port=port,
                           instance_id="MyProject@3a9e429a")
    assert conn.connect() is False
    assert conn.sock is None


def test_handshake_accepts_matching_project_instance():
    port = start_dummy_server(
        compatible_greeting("PROJECT=3a9e429a PORT=6400"))
    conn = UnityConnection(host="127.0.0.1", port=port,
                           instance_id="MyProject@3a9e429a")
    try:
        assert conn.connect() is True
        assert conn.use_framing is True
    finally:
        conn.disconnect()


def test_handshake_without_project_token_still_connects():
    # PROJECT is optional, but the compatibility manifest is mandatory.
    port = start_dummy_server(compatible_greeting())
    conn = UnityConnection(host="127.0.0.1", port=port,
                           instance_id="MyProject@3a9e429a")
    try:
        assert conn.connect() is True
    finally:
        conn.disconnect()


def test_small_frame_ping_pong():
    port = start_dummy_server(compatible_greeting(), respond_ping=True)
    conn = UnityConnection(host="127.0.0.1", port=port)
    try:
        assert conn.connect() is True
        assert conn.use_framing is True
        payload = b'{"type":"ping"}'
        conn.sock.sendall(struct.pack(">Q", len(payload)) + payload)
        resp = conn.receive_full_response(conn.sock)
        assert json.loads(resp.decode("utf-8"))["type"] == "pong"
    finally:
        conn.disconnect()


def test_port_discovery_probe_completes_handshake_before_ping():
    port = start_dummy_server(compatible_greeting(), respond_ping=True)
    assert PortDiscovery._try_probe_unity_mcp(port) is True


def test_port_discovery_probe_surfaces_package_incompatibility():
    greeting = (
        f"WELCOME UNITY-MCP {BRIDGE_PROTOCOL_VERSION} FRAMING=1 "
        f"BRIDGE_PROTOCOL={BRIDGE_PROTOCOL_VERSION} "
        "UNITY_PACKAGE=99.0.0 RESOURCES=editor_state,tool_states "
        "TOOL_GROUPS=core,testing\n"
    ).encode("ascii")
    port = start_dummy_server(greeting)

    with pytest.raises(BridgeCompatibilityError, match="Server/package version mismatch"):
        PortDiscovery._try_probe_unity_mcp(port)


def test_instance_discovery_does_not_hide_probe_incompatibility(tmp_path, monkeypatch):
    status_path = tmp_path / "unity-mcp-status-deadbeef.json"
    status_path.write_text(
        json.dumps({
            "project_path": str(tmp_path / "Project" / "Assets"),
            "unity_port": 6400,
        }),
        encoding="utf-8",
    )
    monkeypatch.setattr(PortDiscovery, "get_registry_dir", staticmethod(lambda: tmp_path))

    def incompatible_probe(_port):
        raise BridgeCompatibilityError(
            "Server/package version mismatch: install matching releases"
        )

    monkeypatch.setattr(
        PortDiscovery, "_try_probe_unity_mcp", staticmethod(incompatible_probe)
    )

    with pytest.raises(BridgeCompatibilityError, match="install matching releases"):
        PortDiscovery.discover_all_unity_instances()


def test_instance_discovery_skips_incompatible_peer_when_compatible_exists(
    tmp_path, monkeypatch
):
    for hash_value, project_name, port in (
        ("incompatible", "OldProject", 6400),
        ("compatible", "CurrentProject", 6401),
    ):
        (tmp_path / f"unity-mcp-status-{hash_value}.json").write_text(
            json.dumps({
                "project_path": str(tmp_path / project_name / "Assets"),
                "unity_port": port,
            }),
            encoding="utf-8",
        )

    monkeypatch.setattr(PortDiscovery, "get_registry_dir", staticmethod(lambda: tmp_path))

    def probe(port):
        if port == 6400:
            raise BridgeCompatibilityError("old editor is incompatible")
        return port == 6401

    monkeypatch.setattr(PortDiscovery, "_try_probe_unity_mcp", staticmethod(probe))

    instances = PortDiscovery.discover_all_unity_instances()

    assert [instance.port for instance in instances] == [6401]


def test_instance_discovery_surfaces_explicit_incompatible_peer(
    tmp_path, monkeypatch
):
    for hash_value, project_name, port in (
        ("incompatible", "OldProject", 6400),
        ("compatible", "CurrentProject", 6401),
    ):
        (tmp_path / f"unity-mcp-status-{hash_value}.json").write_text(
            json.dumps({
                "project_path": str(tmp_path / project_name / "Assets"),
                "unity_port": port,
            }),
            encoding="utf-8",
        )

    monkeypatch.setattr(PortDiscovery, "get_registry_dir", staticmethod(lambda: tmp_path))

    def probe(port):
        if port == 6400:
            raise BridgeCompatibilityError("old editor is incompatible")
        return port == 6401

    monkeypatch.setattr(PortDiscovery, "_try_probe_unity_mcp", staticmethod(probe))

    with pytest.raises(BridgeCompatibilityError, match="old editor is incompatible"):
        PortDiscovery.discover_all_unity_instances("OldProject@incompatible")


def test_port_discovery_skips_incompatible_latest_status(tmp_path, monkeypatch):
    compatible_registry = tmp_path / "unity-mcp-port-compatible.json"
    compatible_registry.write_text(
        json.dumps({"unity_port": 6401}), encoding="utf-8"
    )
    monkeypatch.setattr(
        PortDiscovery,
        "_read_latest_status",
        staticmethod(lambda: {"unity_port": 6400}),
    )
    monkeypatch.setattr(
        PortDiscovery,
        "list_candidate_files",
        staticmethod(lambda: [compatible_registry]),
    )

    def probe(port):
        if port == 6400:
            raise BridgeCompatibilityError("latest editor is incompatible")
        return port == 6401

    monkeypatch.setattr(PortDiscovery, "_try_probe_unity_mcp", staticmethod(probe))

    assert PortDiscovery.discover_unity_port() == 6401


def test_unframed_data_disconnect():
    port = start_handshake_enforcing_server()
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    sock.connect(("127.0.0.1", port))
    sock.settimeout(1.0)
    sock.sendall(b"BAD")
    time.sleep(0.4)
    try:
        data = sock.recv(1024)
        assert data == b""
    except (ConnectionResetError, ConnectionAbortedError):
        # Some platforms raise instead of returning empty bytes when the
        # server closes the connection after detecting pre-handshake data.
        pass
    finally:
        sock.close()


def test_zero_length_payload_heartbeat():
    # Server that sends handshake and a zero-length heartbeat frame followed by a pong payload
    import socket
    import struct
    import threading
    import time

    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    sock.bind(("127.0.0.1", 0))
    sock.listen(1)
    port = sock.getsockname()[1]
    ready = threading.Event()

    def _run():
        ready.set()
        conn, _ = sock.accept()
        try:
            conn.sendall(compatible_greeting())

            def _read_exact(n: int) -> bytes:
                data = b""
                while len(data) < n:
                    data += conn.recv(n - len(data))
                return data

            header = _read_exact(8)
            request_len = struct.unpack(">Q", header)[0]
            request = json.loads(_read_exact(request_len).decode("utf-8"))
            server_handshake = request["params"]["handshake"]
            ack = json.dumps({
                "status": "success",
                "result": {
                    "bridge_protocol_version": BRIDGE_PROTOCOL_VERSION,
                    "python_server": server_handshake["python_server"],
                    "unity_package": UNITY_PACKAGE,
                },
            }).encode("utf-8")
            conn.sendall(struct.pack(">Q", len(ack)) + ack)
            # Heartbeat frame (length=0)
            conn.sendall(struct.pack(">Q", 0))
            time.sleep(0.02)
            # Real payload frame
            payload = b'{"type":"pong"}'
            conn.sendall(struct.pack(">Q", len(payload)) + payload)
            time.sleep(0.02)
        finally:
            try:
                conn.close()
            except Exception:
                pass
            sock.close()

    threading.Thread(target=_run, daemon=True).start()
    ready.wait()

    conn = UnityConnection(host="127.0.0.1", port=port)
    try:
        assert conn.connect() is True
        # Receive should skip heartbeat and return the pong payload (or empty if only heartbeats seen)
        resp = conn.receive_full_response(conn.sock)
        assert resp in (b'{"type":"pong"}', b"")
    finally:
        conn.disconnect()


class _DeliveryStageSocket:
    def __init__(self, fail_send_number: int | None = None):
        self.fail_send_number = fail_send_number
        self.send_count = 0
        self.closed = False
        self.timeout = None

    def getblocking(self):
        return True

    def setblocking(self, _value):
        pass

    def gettimeout(self):
        return self.timeout

    def settimeout(self, value):
        self.timeout = value

    def recv(self, *_args):
        raise BlockingIOError()

    def sendall(self, _data):
        self.send_count += 1
        if self.send_count == self.fail_send_number:
            raise ConnectionError("send failed")

    def close(self):
        self.closed = True


def test_receive_failure_after_complete_send_is_marked_delivery_uncertain(monkeypatch):
    conn = UnityConnection(host="127.0.0.1", port=1)
    conn.sock = _DeliveryStageSocket()
    conn.use_framing = True
    monkeypatch.setattr(
        conn, "receive_full_response",
        lambda _sock: (_ for _ in ()).throw(TimeoutError("reply was lost")),
    )

    with pytest.raises(TimeoutError) as raised:
        conn.send_command("run_tests", {"mode": "EditMode"}, max_attempts=0)

    assert raised.value.request_may_have_reached_unity is True


def test_incomplete_send_is_not_marked_delivery_uncertain():
    conn = UnityConnection(host="127.0.0.1", port=1)
    # Framing sends the header first and payload second. Failing the payload send means
    # the transport cannot prove the complete request reached Unity.
    conn.sock = _DeliveryStageSocket(fail_send_number=2)
    conn.use_framing = True

    with pytest.raises(ConnectionError) as raised:
        conn.send_command("run_tests", {"mode": "EditMode"}, max_attempts=0)

    assert not getattr(raised.value, "request_may_have_reached_unity", False)


