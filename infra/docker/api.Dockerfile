FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY global.json ./
COPY apps/api/ ./apps/api/
RUN dotnet restore apps/api/QuantDesk.sln
RUN dotnet publish apps/api/src/QuantDesk.Api/QuantDesk.Api.csproj -c Release -o /publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /publish ./
USER $APP_UID
ENTRYPOINT ["dotnet", "QuantDesk.Api.dll"]