# Use a imagem base do .NET 8.0 SDK para build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

# Copie o .csproj e restaure dependências
COPY SignalServer.csproj ./
RUN dotnet restore

# Copie o restante do código e build
COPY . ./
RUN dotnet publish -c Release -o out

# Use a imagem runtime do .NET 8.0
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/out .

# Exponha a porta 5000
EXPOSE 5000

# Defina a variável de ambiente para a porta
ENV ASPNETCORE_URLS=http://0.0.0.0:5000

# Inicie o aplicativo
ENTRYPOINT ["dotnet", "SignalServer.dll"]