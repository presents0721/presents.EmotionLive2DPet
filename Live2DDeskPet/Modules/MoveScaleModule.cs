using System;
using System.Text.Json;
using Alife.Framework;

namespace presents.EmotionLive2DPet;

/// <summary>
/// 移动/缩放模块：在右下角提供一个圆形控制按钮，悬停时展开二级菜单，
/// 菜单包含 移动 / 缩放 / 高度 三个圆形按钮。
/// 移动按钮拖拽移动桌宠、缩放按钮拖拽缩放桌宠；双击对应按钮只重置位移或只重置缩放；
/// 高度按钮打开模型高度倍数面板（可点 ×、点面板外或按 Esc 关闭）。
/// 用户设置的缩放/偏移通过 <see cref="StorageSystem"/> 持久化，
/// 桌宠就绪时恢复（参考鼠标穿透模块的存储方式）。
/// </summary>
/// <summary>用户设置的模型缩放与偏移（持久化）。</summary>
public record MoveScaleState
{
    public double Scale { get; set; } = 1;
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }

    /// <summary>模型高度相对桌宠窗口高度的倍数（默认 1.1，即比窗口高一点）。</summary>
    public double HeightRatio { get; set; } = 1.1;
}

public class MoveScaleModule : IPetModule, IDisposable
{
    public string CssCode => @"
#pet-controls {
    position:fixed; right:15px; bottom:52px;
    display:flex; flex-direction:row; align-items:center; gap:6px;
    z-index:2100;
}
#pet-btn {
    width:28px; height:28px;
    background:rgba(0,0,0,0.4);
    backdrop-filter:blur(10px); border-radius:50%;
    display:flex; justify-content:center; align-items:center;
    color:white; cursor:pointer;
    box-shadow:0 4px 10px rgba(0,0,0,0.2);
    border:1px solid rgba(255,255,255,0.15);
    opacity:0; transition:opacity 0.3s, background 0.2s, transform 0.1s;
}
#pet-btn.near { opacity:1; }
#pet-btn:hover { background:rgba(0,0,0,0.6); }
#pet-btn:active { transform:scale(0.9); }

#pet-menu {
    display:flex; flex-direction:row; align-items:center; gap:6px;
    opacity:0; pointer-events:none; transition:opacity 0.2s;
}
#pet-controls:hover #pet-menu, body.pet-dragging #pet-menu, body.pet-height-open #pet-menu, body.pet-menu-open #pet-menu {
    opacity:1; pointer-events:auto;
}
.pet-item {
    width:28px; height:28px;
    background:rgba(0,0,0,0.4);
    backdrop-filter:blur(10px); border-radius:50%;
    display:flex; justify-content:center; align-items:center;
    color:white; cursor:pointer;
    box-shadow:0 4px 10px rgba(0,0,0,0.2);
    border:1px solid rgba(255,255,255,0.15);
    transition:background 0.2s, transform 0.1s;
}
.pet-item:hover { background:rgba(0,0,0,0.6); }
.pet-item:active { transform:scale(0.9); }
#pet-move-btn { cursor:move; }
#pet-scale-btn { cursor:nwse-resize; }

#pet-height-panel {
    position:fixed; right:88px; bottom:88px; width:210px;
    background:rgba(0,0,0,0.68);
    backdrop-filter:blur(8px); color:white;
    border-radius:8px; padding:10px 12px; font-size:12px;
    border:1px solid rgba(255,255,255,0.15);
    box-shadow:0 4px 12px rgba(0,0,0,0.3);
    opacity:0; pointer-events:none; transition:opacity 0.2s; z-index:2200;
}
#pet-height-panel.show { opacity:1; pointer-events:auto; }
#pet-height-head { display:flex; justify-content:space-between; align-items:center; margin-bottom:6px; gap:8px; }
#pet-height-title { flex:1; }
#pet-height-right { display:flex; align-items:center; gap:6px; }
#pet-height-close {
    background:transparent; border:0; color:rgba(255,255,255,0.72);
    font-size:15px; line-height:1; cursor:pointer; padding:0 2px;
}
#pet-height-close:hover { color:#fff; }
#pet-height-panel input[type=range] { width:100%; accent-color:#7ec8ff; }
#pet-height-foot { display:flex; justify-content:space-between; align-items:center; margin-top:6px; color:rgba(255,255,255,0.6); }
#pet-height-reset {
    background:rgba(255,255,255,0.12); color:white; border:0; border-radius:4px;
    padding:2px 6px; cursor:pointer; font-size:11px;
}
#pet-height-reset:hover { background:rgba(255,255,255,0.24); }
";
    public string HtmlCode => @"
<div id='pet-controls'>
    <div id='pet-menu'>
        <div id='pet-height-btn' class='pet-item' title='调整模型高度倍数'>
            <svg viewBox='0 0 24 24' width='14' height='14' fill='currentColor'>
                <path d='M12 2l3 3h-2v5h-2V5H9l3-3zm0 20l-3-3h2v-5h2v5h2l-3 3zM4 12h16v-1H4v1z'/>
            </svg>
        </div>
        <div id='pet-scale-btn' class='pet-item' title='拖动缩放，双击重置缩放'>
            <svg viewBox='0 0 24 24' width='14' height='14' fill='currentColor'>
                <path d='M7 14H5v5h5v-2H7v-3zm-2-4h2V7h3V5H5v5zm12 7h-3v2h5v-5h-2v3zM14 5v2h3v3h2V5h-5z'/>
            </svg>
        </div>
        <div id='pet-move-btn' class='pet-item' title='拖动移动，双击重置位移'>
            <svg viewBox='0 0 24 24' width='14' height='14' fill='currentColor'>
                <path d='M13 6.83V2h-2v4.83L8.66 4.5 7.25 5.91 12 10.66l4.75-4.75-1.41-1.41L13 6.83zM2 11h4.83L4.5 8.66l1.41-1.41L10.66 12l-4.75 4.75-1.41-1.41L6.83 13H2v-2zm9 9.17V22h2v-4.83l2.34 2.34 1.41-1.41L12 13.34l-4.75 4.75 1.41 1.41L11 17.17zM14 11h4.83l-2.34-2.34 1.41-1.41L22 12l-4.1 4.1-1.41-1.41L18.83 13H14v-2z'/>
            </svg>
        </div>
    </div>
    <div id='pet-btn' title='移动 / 缩放'>
        <svg viewBox='0 0 24 24' width='16' height='16' fill='currentColor'>
            <path d='M12 1l-3 3h2v4h2V4h2l-3-3zM3 12l3 3v-2h4v-2H6V9l-3 3zM12 23l3-3h-2v-4h-2v4H9l3 3zM21 12l-3-3v2h-4v2h4v2l3-3z'/>
        </svg>
    </div>
</div>
<div id='pet-height-panel'>
    <div id='pet-height-head'>
        <span id='pet-height-title'>模型高度（相对窗口高度）</span>
        <span id='pet-height-right'>
            <span id='pet-height-value'>110%</span>
            <button id='pet-height-close' title='关闭'>&#10005;</button>
        </span>
    </div>
    <input id='pet-height-range' type='range' min='30' max='250' step='1' value='110'>
    <div id='pet-height-foot'>
        <span>调小＝模型变小，窗口不动</span>
        <button id='pet-height-reset'>重置 110%</button>
    </div>
</div>
";
    public string JsCode => @"
(function() {
    // 菜单收起留 450ms 缓冲：按钮之间有间隙，鼠标横移时哪怕离开一帧也不会立刻把菜单收掉
    var controls = document.getElementById('pet-controls');
    var menuTimer = 0;
    function openMenu() {
        if (menuTimer) { clearTimeout(menuTimer); menuTimer = 0; }
        document.body.classList.add('pet-menu-open');
    }
    function scheduleCloseMenu() {
        if (menuTimer) clearTimeout(menuTimer);
        menuTimer = setTimeout(function() { menuTimer = 0; document.body.classList.remove('pet-menu-open'); }, 450);
    }
    if (controls) {
        controls.addEventListener('mouseenter', openMenu);
        controls.addEventListener('mouseleave', scheduleCloseMenu);
    }

    function sendEnd(type) {
        postMessage({type:type, scale:userScale, offsetX:userOffsetX, offsetY:userOffsetY});
    }

    var moveBtn = document.getElementById('pet-move-btn');
    if (moveBtn) {
        var moved = false;
        moveBtn.addEventListener('pointerdown', function(e) {
            moved = false;
            moveBtn.setPointerCapture(e.pointerId);
            document.body.classList.add('pet-dragging');
        });
        moveBtn.addEventListener('pointermove', function(e) {
            if (moveBtn.hasPointerCapture(e.pointerId)) {
                moved = true;
                userOffsetX += e.movementX;
                userOffsetY += e.movementY;
                applyTransform();
            }
        });
        moveBtn.addEventListener('pointerup', function(e) {
            document.body.classList.remove('pet-dragging');
            moveBtn.releasePointerCapture(e.pointerId);
            if (moved) sendEnd('pet_move_end');
        });
        moveBtn.addEventListener('dblclick', function() {
            userOffsetX = 0; userOffsetY = 0; applyTransform();
            postMessage({type:'pet_move_reset'});
        });
    }

    var scaleBtn = document.getElementById('pet-scale-btn');
    if (scaleBtn) {
        var moved = false;
        scaleBtn.addEventListener('pointerdown', function(e) {
            moved = false;
            scaleBtn.setPointerCapture(e.pointerId);
            document.body.classList.add('pet-dragging');
        });
        scaleBtn.addEventListener('pointermove', function(e) {
            if (scaleBtn.hasPointerCapture(e.pointerId)) {
                moved = true;
                userScale = Math.max(0.1, userScale * (1 + e.movementX / 200));
                applyTransform();
            }
        });
        scaleBtn.addEventListener('pointerup', function(e) {
            document.body.classList.remove('pet-dragging');
            scaleBtn.releasePointerCapture(e.pointerId);
            if (moved) sendEnd('pet_scale_end');
        });
        scaleBtn.addEventListener('dblclick', function() {
            userScale = 1; applyTransform();
            postMessage({type:'pet_scale_reset'});
        });
    }

    messageBus.on('pet_state', function(msg) {
        userScale = msg.scale;
        userOffsetX = msg.offsetX;
        userOffsetY = msg.offsetY;
        if (msg.heightRatio) applyHeightRatio(msg.heightRatio * 100, false);
        applyTransform();
    });

    // 模型高度倍数：滑块实时改，松手才写盘
    var heightPanel = document.getElementById('pet-height-panel');
    var heightBtn = document.getElementById('pet-height-btn');
    var heightClose = document.getElementById('pet-height-close');
    var heightRange = document.getElementById('pet-height-range');
    var heightValue = document.getElementById('pet-height-value');
    var heightReset = document.getElementById('pet-height-reset');

    function setHeightPanel(open) {
        if (!heightPanel) return;
        heightPanel.classList.toggle('show', open);
        // 面板开着时菜单不收起，否则移过去调滑块时菜单（含高度按钮）会自己消失
        document.body.classList.toggle('pet-height-open', open);
    }

    if (heightBtn && heightPanel) {
        heightBtn.addEventListener('click', function() {
            setHeightPanel(!heightPanel.classList.contains('show'));
        });
    }
    if (heightClose) {
        heightClose.addEventListener('click', function() { setHeightPanel(false); });
    }
    // 点面板外面或按 Esc 也能关闭，避免只能靠按钮关闭
    document.addEventListener('pointerdown', function(e) {
        if (!heightPanel || !heightPanel.classList.contains('show')) return;
        if (heightPanel.contains(e.target) || (heightBtn && heightBtn.contains(e.target))) return;
        setHeightPanel(false);
    }, true);
    document.addEventListener('keydown', function(e) {
        if (e.key === 'Escape') setHeightPanel(false);
    });
    if (heightRange) {
        heightRange.addEventListener('input', function() { applyHeightRatio(parseFloat(heightRange.value), false); });
        heightRange.addEventListener('change', function() { applyHeightRatio(parseFloat(heightRange.value), true); });
    }
    if (heightReset) {
        heightReset.addEventListener('click', function() { applyHeightRatio(110, true); });
    }

    postMessage({type:'pet_ready'});
})();
";

    readonly PetBridge bridge;
    readonly StorageSystem storage;
    readonly string stateKey;
    readonly object persistLock = new();
    MoveScaleState state;

    public MoveScaleModule(PetBridge bridge, StorageSystem storage, PetStorageKey storageKey)
    {
        this.bridge = bridge;
        this.storage = storage;
        stateKey = $"{storageKey.Value}/Live2DDeskPet/MoveScale";
        state = storage.GetObject<MoveScaleState>(stateKey) ?? new MoveScaleState();
        bridge.OnMessage += OnBridgeMessage;
    }
    public void Dispose()
    {
        bridge.OnMessage -= OnBridgeMessage;
    }

    void Persist()
    {
        lock (persistLock)
        {
            storage.SetObject(stateKey, state);
        }
    }

    void OnBridgeMessage(string type, JsonElement data)
    {
        switch (type)
        {
            case "pet_ready":
                bridge.SendMessage("pet_state", new
                {
                    scale = state.Scale,
                    offsetX = state.OffsetX,
                    offsetY = state.OffsetY,
                    heightRatio = state.HeightRatio,
                });
                break;
            case "pet_move_end":
            case "pet_scale_end":
                state.Scale = data.GetProperty("scale").GetDouble();
                state.OffsetX = data.GetProperty("offsetX").GetDouble();
                state.OffsetY = data.GetProperty("offsetY").GetDouble();
                Persist();
                break;
            case "pet_height_ratio":
                state.HeightRatio = data.GetProperty("ratio").GetDouble();
                Persist();
                break;
            case "pet_move_reset":
                state.OffsetX = 0;
                state.OffsetY = 0;
                Persist();
                break;
            case "pet_scale_reset":
                state.Scale = 1;
                Persist();
                break;
        }
    }
}
