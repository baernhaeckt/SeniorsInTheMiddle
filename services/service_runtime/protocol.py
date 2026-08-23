"""Wire protocol: length-prefixed JSON frames over a unix stream socket.

Every message is::

    +---------------------------+-------------------------+
    | uint32 big-endian length  | UTF-8 JSON payload      |
    +---------------------------+-------------------------+

Requests (dotnet -> python)::

    {"id": "<correlation id>", "method": "<name>", "payload": {...}}

Responses (python -> dotnet)::

    {"id": "...", "ok": true,  "result": <any>}
    {"id": "...", "ok": false, "error": {"code": "...", "message": "...", "details": <any>}}

Requests are correlated by ``id``, so a caller may pipeline several requests on
one connection and receive the answers in any order.
"""

from __future__ import annotations

import asyncio
import json
from dataclasses import dataclass, field
from typing import Any

from .errors import FrameTooLargeError, InvalidRequestError, ProtocolError

__all__ = [
    "HEADER_SIZE",
    "Request",
    "read_frame",
    "write_frame",
    "decode_request",
    "encode_success",
    "encode_error",
]

HEADER_SIZE = 4


async def read_frame(reader: asyncio.StreamReader, max_bytes: int) -> bytes | None:
    """Read one frame, or ``None`` when the peer closed the connection cleanly."""
    try:
        header = await reader.readexactly(HEADER_SIZE)
    except asyncio.IncompleteReadError as exc:
        if not exc.partial:
            return None
        raise ProtocolError("connection closed in the middle of a frame header") from exc

    length = int.from_bytes(header, "big")
    if length == 0:
        raise ProtocolError("received an empty frame")
    if length > max_bytes:
        raise FrameTooLargeError(f"frame of {length} bytes exceeds the {max_bytes} byte limit")

    try:
        return await reader.readexactly(length)
    except asyncio.IncompleteReadError as exc:
        raise ProtocolError("connection closed in the middle of a frame body") from exc


async def write_frame(writer: asyncio.StreamWriter, body: bytes) -> None:
    writer.write(len(body).to_bytes(HEADER_SIZE, "big") + body)
    await writer.drain()


@dataclass(slots=True)
class Request:
    """One decoded call: the id to answer under, the method, and its arguments."""

    id: str
    method: str
    payload: dict[str, Any] = field(default_factory=dict)


def decode_request(frame: bytes) -> Request:
    """Parse a frame into a :class:`Request`.

    Raises :class:`InvalidRequestError` (carrying a ``request_id`` attribute,
    so the runtime can still answer) for messages that are well-framed but
    malformed, and :class:`ProtocolError` for frames that cannot be correlated
    to a request at all.
    """
    try:
        message = json.loads(frame.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ProtocolError(f"frame is not valid UTF-8 JSON: {exc}") from exc

    if not isinstance(message, dict):
        raise ProtocolError("frame is not a JSON object")

    request_id = message.get("id")
    if not isinstance(request_id, str) or not request_id:
        raise ProtocolError("request is missing a non-empty string 'id'")

    method = message.get("method")
    if not isinstance(method, str) or not method:
        raise _invalid(request_id, "request is missing a non-empty string 'method'")

    payload = message.get("payload")
    if payload is None:
        payload = {}
    if not isinstance(payload, dict):
        raise _invalid(request_id, "'payload' must be a JSON object")

    return Request(id=request_id, method=method, payload=payload)


def _invalid(request_id: str, message: str) -> InvalidRequestError:
    error = InvalidRequestError(message)
    error.request_id = request_id
    return error


def _encode(message: dict[str, Any]) -> bytes:
    return json.dumps(message, separators=(",", ":"), ensure_ascii=False).encode("utf-8")


def encode_success(request_id: str, result: Any) -> bytes:
    return _encode({"id": request_id, "ok": True, "result": result})


def encode_error(request_id: str, error: dict[str, Any]) -> bytes:
    return _encode({"id": request_id, "ok": False, "error": error})
