# Render has no native .NET runtime -- it builds this Dockerfile directly, so this is what
# actually runs the app in production. Multi-stage: build with the full SDK, run with just the
# smaller ASP.NET runtime image.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy just the csproj first so `dotnet restore` is cached by Docker as long as dependencies
# haven't changed, even if application code has -- keeps rebuilds fast.
COPY TestCaseHub.Api/TestCaseHub.Api.csproj TestCaseHub.Api/
RUN dotnet restore TestCaseHub.Api/TestCaseHub.Api.csproj

COPY TestCaseHub.Api/ TestCaseHub.Api/
WORKDIR /src/TestCaseHub.Api
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# NOTE: no EXPOSE/ASPNETCORE_URLS pinned here on purpose -- Program.cs reads Render's PORT env
# var itself and binds to it dynamically, so this image works unmodified on Render (or any
# other host that sets PORT) as well as anywhere else.
ENTRYPOINT ["dotnet", "TestCaseHub.Api.dll"]
