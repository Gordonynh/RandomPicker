# 窗口与菜单

悬浮球、中央结果弹窗、右键菜单的底层行为，以及踩过的坑。

## 置顶

只设 `Window.Topmost` 是不够的：全屏应用、别的置顶窗口、甚至资源管理器重启，
都会把窗口挤下去。`Interop/TopmostEnforcer.cs` 额外做两件事：

1. 加上 `WS_EX_TOOLWINDOW`（不进 Alt+Tab）和 `WS_EX_NOACTIVATE`（不抢焦点）——
   不抢焦点本身就减少了被系统降层的机会；
2. 起一个秒级定时器，反复 `SetWindowPos(HWND_TOPMOST)` 顶回去。

全是文档化的公开 API，不改系统设置，也不碰别的进程。

> **菜单窗口不能加 `WS_EX_NOACTIVATE`。** 它靠失焦来自动关闭，拿不到焦点就永远关不掉。
> 所以 `TopmostEnforcer` 的 `preventActivation` 参数默认 true，给菜单传 false。

## 隐藏时的「幽灵控件」

`SetWindowPos` 的 **`SWP_SHOWWINDOW` 会把已经 `Hide()` 掉的原生窗口重新显示出来**。
定时器每秒跑一次，于是球消失不到一秒又被顶回来；但 Avalonia 这边 `IsVisible` 仍是 `false`，
窗口处在「系统认为显示、框架认为隐藏」的分裂状态——**看得见、点不着、也关不掉**。

修法是在 `Reassert()` 开头挡掉隐藏状态：

```csharp
if (!_window.IsVisible) return;
```

隐藏只 `Hide()`、不销毁实例，所以位置、置顶定时器、名单状态都还在，
叫回来不会挪地方。完整链路：`PickerHostService.SetPickerWindowVisible(bool)`。

## OnOpened 会重入

隐藏之后再 `Show()`，`OnOpened` 会**再跑一遍**。里面建 `TopmostEnforcer` 之前必须
`Dispose` 掉旧那个，否则置顶定时器会一个叠一个，每个都在对着同一个窗口句柄调 `SetWindowPos`。

## 和其他插件抢置顶

课后自动复原的全屏倒计时、值日的整屏提醒，和本插件都在用定时器顶自己。
谁的定时器刚跳过谁就在上面，于是互相抢、画面闪；更糟的是球一旦压在倒计时上面，
**用户点屏幕想取消，点到的是「抽一个」**。

解法是 `Shared/Overlay/ExclusiveOverlay.cs`：用一个**内核命名事件**（`Local\...`）当信号，
持有句柄的一方表示「现在有独占全屏窗口」。窗口开着时本插件主动让位
（`SetWindowPos(HWND_NOTOPMOST)`），不再跟它抢。

- 三个插件各自在独立的 `PluginLoadContext` 里，静态字段不通用，所以走命名对象——
  名字一样就是同一个内核对象，跨程序集甚至跨进程都认。
- **用「持有句柄」而不是「置位 / 复位」**：句柄全关掉时内核对象自动消失，
  哪怕持有方崩了没走到清理代码，信号也会自己失效，不会把球永久压住。

## 中央弹窗为什么只有卡片那么大

早先版本是整屏窗口，结果它把悬浮球盖住了，连着抽时第二下点的是它而不是球，
表现就是「点不动」。

试过用 `WS_EX_TRANSPARENT` 让窗口穿透，但那个样式只有在窗口同时是 `WS_EX_LAYERED` 时
才对命中测试生效；而给 Avalonia 的透明窗口补 `WS_EX_LAYERED` 会打乱它自己的合成，
窗口直接不显示。两条路都堵死，索性从根上解决：**窗口不覆盖屏幕，就没有挡不挡的问题**。

弹窗同时是单例复用的：再抽一次不新开窗口，直接换内容并重置计时，避免连点叠出一摞。

## 右键菜单为什么用 MenuFlyout

用 `MenuFlyout` 而**不是** `ContextMenu`，原因是实打实的坑：ClassIsland 的控件主题只来自
FluentAvalonia，仓库里没有引用 `Avalonia.Themes.Fluent`，而 FluentAvalonia 2.4.1 压根不提供
`ContextMenu` 的 `ControlTheme`（DLL 里连这个类型名都没有）。Avalonia 找隐式主题只按
StyleKey 精确匹配、没有基类回退，于是 `ContextMenu` 的 Template 是 null → 不产生可视子级 →
量出来 0×0，弹出窗口创建了但屏幕上什么都没有，表现就是「右键没反应」。

`MenuFlyout` / `MenuFlyoutPresenter` / `MenuItem` / `Separator` 都被 FluentAvalonia 完整主题化了，
这也是 ClassIsland 自己全仓库的写法。

## 菜单里的单选组要自己接线

`MenuItem` 的 `ToggleType = Radio` + `GroupName` 只负责**外观**，互斥由
`DefaultMenuInteractionHandler` 里的 `RadioButtonGroupManager` 做——而那张分组表
**只在菜单挂到可视树上的那一刻**扫现有子项建立。我们的菜单项是每次打开时新建的、
`GroupName` 在对象初始化器里就设好了，等 presenter 建表时子项还没生成，表是空的，
于是点第二项时没人去取消第一项，两个圆点同时亮。

所以 `PickerWindow.BuildMenuItems()` 末尾必须调 `WireRadioGroups(items)`：按 `GroupName`
递归收集（要进子菜单），给每项挂 Click 去取消同组其它项。**新增一组单选只要 GroupName
给对就自动接上。**

顺带两个相关的坑：

- `DefaultMenuInteractionHandler.Click()` 是**先**改 `IsChecked` **再**抛 Click，
  所以 `Toggle()` 的处理器里读 `item.IsChecked` 就是翻转后的值，别再自己取反。
- 点已选中的 radio 项时 Avalonia 不会重新置 true，`WireRadioGroups` 里补了一下。

## 托盘菜单

用 `ITaskBarIconService.MoreOptionsMenuItems`（背后是 `MoreOptionsMenu!.Items`），
那个菜单要等宿主 `MainWindow` 构造完才存在，取早了直接抛空引用——插件可能比它先起来，
所以加不上就隔一秒重试，最多试半分钟。

**标题必须带「抽选」二字**（现为「显示 / 隐藏抽选悬浮球」）：SystemTools 等插件也有自己的
悬浮窗，会往同一个「更多选项」菜单里塞「显示悬浮窗 / 隐藏悬浮窗」，两边同名时用户分不清。
