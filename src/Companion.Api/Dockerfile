FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /source

COPY . .
RUN dotnet restore src/Companion.Api/Companion.Api.csproj
RUN dotnet publish src/Companion.Api/Companion.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 9000
ENTRYPOINT ["dotnet", "Companion.Api.dll"]
