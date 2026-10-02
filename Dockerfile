# One image that serves both the API and the built frontend, so the whole app lives at a single URL.

FROM node:22-alpine AS web
WORKDIR /web
COPY frontend/package*.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
WORKDIR /src
COPY backend/JobAgent.Api/JobAgent.Api.csproj backend/JobAgent.Api/
RUN dotnet restore backend/JobAgent.Api/JobAgent.Api.csproj
COPY backend/ backend/
RUN dotnet publish backend/JobAgent.Api/JobAgent.Api.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=api /out ./
COPY --from=web /web/dist ./wwwroot
# Auto-apply opens a browser window on the server's machine, which makes no sense when hosted; friends use the extension.
ENV Automation__Enabled=false
# Uploaded resumes and tailored resumes live here: mount a persistent disk/volume at /app/uploads.
VOLUME /app/uploads
EXPOSE 8080
ENV PORT=8080
ENTRYPOINT ["dotnet", "JobAgent.Api.dll"]
