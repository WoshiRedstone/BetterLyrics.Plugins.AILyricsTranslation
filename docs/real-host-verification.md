# Real-host verification (loose MSIX registration)

The user requirement was to start the *real* BetterLyrics and verify the plugin inside it,
not just against a mock harness. This machine has no Visual Studio (so `wapproj` cannot be
built) and the current user is not an administrator (so a self-signed MSIX cannot be
trusted), and an unpackaged `dotnet build` output cannot start because `AppInstance` throws
`REGDB_E_CLASSNOTREG` without package identity.

The working approach is **loose registration of a copy of the installed Store package**.
Paths below assume a workspace laid out as `<workspace>\BetterLyrics.Plugins.AILyricsTranslation`
(this repo), `<workspace>\BetterLyrics-dev\BetterLyrics-dev` (host sources) and
`<workspace>\dist` (delivered artifact); adjust `$ws` accordingly.

```powershell
$ws = 'C:\BetterLyrics'   # <- change me

# 1. Copy the installed package payload (read-only source, never modified)
$realRoot = 'C:\Program Files\WindowsApps\37412.BetterLyrics_1.3.544.0_x64__rd1g0rsrrtxw8'
robocopy $realRoot "$ws\_packaged\BetterLyrics.Real" /E /NFL /NDL /NJH /NJS /NP

# 2. Copy it again as the test payload and give it a new identity
robocopy "$ws\_packaged\BetterLyrics.Real" "$ws\_packaged\BetterLyrics.Test" /E /NFL /NDL /NJH /NJS /NP
Remove-Item "$ws\_packaged\BetterLyrics.Test\AppxSignature.p7x",
            "$ws\_packaged\BetterLyrics.Test\AppxBlockMap.xml" -Force

# 3. Rewrite Identity Name and the protocol name (so activating the test copy never
#    redirects into the real installed app), and make sure every image the manifest
#    references exists under its unqualified name (the Store payload only ships
#    *.scale-200.png variants, and registration fails with 0x80070002 otherwise).
$t = "$ws\_packaged\BetterLyrics.Test"
$m = Get-Content "$t\AppxManifest.xml" -Raw
$m = $m -replace 'Name="37412\.BetterLyrics"', 'Name="37412.BetterLyrics.Test"'
$m = $m -replace '<uap:Protocol Name="betterlyrics"', '<uap:Protocol Name="betterlyricstest"'
Set-Content "$t\AppxManifest.xml" -Value $m -Encoding UTF8 -NoNewline
foreach ($n in 'LargeTile','SmallTile','SplashScreen','Square150x150Logo','Square44x44Logo','StoreLogo','Wide310x150Logo') {
    Copy-Item "$t\Images\$n.scale-200.png" "$t\Images\$n.png" -Force
}

# 4. Overlay our build output. IMPORTANT: the UI assembly must stay official and the
#    official resources.pri must stay in place - a self-built BetterLyrics.WinUI3.dll
#    cannot resolve its XAML against the packaged resource index and SettingsWindow
#    throws XamlParseException. Only Core and Sdk are replaced.
$bin = "$ws\BetterLyrics-dev\BetterLyrics-dev\src\BetterLyrics.DotNet\BetterLyrics.WinUI3\BetterLyrics.WinUI3\bin\x64\Debug\net10.0-windows10.0.26100.0"
Copy-Item "$bin\BetterLyrics.Core.dll" "$t\BetterLyrics.WinUI3\BetterLyrics.Core.dll" -Force
Copy-Item "$bin\BetterLyrics.Sdk.dll"  "$t\BetterLyrics.WinUI3\BetterLyrics.Sdk.dll"  -Force

# 5. Ship the plugin as a bundled plugin next to the executable. BundledPluginInstaller
#    copies it into the per-user plugins directory on startup, which is what makes it
#    "built in" rather than hand-installed.
robocopy "$ws\dist\BetterLyrics.Plugins.AILyricsTranslation" `
         "$t\BetterLyrics.WinUI3\plugins\BetterLyrics.Plugins.AILyricsTranslation" /E /NFL /NDL /NJH /NJS /NP

# 6. Register and launch. Registration and activation need Windows PowerShell 5.1 -
#    the pwsh Appx module is unavailable on this machine (0x80131539).
$ps = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
& $ps -NoProfile -Command "Add-AppxPackage -Register '$t\AppxManifest.xml'"
Start-Process 'shell:AppsFolder\37412.BetterLyrics.Test_rd1g0rsrrtxw8!App'
```

Pitfalls that cost real time:

- **Wait at least 12-15 seconds** before concluding the app did not start. It needs a few
  seconds to build its window; a 5 s check reports a false negative.
- Use `shell:AppsFolder\<AppFullName>!App` with a **backslash**; PowerShell 5.1 fails with
  "The system cannot find the file specified" for the forward-slash form.
- A build produced with `-p:WindowsPackageType=None` must **never** be dropped into a
  registered package: it defines `MICROSOFT_WINDOWSAPPSDK_BOOTSTRAP_AUTO_INITIALIZE`, so it
  boots the runtime itself while package identity already exists and dies with no WER entry.
  Use a plain `dotnet build ... -c Debug -p:Platform=x64`.
- Do not leave stray `*.xbf` files or a self-generated `BetterLyrics.WinUI3.pri` in the
  payload. The app then fails to start (`AppModel` creates the container, launches the
  process, then tears everything down). A clean copy of the official payload plus swapped
  Core/Sdk starts reliably.
- `CopyFromScreen` screenshotting fails with an invalid handle for these windows; use
  `PrintWindow(hwnd, hdc, 2)` on the handle found via `EnumWindows` +
  `GetWindowThreadProcessId` (see `tests/capture-host-window.ps1`).
- The plugin configuration lives in the `LocalState` of the **test** package. Re-deploying
  the plugin directory removes `config\`, and the plugin falls back to the defaults
  (`Google` with an empty URL, which is unreachable on an offline machine). Write the
  config again after every redeploy.

After the run, read the plugin log at:

```
%LOCALAPPDATA%\Packages\37412.BetterLyrics.Test_rd1g0rsrrtxw8\LocalState\plugins\BetterLyrics.Plugins.AILyricsTranslation\logs\plugin.log
```

A healthy real-endpoint run initializes with the configured mode/model, splits the lyrics
into 4 chunks of 12 lines, all four chat requests succeed, and
`Chunked translation done, 45 lines` closes it.

Cleanup (the deliverable must not leave system state behind):

```powershell
& $ps -NoProfile -Command "Remove-AppxPackage -Package 37412.BetterLyrics.Test_1.3.544.0_x64__rd1g0rsrrtxw8"
Remove-Item "$ws\_packaged" -Recurse -Force
```
