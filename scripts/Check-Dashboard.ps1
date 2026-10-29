$ErrorActionPreference = 'Stop'
& npm.cmd --prefix tests/firebase test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& dotnet run --project tests/Tunapp.DirectChecks --no-build --no-restore
exit $LASTEXITCODE
