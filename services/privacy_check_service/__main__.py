"""Entry point: ``python -m privacy_check_service``.

This service owns its socket path. ``SERVICE_SOCKET_PATH`` overrides it, and
every other runtime setting has a default (see ``RuntimeConfig.from_env``).
"""

from __future__ import annotations

from service_runtime import run

from .privacy_checker_service import DEFAULT_SOCKET_PATH, PrivacyCheckService


def main() -> None:
    run(PrivacyCheckService(), socket_path=DEFAULT_SOCKET_PATH)


if __name__ == "__main__":
    main()
