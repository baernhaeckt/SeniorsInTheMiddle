"""Shared runtime for the python services of SeniorsInTheMiddle.

A service implements :class:`Service` and gets its own unix socket, which the
dotnet host in the same container connects to::

    from typing import Any
    from service_runtime import Service, run

    class EchoService(Service):
        async def handle(self, method: str, payload: dict[str, Any]) -> Any:
            if method == "echo":
                return payload
            raise MethodNotFoundError(method)

    run(EchoService(), socket_path="/run/services/echo.sock")
"""

from .client import RemoteServiceError, ServiceClient
from .config import RuntimeConfig
from .errors import (
    FrameTooLargeError,
    InvalidRequestError,
    MethodNotFoundError,
    ProtocolError,
    RequestTimeoutError,
    ServiceError,
    ServiceRuntimeError,
)
from .runner import configure_logging, run, serve
from .runtime import ServiceRuntime
from .service import Service

__version__ = "0.1.0"

__all__ = [
    "Service",
    "ServiceRuntime",
    "RuntimeConfig",
    "ServiceClient",
    "run",
    "serve",
    "configure_logging",
    "ServiceRuntimeError",
    "ServiceError",
    "InvalidRequestError",
    "MethodNotFoundError",
    "RequestTimeoutError",
    "RemoteServiceError",
    "ProtocolError",
    "FrameTooLargeError",
    "__version__",
]
