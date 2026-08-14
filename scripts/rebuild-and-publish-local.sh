#!/usr/bin/env bash
# Rebuild and replace the local MaterialLedger instance on port 59406.
set -euo pipefail

repo=/home/aj/src/MaterialLedger
runtime="$repo/.run"
port=59406
url="http://127.0.0.1:${port}/"
lan_url="http://hermes.lan:${port}/"
log_dir="$runtime/logs"
pid_file="$runtime/materialledger.pid"
lock_file="$runtime/rebuild-and-publish.lock"

mkdir -p "$log_dir"
exec 9>"$lock_file"
flock -n 9 || { echo "A rebuild/publish is already running." >&2; exit 1; }

[[ -x "$runtime/start-local.sh" ]] || { echo "Missing executable startup script: $runtime/start-local.sh" >&2; exit 1; }
[[ -f "$runtime/local-app.env" && -f "$runtime/dev-sql.env" ]] || {
  echo "Missing local runtime environment file(s) under $runtime." >&2
  exit 1
}

# Docker does not automatically restart this ad-hoc local SQL Server after a
# host/Docker restart. Ensure the database backing the local app is available
# before performing an expensive build and launching the web process.
db_container=materialledger-temp-sql
db_port=14339
if ! docker container inspect "$db_container" >/dev/null 2>&1; then
  echo "Missing local SQL Server container: $db_container" >&2
  exit 1
fi

db_status="$(docker container inspect --format '{{.State.Status}}' "$db_container")"
case "$db_status" in
  running) ;;
  created|exited)
    echo "Starting local SQL Server container ($db_container)..."
    docker start "$db_container" >/dev/null
    ;;
  *)
    echo "Local SQL Server container $db_container is in unexpected state: $db_status" >&2
    exit 1
    ;;
esac

for _ in {1..60}; do
  if sudo lsof -nP -t -iTCP:"$db_port" -sTCP:LISTEN >/dev/null 2>&1; then
    break
  fi
  sleep 1
done
if ! sudo lsof -nP -t -iTCP:"$db_port" -sTCP:LISTEN >/dev/null 2>&1; then
  echo "Local SQL Server did not listen on port $db_port within 60 seconds." >&2
  echo "Check with: docker logs $db_container" >&2
  exit 1
fi

cd "$repo"
echo "Building MaterialLedger (Debug)..."
dotnet restore MaterialLedger.sln
dotnet build MaterialLedger.Web/MaterialLedger.Web.csproj --configuration Debug --no-restore

# Refuse to displace an unrelated application that happens to use this port.
old_pids="$(sudo lsof -nP -t -iTCP:"$port" -sTCP:LISTEN 2>/dev/null || true)"
for pid in $old_pids; do
  command="$(tr '\0' ' ' </proc/"$pid"/cmdline 2>/dev/null || true)"
  if [[ "$command" != *"$repo"* ]]; then
    echo "Port $port is owned by an unrelated process (PID $pid): $command" >&2
    echo "Refusing to stop it." >&2
    exit 1
  fi
  echo "Stopping existing MaterialLedger process (PID $pid)..."
  kill -TERM "$pid"
done

for _ in {1..30}; do
  if ! sudo lsof -nP -t -iTCP:"$port" -sTCP:LISTEN >/dev/null 2>&1; then
    break
  fi
  sleep 1
done
if sudo lsof -nP -t -iTCP:"$port" -sTCP:LISTEN >/dev/null 2>&1; then
  echo "Port $port did not become available after 30 seconds." >&2
  exit 1
fi

log_file="$log_dir/materialledger-$(date +%Y%m%d-%H%M%S).log"
echo "Starting MaterialLedger on port $port; log: $log_file"
# Do not let the background app or MSBuild node inherit the publish lock.
# An inherited lock would make every later publish incorrectly report itself busy.
flock -u 9
exec 9>&-
nohup "$runtime/start-local.sh" >"$log_file" 2>&1 < /dev/null &
new_pid=$!
echo "$new_pid" >"$pid_file"

for _ in {1..30}; do
  if curl --silent --show-error --fail --max-time 2 -o /dev/null "$url"; then
    if curl --silent --show-error --fail --max-time 2 -o /dev/null "$lan_url"; then
      echo "Published successfully: $lan_url (launcher PID $new_pid)"
      exit 0
    fi
    echo "MaterialLedger is listening locally but is not reachable at $lan_url" >&2
    exit 1
  fi
  if ! kill -0 "$new_pid" 2>/dev/null; then
    echo "MaterialLedger exited during startup. See $log_file" >&2
    exit 1
  fi
  sleep 1
done

echo "MaterialLedger did not become healthy within 30 seconds. See $log_file" >&2
exit 1
