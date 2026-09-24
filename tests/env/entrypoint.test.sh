#!/bin/bash
#
# Tests docker/entrypoint.sh: the config it generates from the environment.
#
#   bash tests/env/entrypoint.test.sh
#
# The generated file is consumed by .NET's XmlSerializer, so the element names have to match the
# properties of MediaBrowser.Model.ChildServer.ChildServerConfiguration exactly. There is no .NET
# here to prove that by round trip, so the last test reads the property names straight out of the
# C# source and asserts every element used is one of them. That catches the failure this approach
# is actually prone to: a typo or a renamed property producing XML the server silently ignores.
set -uo pipefail

cd "$(dirname "$0")/../.."

ENTRYPOINT="docker/entrypoint.sh"
MODEL="MediaBrowser.Model/ChildServer/ChildServerConfiguration.cs"
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

PASS=0
FAIL=0

ok() { printf '  \033[32mok\033[0m %s\n' "$1"; PASS=$((PASS + 1)); }
no() { printf '  \033[31mFAIL\033[0m %s\n     %s\n' "$1" "${2:-}"; FAIL=$((FAIL + 1)); }

# Runs the entrypoint with a clean config dir and echoes the path of the file it produced.
run_entrypoint() {
  local dir="${WORK}/config-$RANDOM"
  mkdir -p "$dir"
  JELLYFIN_CONFIG_DIR="$dir" CHILD_SERVER_CONFIG_ONLY=1 bash "$ENTRYPOINT" > "${dir}/stderr.log" 2>&1
  local code=$?
  printf '%s %s' "$code" "${dir}/childserver.xml"
}

# xpath value of a single element, or empty.
value_of() {
  python3 - "$1" "$2" <<'PY'
import sys, xml.etree.ElementTree as ET
tree = ET.parse(sys.argv[1])
found = tree.getroot().find(sys.argv[2])
print(found.text if found is not None and found.text is not None else '')
PY
}

echo "entrypoint: writes nothing without a parent URL"
result=$(env -u CHILD_PARENT_URL -u CHILD_PARENT_USERNAME -u CHILD_PARENT_PASSWORD \
  -u CF_ACCESS_CLIENT_ID -u CF_ACCESS_CLIENT_SECRET -u CHILD_PARENT_HEADERS \
  bash -c "$(declare -f run_entrypoint); WORK=$WORK ENTRYPOINT=$ENTRYPOINT run_entrypoint")
read -r code file <<< "$result"
if [ "$code" -eq 0 ] && [ ! -f "$file" ]; then
  ok "no config file created, the dashboard stays in charge"
else
  no "no config file created" "exit=$code exists=$([ -f "$file" ] && echo yes || echo no)"
fi

echo
echo "entrypoint: full configuration"
dir="${WORK}/full"
mkdir -p "$dir"
JELLYFIN_CONFIG_DIR="$dir" CHILD_SERVER_CONFIG_ONLY=1 \
  CHILD_PARENT_URL="https://parent.example.com" \
  CHILD_PARENT_USERNAME="remote" \
  CHILD_PARENT_PASSWORD="p@ss & <word>" \
  CF_ACCESS_CLIENT_ID="abc123.access" \
  CF_ACCESS_CLIENT_SECRET="cfast_secret" \
  CHILD_PREFETCH_EPISODE_COUNT=6 \
  CHILD_MAX_CACHE_SIZE_MB=2048 \
  CHILD_DOWNLOAD_WAIT_TIMEOUT_SECONDS=600 \
  CHILD_SYNC_INTERVAL_HOURS=12 \
  CHILD_LIBRARY_PATH="/config/mirror" \
  bash "$ENTRYPOINT" 2> "${dir}/stderr.log"
full="${dir}/childserver.xml"

if [ -f "$full" ]; then ok "config file created"; else no "config file created" "$(cat "${dir}/stderr.log")"; fi

if python3 -c "import xml.etree.ElementTree as ET,sys; ET.parse(sys.argv[1])" "$full" 2> /dev/null; then
  ok "well formed XML"
else
  no "well formed XML" "$(python3 -c "import xml.etree.ElementTree as ET,sys; ET.parse(sys.argv[1])" "$full" 2>&1 | tail -1)"
fi

root=$(python3 -c "import xml.etree.ElementTree as ET,sys; print(ET.parse(sys.argv[1]).getroot().tag)" "$full")
if [ "$root" = "ChildServerConfiguration" ]; then
  ok "root element is ChildServerConfiguration"
else
  no "root element is ChildServerConfiguration" "got <$root>"
fi

for pair in \
  "ParentUrl=https://parent.example.com" \
  "Username=remote" \
  "PrefetchEpisodeCount=6" \
  "MaxCacheSizeMb=2048" \
  "DownloadWaitTimeoutSeconds=600" \
  "SyncIntervalHours=12" \
  "LibraryPath=/config/mirror"; do
  element="${pair%%=*}"
  expected="${pair#*=}"
  actual=$(value_of "$full" "$element")
  if [ "$actual" = "$expected" ]; then ok "$element = $expected"; else no "$element" "expected '$expected', got '$actual'"; fi
done

# The escaping matters: an unescaped & makes the whole file unparseable, and a password with one
# in it is not unusual. The parser returning the original string proves the round trip.
actual=$(value_of "$full" "Password")
if [ "$actual" = 'p@ss & <word>' ]; then
  ok "Password survives XML escaping (& and <>)"
else
  no "Password escaping" "got '$actual'"
fi

headers=$(python3 - "$full" <<'PY'
import sys, xml.etree.ElementTree as ET
root = ET.parse(sys.argv[1]).getroot()
for h in root.findall('./CustomHeaders/ParentRequestHeader'):
    print(f"{h.findtext('Name')}={h.findtext('Value')}")
PY
)
expected_headers=$'CF-Access-Client-Id=abc123.access\nCF-Access-Client-Secret=cfast_secret'
if [ "$headers" = "$expected_headers" ]; then
  ok "Cloudflare headers become ParentRequestHeader entries"
else
  no "Cloudflare headers" "got: $(tr '\n' ' ' <<< "$headers")"
fi

echo
echo "entrypoint: CHILD_PARENT_HEADERS syntax"
dir="${WORK}/headers"
mkdir -p "$dir"
JELLYFIN_CONFIG_DIR="$dir" CHILD_SERVER_CONFIG_ONLY=1 \
  CHILD_PARENT_URL="https://p.example.com" \
  CHILD_PARENT_HEADERS='X-One: first; X-Two: https://has:colons/in-value' \
  bash "$ENTRYPOINT" 2> "${dir}/stderr.log"
headers=$(python3 - "${dir}/childserver.xml" <<'PY'
import sys, xml.etree.ElementTree as ET
root = ET.parse(sys.argv[1]).getroot()
for h in root.findall('./CustomHeaders/ParentRequestHeader'):
    print(f"{h.findtext('Name')}={h.findtext('Value')}")
PY
)
if [ "$headers" = $'X-One=first\nX-Two=https://has:colons/in-value' ]; then
  ok "semicolon separated pairs, and only the first colon splits"
else
  no "header parsing" "got: $(tr '\n' ' ' <<< "$headers")"
fi

echo
echo "entrypoint: secrets from files"
dir="${WORK}/secretfile"
mkdir -p "$dir"
printf 'from-a-file\n' > "${dir}/pw"          # trailing newline is deliberate
printf 'id-from-file.access\n' > "${dir}/cfid"
JELLYFIN_CONFIG_DIR="$dir" CHILD_SERVER_CONFIG_ONLY=1 \
  CHILD_PARENT_URL="https://p.example.com" \
  CHILD_PARENT_PASSWORD="inline-loses" \
  CHILD_PARENT_PASSWORD_FILE="${dir}/pw" \
  CF_ACCESS_CLIENT_ID_FILE="${dir}/cfid" \
  bash "$ENTRYPOINT" 2> "${dir}/stderr.log"
pw=$(value_of "${dir}/childserver.xml" "Password")
if [ "$pw" = "from-a-file" ]; then
  ok "_FILE wins over the inline value, trailing newline stripped"
else
  no "_FILE secret" "got '$pw'"
fi
cfid=$(python3 -c "
import sys, xml.etree.ElementTree as ET
r = ET.parse(sys.argv[1]).getroot()
print(r.findtext('./CustomHeaders/ParentRequestHeader/Value') or '')
" "${dir}/childserver.xml")
if [ "$cfid" = "id-from-file.access" ]; then ok "CF_ACCESS_CLIENT_ID_FILE read"; else no "CF id from file" "got '$cfid'"; fi

echo
echo "entrypoint: rejects bad input"
dir="${WORK}/bad"
mkdir -p "$dir"
out=$(JELLYFIN_CONFIG_DIR="$dir" CHILD_SERVER_CONFIG_ONLY=1 \
  CHILD_PARENT_URL="https://p.example.com" CHILD_MAX_CACHE_SIZE_MB="not-a-number" \
  bash "$ENTRYPOINT" 2>&1)
if [ $? -ne 0 ] && grep -q "must be an integer" <<< "$out"; then
  ok "a non-numeric size fails loudly"
else
  no "non-numeric size" "exit ok or wrong message: $out"
fi

out=$(JELLYFIN_CONFIG_DIR="$dir" CHILD_SERVER_CONFIG_ONLY=1 \
  CHILD_PARENT_URL="https://p.example.com" CHILD_PARENT_HEADERS="no-colon-here" \
  bash "$ENTRYPOINT" 2>&1)
if [ $? -ne 0 ] && grep -q "is not 'Name: value'" <<< "$out"; then
  ok "a malformed header fails loudly"
else
  no "malformed header" "exit ok or wrong message: $out"
fi

out=$(JELLYFIN_CONFIG_DIR="$dir" CHILD_SERVER_CONFIG_ONLY=1 \
  CHILD_PARENT_URL="https://p.example.com" CHILD_PARENT_PASSWORD_FILE="/nope/missing" \
  bash "$ENTRYPOINT" 2>&1)
if [ $? -ne 0 ] && grep -q "cannot be read" <<< "$out"; then
  ok "an unreadable secret file fails loudly"
else
  no "missing secret file" "exit ok or wrong message: $out"
fi

echo
echo "entrypoint: credentials without a URL are not silently dropped"
dir="${WORK}/nourl"
mkdir -p "$dir"
out=$(env -u CHILD_PARENT_URL JELLYFIN_CONFIG_DIR="$dir" CHILD_SERVER_CONFIG_ONLY=1 \
  CF_ACCESS_CLIENT_ID="abc.access" bash "$ENTRYPOINT" 2>&1)
if grep -q "CHILD_PARENT_URL is not" <<< "$out"; then
  ok "warns when a token is set but the URL is not"
else
  no "missing URL warning" "got: $out"
fi

echo
echo "entrypoint: every element matches a real property of ChildServerConfiguration"
mapfile -t properties < <(sed -n 's/^[[:space:]]*public .* \([A-Za-z0-9_]*\) { get; set; }.*/\1/p' "$MODEL")
mapfile -t header_properties < <(sed -n 's/^[[:space:]]*public .* \([A-Za-z0-9_]*\) { get; set; }.*/\1/p' \
  MediaBrowser.Model/ChildServer/ParentRequestHeader.cs)
if [ "${#properties[@]}" -lt 8 ]; then
  no "parsed the model" "only found ${#properties[@]} properties in $MODEL"
else
  ok "parsed ${#properties[@]} properties from the C# model"
fi

mapfile -t elements < <(python3 - "$full" <<'PY'
import sys, xml.etree.ElementTree as ET
root = ET.parse(sys.argv[1]).getroot()
for child in root:
    print(child.tag)
PY
)
unknown=()
for element in "${elements[@]}"; do
  # shellcheck disable=SC2076
  [[ " ${properties[*]} " =~ " ${element} " ]] || unknown+=("$element")
done
if [ "${#unknown[@]}" -eq 0 ]; then
  ok "all ${#elements[@]} emitted elements exist on the model"
else
  no "emitted elements exist on the model" "not properties: ${unknown[*]}"
fi

for element in Name Value; do
  # shellcheck disable=SC2076
  if [[ " ${header_properties[*]} " =~ " ${element} " ]]; then
    ok "ParentRequestHeader.$element exists on the model"
  else
    no "ParentRequestHeader.$element" "not a property of ParentRequestHeader"
  fi
done

# A secret that reached the log would defeat the point of the _FILE support.
if grep -qE "cfast_secret|p@ss|from-a-file" "${WORK}/full/stderr.log" "${WORK}/secretfile/stderr.log" 2> /dev/null; then
  no "secrets stay out of the log" "a secret value appears in stderr"
else
  ok "secrets stay out of the log"
fi

echo
printf 'passed %d, failed %d\n' "$PASS" "$FAIL"
[ "$FAIL" -eq 0 ]
