using System;
using System.Collections.Generic;
using System.Globalization;
using Alife.Framework;

namespace presents.EmotionLive2DPet;

/// <summary>设置项类型。</summary>
public enum PetSettingType
{
    Bool,
    Number,
    Choice,
}

/// <summary>一个可注册的设置项描述（由各模块在构造时注册，设置面板据此动态渲染）。</summary>
public class PetSetting
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string Group { get; init; } = "其它";
    public PetSettingType Type { get; init; } = PetSettingType.Bool;
    public string Default { get; init; } = "1";
    /// <summary>Choice 类型的可选值（存的值）。</summary>
    public string[] Options { get; init; } = Array.Empty<string>();
    /// <summary>Choice 类型可选项的显示名（与 Options 一一对应）。</summary>
    public string[] OptionLabels { get; init; } = Array.Empty<string>();
    public double Min { get; init; }
    public double Max { get; init; } = 100;
    public double Step { get; init; } = 1;
    /// <summary>隐藏项：不显示在设置面板里（但仍可读写，用于内部状态）。</summary>
    public bool Hidden { get; init; }
}

/// <summary>
/// 桌宠交互中枢：模块间共享的交互状态、可扩展的设置系统与统一上报入口。
/// - <see cref="EditMode"/>：区域编辑模式开关。
/// - 设置系统：任何模块都能注册设置项（开关/数值/选项，可分组的任意组合），
///   前端「设置」面板自动按描述渲染，值按角色持久化——新增功能无需改面板代码。
/// - <see cref="Report"/>：所有交互统一从这里把"发生了什么"告诉角色，由她自行决定反应。
/// </summary>
public class PetInteractionHub
{
    /// <summary>
    /// 内置交互开关，Group 用于面板分组。
    /// 注意：点击/长按/抚摸/滚轮/双击 这几个「身体部位」手势不在这里——
    /// 它们已并入「互动区域」机制，由每个区域自己的勾选项控制（见区域设置面板）。
    /// </summary>
    public static readonly (string Key, string Label, string Group, bool Default)[] BoolCatalog =
    {
        ("dwell",      "停留注视", "感知", true),
        ("neglect",    "久不理",   "感知", true),
        ("menu",       "右键菜单", "感知", true),
        ("edge",       "贴边吸附", "桌面", false),
        ("return",     "拖出爬回", "桌面", false),
        // 模型自带 Interaction 的 8 种交互类型（内容在「模型交互」页编辑）
        ("ix_startup",      "启动反馈", "模型交互", true),
        ("ix_head",         "点头部",   "模型交互", false),
        ("ix_body",         "点身体",   "模型交互", false),
        ("ix_mouse_combo",  "连续触摸", "模型交互", false),
        ("ix_rotate",       "旋转",     "模型交互", false),
        ("ix_window_shake", "大幅晃动", "模型交互", true),
        ("ix_window_move",  "快速移动", "模型交互", true),
        ("ix_mouse_shake",  "鼠标转圈", "模型交互", true),
        ("hotkey",     "全局热键", "其它", true),
    };

    /// <summary>模型 Interaction 的 8 种交互类型（Key=存储用的开关名，Type=模型文件里的字段名）。</summary>
    public static readonly (string Key, string Type, string Label)[] InteractionTypes =
    {
        ("ix_startup",      "startup",      "启动反馈"),
        ("ix_head",         "head",         "点头部"),
        ("ix_body",         "body",         "点身体"),
        ("ix_mouse_combo",  "mouse_combo",  "连续触摸"),
        ("ix_rotate",       "rotate",       "旋转"),
        ("ix_window_shake", "window_shake", "大幅晃动"),
        ("ix_window_move",  "window_move",  "快速移动"),
        ("ix_mouse_shake",  "mouse_shake",  "鼠标转圈"),
    };

    /// <summary>该交互类型是否启用。</summary>
    public bool IsInteractionOn(string type)
    {
        foreach ((string Key, string Type, string _) in InteractionTypes)
            if (Type == type) return IsOn(Key);
        return false;
    }

    readonly InteractedEventCallback onInteracted;
    readonly StorageSystem storage;
    readonly string settingsKey;
    readonly object gate = new();
    readonly Dictionary<string, PetSetting> registry = new();
    readonly List<string> order = new();
    readonly Dictionary<string, string> values = new();

    public volatile bool EditMode;
    public volatile string EditTool = "rect";

    public PetInteractionHub(InteractedEventCallback onInteracted, StorageSystem storage, PetStorageKey storageKey)
    {
        this.onInteracted = onInteracted;
        this.storage = storage;
        settingsKey = $"{storageKey.Value}/Live2DDeskPet/PetSettings";
        try
        {
            Dictionary<string, string>? saved = storage.GetObject<Dictionary<string, string>>(settingsKey);
            if (saved != null)
                foreach (KeyValuePair<string, string> kv in saved)
                    values[kv.Key] = kv.Value;
        }
        catch { }
    }

    /// <summary>注册一个设置项（重复 key 只保留首次）。</summary>
    public void Register(PetSetting setting)
    {
        lock (gate)
        {
            if (registry.ContainsKey(setting.Key)) return;
            registry[setting.Key] = setting;
            order.Add(setting.Key);
        }
    }

    public PetSetting? Describe(string key)
    {
        lock (gate) return registry.TryGetValue(key, out PetSetting? s) ? s : null;
    }

    string RawOrDefault(string key)
    {
        lock (gate)
        {
            if (values.TryGetValue(key, out string? v) && v != null) return v;
            return registry.TryGetValue(key, out PetSetting? s) ? s.Default : "1";
        }
    }

    public bool IsOn(string key) => RawOrDefault(key) is "1" or "true" or "True" or "on";

    public double GetNumber(string key) =>
        double.TryParse(RawOrDefault(key), NumberStyles.Any, CultureInfo.InvariantCulture, out double d) ? d : 0;

    public string GetString(string key) => RawOrDefault(key);

    public void SetValue(string key, string value)
    {
        lock (gate)
        {
            values[key] = value ?? "";
            try { storage.SetObject(settingsKey, values); } catch { }
        }
    }

    /// <summary>设置面板数据：按注册顺序返回所有设置项及其当前值。</summary>
    public List<Dictionary<string, object?>> Snapshot()
    {
        List<Dictionary<string, object?>> list = new();
        lock (gate)
        {
            foreach (string key in order)
            {
                PetSetting s = registry[key];
                if (s.Hidden) continue;
                string value = values.TryGetValue(key, out string? v) && v != null ? v : s.Default;
                list.Add(new Dictionary<string, object?>
                {
                    ["key"] = s.Key,
                    ["label"] = s.Label,
                    ["group"] = s.Group,
                    ["type"] = s.Type switch { PetSettingType.Number => "number", PetSettingType.Choice => "choice", _ => "bool" },
                    ["value"] = value,
                    ["default"] = s.Default,
                    ["options"] = s.Options,
                    ["optionLabels"] = s.OptionLabels,
                    ["min"] = s.Min,
                    ["max"] = s.Max,
                    ["step"] = s.Step,
                });
            }
        }
        return list;
    }

    /// <summary>把"发生了什么"上报给角色（由她决定反应）。message 为空则不报。</summary>
    public void Report(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        onInteracted?.Invoke(message);
    }
}
