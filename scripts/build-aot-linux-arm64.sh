#!/bin/sh
# Compila LiteSonic in Native AOT per linux-arm64 (es. NAS Synology) su una macchina Linux x64 con Docker.
# Usa il sysroot di cross-compilazione di Microsoft (glibc vecchia): il binario gira anche su DSM.
#
# Uso: build-aot-linux-arm64.sh [branch|tag|commit]     (default: main)
# Cartella di lavoro: $LITESONIC_BUILD_DIR (default ~/litesonic-build) con src/, cache/ e out-aot/
set -e
REPO=https://github.com/costmann/LiteSonic.git
REF=${1:-main}
WORK=${LITESONIC_BUILD_DIR:-$HOME/litesonic-build}
IMAGE=mcr.microsoft.com/dotnet-buildtools/prereqs:azurelinux-3.0-net10.0-cross-arm64

mkdir -p "$WORK/cache/dotnet" "$WORK/cache/nuget"
cd "$WORK"

# sorgenti sempre allineati a GitHub
if [ -d src/.git ]; then
    git -C src fetch --quiet origin
else
    rm -rf src
    git clone --quiet "$REPO" src
fi
git -C src checkout --quiet --detach "origin/$REF" 2>/dev/null || git -C src checkout --quiet --detach "$REF"
echo "Compilo $(git -C src log -1 --format='%h %s')"

rm -rf out-aot && mkdir out-aot
# --user: i file prodotti restano del tuo utente, non di root
docker run --rm --user "$(id -u):$(id -g)" \
  -v "$WORK/src:/src" -v "$WORK/out-aot:/out" \
  -v "$WORK/cache/dotnet:/opt/dotnet" -v "$WORK/cache/nuget:/nuget" \
  -e HOME=/tmp -e NUGET_PACKAGES=/nuget \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
  -w /src "$IMAGE" \
  bash -c "set -e
    [ -x /opt/dotnet/dotnet ] || { curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/di.sh && bash /tmp/di.sh --channel 10.0 --install-dir /opt/dotnet; }
    export PATH=/opt/dotnet:\$PATH
    dotnet publish -c Release -r linux-arm64 -p:PublishAot=true -p:SysRoot=/crossrootfs/arm64 -p:LinkerFlavor=lld -o /out
    rm -f /out/*.dbg /out/appsettings.json /out/*.staticwebassets.endpoints.json"

# controllo di compatibilità: il NAS ha glibc 2.36
MAX=$(objdump -T out-aot/litesonic out-aot/libe_sqlite3.so | grep -oE 'GLIBC_[0-9]+\.[0-9]+' | sort -t. -k2,2n -u | tail -1)
echo "glibc massima richiesta: $MAX"
ls -la out-aot
