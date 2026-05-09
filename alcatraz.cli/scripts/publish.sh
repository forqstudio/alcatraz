#!/usr/bin/env bash
# Publish a self-contained single-file `alcatraz` binary to alcatraz.cli/dist/.
# Output runs without a separate .NET install.
set -euo pipefail

cd "$(dirname "$0")/.."

RID="${RID:-$(dotnet --info | awk -F': *' '/RID:/ {print $2; exit}')}"
OUT="${OUT:-dist}"

# Unlink any previous binary first. If a running `alcatraz ssh` session has it open,
# overwriting in place fails with "Text file busy" — but unlink on Linux just detaches
# the inode, leaving the running process untouched and the path free for the new file.
rm -f "$OUT/alcatraz"

dotnet publish src/Alcatraz.Cli/Alcatraz.Cli.csproj \
  -c Release \
  -r "$RID" \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:DebugType=None \
  -p:DebugSymbols=false \
  -o "$OUT"

echo
echo "Built: $(pwd)/$OUT/alcatraz"
echo "Add it to PATH, e.g.:"
echo "    sudo ln -sf $(pwd)/$OUT/alcatraz /usr/local/bin/alcatraz"
