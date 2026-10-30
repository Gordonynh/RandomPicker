using System;
using System.IO;

namespace ClassIsland.RandomPicker.Services;

/// <summary>
/// 名单库：5 份名单，放在插件配置目录的 <c>namelib/</c> 下。
/// </summary>
/// <remarks>
/// 文件名固定是 <c>list1.txt</c> ~ <c>list5.txt</c>，<b>不跟着名单名走</b>。
/// 名字写在文件内容里（<c>@高一5班</c>），所以改名字只改一行字，
/// 不用重命名文件，「当前选中第几份」这个记忆也不会因为改名而错位。
/// <para/>
/// 开了「文字抽选用单独名单」时，那份是同一序号旁边的 <c>listN-文字.txt</c>。
/// </remarks>
public sealed class NameListLibrary
{
    /// <summary>一共几份名单。</summary>
    public const int SlotCount = 5;

    /// <summary>名单放在配置目录下的哪个子目录。</summary>
    public const string FolderName = "namelib";

    private readonly string _folder;

    public NameListLibrary(string configFolder)
    {
        _folder = Path.Combine(configFolder, FolderName);
    }

    /// <summary>名单目录。</summary>
    public string FolderPath => _folder;

    /// <summary>第 <paramref name="slot"/> 份名单的文件路径（从 1 起算）。</summary>
    public string PathOf(int slot) => Path.Combine(_folder, $"list{Clamp(slot)}.txt");

    /// <summary>第 <paramref name="slot"/> 份名单的「文字抽选专用」兄弟文件。</summary>
    public string TextPathOf(int slot) => Path.Combine(_folder, $"list{Clamp(slot)}-文字.txt");

    /// <summary>把序号夹进 1 ~ 5。</summary>
    public static int Clamp(int slot) => Math.Clamp(slot, 1, SlotCount);

    /// <summary>读一下某份名单的名字，读不到就是 <c>null</c>。</summary>
    public string? PeekTitle(int slot) => RosterService.PeekTitle(PathOf(slot));

    /// <summary>
    /// 把老版本那两份名单搬进名单库。
    /// </summary>
    /// <remarks>
    /// 只在 <c>list1.txt</c> 还不存在时动手，所以升级只会发生一次；
    /// 已经在用新结构了就什么都不做。内容复制过去之后，老文件改名为 <c>.bak</c> 留底——
    /// 留着原名会被误会成「插件还在读它」。
    /// </remarks>
    public void MigrateLegacy(string legacyRosterPath, string legacyTextPath)
    {
        try
        {
            Directory.CreateDirectory(_folder);
        }
        catch (Exception)
        {
            return;
        }

        MigrateOne(legacyRosterPath, PathOf(1));
        MigrateOne(legacyTextPath, TextPathOf(1));
    }

    private static void MigrateOne(string legacyPath, string targetPath)
    {
        try
        {
            if (!File.Exists(legacyPath) || File.Exists(targetPath))
            {
                return;
            }

            File.Copy(legacyPath, targetPath);
            File.Move(legacyPath, legacyPath + ".bak");
        }
        catch (Exception)
        {
            // 迁移失败不该拦住插件启动：用户还能在设置页里手动把内容粘过去。
        }
    }

    /// <summary>
    /// 菜单 / 下拉框里怎么写这一项。
    /// </summary>
    /// <param name="slot">序号，从 1 起算。</param>
    /// <param name="title">名单自己声明的名字，没有就传 <c>null</c>。</param>
    /// <param name="maxTitleChars">
    /// 名字最多显示几个字，超出的截断并加省略号。传 0 或负数表示不限。
    /// </param>
    /// <returns>有名字时就是名字本身，没名字时是「名单1」这种默认叫法。</returns>
    /// <remarks>
    /// <b>有名字时不带序号。</b>名字本来就是给人认的（「高一5班」比「1 - 高一5班」清爽），
    /// 序号只在用户还没起名时用来区分——那种时候没有别的信息可显示。
    /// </remarks>
    public static string Label(int slot, string? title, int maxTitleChars)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return $"名单{slot}";
        }

        var text = title.Trim();
        if (maxTitleChars > 0 && text.Length > maxTitleChars)
        {
            text = text[..maxTitleChars] + "…";
        }

        return text;
    }
}
