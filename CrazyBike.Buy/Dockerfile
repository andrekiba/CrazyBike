FROM mcr.microsoft.com/dotnet/aspnet:6.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src
COPY ["CrazyBike.Buy/CrazyBike.Buy.csproj", "CrazyBike.Buy/"]
RUN dotnet restore "CrazyBike.Buy/CrazyBike.Buy.csproj"
COPY . .
WORKDIR "/src/CrazyBike.Buy"
RUN dotnet build "CrazyBike.Buy.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "CrazyBike.Buy.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "CrazyBike.Buy.dll"]
