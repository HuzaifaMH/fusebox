# Builds the two images in this repository: the gateway, and the demo that goes with it. Build one with --target.
#   docker build --target gateway -t fusebox-gateway .
#   docker build --target demo -t fusebox-demo .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, so a code change does not download every package again.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Fusebox.Domain/Fusebox.Domain.csproj src/Fusebox.Domain/
COPY src/Fusebox.Application/Fusebox.Application.csproj src/Fusebox.Application/
COPY src/Fusebox.Infrastructure/Fusebox.Infrastructure.csproj src/Fusebox.Infrastructure/
COPY src/Fusebox.Gateway/Fusebox.Gateway.csproj src/Fusebox.Gateway/
COPY demo/Fusebox.Demo/Fusebox.Demo.csproj demo/Fusebox.Demo/
RUN dotnet restore src/Fusebox.Gateway/Fusebox.Gateway.csproj \
 && dotnet restore demo/Fusebox.Demo/Fusebox.Demo.csproj

COPY src/ src/
COPY demo/ demo/
RUN dotnet publish src/Fusebox.Gateway/Fusebox.Gateway.csproj -c Release --no-restore -o /out/gateway \
 && dotnet publish demo/Fusebox.Demo/Fusebox.Demo.csproj -c Release --no-restore -o /out/demo

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS gateway
WORKDIR /app
COPY --from=build /out/gateway ./
# The base image's own unprivileged user. Port 8080 needs no special rights.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Fusebox.Gateway.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS demo
WORKDIR /app
COPY --from=build /out/demo ./
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Fusebox.Demo.dll"]
