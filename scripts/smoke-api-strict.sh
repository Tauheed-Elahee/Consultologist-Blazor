#!/usr/bin/env bash
set -euo pipefail

BASE_URL="${BASE_URL:-https://canada-east-ai-function-gmenbbe9erewh4bj.canadaeast-01.azurewebsites.net}"
ALLOWED_ORIGIN="${ALLOWED_ORIGIN:-https://app.consultologist.ai}"

tmp_dir="$(mktemp -d)"
trap 'rm -rf "$tmp_dir"' EXIT

fail() {
  echo "FAIL: $*" >&2
  exit 1
}

pass() {
  echo "PASS: $*"
}

request() {
  local name="$1"
  shift

  local headers="$tmp_dir/$name.headers"
  local body="$tmp_dir/$name.body"
  local status

  status="$(curl -sS -o "$body" -D "$headers" -w "%{http_code}" "$@")"
  printf '%s\n' "$status" > "$tmp_dir/$name.status"
}

status_of() {
  cat "$tmp_dir/$1.status"
}

body_of() {
  cat "$tmp_dir/$1.body"
}

header_of() {
  local name="$1"
  local header="$2"

  awk -v h="$header" 'BEGIN { IGNORECASE = 1 } index($0, h ":") == 1 { sub(/^[^:]+:[[:space:]]*/, ""); sub(/\r$/, ""); print; exit }' "$tmp_dir/$name.headers"
}

assert_status() {
  local name="$1"
  local expected="$2"
  local actual

  actual="$(status_of "$name")"
  [[ "$actual" == "$expected" ]] || fail "$name expected status $expected, got $actual; body=$(body_of "$name")"
}

assert_body() {
  local name="$1"
  local expected="$2"
  local actual

  actual="$(body_of "$name")"
  [[ "$actual" == "$expected" ]] || fail "$name expected body '$expected', got '$actual'"
}

assert_empty_body() {
  local name="$1"
  local size

  size="$(wc -c < "$tmp_dir/$name.body" | tr -d ' ')"
  [[ "$size" == "0" ]] || fail "$name expected empty body, got $size bytes: $(body_of "$name")"
}

assert_header_contains() {
  local name="$1"
  local header="$2"
  local expected="$3"
  local actual

  actual="$(header_of "$name" "$header")"
  [[ "$actual" == *"$expected"* ]] || fail "$name expected header $header to contain '$expected', got '$actual'"
}

assert_json() {
  local name="$1"
  local expression="$2"
  local description="$3"

  python3 - "$tmp_dir/$name.body" "$expression" "$description" <<'PY'
import json
import sys

path, expression, description = sys.argv[1:]
with open(path, "r", encoding="utf-8") as handle:
    payload = json.load(handle)

allowed_builtins = {
    "isinstance": isinstance,
    "len": len,
    "str": str,
}

if not eval(expression, {"__builtins__": allowed_builtins}, {"payload": payload}):
    raise SystemExit(f"{description}; payload={payload!r}")
PY
}

request aspnet_strict "$BASE_URL/api/AspNetStrictProbe"
assert_status aspnet_strict 200
assert_header_contains aspnet_strict Content-Type "text/plain"
assert_body aspnet_strict "ok"
pass "AspNetStrictProbe returns exact 200 text body"

request http_strict "$BASE_URL/api/HttpStrictProbe"
assert_status http_strict 200
assert_header_contains http_strict Content-Type "text/plain"
assert_body http_strict "ok"
pass "HttpStrictProbe returns exact 200 text body"

request http_no_content_strict "$BASE_URL/api/HttpNoContentStrictProbe"
assert_status http_no_content_strict 204
assert_header_contains http_no_content_strict X-Consultologist-Probe "HttpNoContentStrictProbe"
assert_empty_body http_no_content_strict
pass "HttpNoContentStrictProbe returns exact 204 empty body with marker header"

request aspnet_probe "$BASE_URL/api/AspNetProbe"
assert_status aspnet_probe 200
assert_body aspnet_probe "ok"
pass "AspNetProbe remains healthy"

request http_probe "$BASE_URL/api/HttpProbe"
assert_status http_probe 204
assert_empty_body http_probe
pass "HttpProbe remains healthy"

request http_string_probe "$BASE_URL/api/HttpStringProbe"
assert_status http_string_probe 200
pass "HttpStringProbe remains reachable"

request agent_proxy_invalid \
  -X POST \
  -H "Content-Type: application/json" \
  -d '{}' \
  "$BASE_URL/api/AgentProxy"
assert_status agent_proxy_invalid 400
assert_header_contains agent_proxy_invalid Content-Type "application/json"
assert_json agent_proxy_invalid 'payload.get("Success") is False' "AgentProxy invalid response should have Success=false"
assert_json agent_proxy_invalid 'payload.get("Error") == "Invalid request: ConsultDraft, SectionName, and SectionStandard are required"' "AgentProxy invalid response should include exact validation error"
pass "AgentProxy invalid POST returns exact 400 validation JSON"

request consult_generation_invalid \
  -X POST \
  -H "Content-Type: application/json" \
  -d '{}' \
  "$BASE_URL/api/ConsultGeneration"
assert_status consult_generation_invalid 400
assert_header_contains consult_generation_invalid Content-Type "application/json"
assert_json consult_generation_invalid 'payload.get("Success") is False' "ConsultGeneration invalid response should have Success=false"
assert_json consult_generation_invalid 'payload.get("FailedSections", {}).get("request") == "ConsultDraft is required."' "ConsultGeneration invalid response should include exact validation error"
pass "ConsultGeneration invalid POST returns exact 400 validation JSON"

request consult_generation_cors \
  -X OPTIONS \
  -H "Origin: $ALLOWED_ORIGIN" \
  -H "Access-Control-Request-Method: POST" \
  -H "Access-Control-Request-Headers: Content-Type, Authorization" \
  "$BASE_URL/api/ConsultGeneration"
assert_status consult_generation_cors 204
assert_header_contains consult_generation_cors Access-Control-Allow-Origin "$ALLOWED_ORIGIN"
assert_header_contains consult_generation_cors Access-Control-Allow-Methods "POST"
assert_header_contains consult_generation_cors Access-Control-Allow-Headers "Content-Type"
assert_header_contains consult_generation_cors Access-Control-Allow-Headers "Authorization"
pass "ConsultGeneration CORS preflight returns expected platform CORS headers"

request consult_generation_jobs_cors \
  -X OPTIONS \
  -H "Origin: $ALLOWED_ORIGIN" \
  -H "Access-Control-Request-Method: POST" \
  -H "Access-Control-Request-Headers: Content-Type, Authorization" \
  "$BASE_URL/api/ConsultGenerationJobs"
assert_status consult_generation_jobs_cors 204
assert_header_contains consult_generation_jobs_cors Access-Control-Allow-Origin "$ALLOWED_ORIGIN"
assert_header_contains consult_generation_jobs_cors Access-Control-Allow-Methods "POST"
assert_header_contains consult_generation_jobs_cors Access-Control-Allow-Headers "Content-Type"
assert_header_contains consult_generation_jobs_cors Access-Control-Allow-Headers "Authorization"
pass "ConsultGenerationJobs CORS preflight returns expected platform CORS headers"

request consult_generation_jobs_invalid \
  -X POST \
  -H "Content-Type: application/json" \
  -d '{}' \
  "$BASE_URL/api/ConsultGenerationJobs"
assert_status consult_generation_jobs_invalid 400
assert_header_contains consult_generation_jobs_invalid Content-Type "application/json"
assert_json consult_generation_jobs_invalid 'payload.get("error") == "ConsultDraft is required."' "ConsultGenerationJobs invalid response should include exact validation error"
pass "ConsultGenerationJobs invalid POST returns exact 400 validation JSON"

request consult_generation_jobs_not_found "$BASE_URL/api/ConsultGenerationJobs/not-found"
assert_status consult_generation_jobs_not_found 404
assert_header_contains consult_generation_jobs_not_found Content-Type "application/json"
assert_json consult_generation_jobs_not_found 'payload.get("error") == "Consult generation job was not found."' "ConsultGenerationJobs not-found response should include exact error"
pass "ConsultGenerationJobs not-found lookup returns exact 404 JSON"

request consult_generation_job_events_not_found "$BASE_URL/api/ConsultGenerationJobs/not-found/events"
assert_status consult_generation_job_events_not_found 404
assert_header_contains consult_generation_job_events_not_found Content-Type "application/json"
assert_json consult_generation_job_events_not_found 'payload.get("error") == "Consult generation job was not found."' "ConsultGenerationJobs events not-found response should include exact error"
pass "ConsultGenerationJobs events not-found lookup returns exact 404 JSON"

if [[ "${RUN_VALID_AGENT_PROXY:-0}" == "1" ]]; then
  request agent_proxy_valid \
    -X POST \
    -H "Content-Type: application/json" \
    -d '{"ConsultDraft":"Patient has fatigue.","SectionName":"History","SectionStandard":"Write one concise clinical sentence."}' \
    "$BASE_URL/api/AgentProxy"
  assert_status agent_proxy_valid 200
  assert_header_contains agent_proxy_valid Content-Type "application/json"
  assert_json agent_proxy_valid 'payload.get("Success") is True' "AgentProxy valid response should have Success=true"
  assert_json agent_proxy_valid 'isinstance(payload.get("Response"), str) and len(payload.get("Response").strip()) > 0' "AgentProxy valid response should include non-empty Response"
  pass "AgentProxy valid POST returns successful generation JSON"
else
  echo "SKIP: AgentProxy valid Foundry call. Set RUN_VALID_AGENT_PROXY=1 to enable."
fi

if [[ "${RUN_VALID_DURABLE_JOB:-0}" == "1" ]]; then
  request durable_job_valid \
    -X POST \
    -H "Content-Type: application/json" \
    -d '{"ConsultDraft":"Patient has fatigue.","Sections":[{"Id":"history","Name":"History","Standard":"Write one concise clinical sentence."}]}' \
    "$BASE_URL/api/ConsultGenerationJobs"
  assert_status durable_job_valid 202
  assert_header_contains durable_job_valid Content-Type "application/json"
  assert_json durable_job_valid 'isinstance(payload.get("JobId"), str) and len(payload.get("JobId")) > 0' "Durable job start should return JobId"
  assert_json durable_job_valid 'isinstance(payload.get("StatusUrl"), str) and payload.get("StatusUrl").endswith(payload.get("JobId"))' "Durable job start should return matching StatusUrl"

  status_url="$(python3 - "$tmp_dir/durable_job_valid.body" <<'PY'
import json
import sys
with open(sys.argv[1], "r", encoding="utf-8") as handle:
    print(json.load(handle)["StatusUrl"])
PY
)"

  terminal_status=""
  for _ in $(seq 1 24); do
    request durable_job_status "$status_url"
    assert_status durable_job_status 200
    terminal_status="$(python3 - "$tmp_dir/durable_job_status.body" <<'PY'
import json
import sys
with open(sys.argv[1], "r", encoding="utf-8") as handle:
    payload = json.load(handle)
print(payload.get("Status", ""))
PY
)"

    if [[ "$terminal_status" == "Completed" || "$terminal_status" == "Failed" ]]; then
      break
    fi

    sleep 5
  done

  [[ "$terminal_status" == "Completed" || "$terminal_status" == "Failed" ]] || fail "Durable job did not reach a terminal state; last body=$(body_of durable_job_status)"
  assert_json durable_job_status 'payload.get("TotalSectionCount") == 1' "Durable job status should track one section"
  pass "ConsultGenerationJobs valid POST reaches terminal status: $terminal_status"
else
  echo "SKIP: Durable valid job. Set RUN_VALID_DURABLE_JOB=1 to enable."
fi

if [[ "${RUN_VALID_DURABLE_JOB_SSE:-0}" == "1" ]]; then
  request durable_job_sse_start \
    -X POST \
    -H "Content-Type: application/json" \
    -d '{"ConsultDraft":"Patient has fatigue.","Sections":[{"Id":"history","Name":"History","Standard":"Write one concise clinical sentence."}]}' \
    "$BASE_URL/api/ConsultGenerationJobs"
  assert_status durable_job_sse_start 202
  assert_json durable_job_sse_start 'isinstance(payload.get("JobId"), str) and len(payload.get("JobId")) > 0' "Durable SSE job start should return JobId"

  events_url="$(python3 - "$tmp_dir/durable_job_sse_start.body" <<'PY'
import json
import sys
with open(sys.argv[1], "r", encoding="utf-8") as handle:
    payload = json.load(handle)
print(f'{payload["StatusUrl"]}/events')
PY
)"

  request durable_job_sse_stream \
    --max-time 120 \
    -N \
    "$events_url"
  assert_status durable_job_sse_stream 200
  assert_header_contains durable_job_sse_stream Content-Type "text/event-stream"

  sse_body="$(body_of durable_job_sse_stream)"
  [[ "$sse_body" == *"event: snapshot"* ]] || fail "Durable SSE stream did not include snapshot; body=$sse_body"
  python3 - "$tmp_dir/durable_job_sse_stream.body" <<'PY'
import json
import sys

path = sys.argv[1]
events = []
current_event = None
current_data = []

with open(path, "r", encoding="utf-8") as handle:
    for raw_line in handle:
        line = raw_line.rstrip("\n")
        if line == "":
            if current_event is not None:
                events.append((current_event, "\n".join(current_data)))
            current_event = None
            current_data = []
            continue

        if line.startswith("event: "):
            current_event = line.removeprefix("event: ")
        elif line.startswith("data: "):
            current_data.append(line.removeprefix("data: "))

for event_name, data in events:
    if event_name == "snapshot":
        payload = json.loads(data)
        if payload.get("TotalSectionCount") != 1:
            raise SystemExit(f"Durable SSE snapshot should be entity-backed with TotalSectionCount=1; payload={payload!r}")
        break
else:
    raise SystemExit("Durable SSE stream did not include a parseable snapshot event")
PY
  [[ "$sse_body" == *"event: section-completed"* || "$sse_body" == *"event: section-failed"* ]] || fail "Durable SSE stream did not include a section event; body=$sse_body"
  [[ "$sse_body" == *"event: done"* ]] || fail "Durable SSE stream did not include done; body=$sse_body"
  pass "ConsultGenerationJobs valid SSE stream emits snapshot, section progress, and done"
else
  echo "SKIP: Durable valid SSE stream. Set RUN_VALID_DURABLE_JOB_SSE=1 to enable."
fi
