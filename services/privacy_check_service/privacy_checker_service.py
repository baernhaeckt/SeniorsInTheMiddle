"""Privacy risk check service on top of ``service_runtime``.

Methods:
    risk_check  {"text": str, "replaced_names": [str, ...]}
                -> {"risks": [{"name": str, "risk_probability": float}, ...]}

``risks`` lists the replaced name(s) with the highest re-identification
probability; it is empty when ``replaced_names`` is empty.
"""

from __future__ import annotations

import asyncio
import logging
from typing import Any

from service_runtime import InvalidRequestError, MethodNotFoundError, Service

from .privacy_checker import PrivacyChecker

__all__ = ["PrivacyCheckService", "DEFAULT_SOCKET_PATH"]

logger = logging.getLogger(__name__)

#: Each service owns one socket; the dotnet host connects to this path.
DEFAULT_SOCKET_PATH = "/run/services/privacy-check-service.sock"


class PrivacyCheckService(Service):
    """Embedding + Bayesian re-identification risk of anonymised names."""

    def __init__(self) -> None:
        self._checker: PrivacyChecker | None = None
        self._handled = 0

    async def start(self) -> None:
        # The embedding model is loaded exactly once, before the socket is opened.
        self._checker = PrivacyChecker()
        logger.info("Privacy-check-service ready")

    async def stop(self) -> None:
        logger.info("Privacy-check-service handled %d request(s)", self._handled)

    async def handle(self, method: str, payload: dict[str, Any]) -> Any:
        self._handled += 1
        checker = self._checker
        if checker is None:
            raise RuntimeError("service not started")

        match method:
            case "risk_check":
                text = self._require_str(payload, "text")
                replaced_names = self._require_str_list(payload, "replaced_names")
                if not replaced_names:
                    return {"risks": []}
                # MCMC sampling is CPU bound and takes a while; keep the event loop
                # (and the other requests on this socket) responsive meanwhile.
                risks = await asyncio.to_thread(checker.check_privacy_risk, text, replaced_names)
                return {"risks": risks}
            case _:
                raise MethodNotFoundError(method)

    @staticmethod
    def _require_str(payload: dict[str, Any], key: str) -> str:
        value = payload.get(key)
        if not isinstance(value, str) or not value:
            raise InvalidRequestError(f"'{key}' must be a non-empty string", details={"field": key})
        return value

    @staticmethod
    def _require_str_list(payload: dict[str, Any], key: str) -> list[str]:
        value = payload.get(key, [])
        if not isinstance(value, list) or not all(isinstance(item, str) and item for item in value):
            raise InvalidRequestError(f"'{key}' must be a list of non-empty strings", details={"field": key})
        return value
