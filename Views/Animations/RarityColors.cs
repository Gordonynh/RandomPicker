using System;
using Avalonia.Media;
using ClassIsland.RandomPicker.Services;

namespace ClassIsland.RandomPicker.Views.Animations;

/// <summary>
/// 品质配色到界面颜色的换算。
/// </summary>
/// <remarks>
/// 品质表那一层是纯计算，颜色存成三个字节，不引用界面框架。
/// 换算放在这里，是它唯一该出现的地方。
/// </remarks>
internal static class RarityColors
{
    /// <summary>取某一档品质的配色。</summary>
    public static Color ColorOf(ItemRarity rarity)
    {
        var tier = ItemRarityTable.TierOf(rarity);
        return Color.FromRgb(tier.Red, tier.Green, tier.Blue);
    }

    /// <summary>
    /// 把底色往品质色上混一点，用做方块底色。
    /// </summary>
    /// <param name="baseColor">底色。</param>
    /// <param name="tint">要混进去的颜色。</param>
    /// <param name="amount">混多少，0 = 全是底色，1 = 全是品质色。</param>
    /// <remarks>
    /// 方块底色不能直接用满饱和的品质色：五个方块并排全是大红大蓝，看不清名字。
    /// 混一点点进去，既认得出品质，又不抢文字的清晰度。
    /// </remarks>
    public static Color Mix(Color baseColor, Color tint, double amount)
    {
        // 喵~防御：比例越界时夹回来，免得算出超过 255 的分量，转成字节时会回绕。
        var ratio = amount < 0 ? 0 : amount > 1 ? 1 : amount;
        // 三个分量各自线性插值。
        return Color.FromRgb(
            (byte)Math.Round(baseColor.R + (tint.R - baseColor.R) * ratio),
            (byte)Math.Round(baseColor.G + (tint.G - baseColor.G) * ratio),
            (byte)Math.Round(baseColor.B + (tint.B - baseColor.B) * ratio));
    }
}
