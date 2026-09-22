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

echo "[e2e] test exit code: $status; collecting container state and logs"
# A container that died takes its name out of Docker's DNS, so the suite sees "fetch failed" or
# "ENOTFOUND child" and blames the network. The exit code says what really happened: 137 is a kill
# (out of memory), 139 a segmentation fault, 0 a clean stop.
{
  echo "== docker compose ps -a =="
  docker compose ps -a
  echo
  echo "== container state =="
  for service in child parent gate; do
    cid="$(docker compose ps -aq "$service" 2>/dev/null | head -1)"
    if [[ -n "$cid" ]]; then
      docker inspect "$cid" --format \
        '{{.Name}} status={{.State.Status}} exit={{.State.ExitCode}} oomKilled={{.State.OOMKilled}} error="{{.State.Error}}" started={{.State.StartedAt}} finished={{.State.FinishedAt}}'
    else
      echo "$service: no container"
    fi
  done
  echo
  echo "== host memory =="
  free -m 2>/dev/null || true
} > test-results/containers.txt 2>&1 || true
cat test-results/containers.txt || true

for service in child parent gate; do
  docker compose logs --no-color --timestamps "$service" > "test-results/$service.log" 2>&1 || true
done

echo "[e2e] tearing down"
docker compose down -v --remove-orphans || echo "[e2e] warning: docker compose down failed"

exit "$status"
