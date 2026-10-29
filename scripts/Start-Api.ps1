param(
    [string]$CredentialsPath = 'C:\Users\Javi\Documents\Javi\tunapp-j27-firebase-adminsdk-fbsvc-e39e5a23d3.json',
    [string]$Urls = 'http://localhost:5188'
)
$ErrorActionPreference = 'Stop'
if (!(Test-Path -LiteralPath $CredentialsPath -PathType Leaf)) { throw 'No se encuentra el archivo de credenciales del servidor.' }
$env:GOOGLE_APPLICATION_CREDENTIALS = (Resolve-Path -LiteralPath $CredentialsPath).Path
$env:Firebase__ProjectId = 'tunapp-j27'
# Escucha local por defecto. Un móvil físico requiere alojamiento HTTPS o un túnel de desarrollo.
dotnet run --project (Join-Path $PSScriptRoot '..\src\Tunapp.Api\Tunapp.Api.csproj') --urls $Urls
