#!/usr/bin/env bash
set -euo pipefail
if [ "$#" -ne 3 ]; then
  echo "Uso: bash publicar-ios.sh IDENTIFICADOR CERTIFICADO PERFIL" >&2
  exit 1
fi
cd "$(dirname "$0")"
dotnet publish Tunapp.csproj -f net10.0-ios -c Release \
  -p:RuntimeIdentifier=ios-arm64 \
  -p:ArchiveOnBuild=true \
  -p:BuildIpa=true \
  "-p:ApplicationId=$1" \
  "-p:CodesignKey=$2" \
  "-p:CodesignProvision=$3"
