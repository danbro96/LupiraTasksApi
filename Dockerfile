FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/LupiraTasksApi/LupiraTasksApi.csproj src/LupiraTasksApi/
RUN dotnet restore src/LupiraTasksApi/LupiraTasksApi.csproj
COPY src/LupiraTasksApi/ src/LupiraTasksApi/
RUN dotnet publish src/LupiraTasksApi/LupiraTasksApi.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out /app
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
ENTRYPOINT ["dotnet", "/app/LupiraTasksApi.dll"]
