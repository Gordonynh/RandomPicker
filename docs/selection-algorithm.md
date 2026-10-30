# 抽选算法

用户视角的行为见 [README](../README.md)，这里只记实现和取舍。

## 入口

`PickerHostService.Pick()` 按 `PickerSettings.Mode` 分三路：

| 来源 | 实现 |
|---|---|
| 随机抽人（`Random` / `NoRepeat`） | `RosterService.PickMany` |
| 随机抽组（`Group`） | `GroupPicker.PickMany` |
| 拍照抽人（`Photo`） | `CameraPicker.CaptureAndPickAsync` |

放回 / 不放回不是第三维度，而是 `PickerSettings.TextMode`：名单来源时它就是 `Mode` 本身，
抽组时 `Mode` 是 `Group`、由 `TextMode` 决定放不放回。**判断一律走 `PickerSettings.IsNoRepeat`**，
不要拿 `Mode == PickMode.NoRepeat` 去比。

一次抽几个由 `PickCount`（1~3）控制，三种来源都生效。

## 不放回

候选集是「本轮没抽过的人」，等概率取一个：

```csharp
var chosen = remaining[RandomNumberGenerator.GetInt32(remaining.Count)];
```

抽完一轮（候选为空）自动开新一轮：清 `DrawnThisRound`，候选恢复成全体，但——

1. **排除这一批已经抽到的**，所以一次抽 3 个时不会因为跨过轮次边界而重复；
2. **排除上一轮最后一抽**。那两抽是紧挨着发生的，撞上看起来就像抽重了。
   名单只剩一个人时挡不住，也没必要挡。

手动「重置本轮」只清 `DrawnThisRound`，**不清「上一个人是谁」**，理由同上。
（放回模式下这个按钮的含义不同，见下一节末尾。）

## 放回

不是纯均匀随机，而是 `WeightedPicker` 加权：

| 多久之前抽到的 | 权重 |
|---|---|
| 上一次 | 0（硬排除，保证不连着抽同一个） |
| 两次前 | 0.5 |
| 三次前 | 0.25 |
| 四次前 | 0.125 |
| 更早 / 没抽过 | 1 |

参数：`Decay = 0.5`、`Memory = 4`（只记最近 4 次，`RecentPicks` 长度由它卡住）。

**为什么不用纯均匀**：一节课抽十几次、班上三十人，「某人被抽三次、另一人一次没抽」
一点都不稀奇。均匀分布和看起来公平是两回事。

**为什么也不把最近抽过的全拉黑**：N 小的时候会退化成死板轮流（两个人就是 ABABAB），一眼假。

**参数是模拟出来的**（每组 8000 次）：

| 场景 | 纯均匀 | 加权重 |
|---|---|---|
| 6 组抽 10 次，某组被抽 ≥4 次 | 39% | 5% |
| 30 人抽 12 次，某人被抽 ≥3 次 | 19% | 4% |
| 连抽同一人 | 会 | 0 |

也试过 `0.35 / 记忆 5`（上表能再降一半），但那时「6 组抽 10 次最多次数是 2 次」的概率
到 61%，已经接近死板轮回了，所以没取。

**随机数**：`RandomNumberGenerator` 直接取操作系统熵源，没有种子、没有跨线程状态。
.NET 8 没有 `RandomNumberGenerator.GetDouble()`，所以 `WeightedPicker.NextDouble()`
自己取 8 字节、右移 11 位留 53 位尾数——精度和 `Random.NextDouble()` 一致，
也不会因为对 int 取模产生偏斜。

**「重置本轮」在放回模式下重置的是权重**：这里没有轮次可清，能清的是
`RecentPicks` / `RecentGroups` 这份降权记忆（连同 `LastPicked` / `LastGroupPicked`）。
清完之后所有人回到同一权重，等于重新开始积累「谁刚被抽过」。
按钮文案两种模式都叫「重置本轮」，含义由 `PickerHostService.ResetRound()` 按
`IsNoRepeat` 分流——菜单和设置页调的是同一个方法，不会两边不一致。

## 抽组

组不是名单，不读文件：`GroupPicker.Labels(settings)` 按 `GroupCount`（1~26）和
`GroupStyle`（字母 / 数字）算出 `A 组 …` 或 `1 组 …`。

抽选逻辑和名单是同一套：不放回用等概率、放回用同一份权重，只是状态换成
`DrawnGroupsThisRound` / `LastGroupPicked` / `RecentGroups`。

改组数或换组名写法会 `GroupPicker.ResetRound()`——「A 组」和「1 组」不是同一个组，
旧的轮次记录留着只会让「还剩几组」算得莫名其妙。

## 相关状态

| 字段 | 用途 |
|---|---|
| `DrawnThisRound` | 不放回，本轮已抽到的人 |
| `DrawnGroupsThisRound` | 不放回，本轮已抽到的组 |
| `LastPicked` / `LastGroupPicked` | 开新一轮时避开「连着两次同一个」 |
| `RecentPicks` / `RecentGroups` | 放回模式的降权依据，最近的排最前 |

全部存在插件配置目录的 `settings.json` 里。老配置没有 `RecentPicks`，
`PickerSettings.Normalize()` 会从 `LastPicked` 补一条，升级后第一次抽仍然避开刚抽过的那个。

**换名单会清空 `DrawnThisRound`**：那些名字属于上一份名单，留着只会让「本轮还剩几个」算错。
`RecentPicks` 之类不清——「刚抽过谁」跨名单仍然有意义。

名单库见 `NameListLibrary`：5 份名单固定叫 `namelib/list1.txt` ~ `list5.txt`，
名字写在文件里（`&$高一5班$`），所以改名不用动文件名，也不影响「当前用第几份」这个记忆。

## 拍照模式

见 [photo-detection.md](photo-detection.md)。它不参与上面这套轮次 / 权重逻辑，
用自己的「人脸位置回避」。
