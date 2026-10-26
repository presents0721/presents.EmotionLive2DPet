using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Framework;

namespace presents.EmotionLive2DPet;

/// <summary>
/// 桌宠窗口内「更换模型」面板所需的目录与切换回调。
/// 模块本身不依赖插件本体，避免 DI 循环依赖。
/// </summary>
public sealed record PetModelCatalog(
    Func<IReadOnlyList<PetModelEntry>> Enumerate,
    Func<string, Task<bool>> SwitchAsync);

/// <summary>
/// 窗口内快速更换模型：递归列出默认模型目录与外部目录里的所有模型，
/// 点一下原地热切换，不用再去功能管理里翻文件夹、抄文件名。
/// 面板与「外观与表演」「表情校准」共用同一套位置/透明度/收起规则。
/// </summary>
public sealed class ModelPickerModule(PetBridge bridge, PetModelCatalog catalog, PetModelMetadata metadata)
    : IPetModule
{
    bool subscribed;

    public string CssCode => @"
#pet-model-panel {
    position:fixed; right:6px; top:6px; width:250px; max-height:72%;
    display:none; flex-direction:column;
    background:rgba(22,22,26,0.58); color:#eee; border-radius:10px;
    z-index:2400; font-size:12px; font-family:'Microsoft YaHei',sans-serif;
    box-shadow:0 8px 24px rgba(0,0,0,0.45); border:1px solid rgba(255,255,255,0.12);
    overflow:hidden; backdrop-filter:blur(3px);
    transition:background 0.18s;
}
#pet-model-panel:hover { background:rgba(22,22,26,0.93); }
#pet-model-panel.show { display:flex; }
#pet-model-panel .mp-title {
    display:flex; justify-content:space-between; align-items:center;
    padding:7px 10px; font-weight:600; background:rgba(255,255,255,0.08);
    cursor:move; user-select:none;
}
#pet-model-panel .mp-fold { font-size:10px; font-weight:400; color:#bbb; cursor:pointer; padding:0 3px; }
#pet-model-panel .mp-fold:hover { color:#fff; }
#pet-model-panel.folded .mp-search,
#pet-model-panel.folded #mp-list,
#pet-model-panel.folded .mp-status,
#pet-model-panel.folded .mp-foot { display:none; }
#pet-model-panel.folded { width:auto; }
#pet-model-panel .mp-search { padding:6px 8px; border-bottom:1px solid rgba(255,255,255,0.08); }
#pet-model-panel .mp-search input {
    width:100%; box-sizing:border-box; padding:4px 7px; border-radius:6px;
    border:1px solid rgba(255,255,255,0.14); background:rgba(255,255,255,0.08);
    color:#eee; outline:none; font-size:11px;
}
#pet-model-panel .mp-search input::placeholder { color:#888; }
#mp-list { overflow-y:auto; padding:4px; }
#pet-model-panel .mp-row {
    padding:5px 7px; border-radius:6px; cursor:pointer;
    border-left:2px solid transparent;
}
#pet-model-panel .mp-row:hover { background:rgba(255,255,255,0.12); }
#pet-model-panel .mp-row.cur { background:rgba(244,114,182,0.16); border-left-color:#f472b6; }
#pet-model-panel .mp-name { font-size:12px; }
#pet-model-panel .mp-loc { font-size:10px; color:#999; margin-top:2px; word-break:break-all; line-height:1.4; }
#pet-model-panel .mp-tag {
    font-size:9px; padding:0 4px; margin-left:5px; border-radius:3px;
    background:rgba(244,114,182,0.22); color:#f9a8d4;
}
#pet-model-panel .mp-tag.ext { background:rgba(138,180,248,0.18); color:#8ab4f8; }
#pet-model-panel .mp-status {
    padding:5px 10px; font-size:11px; color:#8ab4f8; min-height:16px;
    border-top:1px solid rgba(255,255,255,0.08);
}
#pet-model-panel .mp-status.err { color:#f87171; }
#pet-model-panel .mp-foot { display:flex; background:rgba(255,255,255,0.08); }
#pet-model-panel .mp-foot div { flex:1; text-align:center; padding:6px; cursor:pointer; }
#pet-model-panel .mp-foot div:hover { background:rgba(255,255,255,0.18); }
#pet-model-panel .mp-empty { padding:8px 10px; color:#aaa; font-size:11px; line-height:1.6; }
";

    public string HtmlCode => @"
<div id='pet-model-panel'>
    <div class='mp-title'><span>更换模型</span><span class='mp-fold' data-act='mp-fold'>收起</span></div>
    <div class='mp-search'><input id='mp-search' type='text' placeholder='搜索模型名或路径' /></div>
    <div id='mp-list'></div>
    <div class='mp-status' id='mp-status'>点模型名即可原地切换</div>
    <div class='mp-foot'>
        <div data-act='mp-folder'>打开模型文件夹</div>
        <div data-act='mp-close'>关闭</div>
    </div>
</div>
";

    public string JsCode => @"
(function() {
    if (window.__petModelPicker) return;
    window.__petModelPicker = true;

    var POS_KEY = '__petModelPickerPos';
    var data = { models: [], current: '', switching: false };
    var dragOffset = null;

    function el(id) { return document.getElementById(id); }

    function clampPos(node, left, top) {
        var maxLeft = Math.max(0, window.innerWidth - node.offsetWidth);
        var maxTop = Math.max(0, window.innerHeight - node.offsetHeight);
        return { left: Math.max(0, Math.min(maxLeft, left)), top: Math.max(0, Math.min(maxTop, top)) };
    }
    function place(left, top) {
        var p = el('pet-model-panel');
        if (!p) return;
        var pos = clampPos(p, left, top);
        p.style.left = pos.left + 'px';
        p.style.top = pos.top + 'px';
        p.style.right = 'auto';
    }
    function restorePos() {
        var p = el('pet-model-panel');
        if (!p) return;
        var saved = null;
        try { saved = JSON.parse(localStorage.getItem(POS_KEY) || 'null'); } catch (e) { saved = null; }
        if (saved && typeof saved.left === 'number' && typeof saved.top === 'number') place(saved.left, saved.top);
        // 默认贴右下角，避免挡住人物脸
        else place(window.innerWidth - p.offsetWidth - 6, window.innerHeight - p.offsetHeight - 6);
    }

    function setStatus(text, isError) {
        var s = el('mp-status');
        if (!s) return;
        s.textContent = text;
        s.className = 'mp-status' + (isError ? ' err' : '');
    }

    function render() {
        var list = el('mp-list');
        if (!list) return;
        list.innerHTML = '';

        var input = el('mp-search');
        var query = input ? String(input.value || '').trim().toLowerCase() : '';
        var models = data.models.filter(function(m) {
            if (!query) return true;
            return String(m.name || '').toLowerCase().indexOf(query) >= 0
                || String(m.location || '').toLowerCase().indexOf(query) >= 0;
        });

        if (!models.length) {
            var empty = document.createElement('div');
            empty.className = 'mp-empty';
            empty.textContent = data.models.length
                ? '没有匹配的模型'
                : '没有找到模型，请把模型放进 Storage/Live2DDeskPet/Models';
            list.appendChild(empty);
            return;
        }

        models.forEach(function(m) {
            var row = document.createElement('div');
            row.className = 'mp-row' + (m.current ? ' cur' : '');
            // 不用 data-act，避免被通用右键菜单的点击委托误当成菜单项
            row.setAttribute('data-id', String(m.identifier || m.name || ''));

            var name = document.createElement('div');
            name.className = 'mp-name';
            name.textContent = m.name || '(未命名)';
            if (m.current) {
                var tag = document.createElement('span');
                tag.className = 'mp-tag';
                tag.textContent = '当前';
                name.appendChild(tag);
            } else if (m.bundled === false) {
                var ext = document.createElement('span');
                ext.className = 'mp-tag ext';
                ext.textContent = '外部';
                name.appendChild(ext);
            }
            var loc = document.createElement('div');
            loc.className = 'mp-loc';
            loc.textContent = m.location || '';

            row.appendChild(name);
            row.appendChild(loc);
            list.appendChild(row);
        });
    }

    messageBus.on('pet_model_list', function(msg) {
        data.models = (msg && msg.models) || [];
        data.current = (msg && msg.current) || '';
        data.switching = !!(msg && msg.switching);
        if (msg && msg.ok === true) setStatus('已切换到 ' + data.current);
        else if (msg && msg.ok === false) setStatus('切换失败，已保留原模型', true);
        else if (!data.switching) setStatus('点模型名即可原地切换');
        render();
        if (window.__petReportUi) window.__petReportUi();
    });

    messageBus.on('pet_model_open', function() {
        var p = el('pet-model-panel');
        if (!p) return;
        var opening = !p.classList.contains('show');
        if (opening) {
            // 与「外观与表演」「表情校准」互斥，避免三个浮层叠在一起挡人物
            ['pet-outfit-panel', 'pet-cal-panel'].forEach(function(id) {
                var other = el(id);
                if (other) other.classList.remove('show');
            });
        }
        p.classList.toggle('show');
        if (p.classList.contains('show')) {
            restorePos();
            render();
            postMessage({type: 'pet_model_pull'});
        }
        if (window.__petReportUi) window.__petReportUi();
    });

    document.addEventListener('pointerdown', function(e) {
        var title = e.target && e.target.closest ? e.target.closest('#pet-model-panel .mp-title') : null;
        if (!title || (e.target.closest && e.target.closest('.mp-fold'))) return;
        var p = el('pet-model-panel');
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
        var p = el('pet-model-panel');
        if (!p) return;
        try {
            localStorage.setItem(POS_KEY, JSON.stringify({ left: parseFloat(p.style.left) || 0, top: parseFloat(p.style.top) || 0 }));
        } catch (e) {}
        if (window.__petReportUi) window.__petReportUi();
    });

    document.addEventListener('click', function(e) {
        var row = e.target && e.target.closest ? e.target.closest('#pet-model-panel .mp-row') : null;
        if (row) {
            if (data.switching) return;
            var rowId = row.getAttribute('data-id');
            if (!rowId) return;
            if (row.classList.contains('cur')) { setStatus('已经是当前模型'); return; }
            data.switching = true;
            setStatus('正在切换…');
            postMessage({type: 'pet_model_switch', identifier: rowId});
            return;
        }

        var t = e.target && e.target.closest ? e.target.closest('#pet-model-panel [data-act]') : null;
        if (!t) return;
        var act = t.getAttribute('data-act');
        if (act === 'mp-close') {
            var p = el('pet-model-panel');
            if (p) p.classList.remove('show');
            if (window.__petReportUi) window.__petReportUi();
            return;
        }
        if (act === 'mp-fold') {
            var panel = el('pet-model-panel');
            if (!panel) return;
            var folded = panel.classList.toggle('folded');
            t.textContent = folded ? '展开' : '收起';
            if (window.__petReportUi) window.__petReportUi();
            return;
        }
        if (act === 'mp-folder') { postMessage({type: 'pet_model_folder'}); return; }
    });

    var search = el('mp-search');
    if (search) search.addEventListener('input', function() { render(); });
})();
";

    public void OnStart()
    {
        if (subscribed) return;
        bridge.OnMessage += OnMessage;
        subscribed = true;
    }

    void OnMessage(string type, JsonElement data)
    {
        if (type == "pet_model_pull")
        {
            SendList();
            return;
        }

        if (type == "pet_model_folder")
        {
            OpenModelFolder();
            return;
        }

        if (type == "pet_model_switch")
        {
            string identifier = data.TryGetProperty("identifier", out JsonElement idProp) ? (idProp.GetString() ?? "") : "";
            _ = SwitchAsync(identifier);
        }
    }

    async Task SwitchAsync(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier)) return;

        bool ok = false;
        try { ok = await catalog.SwitchAsync(identifier); }
        catch (Exception e) { AlifeLog.LogWarning("[桌宠] 窗口内切换模型失败：" + e.Message); }

        SendList(ok);
    }

    void SendList(bool? ok = null)
    {
        string current = metadata.ModelDirectory;
        List<object> models = catalog.Enumerate()
            .Select(entry => (object)new
            {
                name = entry.Name,
                location = entry.Location,
                identifier = entry.Identifier,
                bundled = entry.IsBundled,
                current = string.Equals(entry.Directory, current, StringComparison.OrdinalIgnoreCase),
            })
            .ToList();

        bridge.SendMessage("pet_model_list", new
        {
            models,
            current = Path.GetFileName(current),
            switching = false,
            ok,
        });
    }

    void OpenModelFolder()
    {
        string target = metadata.ModelDirectory;
        if (string.IsNullOrWhiteSpace(target) || Directory.Exists(target) == false) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
        }
        catch (Exception e) { AlifeLog.LogWarning("[桌宠] 打开模型文件夹失败：" + e.Message); }
    }
}
