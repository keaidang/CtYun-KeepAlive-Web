FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base

WORKDIR /app

COPY ./publish/ ./

ENV CTYUN_DATA_DIR=/app/data
ENV PORT=8080

EXPOSE 8080
VOLUME ["/app/data"]

ENTRYPOINT ["dotnet", "CtYun.dll"]
