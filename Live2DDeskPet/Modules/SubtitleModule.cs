using System;
using System.Threading;
using System.Threading.Tasks;
using Alife.Foundation;

namespace presents.EmotionLive2DPet;

public class SubtitleModule : IPetModule, IDisposable
{
    //字幕贴在窗口底部（参考 3D 桌宠的做法）：顶部气泡会盖住角色的头和脸，底部字幕条不会。
    public string CssCode => @"
#bubble-container {
    position:fixed; left:50%; bottom:10px;
    transform:translateX(-50%) scale(var(--ui-scale,1));
    transform-origin:bottom center;
    width:max-content; max-width:88vw;
    pointer-events:none; z-index:1000;
    opacity:0; transition:opacity 0.18s ease;
}
#bubble-container.show { opacity:1; }
#bubble {
    background:linear-gradient(180deg, rgba(24,26,34,0.74), rgba(16,18,24,0.80));
    border:1px solid rgba(255,255,255,0.16);
    box-shadow:0 8px 22px rgba(0,0,0,0.35);
    backdrop-filter:blur(10px); -webkit-backdrop-filter:blur(10px);
    border-radius:12px;
    padding:8px 14px;
    font-family:'Microsoft YaHei',system-ui,sans-serif;
    font-size:13px; line-height:1.5; color:#f3f5fa;
    text-align:center;
    white-space:pre-wrap; word-break:break-word;
    max-height:26vh; overflow:hidden;
}
";

    public string HtmlCode => "<div id='bubble-container'><div id='bubble'></div></div>";

    public string JsCode => @"
messageBus.on('bubble', (msg) => {
    document.getElementById('bubble').innerText = msg.text;
    document.getElementById('bubble-container').classList.add('show');
});
messageBus.on('hide-bubble', () => {
    document.getElementById('bubble-container').classList.remove('show');
});
";

    public event Action? Showed;
    public event Action? Hid;

    public void Show(string text)
    {
        bridge.SendMessage("bubble", new { text });
        lastShowingTime = DateTime.Now;
        maxShowingTime = Math.Max(text.Length * 200, 1500);
        if (isShowing == false)
        {
            isShowing = true;
            Showed?.Invoke();
        }
    }

    public void Hide()
    {
        bridge.SendMessage("hide-bubble");
        if (isShowing)
        {
            isShowing = false;
            Hid?.Invoke();
        }
    }

    readonly PetBridge bridge;
    readonly CancellationTokenSource? cancellationTokenSource;
    DateTime? lastShowingTime;
    int maxShowingTime;
    bool isShowing;

    public SubtitleModule(PetBridge bridge)
    {
        this.bridge = bridge;

        cancellationTokenSource = new CancellationTokenSource();
        AutoHideLoop(cancellationTokenSource.Token);
    }
    public void Dispose()
    {
        cancellationTokenSource?.Cancel();
        cancellationTokenSource?.Dispose();
    }


    async void AutoHideLoop(CancellationToken cancellationToken)
    {
        try
        {
            while (cancellationToken.IsCancellationRequested == false)
            {
                await Task.Delay(1000, cancellationToken);
                if (lastShowingTime == null)
                    continue;

                if (DateTime.Now - lastShowingTime > TimeSpan.FromMilliseconds(maxShowingTime))
                {
                    Hide();
                    lastShowingTime = null;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            AlifeLog.LogError(e);
        }
    }
}
