"""Entry point: ``python -m pii_service``.

This service owns its socket path. ``SERVICE_SOCKET_PATH`` overrides it, and
every other runtime setting has a default (see ``RuntimeConfig.from_env``).
``DEFAULT_LANGUAGE`` (``de``/``en``) selects the spaCy model.
"""

from __future__ import annotations

from service_runtime import run

from .pii_service import DEFAULT_SOCKET_PATH, PiiService


def main() -> None:
    run(PiiService(), socket_path=DEFAULT_SOCKET_PATH)


if __name__ == "__main__":
    main()
