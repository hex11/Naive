# https://hub.docker.com/_/microsoft-dotnet-core
FROM mcr.microsoft.com/dotnet/core/sdk:3.1 AS build
WORKDIR /source

# copy everything and build app
COPY . .
WORKDIR /source/NaiveSocksDotNetCore
RUN dotnet restore -r linux-musl-x64 && \
    dotnet publish -c release -o /app -r linux-musl-x64 --self-contained false --no-restore


# final stage/image
FROM mcr.microsoft.com/dotnet/core/aspnet:3.1-alpine

# for colurful terminal output
RUN apk add --no-cache ncurses-libs

WORKDIR /app
COPY --from=build /app ./

ENTRYPOINT ["./NaiveSocksDotNetCore"]
