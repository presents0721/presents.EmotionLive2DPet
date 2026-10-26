using System;
using System.Drawing;
using System.Text.Json;

namespace presents.EmotionLive2DPet;

public class DragModule : IPetModule, IDisposable
{
    public string JsCode => @"
let isDragging = false;
let dragStart = null;
window.addEventListener('mousedown', function(e) {
    if (e.button !== 0) return;
    if (!e.target.closest('[data-drag]')) return;
    isDragging = true;
    dragStart = {x: e.screenX, y: e.screenY};
    postMessage({type:'drag_start'});
});
// 窗口跟随光标移动时 clientX/movementX 会失效，改用屏幕坐标的累计位移
window.addEventListener('mousemove', function(e) {
    if (isDragging === true && dragStart) {
        postMessage({type:'drag_move',dx:Math.round(e.screenX-dragStart.x),dy:Math.round(e.screenY-dragStart.y)});
    }
});
window.addEventListener('mouseup', function(e) {
    if (isDragging === true && dragStart) {
        postMessage({type:'drag_move',dx:Math.round(e.screenX-dragStart.x),dy:Math.round(e.screenY-dragStart.y)});
    }
    isDragging = false;
    dragStart = null;
});
";

    readonly PetBridge bridge;
    readonly PetWindow window;
    Rectangle startPosition;
    bool hasStart;

    public DragModule(PetBridge bridge, PetWindow window)
    {
        this.bridge = bridge;
        this.window = window;
        bridge.OnMessage += OnBridgeMessage;
    }
    public void Dispose()
    {
        bridge.OnMessage -= OnBridgeMessage;
    }

    void OnBridgeMessage(string type, JsonElement data)
    {
        switch (type)
        {
            case "drag_start":
                startPosition = window.Bounds;
                hasStart = true;
                break;
            case "drag_move":
                if (hasStart)
                {
                    Rectangle b = startPosition;
                    b.X += data.GetProperty("dx").GetInt32();
                    b.Y += data.GetProperty("dy").GetInt32();
                    _ = window.SetBoundsAsync(b);
                }
                break;
            case "pet_window_drag":
                // 永久区拖动：带动整个桌宠窗口移动（增量位移）
                Rectangle moved = window.Bounds;
                moved.X += data.GetProperty("dx").GetInt32();
                moved.Y += data.GetProperty("dy").GetInt32();
                _ = window.SetBoundsAsync(moved);
                break;
        }
    }
}
