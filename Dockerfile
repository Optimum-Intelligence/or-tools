# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY employee-scheduling-api/EmployeeScheduling.Api.csproj employee-scheduling-api/
RUN dotnet restore employee-scheduling-api/EmployeeScheduling.Api.csproj
COPY employee-scheduling-api/ employee-scheduling-api/
RUN dotnet publish employee-scheduling-api/EmployeeScheduling.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgomp1 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .
ENV PORT=8080
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["/bin/sh", "-c", "unset ASPNETCORE_URLS ASPNETCORE_HTTP_PORTS; exec dotnet EmployeeScheduling.Api.dll"]
