FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Restore layer: copy both csproj (the host references the Core bounded-context project).
COPY src/LupiraTasksApi.Core/LupiraTasksApi.Core.csproj src/LupiraTasksApi.Core/
COPY src/LupiraTasksApi/LupiraTasksApi.csproj src/LupiraTasksApi/
RUN dotnet restore src/LupiraTasksApi/LupiraTasksApi.csproj
COPY src/LupiraTasksApi.Core/ src/LupiraTasksApi.Core/
COPY src/LupiraTasksApi/ src/LupiraTasksApi/
RUN dotnet publish src/LupiraTasksApi/LupiraTasksApi.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# libldap2: System.DirectoryServices.Protocols (CalDAV /dav Basic auth -> Authentik LDAP bind).
# (Base image is Ubuntu 24.04 — the package is libldap2, not Debian's libldap-2.5-0.)
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl libldap2 \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out /app
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
ENTRYPOINT ["dotnet", "/app/LupiraTasksApi.dll"]
