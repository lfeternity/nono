param(
    [switch]$Launch,
    [switch]$StopExistingCodex,
    [switch]$Restore,
    [string]$PackageName = "OpenAI.Codex",
    [string]$PatchRoot = (Join-Path $env:LOCALAPPDATA "CodexWslCustomPetsPatch"),
    [string]$ShortcutPath = (Join-Path (Get-Location).Path "Codex Patched.lnk"),
    [ValidateRange(1, 128)]
    [int]$RobocopyThreads = 16
)

$ErrorActionPreference = "Stop"

function Get-FullPath([string]$Path) {
    return [System.IO.Path]::GetFullPath($Path)
}

function Get-ExtendedLengthPath([string]$Path) {
    $fullPath = Get-FullPath $Path
    if ($fullPath.StartsWith("\\?\", [System.StringComparison]::Ordinal)) {
        return $fullPath
    }
    if ($fullPath.StartsWith("\\", [System.StringComparison]::Ordinal)) {
        return "\\?\UNC\" + $fullPath.Substring(2)
    }
    return "\\?\$fullPath"
}

function Assert-UnderDirectory([string]$Path, [string]$Root) {
    $fullPath = Get-FullPath $Path
    $fullRoot = (Get-FullPath $Root).TrimEnd("\")
    $prefix = "$fullRoot\"
    if ($fullPath -ne $fullRoot -and -not $fullPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to operate outside patch root: $fullPath"
    }
}

function Remove-DirectoryIfPresent([string]$Path, [string]$Root) {
    Assert-UnderDirectory $Path $Root
    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $longPath = Get-ExtendedLengthPath $Path
    $lastError = $null
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            if (-not [System.IO.Directory]::Exists($longPath)) {
                return
            }
            [System.IO.Directory]::Delete($longPath, $true)
            if (-not [System.IO.Directory]::Exists($longPath)) {
                return
            }
        } catch {
            $lastError = $_
            Start-Sleep -Milliseconds (250 * $attempt)
        }
    }

    Require-Command "robocopy" | Out-Null

    $emptyDir = Join-Path $Root ("empty-delete-" + [System.Guid]::NewGuid().ToString("N"))
    Assert-UnderDirectory $emptyDir $Root
    New-Item -ItemType Directory -Path $emptyDir -Force | Out-Null
    try {
        & robocopy $emptyDir $Path /MIR /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
        if ($LASTEXITCODE -ge 8) {
            throw "robocopy purge failed with exit code $LASTEXITCODE"
        }
    } finally {
        $emptyLongPath = Get-ExtendedLengthPath $emptyDir
        if ([System.IO.Directory]::Exists($emptyLongPath)) {
            try {
                [System.IO.Directory]::Delete($emptyLongPath, $true)
            } catch {
                Write-Host "Warning: failed to remove temporary empty directory: $emptyDir"
            }
        }
    }

    try {
        if ([System.IO.Directory]::Exists($longPath)) {
            [System.IO.Directory]::Delete($longPath, $true)
        }
    } catch {
        $detail = $_.Exception.Message
        if ($null -ne $lastError) {
            $detail = "$detail Previous detail: $($lastError.Exception.Message)"
        }
        throw "Failed to remove directory tree: $Path. Detail: $detail"
    }
}

function Remove-ShortcutIfPresent([string]$Path) {
    $fullPath = Get-FullPath $Path
    if ([System.IO.Path]::GetExtension($fullPath) -ne ".lnk") {
        throw "Refusing to remove non-shortcut path: $fullPath"
    }
    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Force
    }
}

function Require-Command([string]$Name) {
    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        throw "Required command not found: $Name"
    }
    return $command
}

function Get-DirectoryFileStats([string]$Path) {
    $stats = [pscustomobject]@{
        Files = [int64]0
        Bytes = [int64]0
    }

    if (-not (Test-Path -LiteralPath $Path)) {
        return $stats
    }

    $dirs = New-Object 'System.Collections.Generic.Stack[string]'
    $dirs.Push((Get-ExtendedLengthPath $Path))
    while ($dirs.Count -gt 0) {
        $dir = $dirs.Pop()
        foreach ($file in [System.IO.Directory]::EnumerateFiles($dir)) {
            $fileInfo = New-Object System.IO.FileInfo($file)
            $stats.Files += 1
            $stats.Bytes += $fileInfo.Length
        }
        foreach ($subdir in [System.IO.Directory]::EnumerateDirectories($dir)) {
            $attributes = [System.IO.File]::GetAttributes($subdir)
            if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) {
                $dirs.Push($subdir)
            }
        }
    }
    return $stats
}

function Format-ByteSize([int64]$Bytes) {
    if ($Bytes -ge 1GB) {
        return "{0:N1} GB" -f ($Bytes / 1GB)
    }
    if ($Bytes -ge 1MB) {
        return "{0:N1} MB" -f ($Bytes / 1MB)
    }
    if ($Bytes -ge 1KB) {
        return "{0:N1} KB" -f ($Bytes / 1KB)
    }
    return "$Bytes B"
}

function Write-CopyProgress([string]$Destination, [object]$SourceStats) {
    $activity = "Copying Codex app"
    if ($SourceStats.Bytes -le 0) {
        Write-Progress -Activity $activity -Status "Copying files..." -PercentComplete 0
        return
    }

    try {
        $destStats = Get-DirectoryFileStats $Destination
    } catch {
        Write-Progress -Activity $activity -Status "Copying files..." -PercentComplete 0
        return
    }
    $percent = [math]::Floor(($destStats.Bytes / $SourceStats.Bytes) * 100)
    $percent = [math]::Max(0, [math]::Min(99, $percent))
    $status = "{0} / {1}, {2:N0} / {3:N0} files" -f `
        (Format-ByteSize $destStats.Bytes), `
        (Format-ByteSize $SourceStats.Bytes), `
        $destStats.Files, `
        $SourceStats.Files

    Write-Progress -Activity $activity -Status $status -PercentComplete $percent
}

function Invoke-Robocopy([string]$Source, [string]$Destination) {
    Require-Command "robocopy" | Out-Null

    $activity = "Copying Codex app"
    Write-Progress -Activity $activity -Status "Scanning source files..." -PercentComplete 0
    try {
        $sourceStats = Get-DirectoryFileStats $Source
    } catch {
        Write-Host "Warning: failed to pre-scan source for copy progress. Copy will continue with an indeterminate progress bar."
        $sourceStats = [pscustomobject]@{
            Files = [int64]0
            Bytes = [int64]0
        }
    }
    Write-Progress `
        -Activity $activity `
        -Status ("Copying {0:N0} files ({1}) with robocopy /MT:{2}..." -f $sourceStats.Files, (Format-ByteSize $sourceStats.Bytes), $RobocopyThreads) `
        -PercentComplete 0

    $copyJob = Start-Job -ScriptBlock {
        param([string]$SourcePath, [string]$DestinationPath, [int]$Threads)
        & robocopy $SourcePath $DestinationPath /E /MT:$Threads /NFL /NDL /NJH /NJS /NP | Out-Null
        $LASTEXITCODE
    } -ArgumentList $Source, $Destination, $RobocopyThreads

    try {
        while ($copyJob.State -eq "Running") {
            Write-CopyProgress $Destination $sourceStats
            Start-Sleep -Milliseconds 2000
        }

        $jobState = $copyJob.State
        $jobOutput = @(Receive-Job -Job $copyJob -Wait -ErrorAction Stop)
        if ($jobState -eq "Failed") {
            throw "robocopy job failed."
        }

        $exitCode = 0
        if ($jobOutput.Count -gt 0) {
            $exitCode = [int]$jobOutput[-1]
        }
        if ($exitCode -ge 8) {
            throw "robocopy failed with exit code $exitCode"
        }

        Write-Progress -Activity $activity -Status "Copy complete." -PercentComplete 100
    } finally {
        Write-Progress -Activity $activity -Completed
        Remove-Job -Job $copyJob -Force -ErrorAction SilentlyContinue
    }
}

function Read-Exact([System.IO.Stream]$Stream, [byte[]]$Buffer, [int]$Count) {
    $offset = 0
    while ($offset -lt $Count) {
        $read = $Stream.Read($Buffer, $offset, $Count - $offset)
        if ($read -le 0) {
            throw "Unexpected end of stream."
        }
        $offset += $read
    }
}

function Convert-BytesToLowerHex([byte[]]$Bytes) {
    return -join ($Bytes | ForEach-Object { $_.ToString("x2") })
}

function Get-AsarHeaderIntegrityHash([string]$AsarPath) {
    $stream = [System.IO.File]::OpenRead($AsarPath)
    try {
        $sizeBuffer = [byte[]]::new(8)
        Read-Exact $stream $sizeBuffer $sizeBuffer.Length

        $headerSize = [System.BitConverter]::ToUInt32($sizeBuffer, 4)
        if ($headerSize -gt [int]::MaxValue) {
            throw "ASAR header is too large to hash safely: $headerSize bytes"
        }

        $headerBuffer = [byte[]]::new([int]$headerSize)
        Read-Exact $stream $headerBuffer $headerBuffer.Length

        $headerStringLength = [System.BitConverter]::ToInt32($headerBuffer, 4)
        if ($headerStringLength -lt 0 -or 8 + $headerStringLength -gt $headerBuffer.Length) {
            throw "Invalid ASAR header string length: $headerStringLength"
        }

        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            $hash = $sha256.ComputeHash($headerBuffer, 8, $headerStringLength)
        } finally {
            $sha256.Dispose()
        }
        return Convert-BytesToLowerHex $hash
    } finally {
        $stream.Dispose()
    }
}

function Stop-ExistingCodexProcesses([string]$InstallLocation, [string]$PatchRoot) {
    $installRoot = Get-FullPath $InstallLocation
    $patchRootFull = Get-FullPath $PatchRoot

    $targets = Get-CimInstance Win32_Process |
        Where-Object {
            $_.Name -eq "Codex.exe" -and
            $_.ExecutablePath -and
            (
                $_.ExecutablePath.StartsWith($installRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
                $_.ExecutablePath.StartsWith($patchRootFull, [System.StringComparison]::OrdinalIgnoreCase)
            )
        }

    if ($null -eq $targets -or @($targets).Count -eq 0) {
        return
    }

    Write-Host "Stopping running Codex Desktop processes..."
    foreach ($process in @($targets)) {
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Seconds 2
}

function Find-ByteSequenceOffsets([byte[]]$Bytes, [byte[]]$Pattern) {
    $offsets = New-Object System.Collections.Generic.List[int]
    if ($Pattern.Length -eq 0 -or $Bytes.Length -lt $Pattern.Length) {
        return $offsets
    }

    $index = 0
    while ($index -lt $Bytes.Length) {
        $index = [Array]::IndexOf($Bytes, $Pattern[0], $index)
        if ($index -lt 0) {
            break
        }
        if ($index + $Pattern.Length -le $Bytes.Length) {
            $matched = $true
            for ($i = 1; $i -lt $Pattern.Length; $i++) {
                if ($Bytes[$index + $i] -ne $Pattern[$i]) {
                    $matched = $false
                    break
                }
            }
            if ($matched) {
                $offsets.Add($index)
            }
        }
        $index += 1
    }
    return $offsets
}

function Replace-ByteSequence([byte[]]$Bytes, [byte[]]$OldBytes, [byte[]]$NewBytes) {
    $offsets = Find-ByteSequenceOffsets $Bytes $OldBytes
    if ($offsets.Count -ne 1) {
        throw "Expected exactly one custom pet/avatar loader byte sequence; found $($offsets.Count)."
    }

    $offset = $offsets[0]
    $prefixLength = $offset
    $suffixOffset = $offset + $OldBytes.Length
    $suffixLength = $Bytes.Length - $suffixOffset
    $result = [byte[]]::new($Bytes.Length - $OldBytes.Length + $NewBytes.Length)

    [Array]::Copy($Bytes, 0, $result, 0, $prefixLength)
    [Array]::Copy($NewBytes, 0, $result, $prefixLength, $NewBytes.Length)
    [Array]::Copy($Bytes, $suffixOffset, $result, $prefixLength + $NewBytes.Length, $suffixLength)
    return $result
}

function Update-AsarIntegrityHash([string]$ExePath, [string]$OldHash, [string]$NewHash) {
    $old = $OldHash.ToLowerInvariant()
    $new = $NewHash.ToLowerInvariant()
    if ($old -eq $new) {
        return
    }
    if ($old.Length -ne 64 -or $new.Length -ne 64) {
        throw "Unexpected SHA-256 hash length while updating ASAR integrity."
    }

    $encoding = [System.Text.Encoding]::ASCII
    $oldBytes = $encoding.GetBytes($old)
    $newBytes = $encoding.GetBytes($new)
    $bytes = [System.IO.File]::ReadAllBytes($ExePath)
    $offsets = Find-ByteSequenceOffsets $bytes $oldBytes
    if ($offsets.Count -eq 0) {
        throw "Original ASAR integrity hash not found in patched Codex.exe: $old"
    }

    foreach ($offset in $offsets) {
        [Array]::Copy($newBytes, 0, $bytes, $offset, $newBytes.Length)
    }
    [System.IO.File]::WriteAllBytes($ExePath, $bytes)
    Write-Host "Updated ASAR integrity hash in patched Codex.exe ($($offsets.Count) occurrence(s))."
}

function Get-HashOccurrenceCount([string]$Path, [string]$Hash) {
    if ($Hash.Length -ne 64) {
        throw "Unexpected SHA-256 hash length while searching for ASAR integrity."
    }

    $encoding = [System.Text.Encoding]::ASCII
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $hashBytes = $encoding.GetBytes($Hash.ToLowerInvariant())
    return (Find-ByteSequenceOffsets $bytes $hashBytes).Count
}

function Test-JavaScriptSyntax([string]$Path) {
    $node = Get-Command "node" -ErrorAction SilentlyContinue
    if ($null -eq $node) {
        Write-Host "node not found; skipping JavaScript syntax check."
        return
    }

    & $node.Source --check $Path
    if ($LASTEXITCODE -ne 0) {
        throw "JavaScript syntax check failed after patching: $Path"
    }
}

function Invoke-AsarJavaScriptPatch([string]$AsarPath, [string]$WorkDir) {
    $node = Require-Command "node"
    $patcherPath = Join-Path $WorkDir "patch-asar-custom-pets.js"
    Assert-UnderDirectory $patcherPath $WorkDir

    $patcher = @'
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const crypto = require("node:crypto");
const { spawnSync } = require("node:child_process");

const asarPath = process.argv[2];
if (!asarPath) {
  throw new Error("Usage: node patch-asar-custom-pets.js <app.asar>");
}

const patterns = [
  {
    name: "Codex 26.506 custom pet/avatar loader",
    old: "let n=We({preferWsl:t,hostConfig:e.hostConfig}),r=await e.platformPath(),i=r.join(n,`pets`),a=r.join(n,`avatars`);",
    replacement: "let n=We({preferWsl:t,hostConfig:e.hostConfig}),r=await e.platformPath();r.join(n,`pets`).includes(`/`)&&/^[a-zA-Z]:[\\\\/]/.test(n)&&(n=Ue(n));let i=r.join(n,`pets`),a=r.join(n,`avatars`);",
  },
  {
    name: "Codex 26.527 custom pet/avatar loader",
    old: "let n=Be({preferWsl:t,hostConfig:e.hostConfig}),r=await e.platformPath(),i=r.join(n,`pets`),a=r.join(n,`avatars`);",
    replacement: "let n=Be({preferWsl:t,hostConfig:e.hostConfig}),r=await e.platformPath();r.join(n,`pets`).includes(`/`)&&/^[a-zA-Z]:[\\\\/]/.test(n)&&(n=ze(n));let i=r.join(n,`pets`),a=r.join(n,`avatars`);",
  },
  {
    name: "Codex 26.602 custom pet/avatar loader",
    old: "let n=L({preferWsl:t,hostConfig:e.hostConfig}),r=await e.platformPath(),i=r.join(n,`pets`),a=r.join(n,`avatars`);",
    replacement: "let n=L({preferWsl:t,hostConfig:e.hostConfig}),r=await e.platformPath();r.join(n,`pets`).includes(`/`)&&/^[a-zA-Z]:[\\\\/]/.test(n)&&(n=Ve(n));let i=r.join(n,`pets`),a=r.join(n,`avatars`);",
  },
];

function parseAsar(buffer) {
  const headerSize = buffer.readUInt32LE(4);
  const headerBuffer = buffer.subarray(8, 8 + headerSize);
  const headerStringLength = headerBuffer.readUInt32LE(4);
  const headerJson = headerBuffer.subarray(8, 8 + headerStringLength).toString("utf8");
  return {
    header: JSON.parse(headerJson),
    headerSize,
    dataStart: 8 + headerSize,
  };
}

function walkFiles(node, parts = [], out = []) {
  const files = node.files ?? {};
  for (const [name, child] of Object.entries(files)) {
    const childParts = [...parts, name];
    if (typeof child.offset === "string" && typeof child.size === "number") {
      out.push({
        path: childParts.join("/"),
        entry: child,
        offset: Number(child.offset),
        size: child.size,
        unpacked: child.unpacked === true,
      });
    }
    if (child.files) {
      walkFiles(child, childParts, out);
    }
  }
  return out;
}

function countOccurrences(haystack, needle) {
  let count = 0;
  let index = 0;
  while (index <= haystack.length - needle.length) {
    const found = haystack.indexOf(needle, index);
    if (found < 0) {
      break;
    }
    count += 1;
    index = found + 1;
  }
  return count;
}

function checkJavaScriptSyntax(bytes) {
  const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "codex-asar-patch-"));
  const tempFile = path.join(tempDir, "bundle.mjs");
  try {
    fs.writeFileSync(tempFile, bytes);
    const result = spawnSync(process.execPath, ["--check", tempFile], {
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"],
    });
    if (result.status !== 0) {
      const detail = [result.stdout, result.stderr].filter(Boolean).join("\n").trim();
      throw new Error(`JavaScript syntax check failed after patching.${detail ? `\n${detail}` : ""}`);
    }
  } finally {
    fs.rmSync(tempDir, { force: true, recursive: true });
  }
}

function buildHeaderBuffer(header) {
  const headerJson = JSON.stringify(header);
  const headerJsonBuffer = Buffer.from(headerJson, "utf8");
  const padding = (4 - (headerJsonBuffer.length % 4)) % 4;
  const headerSize = 8 + headerJsonBuffer.length + padding;

  const sizePickle = Buffer.alloc(8);
  sizePickle.writeUInt32LE(4, 0);
  sizePickle.writeUInt32LE(headerSize, 4);

  const headerPickle = Buffer.alloc(headerSize);
  headerPickle.writeUInt32LE(headerSize - 4, 0);
  headerPickle.writeUInt32LE(headerJsonBuffer.length, 4);
  headerJsonBuffer.copy(headerPickle, 8);

  return Buffer.concat([sizePickle, headerPickle]);
}

function sha256Hex(bytes) {
  return crypto.createHash("sha256").update(bytes).digest("hex");
}

function updateEntryIntegrity(entry, bytes) {
  if (!entry.integrity) {
    return;
  }
  if (entry.integrity.algorithm !== "SHA256") {
    throw new Error(`Unsupported ASAR file integrity algorithm: ${entry.integrity.algorithm}`);
  }

  const blockSize = entry.integrity.blockSize || 4194304;
  const blocks = [];
  for (let offset = 0; offset < bytes.length; offset += blockSize) {
    blocks.push(sha256Hex(bytes.subarray(offset, Math.min(offset + blockSize, bytes.length))));
  }

  entry.integrity.hash = sha256Hex(bytes);
  entry.integrity.blockSize = blockSize;
  entry.integrity.blocks = blocks;
}

function main() {
  const original = fs.readFileSync(asarPath);
  const { header, dataStart } = parseAsar(original);
  const data = original.subarray(dataStart);
  const files = walkFiles(header);
  const jsFiles = files.filter((file) => !file.unpacked && file.path.endsWith(".js"));
  const matches = [];

  for (const file of jsFiles) {
    const content = data.subarray(file.offset, file.offset + file.size);
    for (const pattern of patterns) {
      const oldBytes = Buffer.from(pattern.old, "ascii");
      const newBytes = Buffer.from(pattern.replacement, "ascii");
      const oldCount = countOccurrences(content, oldBytes);
      const newCount = countOccurrences(content, newBytes);
      if (oldCount > 1 || newCount > 1) {
        throw new Error(`Expected at most one loader sequence per bundle; found old=${oldCount} new=${newCount} in ${file.path}.`);
      }
      if (oldCount === 1 || newCount === 1) {
        matches.push({
          file,
          pattern,
          alreadyPatched: newCount === 1,
          oldBytes,
          newBytes,
        });
      }
    }
  }

  if (matches.length !== 1) {
    throw new Error(`Expected exactly one custom pet/avatar loader byte sequence across ASAR JavaScript bundles; found ${matches.length}.`);
  }

  const match = matches[0];
  if (match.alreadyPatched) {
    console.log(`Custom pet/avatar loader is already patched in ${match.file.path}.`);
    return;
  }

  const target = match.file;
  const before = data.subarray(0, target.offset);
  const targetContent = data.subarray(target.offset, target.offset + target.size);
  const after = data.subarray(target.offset + target.size);
  const oldIndex = targetContent.indexOf(match.oldBytes);
  const patchedContent = Buffer.concat([
    targetContent.subarray(0, oldIndex),
    match.newBytes,
    targetContent.subarray(oldIndex + match.oldBytes.length),
  ]);

  checkJavaScriptSyntax(patchedContent);

  const delta = patchedContent.length - target.size;
  target.entry.size = patchedContent.length;
  updateEntryIntegrity(target.entry, patchedContent);
  for (const file of files) {
    if (file.entry !== target.entry && file.offset > target.offset) {
      file.entry.offset = String(file.offset + delta);
    }
  }

  const headerBuffer = buildHeaderBuffer(header);
  const patchedData = Buffer.concat([before, patchedContent, after]);
  fs.writeFileSync(asarPath, Buffer.concat([headerBuffer, patchedData]));
  console.log(`Patched custom pet/avatar loader in ${target.path} using ${match.pattern.name}.`);
}

try {
  main();
} catch (error) {
  console.error(error && error.stack ? error.stack : String(error));
  process.exit(1);
}
'@

    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($patcherPath, $patcher, $encoding)

    Write-Host "Patching copied app.asar without extracting full ASAR..."
    & $node.Source $patcherPath $AsarPath
    if ($LASTEXITCODE -ne 0) {
        throw "ASAR JavaScript patch failed with exit code $LASTEXITCODE"
    }
}

function Start-CodexApp([string]$AppPath) {
    $exe = Join-Path $AppPath "Codex.exe"
    if (-not (Test-Path -LiteralPath $exe)) {
        throw "Codex.exe not found: $exe"
    }

    Write-Host "Launching patched Codex..."
    $process = Start-Process `
        -FilePath $exe `
        -WorkingDirectory $AppPath `
        -PassThru

    Write-Host "Patched Codex process id: $($process.Id)"
}

function New-CodexShortcut([string]$ShortcutPath, [string]$AppPath) {
    $shortcutFullPath = Get-FullPath $ShortcutPath
    if ([System.IO.Path]::GetExtension($shortcutFullPath) -ne ".lnk") {
        throw "ShortcutPath must point to a .lnk file: $shortcutFullPath"
    }

    $exe = Join-Path $AppPath "Codex.exe"
    if (-not (Test-Path -LiteralPath $exe)) {
        throw "Codex.exe not found: $exe"
    }

    $shortcutParent = Split-Path -Parent $shortcutFullPath
    if ($shortcutParent) {
        New-Item -ItemType Directory -Path $shortcutParent -Force | Out-Null
    }

    $shellType = [type]::GetTypeFromProgID("WScript.Shell")
    if ($null -eq $shellType) {
        throw "WScript.Shell COM object is not available; cannot create shortcut."
    }

    $shell = [Activator]::CreateInstance($shellType)
    $shortcut = $shell.CreateShortcut($shortcutFullPath)
    $shortcut.TargetPath = $exe
    $shortcut.WorkingDirectory = $AppPath
    $shortcut.IconLocation = "$exe,0"
    $shortcut.Description = "Launch patched Codex Desktop"
    $shortcut.Save()

    Write-Host "Created shortcut:"
    Write-Host $shortcutFullPath
}

$package = Get-AppxPackage -Name $PackageName
if ($null -eq $package) {
    throw "Codex package not found: $PackageName"
}

$sourceApp = Join-Path $package.InstallLocation "app"
if (-not (Test-Path -LiteralPath $sourceApp)) {
    throw "Codex app directory not found: $sourceApp"
}

$packagePatchRoot = Get-FullPath (Join-Path $PatchRoot $package.PackageFullName)
$patchedApp = Join-Path $packagePatchRoot "app"
$extractDir = Join-Path $packagePatchRoot "asar"
$shortcutFullPath = Get-FullPath $ShortcutPath

if ($StopExistingCodex) {
    Stop-ExistingCodexProcesses $package.InstallLocation $PatchRoot
}

if ($Restore) {
    Remove-DirectoryIfPresent $packagePatchRoot $PatchRoot
    Remove-ShortcutIfPresent $shortcutFullPath
    Write-Host "Removed patched Codex copy:"
    Write-Host $packagePatchRoot
    Write-Host "Removed shortcut:"
    Write-Host $shortcutFullPath
    return
}

Require-Command "node" | Out-Null

New-Item -ItemType Directory -Path $packagePatchRoot -Force | Out-Null
Remove-DirectoryIfPresent $patchedApp $packagePatchRoot
Remove-DirectoryIfPresent $extractDir $packagePatchRoot
New-Item -ItemType Directory -Path $patchedApp -Force | Out-Null

Write-Host "Copying Codex app to patched directory..."
Invoke-Robocopy $sourceApp $patchedApp

$asarPath = Join-Path $patchedApp "resources\app.asar"
if (-not (Test-Path -LiteralPath $asarPath)) {
    throw "app.asar not found in patched copy: $asarPath"
}

$copiedExe = Join-Path $patchedApp "Codex.exe"
if (-not (Test-Path -LiteralPath $copiedExe)) {
    throw "Codex.exe not found in patched copy: $copiedExe"
}

$originalAsarHash = Get-AsarHeaderIntegrityHash $asarPath
$embeddedAsarHashOccurrenceCount = Get-HashOccurrenceCount $copiedExe $originalAsarHash
if ($embeddedAsarHashOccurrenceCount -gt 0) {
    Write-Host "Found embedded ASAR integrity hash in copied Codex.exe ($embeddedAsarHashOccurrenceCount occurrence(s))."
} else {
    Write-Host "No embedded ASAR integrity hash found in copied Codex.exe; this Codex build appears not to store app.asar integrity in the executable."
}

Copy-Item -LiteralPath $asarPath -Destination "$asarPath.bak" -Force
Invoke-AsarJavaScriptPatch $asarPath $packagePatchRoot

$patchedAsarHash = Get-AsarHeaderIntegrityHash $asarPath
if ($embeddedAsarHashOccurrenceCount -gt 0) {
    Update-AsarIntegrityHash $copiedExe $originalAsarHash $patchedAsarHash

    $updatedAsarHashOccurrenceCount = Get-HashOccurrenceCount $copiedExe $patchedAsarHash
    if ($updatedAsarHashOccurrenceCount -lt $embeddedAsarHashOccurrenceCount) {
        throw "Patched Codex.exe ASAR integrity hash was not updated correctly."
    }
} else {
    Write-Host "Skipping ASAR integrity update because the copied Codex.exe did not contain the original app.asar header hash."
}

New-CodexShortcut $shortcutFullPath $patchedApp

Write-Host "Patched app directory:"
Write-Host $patchedApp

if ($Launch) {
    Start-CodexApp $patchedApp
}
