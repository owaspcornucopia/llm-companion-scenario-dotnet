# Use the SDK image to compile; building in production is how other teams create folklore.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /source

# Copy, restore, and publish a release build; the runtime image gets artifacts, not my entire toolbox.
COPY . .
RUN dotnet restore src/Companion.Api/Companion.Api.csproj
RUN dotnet publish src/Companion.Api/Companion.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

# Use the smaller ASP.NET image to run the published application; excess layers are not a personality.
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
# Document the HTTP port; Docker should not need to guess what I already know.
EXPOSE 9000
# Start the API through .NET with the published assembly; one command, no interpretive dance.
ENTRYPOINT ["dotnet", "Companion.Api.dll"]
