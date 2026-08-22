"""Process entry point helpers: logging, signal handling, one-liner startup."""

from __future__ import annotations

import asyncio
import logging
import os
import signal
import sys
from pathlib import Path

from .config import RuntimeConfig
from .runtime import ServiceRuntime
from .service import Service

__all__ = ["run", "serve", "configure_logging"]

logger = logging.getLogger("service_runtime")

_STOP_SIGNALS = ("SIGINT", "SIGTERM")


def configure_logging(level: str = "INFO") -> None:
    """Minimal stderr logging so container logs stay readable and interleaved."""
    logging.basicConfig(
        level=getattr(logging, level.upper(), logging.INFO),
        format="%(asctime)s %(levelname)-8s %(name)s %(message)s",
        stream=sys.stderr,
        force=True,
    )


async def serve(service: Service, config: RuntimeConfig) -> None:
    """Serve until SIGINT/SIGTERM, then shut down gracefully."""
    runtime = ServiceRuntime(service, config)
    loop = asyncio.get_running_loop()
    stop = asyncio.Event()

    for name in _STOP_SIGNALS:
        sig = getattr(signal, name, None)
        if sig is None:
            continue
        try:
            loop.add_signal_handler(sig, stop.set)
        except NotImplementedError:  # pragma: no cover - windows / non-main thread
            signal.signal(sig, lambda *_: loop.call_soon_threadsafe(stop.set))

    await runtime.start()
    try:
        await stop.wait()
    finally:
        await runtime.stop()


def run(
    service: Service,
    config: RuntimeConfig | None = None,
    *,
    socket_path: str | os.PathLike[str] | None = None,
) -> None:
    """Block and serve ``service``.

    Pass either a ready-made ``config`` or a ``socket_path``; the socket path may
    also come from ``SERVICE_SOCKET_PATH`` in the environment::

        run(EchoService(), socket_path="/run/services/example.sock")
    """
    if config is None:
        config = RuntimeConfig.from_env(default_socket_path=Path(socket_path) if socket_path else None)

    configure_logging(config.log_level)
    logger.info("starting %s on %s", type(service).__name__, config.socket_path)
    asyncio.run(serve(service, config))
