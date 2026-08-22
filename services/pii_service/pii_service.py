"""PII detection service on top of ``service_runtime``.

Methods:
    analyze           {"text": str}      -> analysis dict (empty dict when nothing was found)
    replacement_text  {"pii_type": str}  -> str
"""

from __future__ import annotations

import logging
from typing import Any

from service_runtime import InvalidRequestError, MethodNotFoundError, Service

from .pii_detector import PiiDetector
from .pii_types import PiiTypes

__all__ = ["PiiService", "DEFAULT_SOCKET_PATH"]

logger = logging.getLogger(__name__)

#: Each service owns one socket; the dotnet host connects to this path.
DEFAULT_SOCKET_PATH = "/run/services/pii-service.sock"


class PiiService(Service):
    """Presidio/spaCy based PII detection."""

    def __init__(self) -> None:
        self._detector: PiiDetector | None = None
        self._handled = 0

    async def start(self) -> None:
        # The spaCy model is loaded exactly once, before the socket is opened.
        self._detector = PiiDetector()
        logger.info("PII-service ready (language=%s)", self._detector.language_code)

    async def stop(self) -> None:
        logger.info("PII-service handled %d request(s)", self._handled)

    async def handle(self, method: str, payload: dict[str, Any]) -> Any:
        self._handled += 1
        detector = self._detector
        if detector is None:
            raise RuntimeError("service not started")

        match method:
            case "analyze":
                text = self._require_str(payload, "text")
                return detector.analyze_text(text, detection_entities=[p.value for p in PiiTypes])
            case "replacement_text":
                pii_type = self._require_str(payload, "pii_type")
                return detector.create_replacement_text(pii_type)
            case _:
                raise MethodNotFoundError(method)

    @staticmethod
    def _require_str(payload: dict[str, Any], key: str) -> str:
        value = payload.get(key)
        if not isinstance(value, str) or not value:
            raise InvalidRequestError(f"'{key}' must be a non-empty string", details={"field": key})
        return value
