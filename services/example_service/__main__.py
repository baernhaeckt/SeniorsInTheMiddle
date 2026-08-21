"""Entry point: ``python -m example_service``.

This service owns its socket path. ``SERVICE_SOCKET_PATH`` overrides it, and
every other runtime setting has a default (see ``RuntimeConfig.from_env``).
"""

from __future__ import annotations

from service_runtime import run

from .service import DEFAULT_SOCKET_PATH, ExampleService


def main() -> None:
    run(ExampleService(), socket_path=DEFAULT_SOCKET_PATH)


if __name__ == "__main__":
    main()
