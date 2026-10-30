# 拍照抽人的实现

用户视角的说明见 [README](../README.md)，这里只记实现细节和实测数据。

## 流水线

```
取帧 → 检测（YuNet）→ 按重叠度合并 → 挑出 N 个人脸 → 逐个裁切 → 显示
```

代码：`Services/CameraPicker.cs`（取帧、裁切、摄像头生命周期）、
`Services/FaceModel.cs`（ONNX 推理与解码）。

## 取帧

**走 `MediaFrameReader` 读预览流，不是 `CapturePhotoToStreamAsync`。**

实测多种摄像头（含虚拟摄像头、采集卡透传）的 `MediaStreamType.Photo` 档位**全是空的**——
虚拟/转接摄像头只有预览流，拍照接口一律抛异常。老版本「拍不出照」就是这个原因。
预览流这条路三个都能用，而且 `MediaFrameReader` 一次性建好之后**每帧只要 3~43 ms**。

**摄像头保持热着。** 打开一次要 200 ms ~ 1.5 s，首帧还要再等一会儿；
抽完一次之后维持一段时间（默认 120 秒）不关，连着抽就几乎是瞬时的。
代价是这期间指示灯亮着，到点自动关。设成 0 就是用完立刻关。

分辨率取摄像头支持的最高档，但**过了 2560 宽就不再往上取**：多出来的像素在缩到 640×640
时全被扔掉，只是白白拖慢取帧。

## 检测

模型是 OpenCV Zoo 的 `face_detection_yunet_2023mar.onnx`（232 KB），靠 ONNX Runtime 跑。
两样都随插件打包——宿主没有。这也是插件包 4.6 MB 而其它插件只有几十 KB 的原因。

**为什么不用 Windows 自带的 `FaceDetector`**：一张 1320×1214 的看台照片，
自带整帧只检出 4 张脸，切成 4×4 放大再检也只有 58 张、要 736 ms；
YuNet 单次推理是 **97 张 / 63 ms**。快一个数量级，还多找出七成的人。

几个改之前必须先知道的事实：

- 这个 onnx 的输入维度是**写死的 640×640**（`FaceModel.InputSize`），喂别的尺寸会被 ORT 拒绝。
- 摄像头画面大多是 16:9，直接拉成正方会掉召回。同一张 16:9 画面，
  **补边保持比例 67 张、拉伸只有 60 张**，所以走补边（`PadValue = 114`，和常见 letterbox 一致）。
- 缩放 + 补边 + 转 BGR planar 是在像素数组上一趟走完的，没走 WIC 的 `BitmapTransform`——
  那条路要先编码再解码，光这一下就比整个推理还慢。
- YuNet 吃的是 **0-255 原始值，不做归一化**，通道顺序 BGR。
- 解码和 OpenCV `FaceDetectorYN` 一致：`cx = (列 + bbox0) × stride`、`w = exp(bbox2) × stride`、
  `分数 = sqrt(cls × obj)`；先验点行优先，三个尺度 8/16/32。

**分数阈值**（默认 0.50，实测）：

| 阈值 | 检出人数 |
|---|---|
| 0.50 | 97 |
| 0.70 | 60 |
| 0.90（OpenCV 默认） | 1 |

0.9 在人多的场景下完全不能用，所以默认取 0.5。

**分块检测**（默认关）：把画面切成有重叠的 3×3 各检一遍再合并，能多找出一些
（实测 97 → 132），代价是每块一次推理。单次推理已经够用且只要 60 ms 左右，所以默认不开。

**合并**：按分数降序、面积降序，IoU > 0.3 视为重复框（`FaceModel.Merge`）。

## 挑人

`CameraPicker.ChooseMany` 一次挑 N 个，三件事都要顾上：

- 避开最近抽过的（`PhotoAvoidRecent`，默认 6）。教室里人不动，**用人脸在画面里的位置当身份**，
  位置落在最近抽过的那几个附近的先跳过。避开之后一个都不剩就放宽条件重挑——
  宁可重复也不能抽不出人。
- 同一批里不能挑到同一个人：多个人脸框离得近，几乎一定是同一个人。
- 实在挑不出那么多就少给几个，不硬凑。

回避记录**只在内存里**：摄像头一挪、人一换座位，那些坐标就没意义了，
存进配置反而会把过时的回避带到下一次开机。

## 裁切

按人脸框放大后裁：横向 ×1.8、纵向 ×2.4（`CropWidthFactor` / `CropHeightFactor`），
纵向多留头顶和肩膀。某一张裁失败就跳过，其余的照常用。

原图默认**不落盘**——合影是在场者的影像，不开「保存原始照片」时整帧只在内存里待到裁完就丢。

## 失败与兜底

- 没有摄像头 / 系统没给权限 / 无画面 / 检不到人脸 → 退回按名单抽，并在中央卡片上写明原因。
  静默失败比抽错人更让人摸不着头脑。
- 拍照模式还可以设一个「混入文字抽选」的概率（`PhotoTextChance`），按概率改成抽名字，
  照顾没被拍进画面的人。掷骰子用同一个随机源。

## 依赖加载

插件**不生成 deps.json**（生成了宿主的 `AssemblyDependencyResolver` 会照着它去插件目录找
所有依赖，把本该用宿主那份的 Avalonia 之类也一起拖下水），所以两个解析器都得自己挂：

- `FaceModel.EnsureManagedResolvable` —— 在**插件自己的** `PluginLoadContext` 上挂回调，
  按路径加载 `Microsoft.ML.OnnxRuntime.dll`（宿主没有这个程序集）。
- `FaceModel.EnsureNativeResolvable` —— 给 `Microsoft.ML.OnnxRuntime` 这个程序集挂
  `NativeLibrary.SetDllImportResolver`，把 `onnxruntime` 指到插件目录。

随包发布的原生文件（都在 `src/Native/`，写进 `pack-include.txt`）：

```
Microsoft.ML.OnnxRuntime.dll
onnxruntime.dll
onnxruntime_providers_shared.dll
face_detection_yunet_2023mar.onnx
```
