FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY ReSharder.Operator.csproj .
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

USER app

COPY --from=build /app .

EXPOSE 8080

ENTRYPOINT ["dotnet", "ReSharder.Operator.dll"]
