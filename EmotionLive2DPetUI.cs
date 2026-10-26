using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Alife.Framework;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace presents.EmotionLive2DPet;

/// <summary>
/// 「Live2D 具身桌宠」插件设置页。
///
/// 为什么手写而不用框架的自动表单：
///   · 自动表单把配置项按声明顺序平铺，换模型要去功能管理处翻文件夹、抄文件夹名再填回来，极其难用；
///   · 这里把「模型」提到最上面：递归扫描默认模型目录与额外目录，列出所有 *.model3.json，点一下即可切换；
///   · 其余配置按「表演 / 语音与字幕 / 更多」分组，改动即时下发到运行中的桌宠（不必重启）。
/// 持久化仍沿用框架底部的「应用全局 / 应用到角色」按钮。
/// </summary>
public sealed class EmotionLive2DPetUI : ModuleUIBase<EmotionLive2DPet, EmotionLive2DPetConfig>
{
    string message = "";
    string modelFilter = "";

    List<PetModelEntry> models = new();
    bool modelsLoaded;
    bool switching;

    /// <summary>已经用手写控件渲染过的配置项，反射区会跳过，避免重复出现。</summary>
    static readonly HashSet<string> ExplicitProps = new(StringComparer.Ordinal) {
        nameof(EmotionLive2DPetConfig.ModelName),
        nameof(EmotionLive2DPetConfig.ExtraModelPaths),
        nameof(EmotionLive2DPetConfig.ExpressionIntensity),
        nameof(EmotionLive2DPetConfig.BodyMotionGain),
        nameof(EmotionLive2DPetConfig.AutoEmotionFromChat),
        nameof(EmotionLive2DPetConfig.ShowSubtitle),
        nameof(EmotionLive2DPetConfig.IdleMotionEnabled),
        nameof(EmotionLive2DPetConfig.GazeFollowEnabled),
        nameof(EmotionLive2DPetConfig.SpeechLipSyncEnabled),
        nameof(EmotionLive2DPetConfig.PersistEmotion)
    };

    /// <summary>未被手写控件覆盖的配置项（按声明顺序完整列出，不漏项）。</summary>
    static readonly PropertyInfo[] RestProps = typeof(EmotionLive2DPetConfig)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite)
        .Where(p => !ExplicitProps.Contains(p.Name))
        .Where(p => p.GetCustomAttribute<BrowsableAttribute>()?.Browsable != false)
        .ToArray();

    /// <summary>把改动同步给运行中的模块（立即生效；持久化交给框架底部按钮）。</summary>
    void Sync()
    {
        try
        {
            if (Module == null) return;
            Module.Configuration = Configuration;
            Module.ApplyConfigChange();
        }
        catch { }
    }

    // ---------------- 模型 ----------------

    void RefreshModels()
    {
        //扫描不依赖模块实例：插件还没跑起来（或没配置模型）时，这里也必须能列出模型并给出反馈
        try
        {
            models = EmotionLive2DPet.ScanModels(Configuration?.ExtraModelPaths);
            message = models.Count == 0
                ? $"没有找到模型。默认目录：{EmotionLive2DPet.ModelRoot}"
                : $"找到 {models.Count} 个模型，点一下即可切换";
        }
        catch (Exception e)
        {
            models = new List<PetModelEntry>();
            message = "扫描模型失败：" + e.Message;
        }
        modelsLoaded = true;
    }

    void EnsureModels()
    {
        if (modelsLoaded || Configuration == null) return;
        RefreshModels();
    }

    IEnumerable<PetModelEntry> VisibleModels()
    {
        if (string.IsNullOrWhiteSpace(modelFilter)) return models;
        string key = modelFilter.Trim();
        return models.Where(m =>
            m.Name.Contains(key, StringComparison.OrdinalIgnoreCase)
            || m.Location.Contains(key, StringComparison.OrdinalIgnoreCase));
    }

    bool IsActive(PetModelEntry entry)
    {
        string current = Configuration.ModelName ?? "";
        return string.Equals(entry.Identifier, current, StringComparison.OrdinalIgnoreCase)
               || string.Equals(entry.Directory, current, StringComparison.OrdinalIgnoreCase)
               || string.Equals(entry.ModelJsonPath, current, StringComparison.OrdinalIgnoreCase);
    }

    PetModelEntry? ActiveModel()
    {
        return models.FirstOrDefault(IsActive);
    }

    async Task PickModelAsync(PetModelEntry entry)
    {
        if (switching) return;

        switching = true;
        message = $"正在加载模型「{entry.Name}」…";
        try
        {
            await InvokeAsync(StateHasChanged);

            bool ok;
            if (Module == null)
            {
                //模块还没启动：先记下选择，应用配置后由插件启动时加载
                Configuration.ModelName = entry.Identifier;
                ok = true;
                message = $"已选择「{entry.Name}」，点底部「应用到全局 / 应用到角色」后生效";
            }
            else
            {
                ok = await Module.SwitchModelAsync(entry.Identifier);
                if (ok)
                {
                    Configuration.ModelName = entry.Identifier;
                    Sync();
                    message = $"已切换到「{entry.Name}」";
                }
                else if (string.IsNullOrEmpty(message) || message.StartsWith("正在", StringComparison.Ordinal))
                {
                    message = $"模型「{entry.Name}」加载失败，已保留原模型";
                }
            }
            if (ok == false && message.StartsWith("正在", StringComparison.Ordinal))
                message = $"模型「{entry.Name}」加载失败";
        }
        catch (Exception e)
        {
            message = "切换模型失败：" + e.Message;
        }
        finally
        {
            switching = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    async Task BrowseModelsAsync()
    {
        string[]? picked = null;
        try
        {
            if (Module == null)
            {
                message = "插件尚未启动，无法打开文件夹选择框；也可以把路径粘贴到下面「额外模型目录」";
                await InvokeAsync(StateHasChanged);
                return;
            }

            picked = await Module.PickFolderAsync("选择模型文件夹（会递归扫描其中的 *.model3.json）");
        }
        catch (Exception e)
        {
            message = "打开文件夹选择框失败：" + e.Message + "；也可以把路径粘贴到下面「额外模型目录」";
            await InvokeAsync(StateHasChanged);
            return;
        }

        if (picked == null || picked.Length == 0)
        {
            message = "没有选择目录（若弹窗没有出现，把路径粘贴到下面「额外模型目录」后回车）";
            await InvokeAsync(StateHasChanged);
            return;
        }

        Configuration.ExtraModelPaths = AppendPath(Configuration.ExtraModelPaths, picked[0]);
        Sync();
        RefreshModels();
        message = models.Count == 0
            ? "该目录及子目录内没有找到 *.model3.json"
            : $"已添加模型目录：{picked[0]}，在上面的列表里点选即可切换";
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>没有模型时一键拉取官方示例模型（与官方桌宠共用缓存）。</summary>
    async Task DownloadSamplesAsync()
    {
        message = "正在下载官方示例模型…";
        await InvokeAsync(StateHasChanged);
        try { await EmotionLive2DPet.EnsureDefaultModelsAsync(); }
        catch (Exception e) { message = "下载失败：" + e.Message; }

        RefreshModels();
        if (models.Count > 0)
            message = $"已就绪：找到 {models.Count} 个模型，点一下即可切换";
        else if (message.StartsWith("下载失败", StringComparison.Ordinal) == false)
            message = "下载没有成功（可能网络不可用）。请手动把模型文件夹放进默认目录，或用「浏览模型文件夹…」指定任意目录";
        await InvokeAsync(StateHasChanged);
    }

    static string AppendPath(string? current, string folder)
    {
        List<string> list = (current ?? "")
            .Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();
        if (!list.Any(p => string.Equals(p, folder, StringComparison.OrdinalIgnoreCase)))
            list.Add(folder);
        return string.Join(";", list);
    }

    void OpenCurrentModelFolder()
    {
        string? dir = null;
        try { dir = Module?.CurrentModelDirectory; } catch { }
        if (string.IsNullOrWhiteSpace(dir) || Directory.Exists(dir) == false)
            dir = EmotionLive2DPet.ModelRoot;

        bool ok = EmotionLive2DPet.OpenFolderPath(dir);
        message = ok ? "已在资源管理器中打开：" + dir : "无法打开目录：" + dir;
    }

    void OpenModelsFolder()
    {
        bool ok = EmotionLive2DPet.OpenFolderPath(EmotionLive2DPet.ModelRoot);
        message = ok ? "已在资源管理器中打开默认模型目录" : "无法打开默认模型目录";
    }

    // ---------------- 渲染 ----------------

    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        if (Configuration is null) return;
        EnsureModels();

        int s = 0;
        b.OpenElement(s++, "section");
        b.AddAttribute(s++, "class", "el2d");
        b.AddMarkupContent(s++, "<style>" + Styles + "</style>");

        // ---- 标题 ----
        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "class", "el2d-hero");
        b.OpenElement(s++, "div");
        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "class", "el2d-title");
        b.AddContent(s++, "Live2D 具身桌宠");
        b.CloseElement();
        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "class", "el2d-sub");
        b.AddContent(s++, "情绪驱动表情、身体小动作与口型；换模型、调强度都在这一页完成，改动即时生效。");
        b.CloseElement();
        b.CloseElement();
        b.CloseElement();

        // ---- 模型 ----
        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "class", "el2d-card");

        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "class", "el2d-card-head");
        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "class", "el2d-card-title");
        b.AddContent(s++, "模型");
        b.CloseElement();
        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "class", "el2d-actions");
        Button(b, ref s, "刷新列表", false, RefreshModels);
        Button(b, ref s, "浏览模型文件夹…", true, () => { _ = BrowseModelsAsync(); });
        Button(b, ref s, "打开模型目录", false, OpenCurrentModelFolder);
        b.CloseElement();
        b.CloseElement();

        PetModelEntry? active = ActiveModel();
        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "class", "el2d-current");
        b.AddContent(s++, "当前：");
        b.OpenElement(s++, "strong");
        b.AddContent(s++, active?.Name ?? (string.IsNullOrWhiteSpace(Configuration.ModelName) ? "未选择" : Configuration.ModelName));
        b.CloseElement();
        if (active != null)
            b.AddContent(s++, "  ·  " + active.Location);
        b.CloseElement();

        if (models.Count > 6)
        {
            b.OpenElement(s++, "input");
            b.AddAttribute(s++, "type", "text");
            b.AddAttribute(s++, "class", "el2d-search");
            b.AddAttribute(s++, "placeholder", "搜索模型名称或路径");
            b.AddAttribute(s++, "value", modelFilter);
            b.AddAttribute(s++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
            {
                modelFilter = e.Value?.ToString() ?? "";
            }));
            b.CloseElement();
        }

        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "class", "el2d-models");
        int shown = 0;
        foreach (PetModelEntry entry in VisibleModels())
        {
            shown++;
            bool isActive = IsActive(entry);
            bool isLoading = switching && isActive;

            b.OpenElement(s++, "button");
            b.AddAttribute(s++, "type", "button");
            b.AddAttribute(s++, "class", "el2d-model" + (isActive ? " active" : ""));
            b.AddAttribute(s++, "disabled", switching);
            b.AddAttribute(s++, "title", entry.Directory);
            b.AddAttribute(s++, "onclick", EventCallback.Factory.Create(this, () => { _ = PickModelAsync(entry); }));

            b.OpenElement(s++, "span");
            b.AddAttribute(s++, "class", "el2d-model-name");
            b.AddContent(s++, isLoading ? entry.Name + "（载入中…）" : entry.Name);
            b.CloseElement();

            b.OpenElement(s++, "span");
            b.AddAttribute(s++, "class", "el2d-model-path");
            b.AddContent(s++, entry.Location);
            b.CloseElement();

            b.CloseElement();
        }
        b.CloseElement();

        if (shown == 0)
        {
            b.OpenElement(s++, "div");
            b.AddAttribute(s++, "class", "el2d-empty");
            b.AddContent(s++, models.Count == 0
                ? "还没有找到模型。把模型文件夹放进默认目录，或点右上角「浏览模型文件夹…」指向任意目录。"
                : "没有匹配的模型，换个关键词试试。");
            b.CloseElement();

            if (models.Count == 0)
            {
                b.OpenElement(s++, "div");
                b.AddAttribute(s++, "class", "el2d-actions");
                b.AddAttribute(s++, "style", "margin-bottom:10px");
                Button(b, ref s, "下载示例模型", true, () => { _ = DownloadSamplesAsync(); });
                b.CloseElement();
            }
        }

        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "class", "el2d-model-foot");
        Button(b, ref s, "打开默认模型目录", false, OpenModelsFolder);
        b.OpenElement(s++, "span");
        b.AddAttribute(s++, "class", "el2d-hint-inline");
        b.AddContent(s++, "默认目录：" + EmotionLive2DPet.ModelRoot);
        b.CloseElement();
        b.CloseElement();

        TextInput(b, ref s, "额外模型目录", Configuration.ExtraModelPaths, v =>
        {
            Configuration.ExtraModelPaths = v;
            Sync();
            RefreshModels();
        }, 0, "多个目录用分号或换行分隔；换模型时会递归查找目录下所有 *.model3.json。");
        b.CloseElement();

        // ---- 表演 ----
        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "class", "el2d-grid");

        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "class", "el2d-card");
        CardTitle(b, ref s, "表演");
        RangeInput(b, ref s, "表情强度", Configuration.ExpressionIntensity, 0.2, 2.0, 0.05,
            v => { Configuration.ExpressionIntensity = v; Sync(); }, "越大，FACS 表情越明显。");
        RangeInput(b, ref s, "身体动作幅度", Configuration.BodyMotionGain, 0.2, 2.0, 0.05,
            v => { Configuration.BodyMotionGain = v; Sync(); }, "影响头部摆动、身体倾斜与呼吸。");
        BoolInput(b, ref s, "自动情绪表演", Configuration.AutoEmotionFromChat,
            v => { Configuration.AutoEmotionFromChat = v; Sync(); },
            "根据 AI 回复与你的输入，本地推断情绪并表演。");
        BoolInput(b, ref s, "待机小动作", Configuration.IdleMotionEnabled,
            v => { Configuration.IdleMotionEnabled = v; Sync(); },
            "空闲时呼吸、眨眼、视线游移与低频小动作。");
        BoolInput(b, ref s, "视线跟随鼠标", Configuration.GazeFollowEnabled,
            v => { Configuration.GazeFollowEnabled = v; Sync(); },
            "视线与头部轻微跟随鼠标。");
        b.CloseElement();

        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "class", "el2d-card");
        CardTitle(b, ref s, "语音与字幕");
        BoolInput(b, ref s, "字幕", Configuration.ShowSubtitle,
            v => { Configuration.ShowSubtitle = v; Sync(); },
            "AI 回复时在窗口底部显示字幕条，不会挡住角色。");
        BoolInput(b, ref s, "语音口型同步", Configuration.SpeechLipSyncEnabled,
            v => { Configuration.SpeechLipSyncEnabled = v; Sync(); },
            "使用 Alife 语音插件播放时的真实音量包络驱动口型；未安装语音插件时自动改用说话口型。");
        BoolInput(b, ref s, "记住情绪", Configuration.PersistEmotion,
            v => { Configuration.PersistEmotion = v; Sync(); },
            "重启后沿用上次的情绪状态（会自然衰减）。");
        b.CloseElement();

        b.CloseElement();

        // ---- 其余配置 ----
        if (RestProps.Length > 0)
        {
            b.OpenElement(s++, "div");
            b.AddAttribute(s++, "class", "el2d-card");
            CardTitle(b, ref s, "更多设置");
            b.OpenElement(s++, "div");
            b.AddAttribute(s++, "class", "el2d-hint");
            b.AddContent(s++, "以下选项与模型资源解析相关，修改后会立即重新解析并重载当前模型，无需重启客户端。");
            b.CloseElement();
            foreach (PropertyInfo p in RestProps) RenderProperty(b, ref s, p);
            b.CloseElement();
        }

        b.OpenElement(s++, "p");
        b.AddAttribute(s++, "class", "el2d-status");
        b.AddContent(s++, message);
        b.CloseElement();

        b.CloseElement();
    }

    void RenderProperty(RenderTreeBuilder b, ref int s, PropertyInfo p)
    {
        string hint = p.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";
        Type t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
        object? cur;
        try { cur = p.GetValue(Configuration); } catch { return; }

        if (t == typeof(bool))
        {
            BoolInput(b, ref s, p.Name, cur is bool bv && bv, v => Set(p, v), hint);
            return;
        }
        if (t.IsEnum)
        {
            SelectInput(b, ref s, p.Name, Convert.ToString(cur, CultureInfo.InvariantCulture) ?? "",
                Enum.GetNames(t), v => Set(p, Enum.Parse(t, v)), hint);
            return;
        }
        if (t == typeof(string))
        {
            TextInput(b, ref s, p.Name, cur as string ?? "", v => Set(p, v), 0, hint);
            return;
        }
        if (t == typeof(double) || t == typeof(float) || t == typeof(int) || t == typeof(long))
        {
            double d = 0;
            try { d = Convert.ToDouble(cur, CultureInfo.InvariantCulture); } catch { }
            bool integral = t == typeof(int) || t == typeof(long);
            NumberInput(b, ref s, p.Name, d, integral ? 1 : 0.01,
                v => Set(p, integral ? (object)(int)Math.Round(v) : v), hint);
            return;
        }
        Text(b, ref s, p.Name + "：" + (Convert.ToString(cur, CultureInfo.InvariantCulture) ?? "（不支持在此编辑）"));
    }

    void Set(PropertyInfo p, object value)
    {
        try
        {
            p.SetValue(Configuration, value);
            message = "";
            Sync();
        }
        catch (Exception e) { message = p.Name + " 设置失败：" + e.Message; }
    }

    // ---- 渲染小工具 ----

    static void CardTitle(RenderTreeBuilder b, ref int s, string text)
    {
        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "class", "el2d-card-title");
        b.AddContent(s++, text);
        b.CloseElement();
    }

    static void Text(RenderTreeBuilder b, ref int s, string text)
    {
        b.OpenElement(s++, "p");
        b.AddAttribute(s++, "class", "el2d-hint");
        b.AddContent(s++, text);
        b.CloseElement();
    }

    void Button(RenderTreeBuilder b, ref int s, string label, bool primary, Action click)
    {
        b.OpenElement(s++, "button");
        b.AddAttribute(s++, "type", "button");
        b.AddAttribute(s++, "class", "el2d-btn" + (primary ? " primary" : ""));
        b.AddAttribute(s++, "onclick", EventCallback.Factory.Create(this, click));
        b.AddContent(s++, label);
        b.CloseElement();
    }

    void TextInput(RenderTreeBuilder b, ref int s, string label, string value, Action<string> change, int maxLen, string hint = "")
    {
        b.OpenElement(s++, "label");
        b.AddAttribute(s++, "class", "el2d-row");
        b.OpenElement(s++, "span");
        b.AddAttribute(s++, "class", "el2d-label");
        b.AddContent(s++, label);
        b.CloseElement();
        b.OpenElement(s++, "input");
        b.AddAttribute(s++, "type", "text");
        b.AddAttribute(s++, "value", value);
        if (maxLen > 0) b.AddAttribute(s++, "maxlength", maxLen);
        b.AddAttribute(s++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
        {
            string v = e.Value?.ToString() ?? "";
            if (maxLen > 0 && v.Length > maxLen) v = v.Substring(0, maxLen);
            change(v);
        }));
        b.CloseElement();
        b.CloseElement();
        if (hint.Length > 0) Text(b, ref s, hint);
    }

    void NumberInput(RenderTreeBuilder b, ref int s, string label, double value, double step, Action<double> change, string hint = "")
    {
        b.OpenElement(s++, "label");
        b.AddAttribute(s++, "class", "el2d-row");
        b.OpenElement(s++, "span");
        b.AddAttribute(s++, "class", "el2d-label");
        b.AddContent(s++, label);
        b.CloseElement();
        b.OpenElement(s++, "input");
        b.AddAttribute(s++, "type", "number");
        b.AddAttribute(s++, "step", step);
        b.AddAttribute(s++, "value", value.ToString(CultureInfo.InvariantCulture));
        b.AddAttribute(s++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
        {
            if (double.TryParse(e.Value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double n)
                && double.IsFinite(n))
                change(n);
        }));
        b.CloseElement();
        b.CloseElement();
        if (hint.Length > 0) Text(b, ref s, hint);
    }

    void RangeInput(RenderTreeBuilder b, ref int s, string label, double value, double min, double max, double step,
        Action<double> change, string hint = "")
    {
        b.OpenElement(s++, "label");
        b.AddAttribute(s++, "class", "el2d-row el2d-range-row");
        b.OpenElement(s++, "span");
        b.AddAttribute(s++, "class", "el2d-label");
        b.AddContent(s++, label);
        b.CloseElement();
        b.OpenElement(s++, "span");
        b.AddAttribute(s++, "class", "el2d-range-wrap");
        b.OpenElement(s++, "input");
        b.AddAttribute(s++, "type", "range");
        b.AddAttribute(s++, "min", min.ToString(CultureInfo.InvariantCulture));
        b.AddAttribute(s++, "max", max.ToString(CultureInfo.InvariantCulture));
        b.AddAttribute(s++, "step", step.ToString(CultureInfo.InvariantCulture));
        b.AddAttribute(s++, "value", value.ToString(CultureInfo.InvariantCulture));
        b.AddAttribute(s++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
        {
            if (double.TryParse(e.Value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double n)
                && double.IsFinite(n))
                change(Math.Clamp(n, min, max));
        }));
        b.CloseElement();
        b.OpenElement(s++, "span");
        b.AddAttribute(s++, "class", "el2d-val");
        b.AddContent(s++, value.ToString("0.##", CultureInfo.InvariantCulture));
        b.CloseElement();
        b.CloseElement();
        b.CloseElement();
        if (hint.Length > 0) Text(b, ref s, hint);
    }

    void BoolInput(RenderTreeBuilder b, ref int s, string label, bool value, Action<bool> change, string hint = "")
    {
        b.OpenElement(s++, "label");
        b.AddAttribute(s++, "class", "el2d-row el2d-toggle-row");
        b.OpenElement(s++, "span");
        b.AddAttribute(s++, "class", "el2d-label");
        b.AddContent(s++, label);
        b.CloseElement();
        b.OpenElement(s++, "span");
        b.AddAttribute(s++, "class", "el2d-switch" + (value ? " on" : ""));
        b.OpenElement(s++, "input");
        b.AddAttribute(s++, "type", "checkbox");
        b.AddAttribute(s++, "checked", value);
        b.AddAttribute(s++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e => change(e.Value is true)));
        b.CloseElement();
        b.OpenElement(s++, "i");
        b.CloseElement();
        b.CloseElement();
        b.CloseElement();
        if (hint.Length > 0) Text(b, ref s, hint);
    }

    void SelectInput(RenderTreeBuilder b, ref int s, string label, string value, string[] options, Action<string> change, string hint)
    {
        b.OpenElement(s++, "label");
        b.AddAttribute(s++, "class", "el2d-row");
        b.OpenElement(s++, "span");
        b.AddAttribute(s++, "class", "el2d-label");
        b.AddContent(s++, label);
        b.CloseElement();
        b.OpenElement(s++, "select");
        b.AddAttribute(s++, "value", value);
        b.AddAttribute(s++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e => change(e.Value?.ToString() ?? "")));
        foreach (string option in options)
        {
            b.OpenElement(s++, "option");
            b.AddAttribute(s++, "value", option);
            b.AddAttribute(s++, "selected", option == value);
            b.AddContent(s++, option);
            b.CloseElement();
        }
        b.CloseElement();
        b.CloseElement();
        if (hint.Length > 0) Text(b, ref s, hint);
    }

    const string Styles = """
        .el2d{box-sizing:border-box;width:100%;max-width:860px;margin:8px auto;padding:22px 26px;font:14px/1.6 'Segoe UI','Microsoft YaHei',sans-serif;color:var(--text-color,#2f3240);background:var(--background-color,#fbfcff);border:1px solid #5b6fff1f;border-radius:20px;box-shadow:0 10px 30px #1b23500a}
        .el2d .el2d-hero{display:flex;align-items:flex-start;justify-content:space-between;gap:16px;margin-bottom:4px}
        .el2d .el2d-title{font-size:22px;font-weight:650;letter-spacing:.01em;color:#5b6fff}
        .el2d .el2d-sub{font-size:12px;opacity:.66;margin-top:4px}
        .el2d .el2d-card{margin-top:18px;padding:16px 18px 18px;border:1px solid #5b6fff1f;border-radius:16px;background:#5b6fff08}
        .el2d .el2d-card-head{display:flex;align-items:center;justify-content:space-between;gap:12px;flex-wrap:wrap;margin-bottom:10px}
        .el2d .el2d-card-title{font-size:13.5px;font-weight:650;letter-spacing:.02em;color:#5b6fff;margin-bottom:10px}
        .el2d .el2d-card-head .el2d-card-title{margin-bottom:0}
        .el2d .el2d-actions{display:flex;gap:8px;flex-wrap:wrap}
        .el2d .el2d-btn{box-sizing:border-box;padding:6px 12px;font:inherit;font-size:12px;color:inherit;background:transparent;border:1px solid #5b6fff40;border-radius:9px;cursor:pointer;transition:background .15s,border-color .15s}
        .el2d .el2d-btn:hover{background:#5b6fff18;border-color:#5b6fff80}
        .el2d .el2d-btn.primary{color:#fff;background:#5b6fff;border-color:#5b6fff}
        .el2d .el2d-btn.primary:hover{background:#4a5df0}
        .el2d .el2d-current{font-size:12.5px;opacity:.72;margin:2px 0 10px}
        .el2d .el2d-current strong{color:#5b6fff;font-weight:650}
        .el2d .el2d-search{box-sizing:border-box;width:100%;margin-bottom:10px;padding:8px 11px;font:inherit;font-size:12.5px;color:inherit;background:var(--background-color,#fff);border:1px solid #5b6fff33;border-radius:10px;outline:none}
        .el2d .el2d-search:focus{border-color:#5b6fff99}
        .el2d .el2d-models{display:grid;grid-template-columns:repeat(auto-fill,minmax(228px,1fr));gap:9px;max-height:300px;overflow-y:auto;padding:2px}
        .el2d .el2d-models::-webkit-scrollbar{width:6px}
        .el2d .el2d-models::-webkit-scrollbar-thumb{background:#5b6fff40;border-radius:3px}
        .el2d .el2d-model{display:flex;flex-direction:column;align-items:flex-start;gap:2px;width:100%;box-sizing:border-box;padding:9px 11px;text-align:left;font:inherit;color:inherit;background:transparent;border:1px solid #5b6fff2e;border-radius:12px;cursor:pointer;transition:background .15s,border-color .15s}
        .el2d .el2d-model:hover{background:#5b6fff12;border-color:#5b6fff66}
        .el2d .el2d-model.active{border-color:#5b6fff;background:#5b6fff16;box-shadow:inset 3px 0 0 #5b6fff}
        .el2d .el2d-model[disabled]{cursor:progress;opacity:.75}
        .el2d .el2d-model-name{font-size:13px;font-weight:600}
        .el2d .el2d-model-path{font-size:10.5px;opacity:.55;line-height:1.35;word-break:break-all}
        .el2d .el2d-empty{padding:14px 4px;font-size:12px;opacity:.6}
        .el2d .el2d-model-foot{display:flex;align-items:center;gap:10px;flex-wrap:wrap;margin-top:10px}
        .el2d .el2d-hint-inline{font-size:10.5px;opacity:.45;word-break:break-all}
        .el2d .el2d-grid{display:grid;grid-template-columns:1fr 1fr;gap:16px;align-items:start}
        .el2d .el2d-grid .el2d-card{margin-top:0}
        @media(max-width:720px){.el2d .el2d-grid{grid-template-columns:1fr}.el2d .el2d-models{grid-template-columns:1fr}}
        .el2d .el2d-row{display:flex;align-items:center;justify-content:space-between;gap:14px;margin:11px 0;font-size:13px}
        .el2d .el2d-row>.el2d-label{flex:0 0 auto;max-width:52%;line-height:1.35}
        .el2d .el2d-row>input[type=text],.el2d .el2d-row>input[type=number],.el2d .el2d-row>select{box-sizing:border-box;width:300px;max-width:58%;padding:7px 10px;font:inherit;font-size:12.5px;color:inherit;background:var(--background-color,#fff);border:1px solid #5b6fff38;border-radius:9px;outline:none}
        .el2d .el2d-row>input:focus,.el2d .el2d-row>select:focus{border-color:#5b6fff99;box-shadow:0 0 0 3px #5b6fff15}
        .el2d .el2d-range-row{align-items:center}
        .el2d .el2d-range-wrap{display:flex;align-items:center;gap:8px;flex:1 1 auto;min-width:0}
        .el2d .el2d-range-wrap input[type=range]{flex:1 1 auto;min-width:0;accent-color:#5b6fff;cursor:pointer}
        .el2d .el2d-val{flex:0 0 auto;min-width:34px;text-align:right;font-size:11.5px;color:#5b6fff;font-variant-numeric:tabular-nums}
        .el2d .el2d-toggle-row{cursor:pointer}
        .el2d .el2d-switch{position:relative;flex:0 0 42px;width:42px;height:22px;border-radius:11px;background:#5b6fff2e;transition:background .18s}
        .el2d .el2d-switch.on{background:#5b6fff}
        .el2d .el2d-switch input{position:absolute;inset:0;width:100%;height:100%;margin:0;opacity:0;cursor:pointer}
        .el2d .el2d-switch i{position:absolute;left:2px;top:2px;width:18px;height:18px;border-radius:50%;background:#fff;box-shadow:0 1px 3px #0003;transition:transform .18s;pointer-events:none}
        .el2d .el2d-switch.on i{transform:translateX(20px)}
        .el2d .el2d-hint{font-size:11px;line-height:1.6;opacity:.55;margin:4px 0 8px}
        .el2d .el2d-status{min-height:20px;margin-top:16px;font-size:12.5px;color:#5b6fff}
        """;
}
