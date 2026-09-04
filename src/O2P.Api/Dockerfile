FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

COPY src/O2P.slnx src/
COPY src/O2P.Api/O2P.Api.csproj src/O2P.Api/
COPY src/O2P.Application/O2P.Application.csproj src/O2P.Application/
COPY src/O2P.Domain/O2P.Domain.csproj src/O2P.Domain/
COPY src/O2P.Infrastructure.Metadata/O2P.Infrastructure.Metadata.csproj src/O2P.Infrastructure.Metadata/
COPY src/O2P.Infrastructure.Oracle/O2P.Infrastructure.Oracle.csproj src/O2P.Infrastructure.Oracle/
COPY src/O2P.Infrastructure.Postgres/O2P.Infrastructure.Postgres.csproj src/O2P.Infrastructure.Postgres/
COPY src/O2P.Worker/O2P.Worker.csproj src/O2P.Worker/

RUN dotnet restore src/O2P.Api/O2P.Api.csproj

COPY src/ src/
RUN dotnet publish src/O2P.Api/O2P.Api.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /out .
EXPOSE 80
ENTRYPOINT ["dotnet", "O2P.Api.dll"]
