param([string]$JavaHome = 'C:\Program Files\Android\openjdk\jdk-21.0.8')
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath (Join-Path $JavaHome 'bin\java.exe')) {
    $env:JAVA_HOME = $JavaHome
    $env:PATH = "$JavaHome\bin;$env:PATH"
}
$root = Split-Path -Parent $PSScriptRoot
$firebase = Join-Path $root 'tests\firebase\node_modules\.bin\firebase.cmd'
if (!(Test-Path -LiteralPath $firebase)) { throw 'Instala primero las herramientas: npm.cmd ci --prefix tests/firebase' }
# Proyecto ficticio fijo: nunca usa los datos reales de tunapp-j27.
& $firebase emulators:start --project demo-tunapp-direct --only auth,firestore --config (Join-Path $root 'firebase.json')
