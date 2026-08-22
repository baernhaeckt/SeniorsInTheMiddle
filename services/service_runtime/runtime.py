"""The unix socket server that hosts a single :class:`Service`."""

from __future__ import annotations

import asyncio
import contextlib
import logging
import os
import time
from pathlib import Path
from typing import Any

from .config import RuntimeConfig
from .errors import (
    InvalidRequestError,
    MethodNotFoundError,
    ProtocolError,
    RequestTimeoutError,
    ServiceError,
)
from .protocol import Request, decode_request, encode_error, encode_success, read_frame, write_frame
from .service import Service

__all__ = ["ServiceRuntime"]

logger = logging.getLogger("service_runtime")

#: Methods handled by the runtime itself. Service methods must not use this prefix.
BUILTIN_PREFIX = "$"


class _Connection:
    """One accepted client connection with its in-flight request tasks."""

    __slots__ = ("reader", "writer", "tasks", "write_lock", "peer")

    def __init__(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter, peer: int) -> None:
        self.reader = reader
        self.writer = writer
        self.peer = peer
        self.tasks: set[asyncio.Task[None]] = set()
        self.write_lock = asyncio.Lock()


class ServiceRuntime:
    """Serves a :class:`Service` on a unix socket.

    Typical use is via :func:`service_runtime.run`, which adds signal handling::

        runtime = ServiceRuntime(MyService(), RuntimeConfig(socket_path="/run/x.sock"))
        await runtime.serve_forever()
    """

    def __init__(self, service: Service, config: RuntimeConfig) -> None:
        self._service = service
        self._config = config
        self._server: asyncio.Server | None = None
        self._connections: set[_Connection] = set()
        self._stopped = asyncio.Event()
        self._stopping = False
        self._started_at = time.monotonic()
        self._peer_counter = 0
        self._in_flight = 0
        self._semaphore: asyncio.Semaphore | None = (
            asyncio.Semaphore(config.max_concurrent_requests) if config.max_concurrent_requests else None
        )

    @property
    def config(self) -> RuntimeConfig:
        return self._config

    @property
    def socket_path(self) -> Path:
        return self._config.socket_path

    @property
    def service_name(self) -> str:
        return type(self._service).__name__

    async def start(self) -> None:
        """Run the start hook of the service and begin accepting connections."""
        if self._server is not None:
            raise RuntimeError("runtime is already started")

        path = self._config.socket_path
        path.parent.mkdir(parents=True, exist_ok=True)
        self._prepare_socket_path(path)

        await self._service.start()

        self._started_at = time.monotonic()
        self._stopped.clear()
        self._server = await asyncio.start_unix_server(self._on_client, path=os.fspath(path))

        if self._config.socket_mode is not None:
            os.chmod(path, self._config.socket_mode)

        logger.info("%s listening on %s", self.service_name, path)

    async def serve_forever(self) -> None:
        """Start the runtime and block until :meth:`stop` is called."""
        await self.start()
        await self._stopped.wait()

    async def stop(self) -> None:
        """Stop accepting connections, drain in-flight requests, clean up."""
        if self._stopping:
            await self._stopped.wait()
            return
        self._stopping = True
        logger.info("%s shutting down", self.service_name)

        if self._server is not None:
            self._server.close()
            with contextlib.suppress(Exception):
                await self._server.wait_closed()

        await self._drain_connections()

        with contextlib.suppress(FileNotFoundError):
            self._config.socket_path.unlink()

        try:
            await self._service.stop()
        except Exception:
            logger.exception("%s.stop() failed", self.service_name)

        self._server = None
        self._stopping = False
        self._stopped.set()
        logger.info("%s stopped", self.service_name)

    # ------------------------------------------------------------------ setup

    def _prepare_socket_path(self, path: Path) -> None:
        if not path.exists():
            return
        if not self._config.remove_stale_socket:
            raise OSError(f"socket path {path} already exists")
        if not path.is_socket():
            raise OSError(f"socket path {path} exists and is not a socket")
        logger.warning("removing stale socket %s", path)
        path.unlink()

    async def _drain_connections(self) -> None:
        pending = [task for connection in self._connections for task in connection.tasks]
        if pending:
            logger.info("waiting for %d in-flight request(s)", len(pending))
            _, still_pending = await asyncio.wait(pending, timeout=self._config.shutdown_timeout)
            for task in still_pending:
                logger.warning("cancelling a request that outlived the shutdown timeout")
                task.cancel()
            if still_pending:
                await asyncio.gather(*still_pending, return_exceptions=True)

        for connection in list(self._connections):
            await self._close_connection(connection)

    async def _close_connection(self, connection: _Connection) -> None:
        self._connections.discard(connection)
        with contextlib.suppress(Exception):
            connection.writer.close()
            await connection.writer.wait_closed()

    # --------------------------------------------------------------- serving

    async def _on_client(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
        self._peer_counter += 1
        connection = _Connection(reader, writer, self._peer_counter)
        self._connections.add(connection)
        logger.debug("connection %d opened", connection.peer)
        try:
            await self._read_loop(connection)
        finally:
            if connection.tasks:
                await asyncio.gather(*list(connection.tasks), return_exceptions=True)
            await self._close_connection(connection)
            logger.debug("connection %d closed", connection.peer)

    async def _read_loop(self, connection: _Connection) -> None:
        while not self._stopping:
            try:
                frame = await read_frame(connection.reader, self._config.max_frame_bytes)
            except (ProtocolError, ConnectionResetError) as exc:
                logger.warning("connection %d: %s", connection.peer, exc)
                return
            if frame is None:
                return

            try:
                request = decode_request(frame)
            except InvalidRequestError as exc:
                request_id = getattr(exc, "request_id", None)
                if request_id:
                    await self._send(connection, encode_error(request_id, exc.to_dict()))
                    continue
                logger.warning("connection %d: %s", connection.peer, exc)
                return
            except ProtocolError as exc:
                logger.warning("connection %d: %s", connection.peer, exc)
                return

            task = asyncio.create_task(
                self._process(connection, request),
                name=f"{self.service_name}:{request.method}:{request.id}",
            )
            connection.tasks.add(task)
            task.add_done_callback(connection.tasks.discard)

    async def _process(self, connection: _Connection, request: Request) -> None:
        try:
            result = await self._dispatch(request)
        except ServiceError as exc:
            logger.info("%s failed: %s", request.method, exc.message)
            frame = encode_error(request.id, exc.to_dict())
        except asyncio.CancelledError:
            with contextlib.suppress(Exception):
                await self._send(
                    connection,
                    encode_error(
                        request.id,
                        {"code": "cancelled", "message": f"{request.method} was cancelled during shutdown"},
                    ),
                )
            raise
        except Exception as exc:
            logger.exception("%s raised an unhandled exception", request.method)
            frame = encode_error(
                request.id,
                {"code": "internal_error", "message": f"{type(exc).__name__}: {exc}"},
            )
        else:
            frame = encode_success(request.id, result)

        await self._send(connection, frame)

    async def _dispatch(self, request: Request) -> Any:
        if request.method.startswith(BUILTIN_PREFIX):
            return self._dispatch_builtin(request)

        self._in_flight += 1
        try:
            if self._semaphore is not None:
                async with self._semaphore:
                    return await self._call_service(request)
            return await self._call_service(request)
        finally:
            self._in_flight -= 1

    async def _call_service(self, request: Request) -> Any:
        timeout = self._config.request_timeout
        if timeout is None:
            return await self._service.handle(request.method, request.payload)
        try:
            async with asyncio.timeout(timeout):
                return await self._service.handle(request.method, request.payload)
        except TimeoutError as exc:
            raise RequestTimeoutError(
                f"{request.method} exceeded the {timeout}s request timeout",
                details={"method": request.method, "timeout_seconds": timeout},
            ) from exc

    def _dispatch_builtin(self, request: Request) -> Any:
        match request.method:
            case "$ping":
                return {"pong": True, "service": self.service_name}
            case "$health":
                return {
                    "status": "ok",
                    "service": self.service_name,
                    "uptime_seconds": round(time.monotonic() - self._started_at, 3),
                    "in_flight_requests": self._in_flight,
                    "connections": len(self._connections),
                }
            case "$info":
                return {
                    "service": self.service_name,
                    "socket_path": os.fspath(self._config.socket_path),
                    "protocol": "length-prefixed-json/1",
                    "max_frame_bytes": self._config.max_frame_bytes,
                    "request_timeout": self._config.request_timeout,
                }
            case _:
                raise MethodNotFoundError(request.method)

    async def _send(self, connection: _Connection, frame: bytes) -> None:
        async with connection.write_lock:
            try:
                await write_frame(connection.writer, frame)
            except (ConnectionResetError, BrokenPipeError) as exc:
                logger.warning("connection %d: dropped while writing a response (%s)", connection.peer, exc)
