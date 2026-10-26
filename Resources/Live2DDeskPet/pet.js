// ---- 宿主桥通讯 ----

// Alife 5：宿主在每个窗口内注入 window.alifeWindow 桥，不再有 Node/IPC 频道。
// 多开时每个窗口各自独立，消息天然不会串扰。
function alifeBridge() {
    try {
        return window.alifeWindow || null;
    } catch {
        return null;
    }
}

function postMessage(data) {
    try {
        var bridge = alifeBridge();
        if (bridge) {
            bridge.postMessage(data);
            return;
        }
    } catch {
    }
    // 桥不可用时的兜底：用引导脚本保存的发送器，至少能把错误原因告到主进程
    try {
        if (window.__petRawSend) window.__petRawSend(data);
    } catch {
    }
}

const messageBus = {
    _handlers: new Map(),
    on(type, handler) {
        if (!this._handlers.has(type)) {
            this._handlers.set(type, []);
        }

        this._handlers.get(type).push(handler);
    },
    _dispatch(msg) {
        const handlers = this._handlers.get(msg.type) || [];

        handlers.forEach(handler => handler(msg));
    }
};

function onBridgeMessage(msg) {
    if (typeof msg === 'string') {
        try {
            msg = JSON.parse(msg);
        } catch {
            return;
        }
    }
    if (!msg || !msg.type) return;
    messageBus._dispatch(msg);
}

(function bindBridge() {
    var bridge = alifeBridge();
    if (!bridge) {
        console.error('[Pet] window.alifeWindow 不可用：桌宠无法与主进程通讯');
        return;
    }
    try {
        bridge.onMessage(onBridgeMessage);
    } catch (e) {
        console.error('[Pet] 绑定宿主桥失败:', e);
    }
})();


// ---- 前端日志转发 ----
function formatLogArg(arg) {
    if (arg instanceof Error) return arg.name + ': ' + arg.message + '\n' + (arg.stack || '');
    if (typeof arg === 'object') {
        try {
            return JSON.stringify(arg);
        } catch {
            return String(arg);
        }
    }
    return String(arg);
}

function postLog(level) {
    try {
        var args = Array.prototype.slice.call(arguments, 1);
        postMessage({
            type: 'log',
            level: level,
            text: args.map(formatLogArg).join(' ')
        });
    } catch {
    }
}

{
    var rawWarn = console.warn.bind(console);
    var rawError = console.error.bind(console);
    console.warn = function () {
        postLog.apply(null, ['warn'].concat(Array.prototype.slice.call(arguments)));
        rawWarn.apply(console, arguments);
    };
    console.error = function () {
        postLog.apply(null, ['error'].concat(Array.prototype.slice.call(arguments)));
        rawError.apply(console, arguments);
    };
}

window.addEventListener('error', function (e) {
    var target = e.target;
    if (target && target !== window) {
        postLog('resource-error', target.tagName || 'unknown', target.src || target.href || '');
        return;
    }
    postLog('error', e.message, e.filename + ':' + e.lineno + ':' + e.colno, e.error || '');
}, true);

window.addEventListener('unhandledrejection', function (e) {
    postLog('unhandledrejection', e.reason);
});

// ---- 前端资源注入辅助 ----
function injectCSS(css) {
    var style = document.createElement('style');
    style.textContent = css;
    document.head.appendChild(style);
}

function injectHTML(html) {
    var div = document.createElement('div');
    div.innerHTML = html;
    while (div.firstChild) document.body.appendChild(div.firstChild);
}

// ---- 加载模型 ----

messageBus.on('load', function (msg) {
    loadModel(msg.url, msg.pins);
});

// 创建渲染器失败（显卡驱动异常 / 远程桌面或虚拟机没有 WebGL）时页面会直接停在这里，
// 主进程只能看到「30 秒等不到 init」。这里先明确报出原因，方便用户判断是环境问题。
let app;
try {
    app = new PIXI.Application({
        view: document.getElementById('canvas'),
        autoStart: true,
        resizeTo: window,
        transparent: true,
        backgroundAlpha: 0,
        // 按屏幕 DPI 渲染：不设的话画布按 1 倍像素画，再被系统放大到实际显示尺寸 → 整只模型发糊
        resolution: window.devicePixelRatio || 1,
        autoDensity: true,
        preserveDrawingBuffer: true, // 保留帧缓冲，允许读取像素（生成人形掩码）
    });
} catch (e) {
    postMessage({
        type: 'log',
        level: 'error',
        text: '[Pet] 创建 WebGL 渲染器失败，桌宠无法显示（常见原因：远程桌面 / 虚拟机 / 显卡驱动不支持 WebGL，或硬件加速被关闭）：' + (e && (e.message || e))
    });
    throw e;
}

let model = null;

// 用户在模型上的追加偏移与缩放（独立于布局基准，窗口 resize/updateLayout 不会重置）
let userScale = 1;
let userOffsetX = 0;
let userOffsetY = 0;

// 模型高度相对窗口高度的倍数（1.1 = 比窗口高一点）。由 MoveScaleModule 的滑块调整并持久化。
let modelHeightRatio = 1.1;

// 高度倍数改变（滑块实时调时也会走这里）：只重排，不写盘；save=true 才回报给主进程持久化
function applyHeightRatio(percent, save) {
    let ratio = Math.max(0.3, Math.min(2.5, (percent || 110) / 100));
    modelHeightRatio = ratio;
    const label = document.getElementById('pet-height-value');
    const range = document.getElementById('pet-height-range');
    if (label) label.textContent = Math.round(ratio * 100) + '%';
    if (range) range.value = String(Math.round(ratio * 100));
    applyTransform();
    if (save) postMessage({ type: 'pet_height_ratio', ratio: ratio });
}

// 当前模型视图（窗口 CSS 像素）：mx/my=模型中心位置，ms=每模型单位的像素数。
// 点击区域/穿透判定/配置面板靠它做「模型单位 ⇄ 窗口坐标」换算，与对话框 --ui-scale 同源。
var petView = { mx: 0, my: 0, ms: 1, dirty: false, sentMx: NaN, sentMy: NaN, sentMs: NaN };

// 视图变化时同步：置脏（节流上报主进程）+ 通知 overlay 图层贴模型（region 模块注入后生效）
function updatePetView() {
    if (!model) return;
    petView.mx = model.position.x;
    petView.my = model.position.y;
    petView.ms = Math.abs(model.scale.x) || 1;
    petView.dirty = true;
    // 模型中心水平位置暴露给 CSS（气泡等 UI 用它贴住模型，而不是锚死窗口中心）
    document.documentElement.style.setProperty('--model-cx', petView.mx + 'px');
    if (typeof window.onPetViewChanged === 'function') window.onPetViewChanged();
}

// pet_view 节流上报：100ms 合并一次且值没变不发——拖动/缩放期间最多 10 条/秒，
// 避免高频 IPC 轰炸主进程（此前主进程在 pet_view 路径做重活导致整个 Alife 卡死）。
setInterval(function () {
    if (!petView.dirty) return;
    petView.dirty = false;
    if (petView.mx === petView.sentMx && petView.my === petView.sentMy && petView.ms === petView.sentMs) return;
    petView.sentMx = petView.mx; petView.sentMy = petView.my; petView.sentMs = petView.ms;
    postMessage({ type: 'pet_view', mx: petView.mx, my: petView.my, ms: petView.ms });
}, 100);

// 模块加载完成后调用：作废去重标记，强制下个周期重发一次当前视图
//（主进程模块订阅可能晚于最早一次上报，静息状态下没有这条就永远拿不到视图）
window.requestPetView = function () {
    petView.dirty = true;
    petView.sentMx = NaN; petView.sentMy = NaN; petView.sentMs = NaN;
};

// 模型缩放策略（原始设计）：模型高度 = 窗口高度 × 倍数（默认 1.1，可在前台滑块里调）。
// 窗口是主人：窗口一 resize（用户拉伸/代码调整），模型跟着缩放。
// 垂直方向不再按画布几何中心居中：很多模型画布底部有透明留白，居中摆放时整只人物会悬在
// 窗口底边之上，看起来像没踩到地面。这里把「画布内可见最低点」对准窗口底边，透明留白溢出到窗口外。
let groundPadBottom = 0;   // 画布底 → 可见最低像素的空白（模型单位）
let groundMeasured = false;
let groundTries = 0;

function measureGroundPad() {
    if (!model || groundMeasured) return;
    try {
        var r = app.renderer;
        var gl = r.gl;
        if (!gl) return;
        r.render(app.stage);   // 确保帧缓冲就是当前姿势
        var w = r.width, h = r.height;
        var buf = new Uint8Array(w * h * 4);
        gl.readPixels(0, 0, w, h, gl.RGBA, gl.UNSIGNED_BYTE, buf);
        var step = Math.max(1, Math.floor(w / 160));
        var lowest = -1;
        for (var y = 0; y < h && lowest < 0; y++) {
            var row = y * w * 4;
            for (var x = 0; x < w; x += step) {
                if (buf[row + x * 4 + 3] > 12) { lowest = y; break; }
            }
        }
        if (lowest < 0) {
            // 首帧可能还没画出来，稍后重试几次
            if (++groundTries < 6) setTimeout(measureGroundPad, 120);
            return;
        }
        var res = r.resolution || 1;
        var sc = model.scale.y || 1;
        var bh = model.internalModel.originalHeight;
        if (lowest <= 1) {
            // 已经在窗口底边（或模型比窗口高、被裁切）：不要再下移
            groundPadBottom = 0;
        } else {
            // lowest 是「窗口底 → 可见最低像素」；换算成「画布底 → 可见最低像素」才是模型自身的留白
            var canvasBottomCss = model.position.y + bh * sc * 0.5;
            var artBottomCss = (h - lowest) / res;
            var padPx = Math.max(0, canvasBottomCss - artBottomCss);
            // 上限 25% 画布高：防止把偶尔飘到最下方的头发/饰物当成脚底，把人物整体推得太低
            groundPadBottom = Math.min(padPx / sc, bh * 0.25);
        }
        groundMeasured = true;
        console.warn('[Pet] 接地校准：底部透明留白=' + (groundPadBottom / bh * 100).toFixed(1) + '%');
        applyTransform();
    } catch (e) {
        console.warn('[Pet] 接地校准失败:', e);
    }
}

function applyTransform() {
    if (!model) return;
    var bh = model.internalModel.originalHeight; // 画布逻辑高
    var sc = (window.innerHeight * modelHeightRatio) / bh;    // 模型高 = 窗口高 × 倍数
    sc *= userScale;
    model.scale.set(sc);
    // 可见底边贴住窗口底边（groundPadBottom 乘缩放后是画布底部留白对应的像素高度）
    var cy = window.innerHeight - bh * sc * 0.5 + groundPadBottom * sc + userOffsetY;
    var cx = window.innerWidth * 0.5 + userOffsetX;
    effectBase = {x: cx, y: cy, sc: sc};
    renderWindowEffect(performance.now() / 1000, true);
    updatePetView();
}

// ---- 窗口级演出：跳动 / 抖动 / 位移 / 凑近 / 转身 ----
// 与模型参数无关，任何模型都能演；只改 model.position / scale，效果结束自动回到基准布局。
var windowEffect = null;   // {name, startedAt, duration, strength}
var effectBase = null;     // 不受演出影响的基准位置与缩放

var WINDOW_EFFECTS = {
    // 单次起跳：整只人物离地再落地
    jump: {
        seconds: 0.75,
        sample: function (t, s, h) {
            return {dy: -h * 0.13 * s * Math.sin(Math.PI * t)};
        },
    },
    // 连续两下小跳，比 jump 轻快
    hop: {
        seconds: 1.15,
        sample: function (t, s, h) {
            var first = t < 0.55 ? Math.sin(Math.PI * (t / 0.55)) : 0;
            var second = t >= 0.55 ? Math.sin(Math.PI * ((t - 0.55) / 0.45)) * 0.62 : 0;
            return {dy: -h * 0.065 * s * Math.max(first, second)};
        },
    },
    // 连续弹跳并逐渐衰减
    bounce: {
        seconds: 1.6,
        sample: function (t, s, h) {
            var pulse = Math.abs(Math.sin(Math.PI * 3 * t));
            return {dy: -h * 0.045 * s * pulse * (1 - t)};
        },
    },
    // 高频左右抖（生气/着急）
    shake: {
        seconds: 0.9,
        sample: function (t, s, w) {
            return {dx: Math.sin(t * Math.PI * 16) * w * 0.012 * s * (1 - t)};
        },
    },
    // 横向闪一下再回来（让位/惊讶）
    step: {
        seconds: 1,
        sample: function (t, s, w) {
            return {dx: Math.sin(Math.PI * t) * w * 0.055 * s};
        },
    },
    // 凑近：放大一点
    leanIn: {
        seconds: 1.2,
        sample: function (t, s) {
            var k = 1 + 0.09 * s * Math.sin(Math.PI * t);
            return {sx: k, sy: k};
        },
    },
    // 后退：缩小一点
    retreat: {
        seconds: 1.2,
        sample: function (t, s) {
            var k = 1 - 0.09 * s * Math.sin(Math.PI * t);
            return {sx: k, sy: k};
        },
    },
    // 原地转身：横向压扁到 0 再展开，配合模型自身的画法看起来像翻身
    turn: {
        seconds: 1.1,
        sample: function (t) {
            return {sx: Math.cos(Math.PI * t)};
        },
    },
    // 细微发颤（害怕/紧张）
    tremble: {
        seconds: 1.4,
        sample: function (t, s, w, h) {
            var damp = (1 - t) * s;
            return {
                dx: Math.sin(t * Math.PI * 22) * w * 0.004 * damp,
                dy: Math.sin(t * Math.PI * 19) * h * 0.003 * damp,
            };
        },
    },
};

function playWindowEffect(name, strength, seconds) {
    if (!model) return;
    var key = String(name || '').trim().toLowerCase();
    var def = WINDOW_EFFECTS[key];
    if (!def) {
        console.warn('[Pet] 未知窗口演出:', name);
        return;
    }
    if (!effectBase) applyTransform();
    windowEffect = {
        name: key,
        startedAt: performance.now() / 1000,
        duration: Number(seconds) > 0 ? Number(seconds) : def.seconds,
        strength: Math.max(0.15, Math.min(1, Number(strength) || 0.6)),
    };
    console.warn('[Pet] 窗口演出', key, '强度', windowEffect.strength);
}

function renderWindowEffect(now, force) {
    if (!model) return;
    var base = effectBase;
    if (!base) return;
    if (!windowEffect) {
        model.position.set(base.x, base.y);
        model.scale.set(base.sc);
        return;
    }
    var t = (now - windowEffect.startedAt) / windowEffect.duration;
    if (t >= 1 || t < 0) {
        windowEffect = null;
        model.position.set(base.x, base.y);
        model.scale.set(base.sc);
        if (!force) updatePetView();
        return;
    }
    var def = WINDOW_EFFECTS[windowEffect.name];
    var pose = def.sample(t, windowEffect.strength, window.innerWidth, window.innerHeight);
    model.position.set(base.x + (pose.dx || 0), base.y + (pose.dy || 0));
    model.scale.set(base.sc * (pose.sx ?? 1), base.sc * (pose.sy ?? 1));
    if (!force) updatePetView();
}

app.ticker.add(function () {
    if (!windowEffect) return;
    renderWindowEffect(performance.now() / 1000, false);
    updatePetView();
});

messageBus.on('pet_window_effect', function (msg) {
    if (!msg) return;
    playWindowEffect(msg.name, msg.intensity, msg.seconds);
});

// 每帧强制固定的模型参数（水印这类"开关参数"）：动作与表情每帧都会重写参数，
// 所以在 coreModel.update() 之前压一次才能稳定生效——否则一有动作水印就闪回来。
// 参数名兼容两种大小写（桥接用 Newtonsoft 默认契约，记录类型会发成 Id/Value）。
var staticPins = [];      // 水印这类加载时固定的参数
var dynamicPins = {};     // 换装开关：运行中可随时改，按参数 ID 覆盖

function parsePins(pins) {
    var list = [];
    if (!pins) return list;
    for (var i = 0; i < pins.length; i++) {
        var id = pins[i].id !== undefined ? pins[i].id : pins[i].Id;
        var value = pins[i].value !== undefined ? pins[i].value : pins[i].Value;
        if (id !== undefined && id !== null) list.push({id: id, value: value});
    }
    return list;
}

// coreModel.update 之前统一重写固定参数：动作/表情每帧都会改这些参数，
// 写在这里才能本帧生效并压住它们（写 update 之后只能影响下一帧）。
function installPinHook(core) {
    if (!core || core.__tsumiPinHook) return;
    core.__tsumiPinHook = true;
    var rawUpdate = core.update.bind(core);
    core.update = function () {
        for (var k = 0; k < staticPins.length; k++) {
            try {
                core.setParameterValueById(staticPins[k].id, staticPins[k].value);
            } catch (e) {
                console.warn('[Pet] 固定参数写入失败:', staticPins[k], e);
            }
        }
        for (var id in dynamicPins) {
            if (!Object.prototype.hasOwnProperty.call(dynamicPins, id)) continue;
            try {
                core.setParameterValueById(id, dynamicPins[id]);
            } catch (e) {
                console.warn('[Pet] 换装参数写入失败:', id, e);
            }
        }
        return rawUpdate();
    };
}

function applyParamPins(pins) {
    if (!model || !model.internalModel) return;
    var core = model.internalModel.coreModel;
    if (!core) return;

    staticPins = parsePins(pins);
    installPinHook(core);
    if (staticPins.length === 0) return;

    // 探测：参数名不存在时 setParameterValueById 不会报错，只能主动读一次才知道有没有生效
    try {
        var probe = [];
        for (var j = 0; j < staticPins.length; j++) {
            probe.push(staticPins[j].id + ' 现值=' + core.getParameterValueById(staticPins[j].id) + ' → 目标=' + staticPins[j].value);
        }
        console.warn('[Pet] 固定参数就位: ' + probe.join('; '));
    } catch (e) {
        console.warn('[Pet] 固定参数探测失败（参数名可能不存在）: ' + e);
    }
}

// 换装模块下发的显示/隐藏选择：空列表表示全部交回模型自身
function setDynamicPins(items) {
    var map = {};
    var list = parsePins(items);
    for (var i = 0; i < list.length; i++) map[list[i].id] = list[i].value;
    dynamicPins = map;
    if (model && model.internalModel) installPinHook(model.internalModel.coreModel);
}

messageBus.on('outfit_pins', function (msg) {
    setDynamicPins((msg && msg.items) || []);
});

async function loadModel(url, pins) {
    console.warn('[Pet] pet.js 已加载；本次固定参数 = ' + JSON.stringify(pins || []));
    console.log('[Pet] Loading model:', url);
    if (model) {
        // 先通知具身层卸载旧模型（它挂在旧模型内部模型上的钩子必须摘掉），再移除
        messageBus._dispatch({ type: 'pet:model-unloading' });
        try {
            app.stage.removeChild(model);
            if (model.destroy) model.destroy({ children: true });
        } catch (e) {
            console.warn('[Pet] 移除旧模型失败:', e);
        }
        model = null;
    }
    try {
        model = await PIXI.live2d.Live2DModel.from(url, {autoInteract: false});
        console.log('[Pet] Model loaded successfully');
    } catch (err) {
        console.error('[Pet] Live2D model load failed:', err);
        postMessage({type: 'loaded', ok: false, error: String(err && err.message ? err.message : err)});
        return;
    }
    app.stage.addChild(model);
    applyParamPins(pins);
    
    var updateLayout = function () {
        var s = window.innerHeight / 540;
        document.documentElement.style.setProperty('--ui-scale', s);
        applyTransform();
    };

    model.anchor.set(0.5, 0.5);
    groundPadBottom = 0;
    groundMeasured = false;
    groundTries = 0;
    updateLayout();
    // 等第一帧真正画出来后再量脚底留白（量完会重新摆位并上报新视图）
    setTimeout(measureGroundPad, 160);
    model.interactive = true;
    window.addEventListener('resize', updateLayout);

    // 渲染诊断：画布像素 vs 窗口尺寸能看出像素密度够不够（画布 = 窗口 × DPR 才算按屏幕原生像素渲染）
    console.warn('[Pet] 渲染诊断 DPR=' + window.devicePixelRatio
        + ' 窗口=' + window.innerWidth + 'x' + window.innerHeight
        + ' 画布像素=' + app.renderer.width + 'x' + app.renderer.height
        + ' 模型原始高=' + model.internalModel.originalHeight.toFixed(3)
        + ' 当前缩放=' + model.scale.y.toFixed(2)
        + ' 高度倍数=' + modelHeightRatio);

    // 上报模型原始尺寸（逻辑单位），供主进程自动贴合窗口：宽 = 当前高 × 宽高比
    postMessage({
        type: 'model_info',
        ow: model.internalModel.originalWidth,
        oh: model.internalModel.originalHeight
    });

    // 通知具身层新模型已就位：它会在这里挂 beforeModelUpdate 钩子、按新模型重建表演档案
    messageBus._dispatch({ type: 'pet:model-ready', model: model });
    postMessage({type: 'loaded', ok: true});
}

// ---- 初始化完成 ----
postMessage({type: 'init'});
