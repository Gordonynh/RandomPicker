# AGENTS.md

ClassIsland 插件「随机抽选」：单项目 C# / .NET 8 / Avalonia，没有 solution、测试、lint、CI 或 pre-commit。
改动靠编译验证，纯逻辑另有一条轻量冒烟测试路径（见下）。

## 构建

- `dotnet build -c Release`（在本目录跑，会连带编 `ClassIsland.Core` 等宿主工程）
- **每次编译顺带出安装包**：`cipx/ClassIsland.RandomPicker.cipx`
  （csproj 里的 `CreateCipx` target，随 Build 跑；`cipx/` 已进 .gitignore）。
  宿主安装就是解压 zip、读**根部**的 `manifest.yml`（`PluginService`），
  不校验任何 hash，所以包内容对不对全靠这个 target 的白名单把关。
- **本仓库不能独立编译。** csproj 的 `ProjectReference` 是 `..\..\ClassIsland.Core\...`，
  必须位于 ClassIsland 源码树的 `plugins/<名>/` 下。独立检出直接编会报约 36 个
  CS0246/CS0234（缺 `ClassIsland.Core`、`ClassIsland.Shared`、`Microsoft.Extensions.*`、
  `Windows.*` 等）—— 预期行为，别为了「让编译过」改 using 或删功能。
- **当前布局**：真实仓库在 `~/Documents/coding/ClassIsland/plugins/RandomPicker`，
  `~/Documents/coding/RandomPicker` 是指向它的符号链接。
  编译必须走真实路径：dotnet/MSBuild 会解析符号链接，从链接路径编译时
  `..\..\ClassIsland.Core` 会解析到错误位置（MSB3202 找不到项目文件）。
- **宿主树首次可用需要三步**（都是一次性的，克隆在 `~/Documents/coding/ClassIsland`，depth 1）：
  1. 子模块：`.gitmodules` 用的是 git@ SSH 地址，无 key 会失败 →
     `git config submodule.vendors/EdgeTtsSharp.url https://github.com/ClassIsland/EdgeTtsSharp.git`
     后再 `git submodule update --init`。
  2. 本地 tag：`git tag 2.1.0.0`。宿主的 `SimpleGitInfoGenerator` 跑 `git describe`，
     浅克隆没有 tag 会得到 CS7034（版本串变成 `fatal: 没有发现名称...`）。
  3. WinRT 引用：csproj 用 `HintPath` 硬指
     `$(NuGetPackageRoot)microsoft.windows.sdk.net.ref\10.0.19041.57\...`，
     **不是** PackageReference，本仓库的 restore 拉不到它；缺了报 10 个
     CS0234/CS0246（`Windows.*`）。补法：临时工程加
     `<PackageReference Include="microsoft.windows.sdk.net.ref" Version="10.0.19041.57" />`
     跑一次 `dotnet restore` —— 会报 NU1213（DotnetPlatform 包类型不兼容），
     **但包已经下进缓存，HintPath 就能命中**。
- 冒烟测试：没有测试套件，但纯逻辑（名单/小组解析、抽选规则）可以写个临时 console
  工程直接引用 `bin/Release/net8.0/ClassIsland.RandomPicker.dll` 跑断言 ——
  只碰 `RosterService` / `GroupService` / `PickerSettings` 这些 BCL 类型时，
  不需要宿主、不需要 Avalonia。

## 打包引用规则（最容易改错的地方）

csproj 注释是硬结论的来源，动 `Reference`/打包之前先读。

- **宿主已有的**（`ClassIsland.Core`、Avalonia、FluentAvaloniaUI、WinRT 两件套）：
  引用上标 `Private="false"` / `ExcludeAssets="runtime"`（见 csproj，Avalonia 主包是普通
  PackageReference，靠打包清单把它排除），**绝不能进插件包**。
  宿主 `PluginLoadContext` 有 WinRT 白名单，会强制解析到宿主那份。
- **插件自带的**（ONNX Runtime）：`Private="true"`，dll + 原生库 + 模型平铺到输出根目录，
  且必须在 `pack-include.txt` 里列一行 —— `CreateCipx` target 读那份清单决定哪些文件进 `.cipx`；
  **列了却不在输出目录会直接报打包错误**（引用 / 复制 / 清单三处同步漏一处就断）。
  新增任何随包依赖 = csproj 引用 + `None Include CopyToOutputDirectory` + `pack-include.txt` 三处同步。
- 打包用的是 csproj 里自写的 `CreateCipx`，**不是**官方 `ClassIsland.PluginSdk` 的同名 target：
  官方那个压整个输出目录（会把宿主的 `ClassIsland.Shared.dll` 等一起打包，且强依赖 pwsh），
  原因写在 csproj 注释里，别「改回官方做法」。
- `GenerateDependencyFile=false`（不生成 deps.json）是刻意的：生成了宿主的
  `AssemblyDependencyResolver` 会把本该用宿主那份的 Avalonia 一起拖下水。
  代价是没有 deps.json，程序集/原生库要手动挂解析回调：
  `RandomPickerPlugin.EnsureAssemblyResolvable`、`FaceModel.EnsureManagedResolvable`、
  `FaceModel.EnsureNativeResolvable`。**新增随包托管依赖时这几处要一起改。**
- 不要把 TFM 切成 `net8.0-windows10.0.19041.0`（会触发框架引用还原，注释里说明了为什么走不通）。
- `Native/` 下的 dll 和 onnx 是**提交进仓库的二进制**，不是生成物，别删别重新生成。

## 版本号

`ClassIsland.RandomPicker.csproj` 的 `<Version>` 和 `manifest.yml` 的 `version` 必须一致
（当前 1.2.0.0）；`manifest.yml` 的 `apiVersion` 是宿主 API 级别，非必要不改。

## 运行时布局

- 设置与名单在宿主给的 `PluginConfigFolder` 下：`settings.json`、`名单.txt`、`小组.txt`、
  可选 `名单-文字.txt`，存原图时还有照片。全部纯文本、`FileSystemWatcher` 热加载。
- **抽选对象分个人和小组。** 小组成员来自名单行尾的 id（`张三 G1`，最后一段是 id，
  无 id 行为完全不变），组名来自 `小组.txt`（`G1 第一组`，第一段是 id）；
  合并规则、空组排除等见 `GroupService` 头注释。个人/小组的「本轮已抽」在
  `PickerSettings` 里是**两套字段**（`DrawnThisRound` / `DrawnGroupsThisRound`），别混用。
- 交互：**左键**点悬浮球弹「抽个人 / 抽小组」小菜单（`PickerWindow.ShowPickMenu`，
  点完才抽，不再一步直抽）；右键或触摸长按是设置菜单；拖动照旧。
- 入口 `RandomPickerPlugin` → 注册 `PickerHostService`（IHostedService，悬浮窗与抽选主流程）、
  `PickerNotificationProvider`、`PickerSettingsPage`。
- 拍照抽人走 WinRT `MediaCapture`/`MediaFrameReader`（Windows 专属），失败时静默退回按名单抽；
  `TopmostEnforcer` 用 `OperatingSystem.IsWindows()` 门控。manifest 声称支持三平台，
  改平台相关代码时别破坏非 Windows 下的编译与降级路径。

## 约定

- 注释、UI 字符串、README、commit message 全部是**中文**，新写的也保持中文。
  注释风格是「记录来之不易的实测结论/为什么不能那样改」，不是复述代码在干什么。
- 随机数一律 `RandomNumberGenerator`（操作系统熵源），不要用 `Random`。
- 提交历史只有 `main`，仓库里没有 CI workflow。

## 参考

- `README.md`：用户视角的完整行为说明（三种模式、小组、阈值实测数据、置顶实现），与代码冲突时以代码为准。
- `~/Documents/coding/ClassIsland/AGENTS.md`：宿主仓库的构建命令与约束，宿主侧问题以它为准。
