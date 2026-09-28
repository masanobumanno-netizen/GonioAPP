FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY apps/shared-server/ apps/shared-server/
RUN dotnet publish apps/shared-server -c Release -o /out
FROM mcr.microsoft.com/dotnet/aspnet:8.0
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg ca-certificates && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out/ ./
RUN mkdir /data && chown app:app /data
USER app
ENV ASPNETCORE_URLS=http://+:8080 GONIO_SHARED_DATA=/data
EXPOSE 8080
ENTRYPOINT ["dotnet", "SharedServer.dll"]
