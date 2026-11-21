# syntax=docker/dockerfile:1
FROM node:24-alpine AS web
WORKDIR /web
COPY src/web/package.json src/web/package-lock.json ./
RUN npm ci
COPY src/web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY global.json ./
COPY src/TutorFlow.Api/TutorFlow.Api.csproj src/TutorFlow.Api/
RUN dotnet restore src/TutorFlow.Api/TutorFlow.Api.csproj
COPY src/TutorFlow.Api/ src/TutorFlow.Api/
RUN dotnet publish src/TutorFlow.Api/TutorFlow.Api.csproj -c Release --no-restore -o /out

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Datos__Ruta=/app/data \
    Auditoria__Ruta=/app/logs/auditoria.jsonl
COPY --from=build /out ./
COPY --from=web /web/dist ./wwwroot
COPY data ./data
RUN mkdir -p /app/logs && chown -R app:app /app/logs
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "TutorFlow.Api.dll"]
