# 构建与打包

## 环境

- **.NET 8 SDK**（任何 8.0.x 都行，`global.json` 里锁的是 `8.0.100` + `latestFeature`）
- **Windows**：拍照抽人依赖 WinRT（`MediaCapture` / `FaceDetector` 那套），代码按 dll 路径引用
- 需要 **ClassIsland 源码**：工程通过 `ProjectReference` 指向 `ClassIsland.Core`

## 目录要求

整个仓库必须放在 ClassIsland 源码树的 `plugins/RandomPicker` 下：

```
ClassIsland/                  ← 宿主源码根目录
├─ ClassIsland.Core/
└─ plugins/
   └─ RandomPicker/           ← 本仓库
      └─ src/ClassIsland.RandomPicker.csproj
```

csproj 里的引用是 `..\..\..\ClassIsland.Core\ClassIsland.Core.csproj`（`src/` 里多了一级），
搬到别处会直接找不到工程。

## 编译

```bash
cd plugins/RandomPicker
dotnet build src/ClassIsland.RandomPicker.csproj -c Release
```

产物在 `src/bin/Release/net8.0/`。

## 打包 .cipx

用 Windows PowerShell 跑根目录的 `build.ps1`，一条命令走完**编译 → 打包 → 算 MD5**：

```powershell
./build.ps1                        # Release
./build.ps1 -Configuration Debug   # 换配置
./build.ps1 -SkipBuild             # 跳过编译，直接用现有输出打包
```

脚本自己会检查环境（`dotnet` 在不在、有没有 .NET 8 SDK、工程文件在不在、
上级目录有没有 `ClassIsland.Core`），不满足就直接报清楚原因，不会闷头编译到一半才炸。
脚本里没有任何写死的本机路径，装在哪儿都能跑。

输出：

```
cipx/ClassIsland.RandomPicker.cipx    插件包
cipx/checksums.md                     MD5，格式同官方 PluginSdk 的 generate-md5.ps1
```

> 脚本必须存成 **UTF-8 带 BOM**。PowerShell 5.1 按 ANSI 读无 BOM 的 ps1，
> 中文注释会让整个文件解析失败。

## .cipx 的格式要求

`.cipx` 就是普通 zip，宿主安装时**整个解压到 `Plugins/<id>/`**，
并且用 `pkg.GetEntry("manifest.yml")` 取清单。所以：

- **所有文件必须在 zip 根目录**，绝对不能套一层文件夹——装不上的绝大多数原因都是这个；
- 必须有根目录的 `manifest.yml`。脚本打完会重新打开 zip 自检这一点，
  并且**逐字比对包里的清单和仓库根目录那份**，对不上直接报错。

**清单一定要从仓库根目录现拷，不能让构建缓存替你做决定。** MSBuild 复制 `None` 文件时会把
源文件的时间戳一起带过去，`PreserveNewest` 于是可能误判「输出比源新」而跳过复制，
结果是改完版本号打包出来还是上一版。实测踩过：`manifest.yml` 写 2.0.0.0，
包里躺着 1.1.0.0，装上去一切正常、只有版本号不对。所以 csproj 里那三个 `Link` 文件都设成
`CopyToOutputDirectory="Always"`，`build.ps1` 在第 2 步还会无条件再同步一遍。

`Compress-Archive` 只接受 `.zip` 后缀，所以脚本先压成 zip 再改名。

## 包里放什么

**只放插件自己的东西。** 宿主已加载的程序集（`ClassIsland.Core` / `Shared` / `Shared.IPC` /
`Platforms.Abstractions`）一律不带——再塞一份会重复初始化，这和 csproj 里 `Private=false`
的用意一致。

除插件 dll 之外的文件写在 `pack-include.txt` 里（UTF-8 无 BOM，支持 `#` 注释），
目前是 ONNX Runtime 那四个：

```
Microsoft.ML.OnnxRuntime.dll
onnxruntime.dll
onnxruntime_providers_shared.dll
face_detection_yunet_2023mar.onnx
```

它们从输出目录取。`manifest.yml` / `icon.png` / `README.md` 由 csproj 用 `..\xxx` + `Link`
从仓库根目录复制进输出，所以脚本按文件名取就行。

## 几个已知的坑

**父目录的 `global.json` 可能锁了别的 SDK。** ClassIsland 源码树根目录也有一份 `global.json`，
`dotnet` 从**当前目录**往上找、就近生效，所以在仓库里构建用的是本仓库这份
（`8.0.100` + `rollForward: latestFeature`，任何 8.0.x SDK 都能用）。
在父目录直接跑 `dotnet build` 会用到宿主那份，可能就找不到 SDK 了。

**`C:\Program Files\dotnet\dotnet.exe` 有可能只有运行时没有 SDK。** 这种情况
`dotnet --list-sdks` 会返回空，`build.ps1` 会直接报「需要 .NET 8 SDK」而不是让编译莫名其妙地失败。

**CA1416 警告是预期的。** 拍照抽人用 WinRT API，而 TFM 是 `net8.0`。改成
`net8.0-windows10.0.19041.0` 会触发框架引用还原（离线环境走不通），所以按 dll 路径引用，
接受平台兼容性警告。

## 仓库结构

```
src/                 代码：插件工程 + 全部 C# / axaml
  Native/            ONNX Runtime 原生库与 YuNet 模型（随插件打包）
docs/                技术文档
manifest.yml         插件清单
icon.png             插件图标
README.md            用户文档（打包时也带进 .cipx）
build.ps1            一键编译 + 打包 + 算 MD5
pack-include.txt     除插件 dll 外还要打进包的文件清单
global.json          锁定 .NET SDK 版本
```
