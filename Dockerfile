FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT=RevenueCycle.Api
WORKDIR /source
COPY global.json Directory.Build.props ./
COPY src/ ./src/
RUN dotnet publish src/${PROJECT}/${PROJECT}.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
ARG PROJECT=RevenueCycle.Api
ENV APP_DLL=${PROJECT}.dll
ENV ASPNETCORE_HTTP_PORTS=8080
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir -p /app/data && chown $APP_UID /app/data
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["sh", "-c", "exec dotnet \"$APP_DLL\""]
