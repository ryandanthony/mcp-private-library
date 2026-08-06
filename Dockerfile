# syntax=docker/dockerfile:1
# Multi-stage build for the MCP Private Library ASP.NET Core app.
#
# The app clones repositories at runtime by shelling out to the `git` binary,
# so git must be present in the FINAL runtime image (not just the build stage).

# ---- Build stage ----------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Version supplied by CI (GitVersion). Defaults keep local `docker build` working.
ARG VERSION=0.1.0
ARG INFORMATIONAL_VERSION=0.1.0

# Restore first (better layer caching): copy only project/solution metadata.
COPY McpPrivateLibrary.slnx ./
COPY src/McpPrivateLibrary/McpPrivateLibrary.csproj src/McpPrivateLibrary/
RUN dotnet restore src/McpPrivateLibrary/McpPrivateLibrary.csproj

# Copy the rest and publish.
COPY . .
RUN dotnet publish src/McpPrivateLibrary/McpPrivateLibrary.csproj \
        -c Release \
        -o /app/publish \
        --no-restore \
        -p:Version=${VERSION} \
        -p:InformationalVersion=${INFORMATIONAL_VERSION} \
        -p:UseAppHost=false

# ---- Runtime stage --------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# git is required at runtime for cloning submitted repositories; ca-certificates
# for HTTPS clones. Clean apt lists to keep the image small.
RUN apt-get update \
    && apt-get install -y --no-install-recommends git ca-certificates \
    && rm -rf /var/lib/apt/lists/*

# Record the build version as an image label and env var for traceability.
ARG VERSION=0.1.0
ENV APP_VERSION=${VERSION}
LABEL org.opencontainers.image.title="MCP Private Library" \
      org.opencontainers.image.description="MCP server that indexes GitHub Markdown for semantic search" \
      org.opencontainers.image.source="https://github.com/ryandanthony/mcp-private-library" \
      org.opencontainers.image.version="${VERSION}"

COPY --from=build /app/publish ./

# Run as the non-root user provided by the base image.
USER $APP_UID

# Listen on 8080 inside the container.
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "McpPrivateLibrary.dll"]
