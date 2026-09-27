#!/bin/bash
# Web sessions build with the SDK that CI builds with.
#
# global.json asks for 10.0.100 with rollForward latestFeature. That is a
# floor, not a pin: CI's setup-dotnet installs the newest 10.0 SDK, and the
# build resolves to it. The web image ships 10.0.100, whose analyzers miss
# findings a newer SDK fails the build on, so a change validated on the image's
# SDK could still be refused by CI. This installs the newest 10.0 SDK beside the
# image's, in the same root, so the dotnet on PATH resolves the way CI's does.
# It stays on .NET 10 because global.json does.
#
# Then it restores the solution in locked mode, as CI does. The packages are in
# the container before the first build, and lock-file drift shows here rather
# than in CI.
set -euo pipefail

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

installer=$(mktemp)
trap 'rm -f "$installer"' EXIT

# Progress goes to stderr; stdout is added to the session's context, which
# needs one line saying what the session builds with, not the installer's log.
curl -fsSL --retry 4 --retry-delay 2 --retry-connrefused \
  https://builds.dotnet.microsoft.com/dotnet/scripts/v1/dotnet-install.sh -o "$installer"
bash "$installer" --channel 10.0 --install-dir /usr/share/dotnet --skip-non-versioned-files >&2

cd "${CLAUDE_PROJECT_DIR:-.}"
dotnet restore FallbackPlan.slnx --locked-mode --verbosity quiet >&2

echo "Session setup: .NET SDK $(dotnet --version), the newest 10.0 as CI uses; FallbackPlan.slnx restored in locked mode."
