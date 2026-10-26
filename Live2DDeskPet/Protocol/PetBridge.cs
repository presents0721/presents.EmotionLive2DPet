using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace presents.EmotionLive2DPet;

/// <summary>
/// 桌宠主进程 ⇄ 渲染进程的桥（Alife 5：每个窗口自带 window.alifeWindow，不再有 IPC 频道）。
/// 信封对象为 { type: string, ...payload }：主进程经 PostMessageAsync 下发，渲染进程上报的消息由
/// <see cref="PetWindow"/> 解析后经 <see cref="OnMessage"/> 分发。
/// </summary>
public sealed class PetBridge : IDisposable
{
    public event Action<string, JsonElement>? OnMessage;

    /// <summary>向渲染进程发送消息（合并 type 与 payload 为单一信封对象）。</summary>
    public void SendMessage(string type, object? payload = null)
    {
        try
        {
            JObject envelope = new() { ["type"] = type };
            if (payload != null)
            {
                JObject payloadObj = JObject.FromObject(payload);
                foreach (KeyValuePair<string, JToken?> kvp in payloadObj)
                    envelope[kvp.Key] = kvp.Value;
            }

            string json = envelope.ToString(Newtonsoft.Json.Formatting.None);
            _ = window.Window.PostMessageAsync(json);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "发送桌宠消息失败");
        }
    }

    /// <summary>在渲染进程执行 JS 并返回结果。</summary>
    public Task<string?> ExecuteJavaScriptAsync(string script) => window.ExecuteJavaScriptAsync(script);

    readonly PetWindow window;
    readonly ILogger<EmotionLive2DPet> logger;

    public PetBridge(PetWindow window, ILogger<EmotionLive2DPet> logger)
    {
        this.window = window;
        this.logger = logger;
        window.OnMessage += Forward;
    }
    public void Dispose()
    {
        window.OnMessage -= Forward;
    }

    void Forward(string type, JsonElement payload)
    {
        try
        {
            OnMessage?.Invoke(type, payload);
        }
        catch (Exception e)
        {
            logger.LogError(e, "执行桌宠前端消息回调失败");
        }
    }
}
