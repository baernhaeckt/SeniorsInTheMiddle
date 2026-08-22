"""Error types shared by the runtime and the services built on top of it."""

from __future__ import annotations

from typing import Any

__all__ = [
    "ServiceRuntimeError",
    "ProtocolError",
    "FrameTooLargeError",
    "ServiceError",
    "InvalidRequestError",
    "MethodNotFoundError",
    "RequestTimeoutError",
]


class ServiceRuntimeError(Exception):
    """Base class for everything this package raises."""


class ProtocolError(ServiceRuntimeError):
    """The peer sent something that is not a valid frame."""


class FrameTooLargeError(ProtocolError):
    """A frame exceeded ``RuntimeConfig.max_frame_bytes``."""


class ServiceError(ServiceRuntimeError):
    """Raised by a service to return a structured error to the caller.

    Anything else escaping ``Service.handle`` is reported as ``internal_error``.
    """

    code = "service_error"

    def __init__(
        self,
        message: str,
        *,
        code: str | None = None,
        details: Any = None,
    ) -> None:
        super().__init__(message)
        self.message = message
        self.details = details
        if code is not None:
            self.code = code

    def to_dict(self) -> dict[str, Any]:
        error: dict[str, Any] = {"code": self.code, "message": self.message}
        if self.details is not None:
            error["details"] = self.details
        return error


class InvalidRequestError(ServiceError):
    code = "invalid_request"


class MethodNotFoundError(ServiceError):
    code = "method_not_found"

    def __init__(self, method: str) -> None:
        super().__init__(f"unknown method: {method}", details={"method": method})


class RequestTimeoutError(ServiceError):
    code = "timeout"
