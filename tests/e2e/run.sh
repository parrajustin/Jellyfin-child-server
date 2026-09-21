#!/usr/bin/env bash
# Runs the end-to-end suite in Docker, from any working directory:
#
#   bash tests/e2e/run.sh [playwright args]
#   bash tests/e2e/run.sh --update-snapshots       # regenerate goldens (then commit specs/__screenshots__)
#   bash tests/e2e/run.sh -g "browser playback"     # one test
#
# Environment:
#   E2E_SKIP_CHILD_BUILD=1   do not build the child image here (CI builds jellyfin-child-server:e2e
#                            with its own cache before calling this script)
#   E2E_IGNORE_SNAPSHOTS=1   skip screenshot assertions (developer without goldens)
#
# Steps: fixture library -> build images -> up parent/gate/child -> run the e2e container ->
# collect container logs into test-results/ -> compose down -v -> exit with the test status.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$here"

echo "[e2e] building the parent media library"
node media/build-library.mjs .work/parent-media
mkdir -p test-results .work specs/__screenshots__

echo "[e2e] building images"
if [[ "${E2E_SKIP_CHILD_BUILD:-0}" == "1" ]]; then
  docker compose build e2e
else
  docker compose build child e2e
fi

echo "[e2e] starting parent, gate and child"
docker compose up -d parent gate child

echo "[e2e] running playwright: npx playwright test $*"
status=0
set +e
docker compose run --rm e2e npx playwright test "$@"
status=$?
set -e

echo "[e2e] test exit code: $status; collecting container logs"
for service in child parent gate; do
  docker compose logs --no-color --timestamps "$service" > "test-results/$service.log" 2>&1 || true
done

echo "[e2e] tearing down"
docker compose down -v --remove-orphans || echo "[e2e] warning: docker compose down failed"

exit "$status"
