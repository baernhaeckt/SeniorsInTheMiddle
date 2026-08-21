"""Runtime configuration.

Every service owns its own socket, so the socket path is the one setting a
service *must* provide, either in code or through the environment.
"""

from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path
from typing import Any

__all__ = ["RuntimeConfig"]

DEFAULT_MAX_FRAME_BYTES = 8 * 1024 * 1024


@dataclass(slots=True)
class RuntimeConfig:
    """Settings for a single :class:`~service_runtime.runtime.ServiceRuntime`.

    Attributes:
        socket_path: Absolute path of the unix socket this service listens on.
        socket_mode: chmod applied to the socket file, ``None`` keeps the umask
            default. ``0o660`` lets only the same user/group (the dotnet host)
            talk to the service.
        max_frame_bytes: Largest accepted request frame.
        request_timeout: Seconds a single ``handle`` call may take, ``None``
            disables the timeout.
        max_concurrent_requests: Upper bound of ``handle`` calls running at the
            same time across all connections, ``None`` disables the limit.
        shutdown_timeout: Seconds in-flight requests get to finish on shutdown
            before they are cancelled.
        remove_stale_socket: Unlink a leftover socket file on startup.
        log_level: Level for the ``service_runtime`` logger.
    """

    socket_path: Path
    socket_mode: int | None = 0o660
    max_frame_bytes: int = DEFAULT_MAX_FRAME_BYTES
    request_timeout: float | None = 30.0
    max_concurrent_requests: int | None = 64
    shutdown_timeout: float = 10.0
    remove_stale_socket: bool = True
    log_level: str = "INFO"

    def __post_init__(self) -> None:
        self.socket_path = Path(self.socket_path)
        if not self.socket_path.is_absolute():
            self.socket_path = self.socket_path.resolve()

    @classmethod
    def from_env(
        cls,
        *,
        prefix: str = "SERVICE_",
        default_socket_path: str | os.PathLike[str] | None = None,
        **overrides: Any,
    ) -> "RuntimeConfig":
        """Build a config from ``<PREFIX>*`` environment variables.

        Reads ``SOCKET_PATH``, ``SOCKET_MODE``, ``MAX_FRAME_BYTES``,
        ``REQUEST_TIMEOUT``, ``MAX_CONCURRENT_REQUESTS``, ``SHUTDOWN_TIMEOUT``
        and ``LOG_LEVEL``. Explicit keyword arguments win over the environment.
        """
        env = os.environ

        socket_path = overrides.pop("socket_path", None) or env.get(f"{prefix}SOCKET_PATH") or default_socket_path
        if socket_path is None:
            raise ValueError(
                f"no socket path configured: pass socket_path/default_socket_path or set {prefix}SOCKET_PATH"
            )

        values: dict[str, Any] = {"socket_path": Path(socket_path)}

        if (raw := env.get(f"{prefix}SOCKET_MODE")) is not None:
            values["socket_mode"] = None if raw.strip() == "" else int(raw, 8)
        if (raw := env.get(f"{prefix}MAX_FRAME_BYTES")) is not None:
            values["max_frame_bytes"] = int(raw)
        if (raw := env.get(f"{prefix}REQUEST_TIMEOUT")) is not None:
            values["request_timeout"] = None if raw.strip() == "" else float(raw)
        if (raw := env.get(f"{prefix}MAX_CONCURRENT_REQUESTS")) is not None:
            values["max_concurrent_requests"] = None if raw.strip() == "" else int(raw)
        if (raw := env.get(f"{prefix}SHUTDOWN_TIMEOUT")) is not None:
            values["shutdown_timeout"] = float(raw)
        if (raw := env.get(f"{prefix}LOG_LEVEL")) is not None:
            values["log_level"] = raw

        values.update(overrides)
        return cls(**values)
