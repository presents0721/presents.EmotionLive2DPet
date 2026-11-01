using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using Alife.Client;
using Alife.Foundation;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Alife.Function.Speech;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace presents.EmotionLive2DPet;

/// <summary>聊天输入上行事件回调（由桌宠实现方注册，转发给 AI）。</summary>
public delegate void InputEventCallback(string text);

/// <summary>交互上行事件回调（由桌宠实现方注册，转发给 AI）。</summary>
public delegate void InteractedEventCallback(string text);

/// <summary>重启后恢复的情绪记忆。</summary>
public class EmotionMemory
{
    public string Emotion { get; set; } = "neutral";
    public double Intensity { get; set; }
    public long AtUnixMs { get; set; }
}

/// <summary>
/// Live2D 具身桌宠：增强版底层（窗口/模块/模型管理）+ Soullink 情绪表演层。
/// 情绪识别走本地文本分类器，语音合成复用 Alife 语音插件，AI 可通过工具直接指定情绪、表情与动作。
/// </summary>
[Module("Live2D 具身桌宠",
    """
    情绪驱动的 Live2D 桌宠（Cubism 3+），基于增强版桌宠底层。
    自动把 AI 回复转换为连续情绪、表情、身体小动作与口型；支持窗口内缩放/穿透/区域点击/动作表情面板。
    模型放在 Storage/Live2DDeskPet/Models 下，可与官方桌宠共用模型目录。
    """,
    defaultCategory: "presents/桌宠",
    EditorUI = typeof(EmotionLive2DPetUI))]
public partial class EmotionLive2DPet(
    PluginSystem pluginSystem,
    StorageSystem storageSystem,
    XmlFunctionCaller functionCaller,
    Interactor<EmotionLive2DPet> interactor,
    IAlifeApplication alifeApplication,
    ILogger<EmotionLive2DPet> logger,
    ISpeechService? speechService = null) :
    ChatBehaviour,
    IConfigurable<EmotionLive2DPetConfig>
{
    public EmotionLive2DPetConfig Configuration { get; set; } = new();

    public static string ModelRoot { get; } = Path.Combine(AlifePath.StorageFolderPath, "Live2DDeskPet", "Models");

    /// <summary>没有模型时下载官方示例模型包（与官方桌宠共用缓存）。</summary>
    public static async Task EnsureDefaultModelsAsync()
    {
        if (Directory.Exists(ModelRoot) && Directory.GetDirectories(ModelRoot).Length > 0)
            return;

        const string ModelsZipUrl =
            "https://github.com/BDFFZI/Alife.OfficialPluginStorage/raw/refs/heads/main/Alife.Function.DeskPet/Live2DModel/1.0.0.zip";
        Directory.CreateDirectory(ModelRoot);
        try
        {
            await AlifeUtility.DownloadZipFileAsync(ModelsZipUrl, ModelRoot);
            AlifeLog.LogInformation("已下载默认 Live2D 桌宠模型到 " + ModelRoot);
        }
        catch (Exception ex)
        {
            AlifeLog.LogWarning("下载默认 Live2D 桌宠模型失败\n" + ex);
        }
    }

    /// <summary>
    /// 启动期等待下载示例模型，但**必须有上限**：网络不通时不能把整个插件启动流程挂住，
    /// 否则客户端会一直停在「加载本地插件」。超时后下载继续在后台跑，下次启动就能用上。
    /// </summary>
    static async Task TryWarmupModelsAsync(TimeSpan timeout)
    {
        if (Directory.Exists(ModelRoot) && Directory.GetDirectories(ModelRoot).Length > 0)
            return;

        Task download = EnsureDefaultModelsAsync();
        try
        {
            await download.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            AlifeLog.LogWarning($"下载示例模型超过 {timeout.TotalSeconds:0} 秒，先继续启动插件（后台会接着下载）");
        }
        catch (Exception ex)
        {
            AlifeLog.LogWarning("准备示例模型时出错：" + ex.Message);
        }
    }

    // ---- 运行期组件 ----
    PetModelMetadata metadata = null!;
    ServiceProvider provider = null!;
    PetModelContext modelContext = null!;
    PetConfigContext configContext = null!;
    PetWindow window = null!;
    PetBridge bridge = null!;
    EmbodimentModule embodiment = null!;
    SpeechEnvelopeModule speechModule = null!;
    SubtitleModule subtitleModule = null!;
    ExpressionModule expressionModule = null!;
    UsingModule usingModule = null!;
    RegionInteractionModule regionModule = null!;
    OutfitModule outfitModule = null!;

    /// <summary>换模型串行化，避免连点导致两次加载交错。</summary>
    readonly SemaphoreSlim switchLock = new(1, 1);

    XmlHandler? xmlHandler;
    string lastEmotion = "neutral";
    double lastIntensity = 0.4;
    long lastSaveTicks;
    string memoryKey = string.Empty;
    bool lastChatOccupied;

    // 这三个配置只在解析模型资源时生效，改动后需要重新解析并重载模型，否则 UI 看起来「点了没用」
    bool assetSettingsReady;
    bool lastMergeAssets;
    bool lastHideWatermark;
    string lastWatermarkOverride = "";

    /// <summary>模型切换完成事件（参数为新模型名）。</summary>
    public event Action<string>? ModelSwitched;

    /// <summary>桌宠窗口内的输入框提交的文字（转发给 AI）。</summary>
    public event Action<string>? OnInput;

    /// <summary>点击/手势等互动产生的上行事件（转发给 AI）。</summary>
    public event Action<string>? OnInteracted;

    protected override async Task OnAwake()
    {
        memoryKey = $"{Character.StorageKey}/EmotionLive2DPet/Emotion";

        await TryWarmupModelsAsync(TimeSpan.FromSeconds(12));
        string modelName = Configuration.ModelName?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(modelName))
        {
            //市场版不带任何模型：没配置就自动用找到的第一个（默认模型目录优先），避免写死某个作者模型
            modelName = EnumerateModels().FirstOrDefault()?.Identifier ?? "";
            if (string.IsNullOrWhiteSpace(modelName))
                logger.LogWarning("没有找到 Live2D 模型，请把 Cubism 3+ 模型放进 {Root} 或设置页指定目录", ModelRoot);
        }
        Configuration.ModelName = modelName;
        metadata = LoadMetadata(modelName);
        CaptureAssetSettings();

        provider = BuildProvider(out Type[] moduleTypes);
        try
        {
            window = provider.GetRequiredService<PetWindow>();
            bridge = provider.GetRequiredService<PetBridge>();

            // 前端日志转发：pet.js 会把 console.warn/error 与运行时报错发过来
            bridge.OnMessage += (type, payload) =>
            {
                if (type != "log") return;
                string level = payload.TryGetProperty("level", out JsonElement levelProp) ? levelProp.GetString() ?? "" : "";
                string text = payload.TryGetProperty("text", out JsonElement textProp) ? textProp.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(text)) return;
                if (level is "error" or "resource-error" or "unhandledrejection")
                    AlifeLog.LogWarning($"[桌宠前端/{level}] {text}");
                else
                    logger.LogInformation($"[桌宠前端/{level}] {text}");
            };

            string pluginDir = pluginSystem.PluginContext.GetPluginDirectoryPath("presents.EmotionLive2DPet");
            string? wwwRoot = ResolveWebRoot(pluginDir);
            if (wwwRoot == null)
                throw new DirectoryNotFoundException($"找不到桌宠网页资源 Resources/Live2DDeskPet/index.html（插件目录：{pluginDir}）");
            await window.CreateAsync(wwwRoot);

            await LoadModelAsync();
            await LoadModuleAsync(moduleTypes);

            embodiment = provider.GetRequiredService<EmbodimentModule>();
            speechModule = provider.GetRequiredService<SpeechEnvelopeModule>();
            subtitleModule = provider.GetRequiredService<SubtitleModule>();
            expressionModule = provider.GetRequiredService<ExpressionModule>();
            usingModule = provider.GetRequiredService<UsingModule>();
            regionModule = provider.GetRequiredService<RegionInteractionModule>();
            outfitModule = provider.GetRequiredService<OutfitModule>();

            // 字幕显隐同步说话状态：没有语音包络时也能产生自然的噪声口型
            subtitleModule.Showed += () => embodiment.SetSpeaking(true);
            subtitleModule.Hid += () => embodiment.SetSpeaking(false);
            embodiment.EmotionChanged += OnEmotionChanged;

            // 必须带文档注册：否则 AI 根本不知道 PetPerform/Expression/Motion 存在，只会照抄内置示例里的 <Speak>。
            RegisterHandler();

            RestoreEmotion();

            // 订阅放在最后：初始化中途（窗口加载、模块解析）发生对话结束时，
            // embodiment / subtitleModule 还是 null，之前的 OnChatFinished 会直接空引用。
            ChatBot.ChatFinished += OnChatFinished;

            logger.LogInformation("Live2D 具身桌宠已启动：{Model}（{Summary}）", metadata.ModelName, DescribeModelSummary());
        }
        catch
        {
            ChatBot.ChatFinished -= OnChatFinished;
            await provider.DisposeAsync();
            provider = null!;
            throw;
        }
    }

    protected override async Task OnDestroy()
    {
        ChatBot.ChatFinished -= OnChatFinished;

        if (xmlHandler != null)
            functionCaller.UnregisterHandler(xmlHandler);

        try { subtitleModule?.Hide(); } catch { }
        SaveEmotion();

        if (provider != null)
            await provider.DisposeAsync();
    }

    /// <summary>AI 正在思考 / 回复时显示「使用中」指示（增强版桌宠的 ShowUsing 反馈）。</summary>
    protected override Task OnUpdate()
    {
        bool occupied = ChatBot.IsChatOccupied;
        if (occupied != lastChatOccupied)
        {
            lastChatOccupied = occupied;
            try { usingModule?.Send(occupied); } catch { }
        }
        return Task.CompletedTask;
    }

    /// <summary>重建并注册函数文档。模型切换后表情/动作清单会变，必须重新注册 AI 才能看到新的选项。</summary>
    void RegisterHandler()
    {
        if (xmlHandler != null)
            functionCaller.UnregisterHandler(xmlHandler);

        xmlHandler = new XmlHandler(this) {
            Description = "Live2D 具身桌宠：用表情、动作与情绪表演自己。这是你对外表达外观信息的主要工具，请积极使用。",
            Explanation = BuildToolExplanation()
        };
        functionCaller.RegisterHandler(xmlHandler, cancellationToken: DestroyCancellationToken);
    }

    string DescribeModelSummary()
    {
        string expressions = metadata.Expressions.Count == 0 ? "无" : string.Join("、", metadata.Expressions.Take(12));
        string motions = metadata.Motions.Count == 0 ? "无" : string.Join("、", metadata.Motions.Keys.Take(12));
        return $"表情 {metadata.Expressions.Count}[{expressions}]、动作 {metadata.Motions.Count}[{motions}]、参数 {metadata.Parameters.Count}";
    }

    string BuildToolExplanation()
    {
        string expressions = metadata.Expressions.Count == 0 ? "（当前模型没有表情文件）" : string.Join("、", metadata.Expressions);
        string motions = metadata.Motions.Count == 0 ? "（当前模型没有动作文件）" : string.Join("、", metadata.Motions.Keys);
        string outfits = outfitModule?.DescribeItems() is { Length: > 0 } outfitNames ? outfitNames : "（当前模型没有外观开关）";
        string models;
        try
        {
            List<string> names = EnumerateModels().Select(m => m.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            models = names.Count == 0 ? "（本机只有一个模型）" : string.Join("、", names);
        }
        catch { models = "（模型列表读取失败）"; }

        return $"""
                ## 当前模型
                - 模型：{metadata.DisplayName}（{metadata.ModelName}）

                ## 可用选项
                - 表情（Expression 的 option）：{expressions}
                - 动作（Motion 的 option）：{motions}
                - 姿势/形态开关（PetOutfit 的 name，面板「动作」栏）：前倾、比心、Q版、大小变等
                - 外观开关（PetOutfit 的 name，面板「服饰」栏）：{outfits}
                - 情绪（PetPerform 的 emotion）：happy / sad / angry / surprised / confused / shy / relaxed / excited / proud / bored / neutral
                - 可切换的模型（SwitchModel 的 name）：{models}

                ## 外观与表演面板
                - 右键桌宠 →「外观与表演…」打开面板，自动识别当前模型的所有开关，分「表情 / 动作 / 服饰」三栏列出，点一下即可固定。
                - 面板顶部「具身表演」是总开关：关掉后情绪层不再逐帧改参数，用户固定的表情、动作、服饰会原样一直保持；
                  重新打开则恢复自动情绪表演，固定的表情仍会保留、其余通道继续自动变化。

                ## 使用方式（原生优先，合成兜底）
                - **模型「表情 / 动作」列表里有合适的，就先用 <Expression option="..." /> / <Motion option="..." />。**
                  作者画的爱心眼、黑脸、流泪、圈圈眼等比逐帧合成更像这个角色，不要再用 PetFace 仿一个；
                  动作列表里的举手、挥手、竖手指等也优先用模型原生动作。
                  例：开心且模型有「爱心眼」→ `<Expression option="爱心眼" />`；生气有「黑脸」→ `<Expression option="黑脸" />`；
                  想做姿态切换 → `<Motion option="默认手切举手" />`。
                - **模型没有对应表情、或需要情绪强弱变化时，再用 <PetPerform> / <PetFace> 合成：**
                  PetPerform 会先叠加该情绪对应的原生表情（若模型有），再用 FACS 逐帧合成五官，并叠加头身动作与口型；
                  不依赖 exp3 的模型也能演出来。可选 emotion 见上一节。
                - **想更细腻时用 <PetFace /> 直接填面部参数**（只改填了的通道，intensity 整体缩放）：
                  可用通道：eyeOpen、eyeSquint、eyeSmile、gazeX、gazeY、browInnerUp、browDown、browOuterUp、
                  mouthSmile、mouthFrown、mouthOpen、mouthPucker、blush、tear、sweat、headX、headY、headZ。
                  例：`<PetFace emotion="happy" intensity="0.9" eyeSmile="0.8" mouthSmile="1" headZ="-0.15" seconds="3" />`
                  例（皮笑肉不笑）：`<PetFace emotion="angry" browDown="0.8" mouthSmile="0.35" eyeSquint="0.4" />`
                - **模型没有的人设表情（傲娇/病娇/三无/吃醋…）**：先用 persona 预设再微调，或自己组合通道。
                  persona 可选：tsundere、yandere、kuudere、genki、shy、sleepy、proud、jealous。
                  例：`<PetFace persona="tsundere" intensity="0.9" mouthFrown="0.55" gazeX="-0.7" seconds="4" />`
                  例（病娇）：`<PetFace persona="yandere" intensity="0.85" expression="黑脸" seconds="5" />`
                  规律：傲娇=皱眉+嘴角下撇+脸红+视线躲开+歪头；病娇=睁大眼+笑眼+内眉上抬+淡笑+歪头+泪光；
                  每个角色的人设都不同，可以按人设自由组合这些通道；但模型列表里已有对应表情（如「黑脸」）时仍优先用它。
                - 原生表情/动作默认演一遍就收回：表情约 5 秒（体型/换装类状态表情会一直保持），
                  动作到自身时长后自动回默认姿势。**动作时长由你写 seconds 决定**：一般 1.5~3 秒，
                  想让她保持这个姿势就写 0 或负数；动作卡住不回去时，面板「动作」栏有「回到默认姿势」。
                - 想让她做一整段动作/手势用 <PetGesture name="wave" />：
                  wave 挥手、nod 点头、shakeHead 摇头、bow 鞠躬、leanIn 前倾、leanBack 后仰、
                  lookAround 张望、shrug 耸肩、handsUp 举双手、heart 比心、point 指向。
                  模型有同名原生手势动作时会优先用原生动作；没有手臂参数时只演头身层，不会报错。
                  情绪强烈时她也会自己挑手势，不必每句都写。
                  注意：同一时间只有一个动作槽，一条回复里不要连续发多个 <Motion>/<PetGesture>，
                  后一个会把前一个顶掉；表情可以和一个动作同时演，先写 <Expression> 再写 <Motion> 即可。
                  例：`<PetGesture name="wave" intensity="0.8" />`、`<PetGesture name="nod" />`。
                - 让整只人物在窗口里动起来用 <PetEffect name="jump" />（不依赖模型参数，任何模型都能用）：
                  jump 跳一下、hop 小跳两下、bounce 连续弹跳、shake 抖动、step 横向闪开、
                  leanIn 凑近、retreat 后退、turn 转身、tremble 发颤。
                  例：`<PetEffect name="hop" intensity="0.7" />`。
                - 表情/动作的 option 只能取上面列出的名字（大小写不敏感）；名字不存在时请改用 PetPerform 指定情绪，不要编造名字。
                - 需要换模型时用 <SwitchModel name="模型名" />；想知道她在屏幕哪里用 <Position />；想让她挪到别处用 <Move x="120" y="0" seconds="1" />。
                - 换装（穿脱外套、摘下帽子/发夹、隐藏兽耳/尾巴/裙摆/光环等）用 <PetOutfit name="外套" visible="false" />：
                  visible=false 表示脱下/隐藏，true 表示穿上/显示，不填则切换当前状态；name 优先取上面「外观开关」里的名字。
                  模型没在列表里但确实有对应表情时，也可以直接用表情原名，例如 `<PetOutfit name="隐藏耳朵" visible="true" />`。
                  例：`<PetOutfit name="外套" visible="false" />`、`<PetOutfit name="帽子" />`。
                - 桌宠窗口的回复按角色设定用 <Speak>台词</Speak> 包裹，会流式显示在底部字幕上。
                  <Speak> 里只放纯文本：不要嵌套 <Expression/>、<Motion/>、<PetGesture/>、<PetEffect/>、<PetPerform/> 等标签，也不要写 <!-- 注释 -->，
                  这些表演标签请写在 <Speak> 外面（前后都可以），否则解析器会报标签嵌套或孤儿闭标签错误。
                  即使忘记写 <Speak>，纯文本也会自动显示为字幕，但优先按格式使用 <Speak>。
                """;
    }

    #region 模型与窗口

    /// <summary>
    /// 解析模型目录。<paramref name="identifier"/> 支持三种写法：
    /// 1) Models 下的目录名或相对子路径；2) 任意绝对目录；3) 任意 *.model3.json 绝对路径。
    /// </summary>
    string ResolveModelDirectory(string? identifier = null)
    {
        string value = string.IsNullOrWhiteSpace(identifier) ? Configuration.ModelName : identifier;
        if (string.IsNullOrWhiteSpace(value))
            value = EnumerateModels().FirstOrDefault()?.Identifier ?? "";
        value = (value ?? "").Trim();

        if (Path.IsPathRooted(value))
        {
            if (File.Exists(value))
                return Path.GetDirectoryName(value)!;
            if (Directory.Exists(value))
                return value;
        }

        string combined = Path.Combine(ModelRoot, value);
        if (File.Exists(combined))
            return Path.GetDirectoryName(combined)!;
        if (Directory.Exists(combined))
            return combined;

        string? fallback = null;
        try
        {
            if (Directory.Exists(ModelRoot))
            {
                fallback = Directory.EnumerateDirectories(ModelRoot)
                    .FirstOrDefault(dir => Directory.EnumerateFiles(dir, "*.model3.json", SearchOption.TopDirectoryOnly).Any());
            }
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "扫描默认模型目录失败");
        }
        if (fallback != null)
        {
            logger.LogWarning("模型 {Value} 不存在，改用 {Fallback}", value, fallback);
            return fallback;
        }

        return combined;
    }

    /// <summary>当前模型所在目录。</summary>
    public string CurrentModelDirectory => metadata?.ModelDirectory ?? ResolveModelDirectory();

    /// <summary>当前模型名。</summary>
    public string CurrentModel => metadata?.DisplayName ?? Configuration.ModelName;

    /// <summary>递归枚举所有可用模型：默认模型目录 + 额外目录里的 *.model3.json。</summary>
    public List<PetModelEntry> EnumerateModels() => ScanModels(Configuration.ExtraModelPaths);

    /// <summary>
    /// 只依赖配置的静态扫描：设置页在插件尚未启动（模块实例不可用）时也能列出模型，
    /// 避免「刷新列表」这类按钮点了完全没反应。
    /// </summary>
    public static List<PetModelEntry> ScanModels(string? extraPaths)
    {
        List<PetModelEntry> result = new();
        HashSet<string> seenDirectories = new(StringComparer.OrdinalIgnoreCase);

        void AddFromRoot(string root, bool isBundled)
        {
            if (string.IsNullOrWhiteSpace(root) || Directory.Exists(root) == false) return;

            foreach (string modelJson in ScanModelJsonFiles(root))
            {
                string directory = Path.GetDirectoryName(modelJson)!;
                if (seenDirectories.Add(directory) == false) continue;

                string name = Path.GetFileName(directory);
                if (string.IsNullOrWhiteSpace(name))
                    name = Path.GetFileNameWithoutExtension(modelJson);

                string identifier;
                string location;
                if (isBundled)
                {
                    try
                    {
                        identifier = Path.GetRelativePath(ModelRoot, directory).Replace('\\', '/');
                        location = "内置 · " + identifier;
                    }
                    catch
                    {
                        identifier = directory;
                        location = directory;
                    }
                }
                else
                {
                    identifier = directory;
                    location = directory;
                }

                result.Add(new PetModelEntry(name, modelJson, directory, identifier, location, isBundled));
            }
        }

        AddFromRoot(ModelRoot, true);
        foreach (string extra in (extraPaths ?? "")
            .Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.Trim())
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { AddFromRoot(extra, false); }
            catch (Exception e) { AlifeLog.LogWarning("扫描模型目录失败：" + extra + "\n" + e.Message); }
        }

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    static IEnumerable<string> ScanModelJsonFiles(string root)
    {
        List<string> files = new();
        void Walk(string directory)
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(directory, "*.model3.json"))
                    files.Add(file);
            }
            catch { }

            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(directory); }
            catch { return; }

            foreach (string child in children)
            {
                string name = Path.GetFileName(child);
                if (string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase))
                    continue;
                Walk(child);
            }
        }

        Walk(root);
        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    /// <summary>按配置解析模型元数据（是否补登记未登记资源、是否去水印都在这里生效）。</summary>
    PetModelMetadata LoadMetadata(string identifierOrDirectory)
    {
        string directory = ResolveModelDirectory(identifierOrDirectory);
        return PetModelMetadata.Load(
            directory,
            Configuration.MergeUnregisteredAssets,
            Configuration.HideWatermark,
            Configuration.WatermarkParamOverride);
    }

    /// <summary>水印等固定参数的信封载荷。必须用小写属性名的匿名类型。</summary>
    object[] PinPayload() => metadata.Pins
        .Select(pin => (object)new { id = pin.Id, value = pin.Value })
        .ToArray();

    /// <summary>按模型配置要隐藏的部件 ID（来自模型目录的 _tsumi.hideparts.json）。</summary>
    string[] HidePartsPayload() => metadata.HideParts.ToArray();

    /// <summary>把当前配置写回配置文件（切换模型/设置页更新后持久化）。</summary>
    void PersistConfig()
    {
        try
        {
            string path = Path.Combine(AlifePath.StorageFolderPath, Character.StorageKey, "Configuration", $"{GetType().FullName}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(Configuration,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { AlifeLog.LogError(e); }
    }

    /// <summary>记录当前资源解析相关配置，用于判断是否需要重新解析模型。</summary>
    void CaptureAssetSettings()
    {
        lastMergeAssets = Configuration.MergeUnregisteredAssets;
        lastHideWatermark = Configuration.HideWatermark;
        lastWatermarkOverride = Configuration.WatermarkParamOverride ?? "";
        assetSettingsReady = true;
    }

    /// <summary>设置页改动后立即下发到前端（表情强度、待机、视线、口型等无需重启）。</summary>
    public void ApplyConfigChange()
    {
        if (configContext == null) return;
        configContext.Current = Configuration;
        try { embodiment?.SendSettings(); }
        catch (Exception e) { logger.LogDebug(e, "下发桌宠配置失败"); }
        PersistConfig();

        if (assetSettingsReady == false) return;
        bool assetsChanged =
            lastMergeAssets != Configuration.MergeUnregisteredAssets
            || lastHideWatermark != Configuration.HideWatermark
            || string.Equals(lastWatermarkOverride, Configuration.WatermarkParamOverride ?? "", StringComparison.Ordinal) == false;
        if (assetsChanged == false) return;

        // 表情/动作补登记、去水印都发生在解析模型资源时，必须重新解析并重载才会生效
        CaptureAssetSettings();
        _ = ReapplyAssetSettingsAsync();
    }

    /// <summary>重新解析当前模型的资源设置并原地重载（去水印、补登记未登记表情等）。</summary>
    async Task ReapplyAssetSettingsAsync()
    {
        if (provider == null || metadata == null) return;

        await switchLock.WaitAsync(DestroyCancellationToken);
        try
        {
            PetModelMetadata fresh = LoadMetadata(Configuration.ModelName);
            if (string.IsNullOrEmpty(fresh.ModelPath)) return;

            metadata.ReplaceWith(fresh);
            ApplyConfigChange();          // 新参数表要重新下发给前端；此时基线已更新，不会再次触发重载
            await LoadModelAsync();
            RegisterHandler();            // 表情/动作清单可能变化，重新告知 AI
            logger.LogInformation("已重新解析模型资源设置：{Summary}", DescribeModelSummary());
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "重新解析模型资源设置失败");
        }
        finally
        {
            switchLock.Release();
        }
    }

    /// <summary>打开系统文件夹选择框（设置页「浏览模型文件夹…」）。</summary>
    public async Task<string[]?> PickFolderAsync(string title)
    {
        string? folder = await alifeApplication.SelectFolderAsync();
        return string.IsNullOrEmpty(folder) ? null : new[] { folder };
    }

    /// <summary>
    /// 定位桌宠网页资源目录（含 index.html 的 Live2DDeskPet 目录）。
    /// 市场安装包可能多套了一层同名目录，导致插件根目录下的 Resources 不存在，
    /// 这里按「直接路径 → 递归查找」的顺序兜底，找不到返回 null。
    /// </summary>
    public static string? ResolveWebRoot(string? pluginDir)
    {
        if (string.IsNullOrWhiteSpace(pluginDir) || Directory.Exists(pluginDir) == false) return null;

        string direct = Path.Combine(pluginDir, "Resources", "Live2DDeskPet");
        if (File.Exists(Path.Combine(direct, "index.html"))) return direct;

        try
        {
            foreach (string dir in Directory.EnumerateDirectories(pluginDir, "Live2DDeskPet", SearchOption.AllDirectories))
            {
                if (File.Exists(Path.Combine(dir, "index.html")))
                {
                    AlifeLog.LogWarning($"桌宠资源不在预期位置，已回退到：{dir}");
                    return dir;
                }
            }
        }
        catch (Exception e)
        {
            AlifeLog.LogWarning("递归查找桌宠资源目录失败：" + e.Message);
        }
        return null;
    }

    /// <summary>在资源管理器中打开目录（目录不存在时先创建）。</summary>
    public bool OpenFolder(string? directory) => OpenFolderPath(directory);

    /// <summary>不依赖模块实例的打开目录，供设置页直接调用。</summary>
    public static bool OpenFolderPath(string? directory)
    {
        string target = string.IsNullOrWhiteSpace(directory) ? ModelRoot : directory;
        try { Directory.CreateDirectory(target); } catch { }
        if (Directory.Exists(target) == false) return false;
        try
        {
            System.Diagnostics.Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 热切换模型：解析新目录 → 解析元数据 → 重新下发参数表 → 让页面原地加载模型（秒切）。
    /// 加载失败会自动回滚到原模型，避免桌宠窗口空着。
    /// </summary>
    public async Task<bool> SwitchModelAsync(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier)) return false;

        await switchLock.WaitAsync(DestroyCancellationToken);
        try
        {
            string directory = ResolveModelDirectory(identifier);
            PetModelMetadata candidate = LoadMetadata(directory);
            if (string.IsNullOrEmpty(candidate.ModelPath))
            {
                logger.LogWarning("切换模型失败：{Directory} 下没有可用的 *.model3.json", directory);
                return false;
            }

            if (provider == null || metadata == null)
            {
                //还没启动：只记下选择，等 OnAwake 时生效
                metadata = candidate;
                Configuration.ModelName = identifier;
                return true;
            }

            string previousIdentifier = Configuration.ModelName;
            string previousDirectory = metadata.ModelDirectory;

            metadata.ReplaceWith(candidate);
            Configuration.ModelName = identifier;
            ApplyConfigChange();       //先让前端拿到新模型的参数表，再加载模型

            bool loaded = await LoadModelAsync();
            if (loaded == false)
            {
                logger.LogWarning("模型 {Name} 加载失败，已回滚到原模型", candidate.ModelName);
                metadata.ReplaceWith(LoadMetadata(previousDirectory));
                Configuration.ModelName = previousIdentifier;
                ApplyConfigChange();
                RegisterHandler();
                await LoadModelAsync();
                return false;
            }

            PersistConfig();
            RegisterHandler();          //新模型的表情/动作清单要立刻同步给 AI
            embodiment.Perform(lastEmotion, lastIntensity);
            ModelSwitched?.Invoke(candidate.ModelName);
            logger.LogInformation("已切换 Live2D 模型：{Model}（{Directory}）｜{Summary}", candidate.ModelName, directory, DescribeModelSummary());
            return true;
        }
        catch (Exception e)
        {
            logger.LogError(e, "切换 Live2D 模型失败");
            return false;
        }
        finally
        {
            switchLock.Release();
        }
    }

    ServiceProvider BuildProvider(out Type[] moduleTypes)
    {
        ServiceCollection services = new();
        services.AddSingleton(logger);
        services.AddSingleton(metadata);
        modelContext = new PetModelContext { Metadata = metadata };
        configContext = new PetConfigContext(Configuration);
        services.AddSingleton(modelContext);
        services.AddSingleton(configContext);
        services.AddSingleton(storageSystem);
        services.AddSingleton(alifeApplication);
        services.AddSingleton(new PetStorageKey(Character.StorageKey));
        services.AddSingleton<PetWindow>();
        services.AddSingleton<PetBridge>();
        services.AddSingleton<PetInteractionHub>();
        //窗口内「更换模型」面板只依赖这两个回调，避免模块反向依赖插件本体
        services.AddSingleton(new PetModelCatalog(
            () => EnumerateModels(),
            identifier => SwitchModelAsync(identifier)));
        services.AddSingleton<InputEventCallback>(text => OnInput?.Invoke(text));
        services.AddSingleton<InteractedEventCallback>(text => OnInteracted?.Invoke(text));
        OnInput += text =>
        {
            if (string.IsNullOrWhiteSpace(text) == false)
                interactor.Poke(text.Trim());
        };
        OnInteracted += text =>
        {
            if (string.IsNullOrWhiteSpace(text) == false)
                interactor.Poke(text.Trim());
        };

        //只要装了语音插件就注入：口型开关由前端配置实时控制，避免改配置后必须重启模块
        if (speechService != null)
            services.AddSingleton(speechService);

        moduleTypes = typeof(IPetModule).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false }
                           && typeof(IPetModule).IsAssignableFrom(type))
            .ToArray();
        foreach (Type moduleType in moduleTypes)
            services.AddSingleton(moduleType);

        return services.BuildServiceProvider();
    }

    async Task<bool> LoadModelAsync()
    {
        if (provider == null || metadata == null) return false;
        //没有模型时不要构造空 Uri：那会抛异常把整个模块启动流程打断，桌宠窗口也就永远不出现
        if (string.IsNullOrWhiteSpace(metadata.ModelPath) || File.Exists(metadata.ModelPath) == false)
        {
            logger.LogWarning("没有可加载的 Live2D 模型（{Model}），请把模型放进 {Root}",
                metadata.ModelName, ModelRoot);
            return false;
        }
        PetBridge petBridge = provider.GetRequiredService<PetBridge>();

        TaskCompletionSource<bool> loadedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(string type, JsonElement data)
        {
            if (type != "loaded") return;

            bool ok = data.TryGetProperty("ok", out JsonElement okProp) == false
                      || okProp.ValueKind != JsonValueKind.False;
            if (ok == false && data.TryGetProperty("error", out JsonElement errorProp))
                logger.LogWarning("前端加载模型失败：{Error}", errorProp.GetString());
            loadedTcs.TrySetResult(ok);
        }

        petBridge.OnMessage += Handler;
        try
        {
            petBridge.SendMessage("load", new
            {
                url = new Uri(metadata.ModelPath).AbsoluteUri,
                pins = PinPayload(),
                hideParts = HidePartsPayload(),
            });
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(DestroyCancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            return await loadedTcs.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("加载模型超时：{Model}", metadata.ModelName);
            return false;
        }
        finally
        {
            petBridge.OnMessage -= Handler;
        }
    }

    async Task LoadModuleAsync(Type[] moduleTypes)
    {
        IPetModule[] modules = moduleTypes.Select(provider.GetRequiredService).Cast<IPetModule>().ToArray();
        PetBridge petBridge = provider.GetRequiredService<PetBridge>();

        List<IPetModule> cssModules = modules.Where(m => m.CssCode != null).ToList();
        if (cssModules.Count > 0)
        {
            string css = string.Join("\n", cssModules.Select(m => m.CssCode));
            await petBridge.ExecuteJavaScriptAsync($"injectCSS({JsonSerializer.Serialize(css)})");
        }

        foreach (IPetModule module in modules.Where(m => m.HtmlCode != null))
            await petBridge.ExecuteJavaScriptAsync($"injectHTML({JsonSerializer.Serialize(module.HtmlCode)})");

        foreach (IPetModule module in modules.Where(m => m.JsCode != null))
            await petBridge.ExecuteJavaScriptAsync(module.JsCode!);

        foreach (IPetModule module in modules)
            module.OnStart();
    }

    // ---- 对外表演接口 ----

    public Task ShowUsing(bool isUsing)
    {
        usingModule.Send(isUsing);
        return Task.CompletedTask;
    }

    public Task ShowSubtitle(string? subtitle)
    {
        if (Configuration.ShowSubtitle == false) return Task.CompletedTask;
        if (string.IsNullOrEmpty(subtitle)) subtitleModule.Hide();
        else subtitleModule.Show(subtitle);
        return Task.CompletedTask;
    }

    public Task ShowExpression(string? expression, double? seconds = null)
    {
        if (string.IsNullOrWhiteSpace(expression)) return Task.CompletedTask;
        string wanted = expression.Trim();

        string? canonical = ResolveExpressionName(wanted);

        if (canonical != null)
        {
            // 体型/换装/姿势开关是状态，不是一次性表情：不填 seconds 时默认一直保持，
            // 否则几秒后自动收回，用户会看到「换完体型瞬间弹回去」。
            bool persistent = outfitModule?.IsPersistentExpression(canonical) == true;
            double? effectiveSeconds = seconds is null && persistent ? 0 : seconds;
            logger.LogInformation("播放原生表情 {Expression}（seconds={Seconds}）", canonical, effectiveSeconds);
            expressionModule.PlayExpression(canonical, effectiveSeconds);
            // 自带表情多为单参数开关（如「哭哭」只开一个泪目开关），单播几乎看不出变化；
            // 同时叠加一层 FACS 情绪表演，让眉/眼/嘴/脸颊真正动起来。
            // 把 canonical 一并传给 Perform，避免它按情绪另挑一个原生表情把刚播的顶掉。
            // effectiveSeconds<=0 是「一直保持」，走 ExpressionModule 的 hold 通道；不要再经过 Perform，
            // 否则具身层的 6 秒原生表情回收会把长按的表情收掉。
            string? overlay = ToEmotionKey(canonical) ?? ToEmotionKey(wanted);
            bool hold = effectiveSeconds is double value && value <= 0;
            if (overlay != null && hold == false) embodiment.Perform(overlay, 0.8, expression: canonical);
            return Task.CompletedTask;
        }

        // 模型没有这个表情文件时，退化成情绪表演：脸和身体至少会有反应，而不是毫无动静
        string? emotion = ToEmotionKey(wanted);
        if (emotion != null)
        {
            logger.LogInformation("表情 {Expression} 不在当前模型里，改用情绪表演 {Emotion}", wanted, emotion);
            embodiment.Perform(emotion, 0.8);
            OnEmotionChanged(emotion, 0.8);
            return Task.CompletedTask;
        }

        logger.LogDebug("表情 {Expression} 不在当前模型的表情列表里", wanted);
        return Task.CompletedTask;
    }

    /// <summary>中文/英文情绪词 → 引擎情绪键（AI 常直接说「开心」「哭」，不一定是英文键）。</summary>
    static readonly Dictionary<string, string> EmotionAliases = new(StringComparer.OrdinalIgnoreCase) {
        ["开心"] = "happy", ["高兴"] = "happy", ["快乐"] = "happy", ["微笑"] = "happy", ["笑"] = "happy",
        ["难过"] = "sad", ["伤心"] = "sad", ["哭"] = "sad", ["流泪"] = "sad", ["委屈"] = "sad", ["悲"] = "sad",
        ["生气"] = "angry", ["愤怒"] = "angry", ["怒"] = "angry",
        ["惊讶"] = "surprised", ["震惊"] = "surprised", ["吃惊"] = "surprised",
        ["困惑"] = "confused", ["疑惑"] = "confused", ["疑问"] = "confused",
        ["害羞"] = "shy", ["脸红"] = "shy", ["羞"] = "shy",
        ["放松"] = "relaxed", ["平静"] = "relaxed", ["安心"] = "relaxed",
        ["兴奋"] = "excited", ["激动"] = "excited", ["期待"] = "excited",
        ["得意"] = "proud", ["骄傲"] = "proud",
        ["无聊"] = "bored", ["困"] = "bored", ["累"] = "bored",
        ["担心"] = "concerned", ["忧虑"] = "concerned",
        ["neutral"] = "neutral", ["happy"] = "happy", ["sad"] = "sad", ["angry"] = "angry",
        ["surprised"] = "surprised", ["confused"] = "confused", ["shy"] = "shy",
        ["relaxed"] = "relaxed", ["excited"] = "excited", ["proud"] = "proud",
        ["bored"] = "bored", ["concerned"] = "concerned",
    };

    static string? ToEmotionKey(string text)
    {
        if (EmotionAliases.TryGetValue(text, out string? exact)) return exact;
        foreach ((string alias, string emotion) in EmotionAliases)
        {
            if (alias.Length >= 2 && text.Contains(alias, StringComparison.OrdinalIgnoreCase))
                return emotion;
        }
        return null;
    }

    /// <summary>表情名解析：模型原名、作者 VTS 热键名（如「屑」）、模糊包含都接受。</summary>
    string? ResolveExpressionName(string wanted)
    {
        string? exact = metadata.Expressions.FirstOrDefault(e =>
            string.Equals(e, wanted, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        string? byLabel = metadata.ExpressionFiles.FirstOrDefault(f =>
            string.Equals(f.Label, wanted, StringComparison.OrdinalIgnoreCase))?.Name;
        if (byLabel != null) return byLabel;

        // 名称允许模糊匹配：模型里叫「哭哭」，AI 说「哭」也能对上
        string? contains = metadata.Expressions.FirstOrDefault(e =>
            e.Contains(wanted, StringComparison.OrdinalIgnoreCase)
            || wanted.Contains(e, StringComparison.OrdinalIgnoreCase));
        if (contains != null) return contains;

        return metadata.ExpressionFiles.FirstOrDefault(f =>
            f.Label is { Length: > 0 } label
            && (label.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                || wanted.Contains(label, StringComparison.OrdinalIgnoreCase)))?.Name;
    }

    /// <summary>动作名解析：模型原名、作者 VTS 热键名（如「抚胸手打开」）、模糊包含都接受。</summary>
    bool TryResolveMotion(string wanted, out (string Group, int Index) target)
    {
        if (metadata.Motions.TryGetValue(wanted, out target)) return true;

        MotionFileItem? byLabel = metadata.MotionFiles.FirstOrDefault(f =>
            string.Equals(f.Label, wanted, StringComparison.OrdinalIgnoreCase));
        if (byLabel?.Name is { Length: > 0 } labelName && metadata.Motions.TryGetValue(labelName, out target)) return true;

        string? fuzzy = metadata.Motions.Keys.FirstOrDefault(key =>
            key.Contains(wanted, StringComparison.OrdinalIgnoreCase)
            || wanted.Contains(key, StringComparison.OrdinalIgnoreCase));
        if (fuzzy != null && metadata.Motions.TryGetValue(fuzzy, out target)) return true;

        MotionFileItem? labelFuzzy = metadata.MotionFiles.FirstOrDefault(f =>
            f.Label is { Length: > 0 } label
            && (label.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                || wanted.Contains(label, StringComparison.OrdinalIgnoreCase)));
        if (labelFuzzy?.Name is { Length: > 0 } fuzzyName && metadata.Motions.TryGetValue(fuzzyName, out target)) return true;

        target = default;
        return false;
    }

    public Task ShowMotion(string? motion, double? seconds = null)
    {
        if (string.IsNullOrWhiteSpace(motion)) return Task.CompletedTask;
        if (TryResolveMotion(motion.Trim(), out (string Group, int Index) target) == false)
        {
            logger.LogInformation("动作 {Motion} 不在当前模型的动作列表里", motion);
            return Task.CompletedTask;
        }
        logger.LogInformation("播放原生动作 {Motion} → {Group}#{Index}（seconds={Seconds}）",
            motion, target.Group, target.Index, seconds);
        expressionModule.PlayMotion(target.Group, target.Index, seconds);
        return Task.CompletedTask;
    }

    public Task<Vector2> GetPosition()
    {
        float dpi = (float)window.Dpi;
        Rectangle bounds = window.Bounds;
        Vector2 position = new(
            (bounds.X + bounds.Width / 2.0f) * dpi,
            (bounds.Y + bounds.Height / 2.0f) * dpi
        );
        return Task.FromResult(position);
    }

    public async Task Move(Vector2 offset, float seconds)
    {
        float dpi = (float)window.Dpi;
        Rectangle bounds = window.Bounds;

        double startX = bounds.X;
        double startY = bounds.Y;
        double endX = startX + offset.X / dpi;
        double endY = startY + offset.Y / dpi;
        long startTick = Environment.TickCount64;
        int durationMs = (int)(seconds * 1000);
        if (durationMs <= 0) durationMs = 1;

        while (true)
        {
            long elapsed = Environment.TickCount64 - startTick;
            double t = Math.Min(1.0, (double)elapsed / durationMs);
            double ease = t * (2 - t);
            bounds.X = (int)Math.Round(startX + (endX - startX) * ease);
            bounds.Y = (int)Math.Round(startY + (endY - startY) * ease);

            await window.SetBoundsAsync(bounds);
            if (t >= 1.0) break;
            await Task.Delay(16);
        }
    }

    /// <summary>将桌宠窗口恢复到默认位置与大小（用于桌宠意外跑出屏幕后重置）。</summary>
    public void ResetWindow() => _ = window.ResetBoundsAsync();

    // ---- 区域坐标 ⇄ 当前窗口真实坐标 换算（供配置 UI 所见即所得）----

    public double RegionToWindowX(double x) => regionModule is null ? x : regionModule.ToWindow(x, 0).X;
    public double RegionToWindowY(double y) => regionModule is null ? y : regionModule.ToWindow(0, y).Y;
    public double WindowToRegionX(double x) => regionModule is null ? x : regionModule.ToCalibrated(x, 0).X;
    public double WindowToRegionY(double y) => regionModule is null ? y : regionModule.ToCalibrated(0, y).Y;
    public bool HasRegionView => regionModule is not null && regionModule.HasView;

    /// <summary>读取桌宠点击区域配置（供 UI 编辑）。</summary>
    public List<PetRegionConfig> GetRegions() => regionModule?.GetRegions() ?? new List<PetRegionConfig>();

    /// <summary>保存桌宠点击区域配置（供 UI 编辑）。</summary>
    public void SaveRegions(List<PetRegionConfig> regions) => regionModule?.SaveRegions(regions);

    /// <summary>读取永久不穿透区（供 UI 编辑）。</summary>
    public PetRegionConfig? GetForeverRegion() => regionModule?.GetForeverRegion();

    /// <summary>保存永久不穿透区（供 UI 编辑）。</summary>
    public void SaveForeverRegion(PetRegionConfig region) => regionModule?.SaveForeverRegion(region);

    // ---- 交互开关（与桌宠窗口内的设置面板共用同一份数据）----

    public List<Dictionary<string, object?>> GetPetSettings()
    {
        try
        {
            PetInteractionHub? hub = provider?.GetService<PetInteractionHub>();
            return hub?.Snapshot() ?? new List<Dictionary<string, object?>>();
        }
        catch { return new List<Dictionary<string, object?>>(); }
    }

    public void SetPetSetting(string key, string value)
    {
        try
        {
            provider?.GetService<PetInteractionHub>()?.SetValue(key, value);
            provider?.GetService<GestureInteractionModule>()?.PushSettings();
        }
        catch { }
    }

    #endregion

    #region 对话情绪

    static readonly Regex TagRegex = new("<[^>]+>", RegexOptions.Compiled);

    void OnChatFinished(ChatContext context)
    {
        string aiText = Clean(context.AIMessage);
        string userText = Clean(context.UserMessage);

        if (Configuration.AutoEmotionFromChat)
        {
            if (userText.Length > 0)
                embodiment.SendText(Truncate(userText, 160), "user", 0.55);
            if (aiText.Length > 0)
                embodiment.SendText(Truncate(aiText, 200), "ai", 1.0);
        }

        if (Configuration.ShowSubtitle && aiText.Length > 0)
            subtitleModule.Show(Truncate(aiText, 90));
    }

    static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        string result = TagRegex.Replace(text, " ");
        return Regex.Replace(result, @"\s+", " ").Trim();
    }

    static string Truncate(string text, int max)
    {
        return text.Length <= max ? text : text[..max] + "…";
    }

    #endregion

    #region 情绪持久化

    void OnEmotionChanged(string emotion, double intensity)
    {
        lastEmotion = emotion;
        lastIntensity = intensity;

        // 前端每秒都可能上报状态，落盘做节流；异常退出时由 OnDestroy 兜底保存
        long now = Environment.TickCount64;
        if (now - lastSaveTicks < 30000) return;
        lastSaveTicks = now;
        SaveEmotion();
    }

    void SaveEmotion()
    {
        if (Configuration.PersistEmotion == false) return;
        storageSystem.SetObject(memoryKey, new EmotionMemory {
            Emotion = lastEmotion,
            Intensity = lastIntensity,
            AtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
    }

    void RestoreEmotion()
    {
        if (Configuration.PersistEmotion == false) return;
        EmotionMemory? memory = storageSystem.GetObject<EmotionMemory>(memoryKey);
        if (memory == null || string.IsNullOrWhiteSpace(memory.Emotion)) return;
        if (memory.Emotion == "neutral") return;

        // 离开越久，恢复强度越低，避免一上线就处于高强度情绪
        double ageMinutes = Math.Max(0, (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - memory.AtUnixMs) / 60000.0);
        double decay = Math.Max(0.15, 1.0 - ageMinutes / 240.0);
        lastEmotion = memory.Emotion;
        lastIntensity = Math.Clamp(memory.Intensity * decay * 0.7, 0.1, 1.0);
        embodiment.Perform(lastEmotion, lastIntensity);
    }

    #endregion

    #region AI 具身工具

    readonly StringBuilder speakBuffer = new();

    /// <summary>
    /// 兼容 AI 习惯的 &lt;Speak&gt; 包裹写法（XmlFunctionCaller 内置示例就是这种写法）：
    /// 文本是流式分片到来的，按顺序累积后显示成底部字幕，避免再报「环境中没有&lt;Speak/&gt;」。
    /// </summary>
    [XmlFunction(FunctionMode.Content)]
    [Description("把台词显示在桌宠底部的字幕条上，可以包裹整段回复文本。")]
    public Task Speak(XmlContext context, [XmlContent] string content)
    {
        switch (context.CallMode)
        {
            case CallMode.Opening:
                speakBuffer.Clear();
                break;
            case CallMode.Content:
                speakBuffer.Append(content);
                string text = speakBuffer.ToString().Trim();
                if (text.Length > 0) ShowSubtitle(text);
                break;
            case CallMode.Closing:
                speakBuffer.Clear();
                break;
        }
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("让桌宠做出具身表演：优先播放该情绪对应的模型原生表情，再用 FACS 合成五官、头部/身体动作与口型。emotion 可选 happy/sad/angry/surprised/confused/shy/relaxed/excited；motion 与 expression 填模型自带的名字时优先用原生资源。")]
    public Task PetPerform(
        [Description("情绪名称，例如 happy、shy、sad、angry、surprised、confused、relaxed")] string emotion = "happy",
        [Description("强度 0~1，默认 0.7")] double intensity = 0.7,
        [Description("可选：模型原生动作名（见模型动作列表），留空自动")] string motion = "",
        [Description("可选：模型原生表情名（见模型表情列表），留空自动")] string expression = "")
    {
        double clamped = Math.Clamp(intensity, 0.05, 1.0);

        if (string.IsNullOrWhiteSpace(motion) == false
            && TryResolveMotion(motion.Trim(), out (string Group, int Index) target))
            embodiment.PlayMotion(target.Group, target.Index);

        // 指定表情要交给 Perform 一起下发：否则前端会先用情绪映射挑一个别的原生表情，把刚播的表情顶掉
        string? canonicalExpression = string.IsNullOrWhiteSpace(expression)
            ? null
            : ResolveExpressionName(expression.Trim());

        // 体型/换装类指定表情默认保持，不要走 PetPerform 的 6 秒原生表情回收
        if (canonicalExpression != null && outfitModule?.IsPersistentExpression(canonicalExpression) == true)
            ShowExpression(canonicalExpression, 0);

        string emotionKey = ToEmotionKey(emotion) ?? emotion.Trim().ToLowerInvariant();
        embodiment.Perform(emotionKey, clamped, expression: canonicalExpression);
        OnEmotionChanged(emotionKey, clamped);
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("让她做一个整段手势或身体动作：挥手、点头、摇头、鞠躬、前倾、后仰、张望、耸肩、举双手、比心、指向。模型有对应原生手势动作时会优先用原生动作；没有手臂参数时只演头身层或原生动作，不会报错。")]
    public Task PetGesture(
        [Description("动作名：wave 挥手 / nod 点头 / shakeHead 摇头 / bow 鞠躬 / leanIn 前倾 / leanBack 后仰 / lookAround 张望 / shrug 耸肩 / handsUp 举双手 / heart 比心 / point 指向")] string name = "nod",
        [Description("强度 0~1，默认 0.75")] double intensity = 0.75,
        [Description("持续秒数，由你按场景决定；0=按动作自带时长")] double seconds = 0)
    {
        embodiment.PlayGesture(name, intensity, seconds);
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("让整只人物在窗口里做位移演出，不依赖模型参数、任何模型都能用：jump 跳一下 / hop 小跳两下 / bounce 连续弹跳 / shake 抖动 / step 横向闪开 / leanIn 凑近放大 / retreat 后退缩小 / turn 原地转身 / tremble 发颤。")]
    public Task PetEffect(
        [Description("演出名：jump / hop / bounce / shake / step / leanIn / retreat / turn / tremble")] string name = "jump",
        [Description("强度 0~1，默认 0.6")] double intensity = 0.6,
        [Description("持续秒数，0=按演出自带时长")] double seconds = 0)
    {
        embodiment.PlayWindowEffect(name, intensity, seconds);
        return Task.CompletedTask;
    }

    /// <summary>人设表情预设：模型没有的表情（傲娇/病娇…）用一组面部动作单元组合出来。</summary>
    static readonly Dictionary<string, Dictionary<string, double>> PersonaPresets = new(StringComparer.OrdinalIgnoreCase) {
        // 傲娇：皱眉 + 嘴角下撇 + 脸红 + 视线躲开 + 歪头
        ["tsundere"] = new() {
            ["browDown"] = 0.38, ["browInnerUp"] = 0.30, ["eyeSquint"] = 0.35,
            ["mouthFrown"] = 0.45, ["mouthSmile"] = 0.12, ["blush"] = 0.50,
            ["gazeX"] = -0.55, ["gazeY"] = 0.20, ["headZ"] = 0.30, ["headX"] = -0.15,
        },
        // 病娇：睁大眼 + 笑眼 + 内眉上抬 + 淡笑 + 歪头 + 一点泪光
        ["yandere"] = new() {
            ["eyeOpen"] = 1.0, ["eyeSmile"] = 0.15, ["browInnerUp"] = 0.60, ["browDown"] = 0.15,
            ["mouthSmile"] = 0.60, ["blush"] = 0.25, ["tear"] = 0.12,
            ["headZ"] = -0.35, ["headX"] = 0.10,
        },
        // 三无：眼睛半睁、几乎无表情、微微偏头
        ["kuudere"] = new() {
            ["eyeOpen"] = 0.75, ["eyeSquint"] = 0.15, ["browInnerUp"] = 0.10,
            ["browDown"] = 0.18, ["mouthSmile"] = 0.05, ["headZ"] = 0.08,
        },
        // 元气：睁眼笑、张嘴、抬头
        ["genki"] = new() {
            ["eyeOpen"] = 1.0, ["eyeSmile"] = 0.80, ["mouthSmile"] = 1.0, ["mouthOpen"] = 0.25,
            ["browInnerUp"] = 0.55, ["blush"] = 0.30, ["headZ"] = -0.12, ["headY"] = -0.10,
        },
        // 害羞：眯眼笑、脸红、视线下垂避开
        ["shy"] = new() {
            ["eyeSquint"] = 0.55, ["eyeSmile"] = 0.35, ["mouthSmile"] = 0.45, ["blush"] = 0.90,
            ["browInnerUp"] = 0.45, ["gazeX"] = -0.45, ["gazeY"] = 0.30, ["headZ"] = 0.22,
        },
        // 困倦：眼睛快闭上、头往下垂
        ["sleepy"] = new() {
            ["eyeOpen"] = 0.35, ["eyeSquint"] = 0.20, ["browInnerUp"] = 0.20,
            ["browDown"] = 0.12, ["mouthOpen"] = 0.12, ["headZ"] = 0.20, ["headY"] = 0.25,
        },
        // 得意：笑眼、挑眉、仰头
        ["proud"] = new() {
            ["eyeSmile"] = 0.55, ["eyeOpen"] = 0.85, ["browInnerUp"] = 0.35, ["browOuterUp"] = 0.20,
            ["mouthSmile"] = 0.70, ["blush"] = 0.15, ["headZ"] = -0.12, ["headY"] = -0.18,
        },
        // 吃醋：皱眉下撇、视线偏开、脸红
        ["jealous"] = new() {
            ["browDown"] = 0.45, ["browInnerUp"] = 0.35, ["eyeSquint"] = 0.30,
            ["mouthFrown"] = 0.55, ["mouthSmile"] = 0.10, ["blush"] = 0.30,
            ["gazeX"] = -0.40, ["headZ"] = 0.18,
        },
    };

    [XmlFunction(FunctionMode.OneShot)]
    [Description("用面部动作单元（FACS）精确画表情：只改你填了的通道，可自由组合出傲娇、病娇、冷笑等模型没有的人设表情。intensity 会整体缩放，seconds 后自动淡出；也可先用 persona 选预设再微调。")]
    public Task PetFace(
        [Description("基础情绪，可留空。happy/sad/angry/surprised/confused/shy/relaxed/excited/proud/bored")] string emotion = "",
        [Description("人设预设，可留空：tsundere(傲娇)/yandere(病娇)/kuudere(三无)/genki(元气)/shy(害羞)/sleepy(困倦)/proud(得意)/jealous(吃醋)")] string persona = "",
        [Description("整体强度 0~1，会缩放下面所有参数，默认 0.7")] double intensity = 0.7,
        [Description("保持秒数，0 或负数=一直保持，默认 2.5")] double seconds = 2.5,
        [Description("同时叠加的模型原生表情名，可留空")] string expression = "",
        [Description("睁眼程度 0~1（1=瞪大）")] double? eyeOpen = null,
        [Description("眯眼程度 0~1（笑眼/不屑/怀疑）")] double? eyeSquint = null,
        [Description("笑眼 0~1")] double? eyeSmile = null,
        [Description("视线左右 -1~1，负=看左边/躲开视线")] double? gazeX = null,
        [Description("视线上下 -1~1")] double? gazeY = null,
        [Description("内眉上抬 0~1（委屈/难过/求助）")] double? browInnerUp = null,
        [Description("皱眉下压 0~1（生气/不悦/傲娇）")] double? browDown = null,
        [Description("外眉上抬 0~1（惊讶）")] double? browOuterUp = null,
        [Description("嘴角上扬 0~1")] double? mouthSmile = null,
        [Description("嘴角下撇 0~1（不高兴/傲娇/吃醋）")] double? mouthFrown = null,
        [Description("张嘴 0~1（惊讶/喘气/大笑）")] double? mouthOpen = null,
        [Description("嘟嘴 0~1（撒娇/不满）")] double? mouthPucker = null,
        [Description("脸红 0~1（害羞/心动/发烧）")] double? blush = null,
        [Description("泪目 0~1（哭/感动/病娇）")] double? tear = null,
        [Description("汗水 0~1（尴尬/紧张）")] double? sweat = null,
        [Description("头部左右转 -1~1")] double? headX = null,
        [Description("头部上下 -1~1（负=抬头）")] double? headY = null,
        [Description("歪头 -1~1（正=向右歪）")] double? headZ = null)
    {
        double gain = Math.Clamp(intensity, 0.05, 1.0);
        Dictionary<string, double> aus = new(StringComparer.Ordinal);
        void Put(string key, double? value, double min, double max)
        {
            if (value is not double v) return;
            aus[key] = Math.Clamp(v, min, max) * gain;
        }

        // 先铺预设，再用 AI 显式填的通道覆盖，方便「预设 + 微调」
        if (PersonaPresets.TryGetValue((persona ?? "").Trim(), out Dictionary<string, double>? preset))
        {
            foreach ((string key, double value) in preset)
                aus[key] = value * gain;
        }

        Put("eyeOpen", eyeOpen, 0, 1);
        Put("eyeSquint", eyeSquint, 0, 1);
        Put("eyeSmile", eyeSmile, 0, 1);
        Put("gazeX", gazeX, -1, 1);
        Put("gazeY", gazeY, -1, 1);
        Put("browInnerUp", browInnerUp, 0, 1);
        Put("browDown", browDown, 0, 1);
        Put("browOuterUp", browOuterUp, 0, 1);
        Put("mouthSmile", mouthSmile, 0, 1);
        Put("mouthFrown", mouthFrown, 0, 1);
        Put("mouthOpen", mouthOpen, 0, 1);
        Put("mouthPucker", mouthPucker, 0, 1);
        Put("blush", blush, 0, 1);
        Put("tear", tear, 0, 1);
        Put("sweat", sweat, 0, 1);
        Put("headX", headX, -1, 1);
        Put("headY", headY, -1, 1);
        Put("headZ", headZ, -1, 1);

        string? baseEmotion = string.IsNullOrWhiteSpace(emotion)
            ? null
            : ToEmotionKey(emotion) ?? emotion.Trim().ToLowerInvariant();
        embodiment.SetFace(aus, baseEmotion, gain, seconds, expression);
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("直接设置桌宠的连续情绪（VAD 三维）：valence 愉悦度、arousal 激活度、dominance 掌控感，范围 -1~1；amount 为过渡力度 0~1。适合表达细微情绪。")]
    public Task PetFeel(
        [Description("愉悦度 -1（难过）~1（开心）")] double valence = 0,
        [Description("激活度 -1（平静）~1（兴奋）")] double arousal = 0,
        [Description("掌控感 -1（退缩）~1（强势）")] double dominance = 0,
        [Description("过渡力度 0~1，默认 0.8")] double amount = 0.8)
    {
        embodiment.SetVAD(valence, arousal, dominance, amount);
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("表演一个表情（option 取模型表情列表中的名字）。seconds：保持多少秒后自动收回；不填=普通表情约 5 秒，体型/换装等状态型一直保持；填 0 或负数=一直保持")]
    public Task Expression(string option, double? seconds = null) => ShowExpression(option, seconds);

    [XmlFunction(FunctionMode.OneShot)]
    [Description("表演一个动作（option 取模型动作列表中的名字）。seconds 由你决定：建议写 1.5~3 秒；不填=按动作自身时长演一遍后回默认；填 0 或负数=一直循环（举手/换道具这类姿势会持续保持）")]
    public Task Motion(string option, double? seconds = null) => ShowMotion(option, seconds);

    [XmlFunction(FunctionMode.OneShot)]
    [Description("换装/形态：显示或隐藏模型自带的外观与形态开关（外套、帽子、发夹、兽耳、尾巴、碎发、裙摆、光环、眼镜、水印，以及前倾、比心、Q版、大小变等姿势/形态）。name 取面板「服饰/动作」栏里的名字；visible 不填则切换当前状态。")]
    public Task PetOutfit(
        [Description("开关名，例如 外套、帽子、兽耳、尾巴、Q版、大小变（也可用模型里的表情原名，如 隐藏外套、隐藏耳朵）")] string name,
        [Description("true=穿上/显示，false=脱下/隐藏；不填=切换")] bool? visible = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return Task.CompletedTask;
        if (outfitModule == null || outfitModule.SetOutfit(name, visible) == false)
        {
            logger.LogDebug("换装项不存在：{Name}（可用：{Items}）", name, outfitModule?.DescribeItems() ?? "");
        }
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("查询桌宠当前的情绪状态。")]
    public Task PetStatus()
    {
        interactor.Poke($"Live2D 桌宠当前情绪：{lastEmotion}（强度 {lastIntensity:F2}）。");
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("切换桌宠使用的模型。name 取模型列表里的名字，大小写不敏感，支持部分匹配。")]
    public async Task SwitchModel(
        [Description("要切换的模型名，见附加说明里的「可切换的模型」")] string name = "")
    {
        string want = (name ?? "").Trim();
        if (want.Length == 0)
        {
            interactor.Poke("请告诉我要切换到哪个模型。");
            return;
        }

        List<PetModelEntry> all = EnumerateModels();
        PetModelEntry? hit = all.FirstOrDefault(m => string.Equals(m.Identifier, want, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(m => string.Equals(m.Name, want, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(m => m.Name.Contains(want, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(m => m.Identifier.Contains(want, StringComparison.OrdinalIgnoreCase));

        if (hit == null)
        {
            interactor.Poke($"没有找到模型「{want}」。可切换的模型：{string.Join("、", all.Select(m => m.Name))}");
            return;
        }
        if (string.Equals(hit.Identifier, Configuration.ModelName, StringComparison.OrdinalIgnoreCase))
        {
            interactor.Poke($"现在用的就是「{hit.Name}」。");
            return;
        }

        interactor.Poke($"正在切换到模型「{hit.Name}」…");
        bool ok;
        try { ok = await SwitchModelAsync(hit.Identifier); }
        catch (Exception e)
        {
            logger.LogWarning(e, "AI 切换模型失败");
            ok = false;
        }

        interactor.Poke(ok
            ? $"已切换到「{hit.Name}」，可用表情 {metadata.Expressions.Count} 个、动作 {metadata.Motions.Count} 个。"
            : $"切换到「{hit.Name}」失败，仍在使用「{CurrentModel}」。");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("获取桌宠当前在屏幕上的位置（使用后会以消息返回）。")]
    public async Task Position()
    {
        Vector2 position = await GetPosition();
        interactor.Poke($"当前位置: x={position.X:0}, y={position.Y:0}");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("让桌宠在屏幕上相对移动。x/y 为像素偏移（右/下为正），seconds 为移动时长。")]
    public async Task Move(
        [Description("水平偏移像素，右为正")] double x = 0,
        [Description("垂直偏移像素，下为正")] double y = 0,
        [Description("移动时长（秒），默认 1")] float seconds = 1)
    {
        await Move(new Vector2((float)x, (float)y), Math.Clamp(seconds, 0.1f, 10f));
        Vector2 position = await GetPosition();
        interactor.Poke($"移动成功，当前位置: x={position.X:0}, y={position.Y:0}");
    }

    #endregion
}
