using System;
using Alife.Function.Speech;
using Alife.Framework;
using Microsoft.Extensions.Logging;

namespace presents.EmotionLive2DPet;

/// <summary>
/// 把 Alife 语音插件播放时的真实音量包络转发给桌宠页面，
/// 由前端 Soullink 引擎按时间查表驱动口型（无文件落盘、无额外音频依赖）。
/// 未安装语音插件时自动降级为「说话状态噪声口型」。
/// </summary>
public sealed class SpeechEnvelopeModule : IPetModule, IDisposable
{
    readonly PetBridge bridge;
    readonly ISpeechService? speechService;
    readonly ILogger<EmotionLive2DPet> logger;

    public SpeechEnvelopeModule(PetBridge bridge, ILogger<EmotionLive2DPet> logger, ISpeechService? speechService = null)
    {
        this.bridge = bridge;
        this.logger = logger;
        this.speechService = speechService;

        if (speechService != null)
        {
            speechService.AudioStarted += OnAudioStarted;
            speechService.AudioStopped += OnAudioStopped;
        }
    }

    public void OnStart()
    {
        bridge.SendMessage("voice_config", new {
            available = speechService != null
        });
    }

    public void Dispose()
    {
        if (speechService == null) return;
        speechService.AudioStarted -= OnAudioStarted;
        speechService.AudioStopped -= OnAudioStopped;
    }

    void OnAudioStarted(SpeechAudioContext context)
    {
        try
        {
            bridge.SendMessage("voice_envelope", new {
                envelope = context.Envelope,
                windowMs = context.WindowMs > 0 ? context.WindowMs : 60,
                startUnixMs = context.StartUnixMs
            });
        }
        catch (Exception e)
        {
            logger.LogError(e, "发送语音包络失败");
        }
    }

    void OnAudioStopped()
    {
        bridge.SendMessage("voice_stop", new { });
    }
}
