"""The interface every service implementation fills in."""

from __future__ import annotations

from abc import ABC, abstractmethod
from typing import Any

__all__ = ["Service"]


class Service(ABC):
    """Base class for every python service.

    ``start``/``stop`` are optional lifecycle hooks: ``start`` runs once before
    the socket starts accepting connections, ``stop`` once after the last
    in-flight request finished.
    """

    async def start(self) -> None:
        pass

    async def stop(self) -> None:
        pass

    @abstractmethod
    async def handle(
        self,
        method: str,
        payload: dict[str, Any],
    ) -> Any:
        pass
