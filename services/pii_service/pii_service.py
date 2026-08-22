"""Reference implementation of a service on top of ``service_runtime``.

It shows the three interface methods, structured errors and the socket path
this service listens on.
"""

from __future__ import annotations

import logging
from typing import Any

from services.pii_service.pii_detector import PiiDetector
from services.pii_service.pii_types import PiiTypes
from services.service_runtime import InvalidRequestError, MethodNotFoundError, Service

__all__ = ["ExampleService", "DEFAULT_SOCKET_PATH"]

logger = logging.getLogger(__name__)

#: Each service owns one socket; the dotnet host connects to this path.
DEFAULT_SOCKET_PATH = "/run/services/example-service.sock"


class ExampleService(Service):
    """
    Echo/sum/greet toy service used by the dotnet integration test.
    """

    def __init__(self) -> None:
        self._greeting = "Hoi from the PII-service"
        self._handled = 0

    async def start(self) -> None:
        # Open database pools, load models, warm caches here.
        logger.info("PII-service ready")

    async def stop(self) -> None:
        logger.info("PII-service handled %d request(s)", self._handled)

    async def handle(self, method: str, payload: dict[str, Any]) -> Any:
        self._handled += 1

        # Initialize the PiiDetector for handling PII detection requests
        pii_analyzer = PiiDetector()
        match method:
            case "analyze":
                logger.info(f"Handling 'analyze")
                text = payload.get("text", "")
                return pii_analyzer.analyze_text(text, detection_entities=[p.value for p in PiiTypes])
            case "replacement_text":
                logger.info(f"Handling 'replace_text'")
                pii_type = payload.get("pii_type", "")
                return pii_analyzer.create_replacement_text(pii_type)
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
