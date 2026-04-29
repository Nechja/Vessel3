# syntax=docker/dockerfile:1.7
# Build native AOT binary, ship on a distroless runtime-deps base.

ARG DOTNET_VERSION=10.0
ARG TARGET_RID=linux-x64

# ---- build ---------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG TARGET_RID

# Native AOT toolchain (clang/zlib already in the SDK image; this is belt-and-braces)
RUN apt-get update \
 && apt-get install -y --no-install-recommends clang zlib1g-dev \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /src

COPY global.json ./
COPY Directory.Build.props ./
COPY src/Vessel3.Server/Vessel3.Server.csproj src/Vessel3.Server/
RUN dotnet restore src/Vessel3.Server/Vessel3.Server.csproj -r ${TARGET_RID}

COPY .editorconfig ./
COPY src/ src/

RUN dotnet publish src/Vessel3.Server/Vessel3.Server.csproj \
    -c Release \
    -r ${TARGET_RID} \
    --no-restore \
    -o /publish

# ---- runtime -------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/runtime-deps:${DOTNET_VERSION}-noble-chiseled AS runtime

WORKDIR /app
COPY --from=build /publish/vessel3 /app/vessel3

USER $APP_UID
ENV ASPNETCORE_URLS=http://+:9000
EXPOSE 9000

ENTRYPOINT ["/app/vessel3"]
