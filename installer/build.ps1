$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$Version = "1.0.5"

$projectRoot = Split-Path -Parent $PSScriptRoot
$standaloneProject = Join-Path $projectRoot "standalone\NoNoStandalone.csproj"
$releaseRoot = Join-Path $projectRoot "release"
$installerProject = Join-Path $PSScriptRoot "NoNoInstaller.wixproj"
$setupProject = Join-Path $PSScriptRoot "NoNoSetup.csproj"
$packageAppDirectory = (Join-Path $PSScriptRoot "obj\package-app\$Version") + [IO.Path]::DirectorySeparatorChar
$packageIntermediateDirectory = (Join-Path $PSScriptRoot "obj\package-build\$Version") + [IO.Path]::DirectorySeparatorChar

dotnet build $standaloneProject -c Release "-p:OutDir=$packageAppDirectory" "-p:IntermediateOutputPath=$packageIntermediateDirectory"
if ($LASTEXITCODE -ne 0) {
    throw "NoNo application build failed."
}

$standaloneExecutable = Join-Path $packageAppDirectory "NoNo-Standalone.exe"
$selfTest = Start-Process -FilePath $standaloneExecutable -ArgumentList "--self-test" -WindowStyle Hidden -Wait -PassThru
if ($selfTest.ExitCode -ne 0) {
    throw "NoNo application self-test failed with exit code $($selfTest.ExitCode)."
}

$screenTranslationSelfTest = Start-Process -FilePath $standaloneExecutable -ArgumentList "--screen-translation-self-test" -WindowStyle Hidden -Wait -PassThru
if ($screenTranslationSelfTest.ExitCode -ne 0) {
    $screenTranslationLog = Join-Path (Split-Path -Parent $standaloneExecutable) "NoNo-ScreenTranslation.selftest.log"
    $details = if (Test-Path -LiteralPath $screenTranslationLog) { Get-Content -LiteralPath $screenTranslationLog -Raw } else { "No log was produced." }
    throw "NoNo screen translation self-test failed with exit code $($screenTranslationSelfTest.ExitCode).`n$details"
}

$clipboardOcrSelfTest = Start-Process -FilePath $standaloneExecutable -ArgumentList "--clipboard-ocr-self-test" -WindowStyle Hidden -Wait -PassThru
if ($clipboardOcrSelfTest.ExitCode -ne 0) {
    $clipboardOcrLog = Join-Path (Split-Path -Parent $standaloneExecutable) "NoNo-ClipboardOcr.selftest.log"
    $details = if (Test-Path -LiteralPath $clipboardOcrLog) { Get-Content -LiteralPath $clipboardOcrLog -Raw } else { "No log was produced." }
    throw "NoNo clipboard OCR self-test failed with exit code $($clipboardOcrSelfTest.ExitCode).`n$details"
}

$appDirectory = Split-Path -Parent $standaloneExecutable
$requiredScreenTranslationFiles = @(
    "THIRD-PARTY-NOTICES.txt",
    "OpenCvSharp.dll",
    "Sdcb.PaddleInference.dll",
    "Sdcb.PaddleOCR.dll",
    "Sdcb.PaddleOCR.Models.Local.dll",
    "Sdcb.PaddleOCR.Models.LocalV5.dll",
    "Sdcb.PaddleOCR.Models.Shared.dll",
    "System.Buffers.dll",
    "System.Memory.dll",
    "System.Numerics.Vectors.dll",
    "System.Runtime.CompilerServices.Unsafe.dll",
    "YamlDotNet.dll",
    "dll\x64\common.dll",
    "dll\x64\onnxruntime.dll",
    "dll\x64\onnxruntime_providers_shared.dll",
    "dll\x64\libiomp5md.dll",
    "dll\x64\mkldnn.dll",
    "dll\x64\mklml.dll",
    "dll\x64\OpenCvSharpExtern.dll",
    "dll\x64\paddle2onnx.dll",
    "dll\x64\paddle_inference_c.dll",
    "dll\x64\phi.dll",
    "ocr-models\README.md",
    "ocr-models\PP-OCRv5_server_det_infer\inference.json",
    "ocr-models\PP-OCRv5_server_det_infer\inference.pdiparams",
    "ocr-models\PP-OCRv5_server_det_infer\inference.yml",
    "ocr-models\PP-OCRv5_server_rec_infer\inference.json",
    "ocr-models\PP-OCRv5_server_rec_infer\inference.pdiparams",
    "ocr-models\PP-OCRv5_server_rec_infer\inference.yml",
    "ocr-models\PP-OCRv5_server_rec_infer\ppocrv5_server_dict.txt",
    "ocr-models\en_PP-OCRv5_mobile_rec_infer\inference.json",
    "ocr-models\en_PP-OCRv5_mobile_rec_infer\inference.pdiparams",
    "ocr-models\en_PP-OCRv5_mobile_rec_infer\inference.yml",
    "ocr-models\en_PP-OCRv5_mobile_rec_infer\en_ppocrv5_dict.txt"
)
$missingScreenTranslationFiles = @($requiredScreenTranslationFiles | Where-Object {
    -not (Test-Path -LiteralPath (Join-Path $appDirectory $_) -PathType Leaf)
})
if ($missingScreenTranslationFiles.Count -gt 0) {
    throw "Screen translation runtime payload is incomplete: $($missingScreenTranslationFiles -join ', ')"
}

# Package.wxs lists every payload file explicitly. Debug symbols, caches,
# virtual environments, and user configuration are therefore excluded.
New-Item -ItemType Directory -Force -Path $releaseRoot | Out-Null

dotnet build $installerProject -c Release -t:Rebuild -p:ProductVersion=$Version "-p:AppDir=$appDirectory"
if ($LASTEXITCODE -ne 0) {
    throw "NoNo MSI build failed."
}

$msi = Get-ChildItem -Path $releaseRoot -Recurse -Filter "NoNo-Desktop-Pet-$Version-x64.msi" |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if ($null -eq $msi) {
    throw "The MSI build completed but the expected package was not found."
}

$hash = Get-FileHash -LiteralPath $msi.FullName -Algorithm SHA256

dotnet build $setupProject -c Release -t:Rebuild -p:ProductVersion=$Version "-p:MsiPath=$($msi.FullName)"
if ($LASTEXITCODE -ne 0) {
    throw "NoNo EXE installer build failed."
}

$setupFileName = "NoNo-Desktop-Pet-Setup-$Version-x64.exe"
$setupPath = Join-Path $releaseRoot $setupFileName
if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
    throw "The EXE installer build completed but the expected package was not found."
}

$bundleVerifyRoot = Join-Path $PSScriptRoot ("obj\bundle-verify-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $bundleVerifyRoot | Out-Null
$bundledMsiPath = Join-Path $bundleVerifyRoot $msi.Name
$payloadExtraction = Start-Process -FilePath $setupPath -ArgumentList @("--extract-payload", $bundledMsiPath) -WindowStyle Hidden -Wait -PassThru
if ($payloadExtraction.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $bundledMsiPath -PathType Leaf)) {
    throw "NoNo EXE installer payload extraction failed with exit code $($payloadExtraction.ExitCode)."
}
$bundledMsiHash = Get-FileHash -LiteralPath $bundledMsiPath -Algorithm SHA256
if (-not [String]::Equals($hash.Hash, $bundledMsiHash.Hash, [StringComparison]::OrdinalIgnoreCase)) {
    throw "NoNo EXE installer contains an unexpected MSI payload."
}
$setupUiSelfTest = Start-Process -FilePath $setupPath -ArgumentList "--ui-self-test" -WindowStyle Hidden -Wait -PassThru
if ($setupUiSelfTest.ExitCode -ne 0) {
    throw "NoNo EXE installer UI self-test failed with exit code $($setupUiSelfTest.ExitCode)."
}
$setup = Get-Item -LiteralPath $setupPath
$setupHash = Get-FileHash -LiteralPath $setup.FullName -Algorithm SHA256

$portableFileName = "NoNo-Standalone-$Version.exe"
$portablePath = Join-Path $releaseRoot $portableFileName
$portableConfigPath = $portablePath + ".config"
Copy-Item -LiteralPath $standaloneExecutable -Destination $portablePath -Force
Copy-Item -LiteralPath (Join-Path $appDirectory "NoNo-Standalone.exe.config") -Destination $portableConfigPath -Force

$portable = Get-Item -LiteralPath $portablePath
$portableHash = Get-FileHash -LiteralPath $portable.FullName -Algorithm SHA256
$checksumPath = Join-Path $releaseRoot "SHA256SUMS.txt"
$checksumLines = @(
    "$($portableHash.Hash) *$($portable.Name)",
    "$($hash.Hash) *$($msi.Name)",
    "$($setupHash.Hash) *$($setup.Name)"
)
[IO.File]::WriteAllLines($checksumPath, $checksumLines, (New-Object Text.UTF8Encoding($false)))

Write-Host "Built MSI: $($msi.FullName)"
Write-Host "MSI SHA256: $($hash.Hash)"
Write-Host "Built EXE installer: $($setup.FullName)"
Write-Host "EXE installer SHA256: $($setupHash.Hash)"
Write-Host "Built portable EXE: $($portable.FullName)"
Write-Host "Portable EXE SHA256: $($portableHash.Hash)"
