"""Async client for the unix socket protocol.

The dotnet host has its own implementation of this; this one exists for tests
and for python-to-python calls between services.
"""

from __future__ import annotations

import asyncio
import itertools
import json
import os
from types import TracebackType
from typing import Any

from .config import DEFAULT_MAX_FRAME_BYTES
from .errors import ProtocolError, ServiceError
from .protocol import read_frame, write_frame

__all__ = ["ServiceClient", "RemoteServiceError"]


class RemoteServiceError(ServiceError):
    """An error response returned by the remote service."""

    def __init__(self, code: str, message: str, details: Any = None) -> None:
        super().__init__(message, code=code, details=details)


class ServiceClient:
    """Multiplexing client: several ``call`` coroutines share one connection.

    ::

        async with ServiceClient("/run/services/example.sock") as client:
            print(await client.call("echo", {"message": "hi"}))
    """

    def __init__(
        self,
        socket_path: str | os.PathLike[str],
        *,
        max_frame_bytes: int = DEFAULT_MAX_FRAME_BYTES,
    ) -> None:
        self._socket_path = os.fspath(socket_path)
        self._max_frame_bytes = max_frame_bytes
        self._reader: asyncio.StreamReader | None = None
        self._writer: asyncio.StreamWriter | None = None
        self._pending: dict[str, asyncio.Future[Any]] = {}
        self._read_task: asyncio.Task[None] | None = None
        self._ids = itertools.count(1)
        self._write_lock = asyncio.Lock()

    async def connect(self, *, timeout: float = 10.0, retry_interval: float = 0.1) -> "ServiceClient":
        """Connect, retrying while the service is still starting up."""
        loop = asyncio.get_running_loop()
        deadline = loop.time() + timeout
        last_error: OSError | None = None
        while loop.time() < deadline:
            try:
                self._reader, self._writer = await asyncio.open_unix_connection(self._socket_path)
                break
            except (FileNotFoundError, ConnectionRefusedError, OSError) as exc:
                last_error = exc
                await asyncio.sleep(retry_interval)
        else:
            raise TimeoutError(f"could not connect to {self._socket_path} within {timeout}s") from last_error

        self._read_task = asyncio.create_task(self._read_loop(), name=f"client:{self._socket_path}")
        return self

    async def call(self, method: str, payload: dict[str, Any] | None = None, *, timeout: float | None = 30.0) -> Any:
        """Send a request and await its response."""
        if self._writer is None:
            raise RuntimeError("client is not connected")

        request_id = str(next(self._ids))
        future: asyncio.Future[Any] = asyncio.get_running_loop().create_future()
        self._pending[request_id] = future

        frame = json.dumps(
            {"id": request_id, "method": method, "payload": payload or {}},
            separators=(",", ":"),
        ).encode("utf-8")

        try:
            async with self._write_lock:
                await write_frame(self._writer, frame)
            if timeout is None:
                return await future
            async with asyncio.timeout(timeout):
                return await future
        finally:
            self._pending.pop(request_id, None)

    async def close(self) -> None:
        if self._read_task is not None:
            self._read_task.cancel()
            try:
                await self._read_task
            except asyncio.CancelledError:
                pass
            self._read_task = None
        if self._writer is not None:
            self._writer.close()
            try:
                await self._writer.wait_closed()
            except (ConnectionResetError, BrokenPipeError):
                pass
            self._writer = None
        self._fail_pending(ConnectionResetError("client closed"))

    async def __aenter__(self) -> "ServiceClient":
        return await self.connect()

    async def __aexit__(
        self,
        exc_type: type[BaseException] | None,
        exc: BaseException | None,
        tb: TracebackType | None,
    ) -> None:
        await self.close()

    # ---------------------------------------------------------------- private

    async def _read_loop(self) -> None:
        assert self._reader is not None
        try:
            while True:
                frame = await read_frame(self._reader, self._max_frame_bytes)
                if frame is None:
                    self._fail_pending(ConnectionResetError("service closed the connection"))
                    return
                self._deliver(json.loads(frame.decode("utf-8")))
        except asyncio.CancelledError:
            raise
        except Exception as exc:  # noqa: BLE001 - surfaced through the pending futures
            self._fail_pending(exc)

    def _deliver(self, message: dict[str, Any]) -> None:
        future = self._pending.pop(str(message.get("id")), None)
        if future is None or future.done():
            return
        if message.get("ok"):
            future.set_result(message.get("result"))
            return
        error = message.get("error") or {}
        if not isinstance(error, dict):
            future.set_exception(ProtocolError(f"malformed error response: {error!r}"))
            return
        future.set_exception(
            RemoteServiceError(
                str(error.get("code", "unknown")),
                str(error.get("message", "")),
                error.get("details"),
            )
        )

    def _fail_pending(self, exc: BaseException) -> None:
        for future in self._pending.values():
            if not future.done():
                future.set_exception(exc)
        self._pending.clear()
