#!/usr/bin/env bash
set -euo pipefail

BASE_URL="${BASE_URL:-https://canada-east-ai-function-gmenbbe9erewh4bj.canadaeast-01.azurewebsites.net}"
ACCESS_TOKEN="${ACCESS_TOKEN:-}"

fail() {
  echo "FAIL: $*" >&2
  exit 1
}

pass() {
  echo "PASS: $*"
}

http_status() {
  local method="$1"
  local url="$2"
  shift 2
  curl -sS -o /tmp/consultologist-smoke-body.txt -w "%{http_code}" -X "$method" "$url" "$@"
}

assert_status() {
  local actual="$1"
  local expected="$2"
  local label="$3"

  if [[ "$actual" != "$expected" ]]; then
    echo "Response body:" >&2
    cat /tmp/consultologist-smoke-body.txt >&2 || true
    fail "$label expected HTTP $expected, got $actual"
  fi

  pass "$label returned HTTP $expected"
}

assert_status_one_of() {
  local actual="$1"
  local expected="$2"
  local label="$3"

  if [[ ",$expected," != *",$actual,"* ]]; then
    echo "Response body:" >&2
    cat /tmp/consultologist-smoke-body.txt >&2 || true
    fail "$label expected one of HTTP $expected, got $actual"
  fi

  pass "$label returned HTTP $actual"
}

echo "Consultologist auth smoke"
echo "Base URL: $BASE_URL"

status="$(http_status GET "$BASE_URL/api/Account/Me")"
assert_status "$status" "401" "Anonymous account endpoint"

status="$(http_status GET "$BASE_URL/api/Account/Settings/consult.sectionStandardsMarkdown")"
assert_status "$status" "401" "Anonymous account setting endpoint"

status="$(http_status POST "$BASE_URL/api/ConsultGenerationJobs" \
  -H "Content-Type: application/json" \
  --data '{"consultDraft":"Auth smoke test","sections":[{"id":"history","name":"History","standard":"Write a concise history."}]}')"
assert_status "$status" "401" "Anonymous job creation"

if [[ -z "$ACCESS_TOKEN" ]]; then
  echo "SKIP: Authenticated checks. Set ACCESS_TOKEN to a valid Consultologist API bearer token."
  exit 0
fi

status="$(http_status GET "$BASE_URL/api/Account/Me" \
  -H "Authorization: Bearer $ACCESS_TOKEN")"
assert_status "$status" "200" "Authenticated account endpoint"

status="$(http_status GET "$BASE_URL/api/Account/Settings/consult.sectionStandardsMarkdown" \
  -H "Authorization: Bearer $ACCESS_TOKEN")"
assert_status_one_of "$status" "200,404" "Authenticated account setting endpoint"

status="$(http_status POST "$BASE_URL/api/ConsultGenerationJobs" \
  -H "Authorization: Bearer $ACCESS_TOKEN" \
  -H "Content-Type: application/json" \
  --data '{"consultDraft":"Auth smoke test","sections":[{"id":"history","name":"History","standard":"Write a concise history."}]}')"
assert_status "$status" "202" "Authenticated job creation"

job_id="$(python3 - <<'PY'
import json
from pathlib import Path

payload = json.loads(Path('/tmp/consultologist-smoke-body.txt').read_text())
print(payload.get('jobId') or payload.get('JobId') or '')
PY
)"

[[ -n "$job_id" ]] || fail "Authenticated job creation did not return a job ID"
pass "Authenticated job returned job ID $job_id"

status="$(http_status GET "$BASE_URL/api/ConsultGenerationJobs/$job_id" \
  -H "Authorization: Bearer $ACCESS_TOKEN")"
assert_status "$status" "200" "Authenticated job polling"
