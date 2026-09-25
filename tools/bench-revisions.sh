#!/usr/bin/env bash
# End-to-end wall-clock benchmark of the DngSharp.Dng.Validate CLI across git
# revisions. Builds each revision in a throwaway worktree, then renders every
# DNG in a directory with `-jpeg` (parse → decode → linearize → demosaic →
# colour transform → tone-map → JPEG encode) and reports the median of N runs.
#
# Usage:
#   tools/bench-revisions.sh [-n RUNS] [-i IMAGES_DIR] [-o OUT.md] REV [REV...]
#
# Use the literal `HEAD` (or `WORKTREE`) to benchmark the current working tree,
# including uncommitted changes. Example — baseline vs. parallel vs. current:
#
#   tools/bench-revisions.sh -n 5 e5d1e16 e12501c WORKTREE
#
# Requires: bash 4+, dotnet SDK, python3 (for median). Output is a Markdown
# table on stdout (and optionally -o file). Times are wall-clock seconds of
# the whole process, so they include runtime start-up (~0.1 s) and file I/O.
set -euo pipefail

RUNS=3
IMAGES_DIR="images"
OUT=""
while getopts "n:i:o:" opt; do
  case $opt in
    n) RUNS=$OPTARG ;;
    i) IMAGES_DIR=$OPTARG ;;
    o) OUT=$OPTARG ;;
    *) echo "usage: $0 [-n RUNS] [-i IMAGES_DIR] [-o OUT.md] REV [REV...]" >&2; exit 2 ;;
  esac
done
shift $((OPTIND - 1))
[[ $# -ge 1 ]] || { echo "need at least one revision" >&2; exit 2; }

REPO=$(git rev-parse --show-toplevel)
cd "$REPO"
WORK=$(mktemp -d /tmp/dng-bench.XXXXXX)
trap 'for w in "$WORK"/wt-*; do [[ -d $w ]] && git worktree remove --force "$w" >/dev/null 2>&1 || true; done; rm -rf "$WORK"' EXIT

mapfile -t IMAGES < <(find "$IMAGES_DIR" -maxdepth 1 -iname '*.dng' | sort)
[[ ${#IMAGES[@]} -ge 1 ]] || { echo "no .dng files in $IMAGES_DIR" >&2; exit 2; }

declare -A LABEL BIN
for rev in "$@"; do
  if [[ $rev == HEAD || $rev == WORKTREE ]]; then
    src="$REPO"; LABEL[$rev]="working tree ($(git rev-parse --short HEAD)$(git diff --quiet || echo '+dirty'))"
  else
    src="$WORK/wt-$rev"
    git worktree add --detach "$src" "$rev" >/dev/null 2>&1
    LABEL[$rev]="$(git --no-pager log -1 --format='%h %s' "$rev")"
  fi
  echo "building $rev ..." >&2
  dotnet publish "$src/src/DngSharp.Dng.Validate" -c Release -o "$WORK/bin-$rev" --nologo -v q >/dev/null
  BIN[$rev]="$WORK/bin-$rev/DngSharp.Dng.Validate"
  [[ -x ${BIN[$rev]} ]] || BIN[$rev]="dotnet $WORK/bin-$rev/DngSharp.Dng.Validate.dll"
done

time_run() { # binary image -> seconds (float)
  local start end
  start=$(date +%s.%N)
  $1 -jpeg "$WORK/out.jpg" "$2" >/dev/null 2>&1
  end=$(date +%s.%N)
  echo "$end - $start" | bc -l
}
median() { python3 -c 'import statistics,sys; print(f"{statistics.median(map(float,sys.argv[1:])):.2f}")' "$@"; }

{
  echo "# End-to-end render benchmark (\`-jpeg\`)"
  echo
  echo "- Host: $(nproc) logical cores, $(lscpu | sed -n 's/^Model name: *//p' | head -1)"
  echo "- .NET: $(dotnet --version); median of $RUNS runs per cell, wall-clock seconds (includes process start-up)"
  echo "- Images: ${#IMAGES[@]} × \`$IMAGES_DIR/*.dng\`"
  echo
  hdr="| Revision |"; sep="|---|"
  for img in "${IMAGES[@]}"; do hdr+=" $(basename "$img") |"; sep+="---:|"; done
  hdr+=" total |"; sep+="---:|"
  echo "$hdr"; echo "$sep"
  # Interleave revisions within each run so laptop thermal throttling and
  # background noise hit every revision equally instead of penalising
  # whichever revision happens to run last.
  declare -A SAMPLES
  for rev in "$@"; do time_run "${BIN[$rev]}" "${IMAGES[0]}" >/dev/null; done # warm page cache
  for img in "${IMAGES[@]}"; do
    for ((r = 0; r < RUNS; r++)); do
      for rev in "$@"; do
        SAMPLES["$rev|$img"]+="$(time_run "${BIN[$rev]}" "$img") "
      done
    done
  done
  first_total=""
  for rev in "$@"; do
    row="| ${LABEL[$rev]} |"; total=0
    for img in "${IMAGES[@]}"; do
      # shellcheck disable=SC2086
      m=$(median ${SAMPLES["$rev|$img"]})
      row+=" ${m} s |"; total=$(echo "$total + $m" | bc -l)
      echo "  $rev $(basename "$img"): ${SAMPLES["$rev|$img"]}→ $m" >&2
    done
    total=$(printf '%.2f' "$total")
    if [[ -z $first_total ]]; then first_total=$total; row+=" **$total s** |"
    else row+=" **$total s** ($(echo "scale=2; $first_total / $total" | bc -l)×) |"; fi
    echo "$row"
  done
} | tee "${OUT:-/dev/null}"
