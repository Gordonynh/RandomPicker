using ClassIsland.Core.Abstractions.Services.NotificationProviders;
using ClassIsland.Core.Attributes;

namespace ClassIsland.RandomPicker.Services;

/// <summary>
/// 随机抽选的提醒提供方。
/// </summary>
/// <remarks>
/// <c>INotificationHostService.ShowNotification</c> 是 internal 的，插件够不着；
/// 走 <see cref="NotificationProviderBase"/> 注册成一个提醒提供方，
/// 就能拿到公开的 <c>ShowNotification</c>，抽到人之后把名字推到主界面那条上。
/// 注册之后它也会出现在「设置 → 提醒」里，可以单独调音效、特效这些。
/// </remarks>
[NotificationProviderInfo("B7A3E1C4-92D6-4F58-8E2A-5D0C7F3B6A19", "随机抽选", "",
    "抽到人之后在主界面上显示名字。")]
public class PickerNotificationProvider : NotificationProviderBase
{
}
