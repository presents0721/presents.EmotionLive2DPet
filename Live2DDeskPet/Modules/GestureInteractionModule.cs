using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using Alife.Foundation;
using Alife.Framework;

namespace presents.EmotionLive2DPet;

/// <summary>
/// 丰富交互模块：把鼠标手势、全局光标感知、桌面情境、全局热键识别成语义事件，
/// 拼成一句「发生了什么」上报给角色（她决定反应）。每种交互都能在设置面板里开关。
/// </summary>
public class GestureInteractionModule : IPetModule, IDisposable
{
    public string CssCode => @"
#pet-ctx-menu {
    position:fixed; display:none; min-width:118px;
    background:rgba(25,25,28,0.92); color:#eee; border-radius:8px;
    padding:4px; z-index:2300; font-size:12px; font-family:'Microsoft YaHei',sans-serif;
    box-shadow:0 8px 24px rgba(0,0,0,0.4); border:1px solid rgba(255,255,255,0.12);
}
#pet-ctx-menu.show { display:block; }
#pet-ctx-menu div { padding:5px 10px; border-radius:5px; cursor:pointer; white-space:nowrap; }
#pet-ctx-menu div:hover { background:rgba(255,255,255,0.16); }
#pet-settings-btn {
    position:fixed; right:15px; bottom:192px; width:28px; height:28px;
    background:rgba(0,0,0,0.4); backdrop-filter:blur(10px); border-radius:50%;
    display:flex; justify-content:center; align-items:center;
    color:white; cursor:pointer; z-index:2000;
    box-shadow:0 4px 10px rgba(0,0,0,0.2);
    border:1px solid rgba(255,255,255,0.15);
    opacity:0; transition:opacity 0.3s, background 0.3s;
}
#pet-settings-btn.near { opacity:1; }
#pet-settings-btn:hover { background:rgba(0,0,0,0.6); }
#pet-settings-panel {
    position:fixed; right:6px; top:6px; width:156px; max-height:calc(100% - 12px);
    display:none; flex-direction:column;
    background:rgba(22,22,26,0.94); color:#eee; border-radius:10px;
    z-index:2400; font-size:12px; font-family:'Microsoft YaHei',sans-serif;
    box-shadow:0 8px 24px rgba(0,0,0,0.45); border:1px solid rgba(255,255,255,0.12);
    overflow:hidden;
}
#pet-settings-panel.show { display:flex; }
#pet-settings-panel .ps-title { padding:7px 10px; font-weight:600; background:rgba(255,255,255,0.08); }
#ps-list { overflow-y:auto; padding:4px; }
#pet-settings-panel .ps-group { padding:6px 6px 2px; color:#8ab4f8; font-size:11px; }
#pet-settings-panel .ps-row {
    display:flex; justify-content:space-between; align-items:center;
    padding:4px 6px; border-radius:5px; cursor:pointer;
}
#pet-settings-panel .ps-row:hover { background:rgba(255,255,255,0.12); }
#pet-settings-panel .ps-tg { font-size:11px; color:#999; }
#pet-settings-panel .ps-row.on .ps-tg { color:#4ade80; }
#pet-settings-panel .ps-close { text-align:center; padding:6px; cursor:pointer; background:rgba(255,255,255,0.08); }
#pet-settings-panel .ps-close:hover { background:rgba(255,255,255,0.18); }
#pet-hint {
    position:fixed; left:50%; bottom:8px; transform:translateX(-50%);
    max-width:calc(100% - 16px); box-sizing:border-box;
    background:rgba(20,20,24,0.88); color:#eee; border-radius:8px;
    padding:5px 10px; font-size:11px; line-height:1.5; text-align:center;
    font-family:'Microsoft YaHei',sans-serif; z-index:2500;
    opacity:0; transition:opacity 0.25s; pointer-events:none;
}
#pet-hint.show { opacity:1; }
";

    public string HtmlCode => @"
<div id='pet-ctx-menu'>
    <div data-act='nod'>打个招呼</div>
    <div data-act='input'>打开输入框</div>
    <div data-act='mute'>安静一下</div>
    <div data-act='outfit'>外观与表演…</div>
    <div data-act='calibration'>表情校准…</div>
    <div data-act='models'>更换模型…</div>
    <div data-act='hide'>先躲起来</div>
    <div data-act='settings'>设置…</div>
</div>
<div id='pet-settings-btn' data-act='ps-toggle' title='设置'>
    <svg viewBox='0 0 24 24' width='16' height='16' fill='currentColor'>
        <path d='M19.14 12.94a7.07 7.07 0 0 0 0-1.88l2.03-1.58a.5.5 0 0 0 .12-.64l-1.92-3.32a.5.5 0 0 0-.6-.22l-2.39.96a7.03 7.03 0 0 0-1.63-.94l-.36-2.54a.5.5 0 0 0-.5-.42h-3.84a.5.5 0 0 0-.5.42l-.36 2.54c-.59.24-1.13.56-1.63.94l-2.39-.96a.5.5 0 0 0-.6.22L2.71 8.84a.5.5 0 0 0 .12.64l2.03 1.58a7.07 7.07 0 0 0 0 1.88l-2.03 1.58a.5.5 0 0 0-.12.64l1.92 3.32c.13.22.39.3.6.22l2.39-.96c.5.38 1.04.7 1.63.94l.36 2.54c.04.24.25.42.5.42h3.84c.25 0 .46-.18.5-.42l.36-2.54c.59-.24 1.13-.56 1.63-.94l2.39.96c.21.08.47 0 .6-.22l1.92-3.32a.5.5 0 0 0-.12-.64l-2.03-1.58zM12 15.6A3.6 3.6 0 1 1 12 8.4a3.6 3.6 0 0 1 0 7.2z'/>
    </svg>
</div>
<div id='pet-settings-panel'>
    <div class='ps-title'>设置</div>
    <div id='ps-list'></div>
    <div class='ps-close' data-act='ps-close'>关闭</div>
</div>
<div id='pet-hint'></div>
";

    public string JsCode => @"
(function() {
    if (window.__petGesture) return;
    window.__petGesture = true;
    window.__petSettings = {};
    window.__petItems = {};
    function view() { return window.petView || {mx:0, my:0, ms:1}; }
    var lastArea = '';
    function areaAt(x, y) {
        try {
            if (model && model.hitTest) {
                var a = model.hitTest(x, y);
                if (a && a.then) a.then(function(r) { if (r && r.length) lastArea = r[0]; });
                else if (a && a.length) lastArea = a[0];
            }
        } catch (e) {}
        return lastArea;
    }
    function send(kind, x, y, meta) {
        if (window.petEditOn) return;
        var v = view();
        postMessage({type:'gesture', kind:kind, x:x, y:y, mx:v.mx, my:v.my, ms:v.ms, area:areaAt(x, y), meta:meta || {}});
    }
    function onCanvas(e) { return e.target && e.target.tagName === 'CANVAS'; }

    // ---- 提示条 ----
    window.__petHint = function(text, ms) {
        var h = document.getElementById('pet-hint');
        if (!h) return;
        h.textContent = text;
        h.classList.add('show');
        if (h.__t) clearTimeout(h.__t);
        h.__t = setTimeout(function() { h.classList.remove('show'); }, ms || 4000);
    };

    // ---- 弹层/面板：上报矩形，避免被穿透判定漏掉 ----
    function reportUi() {
        var items = [];
        var ids = ['pet-settings-panel', 'pet-outfit-panel', 'pet-cal-panel', 'pet-model-panel', 'pet-ctx-menu', 'region-toolbar', 'region-meta', 'pet-height-panel'];
        for (var i = 0; i < ids.length; i++) {
            var el = document.getElementById(ids[i]);
            if (!el || !el.classList.contains('show')) continue;
            var r = el.getBoundingClientRect();
            if (r.width <= 0 || r.height <= 0) continue;
            items.push({id: el.id, x: r.left, y: r.top, w: r.width, h: r.height});
        }
        postMessage({type:'ui_bounds', items: items});
    }
    // 换装等其他模块的面板变化后也能复用这套穿透矩形上报
    window.__petReportUi = reportUi;

    // ---- 设置面板（按描述动态渲染：分组 + 开关/数值/选项） ----
    function valueText(it) {
        if (it.type === 'number') return it.value;
        if (it.type === 'choice') {
            var opts = it.options || [];
            var i = opts.indexOf(it.value);
            return (it.optionLabels || [])[i >= 0 ? i : 0] || it.value;
        }
        return it.value === '1' ? '开' : '关';
    }
    function renderSettings(items) {
        var list = document.getElementById('ps-list');
        if (!list) return;
        window.__petItems = {};
        var lastGroup = null;
        list.innerHTML = '';
        items.forEach(function(it) {
            window.__petItems[it.key] = it;
            window.__petSettings[it.key] = it.value;
            if (it.group !== lastGroup) {
                lastGroup = it.group;
                var g = document.createElement('div');
                g.className = 'ps-group';
                g.textContent = it.group;
                list.appendChild(g);
            }
            var row = document.createElement('div');
            row.className = 'ps-row' + (it.value === '1' || it.type !== 'bool' ? ' on' : '');
            row.setAttribute('data-key', it.key);
            var lab = document.createElement('span');
            lab.textContent = it.label;
            var tg = document.createElement('span');
            tg.className = 'ps-tg';
            tg.textContent = valueText(it);
            row.appendChild(lab);
            row.appendChild(tg);
            list.appendChild(row);
        });
        reportUi();
    }
    function nextValue(it) {
        if (it.type === 'number') {
            var n = parseFloat(it.value || '0') || 0;
            n += it.step || 1;
            if (it.max && n > it.max) n = it.min || 0;
            return String(n);
        }
        if (it.type === 'choice') {
            var opts = it.options || [];
            if (!opts.length) return it.value;
            var i = opts.indexOf(it.value);
            return opts[(i + 1) % opts.length];
        }
        return it.value === '1' ? '0' : '1';
    }
    var tipShownJs = false;
    messageBus.on('pet_settings', function(msg) {
        renderSettings(msg.items || []);
        if (!tipShownJs) {
            tipShownJs = true;
            window.__petHint('右键我可以打开菜单，靠近我会浮出按钮，点齿轮可逐项开关交互', 6000);
        }
    });
    messageBus.on('pet_settings_open', function() {
        var p = document.getElementById('pet-settings-panel');
        if (p) p.classList.toggle('show');
        setTimeout(reportUi, 0);
        if (p && p.classList.contains('show')) window.__petHint('点条目即可切换：开关直接切、数值点一下递增、选项循环', 4500);
    });

    document.addEventListener('click', function(e) {
        var t = e.target && e.target.closest ? e.target.closest('[data-act],[data-key]') : null;
        if (!t) return;
        var act = t.getAttribute('data-act');
        if (act === 'ps-close') { var p = document.getElementById('pet-settings-panel'); if (p) p.classList.remove('show'); reportUi(); return; }
        if (act === 'ps-toggle') { postMessage({type:'pet_menu', action:'settings'}); return; }
        if (act === 'outfit') {
            // 换装面板由 OutfitModule 自己渲染，这里只负责转发打开事件
            var m0 = document.getElementById('pet-ctx-menu'); if (m0) m0.classList.remove('show');
            reportUi();
            messageBus._dispatch({type:'pet_outfit_open'});
            return;
        }
        if (act === 'calibration') {
            // 校准面板由 CalibrationModule 渲染
            var m1 = document.getElementById('pet-ctx-menu'); if (m1) m1.classList.remove('show');
            reportUi();
            messageBus._dispatch({type:'pet_calibration_open'});
            return;
        }
        if (act === 'models') {
            // 更换模型面板由 ModelPickerModule 渲染
            var m2 = document.getElementById('pet-ctx-menu'); if (m2) m2.classList.remove('show');
            reportUi();
            messageBus._dispatch({type:'pet_model_open'});
            return;
        }
        var key = t.getAttribute('data-key');
        if (key) {
            var it = window.__petItems[key];
            if (it) postMessage({type:'setting_set', key:key, value: nextValue(it)});
            return;
        }
        // mp- 开头的是「更换模型」面板自己的按钮，由该模块处理，不能当成右键菜单项
        if (act && act.indexOf('mp-') !== 0) { var m = document.getElementById('pet-ctx-menu'); if (m) m.classList.remove('show'); reportUi(); postMessage({type:'pet_menu', action:act}); }
    }, true);

    var down = null, lpTimer = null;
    var rawLen = 0, revs = 0, lastDir = 0, strokeSent = false;

    window.addEventListener('mousedown', function(e) {
        if (window.petEditOn || e.button !== 0 || !onCanvas(e)) return;
        down = {x:e.clientX, y:e.clientY, moved:false};
        rawLen = 0; revs = 0; lastDir = 0; strokeSent = false;
        if (lpTimer) clearTimeout(lpTimer);
        lpTimer = setTimeout(function() { if (down && !down.moved) send('longpress', down.x, down.y); }, 600);
    }, true);

    // 拖拽时桌宠窗口跟着光标走，clientX/clientY 几乎不变，
    // 「是否在动 / 来回方向」改用鼠标原始增量 movementX/Y 判断。
    window.addEventListener('mousemove', function(e) {
        if (!down) return;
        var mx = e.movementX || 0, my = e.movementY || 0;
        if (mx === 0 && my === 0) return;
        rawLen += Math.abs(mx) + Math.abs(my);
        if (!down.moved && rawLen > 8) {
            down.moved = true;
            if (lpTimer) { clearTimeout(lpTimer); lpTimer = null; }
        }
        var dir = Math.abs(mx) >= Math.abs(my) ? (mx > 0 ? 1 : -1) : (my > 0 ? 2 : -2);
        if (lastDir !== 0 && dir === -lastDir) revs++;
        lastDir = dir;
        if (!strokeSent && revs >= 3 && rawLen > 60) { strokeSent = true; send('stroke', down.x, down.y, {axis: Math.abs(dir) === 1 ? 'h' : 'v'}); }
    }, true);

    window.addEventListener('mouseup', function(e) {
        if (!down) return;
        if (lpTimer) { clearTimeout(lpTimer); lpTimer = null; }
        down = null;
    }, true);

    window.addEventListener('dblclick', function(e) {
        if (window.petEditOn || !onCanvas(e)) return;
        send('dblclick', e.clientX, e.clientY);
    }, true);

    window.addEventListener('wheel', function(e) {
        if (window.petEditOn) return;
        send('wheel', e.clientX, e.clientY, {dir: e.deltaY < 0 ? 'up' : 'down'});
    }, {capture:true, passive:true});

    window.addEventListener('contextmenu', function(e) {
        if (window.petEditOn || !onCanvas(e)) return;
        e.preventDefault();
        if (window.__petSettings.menu === '0') return;
        var m = document.getElementById('pet-ctx-menu');
        if (!m) return;
        m.style.left = Math.min(e.clientX, Math.max(0, window.innerWidth - 140)) + 'px';
        m.style.top = Math.min(e.clientY, Math.max(0, window.innerHeight - 170)) + 'px';
        m.classList.add('show');
        reportUi();
    }, true);
    document.addEventListener('click', function(e) {
        var m = document.getElementById('pet-ctx-menu');
        if (m && !(e.target.closest && e.target.closest('#pet-ctx-menu'))) { m.classList.remove('show'); reportUi(); }
    }, true);
    setInterval(reportUi, 400);

    messageBus.on('pet_hint', function(msg) {
        if (window.__petHint) window.__petHint((msg && msg.text) || '', (msg && msg.ms) || 4000);
    });
})();
";

    readonly PetBridge bridge;
    readonly PetWindow window;
    readonly RegionInteractionModule regionModule;
    readonly PetInteractionHub hub;
    readonly System.Threading.Timer desktopTimer;

    readonly object gate = new();
    readonly Dictionary<string, long> lastReport = new();

    double dwellX = double.NaN, dwellY = double.NaN;
    long dwellSince;
    bool dwellReported;
    long lastCursorMoveTick;
    bool petHidden;
    bool edgeLatched;
    long outsideSince;
    bool tipShown;
    bool hotkeyHideRegistered;

    // ---- 系统托盘：躲起来之后唯一常驻的恢复入口 ----
    // 窗口 Hide() 后页面里所有按钮都不可见、SkipTaskbar 也无法从任务栏找回，
    // 因此必须有一个窗口外的入口（参考 presents.DeskPet3D 的托盘实现）。
    static NotifyIcon? petTray;
    static volatile bool trayReady;
    static bool trayStarted;
    static SynchronizationContext? traySync;
    static GestureInteractionModule? activeInstance;

    public GestureInteractionModule(PetBridge bridge, PetWindow window, RegionInteractionModule regionModule, PetInteractionHub hub)
    {
        this.bridge = bridge;
        this.window = window;
        this.regionModule = regionModule;
        this.hub = hub;

        foreach ((string Key, string Label, string Group, bool Default) in PetInteractionHub.BoolCatalog)
            hub.Register(new PetSetting { Key = Key, Label = Label, Group = Group, Type = PetSettingType.Bool, Default = Default ? "1" : "0" });
        hub.Register(new PetSetting { Key = "seen_tip", Label = "首次提示", Group = "其它", Type = PetSettingType.Bool, Default = "0", Hidden = true });

        activeInstance = this;
        lastCursorMoveTick = Environment.TickCount64;
        bridge.OnMessage += OnMessage;
        window.MouseMoved += OnMouseMoved;
        desktopTimer = new System.Threading.Timer(_ => DesktopTick(), null, 2000, 2000);
        CreateTray();   // 托盘线程里同时注册全局热键（WM_HOTKEY 依赖该线程的消息泵）
        SendSettings();
    }

    public void Dispose()
    {
        bridge.OnMessage -= OnMessage;
        window.MouseMoved -= OnMouseMoved;
        desktopTimer.Dispose();
        if (activeInstance == this) activeInstance = null;
        DisposeTray();
    }

    void SendSettings() => bridge.SendMessage("pet_settings", new { items = hub.Snapshot() });

    /// <summary>供外部（管理界面）改完开关后，把最新值推给桌宠窗口内的设置面板。</summary>
    public void PushSettings() => SendSettings();

    void OnMessage(string type, JsonElement data)
    {
        switch (type)
        {
            case "gesture":
                HandleGesture(data);
                break;
            case "pet_menu":
                HandleMenu(data);
                break;
            case "setting_set":
                {
                    string key = data.TryGetProperty("key", out JsonElement kp) ? (kp.GetString() ?? "") : "";
                    string value = data.TryGetProperty("value", out JsonElement vp) ? (vp.GetString() ?? "") : "";
                    if (!string.IsNullOrEmpty(key)) { hub.SetValue(key, value); SendSettings(); }
                    break;
                }
            case "region_ready":
                SendSettings();
                break;
        }
    }

    void HandleGesture(JsonElement data)
    {
        if (hub.EditMode) return;
        string kind = data.TryGetProperty("kind", out JsonElement kp) ? (kp.GetString() ?? "") : "";
        if (kind is not ("longpress" or "stroke" or "dblclick" or "wheel")) return;

        // 手势交互统一与「互动区域」结合：只有落在启用了该手势的区域上才上报，
        // 话术按区域各自配置（留空用默认模板）。没落在区域上就当普通操作，不打扰角色。
        double x = data.TryGetProperty("x", out JsonElement xp) ? xp.GetDouble() : 0;
        double y = data.TryGetProperty("y", out JsonElement yp) ? yp.GetDouble() : 0;
        string? msg = regionModule.GestureMessageAt(kind, x, y);
        if (msg == null) return;
        if (kind is "wheel" or "stroke") { if (!Allow(kind, 2500)) return; }
        hub.Report(msg);
    }

    void HandleMenu(JsonElement data)
    {
        string action = data.TryGetProperty("action", out JsonElement ap) ? (ap.GetString() ?? "") : "";
        switch (action)
        {
            case "nod":
                if (hub.IsOn("menu")) hub.Report("哥哥跟桌宠打了个招呼");
                break;
            case "input":
                bridge.SendMessage("input_toggle", new { on = true });
                break;
            case "mute":
                if (hub.IsOn("menu")) hub.Report("哥哥想让自己安静一会儿");
                break;
            case "hide":
                HidePet(true);
                break;
            case "settings":
                bridge.SendMessage("pet_settings_open", new { });
                if (!tipShown && !hub.IsOn("seen_tip"))
                {
                    tipShown = true;
                    hub.SetValue("seen_tip", "1");
                }
                break;
        }
    }

    bool Allow(string key, int ms)
    {
        lock (gate)
        {
            long now = Environment.TickCount64;
            if (lastReport.TryGetValue(key, out long t) && now - t < ms) return false;
            lastReport[key] = now;
            return true;
        }
    }

    void OnMouseMoved()
    {
        Rectangle b = window.Bounds;
        Point c = window.CursorScreenPoint;
        double cx = c.X - b.X, cy = c.Y - b.Y;
        long now = Environment.TickCount64;
        lastCursorMoveTick = now;

        if (hub.EditMode || !hub.IsOn("dwell")) return;
        if (cx < 0 || cx > b.Width || cy < 0 || cy > b.Height) return;

        string part = regionModule.DescribeRegionAt(cx, cy) ?? "";
        if (string.IsNullOrEmpty(part)) { dwellX = double.NaN; dwellReported = false; return; }
        if (double.IsNaN(dwellX) || Math.Abs(cx - dwellX) + Math.Abs(cy - dwellY) > 12) { dwellX = cx; dwellY = cy; dwellSince = now; dwellReported = false; }
        else if (!dwellReported && now - dwellSince > 2200)
        {
            dwellReported = true;
            if (Allow("dwell", 5000)) hub.Report($"哥哥的鼠标在桌宠的「{part}」上停了好一会儿");
        }
    }

    void DesktopTick()
    {
        try
        {
            long now = Environment.TickCount64;
            if (!hub.EditMode && hub.IsOn("neglect") && now - lastCursorMoveTick > 10 * 60 * 1000)
            {
                if (Allow("neglect", 30 * 60 * 1000)) hub.Report("哥哥已经有很久没理我了");
            }
            if (hub.EditMode) return;
            Rectangle b = window.Bounds;
            Size screen = window.ScreenSize;
            int sw = screen.Width, sh = screen.Height;
            if (hub.IsOn("edge"))
            {
                const int Sn = 8;
                bool near = false; int nx = b.X, ny = b.Y;
                if (b.X <= Sn) { nx = 0; near = true; }
                else if (b.X + b.Width >= sw - Sn) { nx = sw - b.Width; near = true; }
                if (b.Y <= Sn) { ny = 0; near = true; }
                else if (b.Y + b.Height >= sh - Sn) { ny = sh - b.Height; near = true; }
                if (near && !edgeLatched)
                {
                    edgeLatched = true;
                    b.X = nx; b.Y = ny;
                    _ = window.SetBoundsAsync(b);
                    if (Allow("edge", 15000)) hub.Report("桌宠被拖到屏幕边缘贴住了");
                }
                else if (!near) edgeLatched = false;
            }
            if (hub.IsOn("return"))
            {
                bool fullyOut = b.X + b.Width <= 0 || b.X >= sw || b.Y + b.Height <= 0 || b.Y >= sh;
                if (fullyOut)
                {
                    if (outsideSince == 0) outsideSince = now;
                    else if (now - outsideSince > 1500) { outsideSince = 0; ReturnHome(); if (Allow("return", 15000)) hub.Report("桌宠被丢出了屏幕，自己爬回来了"); }
                }
                else outsideSince = 0;
            }
        }
        catch { }
    }

    async void ReturnHome()
    {
        try
        {
            Rectangle b = window.Bounds;
            Size screen = window.ScreenSize;
            int sw = screen.Width, sh = screen.Height;
            int tx = sw - b.Width - 60, ty = sh - b.Height;
            while (Math.Abs(b.X - tx) + Math.Abs(b.Y - ty) > 6)
            {
                b.X += Math.Sign(tx - b.X) * Math.Min(24, Math.Abs(tx - b.X));
                b.Y += Math.Sign(ty - b.Y) * Math.Min(24, Math.Abs(ty - b.Y));
                _ = window.SetBoundsAsync(b);
                await System.Threading.Tasks.Task.Delay(16);
            }
        }
        catch { }
    }

    void HidePet(bool hide)
    {
        // 托盘和热键都没注册上时绝对不能隐藏：窗口 SkipTaskbar，Hide() 之后就再也没有入口能把它叫回来。
        if (hide && !trayReady && !hotkeyHideRegistered)
        {
            AlifeLog.LogWarning("[Live2D桌宠] 托盘与恢复热键均不可用，已取消隐藏");
            bridge.SendMessage("pet_hint", new { text = "托盘和热键都没注册成功，这次先不躲了", ms = 4000 });
            return;
        }

        try
        {
            if (hide)
            {
                _ = window.HideAsync();
                petHidden = true;
                NotifyHidden();
            }
            else if (petHidden)
            {
                _ = window.ShowAsync();
                petHidden = false;
                // 隐藏再显示后置顶状态可能被宿主重置，补一次，避免被其他窗口盖住
                _ = window.SetTopMostAsync(true);
            }
        }
        catch (Exception e) { AlifeLog.LogWarning("[Live2D桌宠] 切换显隐失败：" + e.Message); }
    }

    /// <summary>托盘左键或菜单「显示 / 隐藏」：在窗口外把桌宠唤回来。</summary>
    void ToggleFromTray() => HidePet(!petHidden);

    void OpenSettingsFromTray()
    {
        try
        {
            if (petHidden)
            {
                _ = window.ShowAsync();
                petHidden = false;
            }
            bridge.SendMessage("pet_settings_open", new { });
        }
        catch (Exception e) { AlifeLog.LogWarning("[Live2D桌宠] 托盘打开设置失败：" + e.Message); }
    }

    /// <summary>躲起来时给一个气泡提示，告诉用户去哪儿叫她出来（否则会以为永久隐身了）。</summary>
    static void NotifyHidden()
    {
        try
        {
            SynchronizationContext? ctx = traySync;
            void Show()
            {
                try
                {
                    NotifyIcon? tray = petTray;
                    if (tray is null) return;
                    tray.BalloonTipTitle = "桌宠躲起来了";
                    tray.BalloonTipText = "左键点击托盘图标，或右键选「显示 / 隐藏」，就能把我叫回来。";
                    tray.ShowBalloonTip(4000);
                }
                catch { }
            }
            if (ctx is not null) ctx.Post(_ => Show(), null);
            else Show();
        }
        catch { }
    }

    // 托盘用 WinForms NotifyIcon：本宿主没有给插件全局的 WinForms 消息泵，
    // 需要在独立 STA 线程上跑 Application.Run 来驱动菜单与鼠标事件（与 presents.DeskPet3D 一致）。
    void CreateTray()
    {
        if (trayStarted) return;
        trayStarted = true;
        try
        {
            Thread thread = new(() =>
            {
                try
                {
                    try
                    {
                        traySync = new WindowsFormsSynchronizationContext();
                        SynchronizationContext.SetSynchronizationContext(traySync);
                    }
                    catch { }

                    NotifyIcon tray = new()
                    {
                        Icon = System.Drawing.SystemIcons.Application,
                        Text = "Live2D 桌宠（左键显示/隐藏）",
                        Visible = true,
                    };

                    ContextMenuStrip menu = new();
                    ToolStripMenuItem showHide = new("显示 / 隐藏");
                    showHide.Click += (_, _) => ToggleFromTray();
                    ToolStripMenuItem openSettings = new("设置…");
                    openSettings.Click += (_, _) => OpenSettingsFromTray();
                    menu.Items.Add(showHide);
                    menu.Items.Add(openSettings);
                    tray.ContextMenuStrip = menu;

                    tray.MouseClick += (_, e) =>
                    {
                        if (e.Button == MouseButtons.Left) ToggleFromTray();
                    };

                    petTray = tray;
                    trayReady = true;
                    AlifeLog.LogInformation("[Live2D桌宠] 系统托盘已就绪（躲起来后可从托盘唤回）");
                    RegisterHotkeys();   // 热键消息由本线程的消息泵分发
                    Application.Run();   // 本 STA 线程的消息泵，支撑托盘菜单与点击
                }
                catch (Exception e) { AlifeLog.LogWarning("[Live2D桌宠] 托盘线程内失败：" + e.Message); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
        }
        catch (Exception e) { AlifeLog.LogWarning("[Live2D桌宠] 托盘创建失败（不影响运行）：" + e.Message); }
    }

    static void DisposeTray()
    {
        try
        {
            NotifyIcon? tray = petTray;
            petTray = null;
            trayReady = false;
            trayStarted = false;
            SynchronizationContext? ctx = traySync;
            traySync = null;

            // NotifyIcon 没有 BeginInvoke：用托盘 STA 线程的同步上下文派发销毁，再结束该线程的消息泵
            if (ctx is not null)
            {
                ctx.Post(_ =>
                {
                    try
                    {
                        if (tray is not null) { tray.Visible = false; tray.Dispose(); }
                    }
                    catch { }
                    try { UnregisterHotkeys(); } catch { }
                    try { System.Windows.Forms.Application.Exit(); } catch { }
                }, null);
            }
            else if (tray is not null)
            {
                try { tray.Visible = false; tray.Dispose(); } catch { }
            }
        }
        catch { }
    }

    // Alife 5 宿主删除 Electron，全局热键改用 Win32 RegisterHotKey 实现（MOD_CONTROL|MOD_ALT + 按键）。
    // 同一热键二次注册会返回失败，这里静默忽略即可；注销在 Dispose 统一处理。
    const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002;
    const uint WM_HOTKEY = 0x0312;
    const int HWND_MESSAGE = -3;
    static IntPtr msgWnd;
    static Dictionary<int, Action> hotkeyHandlers = new();
    static int nextHotkeyId = 0xC000;
    // 窗口类注册后系统只保留函数指针，必须用静态字段持有托管委托，否则被 GC 回收时会崩
    static readonly WndProcDelegate WndProcRef = WndProc;

    static bool RegisterGlobalHotkey(int key, Action action)
    {
        // 用消息窗口（HWND_MESSAGE）承载通知，不侵入主线程窗口
        if (msgWnd == IntPtr.Zero)
        {
            string cls = Guid.NewGuid().ToString("N");
            IntPtr hInstance = GetModuleHandleW(null);
            WNDCLASS wc = new() { lpfnWndProc = WndProcRef, lpszClassName = cls, hInstance = hInstance };
            RegisterClassW(ref wc);
            msgWnd = CreateWindowExW(0, cls, "PetHotkey", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, hInstance, IntPtr.Zero);
        }
        int id = nextHotkeyId++;
        if (!RegisterHotKey(msgWnd, id, MOD_CONTROL | MOD_ALT, (uint)key)) return false;
        hotkeyHandlers[id] = action;
        return true;
    }

    /// <summary>释放全部热键（须在创建消息窗口的同一条线程上调用）。</summary>
    static void UnregisterHotkeys()
    {
        foreach (int id in hotkeyHandlers.Keys)
        {
            try { UnregisterHotKey(msgWnd, id); } catch { }
        }
        hotkeyHandlers.Clear();
        if (msgWnd != IntPtr.Zero)
        {
            try { DestroyWindow(msgWnd); } catch { }
            msgWnd = IntPtr.Zero;
        }
    }

    static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_HOTKEY && hotkeyHandlers.TryGetValue((int)wParam, out Action? action))
        {
            try { action?.Invoke(); } catch { }
            return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    void RegisterHotkeys()
    {
        try
        {
            RegisterKey("P", () => { if (hub.IsOn("hotkey")) hub.Report("哥哥用热键戳了我一下"); });
            RegisterKey("M", () => { if (hub.IsOn("hotkey")) hub.Report("哥哥用热键让我安静一会儿"); });
            hotkeyHideRegistered = RegisterKey("H", () => HidePet(!petHidden));
            if (!hotkeyHideRegistered)
                AlifeLog.LogWarning("[Live2D桌宠] 显隐热键未注册，请用系统托盘的「显示 / 隐藏」唤回桌宠");
        }
        catch (Exception e)
        {
            AlifeLog.LogWarning("桌宠全局热键初始化失败: " + e.Message);
        }
    }

    bool RegisterKey(string ch, Action callback)
    {
        try { return RegisterGlobalHotkey(char.ToUpperInvariant(ch[0]), callback); }
        catch (Exception e)
        {
            AlifeLog.LogWarning("桌宠热键注册失败(Ctrl+Alt+" + ch + "): " + e.Message);
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASS { public uint style; public WndProcDelegate lpfnWndProc; public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground; [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName; [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName; }
    delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassW(ref WNDCLASS wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string? name);
}
