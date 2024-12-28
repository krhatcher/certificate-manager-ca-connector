# Find signtool.exe
$signToolPaths = @(
    "C:\Program Files (x86)\Windows Kits\10\bin\*\x64\signtool.exe",
    "C:\Program Files (x86)\Windows Kits\10\bin\*\x86\signtool.exe"
)

$signtool = Get-ChildItem -Path $signToolPaths -ErrorAction SilentlyContinue | 
            Sort-Object -Property VersionInfo.FileVersion -Descending | 
            Select-Object -First 1 -ExpandProperty FullName

if (-not $signtool) {
    Write-Error "Could not find signtool.exe"
    exit 1
}

Write-Host "Using signtool: $signtool"

# Build solution in Release mode
Write-Host "Building solution..."
dotnet build -c Release

# Publish service
Write-Host "Publishing service..."
dotnet publish src/Service/Service.csproj -c Release -r win-x64 --self-contained false

# Publish UI
Write-Host "Publishing UI..."
dotnet publish src/UI/UI.csproj -c Release -r win-x64 --self-contained true

# Sign the service executable
Write-Host "Signing service executable..."
$servicePath = "src\Service\bin\Release\net8.0\win-x64\publish\ca-connector-svc.exe"
if (Test-Path $servicePath) {
    & $signtool sign /a /tr http://timestamp.digicert.com /td sha256 /fd sha256 $servicePath
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to sign service executable"
        exit 1
    }
} else {
    Write-Error "Service executable not found at $servicePath"
    exit 1
}

# Sign the UI app
Write-Host "Signing UI app..."
$uiPath = "src\UI\bin\Release\net8.0-windows\win-x64\publish\ca-connector-ui.exe"
if (Test-Path $uiPath) {
    & $signtool sign /a /tr http://timestamp.digicert.com /td sha256 /fd sha256 $uiPath
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to sign UI app"
        exit 1
    }
} else {
    Write-Error "UI app not found at $uiPath"
    exit 1
}

# Build installer
Write-Host "Building installer..."
dotnet build src/Installer/Installer.wixproj -c Release

# Sign installer
Write-Host "Signing installer..."
$installerPath = "src/Installer/bin/x64/Release/ca-connector.msi"
if (Test-Path $installerPath) {
    & $signtool sign /a /tr http://timestamp.digicert.com /td sha256 /fd sha256 $installerPath
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to sign installer"
        exit 1
    }
    Write-Host "Installer signed successfully"
} else {
    Write-Error "Installer not found at $installerPath"
    exit 1
}