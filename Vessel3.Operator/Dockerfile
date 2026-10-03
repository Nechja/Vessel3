ARG DOTNET_VERSION=10.0
ARG TARGET_RID=linux-x64

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG TARGET_RID

RUN apt-get update \
 && apt-get install -y --no-install-recommends clang zlib1g-dev \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /src

COPY global.json Directory.Build.props ./
COPY --parents */*.csproj ./
RUN dotnet restore Vessel3.Operator/Vessel3.Operator.csproj -r ${TARGET_RID}

COPY . .

RUN dotnet publish Vessel3.Operator/Vessel3.Operator.csproj \
    -c Release \
    -r ${TARGET_RID} \
    --no-restore \
    -o /publish

FROM mcr.microsoft.com/dotnet/runtime-deps:${DOTNET_VERSION}-noble-chiseled AS runtime

WORKDIR /app
COPY --from=build /publish/vessel3-operator /app/vessel3-operator

USER $APP_UID

ENTRYPOINT ["/app/vessel3-operator"]
