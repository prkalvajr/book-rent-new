# Build a partir da raiz do repositorio:
#   docker build -f src/BookRent.Api/Dockerfile -t bookrent-api .
# O contexto precisa ser a raiz para que Directory.*.props e as demais camadas sejam copiados.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /source

# Copia primeiro apenas os arquivos de projeto: a camada de restore so
# e invalidada quando alguma dependencia muda, nao a cada alteracao de codigo.
# O .editorconfig entra aqui porque carrega as supressoes de analisador da pasta de
# migrations (codigo gerado). Sem ele, TreatWarningsAsErrors quebra o build da imagem
# mesmo com o build local verde.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/BookRent.Domain/BookRent.Domain.csproj src/BookRent.Domain/
COPY src/BookRent.Application/BookRent.Application.csproj src/BookRent.Application/
COPY src/BookRent.Infrastructure/BookRent.Infrastructure.csproj src/BookRent.Infrastructure/
COPY src/BookRent.Api/BookRent.Api.csproj src/BookRent.Api/
RUN dotnet restore src/BookRent.Api/BookRent.Api.csproj

COPY src/ src/
RUN dotnet publish src/BookRent.Api/BookRent.Api.csproj \
    -c $BUILD_CONFIGURATION \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
WORKDIR /app

# wget e usado pelo healthcheck do docker compose.
RUN apk add --no-cache wget

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=1 \
    DOTNET_TieredPGO=1

COPY --from=build /app/publish .

# Usuario nao-root fornecido pela imagem base (uid 1654).
USER $APP_UID
EXPOSE 8080

ENTRYPOINT ["dotnet", "BookRent.Api.dll"]
