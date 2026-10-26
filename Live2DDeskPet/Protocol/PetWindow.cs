using System;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Alife.Client;
using Alife.Foundation;
using Alife.Framework;

namespace presents.EmotionLive2DPet;

/// <summary>按角色隔离的存储键前缀，值为角色的 StorageKey（如 "Character\<角色名>"）。</summary>
public record PetStorageKey(string Value);

/// <summary>
/// 桌宠窗口封装（Alife 5 的 IAlifeWindow）：负责创建透明置顶窗口、定位/移动/缩放、DPI 与脚本执行，
/// 并通过 StorageSystem 持久化/恢复窗口位置大小（按角色隔离）。
/// 所有坐标都是 DPI 逻辑坐标（与 IAlifeWindow 的约定一致），光标坐标由物理像素换算得到。
/// </summary>
public sealed class PetWindow(IAlifeApplication alifeApplication, StorageSystem storage, PetStorageKey storageKey) : IAsyncDisposable
{
    public event Action? MouseMoved;

    /// <summary>渲染进程 → 主进程消息（type 与完整信封，保持原有事件形态）。</summary>
    public event Action<string, JsonElement>? OnMessage;

    public IAlifeWindow Window => window ?? throw new InvalidOperationException("桌宠窗口尚未创建。");
    public float Dpi => dpi;
    public Rectangle Bounds => bounds;
    public Point Position => position;
    public Point CursorScreenPoint => cursorScreenPoint;

    /// <summary>主屏尺寸（DPI 逻辑坐标），供贴边/回家等桌面判定使用。</summary>
    public Size ScreenSize => screenSize;

    public async Task ResetBoundsAsync() => await SetBoundsAsync(defaultBounds);

    public async Task SetBoundsAsync(Rectangle newBounds)
    {
        bounds = newBounds;
        position = new Point {
            X = bounds.X + bounds.Width / 2,
            Y = bounds.Y + bounds.Height / 2,
        };
        if (window != null)
            await window.SetBoundsAsync(bounds);
    }

    public Task ShowAsync() => window?.ShowAsync() ?? Task.CompletedTask;
    public Task HideAsync() => window?.HideAsync() ?? Task.CompletedTask;
    public Task SetTopMostAsync(bool topMost) => window?.SetTopMostAsync(topMost) ?? Task.CompletedTask;

    /// <summary>null=整窗可交互，空数组=整窗穿透，数组=仅这些窗口内区域可交互。</summary>
    public Task SetInputRegionsAsync(Rectangle[]? regions) => window?.SetInputRegionsAsync(regions) ?? Task.CompletedTask;

    public async Task<string?> ExecuteJavaScriptAsync(string script)
    {
        try
        {
            if (window != null)
                return await window.ExecuteScriptAsync(script);
        }
        catch (Exception ex)
        {
            AlifeLog.LogWarning("执行桌宠脚本失败：" + ex.Message);
        }

        return null;
    }

    IAlifeWindow? window;
    CancellationTokenSource? cancellationTokenSource;
    Rectangle bounds;
    Point position;
    Size screenSize;
    Point cursorScreenPoint;
    float dpi = 1f;

    readonly string windowBoundsKey = $"{storageKey.Value}/Live2DDeskPet/WindowBounds";
    Rectangle defaultBounds;

    public async Task CreateAsync(string wwwRoot)
    {
        if (!OperatingSystem.IsWindows())
            throw new NotSupportedException("不支持的平台。");

        //获取基础属性（先确定 DPI，再据此把物理像素换算成逻辑坐标）
        dpi = alifeApplication.GetDpiScale();
        screenSize = (alifeApplication.GetScreenSize() / dpi).ToSize();
        defaultBounds = new Rectangle {
            X = Math.Max(0, screenSize.Width - 710),
            Y = Math.Max(0, screenSize.Height - 215),
            Width = Math.Min(360, screenSize.Width),
            Height = Math.Min(540, screenSize.Height),
        };
        bounds = storage.GetObject(windowBoundsKey, defaultBounds);
        // 位置钳制：只在窗口【完全】移出屏幕时才拉回。
        // 用户可自由把窗口部分拖出屏幕边缘（如桌宠半挂在屏幕左侧，X 为负但右缘仍在屏内），
        // 这类"部分出屏"位置必须精确保存/恢复，不能被误判为"跑出屏幕"。
        Rectangle wa = new(0, 0, screenSize.Width, screenSize.Height);
        const int MinVisible = 60; // 拉回后至少露出 60px
        if (bounds.X + bounds.Width <= wa.X) bounds.X = wa.X;                      // 整个窗口在屏幕左侧外
        if (bounds.X >= wa.X + wa.Width) bounds.X = wa.X + wa.Width - MinVisible;  // 整个窗口在屏幕右侧外
        if (bounds.Y + bounds.Height <= wa.Y) bounds.Y = wa.Y;                     // 整个在屏幕上侧外
        if (bounds.Y >= wa.Y + wa.Height) bounds.Y = wa.Y + wa.Height - MinVisible;// 整个在下侧外
        position = new Point {
            X = bounds.X + bounds.Width / 2,
            Y = bounds.Y + bounds.Height / 2,
        };
        cursorScreenPoint = GetCursorPosition();

        //创建窗口
        string url = new Uri(Path.Combine(wwwRoot, "index.html")).AbsoluteUri;
        window = await alifeApplication.CreateWindowAsync(new AlifeWindowOptions {
            Title = "Live2D 桌宠",
            Url = url,
            Bounds = bounds,
            TopMost = true,
            Resizable = false,
            Transparent = true,
        });
        window.MessageReceived += OnWindowMessage;
        window.BoundsChanged += OnBoundsChanged;

        //引导脚本在页面一开始就发 boot：用来区分「页面根本没加载」和「页面加载了但脚本报错」
        TaskCompletionSource bootedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnBoot(string type, JsonElement _)
        {
            if (type == "boot")
            {
                OnMessage -= OnBoot;
                bootedTcs.TrySetResult();
            }
        }
        OnMessage += OnBoot;

        //注册 init 监听：必须在窗口开始加载页面之前完成，避免错过渲染进程在脚本末尾发送的 init 消息
        TaskCompletionSource initTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnInit(string type, JsonElement _)
        {
            if (type == "init")
            {
                OnMessage -= OnInit;
                initTcs.TrySetResult();
            }
        }
        OnMessage += OnInit;

        try
        {
            //唤醒窗口（开始加载页面）
            await window.Launch();

            //等待渲染进程初始化完成，确保 JS 已加载并能通讯后再继续后续消息交互
            using CancellationTokenSource initTimeout = new(TimeSpan.FromSeconds(30));
            try
            {
                await initTcs.Task.WaitAsync(initTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                //把「等不到 init」翻译成人能看懂的原因，而不是抛一个 TaskCanceledException 了事
                throw new InvalidOperationException(bootedTcs.Task.IsCompleted
                    ? $"桌宠窗口前端已开始执行，但 30 秒内没有完成初始化（多半是 lib 资源缺失、WebGL 不可用或前端脚本报错）。请看日志中的 [桌宠前端/...] 行。URL：{url}"
                    : $"桌宠窗口 30 秒内没有加载出页面（index.html 或 lib 目录缺失，或被安全软件拦截）。URL：{url}");
            }
        }
        finally
        {
            OnMessage -= OnInit;
            OnMessage -= OnBoot;
        }

        //处理鼠标信息捕获
        cancellationTokenSource = new CancellationTokenSource();
        Loop(cancellationTokenSource.Token);
    }

    public async ValueTask DisposeAsync()
    {
        // 空安全：初始化中途失败时 window/cts 可能还没赋值，
        // 否则 Dispose 自己抛异常会把真正的失败原因盖掉。
        try
        {
            if (bounds != default)
                storage.SetObject(windowBoundsKey, bounds);
        }
        catch (Exception e) { AlifeLog.LogWarning("PetWindow 保存位置失败: " + e.Message); }

        try
        {
            if (cancellationTokenSource != null)
            {
                await cancellationTokenSource.CancelAsync();
                cancellationTokenSource.Dispose();
                cancellationTokenSource = null;
            }
        }
        catch (Exception e) { AlifeLog.LogWarning("PetWindow 停止轮询失败: " + e.Message); }

        try
        {
            if (window != null)
            {
                window.BoundsChanged -= OnBoundsChanged;
                window.MessageReceived -= OnWindowMessage;
                await window.DisposeAsync();
                window = null;
            }
        }
        catch (Exception e) { AlifeLog.LogWarning("PetWindow 关闭窗口失败: " + e.Message); }
    }

    void OnBoundsChanged(Rectangle newBounds)
    {
        // Android 会钳制/回流窗口，这里同样以宿主回报的真实尺寸为准
        bounds = newBounds;
        position = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        dpi = alifeApplication.GetDpiScale();
    }

    void OnWindowMessage(string payloadJson)
    {
        if (string.IsNullOrEmpty(payloadJson))
            return;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(payloadJson);
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("type", out JsonElement typeProp) == false)
                return;
            string? type = typeProp.GetString();
            if (string.IsNullOrEmpty(type))
                return;

            // JsonElement 依赖 JsonDocument 的生命周期，回调方可能延后使用，这里克隆一份
            OnMessage?.Invoke(type!, root.Clone());
        }
        catch (Exception ex)
        {
            AlifeLog.LogWarning("解析桌宠前端消息失败：" + ex.Message);
        }
    }

    async void Loop(CancellationToken cancellationToken = default)
    {
        // 单次采样超时不终止循环：否则光标位置永久停更 → 穿透/靠近判定用旧坐标 → 窗口卡死。
        while (cancellationToken.IsCancellationRequested == false)
        {
            try
            {
                await Task.Delay(30, cancellationToken);

                Point newCursorPoint = GetCursorPosition();
                if (cursorScreenPoint.X != newCursorPoint.X || cursorScreenPoint.Y != newCursorPoint.Y)
                {
                    cursorScreenPoint = newCursorPoint;
                    MouseMoved?.Invoke();
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                AlifeLog.LogWarning("PetWindow 轮询瞬时失败，500ms 后重试: " + e.Message);
                try { await Task.Delay(500, cancellationToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    Point GetCursorPosition()
    {
        dpi = alifeApplication.GetDpiScale();
        Point newCursorPoint = alifeApplication.GetCursorPosition();
        newCursorPoint.X = (int)(newCursorPoint.X / dpi);
        newCursorPoint.Y = (int)(newCursorPoint.Y / dpi);
        return newCursorPoint;
    }
}
