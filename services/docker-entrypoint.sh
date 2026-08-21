#!/bin/sh
# Runs the integration check inside one container:
#   1. start the python example_service on its unix socket
#   2. let the dotnet test host call it over that socket
#   3. verify the service shuts down gracefully on SIGTERM
set -eu

SOCKET_DIR="${SERVICE_SOCKET_DIR:-/run/services}"
SOCKET_PATH="${SERVICE_SOCKET_PATH:-$SOCKET_DIR/example-service.sock}"
export SERVICE_SOCKET_PATH

mkdir -p "$SOCKET_DIR"

echo "=============================================================="
echo " 1/2  starting example_service on $SOCKET_PATH"
echo "=============================================================="
python -m example_service &
SERVICE_PID=$!
trap 'kill "$SERVICE_PID" 2>/dev/null || true' EXIT INT TERM

echo
echo "=============================================================="
echo " 2/2  dotnet test host"
echo "=============================================================="
set +e
/app/test-host/ServiceRuntime.TestHost "$SOCKET_PATH"
STATUS=$?
set -e

echo
echo "--------------------------------------------------------------"
echo " shutting the service down with SIGTERM"
echo "--------------------------------------------------------------"
kill -TERM "$SERVICE_PID" 2>/dev/null || true
wait "$SERVICE_PID" 2>/dev/null || true
trap - EXIT INT TERM

if [ -e "$SOCKET_PATH" ]; then
    echo "  FAIL  the socket file was left behind at $SOCKET_PATH"
    STATUS=1
else
    echo "  PASS  the socket file was cleaned up"
fi

echo
if [ "$STATUS" -eq 0 ]; then
    echo "[entrypoint] integration check passed"
else
    echo "[entrypoint] integration check FAILED (exit $STATUS)"
fi

exit "$STATUS"
