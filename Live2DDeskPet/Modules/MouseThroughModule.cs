using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Alife.Foundation;

namespace presents.EmotionLive2DPet;

/// <summary>
/// 智能鼠标穿透模块。
/// 两种状态：
///  常态（默认）：人形轮廓(bodyBounds) + 互动区域(regions) + 永久不穿透区(forever) + 按钮区 = 不穿透；其余空白 = 穿透。
///  穿透态：仅永久不穿透区 + 按钮区 = 不穿透；人形与互动区也穿透。
/// 判定在主进程（用全局光标坐标 + 窗口 bounds），因此穿透开启时依然能工作。
/// 前端负责上报：人形掩码(body_mask)、按钮位置(btn_bounds)、永久区拖动/双击事件。
/// 双击永久区或点击锁按钮切换状态；状态持久化到 storage。
/// </summary>
public class MouseThroughModule : IPetModule, IDisposable
{
    public string CssCode => @"
#lock-btn {
    position:fixed; top:8px;
    width:28px; height:28px;
    background:rgba(0,0,0,0.4);
    backdrop-filter:blur(10px); border-radius:50%;
    display:flex; justify-content:center; align-items:center;
    color:white; cursor:pointer; z-index:2100;
    box-shadow:0 4px 10px rgba(0,0,0,0.2);
    border:1px solid rgba(255,255,255,0.2);
    opacity:0; transition:opacity 0.3s, background 0.3s, border-color 0.3s;
}
#lock-btn.near { opacity:1; }
#lock-btn.near.faded { opacity:0.6; }
#lock-btn.left { left:8px; }
#lock-btn.right { right:8px; }
";
    public string HtmlCode => @"
<div id='lock-btn' class='right' title='鼠标穿透（常态/穿透态）'>
    <svg id='lock-open' viewBox='0 0 24 24' width='16' height='16' fill='currentColor'>
        <path d='M12 17c1.1 0 2-.9 2-2s-.9-2-2-2-2 .9-2 2 .9 2 2 2zm6-9h-1V6c0-2.76-2.24-5-5-5S7 3.24 7 6h2c0-1.66 1.34-3 3-3s3 1.34 3 3v2H6c-1.1 0-2 .9-2 2v10c0 1.1.9 2 2 2h12c1.1 0 2-.9 2-2V10c0-1.1-.9-2-2-2z'/>
    </svg>
    <svg id='lock-closed' viewBox='0 0 24 24' width='16' height='16' fill='currentColor' style='display:none'>
        <path d='M18 8h-1V6c0-2.76-2.24-5-5-5S7 3.24 7 6v2H6c-1.1 0-2 .9-2 2v10c0 1.1.9 2 2 2h12c1.1 0 2-.9 2-2V10c0-1.1-.9-2-2-2zm-6 9c-1.1 0-2-.9-2-2s.9-2 2-2 2 .9 2 2-.9 2-2 2zm3.1-9H8.9V6c0-1.71 1.39-3.1 3.1-3.1 1.71 0 3.1 1.39 3.1 3.1v2z'/>
    </svg>
</div>
";
    public string JsCode => @"
(function() {
    var btn = document.getElementById('lock-btn');
    function corner() {
        var isLeft = window.screenX < (screen.width - window.innerWidth) / 2;
        btn.classList.toggle('right', isLeft);
        btn.classList.toggle('left', !isLeft);
        postMessage({type:'lock_corner', corner: isLeft ? 'right' : 'left'});
    }
    function setState(on) {
        btn.classList.toggle('faded', on);
        document.getElementById('lock-open').style.display = on ? 'none' : 'block';
        document.getElementById('lock-closed').style.display = on ? 'block' : 'none';
    }
    btn.addEventListener('click', function() { postMessage({type:'lock_toggle'}); });
    messageBus.on('lock_state', function(msg) { setState(!!msg.on); });
    window.addEventListener('resize', corner);
    setInterval(corner, 500);
    corner();
    // 上报全部桌宠按钮位置（窗口内坐标），供主进程穿透判定 + 靠近显示。
    // 显式列出所有按钮 id（避免选择器引号在 C# 字符串中的转义问题），模块未加载时跳过、轮询补齐。
    function reportBtns() {
        var ids = ['lock-btn', 'input-toggle-btn', 'pet-btn', 'pet-help-btn', 'pet-height-btn', 'region-toggle-btn', 'region-edit-btn', 'pet-settings-btn', 'resize-btn'];
        for (var i = 0; i < ids.length; i++) {
            var el = document.getElementById(ids[i]);
            if (!el) continue;
            var r = el.getBoundingClientRect();
            postMessage({type:'btn_bounds', id: el.id, x: r.left, y: r.top, w: r.width, h: r.height});
        }
    }
    reportBtns();
    setInterval(reportBtns, 1000);
    postMessage({type:'lock_ready'});
})();
" + ProximityJs;

    // 通用"靠近才显示"：前端只负责接收主进程 btn_near 消息设 .near 类。
    // 距离判定在主进程（全局光标轮询），因为穿透态下前端收不到 mousemove。
    public const string ProximityJs = @"
(function() {
    var ids = ['lock-btn', 'input-toggle-btn', 'pet-btn', 'pet-help-btn', 'pet-height-btn', 'region-toggle-btn', 'resize-btn'];
    var btns = {};
    // 动态获取按钮元素（模块加载顺序不定，首次可能拿不到，轮询补齐）
    function refresh() {
        for (var i = 0; i < ids.length; i++) {
            if (!btns[ids[i]]) btns[ids[i]] = document.getElementById(ids[i]);
        }
    }
    function clearNear() {
        for (var i = 0; i < ids.length; i++) {
            if (btns[ids[i]]) btns[ids[i]].classList.remove('near');
        }
    }
    messageBus.on('btn_near', function(msg) {
        var el = null;
        (msg.states || []).forEach(function(s) {
            el = document.getElementById(s.id) || btns[s.id];
            if (el) {
                btns[s.id] = el;
                el.classList.toggle('near', !!s.near);
            }
        });
    });
    // 鼠标移出桌宠窗口：强制隐藏所有按钮
    document.addEventListener('mouseleave', clearNear);
    document.documentElement.addEventListener('mouseleave', clearNear);
    document.body.addEventListener('mouseleave', clearNear);
    document.addEventListener('mouseout', function(e) {
        if (!e.relatedTarget) clearNear(); // relatedTarget 为 null = 移出窗口
    });
    refresh();
    setInterval(refresh, 1000);
})();
";

    readonly PetBridge bridge;
    readonly PetWindow window;
    readonly RegionInteractionModule regionModule;
    readonly PetInteractionHub hub;
    CancellationTokenSource? cancellationTokenSource;
    bool mouseThrough; // false=常态(智能穿透), true=穿透态
    string corner = "right";
    // Alife 5 用「可交互区域」表达穿透：区域之外点击直接落给桌面。
    // 区域只在内容变化时重算（每 50ms 重建上百个矩形没必要）。
    bool regionsDirty = true;
    bool lastEditMode;
    int regionFailures;

    // 人形掩码（窗口内降采样网格，前端上报）：识别不规则人形轮廓（含间隙），非矩形
    int maskGw, maskGh;
    bool[]? maskBits;
    bool hasBody; // 掩码有效
    // 按钮区（窗口内坐标，前端上报）
    readonly List<(string Id, double X, double Y, double W, double H)> buttons = new();
    readonly object buttonsLock = new(); // 保护 buttons 的并发读写（50ms 穿透循环 vs 前端 1s 上报）
    // 弹层/面板区（设置面板、右键菜单等，窗口内坐标）：显示期间必须精确可点击（不参与"靠近显示"）
    readonly List<(string Id, double X, double Y, double W, double H)> uiRects = new();
    readonly object uiLock = new();

    const int LockSize = 34;
    const int LockMargin = 8;
    const int BtnProximity = 60; // 按钮"靠近"判定半径 px
    const int MaskStep = 10;     // 人形掩码格子大小 px（与前端 buildBodyMask 一致）

    public MouseThroughModule(PetBridge bridge, PetWindow window, RegionInteractionModule regionModule, PetInteractionHub hub)
    {
        this.bridge = bridge;
        this.window = window;
        this.regionModule = regionModule;
        this.hub = hub;
        bridge.OnMessage += OnBridgeMessage;
        // 两种状态都持续运行智能穿透循环（常态=空白穿透/区域不穿透，穿透态=仅永久区+按钮不穿透）
        StartLoop();
    }
    public void Dispose()
    {
        bridge.OnMessage -= OnBridgeMessage;
        StopLoop();
    }

    void OnBridgeMessage(string type, JsonElement data)
    {
        // 影响可交互区域的内容变了，下一轮穿透刷新时重算
        if (type is "body_mask" or "btn_bounds" or "ui_bounds" or "lock_toggle" or "forever_dblclick")
            regionsDirty = true;

        switch (type)
        {
            case "lock_ready":
                bridge.SendMessage("lock_state", new { on = mouseThrough });
                break;
            case "lock_corner":
                if (data.TryGetProperty("corner", out JsonElement cornerProp))
                    corner = cornerProp.GetString() == "left" ? "left" : "right";
                break;
            case "lock_toggle":
                SetMouseThrough(!mouseThrough);
                break;
            case "body_mask":
                if (data.TryGetProperty("gw", out var gwProp) && data.TryGetProperty("gh", out var ghProp)
                    && data.TryGetProperty("data", out var dataProp))
                {
                    int gw = gwProp.GetInt32(), gh = ghProp.GetInt32();
                    if (gw > 0 && gh > 0 && dataProp.ValueKind == JsonValueKind.Array)
                    {
                        bool[] bits = new bool[gw * gh];
                        int i = 0;
                        bool any = false;
                        foreach (JsonElement v in dataProp.EnumerateArray())
                        {
                            if (i >= bits.Length) break;
                            bool b = v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.Number && v.GetInt32() != 0);
                            bits[i++] = b;
                            if (b) any = true;
                        }
                        if (any)
                        {
                            maskGw = gw; maskGh = gh; maskBits = bits; hasBody = true;
                        }
                        else
                        {
                            hasBody = false; // 全空=模型未加载或异常，回退为不穿透（避免整窗穿透）
                        }
                    }
                }
                break;
            case "btn_bounds":
                if (data.TryGetProperty("id", out var idProp))
                {
                    string id = idProp.GetString() ?? "";
                    double x = data.TryGetProperty("x", out var p) ? p.GetDouble() : 0;
                    double y = data.TryGetProperty("y", out var p2) ? p2.GetDouble() : 0;
                    double w = data.TryGetProperty("w", out var p3) ? p3.GetDouble() : 0;
                    double h = data.TryGetProperty("h", out var p4) ? p4.GetDouble() : 0;
                    lock (buttonsLock)
                    {
                        int idx = buttons.FindIndex(b => b.Id == id);
                        if (idx >= 0)
                            buttons[idx] = (id, x, y, w, h);
                        else
                        {
                            buttons.Add((id, x, y, w, h));
                            // 记一条：没进这张表的按钮会被穿透判定漏掉（悬停时按钮"隐身"就是这个原因）
                            AlifeLog.LogInformation($"[桌宠] 注册可交互按钮：{id}");
                        }
                    }
                }
                break;
            case "ui_bounds":
                {
                    List<(string Id, double X, double Y, double W, double H)> list = new();
                    if (data.TryGetProperty("items", out var itemsProp) && itemsProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement it in itemsProp.EnumerateArray())
                        {
                            string id = it.TryGetProperty("id", out var ip) ? (ip.GetString() ?? "") : "";
                            double x = it.TryGetProperty("x", out var xv) ? xv.GetDouble() : 0;
                            double y = it.TryGetProperty("y", out var yv) ? yv.GetDouble() : 0;
                            double w = it.TryGetProperty("w", out var wv) ? wv.GetDouble() : 0;
                            double h = it.TryGetProperty("h", out var hv) ? hv.GetDouble() : 0;
                            if (w > 0 && h > 0) list.Add((id, x, y, w, h));
                        }
                    }
                    lock (uiLock) { uiRects.Clear(); uiRects.AddRange(list); }
                }
                break;
            case "forever_dblclick":
                SetMouseThrough(!mouseThrough);
                break;
        }
    }

    public void SetMouseThrough(bool enabled)
    {
        mouseThrough = enabled;
        regionsDirty = true;
        bridge.SendMessage("lock_state", new { on = mouseThrough });
        // 常态/穿透态都持续运行智能穿透循环（不 StopLoop）
        if (cancellationTokenSource == null)
            StartLoop();
        else
            ApplyIgnoreState(); // 立即按新状态刷新一次
    }

    void StartLoop()
    {
        if (cancellationTokenSource != null)
            return;
        cancellationTokenSource = new CancellationTokenSource();
        MouseThroughLoop(cancellationTokenSource.Token);
    }
    void StopLoop()
    {
        cancellationTokenSource?.Cancel();
        cancellationTokenSource?.Dispose();
        cancellationTokenSource = null;
    }

    async void MouseThroughLoop(CancellationToken cancellationToken)
    {
        int idleTicks = 0;
        try
        {
            while (cancellationToken.IsCancellationRequested == false)
            {
                await Task.Delay(50, cancellationToken);
                try
                {
                    ApplyIgnoreState();

                    // 主进程驱动按钮"靠近才显示"（穿透态下前端收不到 mousemove，由全局光标轮询判定）
                    Rectangle b = window.Bounds;
                    Point c = window.CursorScreenPoint;
                    SendBtnNear(c.X - b.X, c.Y - b.Y);

                    // 兜底：每秒强制重算一次区域（窗口尺寸/视图变化后前端可能来不及重报）
                    if (++idleTicks >= 20)
                    {
                        idleTicks = 0;
                        regionsDirty = true;
                    }
                }
                catch (Exception)
                {
                    // 单次失败不能让循环退出，否则窗口会永久卡在上一次的区域状态
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            AlifeLog.LogError(e);
        }
    }

    /// <summary>
    /// 按当前状态刷新「可交互区域」。Alife 5 没有整窗鼠标事件开关，
    /// 而是声明哪些窗口内矩形接收输入，其余位置的点击直接落给桌面。
    /// </summary>
    void ApplyIgnoreState()
    {
        // 编辑模式：整窗强制可交互（暂停智能穿透），否则画框/拖动手柄收不到鼠标事件
        bool edit = hub.EditMode;
        if (edit != lastEditMode) { lastEditMode = edit; regionsDirty = true; }
        if (edit)
        {
            if (regionsDirty) { regionsDirty = false; PushInputRegions(null); }
            return;
        }

        if (regionsDirty == false) return;
        regionsDirty = false;
        PushInputRegions(BuildInputRegions());
    }

    Rectangle[] BuildInputRegions()
    {
        List<Rectangle> list = new();

        // 人形掩码：按行合并连续格子，减少矩形数量
        if (mouseThrough == false && hasBody && maskBits != null)
        {
            for (int gy = 0; gy < maskGh; gy++)
            {
                int runStart = -1;
                for (int gx = 0; gx <= maskGw; gx++)
                {
                    bool filled = gx < maskGw && maskBits[gy * maskGw + gx];
                    if (filled && runStart < 0) runStart = gx;
                    else if (filled == false && runStart >= 0)
                    {
                        list.Add(new Rectangle(runStart * MaskStep, gy * MaskStep, (gx - runStart) * MaskStep, MaskStep));
                        runStart = -1;
                    }
                }
            }
        }

        // 互动区（模型坐标 → 窗口坐标）：常态才接收；穿透态下只有永久区
        if (mouseThrough == false)
        {
            foreach (PetRegionConfig region in regionModule.GetRegions())
                AddRegionRect(list, region);
        }
        AddRegionRect(list, regionModule.GetForeverRegion());

        // 按钮 + 弹层（窗口内坐标，直接可用）
        lock (buttonsLock)
        {
            foreach ((string Id, double X, double Y, double W, double H) btn in buttons)
                AddRect(list, btn.X, btn.Y, btn.W, btn.H);
        }
        lock (uiLock)
        {
            foreach ((string Id, double X, double Y, double W, double H) ui in uiRects)
                AddRect(list, ui.X, ui.Y, ui.W, ui.H);
        }

        return list.ToArray();
    }

    void AddRegionRect(List<Rectangle> list, PetRegionConfig region)
    {
        (double x1, double y1) = regionModule.ToWindow(region.MinX, region.MinY);
        (double x2, double y2) = regionModule.ToWindow(region.MaxX, region.MaxY);
        AddRect(list, Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));
    }

    static void AddRect(List<Rectangle> list, double x, double y, double w, double h)
    {
        if (w <= 0 || h <= 0) return;
        list.Add(Rectangle.FromLTRB(
            (int)Math.Floor(x),
            (int)Math.Floor(y),
            (int)Math.Ceiling(x + w),
            (int)Math.Ceiling(y + h)));
    }

    void PushInputRegions(Rectangle[]? regions) => _ = SetInputRegionsAsync(regions);

    async Task SetInputRegionsAsync(Rectangle[]? regions)
    {
        try
        {
            await window.SetInputRegionsAsync(regions);
            regionFailures = 0;
        }
        catch (Exception e)
        {
            // 单次失败不致命；连续失败就把窗口拉回整窗可交互，
            // 宁可短暂挡一下桌面也不能永久穿透（那样按钮就点不到了）。
            regionFailures++;
            if (regionFailures >= 3)
            {
                regionFailures = 0;
                try { await window.SetInputRegionsAsync(null); } catch { }
            }
            AlifeLog.LogWarning("刷新桌宠穿透区域失败：" + e.Message);
        }
    }

    /// <summary>主进程按钮靠近判定：发给前端 btn_near 消息，前端据此设 .near 类（靠近才显示）。</summary>
    void SendBtnNear(double cx, double cy)
    {
        List<object> states = new();
        lock (buttonsLock)
        {
            foreach ((string Id, double X, double Y, double W, double H) btn in buttons)
            {
                double r = Math.Max(btn.W, btn.H) / 2 + BtnProximity;
                double bx = btn.X + btn.W / 2, by = btn.Y + btn.H / 2;
                double dx = cx - bx, dy = cy - by;
                bool near = dx * dx + dy * dy <= r * r;
                states.Add(new { id = btn.Id, near });
            }
        }
        bridge.SendMessage("btn_near", new { states });
    }

}
