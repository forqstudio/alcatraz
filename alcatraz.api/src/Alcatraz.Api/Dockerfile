FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
# openssh-client provides the ssh-keygen binary the SSH CA shells out to.
USER root
RUN apt-get update \
 && apt-get install -y --no-install-recommends openssh-client \
 && rm -rf /var/lib/apt/lists/*
USER app
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["Alcatraz.Api/Alcatraz.Api.csproj", "Alcatraz.Api/"]
COPY ["Alcatraz.Application/Alcatraz.Application.csproj", "Alcatraz.Application/"]
COPY ["Alcatraz.Domain/Alcatraz.Domain.csproj", "Alcatraz.Domain/"]
COPY ["Alcatraz.Infrastructure/Alcatraz.Infrastructure.csproj", "Alcatraz.Infrastructure/"]

RUN dotnet restore "Alcatraz.Api/Alcatraz.Api.csproj"

COPY . .

RUN dotnet build "Alcatraz.Api/Alcatraz.Api.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "Alcatraz.Api/Alcatraz.Api.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Alcatraz.Api.dll"]
