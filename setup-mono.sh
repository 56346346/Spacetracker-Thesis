#!/usr/bin/env bash
set -e

# 1) Systempakete aktualisieren
apt-get update -qq

# 2) Mono-Entwicklungsumgebung installieren
apt-get install -y --no-install-recommends \
  mono-devel \
  msbuild \
  nuget

# 3) Prüfen der Installation
echo "Mono Version:"
mono --version
echo "MSBuild Version:"
msbuild -version
