# Technical Notes

## Path Issue

The affected custom pet/avatar loader can join a Windows Codex home with POSIX path behavior while WSL mode is active:

```js
let n=We({preferWsl:t,hostConfig:e.hostConfig}),r=await e.platformPath(),i=r.join(n,`pets`),a=r.join(n,`avatars`);
```

That can produce a mixed path:

```text
C:\Users\<USER>\.codex/pets
```

The patch normalizes only this discovery path when the joined path indicates POSIX joining and the Codex home is still a Windows drive path:

```js
let n=We({preferWsl:t,hostConfig:e.hostConfig}),r=await e.platformPath();r.join(n,`pets`).includes(`/`)&&/^[a-zA-Z]:[\\/]/.test(n)&&(n=Ue(n));let i=r.join(n,`pets`),a=r.join(n,`avatars`);
```

The effective discovery path becomes:

```text
/mnt/c/Users/<USER>/.codex/pets
```

Newer builds keep the same loader shape in a different bundle with different minified helper names. The script searches JavaScript file entries in the ASAR header for the known loader byte sequence instead of assuming one fixed bundle filename.

## Byte-Level Patching

The script reads and writes the JavaScript bundle as bytes inside `app.asar`. This is intentional.

Using text decoding and re-encoding on minified Electron bundles can corrupt bytes and produce startup failures such as JavaScript syntax errors. The patch searches for one exact ASCII byte sequence and replaces it with one exact ASCII byte sequence, then rewrites the ASAR header offsets and the patched file's ASAR integrity metadata for the changed file size.

If the expected sequence is not found exactly once, the script stops.

The script does not extract the whole ASAR to the filesystem. This avoids long-path and deep `node_modules` failures from Windows recursive directory operations.

## ASAR Integrity

Some Codex Desktop builds embed Electron ASAR integrity in the copied `Codex.exe`. The relevant hash is the SHA-256 of the ASAR header JSON bytes, not the SHA-256 of the entire `app.asar` file.

After rewriting the copied ASAR, the script:

1. Computes the new ASAR header integrity hash.
2. Looks for the old ASAR header hash in the copied executable.
3. Replaces it with the new hash if it is present.
4. Verifies the copied executable contains the new hash when an embedded hash was present.

The installed WindowsApps package is not modified. The script creates a shortcut to the patched copy so the user can launch it directly.

## Verified Build

This workaround was verified against:

- `OpenAI.Codex_26.506.2212.0_x64__2p2nqsd0c76g0`
- Package version `26.506.2212.0`
- `Codex.exe` product version `26.506.21252`

The script was also updated for:

- `OpenAI.Codex_26.527.3686.0_x64__2p2nqsd0c76g0`
- Package version `26.527.3686.0`
- No old-style embedded `app.asar` integrity hash in the copied `Codex.exe`

The patch may need to be updated if a later package changes the minified loader shape or the embedded ASAR integrity layout.

## Scope

This workaround is intentionally limited to custom pets and avatars discovery. It does not change:

- Global `CODEX_HOME`.
- App-server launch behavior.
- Terminal command execution.
- Workspace paths.
- Skills.
- Agents.
- File browsing helpers.
