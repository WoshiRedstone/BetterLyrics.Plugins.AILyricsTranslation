# BetterLyrics.Plugins.AILyricsTranslation

BetterLyrics 内置插件，把 [BetterLyrics-AI-Lyrics-Translation-Assistant](https://github.com/pliok7485/BetterLyrics-AI-Lyrics-Translation-Assistant) 的
Python 工具（Tkinter + Flask 的本地 HTTP 翻译服务）移植成宿主内的 `ILyricsTranslator` 实现。

移植后**不再需要** Python、Flask、本地 5000 端口服务，也不会写注册表或 PATH。

## 0. 安装

成品在 [Releases](https://github.com/WoshiRedstone/BetterLyrics.Plugins.AILyricsTranslation/releases)
里，下载 `BetterLyrics.Plugins.AILyricsTranslation.v1.0.0.blp` 即可。

`.blp` 就是改了扩展名的 zip，里面是 `<id>.dll`、`<id>.deps.json`、`<id>.runtimeconfig.json`
和 17 个 `Langs\*.json`，共 20 个条目。把它放进宿主的 `plugins` 目录，或按
[第 5 节](#5-内置插件部署) 做成内置插件，宿主启动时就会加载。

> 本插件依赖若干**宿主侧修复**（配置持久化、依赖解析、插件调用入口、开关门禁、内置插件部署）。
> 官方 1.3.544.0 未包含这些补丁，直接安装插件不会被调用——详见[第 6 节](#6-移植过程中修掉的宿主缺陷)。

---

## 1. 功能对照表（Python 原版 → 本插件）

| Python 原版 (`BetterLyrics.py`) | 本插件 |
| --- | --- |
| Flask `POST/GET /translate`、`/api/v1/translate`，请求体 `q`，响应 `{"translatedText": ...}` | 宿主直接调用 `ILyricsTranslator.GetTranslationAsync(text, targetLangTag)`，无需本地 HTTP 服务 |
| 模式 `Google` / `AI` / `Gemini` | `Config.Mode`，`Google` / `AI` / `Gemini` 三种（`AI` 模式若 URL 含 `deepl` 自动走 DeepL） |
| Google 兜底 `translate.googleapis.com/translate_a/single`，`tl=zh-TW`，timeout 15s | `TranslationBackends.GoogleAsync`，目标语言由宿主传入的 `LanguageTag` 决定（繁体输出会在末尾统一转简体），timeout 15s |
| OpenAI 兼容 `chat/completions`，`Bearer` key，固定 timeout 30s | `TranslationBackends.ChatAsync` + `BuildChatCompletionsUrl`（自动补 `/`、`v1/`、`chat/completions`），超时可配置，默认 60s |
| Gemini `v1/models/{model}:generateContent?key=`，timeout 15s | `TranslationBackends.GeminiAsync` + `BuildGeminiUrl`，超时可配置，默认 60s |
| DeepL `Authorization: DeepL-Auth-Key`，form `text` + `target_lang=ZH`，timeout 10s | `TranslationBackends.DeepLAsync`，timeout 10s（固定） |
| `_chunked_translate`：≤24 行整体翻译，否则 12 行一块、`ThreadPoolExecutor(max_workers=10)` | `TranslationEngine.TranslateChunkedAsync`：阈值 `Config.ChunkThreshold`（默认 24），块大小 12，`SemaphoreSlim(10)` 限流异步并发 |
| `align_lyrics`：译文行多截断、少补空行、原文空行保持空行 | `LyricsAligner.Align`，同样的规则 |
| `test_connection()` 按模式发 `Hello` 探测 | 设置面板里的「测试连接」按钮（`Config.TestConnection` → `ActionSettingDef`） |
| `log_debug` 写 `betterlyrics_debug.log`，超 5MB 保留最后 1000 行 | `PluginLogger` 写 `<插件目录>\logs\plugin.log`，同样的 5MB / 1000 行轮转 |
| 多套配置 `betterlyrics_profiles.json` | 宿主 `PluginConfigurator` 的 `config/config.json`（插件面板内直接改，无需重启） |
| 译文为台湾繁体（`tl=zh-TW`，提示词要求「嚴禁簡體」） | **`Config.ToSimplifiedChinese` 默认开启**，`ChineseConverterHelper.ToSimplified` 在行对齐之后把繁体统一转成简体 |

> 具体差异说明：原版工具的输出目标是「台灣繁體」，而 BetterLyrics 的目标语言由设置里的
> `SelectedTargetLanguageCode` 决定。本插件保留原版的「先按目标语言翻译、再按行对齐」流程，
> 并在最后一步按开关做繁→简转换，因此无论上游返回简体还是繁体，最终都能得到简体中文。

## 2. 配置项

| 设置项 (Config 属性) | 类型 | 默认值 | 说明 |
| --- | --- | --- | --- |
| `Mode` | 下拉 | `Google` | 翻译引擎：`Google` / `AI` / `Gemini` |
| `ApiUrl` | 文本 | 空 | `AI` 模式的 chat/completions 地址（含 `deepl` 则走 DeepL）；`Gemini` 模式留空用公网，填则指向自建网关；`Google` 模式留空用公网 gtx，填则指向自建/镜像端点（如 `http://127.0.0.1:8099`，会自动补 `/translate_a/single`） |
| `ApiKey` | 密码 | 空 | 对应引擎的密钥；为空时自动降级到 Google |
| `ModelName` | 文本 | `gpt-3.5-turbo` | Gemini 模式下留空则用 `gemini-1.5-flash` |
| `SystemPrompt` | 多行文本 | 内置繁体提示词 | 保持行数一致、一行一对一、只输出译文 |
| `Temperature` | 数字 | `0.1` | 0–2，步进 0.05 |
| `ToSimplifiedChinese` | 开关 | **开** | 输出简体中文 |
| `ChunkThreshold` | 数字 | `24` | 不超过该行数时一次请求，超过则 12 行分块并发 |
| `RequestTimeoutSeconds` | 数字 | `60` | 单次请求最长等待（5–600 秒）。**真实测试中一次歌词翻译约需 25 秒，原版固定 30 秒会超时并静默保留原文** |
| `TestConnection` | 按钮 | — | 按当前模式发一条探测（AI 模式为 `You are a connectivity probe. Reply with the single word: OK`），结果写入 `logs/plugin.log`；瞬时失败自动重试一次，空回复判为失败 |

界面文案随 `Langs/` 走：`zh-Hans`（简体）、`zh-Hant`（繁体）为人工译文，其余 15 种语言由
`BetterLyrics.DevTools` 依 `[Display]` 特性生成英文模板。

## 3. 构建

```powershell
cd BetterLyrics.Plugins.AILyricsTranslation
dotnet build -c Release      # 第一次：生成 Langs + 打包（此时 Langs 还不存在）
dotnet build -c Release      # 第二次：把 Langs 打进 .blp
```

需要 .NET SDK 10。工程内的 `nuget.config` 把 `globalPackagesFolder` 指到 **`..\_packages`**
（相对本文件），所以还原下来的 `BetterLyrics.Sdk` / `BetterLyrics.DevTools` / `CHTCHSConv`
等包留在仓库的兄弟目录，不写 `%USERPROFILE%\.nuget\packages`，也不碰注册表与 `PATH`。
删掉那一行就回到默认缓存。

`BetterLyrics.DevTools` 会在 `AfterTargets="Build"` 阶段：

1. 扫描带 `[Display]` 的 `Config` 属性，重新生成 `Langs\*.json`（17 种语言模板）；
2. 把 `Localization\*.json` 覆盖到 `$(OutDir)\Langs`（`OverlayPluginLocalization` 目标）并清掉
   运行期残留的 `logs\`，保证安装包干净；
3. 参照 `BetterLyricsHostDir` 下宿主的 `BetterLyrics.WinUI3.deps.json` 裁剪冗余依赖，输出
   `Dist\BetterLyrics.Plugins.AILyricsTranslation.v1.0.0.blp`。

`BetterLyricsHostDir` 默认是 `<仓库>\..\BetterLyrics-dev\BetterLyrics-dev`，指向宿主的
`BetterLyrics.WinUI3.deps.json`。**这个目录必须存在且真的含 deps.json，否则构建直接失败**
（`[DevTools Error] Host deps.json file not found.`，exit code 1，不产出任何包）。

没有宿主源码时用仓库自带的 stub：

```powershell
$stub = Join-Path $env:TEMP 'bl-hoststub'
New-Item -ItemType Directory -Force -Path $stub | Out-Null
Copy-Item .\build\host-deps.stub.json (Join-Path $stub 'BetterLyrics.deps.json') -Force
dotnet build -c Release -p:BetterLyricsHostDir=$stub
```

细节（`{}` stub 与三库 stub 的产物差异、目录名必须等于 `AssemblyName` 等）见
[`docs/build-without-host.md`](docs/build-without-host.md)。

**注意**：I18n 生成在 `AfterTargets="Build"`，第一次构建时 `Langs` 尚不存在，因此
**必须连续构建两次**才能得到含 `Langs\` 的完整 `.blp`。

构建是可复现的：同一个 commit 在任意两个目录各构建一次，得到的
`BetterLyrics.Plugins.AILyricsTranslation.dll` 逐字节相同
（`7AF2D3AE193FF32D91F702AEED68BEBA1A87C0612860CFE82F5D84FFFEB277C0`，46592 字节）。
这依赖 csproj 里的四个属性与根目录 `.gitattributes` 固定 LF，原理与验证方法见
[`AGENTS.md` 第 4.5 节](AGENTS.md)。`.blp` 因 zip 记录文件时间戳而每次哈希不同，
比对时请比条目内容。

更细的工程说明（文件地图、数据流、5 条已知坑、改动检查清单）见 [`AGENTS.md`](AGENTS.md)。

## 4. 验证

插件不依赖外网即可端到端验证（Google 公网端点在本机不可直连）。

四套 Harness 都在 `tests\` 下，首个参数是插件 dll 路径，默认由 `tests\Shared\TestPaths.cs`
解析（先看 `BL_DIST_DIR`，再从当前目录向上找含 `<id>.csproj` 的目录、取兄弟 `dist\`），
所以从仓库里直接跑不用传参：

```powershell
cd tests/PluginHarness    ; dotnet run -c Release
cd tests/InstallerHarness ; dotnet run -c Release
cd tests/RealConfigHarness; dotnet run -c Release
```

Harness 用 `HttpListener` 起本地 mock（OpenAI chat/completions、Gemini `:generateContent`、
DeepL `/deepl`），按宿主同样的 `PluginLoadContext` 加载插件，检查：插件身份与元数据、
`Langs` 打包与键名、设置面板 schema（Choice/Password/Number/Action）、四种后端路由与请求
形态（`Bearer` / `DeepL-Auth-Key` / `?key=` / `target_lang=ZH`）、行数对齐（补齐/截断/空行）、
分块并发（30 行 → 3 请求、24 行 → 1 请求）、繁→简转换、HTTP 500 不抛异常、空输入、连接测试
与日志写入。

内置部署机制的验证在 `tests\InstallerHarness`：首次部署、幂等、新版本覆盖、
本地更新版本保留、卸载后不再回填，以及**把真实交付的 `.blp` 走一遍
宿主自己的安装路径**（解压 → `PluginMetadataHelper.IdentifyPluginId` → 部署成
`<id>/<id>.dll` → 校验语言包随行、包内无运行期日志）。

宿主真实 `PluginConfigurator` 的持久化验证在 `tests\RealConfigHarness`：
`saved → 重开 configurator → 读回`，覆盖交付 dll 里的全部 8 个设置项（string / bool / double）。

要在**真实宿主 GUI**里验证四种后端，用 `tests\mock-backends.ps1`：一个端口
（默认 8099）同时伪造 gtx `/translate_a/single`、`/v1/chat/completions`、Gemini
`:generateContent`、DeepL 四种响应，每次请求写一行日志：

```powershell
& "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile `
  -ExecutionPolicy Bypass -File .\tests\mock-backends.ps1 `
  -Port 8099 -Log ..\_mock\requests.log
```

然后把宿主插件设置里的 `ApiUrl` 指向 `http://127.0.0.1:8099`（AI 模式用 `/v1`、DeepL 用
`/deepl`、Google/Gemini 用根地址），重启宿主即可。踩坑：

- 脚本里不能出现中文字面量，PowerShell 5.1 会按 ANSI 读文件并报
  `MissingEndParenthesisInMethodCall`；繁体样例要用 `[string][char]0x8AAC` 这类写法构造。
- Google 响应必须是**裸数组** `[[["译文","原文"],...]]`。用 `ConvertTo-Json` 把数组
  pipe 进去会被包成 `{"value":[...],"Count":n}`，插件按 `[0]` 取 parts 拿不到，会静默返回原文。
- 每段译文末尾要带 `\n`（真实 gtx 把换行放在段内），否则整首坍成一行。

截图用 `tests\capture-host-window.ps1`（必须用 Windows PowerShell 5.1 跑，
pwsh 下 `Add-Type -ReferencedAssemblies System.Drawing` 会报 `CS1069: Bitmap 已转发`）：

```powershell
& "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile `
  -ExecutionPolicy Bypass -File .\tests\capture-host-window.ps1 `
  -OutPath ..\screenshot-lyrics.png
```

它按标题片段（默认「标准模式」）选窗口，用 `PrintWindow(hwnd, hdc, 2)` 抓图——
`CopyFromScreen` 在这台机器上会报「句柄无效」，且会抓到被遮挡的内容。

### 真实端点测试（`tests\LiveHarness`）

`PluginHarness` 用的是本地 mock；要验证真实模型链路（尤其是**延迟与超时**），用 LiveHarness
直连真实 API。密钥只从环境变量读，不会写进任何文件，输出里打码：

```powershell
$env:BL_LIVE_API_KEY = '<你的 key>'
$env:BL_LIVE_API_URL = 'https://api.ginka.cloud/v1'
$env:BL_LIVE_MODEL   = 'glm-5.3-flash'
cd tests\LiveHarness
dotnet run -c Release
```

覆盖：7 行英文→简体、繁体输入→简体、36 行分块（12×3）、空行保持、标点/emoji 不被 JSON 转义、
空输入、无效 key 不抛异常、真实端点连接测试、日志不含密钥。整轮约 3.5 分钟。
**这正是暴露「30 秒固定超时」缺陷的地方**：慢模型一次翻译约 25 秒，偶尔会越过 30 秒边界，
插件会静默返回英文原文——所以默认超时改成了 60 秒且可配置。

也正是在这里暴露了「连接测试假通过」：`glm-5.3-flash` 是推理模型，`max_tokens` 给小了就把预算
全花在隐藏 token 上，返回 HTTP 200 而 `content` 为空，界面上显示成 `AI OK: `（后面空白）。
现在空回复直接判失败（`AI returned an empty reply (finish_reason=…)`），可重试的瞬时失败
（超时 / `HttpRequestException` / `SSL connection`）自动再试一次，真实拒绝（401/404）不重试，
探测用的 prompt 也换成专用的 `You are a connectivity probe. Reply with the single word: OK`。
该端点在实测中确实抖动：同一请求在 3 s、23 s、40 s、60 s 超时之间随机跳。

> Harness 会把插件日志写进它加载的那个产物目录（`logs\plugin.log`），跑完记得清掉，
> 否则会混进下一次打包。

### 真机验证（真实宿主 GUI）

上面的 Harness 都是在宿主进程之外加载插件 dll。要证明「插件在真实 BetterLyrics 里工作」，
本机做的是：把已安装的官方 BetterLyrics 1.3.544.0 复制一份，改 `Identity Name` 后
`Add-AppxPackage -Register`（松散注册，无需 VS / 证书 / 管理员），再把**我们编译的
`BetterLyrics.Core.dll` + `BetterLyrics.Sdk.dll`** 与 `plugins\BetterLyrics.Plugins.AILyricsTranslation\`
覆盖进 payload。完整可复现步骤见 [`docs/real-host-verification.md`](docs/real-host-verification.md)。

- UI 程序集（`BetterLyrics.WinUI3.dll`）与 `resources.pri` 必须保持官方版本。换成我们自己编的
  UI 程序集后，`SettingsWindow` 初始化会抛
  `XamlParseException: Cannot create instance of type 'BetterLyrics.WinUI3.Controls.WindowSettingsControl'`，
  会话窗仍能开但设置页打不开。
- 用 `-p:WindowsPackageType=None` 构建的产物**不能**放进已注册的包里——它会注入
  `MICROSOFT_WINDOWSAPPSDK_BOOTSTRAP_AUTO_INITIALIZE`，在有包身份时抢先自举并静默崩溃
  （AppModel 日志 201→210/211→217，无 WER）。覆盖进去的二进制必须来自默认（打包形态）构建。
- 激活用 `Start-Process 'shell:AppsFolder\37412.BetterLyrics.Test_rd1g0rsrrtxw8!App'`，
  **判定存活要等 ≥12 s**，5 s 时会误判成没起来。

实测结果：宿主日志 `Application services initialized. Media folders: 0; plugins: 1.`；插件日志显示
`Mode=AI ... RequestTimeout=180s` → `Splitting into 4 chunks of 12 lines` → 4 次
`Chat success` → `Chunked translation done, 45 lines`；截图可见宿主歌词窗口里日文原词下方是
本插件输出的**简体**中文译文。此时 `settings.json` 里 `IsLibreTranslateEnabled` 仍是 `false`，
翻译照常发生。

## 5. 内置插件部署

宿主新增 `BetterLyrics.Core\Helpers\BundledPluginInstaller`，在 `PluginService.LoadPluginsAsync()`
开头把 `AppContext.BaseDirectory\plugins\<PluginId>\<PluginId>.dll` 同步到用户的
`plugins` 目录。`BetterLyrics.WinUI3.csproj` 把 `BundledPluginsDir`（默认指向本仓库的
`dist\`）下的展开目录以 `Link=plugins\...` 复制到输出目录，因此编译出的宿主自带本插件，
用户无需手动安装。

- 用户在插件管理里卸载后会在 `plugins` 目录留下 `.removed-<PluginId>` 标记，不会在下次启动时被回填；
- 已安装副本比内置副本新时保留用户版本；
- 想换内置插件来源，构建时传 `-p:BundledPluginsDir=<dir>`（目录形态仍为 `<PluginId>\<PluginId>.dll`）。

## 6. 移植过程中修掉的宿主缺陷

插件要真正跑起来，宿主有几处必须一起修（均已改在本机 BetterLyrics 源码里并编译通过；
补丁要点见本节，源码不在本仓库，因为它属于 GPL-3.0 的 BetterLyrics 项目）：

1. **`PluginConfigurator.cs` — 插件设置重启后丢失。** 配置以 `Dictionary<string, object>`
   序列化，读回时每个值都是 `JsonElement`；`PluginConfigBase.Get<T>` 用 `Convert.ChangeType`，
   而 `JsonElement` 不实现 `IConvertible`，于是每次都静默落回默认值。现在读回时按
   `ValueKind` 归一成 `string`/`long`/`double`/`bool`。
2. **`PluginLoadContext.cs` — 裁掉的依赖又去插件目录找。** `.blp` 故意不打包宿主已有的程序集，
   但 resolver 仍会返回插件目录内的路径，导致 `FileNotFoundException`。现在要求文件真实存在，
   否则回落到默认 ALC。
3. **`TranslateService.cs` — 翻译插件没有调用入口。** 原第 67-83 行调用 `ILyricsTranslator`
   的代码整段被注释。现在改成"插件优先、LibreTranslate 兜底"，插件异常不影响原有流程。
4. **`LyricsContentParser.cs` — 插件被默认开关挡住。** `GenerateTranslationLyricsDataAsync`
   原先在 `IsLibreTranslateEnabled`（默认 `false`）为假时直接 `return`，插件永远不会被调用；
   且译文 Provider 恒为 `LibreTranslate`。现在只要有翻译插件就继续，并把 Provider 记成插件的
   哈希值（`>= 1000`），界面上显示插件名。
5. **内置插件部署机制（新增）。** 宿主原本只认 `%LOCALAPPDATA%\...\plugins`，没有"随应用分发"
   的概念。新增 `BundledPluginInstaller`，在 `LoadPluginsAsync()` 开头把
   `AppContext.BaseDirectory\plugins` 同步过去（见上一节）。
6. **`BetterLyrics.WinUI3\Controls\PluginItemControl.xaml.cs` — API Key 明文显示。**
   `TextSettingDef` 上有 SDK 提供的 `IsPassword`，但界面完全忽略它，把所有字符串设置都渲染成
   `TextBox`；真机截图里能直接看到完整的 API Key。现在 `TextSettingDef { IsPassword: true }`
   渲染为 `PasswordBox`（`PasswordRevealMode.Peek`，保留可主动查看的按钮），其余字符串仍是
   `TextBox`。

## 7. 已知限制

- **UI 层的 PasswordBox 修复只做到「编译通过 + 字节确认」，没有真机截图。** 本机做真机验证
  时必须让 UI 程序集保持官方版本（换成我们自己编的 `BetterLyrics.WinUI3.dll` 后
  `SettingsWindow` 会抛 `XamlParseException`）；而想在真机上看到这一改动，就必须让自定义 UI
  程序集跑起来，两者互斥。尝试过的替代路径都失败了：
  - 自建 payload（构建输出平铺 + 自生成 `resources.pri` + 手写 `AppxManifest.xml`）→
    `Add-AppxPackage -Register` 成功，但 `shell:AppsFolder\...!App` 起不来（AppModel 日志
    39→69→41→70→210/211→217，容器建了又销毁），直接跑 exe 得到 `0xC0000409`；
  - 用 `makepri new /in "37412.BetterLyrics"` 把 pri 的 ResourceMap 名对齐官方、再叠加到
    官方 payload 上 → 同样起不来。
  所以这一处改动能给出的证据只有：编译 0 错误，且新 `BetterLyrics.WinUI3.dll`（2400768 字节，
  2026-11-01 11:47:51）里存在 `PasswordRevealMode`@1621965 / `IsPassword`@1613421 /
  `PasswordChanged`@1557758，而官方 dll（2115584 字节）只有 `PasswordBox`@1564114、没有前两者。
  **扫描要注意**：.NET 元数据字符串堆是 UTF-8，用 `[Text.Encoding]::Unicode` 去找会全部返回 -1
  并误判「改动没编进去」。
- 其余 5 处宿主修复（配置持久化、依赖解析、调用入口、开关门禁、内置插件部署）都在真实宿主里
  实际生效并有日志证据。

## 8. 致谢与来源

- 翻译服务的最初实现来自
  [BetterLyrics-AI-Lyrics-Translation-Assistant](https://github.com/pliok7485/BetterLyrics-AI-Lyrics-Translation-Assistant)
  （MIT，`Copyright (c) 2026 pliok7485`）。本插件是它的进程内移植版，保留了行数对齐、
  分块并发、繁体提示词、降级 Google、滚动日志等全部行为语义。
- 插件宿主与 SDK 来自 [BetterLyrics](https://github.com/BetterLyrics/BetterLyrics)。

---

## 9. 许可证

本仓库的源码以 **MIT** 协议发布，完整文本见 [`LICENSE`](LICENSE)。

```
MIT License

Copyright (c) 2026 BetterLyrics Community

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

> **关于编译产物**：源码是 MIT，但本插件引用的 `BetterLyrics.Sdk`（1.0.3）是
> **GPL-3.0-or-later**，因此**编译出来的 `.blp` / dll 作为一个整体按 GPL-3.0 分发**，
> 而不是 MIT。`LICENSE` 末尾列出了全部第三方组件与出处，重新分发前请读一遍。

---

## 10. 声明

**本仓库的代码、测试、构建脚本与文档均由 AI（大语言模型）生成**，经人工审阅、编译与
端到端验证后发布。所有功能都经过四套自动化测试（`PluginHarness` 78 项 /
`InstallerHarness` 21 项 / `RealConfigHarness` 16 项 / `LiveHarness` 21 项，全部通过），
并在真实的 BetterLyrics 宿主 GUI 里用真实 API 端点跑通了 AI / Gemini / DeepL / Google
四种后端，另有繁体转简体的截图证据。

即便如此，AI 生成的代码仍可能存在未被测试覆盖的问题。使用前请自行评估；发现缺陷欢迎提
Issue 或 PR。

