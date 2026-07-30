# Codex WSL Custom Pets Patch

A temporary workaround for Codex Desktop custom pets when the app-server is configured to run through WSL on Windows.

In affected builds, custom pet discovery can construct a mixed Windows/POSIX path such as:

```text
C:\Users\<USER>\.codex/pets
```

When the app-server is running in WSL, that path is not discoverable from Linux. This patch normalizes the custom pets and avatars discovery base path to the WSL mount form before those paths are joined:

```text
/mnt/c/Users/<USER>/.codex/pets
```

## What This Does

- Copies the installed Codex Desktop app into a writable local patch directory.
- Applies a narrow byte-level patch to custom pet/avatar discovery directly inside the copied `app.asar`.
- Updates the copied `Codex.exe` ASAR integrity hash when the current Codex build embeds one.
- Creates a `Codex Patched.lnk` shortcut in the current directory.
- Optionally launches the patched copy directly.

The installed WindowsApps package is not modified.

## Requirements

- Windows with the Codex Desktop MSIX package installed.
- PowerShell.
- Node.js available on `PATH`.

## Validated Version

This patch was verified against:

- Package: `OpenAI.Codex_26.506.2212.0_x64__2p2nqsd0c76g0`
- Package version: `26.506.2212.0`
- `Codex.exe` product version: `26.506.21252`

It also handles `OpenAI.Codex_26.527.3686.0_x64__2p2nqsd0c76g0`, where the copied `Codex.exe` does not contain the old embedded `app.asar` integrity hash.

The patch is tied to the bundled JavaScript shape and should be re-run after package updates.

## Usage

Patch the installed app into `%LOCALAPPDATA%\CodexWslCustomPetsPatch` and create `Codex Patched.lnk` in the current directory:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\patch-codex-wsl-custom-pets.ps1 -StopExistingCodex
```

The app copy uses `robocopy /MT:16` by default. You can tune the thread count:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\patch-codex-wsl-custom-pets.ps1 -StopExistingCodex -RobocopyThreads 32
```

Patch and launch the copied app:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\patch-codex-wsl-custom-pets.ps1 -StopExistingCodex -Launch
```

Create the shortcut somewhere else:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\patch-codex-wsl-custom-pets.ps1 -ShortcutPath "$env:USERPROFILE\Desktop\Codex Patched.lnk"
```

Remove the copied patched app and generated shortcut:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\patch-codex-wsl-custom-pets.ps1 -Restore -StopExistingCodex
```

If you used a custom `-ShortcutPath`, pass the same path with `-Restore`.

By default, the patched copy is written under:

```text
%LOCALAPPDATA%\CodexWslCustomPetsPatch\<PackageFullName>\app
```

The default shortcut is:

```text
.\Codex Patched.lnk
```

## Verify

1. Place a valid custom pet at:

   ```text
   C:\Users\<USER>\.codex\pets\<pet-id>\pet.json
   ```

2. Launch the patched copy using `Codex Patched.lnk`.
3. Configure Codex Desktop to use the WSL app-server mode.
4. Open `Settings -> Appearance -> Pets`.
5. Click `Refresh`.

The custom pet should appear. In WSL path context, discovery should no longer stay at the mixed path form:

```text
C:\Users\<USER>\.codex/pets
```

## Restore

Close the patched app and run:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\patch-codex-wsl-custom-pets.ps1 -Restore -StopExistingCodex
```

This removes the patched copy and the default generated shortcut. The installed WindowsApps package is not modified by this project.

## Limitations

- This is a temporary workaround for affected Codex Desktop builds.
- Re-run the script after Codex Desktop updates, because the installed package path and bundle contents may change.
- The byte-level patterns are intentionally strict. If the upstream bundle changes, the script fails instead of patching the wrong code.
- The patch is scoped to custom pet/avatar discovery. It does not change global `CODEX_HOME`, app-server launch behavior, terminal command execution, workspace paths, skills, agents, or file browsing helpers.
