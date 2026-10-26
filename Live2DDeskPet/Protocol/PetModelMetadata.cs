using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alife.Foundation;

namespace presents.EmotionLive2DPet;

#region Live2D官方规范扩展

public record ExpressionItem(string? Name, string? File);

public record MotionItem(string? Name, string? File);

public record FileReferencesModel
{
    public List<ExpressionItem> Expressions { get; set; } = new();
    public Dictionary<string, List<MotionItem>> Motions { get; set; } = new();
}

/// <summary>cdi3.json 中的参数定义，用于前端构建模型无关的表情参数映射。</summary>
public record CdiParameter(string? Id, string? Name, string? GroupId, double Min = -1, double Max = 1, double Default = 0)
{
    public string? GroupName { get; set; }
}

/// <summary>model3.json 的 Groups 分组（LipSync / EyeBlink 等）。</summary>
public record ModelGroup(string? Target, string? Name, List<string> Ids);

/// <summary>表情文件与其实际改写的参数 ID（情绪层据此避让原生表情）。</summary>
public record ExpressionFileItem(string? Name, string? File)
{
    public List<string> Params { get; } = new();

    /// <summary>每个参数被写成的目标值（换装类单参数开关要知道「开」到底是 1 还是 3）。</summary>
    public Dictionary<string, double> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>动作文件与其实际改写的参数 ID、时长。</summary>
public record MotionFileItem(string Group, int Index, string? Name, string? File)
{
    public List<string> Params { get; } = new();

    /// <summary>动作时长（秒），0 表示未知。</summary>
    public double Duration { get; set; }
}

#endregion

#region 自定义的交互反馈

public record MotionRef(string Group, int Index);

public record InteractionItem(string? Text, string? Exp, MotionRef? Mtn);

/// <summary>需要每帧强制固定的参数（水印这类"开关参数"用）。</summary>
public record ParamPin(string Id, double Value);

#endregion

/// <summary>
/// Live2D 模型元数据载体与解析器
/// </summary>
public class PetModelMetadata
{
    /// <summary>未登记资源合并后生成的文件名（放在模型目录内，相对路径才能照常解析）。</summary>
    public const string MergedFileName = "_tsumi.merged.model3.json";

    /// <summary>未登记动作归入的组名。</summary>
    public const string ExtraMotionGroup = "额外动作";

    /// <summary>通过 file:// 加载的模型描述文件（有未登记资源被合并时指向生成的合并文件）。</summary>
    public string ModelPath { get; private set; } = string.Empty;

    /// <summary>模型作者写的原始描述文件（保存交互池等改动要写这里，不要写合并文件）。</summary>
    public string SourceModelPath { get; private set; } = string.Empty;

    public List<string> Expressions { get; private set; } = new();
    public Dictionary<string, (string Group, int Index)> Motions { get; private set; } = new();
    public Dictionary<string, List<InteractionItem>> Interactions { get; private set; } = new();

    /// <summary>每帧强制固定的参数值（水印等）。</summary>
    public List<ParamPin> Pins { get; private set; } = new();

    /// <summary>每个动作的自循环与时长（读自动作文件 Meta）：循环的天然"保持"，一次性动作靠按时长重发来保持。</summary>
    public Dictionary<string, (bool Loop, double Duration)> MotionMeta { get; private set; } = new();

    // ---- 具身表演层（Soullink）需要的模型信息 ----

    /// <summary>模型所在目录（绝对路径）。</summary>
    public string ModelDirectory { get; private set; } = string.Empty;

    /// <summary>模型名（目录名）。</summary>
    public string ModelName { get; private set; } = string.Empty;

    /// <summary>模型展示名（优先取 model3.json 的 Name）。</summary>
    public string DisplayName { get; private set; } = string.Empty;

    /// <summary>模型声明的口型同步参数（Groups 里 Target=Parameter、Name=LipSync 的 Ids）。</summary>
    public List<string> LipSyncParameterIds { get; private set; } = new();

    /// <summary>参数表（cdi3）：换装识别要按参数名判断哪些是「外套/兽耳/发夹」这类开关。</summary>
    public List<CdiParameter> Parameters { get; private set; } = new();

    /// <summary>由物理演算驱动的参数：这些参数每帧都在动，绝不能当换装开关钉死。</summary>
    public HashSet<string> PhysicsOutputIds { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>model3.json 的 Groups 全部分组。</summary>
    public List<ModelGroup> Groups { get; private set; } = new();

    /// <summary>表情文件明细（含其改写的参数 ID）。</summary>
    public List<ExpressionFileItem> ExpressionFiles { get; private set; } = new();

    /// <summary>动作文件明细（含其改写的参数 ID 与时长）。</summary>
    public List<MotionFileItem> MotionFiles { get; private set; } = new();

    /// <summary>
    /// 就地替换为另一份元数据。模型切换时用：各模块持有的是同一个 metadata 实例，
    /// 直接换掉内部集合的引用即可让它们看到新模型——整体换引用是原子的，
    /// 不会出现「Clear 之后、Add 之前」被别的线程读到空集合的情况（那样会 First() 抛异常）。
    /// </summary>
    public void ReplaceWith(PetModelMetadata other)
    {
        ModelPath = other.ModelPath;
        SourceModelPath = other.SourceModelPath;
        Expressions = other.Expressions;
        Motions = other.Motions;
        Interactions = other.Interactions;
        Pins = other.Pins;
        MotionMeta = other.MotionMeta;
        ModelDirectory = other.ModelDirectory;
        ModelName = other.ModelName;
        DisplayName = other.DisplayName;
        LipSyncParameterIds = other.LipSyncParameterIds;
        Parameters = other.Parameters;
        PhysicsOutputIds = other.PhysicsOutputIds;
        Groups = other.Groups;
        ExpressionFiles = other.ExpressionFiles;
        MotionFiles = other.MotionFiles;
    }

    /// <summary>整体替换交互池（编辑保存时用，同样是原子换引用）。</summary>
    public void SetInteractions(Dictionary<string, List<InteractionItem>> map) => Interactions = map;

    /// <summary>
    /// 定位模型描述文件：目录名同名文件 → 目录内任意 *.model3.json → 一层子目录内任意 *.model3.json。
    /// 后两种兜底是为了兼容"作者打包时多套了一层文件夹"「模型名与文件夹名不一致」的模型包。找不到返回 null。
    /// </summary>
    public static string? ResolveModelJsonPath(string modelDirectory)
    {
        if (Directory.Exists(modelDirectory) == false) return null;
        string modelName = Path.GetFileName(modelDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        foreach (string pattern in new[] { $"{modelName}.model3.json", $"{modelName}.model.json" })
        {
            string path = Path.Combine(modelDirectory, pattern);
            if (File.Exists(path)) return path;
        }

        foreach (string extension in new[] { "*.model3.json", "*.model.json" })
        {
            string? found = Pick(EnumerateSafely(modelDirectory, extension));
            if (found != null) return found;

            foreach (string sub in EnumerateDirectoriesSafely(modelDirectory))
            {
                found = Pick(EnumerateSafely(sub, extension));
                if (found != null) return found;
            }
        }
        return null;

        // 合并文件是我们自己生成的产物，不能当模型描述文件；多个候选时取文件名最短的（通常就是正主）
        static string? Pick(IEnumerable<string> files)
        {
            List<string> list = files
                .Where(f => Path.GetFileName(f).StartsWith("_tsumi", StringComparison.OrdinalIgnoreCase) == false)
                .OrderBy(f => Path.GetFileName(f).Length)
                .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return list.Count > 0 ? list[0] : null;
        }
    }

    static IEnumerable<string> EnumerateSafely(string directory, string pattern)
    {
        try { return Directory.EnumerateFiles(directory, pattern); }
        catch { return Array.Empty<string>(); }
    }

    static IEnumerable<string> EnumerateDirectoriesSafely(string directory)
    {
        try { return Directory.EnumerateDirectories(directory); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>递归枚举指定类型的文件；某个子目录无权限时跳过它而不是整体失败。</summary>
    static List<string> EnumerateFilesRecursively(string directory, string pattern)
    {
        List<string> files = new();
        void Walk(string current)
        {
            try { files.AddRange(Directory.EnumerateFiles(current, pattern)); }
            catch { }

            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(current); }
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

        if (Directory.Exists(directory)) Walk(directory);
        return files;
    }

    /// <summary>把绝对路径转成相对模型描述文件目录的路径（model3.json 里的 File 字段一律用这种写法）。</summary>
    static string RelativeFile(string modelJsonDirectory, string file)
    {
        try { return Path.GetRelativePath(modelJsonDirectory, file).Replace('\\', '/'); }
        catch { return Path.GetFileName(file); }
    }

    /// <summary>从模型目录加载元数据（目录内按规范解析模型描述文件）。</summary>
    /// <param name="mergeUnregisteredAssets">把目录里没在 model3.json 登记的表情/动作文件也纳入可用列表。</param>
    /// <param name="hideWatermark">按模型自带的水印表情自动隐藏水印。</param>
    /// <param name="watermarkOverride">手动指定水印参数固定值，形如 "Param191=1;Param71=1"，留空则用模型自带水印表情里的值。</param>
    public static PetModelMetadata Load(string modelDirectory, bool mergeUnregisteredAssets = true,
        bool hideWatermark = true, string? watermarkOverride = null)
    {
        PetModelMetadata metadata = new()
        {
            ModelDirectory = modelDirectory,
            ModelName = Path.GetFileName(modelDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
        };

        string? modelJsonPath = ResolveModelJsonPath(modelDirectory);
        if (modelJsonPath == null)
            return metadata;

        metadata.DisplayName = metadata.ModelName;

        metadata.SourceModelPath = modelJsonPath;
        metadata.ModelPath = modelJsonPath;

        try
        {
            JsonSerializerOptions options = new() { PropertyNameCaseInsensitive = true };
            using JsonDocument jsonDoc = JsonDocument.Parse(File.ReadAllText(modelJsonPath));
            JsonElement root = jsonDoc.RootElement;
            string modelJsonDirectory = Path.GetDirectoryName(modelJsonPath)!;

            //收集支持的表情动作
            {
                JsonElement refs = root.TryGetProperty("FileReferences", out JsonElement fileRefs) ? fileRefs : root;
                FileReferencesModel model = JsonSerializer.Deserialize<FileReferencesModel>(refs.GetRawText(), options) ?? new();

                foreach (ExpressionItem exp in model.Expressions)
                {
                    //表情的 Name 并非所有模型的必填项：缺省时退回文件名（与动作的处理保持一致）
                    string name = string.IsNullOrEmpty(exp.Name) ? Stem(exp.File) : exp.Name;
                    if (string.IsNullOrEmpty(name) == false && metadata.Expressions.Contains(name) == false)
                        metadata.Expressions.Add(name);
                    metadata.ExpressionFiles.Add(new ExpressionFileItem(name, exp.File?.Replace('\\', '/')));
                }

                foreach ((string groupName, List<MotionItem> items) in model.Motions)
                {
                    for (int index = 0; index < items.Count; index++)
                    {
                        MotionItem item = items[index];
                        string name = string.IsNullOrEmpty(item.Name) ? Stem(item.File) : item.Name; //动作 Name 并不是官方规范，所以备用拿 File 名称
                        if (string.IsNullOrEmpty(name) == false)
                        {
                            metadata.Motions[name] = (groupName, index);
                            metadata.MotionMeta[name] = ReadMotionMeta(modelJsonDirectory, item.File);
                        }
                        metadata.MotionFiles.Add(new MotionFileItem(groupName, index, name, item.File?.Replace('\\', '/')));
                    }
                }
            }

            // 交互配置
            if (root.TryGetProperty("Interaction", out JsonElement interactJson))
            {
                foreach (JsonProperty poolProp in interactJson.EnumerateObject())
                {
                    metadata.Interactions[poolProp.Name] =
                        JsonSerializer.Deserialize<List<InteractionItem>>(poolProp.Value.GetRawText(), options) ?? new();
                }
            }

            // 模型展示名（cdi3 缺失时也能给出可读名字）
            if (root.TryGetProperty("Name", out JsonElement nameProp) && nameProp.ValueKind == JsonValueKind.String
                && nameProp.GetString() is { Length: > 0 } displayName)
                metadata.DisplayName = displayName;

            ParseGroups(root, metadata);

            // 目录里躺着、但 model3.json 没登记的表情/动作：按文件名补齐，并合并出一份描述文件供前端加载
            if (mergeUnregisteredAssets)
                MergeUnregisteredAssets(metadata, modelJsonPath, modelJsonDirectory);

            // 具身表演层需要：参数表（cdi3）与各动画实际驱动的参数，用于 FACS 映射与原生动画避让
            ParseDisplayInfo(root, modelJsonDirectory, metadata, options);
            ParseAnimationFiles(modelJsonDirectory, metadata);
            ParsePhysicsOutputs(root, modelJsonDirectory, metadata);

            // 水印：模型自带的水印表情只写了参数，得每帧把参数固定住才真的不出现
            if (hideWatermark)
                metadata.Pins = ResolveWatermarkPins(modelJsonDirectory, watermarkOverride);
        }
        catch (Exception e)
        {
            AlifeLog.LogError("Live2D模型解析失败\n" + e);
        }

        AlifeLog.LogInformation($"[桌宠] {metadata.ModelName}：表情 {metadata.Expressions.Count} 个、动作 {metadata.Motions.Count} 个、"
            + $"参数 {metadata.Parameters.Count} 个、口型 {(metadata.LipSyncParameterIds.Count == 0 ? "按标准参数" : string.Join("/", metadata.LipSyncParameterIds))}"
            + $"，表情列表[{string.Join("、", metadata.Expressions.Take(10))}]");

        return metadata;
    }

    /// <summary>
    /// 把未登记的表情（*.exp3.json）与动作（*.motion3.json）补进描述文件，另存一份合并文件给前端加载。
    /// 原文件保持原样（作者的东西不碰），合并文件每次加载都按目录现状重新生成。
    /// </summary>
    static void MergeUnregisteredAssets(PetModelMetadata metadata, string modelJsonPath, string modelJsonDirectory)
    {
        JsonObject? root = JsonNode.Parse(File.ReadAllText(modelJsonPath)) as JsonObject;
        if (root == null) return;

        JsonObject fileReferences = root["FileReferences"] as JsonObject ?? new JsonObject();
        JsonArray expressions = fileReferences["Expressions"] as JsonArray ?? new JsonArray();
        JsonObject motions = fileReferences["Motions"] as JsonObject ?? new JsonObject();

        // 已登记的文件名（大小写不敏感，只比文件名，够用且不受目录嵌套影响）
        HashSet<string> declaredExpressions = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonNode? node in expressions)
        {
            string? file = node?["File"]?.GetValue<string>();
            if (string.IsNullOrEmpty(file) == false) declaredExpressions.Add(NormalizeFile(file));
        }

        HashSet<string> declaredMotions = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, JsonNode?> entry in motions)
        {
            if (entry.Value is not JsonArray array) continue;
            foreach (JsonNode? item in array)
            {
                string? file = item?["File"]?.GetValue<string>();
                if (string.IsNullOrEmpty(file) == false) declaredMotions.Add(NormalizeFile(file));
            }
        }

        // 很多 VTS 模型把表情全放在 exp/ 子目录且不登记，必须递归扫；写进合并文件的路径也必须是相对路径，
        // 否则前端按「模型描述文件所在目录 + 文件名」解析时会找不到 exp/ 下的文件。
        List<string> extraExpressionFiles = EnumerateFilesRecursively(modelJsonDirectory, "*.exp3.json")
            .Where(f => declaredExpressions.Contains(NormalizeFile(f)) == false)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<string> extraMotionFiles = EnumerateFilesRecursively(modelJsonDirectory, "*.motion3.json")
            .Where(f => declaredMotions.Contains(NormalizeFile(f)) == false)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (extraExpressionFiles.Count == 0 && extraMotionFiles.Count == 0)
            return;

        HashSet<string> addedExpressions = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in extraExpressionFiles)
        {
            string name = Stem(file);
            if (string.IsNullOrEmpty(name) || addedExpressions.Add(name) == false) continue;

            string relative = RelativeFile(modelJsonDirectory, file);
            if (metadata.Expressions.Contains(name) == false)
                metadata.Expressions.Add(name);
            expressions.Add(new JsonObject { ["Name"] = name, ["File"] = relative });
            metadata.ExpressionFiles.Add(new ExpressionFileItem(name, relative));
        }

        if (extraMotionFiles.Count > 0)
        {
            JsonArray group = motions[ExtraMotionGroup] as JsonArray ?? new JsonArray();
            int index = group.Count;
            HashSet<string> addedMotions = new(StringComparer.OrdinalIgnoreCase);
            foreach (string file in extraMotionFiles)
            {
                string name = Stem(file);
                if (string.IsNullOrEmpty(name) || addedMotions.Add(name) == false) continue;

                string relative = RelativeFile(modelJsonDirectory, file);
                metadata.Motions[name] = (ExtraMotionGroup, index);
                metadata.MotionMeta[name] = ReadMotionMeta(modelJsonDirectory, relative);
                metadata.MotionFiles.Add(new MotionFileItem(ExtraMotionGroup, index, name, relative));
                group.Add(new JsonObject { ["Name"] = name, ["File"] = relative });
                index++;
            }
            motions[ExtraMotionGroup] = group;
        }

        fileReferences["Expressions"] = expressions;
        fileReferences["Motions"] = motions;
        root["FileReferences"] = fileReferences;

        string mergedPath = Path.Combine(modelJsonDirectory, MergedFileName);
        File.WriteAllText(mergedPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        metadata.ModelPath = mergedPath;

        AlifeLog.LogInformation($"[桌宠] {Path.GetFileName(modelJsonDirectory)}：补登记 {extraExpressionFiles.Count} 个表情、{extraMotionFiles.Count} 个动作 → {MergedFileName}");
    }

    /// <summary>
    /// 水印参数：模型目录里若带"水印"表情（如 水印.exp3.json），取其参数与值每帧固定，水印即不再出现。
    /// 找不到水印表情就返回空——不猜、不乱动模型。
    /// </summary>
    static List<ParamPin> ResolveWatermarkPins(string modelJsonDirectory, string? watermarkOverride)
    {
        // 手动指定优先（格式 "Param191=1;Param71=1"），用于自动取到的值不好使时调
        if (string.IsNullOrWhiteSpace(watermarkOverride) == false)
        {
            List<ParamPin> manual = new();
            foreach (string part in watermarkOverride.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] kv = part.Split('=', 2);
                if (kv.Length == 2 && double.TryParse(kv[1].Trim(), out double value))
                    manual.Add(new ParamPin(kv[0].Trim(), value));
            }
            if (manual.Count > 0)
            {
                AlifeLog.LogInformation($"[桌宠] 水印参数固定（手动配置）：{Describe(manual)}");
                return manual;
            }
        }

        string? watermarkFile = EnumerateFilesRecursively(modelJsonDirectory, "*.exp3.json")
            .FirstOrDefault(f =>
            {
                string stem = Stem(Path.GetFileName(f));
                return stem.Contains("水印") || stem.Contains("watermark", StringComparison.OrdinalIgnoreCase);
            });
        if (watermarkFile == null) return new();

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(watermarkFile));
            if (doc.RootElement.TryGetProperty("Parameters", out JsonElement parameters) == false) return new();

            List<ParamPin> pins = new();
            foreach (JsonElement item in parameters.EnumerateArray())
            {
                string? id = item.TryGetProperty("Id", out JsonElement idProp) ? idProp.GetString() : null;
                if (string.IsNullOrEmpty(id)) continue;
                double value = item.TryGetProperty("Value", out JsonElement valueProp) && valueProp.ValueKind == JsonValueKind.Number
                    ? valueProp.GetDouble() : 1;
                pins.Add(new ParamPin(id, value));
            }
            if (pins.Count > 0)
                AlifeLog.LogInformation($"[桌宠] 水印参数固定（来自 {Path.GetFileName(watermarkFile)}）：{Describe(pins)}");
            return pins;
        }
        catch (Exception e)
        {
            AlifeLog.LogWarning($"[桌宠] 水印表情解析失败：{Path.GetFileName(watermarkFile)}\n{e}");
            return new();
        }

        static string Describe(List<ParamPin> pins) => string.Join("、", pins.Select(p => $"{p.Id}={p.Value}"));
    }

    /// <summary>读取 model3.json 的 Groups（LipSync / EyeBlink...）。</summary>
    static void ParseGroups(JsonElement root, PetModelMetadata metadata)
    {
        try
        {
            JsonElement refs = root.TryGetProperty("FileReferences", out JsonElement fileRefs) ? fileRefs : root;
            if (refs.TryGetProperty("Groups", out JsonElement groups) == false || groups.ValueKind != JsonValueKind.Array)
                return;

            foreach (JsonElement group in groups.EnumerateArray())
            {
                string? target = group.TryGetProperty("Target", out JsonElement targetProp) ? targetProp.GetString() : null;
                string? name = group.TryGetProperty("Name", out JsonElement nameProp) ? nameProp.GetString() : null;
                List<string> ids = new();
                if (group.TryGetProperty("Ids", out JsonElement idsProp) && idsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement id in idsProp.EnumerateArray())
                        if (id.GetString() is { Length: > 0 } text)
                            ids.Add(text);
                }
                metadata.Groups.Add(new ModelGroup(target, name, ids));
                if (string.Equals(name, "LipSync", StringComparison.OrdinalIgnoreCase))
                    metadata.LipSyncParameterIds = ids;
            }
        }
        catch (Exception e) { AlifeLog.LogWarning("[桌宠] Groups 解析失败\n" + e); }
    }

    /// <summary>读取 cdi3.json（DisplayInfo）的参数表；顺带把 ParameterGroups 的名字填到参数上。</summary>
    static void ParseDisplayInfo(JsonElement root, string modelJsonDirectory, PetModelMetadata metadata, JsonSerializerOptions options)
    {
        try
        {
            JsonElement refs = root.TryGetProperty("FileReferences", out JsonElement fileRefs) ? fileRefs : root;
            string? displayInfoFile = refs.TryGetProperty("DisplayInfo", out JsonElement displayProp) ? displayProp.GetString() : null;
            if (string.IsNullOrEmpty(displayInfoFile)) return;

            string path = Path.Combine(modelJsonDirectory, displayInfoFile.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path) == false) return;

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement displayRoot = doc.RootElement;

            JsonElement paramList = displayRoot.TryGetProperty("Parameters", out JsonElement parameters) ? parameters : displayRoot;
            if (paramList.ValueKind != JsonValueKind.Array) return;

            foreach (JsonElement item in paramList.EnumerateArray())
            {
                string? id = item.TryGetProperty("Id", out JsonElement idProp) ? idProp.GetString() : null;
                if (string.IsNullOrEmpty(id)) continue;
                string? name = item.TryGetProperty("Name", out JsonElement nameProp) ? nameProp.GetString() : null;
                string? groupId = item.TryGetProperty("GroupId", out JsonElement groupProp) ? groupProp.GetString() : null;
                double min = item.TryGetProperty("Min", out JsonElement minProp) && minProp.ValueKind == JsonValueKind.Number ? minProp.GetDouble() : -1;
                double max = item.TryGetProperty("Max", out JsonElement maxProp) && maxProp.ValueKind == JsonValueKind.Number ? maxProp.GetDouble() : 1;
                double def = item.TryGetProperty("Default", out JsonElement defProp) && defProp.ValueKind == JsonValueKind.Number ? defProp.GetDouble() : 0;
                metadata.Parameters.Add(new CdiParameter(id, name, groupId, min, max, def));
            }

            if (displayRoot.TryGetProperty("ParameterGroups", out JsonElement parameterGroups) && parameterGroups.ValueKind == JsonValueKind.Array)
            {
                Dictionary<string, string> groupNames = new();
                foreach (JsonElement group in parameterGroups.EnumerateArray())
                {
                    string? groupId = group.TryGetProperty("Id", out JsonElement idProp) ? idProp.GetString() : null;
                    string? groupName = group.TryGetProperty("Name", out JsonElement nameProp) ? nameProp.GetString() : null;
                    if (string.IsNullOrEmpty(groupId) == false && groupName is { Length: > 0 })
                        groupNames[groupId] = groupName;
                }
                foreach (CdiParameter parameter in metadata.Parameters)
                {
                    if (parameter.GroupId is { Length: > 0 } gid && groupNames.TryGetValue(gid, out string? name))
                        parameter.GroupName = name;
                }
            }
        }
        catch (Exception e) { AlifeLog.LogWarning("[桌宠] DisplayInfo 解析失败\n" + e); }
    }

    /// <summary>扫出每个表情/动作文件实际改写的参数 ID——情绪层要给原生动画让路，必须知道这些参数归谁。</summary>
    static void ParseAnimationFiles(string modelJsonDirectory, PetModelMetadata metadata)
    {
        foreach (ExpressionFileItem expression in metadata.ExpressionFiles)
        {
            string? file = expression.File;
            if (string.IsNullOrEmpty(file)) continue;
            try
            {
                string path = Path.Combine(modelJsonDirectory, file.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(path) == false) continue;
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                JsonElement root = doc.RootElement;
                JsonElement parameters = root.TryGetProperty("Parameters", out JsonElement ps) ? ps : root;
                if (parameters.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in parameters.EnumerateArray())
                    {
                        string? id = item.TryGetProperty("Id", out JsonElement idProp) ? idProp.GetString() : null;
                        if (string.IsNullOrEmpty(id) == false)
                        {
                            expression.Params.Add(id);
                            if (item.TryGetProperty("Value", out JsonElement valueProp) && valueProp.ValueKind == JsonValueKind.Number)
                                expression.Values[id] = valueProp.GetDouble();
                        }
                    }
                }
            }
            catch { }
        }

        foreach (MotionFileItem motion in metadata.MotionFiles)
        {
            if (motion.Name is { Length: > 0 } name && metadata.MotionMeta.TryGetValue(name, out (bool Loop, double Duration) meta))
                motion.Duration = meta.Duration;

            string? file = motion.File;
            if (string.IsNullOrEmpty(file)) continue;
            try
            {
                string path = Path.Combine(modelJsonDirectory, file.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(path) == false) continue;
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("Curves", out JsonElement curves) == false || curves.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (JsonElement curve in curves.EnumerateArray())
                {
                    string? target = curve.TryGetProperty("Target", out JsonElement targetProp) ? targetProp.GetString() : null;
                    if (string.Equals(target, "Parameter", StringComparison.OrdinalIgnoreCase) == false
                        && string.Equals(target, "Model", StringComparison.OrdinalIgnoreCase) == false)
                        continue;
                    string? id = curve.TryGetProperty("Id", out JsonElement idProp) ? idProp.GetString() : null;
                    if (string.IsNullOrEmpty(id) == false)
                        motion.Params.Add(id);
                }
            }
            catch { }
        }

        // 不少模型没有 cdi3.json：不补一份参数表，前端就无从建立模型无关的 FACS 映射（表情当然不变）。
        // 用动作/表情里实际出现的参数名 + 模型分组 + Cubism 标准参数名合成一份，写不存在的参数在 Cubism 里是安全的空操作。
        if (metadata.Parameters.Count == 0)
        {
            HashSet<string> ids = new(StringComparer.Ordinal);
            foreach (ExpressionFileItem expression in metadata.ExpressionFiles)
                foreach (string id in expression.Params)
                    ids.Add(id);
            foreach (MotionFileItem motion in metadata.MotionFiles)
                foreach (string id in motion.Params)
                    ids.Add(id);
            foreach (ModelGroup group in metadata.Groups)
                foreach (string id in group.Ids)
                    ids.Add(id);
            foreach (string id in StandardParameterIds)
                ids.Add(id);

            foreach (string id in ids)
                metadata.Parameters.Add(new CdiParameter(id, id, null));
        }
    }

    /// <summary>
    /// 记录物理演算输出参数。换装会把选中的参数每帧钉死，若钉到物理输出（耳朵开闭、裙摆摆动这类），
    /// 部件就会僵在原地不再跟着身体动，所以必须先把它们排除掉。
    /// </summary>
    static void ParsePhysicsOutputs(JsonElement root, string modelJsonDirectory, PetModelMetadata metadata)
    {
        List<string> files = new();
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("FileReferences", out JsonElement refs) && refs.ValueKind == JsonValueKind.Object
            && refs.TryGetProperty("Physics", out JsonElement physics))
        {
            if (physics.ValueKind == JsonValueKind.String)
            {
                string? single = physics.GetString();
                if (string.IsNullOrEmpty(single) == false) files.Add(single);
            }
            else if (physics.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in physics.EnumerateArray())
                {
                    string? file = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                    if (string.IsNullOrEmpty(file) == false) files.Add(file);
                }
            }
        }

        if (files.Count == 0)
        {
            try { files.AddRange(Directory.EnumerateFiles(modelJsonDirectory, "*.physics3.json").Select(Path.GetFileName)!); }
            catch { return; }
        }

        foreach (string file in files)
        {
            try
            {
                string path = Path.Combine(modelJsonDirectory, file!.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(path) == false) continue;
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("PhysicsSettings", out JsonElement settings) == false
                    || settings.TryGetProperty("Output", out JsonElement outputs) == false
                    || outputs.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (JsonElement output in outputs.EnumerateArray())
                {
                    if (output.TryGetProperty("Destination", out JsonElement destination) == false
                        || destination.TryGetProperty("Id", out JsonElement idProp) == false)
                        continue;
                    string? id = idProp.GetString();
                    if (string.IsNullOrEmpty(id) == false) metadata.PhysicsOutputIds.Add(id);
                }
            }
            catch { }
        }
    }

    /// <summary>Cubism 3/4 通用标准参数，仅用于 cdi3 缺失时补全映射（模型没有的参数写入无副作用）。</summary>
    static readonly string[] StandardParameterIds =
    [
        "ParamAngleX", "ParamAngleY", "ParamAngleZ",
        "ParamEyeLOpen", "ParamEyeROpen", "ParamEyeLSmile", "ParamEyeRSmile",
        "ParamEyeBallX", "ParamEyeBallY",
        "ParamBrowLY", "ParamBrowRY", "ParamBrowLForm", "ParamBrowRForm",
        "ParamMouthForm", "ParamMouthOpenY", "ParamCheek",
        "ParamBodyAngleX", "ParamBodyAngleY", "ParamBodyAngleZ", "ParamBreath",
        "ParamArmLA", "ParamArmRA", "ParamHandL", "ParamHandR",
        "ParamHairFront", "ParamHairBack", "ParamHairSide"
    ];

    /// <summary>取文件名里的模型资源名（去掉 .exp3.json / .motion3.json 两层后缀）。</summary>
    static string Stem(string? file)
        => string.IsNullOrEmpty(file) ? "" : Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(file));

    static string NormalizeFile(string file) => Path.GetFileName(file).ToLowerInvariant();

    /// <summary>读动作文件的 Meta（是否自循环 / 总时长），供"保持动作"用。</summary>
    static (bool Loop, double Duration) ReadMotionMeta(string modelDirectory, string? file)
    {
        try
        {
            if (string.IsNullOrEmpty(file)) return (false, 0);
            string path = Path.Combine(modelDirectory, file.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path) == false) return (false, 0);
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("Meta", out JsonElement meta) == false) return (false, 0);
            bool loop = meta.TryGetProperty("Loop", out JsonElement loopProp) && loopProp.ValueKind == JsonValueKind.True;
            double duration = meta.TryGetProperty("Duration", out JsonElement durProp) && durProp.ValueKind == JsonValueKind.Number
                ? durProp.GetDouble() : 0;
            return (loop, duration);
        }
        catch { return (false, 0); }
    }
}
