#!/bin/bash
# Exercise the session reducer and actual response handling without launching a game or helper.
set -euo pipefail
native_dir=$(cd "$(dirname "$0")" && pwd)
test_dir=$(mktemp -d "${TMPDIR:-/tmp}/ww-session-tests.XXXXXX")
trap 'rm -rf "$test_dir"' EXIT
swiftc -module-cache-path "$test_dir/cache" "$native_dir/Models/SessionState.swift" \
  "$native_dir/Tests/SessionStateTests.swift" -o "$test_dir/state-tests"
"$test_dir/state-tests"
swiftc -module-cache-path "$test_dir/cache" "$native_dir/Models/SessionState.swift" \
  "$native_dir/Models/Models.swift" "$native_dir/Models/Activity.swift" \
  "$native_dir/Models/Session.swift" "$native_dir/Tests/SessionTests.swift" -o "$test_dir/session-tests"
"$test_dir/session-tests"
