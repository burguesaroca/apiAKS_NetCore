# Multi-stage Dockerfile for .NET 8 Web API
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy csproj and restore as distinct layers
COPY ["apiAKS_NetCore.csproj", "./"]
RUN dotnet restore "./apiAKS_NetCore.csproj"

# Copy everything else and publish
COPY . .
RUN dotnet publish "apiAKS_NetCore.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

# Default to listen on port 5000 inside the container; can be overridden
ENV ASPNETCORE_URLS="http://*:5000"
EXPOSE 5000

ENTRYPOINT ["dotnet", "apiAKS_NetCore.dll"]
