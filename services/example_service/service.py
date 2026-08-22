"""Reference implementation of a service on top of ``service_runtime``.

It shows the three interface methods, structured errors and the socket path
this service listens on.
"""

from __future__ import annotations

import asyncio
import logging
from typing import Any

from service_runtime import InvalidRequestError, MethodNotFoundError, Service, ServiceError

__all__ = ["ExampleService", "DEFAULT_SOCKET_PATH"]

logger = logging.getLogger(__name__)

#: Each service owns one socket; the dotnet host connects to this path.
DEFAULT_SOCKET_PATH = "/run/services/example-service.sock"


class ExampleService(Service):
    """Echo/sum/greet toy service used by the dotnet integration test."""

    def __init__(self) -> None:
        self._greeting = "Hoi"
        self._handled = 0

    async def start(self) -> None:
        # Open database pools, load models, warm caches here.
        logger.info("example service ready")

    async def stop(self) -> None:
        logger.info("example service handled %d request(s)", self._handled)

    async def handle(self, method: str, payload: dict[str, Any]) -> Any:
        self._handled += 1

        match method:
            case "echo":
                return payload
            case "greet":
                return {"message": f"{self._greeting}, {self._require_str(payload, 'name')}!"}
            case "sum":
                return {"total": sum(self._require_numbers(payload, "values"))}
            case "slow":
                seconds = float(payload.get("seconds", 0.5))
                await asyncio.sleep(seconds)
                return {"slept_seconds": seconds}
            case "fail":
                raise ServiceError(
                    payload.get("message", "the service refused this request"),
                    code=payload.get("code", "example_failure"),
                    details={"method": method},
                )
            case "stats":
                return {"handled_requests": self._handled}
            case _:
                raise MethodNotFoundError(method)

    # ---------------------------------------------------------------- helpers

    @staticmethod
    def _require_str(payload: dict[str, Any], key: str) -> str:
        value = payload.get(key)
        if not isinstance(value, str) or not value:
            raise InvalidRequestError(f"'{key}' must be a non-empty string", details={"field": key})
        return value

    @staticmethod
    def _require_numbers(payload: dict[str, Any], key: str) -> list[float]:
        values = payload.get(key)
        if not isinstance(values, list) or not all(isinstance(v, (int, float)) and not isinstance(v, bool) for v in values):
            raise InvalidRequestError(f"'{key}' must be a list of numbers", details={"field": key})
        return values
