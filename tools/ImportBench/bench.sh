#!/bin/bash
# Builds ImportBench against the working copy and runs both halves as one named run, in parallel.
# Usage: tools/ImportBench/bench.sh <name> [--suggest]   then: python tools/ImportBench/compare.py base <name>
# The lists are lists/sng.txt ("song.sng|song.sid" a line) and lists/pitch.txt (a .sid a line).
set -e
HERE=$(cd "$(dirname "$0")" && pwd)
NAME=$1
shift
OUT=$HERE/results/bin-$NAME
mkdir -p "$HERE/results"
dotnet build "$HERE/ImportBench.csproj" -c Release -v q -o "$OUT" 2>&1 | grep -E ' error ' | head -3 || true
dotnet "$OUT/ImportBench.dll" sng "$HERE/lists/sng.txt" "$HERE/results/$NAME.sng.txt" "$@" &
dotnet "$OUT/ImportBench.dll" pitch "$HERE/lists/pitch.txt" "$HERE/results/$NAME.pitch.txt" "$@" &
wait
echo "$NAME done"
