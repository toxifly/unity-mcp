"""The stdio bridge attaches a broker resend to the original command by its request_id."""
import json

import transport.legacy.unity_connection as uc
from transport.legacy.unity_connection import UnityConnection


class _FakeSocket:
    def __init__(self, sent):
        self.sent = sent

    def sendall(self, data):
        self.sent.append(data)

    def gettimeout(self):
        return 1.0

    def settimeout(self, _):
        pass

    def close(self):
        pass


def _sent_commands(monkeypatch, *calls):
    sent: list[bytes] = []
    replies = iter([ConnectionError("reply lost"), b'{"status":"success","result":{}}'] * len(calls))

    def connect(self, _timeout=None):
        self.sock = _FakeSocket(sent)
        return True

    def receive(self, _sock):
        reply = next(replies)
        if isinstance(reply, Exception):
            raise reply
        return reply

    monkeypatch.setattr(UnityConnection, "connect", connect)
    monkeypatch.setattr(UnityConnection, "_ensure_live_connection", lambda self: None)
    monkeypatch.setattr(UnityConnection, "receive_full_response", receive)
    monkeypatch.setattr(uc.time, "sleep", lambda _s: None)

    conn = UnityConnection(port=1)
    for command_type, params in calls:
        conn.send_command(command_type, params)
    return [json.loads(data) for data in sent]


def test_resend_reuses_the_request_id(monkeypatch):
    first, resend = _sent_commands(monkeypatch, ("manage_gameobject", {"action": "create"}))

    assert first["request_id"]
    assert resend["request_id"] == first["request_id"]


def test_identical_commands_get_distinct_request_ids(monkeypatch):
    sent = _sent_commands(
        monkeypatch,
        ("manage_gameobject", {"action": "create"}),
        ("manage_gameobject", {"action": "create"}),
    )

    assert sent[0]["request_id"] != sent[2]["request_id"]
