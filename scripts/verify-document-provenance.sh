#!/usr/bin/env bash
#
# Production verification for document intake and its provenance (#235, #238,
# #256).
#
#   ./scripts/verify-document-provenance.sh [fixture-directory]
#   TOKEN=... ./scripts/verify-document-provenance.sh ~/fixtures
#   ./scripts/verify-document-provenance.sh --print-payload
#
# The point of the run is the first check: the same referral sent as a file
# and as text must produce the same effective-input hash. If those differ,
# extraction stopped being a pre-step and the record no longer means what it
# says.
#
# COSTS THREE REAL CONSULT RUNS. That is unavoidable — the hash is computed at
# job start, so no preview endpoint can answer it. The script does not wait for
# any of them to finish. Use --print-payload first if you want to see exactly
# what would be sent.
#
# Fixtures come from the directory given as the first argument, defaulting to
# the working directory. They are generated, not committed:
#
#   dotnet run --file scripts/make-pdf-fixtures.cs -- <dir>
#   dotnet run --file scripts/make-input-fixtures.cs -- <dir>
#
# The referral text comes from scripts/fixtures/consult_draft.txt, beside this
# script, because it is the same source the PDF fixture is built from — the two
# must agree for check 1 to mean anything.
#
# Request bodies are built here from those files rather than read from
# payload-*.json on disk. A stored payload is a copy of a fixture that can
# silently go stale against it, and a run that posts the wrong bytes still
# passes. Derived at send time, that cannot happen (#256).

set -uo pipefail

API="https://canada-east-ai-function-gmenbbe9erewh4bj.canadaeast-01.azurewebsites.net/api/ConsultGenerationJobs"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

PRINT_ONLY=0
FIXTURES="$PWD"

for arg in "$@"; do
    case "$arg" in
        --print-payload) PRINT_ONLY=1 ;;
        -*) echo "unknown option: $arg" >&2; exit 1 ;;
        *)  FIXTURES="$(cd "$arg" 2>/dev/null && pwd)" || { echo "no such directory: $arg" >&2; exit 1; } ;;
    esac
done

REFERRAL="$HERE/fixtures/consult_draft.txt"
TEXT_PDF="$FIXTURES/referral-text.pdf"
SCAN_PDF="$FIXTURES/referral-scan.pdf"

for f in "$REFERRAL" "$TEXT_PDF" "$SCAN_PDF"; do
    [ -f "$f" ] || { echo "missing fixture: $f" >&2; echo "run make-pdf-fixtures.cs first" >&2; exit 1; }
done

# The four request bodies, each derived from a fixture on disk. Kept as
# functions rather than variables so nothing large sits in the environment.
payload() {
    case "$1" in
        file-txt)  python3 -c '
import base64, json, sys
data = open(sys.argv[1], "rb").read()
print(json.dumps({"inputFiles": {"consult_draft": {"contentType": "text/plain", "content": base64.b64encode(data).decode()}}}))
' "$REFERRAL" ;;
        text)      python3 -c '
import json, sys
print(json.dumps({"inputs": {"consult_draft": open(sys.argv[1], encoding="utf-8").read()}}))
' "$REFERRAL" ;;
        file-pdf)  python3 -c '
import base64, json, sys
data = open(sys.argv[1], "rb").read()
print(json.dumps({"inputFiles": {"consult_draft": {"contentType": "application/pdf", "content": base64.b64encode(data).decode()}}}))
' "$TEXT_PDF" ;;
        file-scan) python3 -c '
import base64, json, sys
data = open(sys.argv[1], "rb").read()
print(json.dumps({"inputFiles": {"consult_draft": {"contentType": "application/pdf", "content": base64.b64encode(data).decode()}}}))
' "$SCAN_PDF" ;;
    esac
}

if [ "$PRINT_ONLY" -eq 1 ]; then
    echo
    echo "Fixtures:  $FIXTURES"
    echo "Referral:  $REFERRAL"
    echo
    for p in file-txt text file-pdf file-scan; do
        # Base64 of a PDF is thousands of characters of noise; the shape and
        # the size are what anyone is actually checking.
        printf '\033[1m%s\033[0m\n' "$p"
        payload "$p" | python3 -c '
import json, sys
d = json.load(sys.stdin)
for slot, value in (d.get("inputs") or {}).items():
    print(f"  inputs[{slot}]: {len(value)} characters")
    print("    " + value.splitlines()[0][:70] + " ...")
for slot, f in (d.get("inputFiles") or {}).items():
    kind = f["contentType"]
    size = len(f["content"])
    print(f"  inputFiles[{slot}]: {kind}, {size} base64 characters")
'
        echo
    done
    echo "Nothing was sent."
    exit 0
fi

TOKEN="${TOKEN:-}"
if [ -z "$TOKEN" ]; then
    read -rsp "Bearer token (not echoed): " TOKEN
    echo
fi
[ -n "$TOKEN" ] || { echo "no token supplied" >&2; exit 1; }

pass=0
fail=0
ok()   { printf '  \033[32mPASS\033[0m  %s\n' "$1"; pass=$((pass + 1)); }
bad()  { printf '  \033[31mFAIL\033[0m  %s\n' "$1"; fail=$((fail + 1)); }
note() { printf '        %s\n' "$1"; }

# Sets LAST_STATUS / LAST_BODY. Called directly, never in $(...), so the
# counters above survive and diagnostics reach the terminal.
LAST_STATUS=""
LAST_BODY=""
post() {
    local out
    out="$(payload "$1" | curl -sS -X POST "$API" \
        -H "Authorization: Bearer $TOKEN" \
        -H 'Content-Type: application/json' \
        --data-binary @- \
        -w $'\n%{http_code}' 2>/dev/null)"
    # curl appends "\n<status>" last, so the status is whatever follows the
    # final newline and the body is everything before it.
    LAST_STATUS="${out##*$'\n'}"
    LAST_BODY="${out%$'\n'*}"
}

# Sets JOB_ID, empty when the start did not succeed.
JOB_ID=""
start() {
    local label="$1"
    post "$2"
    JOB_ID=""

    if [ "$LAST_STATUS" != "202" ] && [ "$LAST_STATUS" != "200" ]; then
        bad "$label — expected a started job, got HTTP $LAST_STATUS"
        note "$LAST_BODY"
        return
    fi

    JOB_ID="$(printf '%s' "$LAST_BODY" | python3 -c 'import json,sys; print(json.load(sys.stdin)["JobId"])' 2>/dev/null)"
    [ -n "$JOB_ID" ] || bad "$label — started, but no JobId in the response"
}

# Fetches a job into JOB_JSON, waiting for it to materialise.
#
# A start returns 202 before the entity has processed its Initialize signal,
# so an immediate read can 404 or come back without provenance. Waiting on
# EffectiveInputHash is what separates "not ready yet" from "recorded
# nothing" — without it, an unreadable job and a job with no origin look
# identical, and an absence check would pass for the wrong reason.
JOB_JSON=""
fetch_job() {
    local job="$1" tries="${2:-20}" i
    for (( i = 0; i < tries; i++ )); do
        JOB_JSON="$(curl -sS "$API/$job" -H "Authorization: Bearer $TOKEN" 2>/dev/null)"
        if printf '%s' "$JOB_JSON" \
            | python3 -c 'import json,sys; sys.exit(0 if json.load(sys.stdin).get("EffectiveInputHash") else 1)' 2>/dev/null
        then
            return 0
        fi
        sleep 1
    done
    JOB_JSON=""
    return 1
}

# Reads one field out of the JOB_JSON last fetched.
field() {
    printf '%s' "$JOB_JSON" | python3 -c "
import json, sys
try:
    d = json.load(sys.stdin)
except Exception:
    print(''); raise SystemExit
v = d.get('$1')
print('' if v is None else json.dumps(v) if isinstance(v, (dict, list)) else v)
"
}

echo
echo "1. the same referral, sent both ways"

start 'file submission' file-txt; job_file="$JOB_ID"
start 'text submission' text;     job_text="$JOB_ID"

if [ -n "$job_file" ] && [ -n "$job_text" ]; then
    if fetch_job "$job_file"; then
        hash_file="$(field EffectiveInputHash)"
        origin_file="$(field InputOrigins)"
    else
        bad "could not read job $job_file — no provenance to check"
        hash_file=""; origin_file=""
    fi

    if fetch_job "$job_text"; then
        hash_text="$(field EffectiveInputHash)"
        origin_text="$(field InputOrigins)"
        text_readable=yes
    else
        bad "could not read job $job_text — no provenance to check"
        hash_text=""; origin_text=""; text_readable=no
    fi

    note "file: ${hash_file:-<none>}"
    note "text: ${hash_text:-<none>}"

    if [ -n "$hash_file" ] && [ "$hash_file" = "$hash_text" ]; then
        ok "identical effective-input hash — extraction stayed a pre-step"
    elif [ -n "$hash_file" ] && [ -n "$hash_text" ]; then
        bad "hashes differ; a file and its text are not the same input"
    fi

    case "$origin_file" in
        *text/1*) ok "the file-backed job names the extractor that read it" ;;
        '')       ;;
        *)        bad "expected an origin naming text/1, got: $origin_file" ;;
    esac

    # Only meaningful once the job is known to be readable: otherwise an
    # empty answer means "could not read it", not "recorded nothing".
    if [ "$text_readable" = yes ]; then
        if [ -z "$origin_text" ]; then
            ok "the typed job claims nothing — absence is not an assertion"
        else
            bad "a typed job recorded an origin: $origin_text"
        fi
    fi
fi

echo
echo "2. a text-layer PDF"

start 'pdf submission' file-pdf; job_pdf="$JOB_ID"
if [ -n "$job_pdf" ]; then
    if fetch_job "$job_pdf"; then
        origin_pdf="$(field InputOrigins)"
    else
        bad "could not read job $job_pdf"
        origin_pdf=""
    fi
    note "${origin_pdf:-<none>}"
    case "$origin_pdf" in
        *pdfpig/*) ok "the extractor and its version are on the record" ;;
        '')        ;;
        *)         bad "expected an origin naming pdfpig, got: $origin_pdf" ;;
    esac
fi

echo
echo "3. a scanned PDF is refused, and no job is created"

post file-scan
note "HTTP $LAST_STATUS"
note "$LAST_BODY"

if [ "$LAST_STATUS" = "422" ]; then
    ok "refused as unprocessable — not accepted, and not a 500"
else
    bad "expected 422, got $LAST_STATUS"
fi

case "$LAST_BODY" in
    *"no text layer"*) ok "the reply names the cause" ;;
    *)                 bad "the reply does not name the cause" ;;
esac

echo
echo "----"
printf 'passed %d, failed %d\n' "$pass" "$fail"
echo
echo "Still to check by eye, in the app:"
if [ -n "${job_pdf:-}" ]; then
    echo "  · /history/$job_pdf — the provenance panel should name the"
    echo "    extractor and page count under the input hash."
fi
echo "  · A consult from before today still renders its provenance panel."
echo "    The null path is the one that fails silently."
echo

[ "$fail" -eq 0 ]
