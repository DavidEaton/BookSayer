FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/ src/
RUN dotnet publish src/BookSayer -c Release -o /app/cli && dotnet publish src/BookSayer.Web -c Release -o /app/web

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg && rm -rf /var/lib/apt/lists/*
COPY --from=build /app /app
WORKDIR /app/web
ENV CliPath=/app/cli/BookSayer.dll
USER $APP_UID
ENTRYPOINT ["dotnet", "BookSayer.Web.dll"]
