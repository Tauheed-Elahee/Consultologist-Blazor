#!/usr/bin/env bash
set -euo pipefail

BASE_URL="${BASE_URL:-https://canada-east-ai-function-gmenbbe9erewh4bj.canadaeast-01.azurewebsites.net}"
ALLOWED_ORIGIN="${ALLOWED_ORIGIN:-https://app.consultologist.ai}"
VALID_CONSULT_DRAFT="${VALID_CONSULT_DRAFT:-62-year-old woman with newly diagnosed invasive ductal carcinoma of the left breast. Core biopsy shows ER positive, PR positive, HER2 negative breast cancer. She has fatigue but no documented metastatic disease.}"
VALID_SECTION_STANDARD="${VALID_SECTION_STANDARD:-Write one concise clinical history sentence using only documented patient facts.}"

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
  -H "Access-Control-Request-Headers: Content-Type, Authorization, Last-Event-ID" \
  "$BASE_URL/api/ConsultGeneration"
assert_status consult_generation_cors 204
assert_header_contains consult_generation_cors Access-Control-Allow-Origin "$ALLOWED_ORIGIN"
assert_header_contains consult_generation_cors Access-Control-Allow-Methods "POST"
assert_header_contains consult_generation_cors Access-Control-Allow-Headers "Content-Type"
assert_header_contains consult_generation_cors Access-Control-Allow-Headers "Authorization"
assert_header_contains consult_generation_cors Access-Control-Allow-Headers "Last-Event-ID"
pass "ConsultGeneration CORS preflight returns expected platform CORS headers"

request consult_generation_jobs_cors \
  -X OPTIONS \
  -H "Origin: $ALLOWED_ORIGIN" \
  -H "Access-Control-Request-Method: POST" \
  -H "Access-Control-Request-Headers: Content-Type, Authorization, Last-Event-ID" \
  "$BASE_URL/api/ConsultGenerationJobs"
assert_status consult_generation_jobs_cors 204
assert_header_contains consult_generation_jobs_cors Access-Control-Allow-Origin "$ALLOWED_ORIGIN"
assert_header_contains consult_generation_jobs_cors Access-Control-Allow-Methods "POST"
assert_header_contains consult_generation_jobs_cors Access-Control-Allow-Headers "Content-Type"
assert_header_contains consult_generation_jobs_cors Access-Control-Allow-Headers "Authorization"
assert_header_contains consult_generation_jobs_cors Access-Control-Allow-Headers "Last-Event-ID"
pass "ConsultGenerationJobs CORS preflight returns expected platform CORS headers"

request consult_generation_job_events_cors \
  -X OPTIONS \
  -H "Origin: $ALLOWED_ORIGIN" \
  -H "Access-Control-Request-Method: GET" \
  -H "Access-Control-Request-Headers: Authorization, Last-Event-ID" \
  "$BASE_URL/api/ConsultGenerationJobs/not-found/events"
assert_status consult_generation_job_events_cors 200
assert_header_contains consult_generation_job_events_cors Access-Control-Allow-Origin "$ALLOWED_ORIGIN"
assert_header_contains consult_generation_job_events_cors Access-Control-Allow-Methods "GET"
assert_header_contains consult_generation_job_events_cors Access-Control-Allow-Headers "Authorization"
assert_header_contains consult_generation_job_events_cors Access-Control-Allow-Headers "Last-Event-ID"
pass "ConsultGenerationJobs events CORS preflight allows Last-Event-ID"

request consult_generation_jobs_invalid \
  -X POST \
  -H "Content-Type: application/json" \
  -d '{}' \
  "$BASE_URL/api/ConsultGenerationJobs"
assert_status consult_generation_jobs_invalid 400
assert_header_contains consult_generation_jobs_invalid Content-Type "application/json"
assert_json consult_generation_jobs_invalid 'payload.get("error") == "ConsultDraft is required."' "ConsultGenerationJobs invalid response should include exact validation error"
pass "ConsultGenerationJobs invalid POST returns exact 400 validation JSON"

request consult_generation_jobs_invalid_section \
  -X POST \
  -H "Content-Type: application/json" \
  -d '{"ConsultDraft":"Patient has fatigue.","Sections":[{"Id":"","Name":"History","Standard":""}]}' \
  "$BASE_URL/api/ConsultGenerationJobs"
assert_status consult_generation_jobs_invalid_section 400
assert_header_contains consult_generation_jobs_invalid_section Content-Type "application/json"
assert_json consult_generation_jobs_invalid_section 'payload.get("error") == "Each section requires Id and Name."' "ConsultGenerationJobs invalid section response should allow blank Standard but require Id and Name"
pass "ConsultGenerationJobs invalid section validation allows blank Standard"

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

request consult_generation_job_events_invalid_last_event_id \
  -H "Last-Event-ID: malformed" \
  "$BASE_URL/api/ConsultGenerationJobs/not-found/events"
assert_status consult_generation_job_events_invalid_last_event_id 400
assert_header_contains consult_generation_job_events_invalid_last_event_id Content-Type "application/json"
assert_json consult_generation_job_events_invalid_last_event_id 'payload.get("error") == "Invalid Last-Event-ID header."' "ConsultGenerationJobs events invalid Last-Event-ID should return sanitized error"
pass "ConsultGenerationJobs events rejects malformed Last-Event-ID"

request consult_generation_job_events_mismatched_last_event_id \
  -H "Last-Event-ID: other-job:000000000001" \
  "$BASE_URL/api/ConsultGenerationJobs/not-found/events"
assert_status consult_generation_job_events_mismatched_last_event_id 400
assert_header_contains consult_generation_job_events_mismatched_last_event_id Content-Type "application/json"
assert_json consult_generation_job_events_mismatched_last_event_id 'payload.get("error") == "Last-Event-ID does not match the requested job."' "ConsultGenerationJobs events mismatched Last-Event-ID should return sanitized error"
pass "ConsultGenerationJobs events rejects mismatched Last-Event-ID"

if [[ "${RUN_VALID_AGENT_PROXY:-0}" == "1" ]]; then
  request agent_proxy_valid \
    -X POST \
    -H "Content-Type: application/json" \
    -d "$(python3 - "$VALID_CONSULT_DRAFT" "$VALID_SECTION_STANDARD" <<'PY'
import json
import sys
print(json.dumps({
    "ConsultDraft": sys.argv[1],
    "SectionName": "History",
    "SectionStandard": sys.argv[2],
}))
PY
)" \
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
    -d "$(python3 - "$VALID_CONSULT_DRAFT" "$VALID_SECTION_STANDARD" <<'PY'
import json
import sys
print(json.dumps({
    "ConsultDraft": sys.argv[1],
    "Sections": [
        {
            "Id": "history",
            "Name": "History",
            "Standard": sys.argv[2],
        }
    ],
}))
PY
)" \
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
  assert_json durable_job_status 'payload.get("SchemaVersion") == 1' "Durable job status should expose analysis schema version"
  assert_json durable_job_status 'payload.get("TotalStageCount") == 6' "Durable job status should expose total analysis stage count"
  assert_json durable_job_status 'isinstance(payload.get("AnalysisStatus"), str) and "-" in payload.get("AnalysisStatus")' "Durable job status should expose kebab-case analysis status"
  assert_json durable_job_status 'payload.get("AnalysisStatus") == "section-generation-started" or payload.get("AnalysisStatus", "").endswith("-failed")' "Durable terminal job should expose final analysis stage or preprocessing failure"
  assert_json durable_job_status 'payload.get("CompletedStageCount") in (0, 6)' "Durable terminal job should expose completed analysis stage count"
  assert_json durable_job_status '"PatientConcepts" not in payload' "Durable public job status should not expose patient concepts"
  assert_json durable_job_status '"ProblemContext" not in payload' "Durable public job status should not expose problem context"
  assert_json durable_job_status '"TypicalTrajectoryConcepts" not in payload' "Durable public job status should not expose typical trajectory concepts"
  assert_json durable_job_status '"PatientTrajectoryConcepts" not in payload' "Durable public job status should not expose patient trajectory concepts"
  assert_json durable_job_status '"ValidationWarnings" not in payload' "Durable public job status should not expose preprocessing validation warnings"
  if [[ "$terminal_status" == "Completed" ]]; then
    assert_json durable_job_status 'payload.get("Success") is True' "Durable completed job should have Success=true"
    assert_json durable_job_status 'payload.get("SectionProseProgress", {}).get("history", {}).get("CompletedProseStepCount") == 3' "Durable completed job should persist completed prose step count"
    assert_json durable_job_status 'payload.get("SectionProseProgress", {}).get("history", {}).get("TotalProseStepCount") == 3' "Durable completed job should persist total prose step count"
  else
    assert_json durable_job_status 'isinstance(payload.get("AnalysisError"), str) and len(payload.get("AnalysisError")) > 0' "Durable preprocessing failure should expose analysis error"
  fi
  pass "ConsultGenerationJobs valid POST reaches terminal status: $terminal_status"
else
  echo "SKIP: Durable valid job. Set RUN_VALID_DURABLE_JOB=1 to enable."
fi

if [[ "${RUN_VALID_DURABLE_JOB_SSE:-0}" == "1" ]]; then
  request durable_job_sse_start \
    -X POST \
    -H "Content-Type: application/json" \
    -d "$(python3 - "$VALID_CONSULT_DRAFT" "$VALID_SECTION_STANDARD" <<'PY'
import json
import sys
print(json.dumps({
    "ConsultDraft": sys.argv[1],
    "Sections": [
        {
            "Id": "history",
            "Name": "History",
            "Standard": sys.argv[2],
        }
    ],
}))
PY
)" \
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
import re
import sys
from collections import Counter

path = sys.argv[1]
events = []
current_id = None
current_event = None
current_data = []

with open(path, "r", encoding="utf-8") as handle:
    for raw_line in handle:
        line = raw_line.rstrip("\n")
        if line == "":
            if current_event is not None:
                events.append((current_id, current_event, "\n".join(current_data)))
            current_id = None
            current_event = None
            current_data = []
            continue

        if line.startswith("id: "):
            current_id = line.removeprefix("id: ")
        elif line.startswith("event: "):
            current_event = line.removeprefix("event: ")
        elif line.startswith("data: "):
            current_data.append(line.removeprefix("data: "))

semantic_events = [(event_id, event_name, data) for event_id, event_name, data in events if event_name != "heartbeat"]
if not semantic_events:
    raise SystemExit("Durable SSE stream did not include semantic events")

semantic_sequences = []
for event_id, event_name, data in semantic_events:
    if not event_id:
        raise SystemExit(f"Durable SSE semantic event should include id; event={event_name!r}")
    if not re.fullmatch(r"[0-9a-f]{32}:[0-9]{12}", event_id):
        raise SystemExit(f"Durable SSE event id should be job-scoped and zero padded; event_id={event_id!r}")
    semantic_sequences.append(int(event_id.rsplit(":", 1)[1]))

if semantic_sequences != sorted(semantic_sequences):
    raise SystemExit(f"Durable SSE semantic event ids should be monotonically increasing; sequences={semantic_sequences!r}")

if len(semantic_sequences) != len(set(semantic_sequences)):
    raise SystemExit(f"Durable SSE semantic event ids should not repeat; sequences={semantic_sequences!r}")

heartbeat_ids = [event_id for event_id, event_name, _ in events if event_name == "heartbeat" and event_id]
if heartbeat_ids:
    raise SystemExit(f"Durable SSE heartbeat events should not advance persisted event ids; heartbeat_ids={heartbeat_ids!r}")

for event_id, event_name, data in events:
    if event_name == "snapshot":
        payload = json.loads(data)
        if payload.get("TotalSectionCount") != 1:
            raise SystemExit(f"Durable SSE snapshot should be entity-backed with TotalSectionCount=1; payload={payload!r}")
        if event_id != f"{payload.get('JobId')}:000000000001":
            raise SystemExit(f"Durable SSE first event id should be snapshot sequence 1; event_id={event_id!r}; payload={payload!r}")
        break
else:
    raise SystemExit("Durable SSE stream did not include a parseable snapshot event")

stage_names = {
    "analysis-started",
    "concepts-extracted",
    "problem-identified",
    "typical-trajectory-created",
    "patient-trajectory-created",
    "section-generation-started",
}
stage_events = [(event_name, json.loads(data)) for _, event_name, data in events if event_name in stage_names]
if not stage_events:
    raise SystemExit(f"Durable SSE stream did not include any preprocessing stage events; events={[name for _, name, _ in events]!r}")

counts = Counter(event_name for event_name, _ in stage_events)
duplicates = {name: count for name, count in counts.items() if count > 1}
if duplicates:
    raise SystemExit(f"Durable SSE stream emitted duplicate stage events: {duplicates!r}")

for event_name, payload in stage_events:
    if payload.get("Stage") != event_name:
        raise SystemExit(f"Durable SSE stage payload should match event name; event={event_name!r}; payload={payload!r}")
    if payload.get("TotalStageCount") != 6:
        raise SystemExit(f"Durable SSE stage payload should include TotalStageCount=6; payload={payload!r}")
    if not isinstance(payload.get("CompletedStageCount"), int):
        raise SystemExit(f"Durable SSE stage payload should include integer CompletedStageCount; payload={payload!r}")

for _, event_name, data in events:
    if event_name == "error":
        payload = json.loads(data)
        stage = payload.get("Stage")
        if stage is not None and not stage.endswith("-failed"):
            raise SystemExit(f"Durable SSE preprocessing error stage should be a failed kebab-case stage; payload={payload!r}")

section_step_names = {
    "section-standard-draft-created",
    "section-patient-draft-created",
    "section-instructions-applied",
}
section_step_events = [(event_name, json.loads(data)) for _, event_name, data in events if event_name in section_step_names]
section_step_order = [event_name for event_name, _ in section_step_events]
expected_section_step_order = [
    "section-standard-draft-created",
    "section-patient-draft-created",
    "section-instructions-applied",
]
if section_step_order != expected_section_step_order:
    raise SystemExit(f"Durable SSE stream should emit all section prose steps exactly once in order; actual={section_step_order!r}")

section_completed_index = next((index for index, (_, event_name, _) in enumerate(events) if event_name == "section-completed"), None)
if section_completed_index is None:
    raise SystemExit("Durable SSE stream did not include section-completed")

last_section_step_index = max(index for index, (_, event_name, _) in enumerate(events) if event_name in section_step_names)
if last_section_step_index > section_completed_index:
    raise SystemExit("Durable SSE section prose steps should be emitted before section-completed")

for expected_count, (event_name, payload) in enumerate(section_step_events, start=1):
    if payload.get("Step") != event_name:
        raise SystemExit(f"Durable SSE section prose step payload should match event name; event={event_name!r}; payload={payload!r}")
    if payload.get("SectionId") != "history":
        raise SystemExit(f"Durable SSE section prose step should identify the section; payload={payload!r}")
    if payload.get("CompletedStepCount") != expected_count:
        raise SystemExit(f"Durable SSE section prose step should increment CompletedStepCount; payload={payload!r}")
    if payload.get("TotalStepCount") != 3:
        raise SystemExit(f"Durable SSE section prose step should include TotalStepCount=3; payload={payload!r}")
PY
  resume_cursor="$(python3 - "$tmp_dir/durable_job_sse_stream.body" <<'PY'
import re
import sys

path = sys.argv[1]
events = []
current_id = None
current_event = None
current_data = []

with open(path, "r", encoding="utf-8") as handle:
    for raw_line in handle:
        line = raw_line.rstrip("\n")
        if line == "":
            if current_event is not None:
                events.append((current_id, current_event, "\n".join(current_data)))
            current_id = None
            current_event = None
            current_data = []
            continue

        if line.startswith("id: "):
            current_id = line.removeprefix("id: ")
        elif line.startswith("event: "):
            current_event = line.removeprefix("event: ")
        elif line.startswith("data: "):
            current_data.append(line.removeprefix("data: "))

semantic_ids = [
    event_id
    for event_id, event_name, _ in events
    if event_name != "heartbeat" and event_id
]

if len(semantic_ids) < 2:
    raise SystemExit(f"Durable SSE stream needs at least two semantic events to test replay; semantic_ids={semantic_ids!r}")

for event_id in semantic_ids:
    if not re.fullmatch(r"[0-9a-f]{32}:[0-9]{12}", event_id):
        raise SystemExit(f"Durable SSE event id should be job-scoped and zero padded; event_id={event_id!r}")

print(semantic_ids[0])
PY
)"

  request durable_job_sse_replay \
    --max-time 60 \
    -N \
    -H "Last-Event-ID: $resume_cursor" \
    "$events_url"
  assert_status durable_job_sse_replay 200
  assert_header_contains durable_job_sse_replay Content-Type "text/event-stream"

  python3 - "$tmp_dir/durable_job_sse_replay.body" "$resume_cursor" <<'PY'
import re
import sys

path, cursor = sys.argv[1:]
cursor_job_id, cursor_sequence_text = cursor.rsplit(":", 1)
cursor_sequence = int(cursor_sequence_text)
events = []
current_id = None
current_event = None
current_data = []

with open(path, "r", encoding="utf-8") as handle:
    for raw_line in handle:
        line = raw_line.rstrip("\n")
        if line == "":
            if current_event is not None:
                events.append((current_id, current_event, "\n".join(current_data)))
            current_id = None
            current_event = None
            current_data = []
            continue

        if line.startswith("id: "):
            current_id = line.removeprefix("id: ")
        elif line.startswith("event: "):
            current_event = line.removeprefix("event: ")
        elif line.startswith("data: "):
            current_data.append(line.removeprefix("data: "))

semantic_events = [
    (event_id, event_name, data)
    for event_id, event_name, data in events
    if event_name != "heartbeat"
]

if not semantic_events:
    raise SystemExit("Durable SSE replay did not include semantic events")

semantic_sequences = []
for event_id, event_name, _ in semantic_events:
    if not event_id:
        raise SystemExit(f"Durable SSE replay semantic event should include id; event={event_name!r}")
    if not re.fullmatch(r"[0-9a-f]{32}:[0-9]{12}", event_id):
        raise SystemExit(f"Durable SSE replay event id should be job-scoped and zero padded; event_id={event_id!r}")

    event_job_id, sequence_text = event_id.rsplit(":", 1)
    if event_job_id != cursor_job_id:
        raise SystemExit(f"Durable SSE replay event id should belong to cursor job; cursor={cursor!r}; event_id={event_id!r}")

    sequence = int(sequence_text)
    if sequence <= cursor_sequence:
        raise SystemExit(f"Durable SSE replay emitted event at or before cursor; cursor={cursor!r}; event_id={event_id!r}")

    semantic_sequences.append(sequence)

if semantic_sequences != sorted(semantic_sequences):
    raise SystemExit(f"Durable SSE replay semantic event ids should be monotonically increasing; sequences={semantic_sequences!r}")

if len(semantic_sequences) != len(set(semantic_sequences)):
    raise SystemExit(f"Durable SSE replay semantic event ids should not repeat; sequences={semantic_sequences!r}")

if semantic_sequences[0] != cursor_sequence + 1:
    raise SystemExit(f"Durable SSE replay should start immediately after cursor; cursor={cursor!r}; sequences={semantic_sequences!r}")
PY
  [[ "$sse_body" == *"event: section-completed"* || "$sse_body" == *"event: section-failed"* ]] || fail "Durable SSE stream did not include a section event; body=$sse_body"
  [[ "$sse_body" == *"event: done"* ]] || fail "Durable SSE stream did not include done; body=$sse_body"
  pass "ConsultGenerationJobs valid SSE stream emits snapshot, preprocessing stage progress, section progress, done, and resumable replay"
else
  echo "SKIP: Durable valid SSE stream. Set RUN_VALID_DURABLE_JOB_SSE=1 to enable."
fi
