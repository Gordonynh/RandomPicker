namespace ClassIsland.RandomPicker.Views;

/// <summary>
/// 悬浮钮当前的忙碌原因。
/// </summary>
/// <remarks>
/// 用枚举而不是一个 <c>bool</c>，是因为「忙」有两种来源：拍照（要开摄像头、检测人脸）
/// 和播抽选动画。两者的文案和时长都不一样，钮上得说清楚在忙什么，
/// 用户才知道该等还是该再点一下。
/// </remarks>
public enum PickerBusyKind
{
    /// <summary>不忙：钮上显示「抽」和剩余人数。</summary>
    None,

    /// <summary>正在拍照抽人。</summary>
    Shooting,

    /// <summary>正在播抽选动画。</summary>
    Animating
}
