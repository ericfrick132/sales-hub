# Multi-stage build for both Api and Workers. Select via build-arg PROJECT.
ARG PROJECT=SalesHub.Api

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG PROJECT
WORKDIR /src
COPY SalesHub.sln ./
COPY src/SalesHub.Core/*.csproj src/SalesHub.Core/
COPY src/SalesHub.Infrastructure/*.csproj src/SalesHub.Infrastructure/
COPY src/SalesHub.Api/*.csproj src/SalesHub.Api/
COPY src/SalesHub.Workers/*.csproj src/SalesHub.Workers/
RUN dotnet restore SalesHub.sln
COPY . .
RUN dotnet publish "src/${PROJECT}/${PROJECT}.csproj" -c Release -o /app/out

# Bajar Chromium en el stage de build (tiene SDK para correr la CLI de Playwright).
# Lo copiamos a runtime después para no arrastrar el SDK a la imagen final.
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
ENV PATH="$PATH:/root/.dotnet/tools"
RUN dotnet tool install --global Microsoft.Playwright.CLI \
    && playwright install chromium

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
ARG PROJECT
WORKDIR /app
# ffmpeg para reconvertir audios a OGG/Opus (PTT por WhatsApp).
# El resto: deps de sistema para Chromium (Playwright).
RUN apt-get update && apt-get install -y --no-install-recommends \
        ffmpeg \
        libnss3 libnspr4 libatk1.0-0 libatk-bridge2.0-0 libcups2 \
        libdrm2 libdbus-1-3 libxkbcommon0 libxcomposite1 libxdamage1 \
        libxfixes3 libxrandr2 libgbm1 libpango-1.0-0 libcairo2 \
        libasound2 libatspi2.0-0 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/out ./
COPY --from=build /ms-playwright /ms-playwright
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENV PROJECT_DLL=${PROJECT}.dll
ENTRYPOINT ["sh", "-c", "dotnet $PROJECT_DLL"]
