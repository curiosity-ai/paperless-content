#!/usr/bin/env bash
# The per-line dictionary filter threshold is currently an internal constant, so a single-binary
# configuration A/B cannot change it. Keep this entry point as an explicit failure until the
# threshold becomes configurable; otherwise it produces two mislabeled equivalent runs. ~keep
set -euo pipefail

if [ "$#" -lt 3 ]; then
  echo "usage: ab_line_filter.sh <pdf> <gt.txt> <out-dir> [backend] [layout]" >&2
  exit 2
fi

echo "FATAL: the per-line dictionary filter has no runtime configuration toggle." >&2
echo "To compare safely, run each leg from a distinct recorded source revision and record each binary SHA-256." >&2
echo "Before EACH leg, clear both the OCR cache and extraction cache; never reuse cache entries between same-version binaries." >&2
exit 2
