# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/JobPilot.Api/JobPilot.Api.csproj src/JobPilot.Api/
RUN dotnet restore src/JobPilot.Api/JobPilot.Api.csproj
COPY src/JobPilot.Api/ src/JobPilot.Api/
RUN dotnet publish src/JobPilot.Api/JobPilot.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:10000
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 10000
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "JobPilot.Api.dll"]
