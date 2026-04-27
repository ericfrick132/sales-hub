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

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
ARG PROJECT
WORKDIR /app
COPY --from=build /app/out ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENV PROJECT_DLL=${PROJECT}.dll
ENTRYPOINT ["sh", "-c", "dotnet $PROJECT_DLL"]
