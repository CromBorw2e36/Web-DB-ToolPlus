# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ToolPlus/ToolPlus.csproj ToolPlus/
RUN dotnet restore ToolPlus/ToolPlus.csproj

COPY ToolPlus/ ToolPlus/
RUN dotnet publish ToolPlus/ToolPlus.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Internal tool behind whatever terminates TLS in front of it - plain HTTP inside the container,
# same as the LAN launch profile. Host-side port is remapped in docker-compose.yml.
ENV ASPNETCORE_URLS=http://+:8300
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8300

COPY --from=build /app/publish .

# App_Data holds toolplus.db (ToolDbContext) - the tool's own SQLite store, mounted as a
# volume in docker-compose.yml so it survives image rebuilds. Program.cs creates the folder
# itself on startup if the volume is empty.
VOLUME ["/app/App_Data"]

ENTRYPOINT ["dotnet", "ToolPlus.dll"]
