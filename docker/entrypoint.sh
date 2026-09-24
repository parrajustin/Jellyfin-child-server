#!/bin/bash
#
# Seeds the child server's parent configuration from the environment, then starts the server.
#
# The server itself stores this in <config>/childserver.xml, written by the dashboard page or the
# API. A container has no dashboard on first boot, so without this an operator has to bring the
# server up unconfigured, open the UI and type the parent URL and a Cloudflare Access service
# token by hand. That also means the secret can never come from a Docker secret or a Kubernetes
# Secret, only from a human.
#
# Semantics: the environment wins. When CHILD_PARENT_URL is set, childserver.xml is rewritten on
# every boot from the environment, and any edit made in the UI is replaced. That is deliberate —
# a container whose configuration depends on which boot last touched the UI is worse than one
# that always reflects its env. Set no CHILD_PARENT_* variables and the file is left alone, so
# the UI remains fully in charge.
#
# AccessToken and the cached parent identity are not written. The server signs in again with the
# stored password when it has no usable token, so the only cost of dropping them is one extra
# sign in after a restart.
#
# Every secret also has a _FILE form, read from the named path, for Docker and Kubernetes
# secrets. The _FILE form wins when both are set.
set -euo pipefail

CONFIG_DIR="${JELLYFIN_CONFIG_DIR:-/config/config}"
CONFIG_FILE="${CONFIG_DIR}/childserver.xml"

log() { printf '[child-server] %s\n' "$*" >&2; }

# Reads VAR, or the contents of VAR_FILE when that is set. Trailing newlines are stripped, which
# matters because `echo secret > file` is how people make these.
read_secret() {
  local name="$1" file_var="${1}_FILE" path value
  path="${!file_var:-}"
  if [ -n "$path" ]; then
    if [ ! -r "$path" ]; then
      log "error: ${file_var} points at ${path}, which cannot be read"
      exit 1
    fi
    value=$(< "$path")
    printf '%s' "${value%$'\n'}"
    return
  fi
  printf '%s' "${!name:-}"
}

# Escaping is done with sed rather than ${s//&/&amp;}, because in bash's pattern substitution an
# unescaped & in the replacement stands for the matched text: `${s//</&lt;}` turns "<" into "<lt;".
# The & substitution has to come first, or it would escape the ampersands the others introduce.
xml_escape() {
  printf '%s' "$1" | sed -e 's/&/\&amp;/g' -e 's/</\&lt;/g' -e 's/>/\&gt;/g' -e 's/"/\&quot;/g'
}

# Emits <Name>value</Name> only when the value is non-empty, so an unset variable leaves the
# server's own default in place rather than overwriting it with "".
emit() {
  local element="$1" value="$2"
  [ -n "$value" ] || return 0
  printf '  <%s>%s</%s>\n' "$element" "$(xml_escape "$value")" "$element"
}

emit_int() {
  local element="$1" value="$2"
  [ -n "$value" ] || return 0
  if ! [[ "$value" =~ ^-?[0-9]+$ ]]; then
    log "error: ${element} must be an integer, got '${value}'"
    exit 1
  fi
  printf '  <%s>%s</%s>\n' "$element" "$value" "$element"
}

# CHILD_PARENT_HEADERS is "Name: value" pairs separated by newlines or semicolons. The value may
# contain colons; only the first one splits.
emit_headers() {
  local raw="$1" id="$2" secret="$3"
  local -a names=() values=()

  if [ -n "$raw" ]; then
    local line name value
    while IFS= read -r line; do
      line="${line#"${line%%[![:space:]]*}"}"
      line="${line%"${line##*[![:space:]]}"}"
      [ -n "$line" ] || continue
      if [[ "$line" != *:* ]]; then
        log "error: CHILD_PARENT_HEADERS entry '${line}' is not 'Name: value'"
        exit 1
      fi
      name="${line%%:*}"
      value="${line#*:}"
      name="${name%"${name##*[![:space:]]}"}"
      value="${value#"${value%%[![:space:]]*}"}"
      if [[ "$name" =~ [[:space:]] ]]; then
        log "error: header name '${name}' contains whitespace"
        exit 1
      fi
      names+=("$name")
      values+=("$value")
    done <<< "${raw//;/$'\n'}"
  fi

  # The Cloudflare pair is the overwhelmingly common case, so it gets its own variables rather
  # than making everyone hand-write the header syntax.
  if [ -n "$id" ]; then
    names+=("CF-Access-Client-Id")
    values+=("$id")
  fi
  if [ -n "$secret" ]; then
    names+=("CF-Access-Client-Secret")
    values+=("$secret")
  fi

  [ "${#names[@]}" -gt 0 ] || return 0

  printf '  <CustomHeaders>\n'
  local i
  for i in "${!names[@]}"; do
    printf '    <ParentRequestHeader>\n'
    printf '      <Name>%s</Name>\n' "$(xml_escape "${names[$i]}")"
    printf '      <Value>%s</Value>\n' "$(xml_escape "${values[$i]}")"
    printf '    </ParentRequestHeader>\n'
  done
  printf '  </CustomHeaders>\n'
}

write_config() {
  local password cf_id cf_secret
  password=$(read_secret CHILD_PARENT_PASSWORD)
  cf_id=$(read_secret CF_ACCESS_CLIENT_ID)
  cf_secret=$(read_secret CF_ACCESS_CLIENT_SECRET)
  local headers
  headers=$(read_secret CHILD_PARENT_HEADERS)

  mkdir -p "$CONFIG_DIR"

  # Written to a temp file and moved into place so a crash midway cannot leave the server reading
  # a half-written config.
  local tmp
  tmp=$(mktemp "${CONFIG_FILE}.XXXXXX")
  {
    printf '<?xml version="1.0" encoding="utf-8"?>\n'
    printf '<ChildServerConfiguration xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">\n'
    emit ParentUrl "${CHILD_PARENT_URL:-}"
    emit Username "${CHILD_PARENT_USERNAME:-}"
    emit Password "$password"
    emit_headers "$headers" "$cf_id" "$cf_secret"
    emit_int PrefetchEpisodeCount "${CHILD_PREFETCH_EPISODE_COUNT:-}"
    emit_int MaxCacheSizeMb "${CHILD_MAX_CACHE_SIZE_MB:-}"
    emit_int DownloadWaitTimeoutSeconds "${CHILD_DOWNLOAD_WAIT_TIMEOUT_SECONDS:-}"
    emit_int SyncIntervalHours "${CHILD_SYNC_INTERVAL_HOURS:-}"
    emit LibraryPath "${CHILD_LIBRARY_PATH:-}"
    printf '</ChildServerConfiguration>\n'
  } > "$tmp"
  chmod 600 "$tmp"
  mv "$tmp" "$CONFIG_FILE"

  # Never log the values themselves. Written with if rather than `[ ... ] && ...` because under
  # `set -e` a false test as the last statement of a function aborts the script.
  local described="parent=${CHILD_PARENT_URL}"
  if [ -n "${CHILD_PARENT_USERNAME:-}" ]; then
    described="${described} user=${CHILD_PARENT_USERNAME}"
  fi
  if [ -n "$password" ]; then
    described="${described} password=set"
  fi
  if [ -n "$cf_id" ] || [ -n "$cf_secret" ] || [ -n "$headers" ]; then
    described="${described} headers=set"
  fi
  log "wrote ${CONFIG_FILE} from the environment (${described})"
}

if [ -n "${CHILD_PARENT_URL:-}" ]; then
  write_config
elif [ -n "${CHILD_PARENT_USERNAME:-}${CHILD_PARENT_PASSWORD:-}${CF_ACCESS_CLIENT_ID:-}" ]; then
  log "warning: parent credentials are set but CHILD_PARENT_URL is not, so nothing was written."
  log "warning: set CHILD_PARENT_URL, or configure the parent in the dashboard instead."
else
  log "no CHILD_PARENT_URL set; leaving ${CONFIG_FILE} to the dashboard"
fi

# CHILD_SERVER_CONFIG_ONLY exists for the test in tests/env: it exercises the generation above
# without needing the server or a .NET runtime.
if [ -n "${CHILD_SERVER_CONFIG_ONLY:-}" ]; then
  exit 0
fi

exec dotnet /jellyfin/jellyfin.dll "$@"
