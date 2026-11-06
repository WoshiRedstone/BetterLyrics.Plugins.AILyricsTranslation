# Building without a BetterLyrics checkout

`BetterLyrics.DevTools` wants the host's dependency list so it can drop the assemblies the
host already ships. It reads exactly two file names from `$(BetterLyricsHostDir)`:

- `BetterLyrics.WinUI3.deps.json` (the desktop head), or
- `BetterLyrics.deps.json`

and if neither is present the build **fails**:

```
error : [DevTools Error] Host deps.json file not found.
```

Same for a path that does not exist at all:

```
error : [DevTools Error] Host solution directory is invalid or does not exist: <path>
```

Both are errors, not warnings — the "it only logs and carries on" idea is wrong, which is why
this file exists. Two ways out.

## 1. Point at a real checkout (best)

```powershell
git clone https://github.com/BetterLyrics/BetterLyrics.git D:\src\BetterLyrics
dotnet build -c Release -p:BetterLyricsHostDir=D:\src\BetterLyrics
```

Any folder that contains `BetterLyrics.WinUI3.deps.json` or `BetterLyrics.deps.json` works, so
a host build output directory (`...\bin\x64\Debug\net10.0-windows10.0.26100.0`) is fine too.
With a real host file the packer prints:

```
[DevTools] Successfully packed! Trimmed 3 redundant dependencies. Output: ...\Dist\<id>.v1.0.0.blp
```

and the package has **20 entries / ~37 KB**.

## 2. Use the shipped stub (no checkout needed)

`build/host-deps.stub.json` lists the three assemblies the plugin relies on and that the host
already provides (`BetterLyrics.Sdk`, `CHTCHSConv`, `NLanguageTag`). Copy it into a scratch
folder under the name the task looks for and build against that:

```powershell
$stub = Join-Path $env:TEMP 'bl-hoststub'
New-Item -ItemType Directory -Force -Path $stub | Out-Null
Copy-Item .\build\host-deps.stub.json (Join-Path $stub 'BetterLyrics.deps.json') -Force

dotnet build -c Release -p:BetterLyricsHostDir=$stub
dotnet build -c Release -p:BetterLyricsHostDir=$stub    # twice, see AGENTS.md 4.4
```

Result: the same `Trimmed 3 redundant dependencies` and the same 20-entry package
(about 37 KB; the exact byte count drifts by a few bytes between builds because the zip
records file timestamps), so the stub is a faithful stand-in.

If you use a stub that lists **no** libraries (a file containing just `{}`), the packer prints
`Trimmed 0 redundant dependencies` and produces a **23-entry / ~681 KB** package that still
works — it simply also carries `BetterLyrics.Sdk.dll`, `ChineseConverter.dll` and
`NLanguageTag.dll`, which the host will also load. Prefer the shipped stub; the smaller package
is what the release uses.

## 3. Two things that will bite you

- **The clone folder must keep the assembly name.** The packer derives the package name from
  the project directory name. Cloning into `bl-clone-check` produces `Dist\Plugin.v1.0.0.blp`
  (148 bytes, empty). Clone into `BetterLyrics.Plugins.AILyricsTranslation` and you get
  `Dist\BetterLyrics.Plugins.AILyricsTranslation.v1.0.0.blp`.
- **A JSON syntax error is only a warning.** If `BetterLyrics.deps.json` exists but does not
  parse, the task logs
  `[DevTools Warning] Host JSON parsing failed (…), falling back to full packaging.` and packs
  everything untrimmed. A malformed stub silently gives you the 23-entry package instead of
  failing, so check the `Trimmed N` number in the output.

## 4. Reproducibility

"Reproducible" here means something strict: **the same commit, built twice, in any two folders,
yields a byte-identical `BetterLyrics.Plugins.AILyricsTranslation.dll`**. Verified by cloning the
repository into two different temporary directories, building both, and comparing:

```
6CB6672F1C445D92AEDCD90FC84C2F7706D12ECEF34414BD7C9ED3870ED32472   46592 bytes
```

Four settings cooperate to get there, and each one was added because its absence was measured:

| Property | What happened without it |
| --- | --- |
| `IncludeSourceRevisionInInformationalVersion=false` | The SDK appends `+<commit sha>` to the informational version: the dll grew from 46592 to 47104 bytes and differed per commit |
| `PathMap=$(MSBuildProjectDirectory)=/_/` | The debug directory embeds the absolute `.pdb` path, so two folders produced `...\bl-repro2\...pdb` and `...\bl-repro3\...pdb` — 73 bytes apart |
| `ContinuousIntegrationBuild=true` | The compiler still picked a random MVID and folded the assembly path into the debug blob — 71 bytes apart across two clones |
| `.gitattributes` with `* text=auto eol=lf` | A `core.autocrlf=true` checkout mixed CRLF and LF, and the compiler folded that into the assembly |

If you already cloned the repository before `.gitattributes` existed, re-checkout once:
`git rm --cached -r . && git add -A && git checkout -- .` — afterwards `git ls-files --eol`
should report `w/lf` for every file.

The `.blp` is a different story: `BetterLyrics.DevTools` writes the zip entries with their
file timestamps, so the package hash changes between builds even though every entry inside it
is identical. Compare entries, not the archive, when you check a rebuild.
