using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Alife.Foundation;
using Alife.Framework;

namespace presents.EmotionLive2DPet;

/// <summary>开关面板记忆：服饰/形态开关、固定表情、固定动作，以及「具身表演」总开关。</summary>
public class OutfitMemory
{
    /// <summary>键是参数 ID。0=显示/穿上，1=隐藏/脱下；没有记录表示模型默认状态。</summary>
    public Dictionary<string, int> States { get; set; } = new();

    /// <summary>自动情绪表演（情绪/FACS/视线/原生动作）。关掉后只保留用户手动固定的表演。</summary>
    public bool AutoPerformance { get; set; } = true;

    /// <summary>一直保持的表情或姿势表情名；为空表示不固定。</summary>
    public string? Expression { get; set; }

    /// <summary>一直循环的动作，格式「动作组|序号」；为空表示不固定。</summary>
    public string? Motion { get; set; }
}

public enum SwitchCategory
{
    Outfit,
    Expression,
    Action,
    /// <summary>词表没覆盖到的开关/表情（Q版形态、水印之外的奇怪命名…），统一塞进「其他」栏兜底。</summary>
    Other,
}

/// <summary>可钉住的开关项：服饰部件、形态开关等单参数开关。</summary>
public record OutfitItem(string ParamId, string Label, string SourceName, string ParamName, double OnValue, bool VisibleWhenOn, SwitchCategory Category)
{
    /// <summary>「显示」状态要钉住的值（有些开关的「开」是 1，有些是 3，还有些是「写 1 表示藏起来」）。</summary>
    public double VisibleValue => VisibleWhenOn ? OnValue : 0;

    /// <summary>「隐藏」状态要钉住的值。</summary>
    public double HiddenValue => VisibleWhenOn ? 0 : OnValue;
}

/// <summary>可一直保持的表情 / 姿势表情（按表情文件走，不硬钉参数）。</summary>
public record ExpressionOption(string Name, string Label, SwitchCategory Category);

/// <summary>可一直循环的动作文件。</summary>
public record MotionOption(string Key, string Name, string Group, int Index, double Duration, bool Loop);

/// <summary>
/// 外观与表演面板：自动识别模型里的三类开关并分类列出——
/// 表情（爱心眼/星星眼/脸红/眩晕眼…）、动作（动作文件 + 前倾/比心等姿势表情 + Q版形态）、服饰（外套/兽耳/尾巴/发卡/裙摆/光环…）。
/// 用户可手动固定表情、动作与服饰；关掉「具身表演」后情绪层不再抢参数，固定的表演会一直保持。
/// AI 侧通过 PetOutfit 工具控制服饰/形态开关，表情与动作沿用 Expression / Motion 工具。
/// </summary>
public sealed class OutfitModule(PetBridge bridge, PetModelMetadata metadata, StorageSystem storage, PetStorageKey storageKey)
    : IPetModule, IDisposable
{
    // 开关类动词：命中即视为「可切换」的开关，而不是普通情绪表情
    static readonly string[] SwitchVerbs =
    [
        "隐藏", "隱藏", "显示", "顯示", "开关", "開關", "开闭", "開閉", "切换", "切換",
        "脱下", "脫下", "脱掉", "脫掉", "穿上", "换上", "換上", "摘下", "戴上", "取下", "去掉", "移除",
        "关闭", "關閉", "开启", "開啟", "非表示", "表示", "脱ぐ", "はずす", "外す",
        "hide", "show", "toggle", "switch", "remove", "wear",
    ];

    // 「开关＝1 表示藏起来」的动词：隐藏外套 / 脱衣服 这类写 1 反而是把部件藏掉
    static readonly string[] HideVerbs =
    [
        "隐藏", "隱藏", "脱下", "脫下", "脱掉", "脫掉", "摘下", "取下", "去掉", "移除", "关闭", "非表示",
        "脱ぐ", "はずす", "外す", "hide", "remove", "take off",
    ];

    // 只用于判断「写 1 是藏起来」，不参与标签清洗：否则「脱衣服」会被洗成「衣服」，和真正的衣服开关重名
    static readonly string[] HideMarkers = [.. HideVerbs, "脱", "脫"];

    // 服饰部件名词：兽耳/尾巴/碎发/发卡/裙摆/光环…；单字「耳」「尾」兼容「7兔兔耳」这类参数名
    static readonly string[] OutfitNouns =
    [
        "兽耳", "獸耳", "猫耳", "貓耳", "兔耳", "狐耳", "狗耳", "精灵耳", "耳朵", "耳",
        "尾巴", "尾",
        "兽角", "獸角", "犄角", "独角", "龙角", "龍角", "恶魔角", "角饰",
        "碎发", "刘海", "瀏海", "前发", "前髪", "呆毛", "发夹", "发卡", "髮夾", "发饰", "发带", "发箍", "头箍", "头饰",
        "马尾", "双马尾", "雙马尾", "短发", "长发", "发型", "卷发", "挑染",
        "裙摆", "裙子", "裙", "半裙", "短裙", "长裙",
        "外套", "衣服", "上衣", "衬衫", "卫衣", "毛衣", "制服", "校服", "睡衣", "泳装", "泳衣", "和服", "浴衣", "裤子", "短裤", "长裤",
        "披风", "披風", "斗篷", "披肩", "围巾", "圍巾", "领带", "領帶", "领结", "蝴蝶结", "蝴蝶結", "项圈", "項圈", "颈饰", "项链", "腰带", "腰饰",
        "手套", "袖", "袜", "襪", "鞋", "靴", "拖鞋", "高跟鞋",
        "翅膀", "羽翼", "光环", "光環", "天使环", "光圈",
        "眼罩", "口罩", "眼镜", "眼鏡", "墨镜", "墨鏡", "太阳镜", "护目镜", "单片镜",
        "帽子", "王冠", "皇冠", "花饰", "花环", "花環", "耳环", "耳環", "耳钉", "耳坠", "铃铛", "鈴鐺",
        "纹身", "紋身", "水印", "挂件", "掛件", "配饰", "饰品", "飾品", "装饰", "裝飾", "挂饰", "背包", "勋章", "徽章",
        "ear", "tail", "hairpin", "hair clip", "hairclip", "hairband", "headband", "hair", "bangs", "bang", "fringe", "ahoge",
        "ponytail", "twintail", "braid", "ribbon", "bow", "skirt", "coat", "jacket", "clothes", "clothing", "shirt", "dress",
        "outfit", "uniform", "pajama", "swimsuit", "cape", "cloak", "scarf", "necktie", "tie", "collar", "necklace", "belt",
        "glove", "sleeve", "sock", "shoe", "boot", "heel", "wing", "halo", "eyepatch", "mask", "glasses", "sunglasses",
        "goggles", "monocle", "hat", "crown", "tiara", "headwear", "flower", "earring", "bell", "tattoo", "watermark",
        "accessory", "ornament", "bag", "badge",
        "しっぽ", "尻尾", "髪", "髪飾り", "リボン", "スカート", "コート", "上着", "マント", "マフラー", "ネクタイ",
        "チョーカー", "手袋", "靴下", "羽", "翼", "天使の輪", "眼帯", "マスク", "メガネ", "サングラス", "王冠",
        "イヤリング", "ピアス", "ベルト", "アクセサリー",
    ];

    // 动作 / 姿势 / 形态名词：动作文件之外，很多模型把它做成单参数或多参数表情
    static readonly string[] ActionNouns =
    [
        "前倾", "前傾", "倾身", "侧身", "侧头", "歪头", "低头", "抬头", "转头",
        "比心", "比耶", "剪刀手", "举手", "挥手", "摆手", "招手", "叉腰", "抱胸", "抱臂", "托腮", "猫爪", "拳头", "敬礼", "加油",
        "蹲", "坐", "站", "跳", "转圈", "摇头", "点头", "伸懒腰", "趴", "躺", "伸", "抓", "戳",
        "q版", "Q版", "qq人", "q版人", "q版化", "大头", "变小", "大小变", "变身", "形态", "形態", "兽形", "兽态", "拟态", "幼化", "成人化",
        "pose", "lean", "sit", "kneel", "wave", "point", "peace", "chibi", "squat", "stand", "form",
        "ポーズ", "ちび", "座り", "しゃがみ", "手振り",
    ];

    // 表情名词：爱心眼/星星眼/脸红/眩晕眼/黑脸/哭哭…
    static readonly string[] ExpressionNouns =
    [
        "爱心眼", "愛心眼", "星星眼", "心心眼", "眼冒", "冒星", "晕晕", "晕眩", "眩晕", "頭暈", "头晕",
        "脸红", "臉紅", "赤面", " blush", "害羞", "羞涩", "羞澀",
        "哭", "泪", "淚", "泣", "生气", "生氣", "怒", "愤怒", "黑脸", "黑臉", "阴影", "陰影", "阴暗", "无语", "無語",
        "笑", "微笑", "开心", "開心", "高兴", "汗水", "汗", "流汗", "白眼", "高光", "问号", "問號", "音符",
        "花痴", "花癡", "爱心", "愛心", "星星", "心", "眼", "泪眼", "泪目", "閉眼", "闭眼", "半睁", "眨眼",
        "heart", "star", "blush", "dizzy", "cry", "tear", "angry", "sad", "smile", "shy", "sweat",
        "question", "note", "shadow", "spiral", "love", "wink", "glasses",
        "ハート", "星", "赤面", "涙", "怒", "笑", "照れ", "ぐるぐる", "汗", "ウインク",
    ];

    // 姿势/动作词：仅名词命中时，出现这些词说明是在做动作（尾巴摆动、袖口飘动），不是穿脱
    static readonly string[] PoseWords =
    [
        "摆", "擺", "摇", "搖", "晃", "动", "動", "挥", "揮", "转", "轉", "跳", "走",
        "举", "舉", "伸", "握", "指", "拍", "摸", "抱", "戳", "扶", "靠", "躺", "蹲", "歪",
    ];

    static readonly string[] LabelStripWords = SwitchVerbs.Concat(HideVerbs).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    // 参数名常带分组序号与装饰字符："█开关 20  短发"、"7兔兔耳"、"16圆眼镜"
    static readonly Regex DecorationRegex = new("[█■#＃]|\\d+", RegexOptions.Compiled);

    // "R兽耳开闭" 这类贴在汉字前的左右标记
    static readonly Regex SideMarkerRegex = new("^[A-Za-z]{1,3}(?=[\\u4e00-\\u9fff])", RegexOptions.Compiled);

    static readonly Regex AsciiRegex = new("^[\\x00-\\x7F]+$", RegexOptions.Compiled);

    static readonly Regex CjkRegex = new("[\\u4e00-\\u9fff]", RegexOptions.Compiled);

    readonly Dictionary<string, int> states = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, OutfitItem> extraItems = new(StringComparer.OrdinalIgnoreCase);
    readonly string memoryKey = $"{storageKey.Value}/EmotionLive2DPet/Outfit";
    bool loaded;
    bool subscribed;
    bool autoPerformance = true;
    string? pinnedExpression;
    string? pinnedMotion;

    public string CssCode => @"
#pet-outfit-panel {
    position:fixed; left:6px; top:6px; width:176px; max-height:72%;
    display:none; flex-direction:column;
    background:rgba(22,22,26,0.58); color:#eee; border-radius:10px;
    z-index:2400; font-size:12px; font-family:'Microsoft YaHei',sans-serif;
    box-shadow:0 8px 24px rgba(0,0,0,0.45); border:1px solid rgba(255,255,255,0.12);
    overflow:hidden; backdrop-filter:blur(3px);
    transition:background 0.18s;
}
#pet-outfit-panel:hover { background:rgba(22,22,26,0.93); }
#pet-outfit-panel.show { display:flex; }
#pet-outfit-panel .os-title {
    display:flex; justify-content:space-between; align-items:center;
    padding:7px 10px; font-weight:600; background:rgba(255,255,255,0.08);
    cursor:move; user-select:none;
}
#pet-outfit-panel .os-fold { font-size:10px; font-weight:400; color:#bbb; cursor:pointer; padding:0 3px; }
#pet-outfit-panel .os-fold:hover { color:#fff; }
#pet-outfit-panel.folded .os-auto,
#pet-outfit-panel.folded .os-tabs,
#pet-outfit-panel.folded #os-list,
#pet-outfit-panel.folded .os-close { display:none; }
#pet-outfit-panel.folded { width:auto; }
#pet-outfit-panel .os-auto {
    display:flex; justify-content:space-between; align-items:center; cursor:pointer;
    padding:6px 10px; border-bottom:1px solid rgba(255,255,255,0.08);
}
#pet-outfit-panel .os-auto:hover { background:rgba(255,255,255,0.10); }
#pet-outfit-panel .os-auto-val { font-size:11px; color:#4ade80; }
#pet-outfit-panel .os-auto.off .os-auto-val { color:#f87171; }
#pet-outfit-panel .os-tabs { display:flex; background:rgba(255,255,255,0.05); }
#pet-outfit-panel .os-tab {
    flex:1; text-align:center; padding:6px 0; cursor:pointer; font-size:11px; color:#999;
    border-bottom:2px solid transparent;
}
#pet-outfit-panel .os-tab.on { color:#f472b6; border-bottom-color:#f472b6; background:rgba(255,255,255,0.06); }
#os-list { overflow-y:auto; padding:4px; }
#pet-outfit-panel .os-section { padding:6px 6px 2px; color:#8ab4f8; font-size:11px; }
#pet-outfit-panel .os-row {
    display:flex; justify-content:space-between; align-items:center;
    padding:5px 6px; border-radius:5px; cursor:pointer;
}
#pet-outfit-panel .os-row:hover { background:rgba(255,255,255,0.12); }
#pet-outfit-panel .os-row.active { background:rgba(244,114,182,0.16); }
#pet-outfit-panel .os-val { font-size:11px; color:#999; }
#pet-outfit-panel .os-row.on .os-val { color:#f472b6; }
#pet-outfit-panel .os-row.off .os-val { color:#4ade80; }
#pet-outfit-panel .os-row.active .os-val { color:#f472b6; }
#pet-outfit-panel .os-empty { padding:8px 10px; color:#aaa; font-size:11px; line-height:1.6; }
#pet-outfit-panel .os-close { text-align:center; padding:6px; cursor:pointer; background:rgba(255,255,255,0.08); }
#pet-outfit-panel .os-close:hover { background:rgba(255,255,255,0.18); }
";

    public string HtmlCode => @"
<div id='pet-outfit-panel'>
    <div class='os-title'><span>外观与表演</span><span class='os-fold' data-act='os-fold'>收起</span></div>
    <div class='os-auto' id='os-auto-row' data-act='auto'>
        <span>具身表演（自动情绪）</span><span class='os-auto-val' id='os-auto-val'>开</span>
    </div>
    <div class='os-tabs'>
        <div class='os-tab on' data-tab='expression'>表情</div>
        <div class='os-tab' data-tab='action'>动作</div>
        <div class='os-tab' data-tab='outfit'>服饰</div>
        <div class='os-tab' data-tab='other'>其他</div>
    </div>
    <div id='os-list'></div>
    <div class='os-close' data-act='os-close'>关闭</div>
</div>
";

    public string JsCode => @"
(function() {
    if (window.__petOutfit) return;
    window.__petOutfit = true;

    var state = { auto: true, expressions: [], motions: [], actions: [], others: [], pins: [] };
    var tab = 'expression';
    var motionTimer = null;
    var currentMotionKey = null;

    function pinStateText(v) {
        if (v === 1) return '隐藏';
        if (v === 0) return '显示';
        return '默认';
    }
    function section(title) {
        var list = document.getElementById('os-list');
        if (!list) return;
        var head = document.createElement('div');
        head.className = 'os-section';
        head.textContent = title;
        list.appendChild(head);
    }
    function empty(text) {
        var list = document.getElementById('os-list');
        if (!list) return;
        var box = document.createElement('div');
        box.className = 'os-empty';
        box.textContent = text;
        list.appendChild(box);
    }
    function addRow(label, valueText, opts) {
        var list = document.getElementById('os-list');
        if (!list) return;
        var row = document.createElement('div');
        row.className = 'os-row' + (opts.active ? ' active' : '') + (opts.pin ? (opts.state === 1 ? ' on' : opts.state === 0 ? ' off' : '') : '');
        row.setAttribute('data-kind', opts.kind);
        row.setAttribute('data-key', opts.key);
        if (opts.kind === 'pin') {
            row.setAttribute('data-outfit', opts.key);
            row.setAttribute('data-state', String(opts.state));
        }
        row.setAttribute('data-active', opts.active ? '1' : '0');
        var lab = document.createElement('span');
        lab.textContent = label;
        var val = document.createElement('span');
        val.className = 'os-val';
        val.textContent = valueText;
        row.appendChild(lab);
        row.appendChild(val);
        list.appendChild(row);
    }

    function renderList() {
        var list = document.getElementById('os-list');
        if (!list) return;
        list.innerHTML = '';

        if (tab === 'expression') {
            if (!state.expressions.length) empty('当前模型没有可固定的表情');
            state.expressions.forEach(function(it) {
                addRow(it.label, it.active ? '固定中' : '', { kind: 'expression', key: it.key, active: it.active });
            });
        } else if (tab === 'action') {
            if (state.motions.length) {
                section('动作文件（循环播放）');
                state.motions.forEach(function(it) {
                    addRow(it.label, it.active ? '循环中' : '', { kind: 'motion', key: it.key, active: it.active });
                });
            }
            if (state.actions.length) {
                section('姿势表情（一直保持）');
                state.actions.forEach(function(it) {
                    addRow(it.label, it.active ? '固定中' : '', { kind: 'expression', key: it.key, active: it.active });
                });
            }
            var actionPins = state.pins.filter(function(it) { return it.category === 'action'; });
            if (actionPins.length) {
                section('形态开关');
                actionPins.forEach(function(it) {
                    addRow(it.label, pinStateText(it.state), { kind: 'pin', key: it.key, state: it.state, pin: true });
                });
            }
            if (!state.motions.length && !state.actions.length && !actionPins.length) empty('当前模型没有可固定的动作');
        } else if (tab === 'other') {
            // 词表没认出来的统统进这里：Q版形态、特殊开关、命名奇怪的组合表情…
            var otherPins = state.pins.filter(function(it) { return it.category === 'other'; });
            if (state.others.length) {
                section('表情类（可保持）');
                state.others.forEach(function(it) {
                    addRow(it.label, it.active ? '固定中' : '', { kind: 'expression', key: it.key, active: it.active });
                });
            }
            if (otherPins.length) {
                section('开关类');
                otherPins.forEach(function(it) {
                    addRow(it.label, pinStateText(it.state), { kind: 'pin', key: it.key, state: it.state, pin: true });
                });
            }
            if (!state.others.length && !otherPins.length) empty('当前模型没有未分类的表演项');
        } else {
            var outfitPins = state.pins.filter(function(it) { return it.category !== 'action' && it.category !== 'other'; });
            if (!outfitPins.length) empty('当前模型没有可切换的服饰（外套/帽子/发夹/兽耳/尾巴等）');
            outfitPins.forEach(function(it) {
                addRow(it.label, pinStateText(it.state), { kind: 'pin', key: it.key, state: it.state, pin: true });
            });
        }
        if (window.__petReportUi) window.__petReportUi();
    }

    function applyList(msg) {
        state.auto = !(msg && msg.auto === false);
        state.expressions = (msg && msg.expressions) || [];
        state.motions = (msg && msg.motions) || [];
        state.actions = (msg && msg.actions) || [];
        state.others = (msg && msg.others) || [];
        state.pins = (msg && msg.pins) || [];
        var row = document.getElementById('os-auto-row');
        var val = document.getElementById('os-auto-val');
        if (row) row.classList.toggle('off', !state.auto);
        if (val) val.textContent = state.auto ? '开' : '关';
        renderList();
    }

    function stopMotionLoop() {
        if (motionTimer) { clearInterval(motionTimer); motionTimer = null; }
        currentMotionKey = null;
        // 取消固定时把动作也停掉，否则自循环动作（Idle 之类）会一直播下去
        try {
            var manager = model && model.internalModel ? model.internalModel.motionManager : null;
            if (manager && typeof manager.stopAllMotions === 'function') manager.stopAllMotions();
        } catch (e) {}
    }
    function startMotionLoop(msg) {
        if (motionTimer) { clearInterval(motionTimer); motionTimer = null; }
        if (!msg || !msg.enabled) { stopMotionLoop(); return; }

        var group = msg.group;
        var index = Number(msg.index) || 0;
        var key = group + '|' + index;
        if (key === currentMotionKey) return;   // 还是同一个固定动作，别打断它
        currentMotionKey = key;

        var period = Math.max(1, (Number(msg.duration) || 3) - 0.2) * 1000;
        function play() {
            try { model.motion(group, index, PIXI.live2d.MotionPriority.FORCE); } catch (e) {}
        }
        play();
        // 自循环动作由引擎自己循环，反复重发反而会打断动作；一次性动作才按自身时长重播
        if (!msg.loop) motionTimer = setInterval(play, period);
    }

    messageBus.on('switch_list', function(msg) { applyList(msg); });
    messageBus.on('motion_loop', function(msg) { startMotionLoop(msg); });
    // ---- 面板位置：默认贴右侧、可拖标题栏移动、位置记忆；半透明避免挡住人物 ----
    var POS_KEY = 'petOutfitPanelPos';
    var dragOffset = null;

    function clampPos(el, left, top) {
        var maxLeft = Math.max(0, window.innerWidth - el.offsetWidth);
        var maxTop = Math.max(0, window.innerHeight - el.offsetHeight);
        return { left: Math.max(0, Math.min(maxLeft, left)), top: Math.max(0, Math.min(maxTop, top)) };
    }
    function place(left, top) {
        var p = document.getElementById('pet-outfit-panel');
        if (!p) return;
        var pos = clampPos(p, left, top);
        p.style.left = pos.left + 'px';
        p.style.top = pos.top + 'px';
        p.style.right = 'auto';
    }
    function restorePos() {
        var p = document.getElementById('pet-outfit-panel');
        if (!p) return;
        var saved = null;
        try { saved = JSON.parse(localStorage.getItem(POS_KEY) || 'null'); } catch (e) { saved = null; }
        if (saved && typeof saved.left === 'number' && typeof saved.top === 'number') place(saved.left, saved.top);
        // 默认贴右下角，把头部（大多数模型的脸在上半身）让出来
        else place(window.innerWidth - p.offsetWidth - 6, window.innerHeight - p.offsetHeight - 6);
    }

    messageBus.on('pet_outfit_open', function() {
        var p = document.getElementById('pet-outfit-panel');
        if (!p) return;
        var opening = !p.classList.contains('show');
        // 与「表情校准」面板互斥，避免两个浮层叠在一起
        var cal = document.getElementById('pet-cal-panel');
        if (opening && cal) cal.classList.remove('show');
        // 换模型面板同样互斥，避免三层浮窗叠在一起
        var mp = document.getElementById('pet-model-panel');
        if (opening && mp) mp.classList.remove('show');
        p.classList.toggle('show');
        if (p.classList.contains('show')) restorePos();
        if (window.__petReportUi) window.__petReportUi();
    });

    document.addEventListener('pointerdown', function(e) {
        var title = e.target && e.target.closest ? e.target.closest('#pet-outfit-panel .os-title') : null;
        if (!title || (e.target.closest && e.target.closest('.os-fold'))) return;
        var p = document.getElementById('pet-outfit-panel');
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
        var p = document.getElementById('pet-outfit-panel');
        if (!p) return;
        try {
            localStorage.setItem(POS_KEY, JSON.stringify({ left: parseFloat(p.style.left) || 0, top: parseFloat(p.style.top) || 0 }));
        } catch (e) {}
        if (window.__petReportUi) window.__petReportUi();
    });
    messageBus.on('pet:model-unloading', function() { stopMotionLoop(); });
    messageBus.on('pet:model-ready', function() { currentMotionKey = null; });

    document.addEventListener('click', function(e) {
        var t = e.target && e.target.closest ? e.target.closest('[data-outfit],[data-act],[data-tab],[data-kind]') : null;
        if (!t) return;

        var tabName = t.getAttribute('data-tab');
        if (tabName) {
            tab = tabName;
            var tabs = document.querySelectorAll('#pet-outfit-panel .os-tab');
            for (var i = 0; i < tabs.length; i++) tabs[i].classList.toggle('on', tabs[i].getAttribute('data-tab') === tab);
            renderList();
            return;
        }

        var act = t.getAttribute('data-act');
        if (act === 'os-close') {
            var p = document.getElementById('pet-outfit-panel');
            if (p) p.classList.remove('show');
            if (window.__petReportUi) window.__petReportUi();
            return;
        }
        if (act === 'os-fold') {
            var panel = document.getElementById('pet-outfit-panel');
            if (!panel) return;
            var folded = panel.classList.toggle('folded');
            t.textContent = folded ? '展开' : '收起';
            if (window.__petReportUi) window.__petReportUi();
            return;
        }
        if (act === 'auto') {
            postMessage({type:'switch_auto', enabled: !state.auto});
            return;
        }

        var kind = t.getAttribute('data-kind');
        var key = t.getAttribute('data-key');
        if (!kind || !key) return;
        if (kind === 'pin') {
            var current = parseInt(t.getAttribute('data-state'), 10);
            if (isNaN(current)) current = -1;
            var next = current === 1 ? 0 : 1;
            postMessage({type:'outfit_toggle', key:key, state:next});
            return;
        }
        var active = t.getAttribute('data-active') === '1';
        postMessage({type:'switch_pin', kind:kind, key:key, enabled: !active});
    });
})();
";

    /// <summary>当前模型可钉住的服饰 / 形态开关（按参数 ID 去重）。</summary>
    public List<OutfitItem> DetectItems() => DetectItems(metadata);

    /// <summary>按模型参数表识别可钉住的开关；独立成静态方法便于对真实模型做校验。</summary>
    public static List<OutfitItem> DetectItems(PetModelMetadata metadata)
    {
        List<OutfitItem> items = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> names = ParameterNameMap(metadata);

        foreach (ExpressionFileItem expression in metadata.ExpressionFiles)
        {
            string sourceName = expression.Name ?? "";
            string? paramId = PickOutfitParam(expression, sourceName, names);
            if (paramId == null) continue;
            if (metadata.PhysicsOutputIds.Contains(paramId)) continue;
            if (seen.Add(paramId) == false) continue;

            string paramName = names.GetValueOrDefault(paramId, "");
            double onValue = expression.Values.TryGetValue(paramId, out double value) ? value : 1;
            if (Math.Abs(onValue) < 0.0001) continue;   // 值就是 0 的表情没法当「打开」用
            items.Add(new OutfitItem(paramId, MakeLabel(sourceName, paramName), sourceName, paramName,
                onValue, !HasHideWord(sourceName + " " + paramName), SwitchCategory.Outfit));
        }

        // 参数级兜底：有些模型把开关做成参数、却没导出成表情（如「█开关 1 尾巴」「█开关 2 兽耳」「█开关 17 大小变」）。
        // 物理演算驱动的参数（耳朵开闭、裙摆摆动）必须排除，钉住它们会让部件僵住。
        foreach (CdiParameter parameter in metadata.Parameters)
        {
            string id = parameter.Id ?? "";
            if (id.Length == 0 || seen.Contains(id)) continue;
            if (metadata.PhysicsOutputIds.Contains(id)) continue;
            string name = parameter.Name ?? "";
            if (name.Length == 0) continue;
            if (HasSwitchVerb(name) == false || HasPoseWord(name)) continue;
            // 认不出分类的开关也收进「其他」，避免命名奇怪的模型部件/形态开关直接消失
            SwitchCategory category = Classify(name) ?? SwitchCategory.Other;
            items.Add(new OutfitItem(id, MakeLabel(name, name), name, name, 1, !HasHideWord(name), category));
            seen.Add(id);
        }
        return items;
    }

    /// <summary>可一直保持的表情（按表情文件走，多参数表情也能整体保持）；词表认不出的归入「其他」。</summary>
    public List<ExpressionOption> DetectExpressionOptions()
    {
        List<ExpressionOption> expressions = new();
        List<ExpressionOption> actions = new();
        List<ExpressionOption> others = new();
        Dictionary<string, string> names = ParameterNameMap(metadata);
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (ExpressionFileItem expression in metadata.ExpressionFiles)
        {
            string sourceName = expression.Name ?? "";
            if (sourceName.Length == 0) continue;
            string paramText = string.Join(" ", expression.Params.Select(id => names.GetValueOrDefault(id, "")));
            // 认不出分类的表情（Q版形态「QQ人」、特殊组合表情…）不再静默丢弃，统一进「其他」兜底
            SwitchCategory category = Classify(sourceName + " " + paramText) ?? SwitchCategory.Other;
            List<ExpressionOption> bucket = category switch
            {
                SwitchCategory.Expression => expressions,
                SwitchCategory.Action => actions,
                _ => others,
            };
            if (seen.Add(sourceName))
                bucket.Add(new ExpressionOption(sourceName, OptionLabel(sourceName, expression, names), category));
        }
        return expressions.Concat(actions).Concat(others).ToList();
    }

    /// <summary>表情原名是拼音缩写（aixin / kuku）时，用参数名里的中文当标签，列表才看得懂。</summary>
    static string OptionLabel(string sourceName, ExpressionFileItem expression, Dictionary<string, string> names)
    {
        if (CjkRegex.IsMatch(sourceName)) return sourceName;
        foreach (string id in expression.Params)
        {
            string name = names.GetValueOrDefault(id, "");
            if (name.Length > 0 && CjkRegex.IsMatch(name)) return MakeLabel(name, name);
        }
        return sourceName;
    }

    /// <summary>模型自带的动作文件（可循环播放）。</summary>
    public List<MotionOption> DetectMotionOptions()
    {
        List<MotionOption> options = new();
        foreach (KeyValuePair<string, (string Group, int Index)> entry in metadata.Motions)
        {
            double duration = 3;
            bool loop = false;
            if (metadata.MotionMeta.TryGetValue(entry.Key, out (bool Loop, double Duration) meta))
            {
                loop = meta.Loop;
                duration = meta.Duration > 0.5 ? meta.Duration : 3;
            }
            options.Add(new MotionOption($"{entry.Value.Group}|{entry.Value.Index}", entry.Key, entry.Value.Group,
                entry.Value.Index, duration, loop));
        }
        return options;
    }

    public void OnStart()
    {
        if (subscribed == false)
        {
            bridge.OnMessage += OnMessage;
            subscribed = true;
        }
        LoadStates();
        ApplyToFrontend();
    }

    /// <summary>AI / UI 切换：name 可以是参数 ID、显示名，或模型表情原名（如「隐藏外套」）。</summary>
    public bool SetOutfit(string? name, bool? visible)
    {
        string wanted = (name ?? "").Trim();
        if (wanted.Length == 0) return false;
        List<OutfitItem> items = AllItems();
        OutfitItem? item = items.FirstOrDefault(entry =>
                         string.Equals(entry.ParamId, wanted, StringComparison.OrdinalIgnoreCase))
                         ?? items.FirstOrDefault(entry => string.Equals(entry.Label, wanted, StringComparison.OrdinalIgnoreCase))
                         ?? items.FirstOrDefault(entry => string.Equals(entry.SourceName, wanted, StringComparison.OrdinalIgnoreCase))
                         ?? items.FirstOrDefault(entry => wanted.Length >= 2
                             && (entry.Label.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                                 || entry.SourceName.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                                 || wanted.Contains(entry.Label, StringComparison.OrdinalIgnoreCase)));
        if (item == null)
        {
            // 词表没覆盖到的模型：按单参数表情原名兜底，保证 AI 仍能控制
            item = ResolveFallback(wanted);
            if (item == null) return false;
            extraItems[item.ParamId] = item;
        }

        // visible=true 表示「显示/穿上」，false 表示「隐藏/脱下」，不填则按当前状态取反
        int next = visible is bool flag
            ? (flag ? 0 : 1)
            : states.TryGetValue(item.ParamId, out int current) ? (current == 1 ? 0 : 1) : 1;
        states[item.ParamId] = next;
        SaveStates();
        SendAll();
        return true;
    }

    /// <summary>给 AI 文档用的外观清单，没有时返回空字符串。</summary>
    public string DescribeItems()
        => string.Join("、", AllItems().Select(item => item.Label));

    void OnMessage(string type, JsonElement data)
    {
        if (type == "loaded")
        {
            // 换模型后参数 ID 全变了：清掉临时开关，并校验固定的表情/动作在新模型里是否还存在
            extraItems.Clear();
            if (ExpressionExists(pinnedExpression) == false) pinnedExpression = null;
            if (MotionExists(pinnedMotion) == false) pinnedMotion = null;
            ApplyToFrontend();
            return;
        }
        if (type == "switch_auto")
        {
            autoPerformance = !(data.TryGetProperty("enabled", out JsonElement enabledProp) && enabledProp.ValueKind == JsonValueKind.False);
            SaveStates();
            ApplyToFrontend();
            return;
        }
        if (type == "switch_pin")
        {
            string kind = data.TryGetProperty("kind", out JsonElement kindProp) ? (kindProp.GetString() ?? "") : "";
            string key = data.TryGetProperty("key", out JsonElement keyProp) ? (keyProp.GetString() ?? "") : "";
            bool enabled = data.TryGetProperty("enabled", out JsonElement pinProp) && pinProp.ValueKind == JsonValueKind.True;
            if (kind == "expression" && key.Length > 0)
            {
                pinnedExpression = enabled ? key : (string.Equals(pinnedExpression, key, StringComparison.OrdinalIgnoreCase) ? null : pinnedExpression);
            }
            else if (kind == "motion" && key.Length > 0)
            {
                pinnedMotion = enabled ? key : (string.Equals(pinnedMotion, key, StringComparison.OrdinalIgnoreCase) ? null : pinnedMotion);
            }
            else return;
            SaveStates();
            ApplyToFrontend();
            return;
        }
        if (type != "outfit_toggle") return;

        string key2 = data.TryGetProperty("key", out JsonElement outfitKey) ? (outfitKey.GetString() ?? "") : "";
        if (key2.Length == 0) return;
        int state = data.TryGetProperty("state", out JsonElement stateProp) && stateProp.TryGetInt32(out int parsed) ? parsed : 1;
        states[key2] = state == 0 ? 0 : 1;
        SaveStates();
        SendAll();
    }

    /// <summary>把最新的总开关、固定表情/动作与列表推给前端，并在需要时重新应用固定表演。</summary>
    void ApplyToFrontend()
    {
        bridge.SendMessage("auto_performance", new { enabled = autoPerformance });

        // 取消固定（或没有固定）时要把表情收回去，否则长期避让会让它一直挂在脸上
        if (string.IsNullOrEmpty(pinnedExpression))
            bridge.SendMessage("expression_reset");
        else if (ExpressionExists(pinnedExpression))
            bridge.SendMessage("expression_hold", new { id = pinnedExpression });

        MotionOption? motion = DetectMotionOptions().FirstOrDefault(option =>
            string.Equals(option.Key, pinnedMotion, StringComparison.OrdinalIgnoreCase));
        bridge.SendMessage("motion_loop", new {
            enabled = motion != null,
            group = motion?.Group ?? "",
            index = motion?.Index ?? 0,
            duration = motion?.Duration ?? 3,
            loop = motion?.Loop ?? false,
        });

        SendAll();
    }

    void SendAll()
    {
        List<OutfitItem> pins = AllItems();
        List<ExpressionOption> options = DetectExpressionOptions();
        List<ExpressionOption> expressions = options.Where(option => option.Category == SwitchCategory.Expression).ToList();
        List<ExpressionOption> actions = options.Where(option => option.Category == SwitchCategory.Action).ToList();
        List<ExpressionOption> others = options.Where(option => option.Category == SwitchCategory.Other).ToList();
        List<MotionOption> motions = DetectMotionOptions();

        var pinList = pins.Select(item => new {
            key = item.ParamId,
            label = item.Label,
            state = states.TryGetValue(item.ParamId, out int value) ? value : -1,
            category = item.Category switch {
                SwitchCategory.Action => "action",
                SwitchCategory.Other => "other",
                _ => "outfit",
            },
        }).ToArray();

        bridge.SendMessage("switch_list", new {
            auto = autoPerformance,
            expressions = expressions.Select(option => new {
                key = option.Name,
                label = option.Label,
                active = string.Equals(option.Name, pinnedExpression, StringComparison.OrdinalIgnoreCase),
            }).ToArray(),
            motions = motions.Select(option => new {
                key = option.Key,
                label = option.Name,
                active = string.Equals(option.Key, pinnedMotion, StringComparison.OrdinalIgnoreCase),
            }).ToArray(),
            actions = actions.Select(option => new {
                key = option.Name,
                label = option.Label,
                active = string.Equals(option.Name, pinnedExpression, StringComparison.OrdinalIgnoreCase),
            }).ToArray(),
            others = others.Select(option => new {
                key = option.Name,
                label = option.Label,
                active = string.Equals(option.Name, pinnedExpression, StringComparison.OrdinalIgnoreCase),
            }).ToArray(),
            pins = pinList,
        });

        // 钉住用户选过的开关：每帧在 coreModel.update 前重写，避免被动作/表情覆盖
        bridge.SendMessage("outfit_pins", new {
            items = pins
                .Where(item => states.ContainsKey(item.ParamId))
                .Select(item => new {
                    id = item.ParamId,
                    value = states[item.ParamId] == 1 ? item.HiddenValue : item.VisibleValue,
                })
                .ToArray(),
        });
    }

    bool ExpressionExists(string? name)
        => string.IsNullOrEmpty(name) == false
           && (metadata.ExpressionFiles.Any(expression => string.Equals(expression.Name, name, StringComparison.OrdinalIgnoreCase))
               || MetadataExpressionExists(name));

    bool MetadataExpressionExists(string? name)
        => string.IsNullOrEmpty(name) == false
           && metadata.Expressions.Any(expression => string.Equals(expression, name, StringComparison.OrdinalIgnoreCase));

    bool MotionExists(string? key)
        => string.IsNullOrEmpty(key) == false
           && DetectMotionOptions().Any(option => string.Equals(option.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>识别出的开关 + AI 按原名临时启用的开关。</summary>
    List<OutfitItem> AllItems()
    {
        List<OutfitItem> items = DetectItems(metadata);
        HashSet<string> known = new(items.Select(item => item.ParamId), StringComparer.OrdinalIgnoreCase);
        foreach (OutfitItem extra in extraItems.Values)
            if (known.Add(extra.ParamId))
                items.Add(extra);
        return items;
    }

    /// <summary>
    /// 从表情里挑出真正控制服饰外观的参数：单参数表情直接判断名字；
    /// 多参数表情（如「墨镜」= 镜片 + 两只闭眼）只取唯一一个外观参数，其余视为组合表演跳过。
    /// </summary>
    static string? PickOutfitParam(ExpressionFileItem expression, string sourceName, Dictionary<string, string> names)
    {
        if (expression.Params.Count == 0) return null;

        if (expression.Params.Count == 1)
        {
            string id = expression.Params[0];
            string text = sourceName + " " + names.GetValueOrDefault(id, "");
            return LooksLikeOutfit(text) && Classify(text) == SwitchCategory.Outfit ? id : null;
        }

        if (LooksLikeOutfit(sourceName) == false || Classify(sourceName) != SwitchCategory.Outfit) return null;
        List<string> candidates = new();
        foreach (string id in expression.Params)
        {
            string name = names.GetValueOrDefault(id, "");
            if (HasOutfitNoun(name) == false) continue;
            if (HasPoseWord(name) && HasSwitchVerb(name) == false) continue;
            candidates.Add(id);
        }
        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// <summary>词表没覆盖到的模型：只要是单参数表情，就允许按表情原名直接当开关用（AI 兜底）。</summary>
    OutfitItem? ResolveFallback(string wanted)
    {
        foreach (ExpressionFileItem expression in metadata.ExpressionFiles)
        {
            if (expression.Params.Count != 1) continue;
            string sourceName = expression.Name ?? "";
            if (sourceName.Length == 0) continue;
            bool match = string.Equals(sourceName, wanted, StringComparison.OrdinalIgnoreCase)
                         || sourceName.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                         || wanted.Contains(sourceName, StringComparison.OrdinalIgnoreCase);
            if (match == false) continue;

            string paramId = expression.Params[0];
            if (metadata.PhysicsOutputIds.Contains(paramId)) continue;
            double onValue = expression.Values.TryGetValue(paramId, out double value) ? value : 1;
            if (Math.Abs(onValue) < 0.0001) continue;

            string paramName = ParamName(paramId);
            SwitchCategory category = Classify(sourceName + " " + paramName) ?? SwitchCategory.Outfit;
            return new OutfitItem(paramId, MakeLabel(sourceName, paramName), sourceName, paramName,
                onValue, !HasHideWord(sourceName + " " + paramName), category);
        }
        return null;
    }

    string ParamName(string paramId)
        => metadata.Parameters.FirstOrDefault(parameter =>
               string.Equals(parameter.Id, paramId, StringComparison.OrdinalIgnoreCase))?.Name ?? "";

    static Dictionary<string, string> ParameterNameMap(PetModelMetadata metadata)
    {
        Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (CdiParameter parameter in metadata.Parameters)
            if (parameter.Id is { Length: > 0 } id)
                names.TryAdd(id, parameter.Name ?? "");
        return names;
    }

    // ---- 名称识别 ----

    static bool HasOutfitNoun(string text) => ContainsAny(text, OutfitNouns);

    static bool HasActionNoun(string text) => ContainsAny(text, ActionNouns);

    static bool HasExpressionNoun(string text) => ContainsAny(text, ExpressionNouns);

    static bool HasSwitchVerb(string text) => ContainsAny(text, SwitchVerbs);

    static bool HasHideWord(string text) => ContainsAny(text, HideMarkers);

    static bool HasPoseWord(string text) => ContainsAny(text, PoseWords);

    static bool ContainsAny(string text, string[] words)
        => string.IsNullOrEmpty(text) == false
           && words.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));

    static SwitchCategory? Classify(string text)
    {
        if (HasOutfitNoun(text)) return SwitchCategory.Outfit;
        if (HasActionNoun(text)) return SwitchCategory.Action;
        if (HasExpressionNoun(text)) return SwitchCategory.Expression;
        return null;
    }

    /// <summary>带服饰名词，且要么带开关动词，要么不是在做动作（尾巴摆动/袖口飘动这类姿势不算）。</summary>
    static bool LooksLikeOutfit(string text)
        => HasOutfitNoun(text) && (HasSwitchVerb(text) || HasPoseWord(text) == false);

    static string MakeLabel(string expressionName, string paramName)
    {
        string source = string.IsNullOrWhiteSpace(paramName) ? expressionName : paramName;
        // 参数名只是流水号（fx1 / Param137 / key20）或英文缩写（sy）时，改用表情原名当标签
        if (LooksLikeId(source) && string.IsNullOrWhiteSpace(expressionName) == false)
            source = expressionName;
        else if (AsciiRegex.IsMatch(source) && CjkRegex.IsMatch(expressionName))
            source = expressionName;
        source = DecorationRegex.Replace(source, " ");
        foreach (string keyword in LabelStripWords)
            source = source.Replace(keyword, "", StringComparison.OrdinalIgnoreCase);
        source = SideMarkerRegex.Replace(source, "");
        source = source.Trim(' ', '-', '_', '/', '\\', '·', '.', ':', '：', '(', ')', '（', '）');
        if (source.Length == 0) source = expressionName;
        return source.Length == 0 ? paramName : source;
    }

    static bool LooksLikeId(string text)
    {
        if (AsciiRegex.IsMatch(text) == false) return false;
        if (text.Any(char.IsDigit) == false) return false;
        return text.Any(char.IsLetter) || text.StartsWith('_');
    }

    // ---- 持久化 ----

    void LoadStates()
    {
        if (loaded) return;
        loaded = true;
        try
        {
            OutfitMemory? memory = storage.GetObject<OutfitMemory>(memoryKey);
            if (memory == null) return;
            foreach (KeyValuePair<string, int> entry in memory.States)
                states[entry.Key] = entry.Value;
            autoPerformance = memory.AutoPerformance;
            pinnedExpression = memory.Expression;
            pinnedMotion = memory.Motion;
        }
        catch (Exception e)
        {
            AlifeLog.LogWarning("读取外观与表演设置失败\n" + e.Message);
        }
    }

    void SaveStates()
    {
        try
        {
            storage.SetObject(memoryKey, new OutfitMemory {
                States = new Dictionary<string, int>(states, StringComparer.OrdinalIgnoreCase),
                AutoPerformance = autoPerformance,
                Expression = pinnedExpression,
                Motion = pinnedMotion,
            });
        }
        catch (Exception e)
        {
            AlifeLog.LogWarning("保存外观与表演设置失败\n" + e.Message);
        }
    }

    public void Dispose() { }
}
