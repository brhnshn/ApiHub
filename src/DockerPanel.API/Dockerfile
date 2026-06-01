FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src
COPY ["DockerPanel.API/DockerPanel.API.csproj", "DockerPanel.API/"]
RUN dotnet restore "DockerPanel.API/DockerPanel.API.csproj"
COPY . .
WORKDIR "/src/DockerPanel.API"
RUN dotnet build "DockerPanel.API.csproj" -c Release -o /app/build
RUN dotnet publish "DockerPanel.API.csproj" -c Release -o /app/publish /p:PublishTrimmed=true

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "DockerPanel.API.dll"]
