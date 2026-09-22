# syntax=docker/dockerfile:1

# ---------------------------------------------------------------------------
# Estágio 1 — Build do AdminUI (Vite/React) emitido em LMMentor.Backend/wwwroot
# ---------------------------------------------------------------------------
FROM node:22-alpine AS ui
WORKDIR /src/LMMentor.AdminUI
COPY src/LMMentor.AdminUI/package*.json ./
RUN npm ci
COPY src/LMMentor.AdminUI/ ./
COPY assets/ ../../assets/
RUN npm run build

# ---------------------------------------------------------------------------
# Estágio 2 — Publicação do backend (.NET 10, Native AOT em Release)
# ---------------------------------------------------------------------------
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
RUN apt-get update \
    && apt-get install -y --no-install-recommends clang zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /src/LMMentor.Backend
COPY src/LMMentor.Backend/ ./
# wwwroot já contém o build da UI do estágio anterior.
COPY --from=ui /src/LMMentor.Backend/wwwroot ./wwwroot
ENV DOTNET_EnableDiagnostics=0
RUN dotnet publish -c Release -r linux-x64

# ---------------------------------------------------------------------------
# Estágio 3 — Imagem final (binário AOT, sem runtime .NET)
# ---------------------------------------------------------------------------
FROM ubuntu:24.04 AS final
ENV ASPNETCORE_URLS=http://+:8080 \
    LMMENTOR_DB_PATH=/data/lmmentor.db
RUN groupadd --system lmmentor && useradd --system --gid lmmentor --home-dir /data lmmentor \
    && mkdir -p /data && chown lmmentor:lmmentor /data
COPY --from=backend --chown=lmmentor:lmmentor /src/LMMentor.Backend/bin/Release/net10.0/linux-x64/publish/ /app/
USER lmmentor
WORKDIR /data
EXPOSE 8080
VOLUME ["/data"]
ENTRYPOINT ["/app/LMMentor.Backend"]
