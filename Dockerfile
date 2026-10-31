# Build Native AOT nella stessa architettura del target (buildx: --platform linux/arm64,linux/amd64)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
RUN apt-get update && apt-get install -y --no-install-recommends clang zlib1g-dev && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY LiteSonic.csproj .
RUN dotnet restore -r linux-$(uname -m | sed 's/x86_64/x64/;s/aarch64/arm64/')
COPY . .
RUN dotnet publish -c Release -r linux-$(uname -m | sed 's/x86_64/x64/;s/aarch64/arm64/') -p:PublishAot=true -o /out \
    && rm -f /out/*.dbg

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled-extra AS final
WORKDIR /app
COPY --from=build /out .
ENV LiteSonic__MusicFolder=/music \
    LiteSonic__DataFolder=/data \
    LiteSonic__FfmpegPath=
EXPOSE 4533
VOLUME ["/data"]
ENTRYPOINT ["/app/litesonic"]
