FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
USER app
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["ForqStudio.Api/ForqStudio.Api.csproj", "ForqStudio.Api/"]
COPY ["ForqStudio.Application/ForqStudio.Application.csproj", "ForqStudio.Application/"]
COPY ["ForqStudio.Domain/ForqStudio.Domain.csproj", "ForqStudio.Domain/"]
COPY ["ForqStudio.Infrastructure/ForqStudio.Infrastructure.csproj", "ForqStudio.Infrastructure/"]

RUN dotnet restore "ForqStudio.Api/ForqStudio.Api.csproj"

COPY . .

RUN dotnet build "ForqStudio.Api/ForqStudio.Api.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "ForqStudio.Api/ForqStudio.Api.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "ForqStudio.Api.dll"]
