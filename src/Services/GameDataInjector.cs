using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using VPet_Simulator.Windows.Interface;

namespace VPet.Plugin.PetChat.Services;

public class GameDataInjector
{
    private readonly IMainWindow _mw;

    public GameDataInjector(IMainWindow mw)
    {
        _mw = mw;
    }

    private static object? GetProp(object? obj, string[] candidateNames)
    {
        if (obj == null) return null;
        Type t = obj.GetType();
        foreach (var name in candidateNames)
        {
            try
            {
                PropertyInfo? pi = t.GetProperty(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                if (pi != null)
                {
                    var val = pi.GetValue(obj);
                    if (val != null) return val;
                }
                FieldInfo? fi = t.GetField(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                if (fi != null)
                {
                    var val = fi.GetValue(obj);
                    if (val != null) return val;
                }
            }
            catch
            {
            }
        }
        return null;
    }

    private static string FormatValue(object? v)
    {
        if (v == null) return "(未知)";
        string s = v.ToString() ?? string.Empty;
        // 数值类属性：尝试去掉小数点后多余的0
        if (v is double d)
            return d.ToString(d == Math.Floor(d) ? "0" : "0.##");
        if (v is float f)
            return f.ToString(f == Math.Floor(f) ? "0" : "0.##");
        return s;
    }

    /// <summary>
    /// 获取游戏存档中的宠物数据对象（GameSave_VPet）。
    /// 路径: IMainWindow.GameSavesData.GameSave
    /// </summary>
    private object? GetGameSaveVpet()
    {
        try
        {
            var gsData = GetProp(_mw, new[] { "GameSavesData" });
            if (gsData == null) return null;
            var gs = GetProp(gsData, new[] { "GameSave", "Save" });
            return gs;
        }
        catch
        {
            return null;
        }
    }

    public string BuildGameDataBlock()
    {
        var lines = new List<string>();

        // 日期时间（游戏内时间，暂无直接接口，用系统时间）
        try
        {
            var now = DateTime.Now;
            var zh = new CultureInfo("zh-CN");
            string weekday = zh.DateTimeFormat.GetDayName(now.DayOfWeek);
            lines.Add($"日期时间: {now:yyyy-MM-dd} {weekday} {now:HH:mm}");
        }
        catch
        {
            lines.Add($"日期时间: {DateTime.Now:yyyy-MM-dd HH:mm}");
        }

        object? save = GetGameSaveVpet();

        if (save != null)
        {
            // 宠物名
            var name = GetProp(save, new[] { "Name" });
            lines.Add($"宠物名: {FormatValue(name)}");

            // 主人名
            var hostName = GetProp(save, new[] { "HostName" });
            lines.Add($"主人名: {FormatValue(hostName)}");

            // 金钱
            var money = GetProp(save, new[] { "Money" });
            lines.Add($"金钱: {FormatValue(money)}");

            // 等级
            var level = GetProp(save, new[] { "Level" });
            var levelMax = GetProp(save, new[] { "LevelMax" });
            lines.Add($"等级: {FormatValue(level)}/{FormatValue(levelMax)}");

            // 经验
            var exp = GetProp(save, new[] { "Exp" });
            lines.Add($"经验: {FormatValue(exp)}");

            // 体力
            var strength = GetProp(save, new[] { "Strength" });
            lines.Add($"体力: {FormatValue(strength)}");

            // 心情（Feeling）
            var feeling = GetProp(save, new[] { "Feeling" });
            lines.Add($"心情: {FormatValue(feeling)}");

            // 饱腹度（StrengthFood）
            var food = GetProp(save, new[] { "StrengthFood" });
            lines.Add($"饱腹度: {FormatValue(food)}");

            // 口渴度（StrengthDrink）
            var drink = GetProp(save, new[] { "StrengthDrink" });
            lines.Add($"口渴度: {FormatValue(drink)}");

            // 好感度（Likability）
            var like = GetProp(save, new[] { "Likability" });
            lines.Add($"好感度: {FormatValue(like)}");

            // 健康值（Health）
            var health = GetProp(save, new[] { "Health" });
            lines.Add($"健康值: {FormatValue(health)}");

            // 当前状态（Mode）
            var mode = GetProp(save, new[] { "Mode" });
            lines.Add($"状态: {FormatValue(mode)}");
        }
        else
        {
            lines.Add("(无法读取游戏数据)");
        }

        var sb = new StringBuilder();
        sb.Append("\n======游戏数据======\n");
        foreach (var l in lines)
        {
            sb.AppendLine(l);
        }
        return sb.ToString();
    }
}
