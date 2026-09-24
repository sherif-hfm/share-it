FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY global.json Directory.Build.props Directory.Packages.props NuGet.Config ./
COPY src/ShareIt.Core/ShareIt.Core.csproj src/ShareIt.Core/
COPY src/ShareIt.Infrastructure/ShareIt.Infrastructure.csproj src/ShareIt.Infrastructure/
COPY src/ShareIt.Web/ShareIt.Web.csproj src/ShareIt.Web/
RUN dotnet restore src/ShareIt.Web/ShareIt.Web.csproj
COPY src/ src/
RUN dotnet publish src/ShareIt.Web/ShareIt.Web.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
RUN mkdir -p /data /keys && chown -R app:app /data /keys
COPY --from=build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=8080 ShareIt__DataPath=/data ShareIt__PinPepperFile=/keys/pin-pepper
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "ShareIt.Web.dll"]
