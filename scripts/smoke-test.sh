#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."
dotnet publish BuilderDotnetSample.csproj -c Release -o bin/sample-publish --configfile NuGet.Config

temp_dir=$(mktemp -d)
app_pid=""

stop_app() {
    if [[ -n "$app_pid" ]]; then
        if kill -0 "$app_pid" 2>/dev/null; then
            kill "$app_pid"
        fi
        if wait "$app_pid"; then
            app_pid=""
        else
            local status=$?
            app_pid=""
            printf 'Sample process exited unexpectedly with status %s\n' "$status" >&2
            return 1
        fi
    fi
}

cleanup() {
    local status=$?
    if ! stop_app; then
        status=1
    fi
    rm -f "$temp_dir/app.log" "$temp_dir/body"
    rmdir "$temp_dir"
    return "$status"
}
trap cleanup EXIT

assert_response() {
    local path=$1 expected_status=$2 status
    status=$(curl --silent --show-error --max-time 5 \
        --output "$temp_dir/body" --write-out '%{http_code}' "http://127.0.0.1:8080$path")
    if [[ "$status" != "$expected_status" ]] ||
        { [[ $# -ge 3 ]] && ! printf '%s' "$3" | cmp -s - "$temp_dir/body"; }; then
        printf 'Unexpected response for %s: HTTP %s\n' "$path" "$status" >&2
        cat "$temp_dir/body" >&2
        return 1
    fi
    if ! kill -0 "$app_pid" 2>/dev/null; then
        printf 'Sample process exited during HTTP validation\n' >&2
        cat "$temp_dir/app.log" >&2
        return 1
    fi
}

assert_contains() {
    assert_response "$1" 200
    if ! grep -Fq "$2" "$temp_dir/body"; then
        printf 'Missing expected content for %s: %s\n' "$1" "$2" >&2
        return 1
    fi
}

if curl --silent --max-time 1 --output /dev/null http://127.0.0.1:8080/; then
    printf 'Port 8080 is already serving HTTP; stop that process before testing.\n' >&2
    exit 1
fi

env 'Logging__LogLevel__Microsoft.Hosting.Lifetime=Information' \
    ASPNETCORE_ENVIRONMENT=Development \
    dotnet bin/sample-publish/BuilderDotnetSample.dll --contentRoot "$PWD/bin/sample-publish" \
    > "$temp_dir/app.log" 2>&1 &
app_pid=$!
ready=false
for ((attempt = 0; attempt < 100; attempt++)); do
    if ! kill -0 "$app_pid" 2>/dev/null; then
        break
    fi
    if grep -Fq 'Now listening on: http://0.0.0.0:8080' "$temp_dir/app.log"; then
        ready=true
        break
    fi
    sleep 0.1
done
if [[ "$ready" != true ]]; then
    printf 'Sample failed to bind port 8080 within 10 seconds.\n' >&2
    cat "$temp_dir/app.log" >&2
    exit 1
fi

assert_contains "/" "Two cities."
assert_contains "/styles.css" ".city-grid"
assert_contains "/app.js" "/api/weather/"
assert_contains "/openapi/v1.json" "/api/weather/{city}"
assert_response "/health" 200 $'OK\n'
assert_response "/not-found" 404
assert_response "/api/weather/london" 404
if ! grep -Fq 'Supported cities are redmond and enns.' "$temp_dir/body"; then
    printf 'Unsupported city did not return expected Problem Details.\n' >&2
    exit 1
fi
stop_app
printf 'Passed published page, assets, OpenAPI, health, and 404 checks (no live weather dependency).\n'
