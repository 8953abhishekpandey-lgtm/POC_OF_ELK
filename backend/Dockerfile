# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["ELKMonitor.API/ELKMonitor.API.csproj", "ELKMonitor.API/"]
RUN dotnet restore "ELKMonitor.API/ELKMonitor.API.csproj"
COPY . .
WORKDIR "/src/ELKMonitor.API"
RUN dotnet build "ELKMonitor.API.csproj" -c Release -o /app/build

# Stage 2: Publish
FROM build AS publish
RUN dotnet publish "ELKMonitor.API.csproj" -c Release -o /app/publish --no-restore

# Stage 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Docker
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "ELKMonitor.API.dll"]
