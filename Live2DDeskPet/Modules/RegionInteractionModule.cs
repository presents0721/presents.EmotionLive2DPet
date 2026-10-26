using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Alife.Foundation;
using Alife.Framework;
using System.Drawing;

namespace presents.EmotionLive2DPet;

/// <summary>
/// 桌宠部位交互模块：区域形状（矩形/椭圆/多边形）+ 连点档位触发 + 可视化编辑。
/// 区域坐标存「模型单位」（以模型中心为原点、模型原始像素为 1 单位），
/// 模型缩放/移动/窗口变化时存储值不变，命中与显示时经 ToCalibrated/ToWindow 换算。
/// 编辑能力：进入编辑模式后可在桌宠身上直接拖画/移动/缩放/删除区域；支持从人形一键拾取部件。
/// 所有交互统一通过 <see cref="PetInteractionHub.Report"/> 把「发生了什么」告诉角色，由她决定反应。
/// </summary>
public class RegionInteractionModule : IPetModule, IDisposable
{
    public string JsCode => @"
window.petEditOn = false;
// 拖动（移动桌宠）时窗口跟着光标走，松手仍会触发一次 click；累计按住期间的原始位移来区分「点」和「拖」
var pcAcc = 0, pcDragged = false;
window.addEventListener('mousedown', function(e) { pcAcc = 0; pcDragged = false; }, true);
window.addEventListener('mousemove', function(e) {
    if ((e.buttons & 1) === 0) return;
    pcAcc += Math.abs(e.movementX || 0) + Math.abs(e.movementY || 0);
    if (pcAcc > 8) pcDragged = true;
}, true);
window.addEventListener('click', function(e) {
    if (window.petEditOn) return; // 编辑模式不触发交互
    if (pcDragged) return;        // 拖动过就不算点击
    if (e.target && e.target.tagName !== 'CANVAS') return;
    // 空白穿透由 body_mask（鼠标穿透模块）处理，这里只做区域匹配。
    // 附带点击瞬间的视图快照（mx/my/ms），主进程用它精确换算，不受 pet_view 节流时延影响
    var v = window.petView || {};
    postMessage({type:'region_click', x: e.clientX, y: e.clientY, mx: v.mx || 0, my: v.my || 0, ms: v.ms || 0});
    reportPokeHit(e.clientX, e.clientY);
});
// ---- 原版命中区交互：命中 Head/Body 区域，或 2.5 秒内连续摸到第 3 下 ----
var pokeCombo = 0, pokeLast = 0;
function reportPokeHit(x, y) {
    try {
        if (typeof model === 'undefined' || !model || typeof model.hitTest !== 'function') return;
        var areas = model.hitTest(x, y) || [];
        var cat = '';
        for (var i = 0; i < areas.length; i++) {
            var a = String(areas[i] || '').toLowerCase();
            if (a.indexOf('head') >= 0) { cat = 'head'; break; }
            if (a.indexOf('body') >= 0) cat = 'body';
        }
        if (!cat) return;
        var now = Date.now();
        if (now - pokeLast < 2500) pokeCombo++; else pokeCombo = 1;
        pokeLast = now;
        postMessage({type:'poke_hit', category: cat, combo: (pokeCombo % 3 === 0)});
    } catch (err) { }
}
(function() {
    // ---- 坐标换算（窗口 CSS 像素 <-> 模型单位） ----
    function view() { return window.petView || {mx:0, my:0, ms:1}; }
    function toU(wx, wy) { var v = view(); return {x:(wx - v.mx) / v.ms, y:(wy - v.my) / v.ms}; }
    function toW(ux, uy) { var v = view(); return {x:v.mx + ux * v.ms, y:v.my + uy * v.ms}; }
    function shapeOf(b) { return b.shape || 'rect'; }

    var btn = document.getElementById('region-toggle-btn');
    if (btn) btn.addEventListener('click', function() { postMessage({type:'region_toggle'}); });
    var ebtn = document.getElementById('region-edit-btn');
    if (ebtn) ebtn.addEventListener('click', function() { postMessage({type:'region_edit_toggle'}); });

    // ---- 人形掩码：canvas 降采样读 alpha，得到不规则人形（含间隙），非矩形 ----
    var lastMask = null;
    function buildBodyMask() {
        try {
            var cv = document.getElementById('canvas');
            if (!cv || !cv.width || !cv.height) return;
            var STEP = 10;
            var gw = Math.max(1, Math.ceil(window.innerWidth / STEP));
            var gh = Math.max(1, Math.ceil(window.innerHeight / STEP));
            var tmp = document.createElement('canvas');
            tmp.width = gw; tmp.height = gh;
            var ctx = tmp.getContext('2d');
            ctx.drawImage(cv, 0, 0, gw, gh);
            var px = ctx.getImageData(0, 0, gw, gh).data;
            var bits = [];
            var minX = gw, minY = gh, maxX = -1, maxY = -1;
            for (var y = 0; y < gh; y++) {
                for (var x = 0; x < gw; x++) {
                    var a = px[(y * gw + x) * 4 + 3];
                    var on = a > 8 ? 1 : 0;
                    bits.push(on);
                    if (on) {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            postMessage({type:'body_mask', gw: gw, gh: gh, data: bits});
            if (maxX >= minX && maxY >= minY) {
                lastMask = {gw: gw, gh: gh, bits: bits, step: STEP};
                var bw = (maxX - minX + 1) * STEP;
                var bh = (maxY - minY + 1) * STEP;
                postMessage({type:'body_bbox', x: minX * STEP, y: minY * STEP, w: bw, h: bh});
            }
        } catch (e) { }
    }

    // ---- 永久区交互（非编辑态）：拖动带动整个桌宠、双击切换穿透 ----
    var overlayRoot = document.getElementById('region-overlay');
    var fDrag = false, fStartX = 0, fStartY = 0, fMoved = false, fLastClick = 0;
    if (overlayRoot) {
        overlayRoot.addEventListener('mousedown', function(e) {
            if (window.petEditOn) return;
            if (e.button !== 0) return;
            if (!e.target.closest('.region-forever')) return;
            fDrag = true; fMoved = false;
            fStartX = e.clientX; fStartY = e.clientY;
            postMessage({type:'forever_drag_start'});
            e.preventDefault();
        });
        window.addEventListener('mousemove', function(e) {
            if (!fDrag) return;
            var dx = e.clientX - fStartX, dy = e.clientY - fStartY;
            if (Math.abs(dx) + Math.abs(dy) > 4) fMoved = true;
            if (fMoved) {
                fStartX = e.clientX; fStartY = e.clientY;
                postMessage({type:'forever_drag', dx: e.movementX, dy: e.movementY});
            }
        });
        window.addEventListener('mouseup', function(e) {
            if (!fDrag) return;
            fDrag = false;
            if (fMoved) return;
            var now = Date.now();
            if (now - fLastClick < 400) { postMessage({type:'forever_dblclick'}); fLastClick = 0; }
            else fLastClick = now;
        });
    }
    messageBus.on('region_overlay', function(msg) {
        var overlay = document.getElementById('region-overlay');
        if (!overlay) return;
        if (msg.visible) overlay.classList.add('show');
        else overlay.classList.remove('show');
        if (btn) btn.classList.toggle('faded', !msg.visible);
    });

    // ---- overlay 贴模型（区域框画在模型单位图层里，随模型缩放/移动贴合） ----
    var coordBoxes = [];
    window.onPetViewChanged = function () {
        var layer = document.getElementById('region-layer');
        var v = window.petView;
        if (!layer || !v) return;
        layer.style.transform = 'translate(' + v.mx + 'px,' + v.my + 'px) scale(' + v.ms + ')';
        layer.style.fontSize = (1 / v.ms) + 'px';
    };
    function refreshCoords() {
        var overlay = document.getElementById('region-overlay');
        var v = window.petView;
        if (!overlay || !overlay.classList.contains('show') || !v) return;
        var useUnit = window.regionCoordUnit === 'model';
        for (var i = 0; i < coordBoxes.length; i++) {
            var b = coordBoxes[i];
            if (useUnit) {
                b.coordEl.textContent = 'X:' + Math.round(b.x) + '~' + Math.round(b.x + b.w) + '  Y:' + Math.round(b.y) + '~' + Math.round(b.y + b.h);
            } else {
                var x0 = Math.round(v.mx + b.x * v.ms), y0 = Math.round(v.my + b.y * v.ms);
                var x1 = Math.round(v.mx + (b.x + b.w) * v.ms), y1 = Math.round(v.my + (b.y + b.h) * v.ms);
                b.coordEl.textContent = 'X:' + x0 + '-' + x1 + '  Y:' + y0 + '-' + y1;
            }
        }
    }
    setInterval(refreshCoords, 300);

    // ---- 形状渲染 ----
    function renderShape(layer, r, cls, id, label, editIndex) {
        var el;
        var box = {x: r.x, y: r.y, w: r.w, h: r.h};
        if (shapeOf(r) === 'polygon' && r.points && r.points.length >= 6) {
            el = document.createElementNS('http://www.w3.org/2000/svg', 'polygon');
            var pts = [];
            for (var i = 0; i + 1 < r.points.length; i += 2) pts.push(r.points[i] + ',' + r.points[i + 1]);
            el.setAttribute('points', pts.join(' '));
            var svg = document.getElementById('region-svg');
            if (!svg) {
                svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
                svg.id = 'region-svg';
                svg.setAttribute('width', '1'); svg.setAttribute('height', '1');
                svg.style.cssText = 'position:absolute;left:0;top:0;overflow:visible;';
                layer.appendChild(svg);
            }
            el.setAttribute('class', cls);
            if (id) el.id = id;
            svg.appendChild(el);
        } else {
            el = document.createElement('div');
            el.className = cls;
            if (id) el.id = id;
            if (shapeOf(r) === 'ellipse') el.classList.add('shape-ellipse');
            el.style.left = box.x + 'px';
            el.style.top = box.y + 'px';
            el.style.width = box.w + 'px';
            el.style.height = box.h + 'px';
            layer.appendChild(el);
        }
        var name = document.createElement('div');
        name.className = 'rb-name';
        name.textContent = label;
        var coord = document.createElement('div');
        coord.className = 'rb-coord';
        el.appendChild(name);
        el.appendChild(coord);
        return {coordEl: coord, x: box.x, y: box.y, w: box.w, h: box.h, el: el, region: r, index: editIndex};
    }

    messageBus.on('region_boxes', function(msg) {
        var overlay = document.getElementById('region-overlay');
        if (!overlay) return;
        overlay.innerHTML = '';
        var makeRX = function(cls, posKey, bottom) {
            var r = document.createElement('div');
            r.className = cls;
            for (var x = 0; x <= window.innerWidth; x += 50) {
                var s = document.createElement('span');
                s.style[posKey] = x + 'px';
                s.textContent = x;
                r.appendChild(s);
            }
            overlay.appendChild(r);
        };
        if (!window.__petSettings || window.__petSettings.ruler !== '0') {
            makeRX('region-ruler-x', 'left');
            makeRX('region-ruler-xb', 'left');
        }
        var makeRY = function(cls, posKey) {
            var r = document.createElement('div');
            r.className = cls;
            for (var y = 0; y <= window.innerHeight; y += 50) {
                var s2 = document.createElement('span');
                s2.style[posKey] = y + 'px';
                s2.textContent = y;
                r.appendChild(s2);
            }
            overlay.appendChild(r);
        };
        if (!window.__petSettings || window.__petSettings.ruler !== '0') {
            makeRY('region-ruler-y', 'top');
            makeRY('region-ruler-yr', 'top');
        }
        // 编辑用绘制面（在 layer 之下：空处落到它，形状之上优先接事件）
        var surface = document.createElement('div');
        surface.id = 'region-edit-surface';
        overlay.appendChild(surface);
        var layer = document.createElement('div');
        layer.id = 'region-layer';
        layer.style.cssText = 'position:absolute;left:0;top:0;transform-origin:0 0;';
        overlay.appendChild(layer);
        coordBoxes = [];
        if (msg.forever) {
            coordBoxes.push(renderShape(layer, msg.forever, 'region-box region-forever', 'region-forever', '永久区', -1));
        }
        (msg.boxes || []).forEach(function(b) {
            coordBoxes.push(renderShape(layer, b, 'region-box', null, b.name || '', b.index));
        });
        window.onPetViewChanged();
        refreshCoords();
        attachEditHandlers();
    });

    window.regionCoordUnit = 'window';
    messageBus.on('coord_unit', function(msg) { window.regionCoordUnit = msg.unit === 'model' ? 'model' : 'window'; refreshCoords(); });

    // ---- 编辑模式 ----
    var editMode = false, tool = 'rect';
    var polyPts = [], polyPreview = null;
    messageBus.on('edit_mode', function(msg) {
        editMode = !!msg.on;
        window.petEditOn = editMode;
        var overlay = document.getElementById('region-overlay');
        var bar = document.getElementById('region-toolbar');
        if (overlay) overlay.classList.toggle('edit', editMode);
        if (bar) bar.classList.toggle('show', editMode);
        if (!editMode) { polyPts = []; removePreview(); tool = 'rect'; setActive('rt-rect'); closeMeta(); return; }
        if (!overlay.classList.contains('show')) overlay.classList.add('show');
        var def = window.__petSettings ? window.__petSettings.shape_default : 'rect';
        if (def === 'ellipse') { tool = 'ellipse'; setActive('rt-ellipse'); }
        else if (def === 'polygon') { tool = 'polygon'; setActive('rt-polygon'); }
        else { tool = 'rect'; setActive('rt-rect'); }
        if (window.__petHint) window.__petHint('拖动=画框 · 点部件=拾取 · 双击区域=改名/改反应 · 完成或 ESC 退出', 5000);
        // 编辑事件挂载在绘制面上；若面还没建好（尚未收到区域数据），主动要一次
        if (document.getElementById('region-edit-surface')) attachEditHandlers();
        else postMessage({type:'region_refresh'});
    });
    function removePreview() { if (polyPreview && polyPreview.parentNode) polyPreview.parentNode.removeChild(polyPreview); polyPreview = null; }

    // 编辑期间只要还在动鼠标/打字，就告诉主进程「还在编辑」，避免自动退出把面板变成穿透态
    var lastBeat = 0;
    function beat() {
        if (!editMode) return;
        var now = Date.now();
        if (now - lastBeat < 3000) return;
        lastBeat = now;
        postMessage({type:'region_activity'});
    }
    ['mousemove', 'mousedown', 'keydown', 'input'].forEach(function(ev) {
        document.addEventListener(ev, beat, true);
    });

    // ---- 工具条 ----
    function setActive(id) {
        ['rt-rect', 'rt-ellipse', 'rt-polygon', 'rt-pick'].forEach(function(k) {
            var el = document.getElementById(k);
            if (el) el.classList.toggle('active', k === id);
        });
    }
    // 工具条用 document 级事件委托，避免元素重建/时序问题导致按钮点了没反应
    document.addEventListener('click', function(e) {
        var b = e.target && e.target.closest ? e.target.closest('#region-toolbar button') : null;
        if (!b) return;
        var id = b.id;
        var map = {'rt-rect':'rect', 'rt-ellipse':'ellipse', 'rt-polygon':'polygon', 'rt-pick':'pick'};
        if (map[id]) { tool = map[id]; setActive(id); polyPts = []; removePreview(); return; }
        if (id === 'rt-undo') { postMessage({type:'region_undo'}); return; }
        if (id === 'rt-del') { if (selectedIndex >= 0) postMessage({type:'region_delete', index:selectedIndex}); return; }
        if (id === 'rt-export') { postMessage({type:'region_export'}); return; }
        if (id === 'rt-import') { postMessage({type:'region_import'}); return; }
        if (id === 'rt-unit') {
            var cur = window.regionCoordUnit === 'model' ? 'window' : 'model';
            window.regionCoordUnit = cur;
            b.textContent = '坐标:' + (cur === 'model' ? '模型' : '窗口');
            refreshCoords();
            return;
        }
        if (id === 'rt-done') { postMessage({type:'region_edit_exit'}); return; }
    }, true);

    // ---- 区域设置面板：当场改名 + 改连点反应 ----
    var metaIndex = -1;
    function findRegion(index) {
        for (var i = 0; i < coordBoxes.length; i++) if (coordBoxes[i].index === index) return coordBoxes[i].region;
        return null;
    }
    function addTapRow(times, message) {
        var box = document.getElementById('rm-taps');
        if (!box) return;
        var row = document.createElement('div');
        row.className = 'rm-tap';
        var t = document.createElement('input'); t.type = 'number'; t.min = '1'; t.value = times || 1; t.className = 'rm-times';
        var m = document.createElement('input'); m.type = 'text'; m.value = message || ''; m.placeholder = '发给桌宠的话'; m.className = 'rm-msg';
        var d = document.createElement('span'); d.className = 'rm-del'; d.setAttribute('data-rm', 'deltap'); d.textContent = '×';
        row.appendChild(t); row.appendChild(m); row.appendChild(d);
        box.appendChild(row);
    }
    var GESTURE_VERB = {dblclick:'双击', longpress:'长按', stroke:'来回抚摸', wheel:'用滚轮戳'};
    function fillGestures(r) {
        var g = r.gestures || {};
        var rn = r.name || '区域名';
        ['dblclick', 'longpress', 'stroke', 'wheel'].forEach(function(k) {
            var on = document.querySelector('.rm-gon[data-g=' + k + ']');
            var tx = document.querySelector('.rm-gtx[data-g=' + k + ']');
            var it = g[k] || {};
            if (on) on.checked = !!it.on;
            if (tx) { tx.value = it.msg || ''; tx.placeholder = '默认：哥哥' + GESTURE_VERB[k] + '了桌宠的「' + rn + '」'; }
        });
    }
    function collectGestures() {
        var out = {};
        ['dblclick', 'longpress', 'stroke', 'wheel'].forEach(function(k) {
            var on = document.querySelector('.rm-gon[data-g=' + k + ']');
            var tx = document.querySelector('.rm-gtx[data-g=' + k + ']');
            out[k] = {on: !!(on && on.checked), msg: (tx && tx.value) || ''};
        });
        return out;
    }
    function openMeta(index) {
        var r = findRegion(index);
        if (!r) return;
        metaIndex = index;
        var nameEl = document.getElementById('rm-name');
        if (nameEl) nameEl.value = r.name || '';
        var box = document.getElementById('rm-taps');
        if (box) box.innerHTML = '';
        var taps = r.taps || [];
        if (!taps.length) addTapRow(1, '');
        else taps.forEach(function(t) { addTapRow(t.times, t.message); });
        fillGestures(r);
        var p = document.getElementById('region-meta');
        if (p) p.classList.add('show');
        if (window.__petHint) window.__petHint('改名字、配「点几下/双击/长按…」的反应，点保存生效', 4000);
    }
    function closeMeta() {
        var p = document.getElementById('region-meta');
        if (p) p.classList.remove('show');
        metaIndex = -1;
    }
    function collectTaps() {
        var out = [];
        var box = document.getElementById('rm-taps');
        if (!box) return out;
        var rows = box.querySelectorAll('.rm-tap');
        for (var i = 0; i < rows.length; i++) {
            var t = rows[i].querySelector('.rm-times');
            var m = rows[i].querySelector('.rm-msg');
            out.push({times: parseInt(t && t.value, 10) || 1, message: (m && m.value) || ''});
        }
        return out;
    }
    document.addEventListener('click', function(e) {
        var el = e.target && e.target.closest ? e.target.closest('[data-rm]') : null;
        if (!el) return;
        var act = el.getAttribute('data-rm');
        if (act === 'addtap') { addTapRow((document.getElementById('rm-taps').children.length || 0) + 1, ''); return; }
        if (act === 'deltap') { var row = e.target.closest('.rm-tap'); if (row && row.parentNode) row.parentNode.removeChild(row); return; }
        if (act === 'cancel') { closeMeta(); return; }
        if (act === 'delete') { if (metaIndex >= 0) postMessage({type:'region_delete', index: metaIndex}); closeMeta(); return; }
        if (act === 'save') {
            if (metaIndex < 0) return;
            var nameEl = document.getElementById('rm-name');
            postMessage({type:'region_save_meta', index: metaIndex, name: (nameEl && nameEl.value) || '', taps: collectTaps(), gestures: collectGestures()});
            closeMeta();
            return;
        }
    }, true);

    function sendAdd(shape, u) {
        postMessage({type:'region_add', shape: shape, x1: u.x1, y1: u.y1, x2: u.x2, y2: u.y2, points: u.points || []});
    }
    function sendUpdate(index, shape, u) {
        postMessage({type:'region_update', index: index, shape: shape, x1: u.x1, y1: u.y1, x2: u.x2, y2: u.y2, points: u.points || []});
    }
    function sendForever(u) {
        postMessage({type:'region_set_forever', shape: u.shape, x1: u.x1, y1: u.y1, x2: u.x2, y2: u.y2, points: u.points || []});
    }

    var drawing = null, editBound = false;
    function surfaceDown(e) {
        if (e.button !== 0) return;
        if (tool === 'pick') { pickPart(e.clientX, e.clientY); return; }
        if (tool === 'polygon') {
            var p = toU(e.clientX, e.clientY);
            polyPts.push(p.x, p.y);
            drawPolyPreview();
            return;
        }
        drawing = {t0: toU(e.clientX, e.clientY), shape: tool};
        e.preventDefault();
    }
    function winMove(e) {
        if (!drawing) return;
        var p = toU(e.clientX, e.clientY);
        var x1 = Math.min(drawing.t0.x, p.x), y1 = Math.min(drawing.t0.y, p.y);
        var x2 = Math.max(drawing.t0.x, p.x), y2 = Math.max(drawing.t0.y, p.y);
        x1 = Math.round(x1 / 5) * 5; y1 = Math.round(y1 / 5) * 5;
        x2 = Math.round(x2 / 5) * 5; y2 = Math.round(y2 / 5) * 5;
        drawing.cur = {x1: x1, y1: y1, x2: x2, y2: y2};
        drawRectPreview(drawing.cur, drawing.shape);
    }
    function winUp() {
        if (!drawing) return;
        var c = drawing.cur, shape = drawing.shape;
        drawing = null;
        removePreview();
        if (!c) return;
        if (Math.abs(c.x2 - c.x1) < 6 || Math.abs(c.y2 - c.y1) < 6) return;
        sendAdd(shape, c);
    }
    function overlayDbl() {
        if (tool === 'polygon' && polyPts.length >= 6) {
            sendAdd('polygon', {points: polyPts.slice()});
            polyPts = [];
            removePreview();
        }
    }
    function editKeyDown(e) {
        if (!editMode) return;
        var tg = e.target;
        if (tg && (tg.tagName === 'INPUT' || tg.tagName === 'TEXTAREA')) return; // 正在输入框里打字
        if (e.key === 'Escape') { polyPts = []; removePreview(); postMessage({type:'region_edit_exit'}); }
        if (e.key === 'Delete' && selectedIndex >= 0) postMessage({type:'region_delete', index: selectedIndex});
    }
    function attachEditHandlers() {
        if (!editMode) return;
        var surface = document.getElementById('region-edit-surface');
        if (!surface) return;
        surface.addEventListener('mousedown', surfaceDown);
        if (!editBound) {
            editBound = true;
            window.addEventListener('mousemove', winMove);
            window.addEventListener('mouseup', winUp);
            window.addEventListener('keydown', editKeyDown);
            var ov = document.getElementById('region-overlay');
            if (ov) ov.addEventListener('dblclick', overlayDbl);
        }
        coordBoxes.forEach(function(b) {
            if (b.index < 0) bindMoveForever(b);
            else bindMoveShape(b);
        });
    }

    function drawRectPreview(c, shape) {
        removePreview();
        var layer = document.getElementById('region-layer');
        if (!layer) return;
        var d = document.createElement('div');
        d.className = 'region-box region-preview' + (shape === 'ellipse' ? ' shape-ellipse' : '');
        d.style.left = c.x1 + 'px'; d.style.top = c.y1 + 'px';
        d.style.width = (c.x2 - c.x1) + 'px'; d.style.height = (c.y2 - c.y1) + 'px';
        layer.appendChild(d);
        polyPreview = d;
    }
    function drawPolyPreview() {
        removePreview();
        var layer = document.getElementById('region-layer');
        if (!layer || polyPts.length < 2) return;
        var svg = document.createElementNS('http://www.w3.org/2000/svg', 'polyline');
        var pts = [];
        for (var i = 0; i + 1 < polyPts.length; i += 2) pts.push(polyPts[i] + ',' + polyPts[i + 1]);
        svg.setAttribute('points', pts.join(' '));
        svg.setAttribute('class', 'region-preview');
        layer.appendChild(svg);
        polyPreview = svg;
    }

    var selectedIndex = -1;
    function selectShape(b) {
        selectedIndex = b.index;
        coordBoxes.forEach(function(o) { if (o.el) o.el.classList.remove('selected'); });
        if (b.el) b.el.classList.add('selected');
    }

    // 把区域的模型单位包围盒缩放/移动成新的包围盒（多边形按比例缩放）
    function applyBox(b, nx1, ny1, nx2, ny2, shape) {
        var ow = Math.max(1e-6, b.x + b.w - b.x), oh = Math.max(1e-6, b.y + b.h - b.y);
        var r = b.region;
        if (shapeOf(r) === 'polygon' && r.points && r.points.length >= 6) {
            var sx = (nx2 - nx1) / ow, sy = (ny2 - ny1) / oh;
            var out = [];
            for (var i = 0; i + 1 < r.points.length; i += 2) {
                out.push(nx1 + (r.points[i] - b.x) * sx);
                out.push(ny1 + (r.points[i + 1] - b.y) * sy);
            }
            return {shape:'polygon', x1:nx1, y1:ny1, x2:nx2, y2:ny2, points:out};
        }
        return {shape:shapeOf(r), x1:nx1, y1:ny1, x2:nx2, y2:ny2, points:[]};
    }

    function bindMoveShape(b) {
        b.el.style.pointerEvents = 'auto';
        b.el.addEventListener('mousedown', function(e) {
            if (e.button !== 0) return;
            e.stopPropagation(); e.preventDefault();
            selectShape(b);
            var isResize = e.target.classList.contains('rb-handle');
            var startBox = {x1: b.x, y1: b.y, x2: b.x + b.w, y2: b.y + b.h};
            var start = toU(e.clientX, e.clientY);
            var moved = false;
            function onMove(ev) {
                var p = toU(ev.clientX, ev.clientY);
                var dx = p.x - start.x, dy = p.y - start.y;
                if (Math.abs(dx) + Math.abs(dy) > 1) moved = true;
                var nx1 = startBox.x1, ny1 = startBox.y1, nx2 = startBox.x2, ny2 = startBox.y2;
                if (isResize) {
                    var h = e.target.getAttribute('data-h');
                    if (h.indexOf('l') >= 0) nx1 = startBox.x1 + dx;
                    if (h.indexOf('r') >= 0) nx2 = startBox.x2 + dx;
                    if (h.indexOf('t') >= 0) ny1 = startBox.y1 + dy;
                    if (h.indexOf('b') >= 0) ny2 = startBox.y2 + dy;
                    if (nx2 - nx1 < 6) nx2 = nx1 + 6;
                    if (ny2 - ny1 < 6) ny2 = ny1 + 6;
                } else {
                    nx1 += dx; nx2 += dx; ny1 += dy; ny2 += dy;
                }
                b.el.style.left = Math.min(nx1, nx2) + 'px';
                b.el.style.top = Math.min(ny1, ny2) + 'px';
                b.el.style.width = Math.abs(nx2 - nx1) + 'px';
                b.el.style.height = Math.abs(ny2 - ny1) + 'px';
                if (shapeOf(b.region) === 'polygon') b.el.setAttribute('transform', 'translate(' + (nx1 - b.x) + ',' + (ny1 - b.y) + ')');
                b.cur = {nx1:nx1, ny1:ny1, nx2:nx2, ny2:ny2};
            }
            function onUp() {
                window.removeEventListener('mousemove', onMove);
                window.removeEventListener('mouseup', onUp);
                if (!moved || !b.cur) return;
                var u = applyBox(b, Math.min(b.cur.nx1, b.cur.nx2), Math.min(b.cur.ny1, b.cur.ny2), Math.max(b.cur.nx1, b.cur.nx2), Math.max(b.cur.ny1, b.cur.ny2), shapeOf(b.region));
                sendUpdate(b.index, shapeOf(b.region), u);
            }
            window.addEventListener('mousemove', onMove);
            window.addEventListener('mouseup', onUp);
        });
        // 四角缩放手柄（多边形是 SVG 元素、容不下 HTML 手柄，只支持整体移动）
        if (shapeOf(b.region) !== 'polygon') {
            ['tl', 'tr', 'bl', 'br'].forEach(function(h) {
                var hd = document.createElement('div');
                hd.className = 'rb-handle rb-handle-' + h;
                hd.setAttribute('data-h', h);
                b.el.appendChild(hd);
            });
        }
        // 双击区域 → 当场改名 / 改连点反应
        b.el.addEventListener('dblclick', function(e) { e.stopPropagation(); if (b.index >= 0) openMeta(b.index); });
    }

    function bindMoveForever(b) {
        if (!editMode) return;
        b.el.style.pointerEvents = 'auto';
        b.el.addEventListener('mousedown', function(e) {
            if (e.button !== 0) return;
            e.stopPropagation(); e.preventDefault();
            selectShape(b);
            var isResize = e.target.classList.contains('rb-handle');
            var startBox = {x1: b.x, y1: b.y, x2: b.x + b.w, y2: b.y + b.h};
            var start = toU(e.clientX, e.clientY);
            function onMove(ev) {
                var p = toU(ev.clientX, ev.clientY);
                var dx = p.x - start.x, dy = p.y - start.y;
                var nx1 = startBox.x1, ny1 = startBox.y1, nx2 = startBox.x2, ny2 = startBox.y2;
                if (isResize) {
                    var h = e.target.getAttribute('data-h');
                    if (h.indexOf('l') >= 0) nx1 = startBox.x1 + dx;
                    if (h.indexOf('r') >= 0) nx2 = startBox.x2 + dx;
                    if (h.indexOf('t') >= 0) ny1 = startBox.y1 + dy;
                    if (h.indexOf('b') >= 0) ny2 = startBox.y2 + dy;
                } else { nx1 += dx; nx2 += dx; ny1 += dy; ny2 += dy; }
                b.el.style.left = Math.min(nx1, nx2) + 'px';
                b.el.style.top = Math.min(ny1, ny2) + 'px';
                b.el.style.width = Math.abs(nx2 - nx1) + 'px';
                b.el.style.height = Math.abs(ny2 - ny1) + 'px';
                b.cur = {nx1:nx1, ny1:ny1, nx2:nx2, ny2:ny2};
            }
            function onUp() {
                window.removeEventListener('mousemove', onMove);
                window.removeEventListener('mouseup', onUp);
                if (!b.cur) return;
                sendForever({shape:shapeOf(b.region), x1:Math.min(b.cur.nx1, b.cur.nx2), y1:Math.min(b.cur.ny1, b.cur.ny2), x2:Math.max(b.cur.nx1, b.cur.nx2), y2:Math.max(b.cur.ny1, b.cur.ny2), points:[]});
            }
            window.addEventListener('mousemove', onMove);
            window.addEventListener('mouseup', onUp);
        });
        ['tl', 'tr', 'bl', 'br'].forEach(function(h) {
            var hd = document.createElement('div');
            hd.className = 'rb-handle rb-handle-' + h;
            hd.setAttribute('data-h', h);
            b.el.appendChild(hd);
        });
    }

    // ---- 一键拾取部件（人形连通块 + 凸包 → 多边形区域） ----
    function convexHull(pts) {
        if (pts.length < 3) return pts.slice();
        var p = pts.slice().sort(function(a, b) { return a.x === b.x ? a.y - b.y : a.x - b.x; });
        function cross(o, a, b) { return (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x); }
        var lower = [];
        for (var i = 0; i < p.length; i++) {
            while (lower.length >= 2 && cross(lower[lower.length - 2], lower[lower.length - 1], p[i]) <= 0) lower.pop();
            lower.push(p[i]);
        }
        var upper = [];
        for (var j = p.length - 1; j >= 0; j--) {
            while (upper.length >= 2 && cross(upper[upper.length - 2], upper[upper.length - 1], p[j]) <= 0) upper.pop();
            upper.push(p[j]);
        }
        lower.pop(); upper.pop();
        return lower.concat(upper);
    }
    function pickPart(wx, wy) {
        var m = lastMask;
        if (!m) { postMessage({type:'region_pick_empty'}); return; }
        var gx = Math.floor(wx / m.step), gy = Math.floor(wy / m.step);
        if (gx < 0 || gy < 0 || gx >= m.gw || gy >= m.gh) { postMessage({type:'region_pick_empty'}); return; }
        var start = gy * m.gw + gx;
        if (!m.bits[start]) { postMessage({type:'region_pick_empty'}); return; }
        var seen = new Uint8Array(m.gw * m.gh);
        var q = [start], cells = [], guard = 0;
        seen[start] = 1;
        while (q.length && guard++ < 200000) {
            var c = q.pop(); cells.push(c);
            var cx = c % m.gw, cy = (c - cx) / m.gw;
            if (cx > 0) { var a = c - 1; if (!seen[a] && m.bits[a]) { seen[a] = 1; q.push(a); } }
            if (cx < m.gw - 1) { var b2 = c + 1; if (!seen[b2] && m.bits[b2]) { seen[b2] = 1; q.push(b2); } }
            if (cy > 0) { var u = c - m.gw; if (!seen[u] && m.bits[u]) { seen[u] = 1; q.push(u); } }
            if (cy < m.gh - 1) { var d2 = c + m.gw; if (!seen[d2] && m.bits[d2]) { seen[d2] = 1; q.push(d2); } }
        }
        if (cells.length < 2) { postMessage({type:'region_pick_empty'}); return; }
        var pts = cells.map(function(c) { var cx = c % m.gw, cy = (c - cx) / m.gw; return {x: (cx + 0.5) * m.step, y: (cy + 0.5) * m.step}; });
        var hull = convexHull(pts);
        var up = [];
        hull.forEach(function(p) { var q2 = toU(p.x, p.y); up.push(q2.x, q2.y); });
        var name = '';
        try {
            var areas = model.hitTest ? model.hitTest(wx, wy) : null;
            if (areas && areas.then) areas.then(function(a) { });
        } catch (e) { }
        postMessage({type:'region_add', shape:'polygon', x1:0, y1:0, x2:0, y2:0, points:up, name:name});
    }

    buildBodyMask();
    setInterval(buildBodyMask, 1000);
    try {
        postMessage({type:'model_info', ow: model.internalModel.originalWidth, oh: model.internalModel.originalHeight});
    } catch (e) {}
    if (window.updatePetView) window.updatePetView();
    if (window.requestPetView) window.requestPetView();
    postMessage({type:'region_ready'});
})();
";

    public string CssCode => @"
#region-overlay {
    position:fixed; top:0; left:0; width:100%; height:100%;
    pointer-events:none; z-index:1200;
    opacity:0; transition:opacity 0.2s;
    background-image:
        linear-gradient(to right, rgba(140,140,140,0.35) 1px, transparent 1px),
        linear-gradient(to bottom, rgba(140,140,140,0.35) 1px, transparent 1px);
    background-size:50px 50px;
}
body:hover #region-overlay.show { opacity:1; }
#region-overlay.edit { opacity:1; background-color:rgba(30,30,30,0.12); }
#region-edit-surface { position:absolute; left:0; top:0; width:100%; height:100%; }
#region-overlay.edit #region-edit-surface { pointer-events:auto; cursor:crosshair; }
.region-box {
    position:absolute;
    background:rgba(220,220,220,0.2);
    border:1.5em dashed #888;
    border-radius:6em;
    display:flex; flex-direction:column; align-items:center; justify-content:center;
    color:#333; font-family:'Microsoft YaHei',sans-serif;
    overflow:hidden; box-sizing:border-box;
}
.region-box.shape-ellipse { border-radius:50%; }
.region-box.selected { border-color:#e06; background:rgba(238,0,102,0.15); }
.region-box .rb-name { font-weight:600; color:#222; font-size:11em; }
.region-box .rb-coord { font-size:10em; color:#444; }
.region-box.region-forever {
    background:rgba(130,130,130,0.25);
    border:1.5em solid #888;
    font-weight:600; color:#333; cursor:move; user-select:none;
    pointer-events:auto;
}
#region-overlay.edit .region-box { pointer-events:auto; cursor:move; }
.region-box.region-preview, .region-preview {
    background:rgba(0,160,255,0.18); border:1.5em dashed #0af; pointer-events:none;
}
polygon.region-box, polyline.region-preview {
    fill:rgba(220,220,220,0.2); stroke:#888; stroke-width:1.5; stroke-dasharray:6 4;
}
#region-overlay.edit polygon.region-box { pointer-events:auto; cursor:move; }
polygon.region-box.selected { fill:rgba(238,0,102,0.15); stroke:#e06; }
.rb-handle {
    position:absolute; width:10em; height:10em; background:#e06; border-radius:2em;
    pointer-events:auto; z-index:5;
}
.rb-handle-tl { left:-5em; top:-5em; cursor:nwse-resize; }
.rb-handle-tr { right:-5em; top:-5em; cursor:nesw-resize; }
.rb-handle-bl { left:-5em; bottom:-5em; cursor:nesw-resize; }
.rb-handle-br { right:-5em; bottom:-5em; cursor:nwse-resize; }
#region-overlay.edit .rb-handle { display:block; }
.region-ruler-x { position:fixed; top:0; left:0; width:100%; height:0; pointer-events:none; }
.region-ruler-x span { position:absolute; top:2px; transform:translateX(-50%); font-size:9px; color:#333; font-family:'Consolas',monospace; background:rgba(200,200,200,0.85); padding:0 1px; }
.region-ruler-y { position:fixed; top:0; left:0; width:0; height:100%; pointer-events:none; }
.region-ruler-y span { position:absolute; left:3px; transform:translateY(-50%); font-size:9px; color:#333; font-family:'Consolas',monospace; background:rgba(200,200,200,0.85); padding:0 1px; }
.region-ruler-xb { position:fixed; bottom:0; left:0; width:100%; height:0; pointer-events:none; }
.region-ruler-xb span { position:absolute; bottom:2px; transform:translateX(-50%); font-size:9px; color:#333; font-family:'Consolas',monospace; background:rgba(200,200,200,0.85); padding:0 1px; }
.region-ruler-yr { position:fixed; top:0; right:0; width:0; height:100%; pointer-events:none; }
.region-ruler-yr span { position:absolute; right:3px; transform:translateY(-50%); font-size:9px; color:#333; font-family:'Consolas',monospace; background:rgba(200,200,200,0.85); padding:0 1px; }
#region-toggle-btn, #region-edit-btn {
    position:fixed; right:15px; width:28px; height:28px;
    background:rgba(0,0,0,0.4); backdrop-filter:blur(10px); border-radius:50%;
    display:flex; justify-content:center; align-items:center;
    color:white; cursor:pointer; z-index:2000;
    box-shadow:0 4px 10px rgba(0,0,0,0.2);
    border:1px solid rgba(255,255,255,0.15);
    opacity:0; transition:opacity 0.3s, background 0.3s;
}
#region-toggle-btn { bottom:124px; }
#region-edit-btn { bottom:158px; }
#region-toggle-btn.near, #region-edit-btn.near { opacity:1; }
#region-toggle-btn:hover, #region-edit-btn:hover { background:rgba(0,0,0,0.6); }
#region-toggle-btn.faded { filter:grayscale(1); background:rgba(0,0,0,0.22); border-color:rgba(255,255,255,0.1); }
#region-toolbar {
    position:fixed; top:8px; left:50%; transform:translateX(-50%);
    display:none; gap:5px; align-items:center; justify-content:center; flex-wrap:wrap;
    max-width:calc(100% - 12px); box-sizing:border-box;
    background:rgba(20,20,20,0.82); color:#eee; border-radius:10px;
    padding:6px 8px; z-index:2200; font-size:12px; font-family:'Microsoft YaHei',sans-serif;
    box-shadow:0 6px 20px rgba(0,0,0,0.35);
}
#region-toolbar.show { display:flex; }
#region-toolbar button {
    background:rgba(255,255,255,0.12); color:#eee; border:1px solid rgba(255,255,255,0.2);
    border-radius:6px; padding:3px 8px; cursor:pointer; font-size:12px;
}
#region-toolbar button.active { background:#e06; border-color:#e06; }
#region-toolbar button:hover { background:rgba(255,255,255,0.25); }
#region-toolbar .sep { width:1px; height:16px; background:rgba(255,255,255,0.25); }
#region-meta {
    position:fixed; left:8px; top:8px; width:212px; max-height:calc(100% - 16px);
    display:none; flex-direction:column; gap:4px;
    background:rgba(22,22,26,0.95); color:#eee; border-radius:10px; padding:8px;
    z-index:2600; font-size:12px; font-family:'Microsoft YaHei',sans-serif;
    box-shadow:0 8px 24px rgba(0,0,0,0.5); border:1px solid rgba(255,255,255,0.12);
    box-sizing:border-box; overflow:hidden;
}
#region-meta.show { display:flex; }
#region-meta .rm-title { font-weight:600; }
#region-meta input[type=text], #region-meta input[type=number] {
    background:rgba(255,255,255,0.1); border:1px solid rgba(255,255,255,0.2); color:#eee;
    border-radius:5px; padding:3px 6px; font-size:12px; box-sizing:border-box;
}
#region-meta .rm-sub { color:#8ab4f8; margin-top:2px; }
#rm-name { width:100%; }
#rm-taps { display:flex; flex-direction:column; gap:4px; overflow-y:auto; max-height:150px; }
.rm-tap { display:flex; gap:4px; align-items:center; }
.rm-tap input[type=number] { width:44px; }
.rm-tap input[type=text] { flex:1; min-width:0; }
.rm-tap .rm-del { cursor:pointer; color:#f87171; padding:0 2px; }
.rm-btn { text-align:center; padding:4px 6px; border-radius:5px; cursor:pointer; background:rgba(255,255,255,0.12); }
.rm-btn:hover { background:rgba(255,255,255,0.22); }
.rm-actions { display:flex; gap:4px; }
.rm-btn.primary { background:#2563eb; }
.rm-btn.danger { background:rgba(220,38,38,0.75); }
#rm-gestures { display:flex; flex-direction:column; gap:4px; }
.rm-g { display:flex; gap:4px; align-items:center; }
.rm-g label { display:flex; align-items:center; gap:3px; white-space:nowrap; cursor:pointer; }
.rm-g input[type=text] { flex:1; min-width:0; }
";

    public string HtmlCode => @"
<div id='region-overlay'></div>
<div id='region-toggle-btn' title='坐标区域显示开关'>
    <svg viewBox='0 0 24 24' width='16' height='16' fill='currentColor'>
        <path d='M12 2L2 7l10 5 10-5-10-5zM2 17l10 5 10-5M2 12l10 5 10-5'/>
    </svg>
</div>
<div id='region-edit-btn' title='编辑区域（拖画/拾取）'>
    <svg viewBox='0 0 24 24' width='16' height='16' fill='currentColor'>
        <path d='M3 17.25V21h3.75L17.81 9.94l-3.75-3.75L3 17.25zM20.71 7.04a1 1 0 0 0 0-1.41l-2.34-2.34a1 1 0 0 0-1.41 0l-1.83 1.83 3.75 3.75 1.83-1.83z'/>
    </svg>
</div>
<div id='region-toolbar'>
    <button id='rt-rect' class='active'>矩形</button>
    <button id='rt-ellipse'>椭圆</button>
    <button id='rt-polygon'>多边形</button>
    <button id='rt-pick'>拾取部件</button>
    <span class='sep'></span>
    <button id='rt-unit'>坐标:窗口</button>
    <button id='rt-undo'>撤销</button>
    <button id='rt-del'>删除</button>
    <span class='sep'></span>
    <button id='rt-export'>导出</button>
    <button id='rt-import'>导入</button>
    <button id='rt-done'>完成</button>
</div>
<div id='region-meta'>
    <div class='rm-title'>区域设置</div>
    <div>名称</div>
    <input id='rm-name' type='text' />
    <div class='rm-sub'>连点档位（点几下 → 告诉桌宠什么）</div>
    <div id='rm-taps'></div>
    <div class='rm-btn' data-rm='addtap'>+ 添加档位</div>
    <div class='rm-sub'>手势反应（勾选才生效；留空用默认话术）</div>
    <div id='rm-gestures'>
        <div class='rm-g'><label><input type='checkbox' class='rm-gon' data-g='dblclick' />双击</label><input type='text' class='rm-gtx' data-g='dblclick' /></div>
        <div class='rm-g'><label><input type='checkbox' class='rm-gon' data-g='longpress' />长按</label><input type='text' class='rm-gtx' data-g='longpress' /></div>
        <div class='rm-g'><label><input type='checkbox' class='rm-gon' data-g='stroke' />抚摸</label><input type='text' class='rm-gtx' data-g='stroke' /></div>
        <div class='rm-g'><label><input type='checkbox' class='rm-gon' data-g='wheel' />滚轮</label><input type='text' class='rm-gtx' data-g='wheel' /></div>
    </div>
    <div class='rm-actions'>
        <div class='rm-btn danger' data-rm='delete'>删除</div>
        <div class='rm-btn primary' data-rm='save'>保存</div>
        <div class='rm-btn' data-rm='cancel'>取消</div>
    </div>
</div>
";

    readonly PetBridge bridge;
    readonly StorageSystem storage;
    readonly PetWindow window;
    readonly PetInteractionHub hub;
    readonly SubtitleModule subtitleModule;
    readonly ExpressionModule expressionModule;
    readonly InteractedEventCallback onInteracted;
    readonly string overlayKey;
    readonly string regionsKey;
    readonly string foreverKey;
    readonly string spaceVersionKey;
    readonly string gestureVersionKey;
    bool overlayVisible;

    // ---- 模型视图（前端 pet_view 上报），「模型单位」坐标空间的换算基准 ----
    const int RegionSpaceVersion = 2;
    readonly object viewLock = new();
    double viewMx, viewMy, viewMs = 1;
    bool hasView;
    double modelOh;

    // ---- 编辑模式 ----
    bool editMode;
    long editLastActivity;
    Timer? editTimer;
    // 编辑中的真实操作（画框/拖动/改名/心跳）都算"还在编辑"，避免自动退出把面板变成穿透态
    static readonly HashSet<string> EditActivityTypes = new()
    {
        "region_add", "region_update", "region_delete", "region_set_forever",
        "region_undo", "region_save_meta", "region_export", "region_import",
        "region_refresh", "region_pick_empty", "region_activity",
        "forever_drag_start", "forever_drag"
    };
    readonly object undoLock = new();
    readonly List<List<PetRegionConfig>> undoStack = new();

    public RegionInteractionModule(PetBridge bridge, StorageSystem storage, PetWindow window, PetStorageKey storageKey,
        SubtitleModule subtitleModule, ExpressionModule expressionModule, InteractedEventCallback onInteracted,
        PetInteractionHub hub)
    {
        this.bridge = bridge;
        this.storage = storage;
        this.window = window;
        this.subtitleModule = subtitleModule;
        this.expressionModule = expressionModule;
        this.onInteracted = onInteracted;
        this.hub = hub;
        overlayKey = $"{storageKey.Value}/Live2DDeskPet/RegionOverlayVisible";
        regionsKey = $"{storageKey.Value}/Live2DDeskPet/PetRegions";
        foreverKey = $"{storageKey.Value}/Live2DDeskPet/PetForeverRegion";
        spaceVersionKey = $"{storageKey.Value}/Live2DDeskPet/RegionSpaceVersion";
        gestureVersionKey = $"{storageKey.Value}/Live2DDeskPet/RegionGestureVersion";
        overlayVisible = storage.GetObject(overlayKey, false);
        bridge.OnMessage += OnBridgeMessage;
        EnsureGestureDefaults();

        // 区域编辑相关的可配置项（任何模块都能这样注册；面板自动渲染）
        hub.Register(new PetSetting
        {
            Key = "shape_default", Label = "默认形状", Group = "区域编辑", Type = PetSettingType.Choice,
            Default = "rect", Options = new[] { "rect", "ellipse", "polygon" }, OptionLabels = new[] { "矩形", "椭圆", "多边形" }
        });
        hub.Register(new PetSetting
        {
            Key = "edit_timeout", Label = "编辑自动退出(秒)", Group = "区域编辑", Type = PetSettingType.Number,
            Default = "120", Min = 15, Max = 600, Step = 15
        });
        hub.Register(new PetSetting
        {
            Key = "ruler", Label = "显示坐标刻度尺", Group = "区域编辑", Type = PetSettingType.Bool, Default = "1"
        });
    }
    public void Dispose()
    {
        bridge.OnMessage -= OnBridgeMessage;
        editTimer?.Dispose();
    }

    /// <summary>窗口坐标 → 模型单位（区域存储空间）。视图未就绪时原样返回。</summary>
    public (double X, double Y) ToCalibrated(double wx, double wy)
    {
        lock (viewLock)
        {
            return hasView ? ((wx - viewMx) / viewMs, (wy - viewMy) / viewMs) : (wx, wy);
        }
    }

    /// <summary>模型单位（区域存储空间）→ 窗口坐标。视图未就绪时原样返回。</summary>
    public (double X, double Y) ToWindow(double ux, double uy)
    {
        lock (viewLock)
        {
            return hasView ? (viewMx + ux * viewMs, viewMy + uy * viewMs) : (ux, uy);
        }
    }

    public bool HasView { get { lock (viewLock) { return hasView; } } }

    /// <summary>命中查询：窗口坐标处的区域名（供手势模块描述"摸到了哪里"）。无则返回 null。</summary>
    public string? DescribeRegionAt(double wx, double wy)
    {
        (double ux, double uy) = ToCalibrated(wx, wy);
        foreach (PetRegionConfig r in GetRegions())
        {
            if (r.Contains(ux, uy)) return r.Name;
        }
        PetRegionConfig f = GetForeverRegion();
        if (f.Contains(ux, uy)) return f.Name;
        return null;
    }

    /// <summary>窗口坐标处的区域（含永久区）。无则 null。</summary>
    public PetRegionConfig? RegionAt(double wx, double wy)
    {
        (double ux, double uy) = ToCalibrated(wx, wy);
        foreach (PetRegionConfig r in GetRegions())
        {
            if (r.Contains(ux, uy)) return r;
        }
        PetRegionConfig f = GetForeverRegion();
        return f.Contains(ux, uy) ? f : null;
    }

    /// <summary>
    /// 手势在该坐标处是否有区域反应：区域启用了该手势则返回要发给角色的话术，否则 null。
    /// 手势交互统一走区域机制——不落在已配置区域内就不上报。
    /// </summary>
    public string? GestureMessageAt(string kind, double wx, double wy)
    {
        PetRegionConfig? r = RegionAt(wx, wy);
        if (r == null) return null;
        return kind switch
        {
            "dblclick" => r.DblclickOn ? DefaultText(r.DblclickMsg, r.Name, "双击") : null,
            "longpress" => r.LongpressOn ? DefaultText(r.LongpressMsg, r.Name, "长按") : null,
            "stroke" => r.StrokeOn ? DefaultText(r.StrokeMsg, r.Name, "来回抚摸") : null,
            "wheel" => r.WheelOn ? DefaultText(r.WheelMsg, r.Name, "用滚轮戳") : null,
            _ => null,
        };
    }

    /// <summary>自定义话术留空时用的默认模板。</summary>
    public static string DefaultText(string msg, string regionName, string verb) =>
        string.IsNullOrWhiteSpace(msg) ? $"哥哥{verb}了桌宠的「{regionName}」" : msg;

    /// <summary>
    /// 一次性迁移：区域新增了「手势反应」后，老区域（从没配过手势）统一按默认「只开双击」，
    /// 之后用户在任何区域里的勾选都不会被覆盖。
    /// </summary>
    void EnsureGestureDefaults()
    {
        const int version = 1;
        if (storage.GetObject(gestureVersionKey, 0) >= version) return;
        List<PetRegionConfig> regions = GetRegions();
        if (regions.Count > 0)
        {
            foreach (PetRegionConfig r in regions)
                if (!r.DblclickOn && !r.LongpressOn && !r.StrokeOn && !r.WheelOn)
                    r.DblclickOn = true;
            storage.SetObject(regionsKey, regions);
        }
        storage.SetObject(gestureVersionKey, version);
    }

    void EnsureRegionsMigrated()
    {
        lock (viewLock)
        {
            if (modelOh <= 0) return;
        }
        if (storage.GetObject(spaceVersionKey, 0) == RegionSpaceVersion) return;

        double s0 = 540 * 1.1 / modelOh;
        List<PetRegionConfig> regions = GetRegions();
        foreach (PetRegionConfig r in regions)
        {
            r.X1 = (r.X1 - 180) / s0;
            r.X2 = (r.X2 - 180) / s0;
            r.Y1 = (r.Y1 - 270) / s0;
            r.Y2 = (r.Y2 - 270) / s0;
        }
        storage.SetObject(regionsKey, regions);
        PetRegionConfig? f = storage.GetObject<PetRegionConfig>(foreverKey);
        if (f != null)
        {
            f.X1 = (f.X1 - 180) / s0;
            f.X2 = (f.X2 - 180) / s0;
            f.Y1 = (f.Y1 - 270) / s0;
            f.Y2 = (f.Y2 - 270) / s0;
            storage.SetObject(foreverKey, f);
        }
        storage.SetObject(spaceVersionKey, RegionSpaceVersion);
        RefreshOverlay();
    }

    void OnBridgeMessage(string type, JsonElement data)
    {
        if (editMode && EditActivityTypes.Contains(type)) editLastActivity = Environment.TickCount64;
        switch (type)
        {
            case "region_ready":
                bridge.SendMessage("region_overlay", new { visible = overlayVisible });
                bridge.SendMessage("edit_mode", new { on = editMode });
                RefreshOverlay();
                break;
            case "region_toggle":
                overlayVisible = !overlayVisible;
                storage.SetObject(overlayKey, overlayVisible);
                bridge.SendMessage("region_overlay", new { visible = overlayVisible });
                if (overlayVisible) RefreshOverlay();
                break;
            case "region_edit_toggle":
                SetEditMode(!editMode);
                break;
            case "region_edit_exit":
                SetEditMode(false);
                break;
            case "region_refresh":
                RefreshOverlay();
                break;
            case "region_save_meta":
                SaveMetaFromMessage(data);
                break;
            case "region_add":
                PushUndo();
                AddRegionFromMessage(data);
                break;
            case "region_update":
                PushUndo();
                UpdateRegionFromMessage(data);
                break;
            case "region_delete":
                PushUndo();
                DeleteRegionFromMessage(data);
                break;
            case "region_set_forever":
                PushUndo();
                SetForeverFromMessage(data);
                break;
            case "region_undo":
                Undo();
                break;
            case "region_export":
                ExportPreset();
                break;
            case "region_import":
                ImportPreset();
                break;
            case "region_pick_empty":
                editLastActivity = Environment.TickCount64;
                break;
            case "region_click":
                if (editMode) break;
                HandleRegionClick(data);
                break;
            case "pet_view":
                double pvMx = data.TryGetProperty("mx", out JsonElement pvMxProp) ? pvMxProp.GetDouble() : 0;
                double pvMy = data.TryGetProperty("my", out JsonElement pvMyProp) ? pvMyProp.GetDouble() : 0;
                double pvMs = data.TryGetProperty("ms", out JsonElement pvMsProp) ? pvMsProp.GetDouble() : 1;
                lock (viewLock)
                {
                    if (pvMs > 0) viewMs = pvMs;
                    viewMx = pvMx;
                    viewMy = pvMy;
                    hasView = true;
                }
                break;
            case "model_info":
                double oh = data.TryGetProperty("oh", out JsonElement ohProp) ? ohProp.GetDouble() : 0;
                if (oh > 0)
                {
                    lock (viewLock) { modelOh = oh; }
                    EnsureRegionsMigrated();
                }
                break;
            case "forever_drag_start":
                foreverStart = window.Bounds;
                hasForeverStart = true;
                break;
            case "forever_drag":
                if (editMode) break;
                HandleForeverDrag(data);
                break;
        }
    }

    // ---- 编辑模式 ----
    void SetEditMode(bool on)
    {
        if (editMode == on) return;
        editMode = on;
        hub.EditMode = on;
        editLastActivity = Environment.TickCount64;
        bridge.SendMessage("edit_mode", new { on });
        if (on) editTimer ??= new Timer(_ => CheckEditTimeout(), null, 5000, 5000);
        // 进入/退出都重发区域数据：前端据此重建 overlay 并挂载（或卸载）编辑事件
        RefreshOverlay();
    }

    void CheckEditTimeout()
    {
        if (!editMode) return;
        double seconds = hub.GetNumber("edit_timeout");
        if (seconds < 15) seconds = 15;
        if (Environment.TickCount64 - editLastActivity > (long)(seconds * 1000))
            SetEditMode(false); // 无操作自动退出，避免忘记退出卡住鼠标
    }

    void PushUndo()
    {
        lock (undoLock)
        {
            undoStack.Add(GetRegions());
            if (undoStack.Count > 30) undoStack.RemoveAt(0);
        }
    }

    void Undo()
    {
        lock (undoLock)
        {
            if (undoStack.Count == 0) return;
            List<PetRegionConfig> prev = undoStack[^1];
            undoStack.RemoveAt(undoStack.Count - 1);
            storage.SetObject(regionsKey, prev);
        }
        RefreshOverlay();
    }

    static string ShapeStr(PetRegionShape shape) => shape switch
    {
        PetRegionShape.Ellipse => "ellipse",
        PetRegionShape.Polygon => "polygon",
        _ => "rect",
    };

    static PetRegionShape ParseShape(string? s) => s switch
    {
        "ellipse" => PetRegionShape.Ellipse,
        "polygon" => PetRegionShape.Polygon,
        _ => PetRegionShape.Rect,
    };

    static PetRegionConfig ReadRegion(JsonElement data, PetRegionConfig target)
    {
        target.Shape = ParseShape(data.TryGetProperty("shape", out JsonElement sp) ? sp.GetString() : null);
        if (data.TryGetProperty("name", out JsonElement np) && np.ValueKind == JsonValueKind.String)
        {
            string n = np.GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(n)) target.Name = n;
        }
        if (data.TryGetProperty("x1", out JsonElement x1) && x1.ValueKind == JsonValueKind.Number) target.X1 = x1.GetDouble();
        if (data.TryGetProperty("y1", out JsonElement y1) && y1.ValueKind == JsonValueKind.Number) target.Y1 = y1.GetDouble();
        if (data.TryGetProperty("x2", out JsonElement x2) && x2.ValueKind == JsonValueKind.Number) target.X2 = x2.GetDouble();
        if (data.TryGetProperty("y2", out JsonElement y2) && y2.ValueKind == JsonValueKind.Number) target.Y2 = y2.GetDouble();
        if (data.TryGetProperty("points", out JsonElement pts) && pts.ValueKind == JsonValueKind.Array)
        {
            List<double> list = new();
            foreach (JsonElement v in pts.EnumerateArray())
                if (v.ValueKind == JsonValueKind.Number) list.Add(v.GetDouble());
            if (list.Count >= 6) target.Points = list;
        }
        return target;
    }

    void AddRegionFromMessage(JsonElement data)
    {
        List<PetRegionConfig> regions = GetRegions();
        PetRegionConfig r = ReadRegion(data, new PetRegionConfig());
        if (string.IsNullOrWhiteSpace(r.Name)) r.Name = $"区域{regions.Count + 1}";
        r.DblclickOn = true; // 新建区域默认只开「双击」反应，其余手势按需自行勾选
        regions.Add(r);
        storage.SetObject(regionsKey, regions);
        editLastActivity = Environment.TickCount64;
        RefreshOverlay();
    }

    void UpdateRegionFromMessage(JsonElement data)
    {
        if (data.TryGetProperty("index", out JsonElement ip) == false) return;
        int index = ip.GetInt32();
        List<PetRegionConfig> regions = GetRegions();
        if (index < 0 || index >= regions.Count) return;
        PetRegionConfig r = regions[index];
        r.Shape = ParseShape(data.TryGetProperty("shape", out JsonElement sp) ? sp.GetString() : ShapeStr(r.Shape));
        if (data.TryGetProperty("x1", out JsonElement x1) && x1.ValueKind == JsonValueKind.Number) r.X1 = x1.GetDouble();
        if (data.TryGetProperty("y1", out JsonElement y1) && y1.ValueKind == JsonValueKind.Number) r.Y1 = y1.GetDouble();
        if (data.TryGetProperty("x2", out JsonElement x2) && x2.ValueKind == JsonValueKind.Number) r.X2 = x2.GetDouble();
        if (data.TryGetProperty("y2", out JsonElement y2) && y2.ValueKind == JsonValueKind.Number) r.Y2 = y2.GetDouble();
        if (data.TryGetProperty("points", out JsonElement pts) && pts.ValueKind == JsonValueKind.Array)
        {
            List<double> list = new();
            foreach (JsonElement v in pts.EnumerateArray())
                if (v.ValueKind == JsonValueKind.Number) list.Add(v.GetDouble());
            if (list.Count >= 6) r.Points = list;
        }
        storage.SetObject(regionsKey, regions);
        editLastActivity = Environment.TickCount64;
        RefreshOverlay();
    }

    void DeleteRegionFromMessage(JsonElement data)
    {
        if (data.TryGetProperty("index", out JsonElement ip) == false) return;
        int index = ip.GetInt32();
        List<PetRegionConfig> regions = GetRegions();
        if (index < 0 || index >= regions.Count) return;
        regions.RemoveAt(index);
        storage.SetObject(regionsKey, regions);
        RefreshOverlay();
    }

    /// <summary>当场编辑区域的名称与连点档位（桌宠窗口内的区域设置面板）。</summary>
    void SaveMetaFromMessage(JsonElement data)
    {
        if (data.TryGetProperty("index", out JsonElement ip) == false) return;
        int index = ip.GetInt32();
        List<PetRegionConfig> regions = GetRegions();
        if (index < 0 || index >= regions.Count) return;
        PushUndo();
        PetRegionConfig r = regions[index];
        if (data.TryGetProperty("name", out JsonElement np) && np.ValueKind == JsonValueKind.String)
        {
            string name = np.GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(name)) r.Name = name;
        }
        if (data.TryGetProperty("taps", out JsonElement tp) && tp.ValueKind == JsonValueKind.Array)
        {
            List<PetRegionTap> taps = new();
            foreach (JsonElement t in tp.EnumerateArray())
            {
                int times = t.TryGetProperty("times", out JsonElement tv) && tv.ValueKind == JsonValueKind.Number ? tv.GetInt32() : 1;
                string msg = t.TryGetProperty("message", out JsonElement mv) ? (mv.GetString() ?? "") : "";
                if (times < 1) times = 1;
                taps.Add(new PetRegionTap { Times = times, Message = msg });
            }
            taps.Sort((a, b) => a.Times.CompareTo(b.Times));
            r.Taps = taps;
        }
        ApplyGestures(data, r);
        storage.SetObject(regionsKey, regions);
        RefreshOverlay();
    }

    /// <summary>读取区域设置面板提交的手势反应（勾选开关 + 话术）。</summary>
    static void ApplyGestures(JsonElement data, PetRegionConfig r)
    {
        if (data.TryGetProperty("gestures", out JsonElement gp) == false || gp.ValueKind != JsonValueKind.Object) return;
        void One(string key, ref bool on, ref string msg)
        {
            if (gp.TryGetProperty(key, out JsonElement g) == false || g.ValueKind != JsonValueKind.Object) return;
            on = g.TryGetProperty("on", out JsonElement ov) &&
                 (ov.ValueKind == JsonValueKind.True || (ov.ValueKind == JsonValueKind.Number && ov.GetDouble() != 0));
            msg = g.TryGetProperty("msg", out JsonElement mv) && mv.ValueKind == JsonValueKind.String ? (mv.GetString() ?? "") : "";
        }
        bool dOn = r.DblclickOn, lOn = r.LongpressOn, sOn = r.StrokeOn, wOn = r.WheelOn;
        string dMsg = r.DblclickMsg, lMsg = r.LongpressMsg, sMsg = r.StrokeMsg, wMsg = r.WheelMsg;
        One("dblclick", ref dOn, ref dMsg);
        One("longpress", ref lOn, ref lMsg);
        One("stroke", ref sOn, ref sMsg);
        One("wheel", ref wOn, ref wMsg);
        r.DblclickOn = dOn; r.DblclickMsg = dMsg;
        r.LongpressOn = lOn; r.LongpressMsg = lMsg;
        r.StrokeOn = sOn; r.StrokeMsg = sMsg;
        r.WheelOn = wOn; r.WheelMsg = wMsg;
    }

    void SetForeverFromMessage(JsonElement data)
    {
        PetRegionConfig f = ReadRegion(data, GetForeverRegion());
        storage.SetObject(foreverKey, f);
        RefreshOverlay();
    }

    void ExportPreset()
    {
        try
        {
            string dir = Path.Combine(AlifePath.StorageFolderPath, "Live2DDeskPet", "RegionPresets");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "regions.json");
            string json = JsonSerializer.Serialize(GetRegions(), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(file, json);
            hub.Report($"[桌宠区域预设已导出: {file}]");
        }
        catch (Exception e)
        {
            hub.Report("桌宠区域导出失败: " + e.Message);
        }
    }

    void ImportPreset()
    {
        try
        {
            string file = Path.Combine(AlifePath.StorageFolderPath, "Live2DDeskPet", "RegionPresets", "regions.json");
            if (!File.Exists(file)) { hub.Report("桌宠区域导入失败: 未找到 regions.json"); return; }
            List<PetRegionConfig>? regions = JsonSerializer.Deserialize<List<PetRegionConfig>>(File.ReadAllText(file));
            if (regions == null) return;
            PushUndo();
            storage.SetObject(regionsKey, regions);
            RefreshOverlay();
        }
        catch (Exception e)
        {
            hub.Report("桌宠区域导入失败: " + e.Message);
        }
    }

    void HandleRegionClick(JsonElement data)
    {
        if (data.TryGetProperty("x", out JsonElement xProp) == false) return;
        if (data.TryGetProperty("y", out JsonElement yProp) == false) return;
        double x = xProp.GetDouble();
        double y = yProp.GetDouble();

        List<PetRegionConfig> regions = GetRegions();
        if (regions.Count == 0) return;

        double cmx = data.TryGetProperty("mx", out JsonElement cmxProp) ? cmxProp.GetDouble() : 0;
        double cmy = data.TryGetProperty("my", out JsonElement cmyProp) ? cmyProp.GetDouble() : 0;
        double cms = data.TryGetProperty("ms", out JsonElement cmsProp) ? cmsProp.GetDouble() : 0;
        double ux, uy;
        if (cms > 0) { ux = (x - cmx) / cms; uy = (y - cmy) / cms; }
        else { (ux, uy) = ToCalibrated(x, y); }

        PetRegionConfig? hit = null;
        foreach (PetRegionConfig region in regions)
        {
            if (region.Contains(ux, uy)) { hit = region; break; }
        }
        if (hit == null) return;

        long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        if (now - lastHitTime > 800) combo = 1;
        else combo++;
        lastHitTime = now;
        if (combo > 10) combo = 1;

        TriggerFor(hit, combo);
    }

    void TriggerFor(PetRegionConfig region, int count)
    {
        List<PetRegionTap> taps = region.Taps ?? new();
        if (taps.Count == 0) return;

        PetRegionTap? tap = taps.FirstOrDefault(t => t.Times == count);
        if (tap == null)
            tap = taps.Where(t => t.Times <= count).OrderByDescending(t => t.Times).FirstOrDefault();
        if (tap == null)
            return;

        string message = string.IsNullOrWhiteSpace(tap.Message)
            ? $"桌宠部位「{region.Name}」被触摸 x{count}"
            : tap.Message;
        onInteracted?.Invoke(message);
    }

    public List<PetRegionConfig> GetRegions() => storage.GetObject<List<PetRegionConfig>>(regionsKey) ?? new();

    public PetRegionConfig GetForeverRegion()
    {
        PetRegionConfig? f = storage.GetObject<PetRegionConfig>(foreverKey);
        if (f == null)
        {
            f = new PetRegionConfig { Name = "永久区", X1 = -100, Y1 = -100, X2 = 100, Y2 = 100 };
            storage.SetObject(foreverKey, f);
        }
        return f;
    }

    public void SaveForeverRegion(PetRegionConfig region)
    {
        storage.SetObject(foreverKey, region ?? new PetRegionConfig { Name = "永久区" });
        RefreshOverlay();
    }

    void HandleForeverDrag(JsonElement data)
    {
        if (data.TryGetProperty("dx", out JsonElement dxProp) == false) return;
        if (data.TryGetProperty("dy", out JsonElement dyProp) == false) return;
        int dx = dxProp.GetInt32();
        int dy = dyProp.GetInt32();
        if (hasForeverStart == false) { foreverStart = window.Bounds; hasForeverStart = true; }
        Rectangle b = foreverStart;
        b.X += dx;
        b.Y += dy;
        foreverStart = b;
        _ = window.SetBoundsAsync(b);
    }

    public void SaveRegions(List<PetRegionConfig> regions)
    {
        storage.SetObject(regionsKey, regions ?? new List<PetRegionConfig>());
        RefreshOverlay();
    }

    public void RefreshOverlay()
    {
        var boxes = GetRegions().Select((r, i) => new
        {
            index = i,
            name = r.Name,
            shape = ShapeStr(r.Shape),
            x = r.MinX,
            y = r.MinY,
            w = r.MaxX - r.MinX,
            h = r.MaxY - r.MinY,
            points = r.Points ?? new List<double>(),
            taps = (r.Taps ?? new List<PetRegionTap>()).Select(t => new { times = t.Times, message = t.Message }).ToList(),
            gestures = new
            {
                dblclick = new { on = r.DblclickOn, msg = r.DblclickMsg },
                longpress = new { on = r.LongpressOn, msg = r.LongpressMsg },
                stroke = new { on = r.StrokeOn, msg = r.StrokeMsg },
                wheel = new { on = r.WheelOn, msg = r.WheelMsg },
            },
        }).ToList();
        PetRegionConfig f = GetForeverRegion();
        object forever = new
        {
            index = -1,
            name = f.Name,
            shape = ShapeStr(f.Shape),
            x = f.MinX,
            y = f.MinY,
            w = f.MaxX - f.MinX,
            h = f.MaxY - f.MinY,
            points = f.Points ?? new List<double>(),
        };
        bridge.SendMessage("region_boxes", new { boxes, forever, edit = editMode });
    }

    long lastHitTime;
    int combo;
    Rectangle foreverStart;
    bool hasForeverStart;
}

public enum PetRegionShape
{
    Rect = 0,
    Ellipse = 1,
    Polygon = 2,
}

/// <summary>桌宠点击区域配置：形状 + 模型单位包围盒/多边形顶点 + 连点档位动作。</summary>
public class PetRegionConfig
{
    public string Name { get; set; } = "";
    public PetRegionShape Shape { get; set; } = PetRegionShape.Rect;
    public double X1 { get; set; }
    public double Y1 { get; set; }
    public double X2 { get; set; }
    public double Y2 { get; set; }
    /// <summary>多边形顶点（模型单位，扁平存储 [x1,y1,x2,y2,...]）。Shape=Polygon 时使用。</summary>
    public List<double> Points { get; set; } = new();
    public List<PetRegionTap> Taps { get; set; } = new();

    // ---- 手势反应（按区域各自配置；On=是否启用，Msg=自定义话术，留空用默认"哥哥X了桌宠的「区域名」"） ----
    public bool DblclickOn { get; set; }
    public string DblclickMsg { get; set; } = "";
    public bool LongpressOn { get; set; }
    public string LongpressMsg { get; set; } = "";
    public bool StrokeOn { get; set; }
    public string StrokeMsg { get; set; } = "";
    public bool WheelOn { get; set; }
    public string WheelMsg { get; set; } = "";

    public double MinX => Math.Min(X1, X2);
    public double MinY => Math.Min(Y1, Y2);
    public double MaxX => Math.Max(X1, X2);
    public double MaxY => Math.Max(Y1, Y2);

    public bool Contains(double x, double y)
    {
        switch (Shape)
        {
            case PetRegionShape.Ellipse:
                {
                    double cx = (MinX + MaxX) / 2, cy = (MinY + MaxY) / 2;
                    double rx = (MaxX - MinX) / 2, ry = (MaxY - MinY) / 2;
                    if (rx <= 0 || ry <= 0) return false;
                    double nx = (x - cx) / rx, ny = (y - cy) / ry;
                    return nx * nx + ny * ny <= 1.0;
                }
            case PetRegionShape.Polygon:
                {
                    List<double>? p = Points;
                    if (p == null || p.Count < 6) return RectContains(x, y);
                    bool inside = false;
                    int n = p.Count / 2;
                    for (int i = 0, j = n - 1; i < n; j = i++)
                    {
                        double xi = p[i * 2], yi = p[i * 2 + 1];
                        double xj = p[j * 2], yj = p[j * 2 + 1];
                        if (((yi > y) != (yj > y)) && (x < (xj - xi) * (y - yi) / (yj - yi) + xi))
                            inside = !inside;
                    }
                    return inside;
                }
            default:
                return RectContains(x, y);
        }
    }

    bool RectContains(double x, double y) =>
        x >= MinX && x <= MaxX && y >= MinY && y <= MaxY;
}

/// <summary>连点档位：Times=点几下触发该档，Message=发给角色的消息文案（角色自行决定如何反应）。</summary>
public class PetRegionTap
{
    public int Times { get; set; } = 1;
    public string Message { get; set; } = "";
}
