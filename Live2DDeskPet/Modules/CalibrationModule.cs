using System;
using System.Collections.Generic;
using System.Text.Json;
using Alife.Foundation;
using Alife.Framework;

namespace presents.EmotionLive2DPet;

/// <summary>校准记忆：模型名 → 语义通道 → 倍率（1 = 完全按 cdi3 区间自动校准）。</summary>
public class CalibrationMemory
{
    public Dictionary<string, Dictionary<string, double>> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 进程内共享的校准倍率。前端 profile.js 生成表演档案时读取，
/// 校准面板写入后立即重建档案，不用重启插件。
/// </summary>
public static class CalibrationStore
{
    public static Dictionary<string, Dictionary<string, double>> Models { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static Dictionary<string, double> GainsFor(string? modelName)
    {
        string key = modelName ?? "";
        return Models.TryGetValue(key, out Dictionary<string, double>? gains)
            ? new Dictionary<string, double>(gains, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    }

    public static void SetGain(string? modelName, string channel, double gain)
    {
        string key = modelName ?? "";
        if (!Models.TryGetValue(key, out Dictionary<string, double>? gains))
        {
            gains = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            Models[key] = gains;
        }
        // 回到 1 就从记忆里删掉，避免以后自动校准升级被旧值卡住
        if (Math.Abs(gain - 1) < 0.0005) gains.Remove(channel);
        else gains[channel] = Math.Round(gain, 2);
    }

    public static void ResetModel(string? modelName, string? channel)
    {
        string key = modelName ?? "";
        if (string.IsNullOrEmpty(channel))
        {
            Models.Remove(key);
            return;
        }
        if (Models.TryGetValue(key, out Dictionary<string, double>? gains)) gains.Remove(channel);
    }
}

/// <summary>
/// 表情校准面板：把前端算出的 FACS 通道覆盖率（映射到哪个参数、来源、输出区间）显示出来，
/// 并允许对单个通道手动调倍率，解决「这个模型的嘴形参数幅度就是偏小」这类模型差异问题。
/// </summary>
public sealed class CalibrationModule : IPetModule, IDisposable
{
    // 与 profile.js 的 FACS_KEYS 一致，只用于面板排序与中文名
    static readonly string[] ChannelOrder =
    [
        "eyeOpen", "eyeSmile", "eyeSquint", "eyeBlinkL", "eyeBlinkR",
        "mouthSmile", "mouthFrown", "mouthOpen", "mouthPucker",
        "browInnerUp", "browOuterUp", "browDown",
        "gazeX", "gazeY", "headX", "headY", "headZ", "bodyX", "bodyY", "bodyZ",
        "blush", "tear", "sweat", "breath",
    ];

    readonly PetBridge bridge;
    readonly PetModelMetadata metadata;
    readonly StorageSystem storage;
    readonly PetStorageKey storageKey;
    readonly string memoryKey;
    bool loaded;
    bool subscribed;

    /// <summary>前端最近一次算出的覆盖率报告（原样 JSON，面板渲染时再解析）。</summary>
    string? reportJson;
    string? reportModel;

    public CalibrationModule(PetBridge bridge, PetModelMetadata metadata, StorageSystem storage, PetStorageKey storageKey)
    {
        this.bridge = bridge;
        this.metadata = metadata;
        this.storage = storage;
        this.storageKey = storageKey;
        memoryKey = $"{storageKey.Value}/EmotionLive2DPet/Calibration";
    }

    public string CssCode => @"
#pet-cal-panel {
    position:fixed; right:6px; top:6px; width:232px; max-height:72%;
    display:none; flex-direction:column;
    background:rgba(22,22,26,0.58); color:#eee; border-radius:10px;
    z-index:2400; font-size:12px; font-family:'Microsoft YaHei',sans-serif;
    box-shadow:0 8px 24px rgba(0,0,0,0.45); border:1px solid rgba(255,255,255,0.12);
    overflow:hidden; backdrop-filter:blur(3px);
    transition:background 0.18s;
}
#pet-cal-panel:hover { background:rgba(22,22,26,0.93); }
#pet-cal-panel.show { display:flex; }
#pet-cal-panel .cs-title {
    display:flex; justify-content:space-between; align-items:center;
    padding:7px 10px; font-weight:600; background:rgba(255,255,255,0.08);
    cursor:move; user-select:none;
}
#pet-cal-panel .cs-fold { font-size:10px; font-weight:400; color:#bbb; cursor:pointer; padding:0 3px; }
#pet-cal-panel .cs-fold:hover { color:#fff; }
#pet-cal-panel.folded .cs-sum,
#pet-cal-panel.folded #pet-cal-list,
#pet-cal-panel.folded .cs-foot { display:none; }
#pet-cal-panel.folded { width:auto; }
#pet-cal-panel .cs-sum {
    padding:5px 10px; font-size:11px; color:#8ab4f8; line-height:1.5;
    border-bottom:1px solid rgba(255,255,255,0.08);
}
#pet-cal-panel .cs-sum.warn { color:#fbbf24; }
#pet-cal-list { overflow-y:auto; padding:4px; }
#pet-cal-panel .cs-row { padding:4px 6px; border-radius:5px; }
#pet-cal-panel .cs-row:hover { background:rgba(255,255,255,0.08); }
#pet-cal-panel .cs-row.miss { opacity:0.45; }
#pet-cal-panel .cs-head { display:flex; justify-content:space-between; align-items:center; }
#pet-cal-panel .cs-name { font-size:12px; }
#pet-cal-panel .cs-src {
    font-size:10px; padding:0 4px; margin-left:4px; border-radius:3px;
    background:rgba(138,180,248,0.18); color:#8ab4f8;
}
#pet-cal-panel .cs-src.heuristic { background:rgba(251,191,36,0.18); color:#fbbf24; }
#pet-cal-panel .cs-src.derived { background:rgba(255,255,255,0.12); color:#bbb; }
#pet-cal-panel .cs-target {
    font-size:10px; color:#999; margin-top:2px; word-break:break-all; line-height:1.4;
}
#pet-cal-panel .cs-ctrl { display:flex; align-items:center; gap:4px; }
#pet-cal-panel .cs-btn {
    width:18px; height:18px; line-height:17px; text-align:center; cursor:pointer;
    border-radius:4px; background:rgba(255,255,255,0.12); font-size:12px; user-select:none;
}
#pet-cal-panel .cs-btn:hover { background:rgba(255,255,255,0.26); }
#pet-cal-panel .cs-gain { min-width:36px; text-align:center; font-size:11px; color:#4ade80; }
#pet-cal-panel .cs-gain.off { color:#999; }
#pet-cal-panel .cs-foot { display:flex; background:rgba(255,255,255,0.08); }
#pet-cal-panel .cs-foot div { flex:1; text-align:center; padding:6px; cursor:pointer; }
#pet-cal-panel .cs-foot div:hover { background:rgba(255,255,255,0.18); }
#pet-cal-panel .cs-empty { padding:8px 10px; color:#aaa; font-size:11px; line-height:1.6; }
";

    public string HtmlCode => @"
<div id='pet-cal-panel'>
    <div class='cs-title'><span>表情校准</span><span class='cs-fold' data-act='cs-fold'>收起</span></div>
    <div class='cs-sum' id='cs-sum'>等待模型加载…</div>
    <div id='pet-cal-list'></div>
    <div class='cs-foot'>
        <div data-act='cs-reset-all'>全部恢复默认</div>
        <div data-act='cs-close'>关闭</div>
    </div>
</div>
";

    public string JsCode => @"
(function() {
    if (window.__petCalibration) return;
    window.__petCalibration = true;

    var L = {
        eyeOpen: '眼睛开合', eyeSmile: '笑眼', eyeSquint: '眯眼',
        eyeBlinkL: '左眼眨眼', eyeBlinkR: '右眼眨眼',
        mouthSmile: '微笑', mouthFrown: '嘴角下撇', mouthOpen: '张嘴', mouthPucker: '撅嘴',
        browInnerUp: '内眉上抬', browOuterUp: '外眉上抬', browDown: '眉毛下压',
        gazeX: '眼球左右', gazeY: '眼球上下',
        headX: '头部左右', headY: '头部俯仰', headZ: '头部倾斜',
        bodyX: '身体左右', bodyY: '身体前后', bodyZ: '身体倾斜',
        blush: '脸红', tear: '泪目', sweat: '汗滴', breath: '呼吸'
    };
    var SRC = { 'standard-group': '模型分组', 'standard-id': '标准ID', 'name-match': '名称匹配', derived: '共用参数', heuristic: '推断' };
    var data = { report: null, gains: {} };

    function keyOf(el) { return el.getAttribute ? el.getAttribute('data-key') : null; }

    function fmtGain(v) {
        var n = Number(v);
        if (!isFinite(n)) n = 1;
        return (Math.round(n * 10) / 10).toFixed(1) + '×';
    }

    function row(key, entry) {
        var el = document.createElement('div');
        el.className = 'cs-row' + (entry && entry.mapped ? '' : ' miss');
        el.setAttribute('data-key', key);

        var head = document.createElement('div');
        head.className = 'cs-head';

        var name = document.createElement('span');
        name.className = 'cs-name';
        name.textContent = L[key] || key;
        if (entry && entry.mapped) {
            var tag = document.createElement('span');
            var src = entry.source || '';
            tag.className = 'cs-src' + (src === 'name-match' || src === 'heuristic' ? ' heuristic' : src === 'derived' ? ' derived' : '');
            tag.textContent = SRC[src] || src;
            name.appendChild(tag);
        }
        head.appendChild(name);

        var ctrl = document.createElement('span');
        ctrl.className = 'cs-ctrl';
        var gain = Number(data.gains[key]);
        if (!isFinite(gain) || gain <= 0) gain = 1;
        var adjustable = !!(entry && entry.mapped && entry.adjustable);
        if (adjustable) {
            var minus = document.createElement('span');
            minus.className = 'cs-btn';
            minus.setAttribute('data-act', 'cs-gain');
            minus.setAttribute('data-key', key);
            minus.setAttribute('data-delta', '-0.2');
            minus.textContent = '−';
            var val = document.createElement('span');
            val.className = 'cs-gain';
            val.textContent = fmtGain(gain);
            var plus = document.createElement('span');
            plus.className = 'cs-btn';
            plus.setAttribute('data-act', 'cs-gain');
            plus.setAttribute('data-key', key);
            plus.setAttribute('data-delta', '0.2');
            plus.textContent = '+';
            ctrl.appendChild(minus); ctrl.appendChild(val); ctrl.appendChild(plus);
        } else {
            var fixed = document.createElement('span');
            fixed.className = 'cs-gain off';
            fixed.textContent = entry && entry.mapped ? '绝对值' : '无通道';
            ctrl.appendChild(fixed);
        }
        head.appendChild(ctrl);
        el.appendChild(head);

        var target = document.createElement('div');
        target.className = 'cs-target';
        if (entry && entry.mapped) {
            var ids = (entry.targets || []).join('+');
            var range = entry.range ? (entry.range[0] + '~' + entry.range[1]) : '?';
            target.textContent = ids + ' · ' + range;
        } else {
            target.textContent = '这个模型没有对应参数，表情会由其它通道合成';
        }
        el.appendChild(target);
        return el;
    }

    function render() {
        var list = document.getElementById('pet-cal-list');
        var sum = document.getElementById('cs-sum');
        if (!list || !sum) return;
        while (list.firstChild) list.removeChild(list.firstChild);

        var report = data.report;
        if (!report || !report.perKey) {
            var empty = document.createElement('div');
            empty.className = 'cs-empty';
            empty.textContent = '模型尚未加载完成，稍等片刻后重新打开面板';
            list.appendChild(empty);
            sum.textContent = '等待模型加载…';
            return;
        }

        var mapped = report.mappedKeyCount || 0;
        var total = report.totalKeyCount || 0;
        var miss = report.unmappedKeys || [];
        sum.textContent = '已映射 ' + mapped + '/' + total + ' 个表情通道'
            + (miss.length ? '，缺 ' + miss.length + ' 个' : '');
        sum.className = 'cs-sum' + (miss.length > 6 ? ' warn' : '');

        var keys = ['eyeOpen', 'eyeSmile', 'eyeSquint', 'eyeBlinkL', 'eyeBlinkR',
            'mouthSmile', 'mouthFrown', 'mouthOpen', 'mouthPucker',
            'browInnerUp', 'browOuterUp', 'browDown',
            'gazeX', 'gazeY', 'headX', 'headY', 'headZ', 'bodyX', 'bodyY', 'bodyZ',
            'blush', 'tear', 'sweat', 'breath'];
        for (var i = 0; i < keys.length; i++) list.appendChild(row(keys[i], report.perKey[keys[i]]));
    }

    function sendGain(key, gain) {
        postMessage({type: 'calibration_gain', key: key, gain: gain});
    }

    // ---- 面板位置：默认贴右下角、可拖标题栏移动、位置记忆；半透明避免挡住人物（与「外观与表演」同一套） ----
    var POS_KEY = 'petCalPanelPos';
    var dragOffset = null;

    function clampPos(el, left, top) {
        var maxLeft = Math.max(0, window.innerWidth - el.offsetWidth);
        var maxTop = Math.max(0, window.innerHeight - el.offsetHeight);
        return { left: Math.max(0, Math.min(maxLeft, left)), top: Math.max(0, Math.min(maxTop, top)) };
    }
    function place(left, top) {
        var p = document.getElementById('pet-cal-panel');
        if (!p) return;
        var pos = clampPos(p, left, top);
        p.style.left = pos.left + 'px';
        p.style.top = pos.top + 'px';
        p.style.right = 'auto';
    }
    function restorePos() {
        var p = document.getElementById('pet-cal-panel');
        if (!p) return;
        var saved = null;
        try { saved = JSON.parse(localStorage.getItem(POS_KEY) || 'null'); } catch (e) { saved = null; }
        if (saved && typeof saved.left === 'number' && typeof saved.top === 'number') place(saved.left, saved.top);
        // 默认贴右下角，把头部让出来
        else place(window.innerWidth - p.offsetWidth - 6, window.innerHeight - p.offsetHeight - 6);
    }

    messageBus.on('calibration_data', function(msg) {
        var parsed = null;
        if (msg && msg.report) { try { parsed = JSON.parse(msg.report); } catch (e) { parsed = null; } }
        data.report = parsed;
        data.gains = (msg && msg.gains) || {};
        render();
    });

    messageBus.on('pet_calibration_open', function() {
        var p = document.getElementById('pet-cal-panel');
        if (!p) return;
        if (!p.classList.contains('show')) {
            // 与「外观与表演」面板互斥，避免两个浮层叠在一起挡住人物
            var outfit = document.getElementById('pet-outfit-panel');
            if (outfit) outfit.classList.remove('show');
            var picker = document.getElementById('pet-model-panel');
            if (picker) picker.classList.remove('show');
        }
        p.classList.toggle('show');
        if (p.classList.contains('show')) {
            restorePos();
            render();
            postMessage({type: 'calibration_pull'});
        }
        if (window.__petReportUi) window.__petReportUi();
    });

    document.addEventListener('pointerdown', function(e) {
        var title = e.target && e.target.closest ? e.target.closest('#pet-cal-panel .cs-title') : null;
        if (!title || (e.target.closest && e.target.closest('.cs-fold'))) return;
        var p = document.getElementById('pet-cal-panel');
        if (!p) return;
        var r = p.getBoundingClientRect();
        dragOffset = { dx: e.clientX - r.left, dy: e.clientY - r.top };
        e.preventDefault();
    });
    document.addEventListener('pointermove', function(e) {
        if (!dragOffset) return;
        place(e.clientX - dragOffset.dx, e.clientY - dragOffset.dy);
    });
    document.addEventListener('pointerup', function() {
        if (!dragOffset) return;
        dragOffset = null;
        var p = document.getElementById('pet-cal-panel');
        if (!p) return;
        try {
            localStorage.setItem(POS_KEY, JSON.stringify({ left: parseFloat(p.style.left) || 0, top: parseFloat(p.style.top) || 0 }));
        } catch (e) {}
        if (window.__petReportUi) window.__petReportUi();
    });

    document.addEventListener('click', function(e) {
        var t = e.target && e.target.closest ? e.target.closest('#pet-cal-panel [data-act]') : null;
        if (!t) return;
        var act = t.getAttribute('data-act');
        if (act === 'cs-close') {
            var p = document.getElementById('pet-cal-panel');
            if (p) p.classList.remove('show');
            if (window.__petReportUi) window.__petReportUi();
            return;
        }
        if (act === 'cs-fold') {
            var panel = document.getElementById('pet-cal-panel');
            if (!panel) return;
            var folded = panel.classList.toggle('folded');
            t.textContent = folded ? '展开' : '收起';
            if (window.__petReportUi) window.__petReportUi();
            return;
        }
        if (act === 'cs-reset-all') { postMessage({type: 'calibration_reset', key: ''}); return; }
        if (act === 'cs-gain') {
            var key = t.getAttribute('data-key');
            if (!key) return;
            var delta = Number(t.getAttribute('data-delta')) || 0;
            var current = Number(data.gains[key]);
            if (!isFinite(current) || current <= 0) current = 1;
            var next = Math.min(3, Math.max(0.2, Math.round((current + delta) * 10) / 10));
            sendGain(key, next);
            return;
        }
    });
})();
";

    public void OnStart()
    {
        if (subscribed == false)
        {
            bridge.OnMessage += OnMessage;
            subscribed = true;
        }
        Load();
    }

    public void Dispose() { }

    void OnMessage(string type, JsonElement data)
    {
        if (type == "loaded")
        {
            // 成功加载时不要清报告：profile.js 在这条消息之前就已经发过 calibration_report，
            // 清掉会让面板一直停在「模型尚未加载完成」。失败时才清，避免显示上一个模型的旧报告。
            bool ok = data.TryGetProperty("ok", out JsonElement okProp) == false || okProp.ValueKind != JsonValueKind.False;
            if (ok == false)
            {
                reportJson = null;
                reportModel = null;
            }
            return;
        }

        if (type == "calibration_report")
        {
            reportJson = data.TryGetProperty("report", out JsonElement report) && report.ValueKind == JsonValueKind.Object
                ? report.GetRawText()
                : null;
            reportModel = metadata.ModelName;
            SendData();
            return;
        }

        if (type == "calibration_pull")
        {
            // 模块启动晚于模型首次上报，报告可能没收到：面板打开时主动让前端补发一次
            if (string.IsNullOrEmpty(reportJson) || reportModel != metadata.ModelName)
                bridge.SendMessage("calibration_report_request", new { });
            SendData();
            return;
        }

        if (type == "calibration_gain")
        {
            string key = data.TryGetProperty("key", out JsonElement keyProp) ? (keyProp.GetString() ?? "") : "";
            double gain = data.TryGetProperty("gain", out JsonElement gainProp) && gainProp.TryGetDouble(out double parsed) ? parsed : 1;
            if (key.Length == 0) return;
            CalibrationStore.SetGain(metadata.ModelName, key, Math.Clamp(gain, 0.2, 3.0));
            Save();
            SendData();
            // 前端拿到倍率后立即重建表演档案，不用重启插件
            bridge.SendMessage("calibration_gains", new { gains = CalibrationStore.GainsFor(metadata.ModelName) });
            return;
        }

        if (type != "calibration_reset") return;

        string resetKey = data.TryGetProperty("key", out JsonElement resetProp) ? (resetProp.GetString() ?? "") : "";
        CalibrationStore.ResetModel(metadata.ModelName, resetKey);
        Save();
        SendData();
        bridge.SendMessage("calibration_gains", new { gains = CalibrationStore.GainsFor(metadata.ModelName) });
    }

    void SendData()
    {
        bridge.SendMessage("calibration_data", new {
            model = metadata.ModelName,
            gains = CalibrationStore.GainsFor(metadata.ModelName),
            report = reportModel == metadata.ModelName ? (reportJson ?? "") : "",
        });
    }

    void Load()
    {
        if (loaded) return;
        loaded = true;
        try
        {
            CalibrationMemory? memory = storage.GetObject<CalibrationMemory>(memoryKey);
            if (memory?.Models == null) return;
            CalibrationStore.Models.Clear();
            foreach (KeyValuePair<string, Dictionary<string, double>> model in memory.Models)
                CalibrationStore.Models[model.Key] = new Dictionary<string, double>(model.Value, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e)
        {
            AlifeLog.LogWarning("读取表情校准设置失败\n" + e.Message);
        }
    }

    void Save()
    {
        try
        {
            CalibrationMemory memory = new();
            foreach (KeyValuePair<string, Dictionary<string, double>> model in CalibrationStore.Models)
                memory.Models[model.Key] = new Dictionary<string, double>(model.Value, StringComparer.OrdinalIgnoreCase);
            storage.SetObject(memoryKey, memory);
        }
        catch (Exception e)
        {
            AlifeLog.LogWarning("保存表情校准设置失败\n" + e.Message);
        }
    }
}
