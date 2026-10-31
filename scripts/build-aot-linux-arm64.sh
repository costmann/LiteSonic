#!/bin/sh
# Compila LiteSonic in Native AOT per linux-arm64 con il sysroot di Microsoft (glibc vecchia, compatibile col NAS)
set -e
cd "$(dirname "$0")"
rm -rf out-aot
docker run --rm \
  -v "$PWD/src:/src" -v "$PWD/out-aot:/out" \
  -v "$PWD/cache/dotnet:/opt/dotnet" -v "$PWD/cache/nuget:/root/.nuget" \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
  -w /src mcr.microsoft.com/dotnet-buildtools/prereqs:azurelinux-3.0-net10.0-cross-arm64 \
  bash -c "set -e
    [ -x /opt/dotnet/dotnet ] || { curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/di.sh && bash /tmp/di.sh --channel 10.0 --install-dir /opt/dotnet; }
    export PATH=/opt/dotnet:\$PATH
    dotnet publish -c Release -r linux-arm64 -p:PublishAot=true -p:SysRoot=/crossrootfs/arm64 -p:LinkerFlavor=lld -o /out
    rm -f /out/*.dbg /out/appsettings.json"
ls -la out-aot
