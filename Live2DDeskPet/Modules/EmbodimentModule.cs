using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Alife.Framework;
using Microsoft.Extensions.Logging;

namespace presents.EmotionLive2DPet;

/// <summary>
/// C# 与前端具身表演引擎之间的桥：下发模型参数表、情绪事件、VAD 目标、原生动作/表情、鼠标位置等。
/// 情绪合成与 FACS 表演在前端完成，C# 只负责事件编排。
/// </summary>
public sealed class EmbodimentModule : IPetModule, IDisposable
{
    readonly PetBridge bridge;
    readonly PetModelContext modelContext;
    readonly PetWindow window;
    readonly PetConfigContext configContext;
    readonly ILogger<EmotionLive2DPet> logger;

    /// <summary>前端上报的主导情绪。</summary>
    public event Action<string, double>? EmotionChanged;

    public EmbodimentModule(
        PetBridge bridge,
        PetModelContext modelContext,
        PetWindow window,
        PetConfigContext configContext,
        ILogger<EmotionLive2DPet> logger)
    {
        this.bridge = bridge;
        this.modelContext = modelContext;
        this.window = window;
        this.configContext = configContext;
        this.logger = logger;

        bridge.OnMessage += OnBridgeMessage;
        window.MouseMoved += OnMouseMoved;
    }

    public void Dispose()
    {
        bridge.OnMessage -= OnBridgeMessage;
        window.MouseMoved -= OnMouseMoved;
    }

    public void OnStart()
    {
        SendSettings();
    }

    void OnMouseMoved()
    {
        var cursor = window.CursorScreenPoint;
        var bounds = window.Bounds;
        bridge.SendMessage("pointer", new {
            x = cursor.X - bounds.X,
            y = cursor.Y - bounds.Y,
            inside = cursor.X >= bounds.X && cursor.X <= bounds.X + bounds.Width
                     && cursor.Y >= bounds.Y && cursor.Y <= bounds.Y + bounds.Height,
            width = bounds.Width,
            height = bounds.Height
        });
    }

    void OnBridgeMessage(string type, JsonElement data)
    {
        if (type != "state") return;
        try
        {
            string emotion = data.TryGetProperty("emotion", out JsonElement emotionProp)
                ? emotionProp.GetString() ?? "neutral"
                : "neutral";
            double intensity = data.TryGetProperty("intensity", out JsonElement intensityProp)
                && intensityProp.TryGetDouble(out double value)
                ? value
                : 0.5;
            EmotionChanged?.Invoke(emotion, intensity);
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "读取情绪状态失败");
        }
    }

    /// <summary>把当前模型参数表与配置下发给前端（启动、换模型、设置页改动后都会重新调用）。</summary>
    public void SendSettings()
    {
        PetModelMetadata metadata = modelContext.Metadata;
        EmotionLive2DPetConfig config = configContext.Current;
        if (metadata == null) return;

        // 表情文件里的参数 ID（key4/Clothes1…）本身没有语义，把 cdi3 的名字一起下发给前端，
        // 这样即使表情名是 Exp01，也能按参数名认出「爱心眼 / 黑脸 / 圈圈眼」。
        Dictionary<string, string> parameterNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (CdiParameter parameter in metadata.Parameters)
            if (parameter.Id is { Length: > 0 } id)
                parameterNames.TryAdd(id, parameter.Name ?? "");

        bridge.SendMessage("embodied_settings", new {
            modelName = metadata.ModelName,
            displayName = metadata.DisplayName,
            parameters = metadata.Parameters.Select(p => new {
                id = p.Id,
                name = p.Name,
                groupId = p.GroupId,
                groupName = p.GroupName,
                min = p.Min,
                max = p.Max,
                @default = p.Default
            }).ToArray(),
            groups = metadata.Groups.Select(g => new {
                Target = g.Target,
                Name = g.Name,
                Ids = g.Ids
            }).ToArray(),
            expressions = metadata.ExpressionFiles.Select(e => new {
                name = e.Name,
                label = e.Label,
                file = e.File,
                @params = e.Params,
                paramNames = e.Params
                    .Select(id => parameterNames.TryGetValue(id, out string? name) ? (name ?? "") : "")
                    .Where(name => name.Length > 0)
                    .ToArray()
            }).ToArray(),
            motions = metadata.MotionFiles.Select(m => new {
                group = m.Group,
                index = m.Index,
                name = m.Name,
                label = m.Label,
                file = m.File,
                @params = m.Params,
                duration = m.Duration,
                loop = m.Name is { Length: > 0 } motionName
                    && metadata.MotionMeta.TryGetValue(motionName, out (bool Loop, double Duration) motionMeta)
                    && motionMeta.Loop
            }).ToArray(),
            lipSyncIds = metadata.LipSyncParameterIds,
            // 用户在「表情校准」面板里手动调的通道倍率（按模型记忆），1 = 完全自动校准
            calibrationGains = CalibrationStore.GainsFor(metadata.ModelName),
            config = new {
                expressionIntensity = config.ExpressionIntensity,
                bodyMotionGain = config.BodyMotionGain,
                idleEnabled = config.IdleMotionEnabled,
                gazeFollow = config.GazeFollowEnabled,
                lipSyncEnabled = config.SpeechLipSyncEnabled,
                autoChat = config.AutoEmotionFromChat
            }
        });
    }

    /// <summary>送入一段文本做情绪推断与表演（来源仅用于调试标记）。</summary>
    public void SendText(string text, string source, double gain = 1.0)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        bridge.SendMessage("emotion_text", new {
            text,
            source,
            gain
        });
    }

    /// <summary>直接指令一个情绪表演。</summary>
    public void Perform(string emotion, double intensity, string? motion = null, string? expression = null)
    {
        bridge.SendMessage("pet_perform", new {
            emotion = string.IsNullOrWhiteSpace(emotion) ? "happy" : emotion,
            intensity = Math.Clamp(intensity, 0.05, 1.0),
            motion,
            expression
        });
    }

    /// <summary>AI 直接指定面部动作单元（FACS）与基础情绪，用于组合模型没有的人设表情。</summary>
    public void SetFace(IReadOnlyDictionary<string, double> aus, string? emotion, double intensity,
        double seconds, string? expression)
    {
        bridge.SendMessage("pet_face", new {
            aus,
            emotion = string.IsNullOrWhiteSpace(emotion) ? null : emotion,
            intensity = Math.Clamp(intensity, 0.05, 1.0),
            seconds,
            expression = string.IsNullOrWhiteSpace(expression) ? null : expression
        });
    }

    /// <summary>直接设置连续 VAD 情绪目标。</summary>
    public void SetVAD(double valence, double arousal, double dominance, double amount = 1.0)
    {
        bridge.SendMessage("pet_vad", new {
            valence = Math.Clamp(valence, -1, 1),
            arousal = Math.Clamp(arousal, -1, 1),
            dominance = Math.Clamp(dominance, -1, 1),
            amount = Math.Clamp(amount, 0, 1)
        });
    }

    /// <summary>说话状态（无语音包络时用于合成噪声口型）。</summary>
    public void SetSpeaking(bool speaking)
    {
        bridge.SendMessage("speech_state", new { speaking });
    }

    /// <summary>指定一段整段手势（挥手/鞠躬/比心…）；模型缺手臂参数时自动只演头身层。</summary>
    public void PlayGesture(string name, double intensity = 0.75, double seconds = 0)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        bridge.SendMessage("pet_gesture", new {
            name = name.Trim(),
            intensity = Math.Clamp(intensity, 0.2, 1.0),
            seconds = Math.Max(0, seconds)
        });
    }

    /// <summary>窗口级位移演出（跳/抖/凑近/转身），不依赖模型参数。</summary>
    public void PlayWindowEffect(string name, double intensity = 0.6, double seconds = 0)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        bridge.SendMessage("pet_window_effect", new {
            name = name.Trim(),
            intensity = Math.Clamp(intensity, 0.15, 1.0),
            seconds = Math.Max(0, seconds)
        });
    }

    public void PlayExpression(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return;
        bridge.SendMessage("native_expression", new { name = expression });
    }

    public void PlayMotion(string group, int index)
    {
        if (string.IsNullOrWhiteSpace(group)) return;

        // 原生动作演一遍就回默认：这些 motion3 多数声明了 Loop，播完不主动停会一直保持抬手/换道具的状态
        PetModelMetadata? metadata = modelContext.Metadata;
        double autoStop = 0;
        string? name = metadata?.Motions.FirstOrDefault(kv => kv.Value.Group == group && kv.Value.Index == index).Key;
        if (name != null && metadata!.MotionMeta.TryGetValue(name, out (bool Loop, double Duration) meta)
            && meta.Duration > 0.5)
            autoStop = Math.Min(meta.Duration + 0.3, 20);
        MotionFileItem? motionFile = metadata?.MotionFiles.FirstOrDefault(file =>
            file.Group == group && file.Index == index);

        bridge.SendMessage("native_motion", new {
            group,
            index = Math.Max(0, index),
            priority = "force",
            autoStopSeconds = autoStop,
            @params = motionFile?.Params ?? new List<string>()
        });
    }
}
