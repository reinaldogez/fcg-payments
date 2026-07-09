# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# nao instala git hooks no build: o manifesto de tools e o repositorio git
# nao fazem parte do contexto do container
ENV HUSKY=0

COPY ["fcg-payments.slnx", "./"]
COPY ["nuget.config", "./"]
COPY ["src/Fcg.Payments.Domain/Fcg.Payments.Domain.csproj",                 "src/Fcg.Payments.Domain/"]
COPY ["src/Fcg.Payments.Application/Fcg.Payments.Application.csproj",        "src/Fcg.Payments.Application/"]
COPY ["src/Fcg.Payments.Infrastructure/Fcg.Payments.Infrastructure.csproj", "src/Fcg.Payments.Infrastructure/"]
COPY ["src/Fcg.Payments.Api/Fcg.Payments.Api.csproj",                       "src/Fcg.Payments.Api/"]

# Token do feed NuGet via secret mount do BuildKit (o feed exige token mesmo para
# pacote publico). Nunca ARG/ENV: vazaria numa layer da imagem. O metodo explicito
# (dotnet nuget update source) e necessario porque a env var
# NuGetPackageSourceCredentials_* nao casa o source com hifen -> cairia para anonimo/401.
RUN --mount=type=secret,id=gh_token \
    dotnet nuget update source github-fcg \
      --username x --password "$(cat /run/secrets/gh_token)" --store-password-in-clear-text \
      --configfile nuget.config \
 && dotnet restore "src/Fcg.Payments.Api/Fcg.Payments.Api.csproj"

COPY src/ src/
RUN dotnet publish "src/Fcg.Payments.Api/Fcg.Payments.Api.csproj" \
    -c Release -o /app/publish --no-restore

# ---- final ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Fcg.Payments.Api.dll"]
