# AGENTS.md — 给智能体的工程说明

本文件写给**要修改、编译、验证或移植本插件的人或智能体**。它刻意不重复 `README.md`
里的功能对照表和设置项说明，只讲三件事：

1. 代码是怎么组织的、每个文件负责什么、数据怎么流动（第 1–3 节）；
2. 从零把它编译出 `.blp` 的完整步骤与已知坑（第 4 节）；
3. 怎么证明改动是对的——四套 Harness、mock 后端、真实宿主（第 5–7 节）。

> 改动前请先读第 4.4 节「已知坑」，那里的三条（两次构建、`Langs` overlay、代理绕过）
> 是踩过的真实故障，不读会重复踩。

---

## 1. 这是什么

BetterLyrics 宿主里的**内置歌词翻译插件**，把
[BetterLyrics-AI-Lyrics-Translation-Assistant](https://github.com/pliok7485/BetterLyrics-AI-Lyrics-Translation-Assistant)
（`BetterLyrics.py`，Tkinter + Flask，监听 `127.0.0.1:5000/translate`）的翻译能力，改写成宿主内的
`ILyricsTranslator` 实现。

原版是"进程外 + HTTP"：宿主把歌词 POST 给本地 Flask，Flask 调上游 API，把译文回传。
本插件是"进程内 + 接口"：宿主在 `LyricsContentParser` 里直接把整段歌词交给
`ILyricsTranslator.GetTranslationAsync`，插件返回逐行对齐的简体中文。**不再需要 Python、
Flask、5000 端口，也不写注册表或 PATH。**

保留的语义（这些是移植时被刻意保真的部分，不要随手改）：

| 原版行为 | 本插件落点 |
| --- | --- |
| 行数必须严格一致 | `LyricsAligner.Align` |
| ≤24 行整体翻、超过则 12 行一块、10 并发 | `TranslationEngine.TranslateChunkedAsync` |
| 无 key / 无 url 时降级 Google | `TranslationEngine.TranslateRawAsync` |
| 台湾繁体 → 简体 | `ChineseConverterHelper.ToSimplified`（在 `Finish()` 末尾） |
| 5MB / 1000 行滚动的调试日志 | `PluginLogger` |

---

## 2. 文件地图

```
BetterLyrics.Plugins.AILyricsTranslation/
├── Plugin.cs                 IPlugin + ILyricsTranslator 实现，设置面板 schema，连接测试入口
├── Config.cs                 PluginConfigBase 派生，10 个设置项（[Display] 是 Langs 的来源）
├── TranslationEngine.cs      缓存 / 分块 / 并发 / 模式分派 / 行对齐 / 繁→简 的编排层
├── TranslationBackends.cs    HTTP 细节：四个后端各自的请求构造与响应解析
├── SystemProxy.cs            IWebProxy 包装，让 loopback 绕过系统代理（见 4.4）
├── LyricsAligner.cs          行数对齐（纯函数）
├── ChineseConverterHelper.cs CHTCHSConv 包装，繁↔简
├── PluginLogger.cs           线程安全写文件 + 5MB/1000 行轮转
├── Langs/*.json              17 种语言的界面文案（15 种由 DevTools 生成，zh-Hans/zh-Hant 人工翻译）
├── Localization/*.json       人工译文源文件，构建时覆盖到 Langs（见 4.4）
├── docs/real-host-verification.md  真机（真实宿主 GUI）验证的完整可复现步骤
└── tests/
    ├── Shared/TestPaths.cs   定位 dist 目录，避免写死机器路径
    ├── PluginHarness/        78 项：接口/设置/Langs/四后端路由/对齐/分块/错误/日志
    ├── InstallerHarness/     21 项：内置插件部署（BundledPluginInstaller）与 .blp 解包
    ├── RealConfigHarness/    16 项：宿主真 PluginConfigurator 的持久化
    ├── LiveHarness/          真实 API 端点（需要 BL_LIVE_* 环境变量）
    ├── mock-backends.ps1     一个端口同时伪造 gtx / chat / Gemini / DeepL
    └── capture-host-window.ps1  截宿主窗口（PrintWindow）
```

依赖方向是单向的：
`Plugin` → `TranslationEngine` → `TranslationBackends` / `LyricsAligner` / `ChineseConverterHelper`，
`PluginLogger` 被所有层使用。**不要让 `TranslationBackends` 反向依赖 `Plugin`**——
Harness 会单独 new 它。

---

## 3. 一次翻译的完整数据流

宿主调用 `ILyricsTranslator.GetTranslationAsync(text, targetLangTag)`：

```
Plugin.GetTranslationAsync
  └─ 空白文本直接返回原样（原版 Flask 也是这个语义）
  └─ TranslationEngine.TranslateAsync(text, Config, lang, token)
       1) 查缓存：key = Mode|ApiUrl|ModelName|text（\u0001 连接），命中直接返回
       2) TranslateRawAsync 按 Mode/ApiUrl 分派：
            url 含 "deepl"        → DeepLAsync        （Authorization: DeepL-Auth-Key，form target_lang=ZH）
            Mode=Gemini           → GeminiAsync       （?key=，v1/models/{model}:generateContent）
            Mode=Google / url 空  → GoogleAsync       （gtx translate_a/single，裸数组解析）
            其余                  → ChatAsync         （OpenAI 兼容 chat/completions）
       3) 行数 > ChunkThreshold(默认24) 时走 TranslateChunkedAsync：
            12 行一块，SemaphoreSlim(10) 限流；单块失败只把该块退回原文
       4) Finish()：LyricsAligner.Align（补齐/截断/空行）→ ChineseConverterHelper.ToSimplified
       5) 任何异常都被 catch，记 Error 并返回原文——**绝不把异常抛给宿主**
```

几个容易踩的设计点：

- **`HttpClient.Timeout = InfiniteTimeSpan`**。每个请求的超时由
  `TranslationBackends.SendAsync` 用 linked CTS 单独控制（Google 15s、DeepL 10s、
  chat/Gemini 用 `Config.RequestTimeoutSeconds`）。如果外面再套一个 HttpClient 超时，
  它会更早触发，用户改设置就不生效了。`SendAsync` 把自身超时翻译成
  `TimeoutException("The request did not finish within {n} s. Increase \"Request Timeout (seconds)\" if the model is slow.")`。
- **目标语言解析**：`ResolveTargetLanguage` 把一切 `zh*` 映射成 `zh-CN`，
  因为繁体在最后会被统一转简体，没必要让上游再选一次。
- **缓存容量 256**，key 含 Mode/Url/Model——改设置会自然失效，删掉任意一项都会命中旧译文。
- **`Config` 的属性名就是持久化 key**。改名等于让老配置失效，改名要同步改
  `Plugin.BuildSettingDefDict()` 的 `switch` 与 `Langs/*.json` 的 `Settings.<Name>.Label`。

---

## 4. 编译

### 4.1 前置

- .NET SDK 10（`dotnet --list-sdks` 应含 `10.x`；本插件 `TargetFramework=net10.0`）
- 能访问 nuget.org（首次还原 `BetterLyrics.Sdk` / `BetterLyrics.DevTools` / `CHTCHSConv`）
- **必须**准备一个含 `BetterLyrics.deps.json` 的目录供依赖裁剪（见 4.2，仓库自带 stub）

### 4.2 目录布局与 BetterLyricsHostDir（重要）

推荐的布局与本仓库的 `nuget.config` / `csproj` 期望值：

```
<workspace>/
├── BetterLyrics.Plugins.AILyricsTranslation/   ← 本仓库（目录名不能改，见下）
└── BetterLyrics-dev/
    └── BetterLyrics-dev/                       ← BetterLyrics 源码
```

`BetterLyrics.DevTools` 要读宿主的 `BetterLyrics.WinUI3.deps.json`（或 `BetterLyrics.deps.json`）
才能裁掉宿主已带的程序集。它由 csproj 里的 `BetterLyricsHostDir` 指定，默认指向
`<仓库>\..\BetterLyrics-dev\BetterLyrics-dev`。

**这个目录必须存在且真的含 deps.json —— 否则构建直接失败，不是降级。** 实测两种报错都是
`error :`（会以 exit code 1 结束，产物一个都不生成）：

```
error : [DevTools Error] Host solution directory is invalid or does not exist: <path>
error : [DevTools Error] Host deps.json file not found.
```

没有宿主源码时，用仓库自带的 stub（详见 [`docs/build-without-host.md`](docs/build-without-host.md)）：

```powershell
$stub = Join-Path $env:TEMP 'bl-hoststub'
New-Item -ItemType Directory -Force -Path $stub | Out-Null
Copy-Item .\build\host-deps.stub.json (Join-Path $stub 'BetterLyrics.deps.json') -Force
dotnet build -c Release -p:BetterLyricsHostDir=$stub
```

stub 里列了 `BetterLyrics.Sdk` / `CHTCHSConv` / `NLanguageTag` 三个库，裁剪结果与真实宿主一致
（`Trimmed 3 redundant dependencies`，20 条目 ≈ 37 KB）。若改用只含 `{}` 的 stub，会变成
`Trimmed 0`、23 条目 ≈ 681 KB，功能相同但把宿主已带的三个 dll 也打进去了。

两个连带坑：

- **仓库目录名必须保持 `BetterLyrics.Plugins.AILyricsTranslation`。** DevTools 用 `ProjectDir`
  的目录名当包名，clone 成 `bl-clone-check` 会产出 `Dist\Plugin.v1.0.0.blp`（148 字节空壳）。
- **deps.json 语法错误只是 warning**：`Host JSON parsing failed (…), falling back to full packaging.`
  然后按未裁剪打包。看到的是 23 条目而不是报错，容易误以为裁剪生效——请核对输出里的
  `Trimmed N` 数字。

`nuget.config` 把 `globalPackagesFolder` 指到 `..\_packages`（相对本文件）。这是刻意的：
包不进 `%USERPROFILE%\.nuget\packages`，构建不在系统上留痕。删掉那行就回到默认缓存。

### 4.3 构建命令

```powershell
cd BetterLyrics.Plugins.AILyricsTranslation

dotnet build -c Release          # 第一次：生成 Langs + 打包（此时 Langs 还不存在）
dotnet build -c Release          # 第二次：把 Langs 打进 .blp
```

产物：

```
bin/Release/net10.0/BetterLyrics.Plugins.AILyricsTranslation.dll   插件本体
bin/Release/net10.0/Langs/*.json                                    17 种语言
Dist/BetterLyrics.Plugins.AILyricsTranslation.v1.0.0.blp            安装包（zip）
```

`.blp` 就是改了扩展名的 zip，可以直接解开核对：

```powershell
Expand-Archive Dist\BetterLyrics.Plugins.AILyricsTranslation.v1.0.0.blp -DestinationPath x
Get-ChildItem x -Recurse
```

正常应有 20 个条目：`<id>.dll`、`<id>.deps.json`、`<id>.runtimeconfig.json`、`Langs\*.json` ×17。
**不应该**有 `logs\`、`Localization\`、`ChineseConverter.dll`、`NLanguageTag.dll`、`BetterLyrics.Sdk.dll`
（后三者由宿主提供，被裁剪）。

### 4.4 已知坑（都真实踩过）

**① 必须连续构建两次。** `Langs` 由 DevTools 在 `AfterTargets="Build"` 生成，第一次构建时它
还不存在，`.blp` 里会缺 17 个语言文件。看到 `Successfully generated 17 language templates`
之后的**下一次**构建才是完整的。

**② `Langs/*.json` 每次构建都会被重新生成，会覆盖人工译文。** 所以人工译文放在
`Localization/`，由 csproj 里的 `OverlayPluginLocalization` 目标在打包前覆盖过去，
并顺手删掉 `$(OutDir)logs\` 与 `$(OutDir)Localization\`。增删设置项后要同步更新
`Localization/zh-Hans.json` 与 `zh-Hant.json`，否则中文界面会回落到英文。

**③ 系统代理会吃掉 127.0.0.1 的请求。** 若 WinINET 配了代理且 override 列表里没有
`<local>`（某些加速器会这样写），HTTP 请求打到 `http://127.0.0.1:port` 会被代理接走并返回
**404**，表现为"本地镜像怎么都不生效/译文全是原文"。`SystemProxy.cs` 里的
`LoopbackBypassProxy` 在 `HttpClientHandler` 上把 loopback 目标直接放行，其余目标仍走系统代理。
改网络层时**不要**去掉它，否则本机所有 mock 测试都会静默失败。

**④ tests 不能落在插件的编译 glob 里。** `tests/` 在仓库内，而 `.csproj` 位于仓库根，
Microsoft.NET.Sdk 的默认 glob 会把 `tests/**/*.cs`（含各 Harness 自己生成的
`obj/**/AssemblyInfo.cs`）一起编进插件，报 `CS0101`/`CS0579`/`CS8802`。csproj 顶部有：

```xml
<Compile Remove="tests\**" />
<None Remove="tests\**" />
<EmbeddedResource Remove="tests\**" />
<Content Remove="tests\**" />
<Page Remove="tests\**" />
```

新增子项目时记得同样加排除。

**⑤ `nuget.config` 的 XML 注释里不能出现 `--`。** 写 `--packages` 会让 NuGet 报
`NuGet.Config 不是有效的 XML。… An XML comment cannot contain '--'`，四个 Harness 全挂。

### 4.5 构建可复现性

「可复现」在这里有严格定义：**同一个 commit，在任意目录、任意机器上两次干净构建，产出的 dll
逐字节相同**。做到这一点需要四处配合，缺一处就会悄悄退回不确定：

```xml
<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>
<Deterministic>true</Deterministic>
<PathMap Condition="'$(PathMap)' == ''">$(MSBuildProjectDirectory)=/_/</PathMap>
<ContinuousIntegrationBuild Condition="'$(ContinuousIntegrationBuild)' == ''">true</ContinuousIntegrationBuild>
```

每一项都是实测撞出来的，不是一个都不能少的理论洁癖：

| 属性 | 不做会怎样（实测） |
| --- | --- |
| `IncludeSourceRevisionInInformationalVersion=false` | SDK 把 `+<commit sha>` 拼进 `InformationalVersion`，dll 从 46592 变 47104 字节，且不同 commit 大小都不同 |
| `PathMap` | 调试目录里嵌了 `.pdb` 的绝对路径，两个目录各建一次得到 `C:\...\bl-repro2\...pdb` 与 `C:\...\bl-repro3\...pdb`，差 73 字节 |
| `ContinuousIntegrationBuild=true` | 编译器仍会挑随机的 MVID 并把程序集文件路径折进 debug blob，两个 clone 各建一次差 71 字节 |

另外仓库根有 `.gitattributes` 固定 `* text=auto eol=lf`：`core.autocrlf=true` 的检出会把部分文件
变成 CRLF，编译器把这些差异也折进程序集。**已经 clone 但工作树是 CRLF 的，需要重新检出一次**
（`git rm --cached -r . && git add -A && git checkout -- .` 之后 `git ls-files --eol` 应该全是 `w/lf`）。

验证方式（两个不同目录各建一次，对比 dll 的 SHA256）：

```powershell
$dll = 'bin\Release\net10.0\BetterLyrics.Plugins.AILyricsTranslation.dll'
(Get-FileHash $dll).Hash   # 应为 091D52207B25FEFE7917F2C8D350BDD273E118DEB66A562408DCF37E98F2243E
```

`.blp` 仍然每次哈希都变——DevTools 按文件时间戳写 zip 条目。**核对重建是否一致时要比条目内容，
不要比归档哈希**。

---

## 5. 验证（不需要外网）

四套 Harness 都以「插件 dll 路径」为首个参数，默认值由 `tests/Shared/TestPaths.cs` 解析：
先看环境变量 `BL_DIST_DIR`，再从当前目录向上找含 `<id>.csproj` 的目录并取兄弟 `dist\`，
最后才抛 `DirectoryNotFoundException`。所以从仓库里直接跑不用传参：

```powershell
cd tests/PluginHarness   ; dotnet run -c Release
cd tests/InstallerHarness; dotnet run -c Release
cd tests/RealConfigHarness; dotnet run -c Release
```

| Harness | 覆盖 | 期望 |
| --- | --- | --- |
| `PluginHarness` | 身份/元数据、`Langs` 打包与键、设置 schema（Choice/Password/Number/Action）、四后端路由与请求形态（`Bearer`/`DeepL-Auth-Key`/`?key=`/`target_lang=ZH`）、行对齐（补齐/截断/空行）、分块（30 行→3 请求、24 行→1 请求）、繁→简、HTTP 500 不抛、空输入、连接测试、日志 | `ALL CHECKS PASSED`（78 项） |
| `InstallerHarness` | `BundledPluginInstaller.Synchronize`：首装、幂等、新版本覆盖、本地更新版保留、卸载后不回填；把**真实交付的 `.blp`** 走一遍宿主安装路径（解压 → `PluginMetadataHelper.IdentifyPluginId` → 部署 → 校验语言包随行、包内无运行期日志） | `ALL CHECKS PASSED`（21 项） |
| `RealConfigHarness` | 宿主真 `PluginConfigurator`：`saved → 重开 → 读回`，覆盖全部 8 个可持久化设置（string/bool/double） | `ALL CHECKS PASSED`（16 项） |
| `LiveHarness` | 真实端点：7 行英文→简体、繁体→简体、36 行分块、空行保持、标点/emoji 不被转义、空输入、无效 key 不抛、连接测试、**日志不含密钥** | `ALL LIVE CHECKS PASSED`（约 3.5 分钟） |

`LiveHarness` 的配置只从环境变量读，不落盘：

```powershell
$env:BL_LIVE_API_KEY = '<key>'
$env:BL_LIVE_API_URL = 'https://api.ginka.cloud/v1'
$env:BL_LIVE_MODEL   = 'glm-5.3-flash'
$env:BL_LIVE_PROMPT  = '把歌词逐行翻译成中文简体，保持行数一致。'
```

> Harness 会把 `logs\plugin.log` 写进它加载的那个产物目录。跑完删掉，否则下次打包会带进去。

---

## 6. 在真实宿主里验证

Harness 是在宿主进程**之外**加载 dll。要证明"插件在真的 BetterLyrics 里工作"，
完整步骤见 [`docs/real-host-verification.md`](docs/real-host-verification.md)：把已安装的官方包
复制成 `_packaged\BetterLyrics.Test`，改 `Identity Name` 后 `Add-AppxPackage -Register`
（松散注册，免 VS/证书/管理员），覆盖**我们编译的 `BetterLyrics.Core.dll` + `BetterLyrics.Sdk.dll`**
与插件目录，再 `Start-Process 'shell:AppsFolder\<FullName>!App'`。

三条非踩不可的：

- **UI 程序集必须保持官方版本。** 换成我们自己编的 `BetterLyrics.WinUI3.dll` 后
  `SettingsWindow` 会抛 `XamlParseException: Cannot create instance of type
  'BetterLyrics.WinUI3.Controls.WindowSettingsControl'`（会话窗还能开，设置页打不开）。
- **`-p:WindowsPackageType=None` 的产物不能放进已注册的包里。** 它会注入
  `MICROSOFT_WINDOWSAPPSDK_BOOTSTRAP_AUTO_INITIALIZE`，在有包身份时抢先自举并静默崩溃。
- **判定进程存活要等 ≥12 s。** 5 秒时会被误判成没有启动。

顺便：本仓库依赖的宿主侧修复（配置持久化、依赖解析、插件调用入口、开关门禁、内置插件部署、
PasswordBox）都记录在 `README.md` 第 6 节。**如果换成未打补丁的宿主，插件不会工作**——
这些不是可选项，是让它跑起来的前提。

---

## 7. 改动检查清单

改代码前后各过一遍：

- [ ] 新增/改名设置项 → 同步 `Config`（`[Display]`）、`Plugin.BuildSettingDefDict()` 的 `switch`、
      `Localization/zh-Hans.json` + `zh-Hant.json`，并重跑 `PluginHarness`（它会断言
      "每个设置项都有本地化 header"和"zh-Hans 覆盖全部英文键"）
- [ ] 改网络层 → 保留 `LoopbackBypassProxy`，并跑 `PluginHarness`（它用 127.0.0.1 的 mock）
- [ ] 改分块/对齐 → 跑 `PluginHarness` 的 chunking 与 alignment 段
- [ ] 改 `Config` 持久化相关 → 跑 `RealConfigHarness`
- [ ] 动了 `Install/Ui` 相关或 `.blp` 内容 → 跑 `InstallerHarness`
- [ ] 动了提示词/模型调用 → 跑 `LiveHarness`（真实端点会抖动，见下）
- [ ] 交付前**连续构建两次**，核对 `.blp` 是 20 个条目、无 `logs/`
- [ ] 全仓扫描一次密钥：`Select-String -Path . -Pattern 'sk-' -Recurse` 应为空

### 关于真实端点的抖动

`LiveHarness` 用的公共网关不稳定：同一个 probe 请求实测在 3 s / 23 s / 40 s / 60 s 超时之间随机跳，
且推理模型（`glm-5.3-flash`）在 `max_tokens` 给小了时会把预算全花在隐藏 token 上，
返回 HTTP 200 但 `content` 为空。因此：

- 连接测试对**瞬时失败**（超时 / `HttpRequestException` / `SSL connection`）自动重试一次；
- **空回复判为失败**（`AI returned an empty reply (finish_reason=…)`），不能算通过——
  早期版本就因为 `AI OK: ` 后面是空的却判 PASS 而漏掉了这个 bug；
- 探测 prompt 用专用的 `You are a connectivity probe. Reply with the single word: OK`，
  不要用用户的歌词提示词去探测。

---

## 8. 许可证

本仓库源码是 **MIT**（见 `LICENSE`）。但它引用的 `BetterLyrics.Sdk 1.0.3` 是
**GPL-3.0-or-later**，所以**编译出来的二进制整体按 GPL-3.0 分发**。`LICENSE` 末尾有完整的
第三方声明，改依赖前先读一遍，别把声明写错。
