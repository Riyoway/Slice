$ErrorActionPreference = 'Stop'
dotnet restore
dotnet build -c Release -p:Platform=x64
