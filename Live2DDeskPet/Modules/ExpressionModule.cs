using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alife.Foundation;

namespace presents.EmotionLive2DPet;

public class ExpressionModule : IPetModule, IDisposable
{
    public string JsCode => @"
// 表情管理器默认会让旧表情淡出 1 秒，新旧两张脸会在这 1 秒里叠加（圈圈眼叠爱心眼之类）。
// 切表情前先把旧表情队列整体停掉，保证同一时刻只有一张脸。
function __petStopExpressions() {
    try {
        const manager = model && model.internalModel && model.internalModel.motionManager
            ? model.internalModel.motionManager.expressionManager : null;
        if (manager && typeof manager.stopAllExpressions === 'function') manager.stopAllExpressions();
    } catch (e) {}
}
messageBus.on('expression', (msg) => { __petStopExpressions(); model.expression(msg.id); });

// 动作同样是一段一段的：演完/保持时间到要能回默认姿势，否则举手、换道具这类 Loop=true 的
// motion3 会一直被引擎循环，看起来「调用一次就换不回去」。
window.__petMotionSeq = 0;
function __petStopMotions() {
    try {
        const manager = model && model.internalModel ? model.internalModel.motionManager : null;
        if (manager && typeof manager.stopAllMotions === 'function') manager.stopAllMotions();
    } catch (e) {}
}
// 动作参数不会自己回默认：停掉动作后要把它们写回默认值，否则手/道具会停在最后一帧
let __petActiveMotionParams = [];
function __petRememberMotionParams(ids) {
    if (!Array.isArray(ids)) return;
    for (const id of ids) {
        if (id && __petActiveMotionParams.includes(id) === false) __petActiveMotionParams.push(id);
    }
}
function __petResetMotionParams() {
    const ids = __petActiveMotionParams;
    __petActiveMotionParams = [];
    if (!ids.length) return;
    try {
        const core = model && model.internalModel ? model.internalModel.coreModel : null;
        if (!core) return;
        for (const id of ids) {
            let index = -1;
            try { index = core.getParameterIndex(id); } catch (e) { index = -1; }
            if (!(index >= 0)) continue;
            let def = 0;
            try {
                if (typeof core.getParameterDefaultValue === 'function') {
                    const value = Number(core.getParameterDefaultValue(index));
                    if (Number.isFinite(value)) def = value;
                }
            } catch (e) {}
            try { core.setParameterValueById(id, def); } catch (e) {}
        }
    } catch (e) {}
}
function __petStopAndResetMotions() {
    __petStopMotions();
    __petResetMotionParams();
}
messageBus.on('motion', (msg) => {
    const seq = ++window.__petMotionSeq;
    __petRememberMotionParams(msg.params);
    console.warn('[Embodiment] 原生动作', msg.group, msg.index, 'autoStop=', msg.autoStopSeconds);
    model.motion(msg.group, msg.index, PIXI.live2d.MotionPriority.FORCE);
    const after = Number(msg.autoStopSeconds);
    const resetAfter = Number(msg.resetAfterSeconds) || after;
    if (after > 0) {
        // 先停住动作但保留最后一帧参数（举手/竖指才会保持住），到 resetAfter 再复位回默认
        setTimeout(function() {
            if (window.__petMotionSeq !== seq) return;   // 已经换了新动作，不要停掉新的
            __petStopMotions();
            if (resetAfter <= after) {
                console.warn('[Embodiment] 动作演完，回到默认姿势');
                __petResetMotionParams();
            } else {
                console.warn('[Embodiment] 动作保持中，', resetAfter, '秒后回默认');
            }
        }, after * 1000);
        if (resetAfter > after) {
            setTimeout(function() {
                if (window.__petMotionSeq !== seq) return;
                __petResetMotionParams();
            }, resetAfter * 1000);
        }
    }
});
messageBus.on('motion_stop', () => { window.__petMotionSeq++; __petStopAndResetMotions(); });
messageBus.on('motion_reset', () => { window.__petMotionSeq++; __petStopAndResetMotions(); });
// 收回表情：不能去播「列表第一个」（很多模型第一个是前倾/隐藏部件之类的开关表情），
// 正确做法是把当前表情重置回空的默认表情，参数交还给具身表演层。
messageBus.on('expression_reset', () => {
    __petStopExpressions();
    const manager = model.internalModel && model.internalModel.motionManager
        ? model.internalModel.motionManager.expressionManager : null;
    if (manager && typeof manager.resetExpression === 'function') manager.resetExpression();
});
";

    /// <summary>没指定秒数时，表情保持多久后自动收回（重置为空表情，参数交还具身层）。想改就改这里。</summary>
    const double DefaultExpressionSeconds = 5;

    /// <summary>
    /// 播放表情。seconds 由她决定：
    /// 不填=默认 <see cref="DefaultExpressionSeconds"/> 秒后收回；&gt;0=保持这么多秒后收回；&lt;=0=一直保持不收回。
    /// </summary>
    public void PlayExpression(string? id, double? seconds = null)
    {
        // 显式播放表情会接管当前表情，之前面板固定的状态不再由本模块维持
        externalHold = false;
        // hold=true 让具身层长期避让，避免表情保持到一半被情绪参数顶回去
        bool hold = seconds is double s && s <= 0;
        bridge.SendMessage("expression", new { id, hold });

        if (hold)
        {
            // 一直保持还要登记成「固定表情」：否则之后任何一次自动情绪都会换个原生表情把它顶掉
            bridge.SendMessage("expression_hold", new { id });
            heldExpression = id;            // 一直保持
            lastPlayExpressionTime = null;
            return;
        }

        heldExpression = null;
        expressionHoldSeconds = seconds ?? DefaultExpressionSeconds;
        lastPlayExpressionTime = DateTime.Now;
    }

    /// <summary>
    /// 播放动作。seconds 由她决定：
    /// 不填=演一遍后自动回到默认（自循环的过渡动作也会被停下）；&gt;0=保持这么多秒后停下；
    /// &lt;=0=一直保持（自循环动作由引擎一直循环，一次性动作按时长重播）。
    /// </summary>
    public void PlayMotion(string group, int index, double? seconds = null)
    {
        StopMotionHold();

        string? name = metadata.Motions.FirstOrDefault(kv => kv.Value.Group == group && kv.Value.Index == index).Key;
        (bool Loop, double Duration) meta = (false, 0);
        bool hasMeta = name != null && metadata.MotionMeta.TryGetValue(name, out meta);
        // 动作停掉后引擎不会自动把这些参数复位；不带回列表，手/道具就会停在最后一帧
        MotionFileItem? motionFile = metadata.MotionFiles.FirstOrDefault(file =>
            file.Group == group && file.Index == index);
        string[] motionParams = motionFile?.Params.ToArray() ?? [];

        // 自循环动作不能一直播：循环会把「举起→放下→又举起」演成反复抬手。
        // 先播一遍（到自身时长前）就停住动作、把最后一帧保持住；到 seconds 再复位参数。
        double autoStop = 0;
        double resetAfter = 0;
        if (hasMeta && meta.Duration > 0.5)
        {
            double playOnce = Math.Max(0.4, meta.Duration - 0.1);
            if (meta.Loop)
            {
                if (seconds is null)
                {
                    autoStop = playOnce;
                    resetAfter = playOnce;
                }
                else if (seconds.Value > 0)
                {
                    autoStop = playOnce;
                    resetAfter = Math.Min(seconds.Value, 30);
                }
                // seconds<=0：一直循环，不停
            }
            else if (seconds is null)
            {
                autoStop = Math.Min(meta.Duration + 0.3, 20);
                resetAfter = autoStop;
            }
        }
        bridge.SendMessage("motion", new {
            group, index, autoStopSeconds = autoStop, resetAfterSeconds = resetAfter, @params = motionParams
        });
        if (seconds is null) return;
        if (name == null || hasMeta == false) return;

        double keep = seconds.Value;
        if (meta.Loop) return;          // 循环动作交给 autoStop：>0 到点停，<=0 一直循环
        double period = meta.Duration > 1 ? meta.Duration - 0.2 : 4;

        motionHoldCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationTokenSource!.Token);
        _ = MotionHoldLoop(group, index, period, keep, motionHoldCts.Token, motionParams);
    }

    readonly PetBridge bridge;
    readonly PetModelMetadata metadata;
    CancellationTokenSource? cancellationTokenSource;
    CancellationTokenSource? motionHoldCts;
    /// <summary>面板固定了表情：暂停自动回收，避免之前一次性表情的 5 秒计时把新固定的表情收掉。</summary>
    bool externalHold;

    /// <summary>外观面板固定/切换表情时调用，让本模块停止自动回收。</summary>
    public void HoldExternally(string id)
    {
        externalHold = true;
        heldExpression = id;
        lastPlayExpressionTime = null;
    }

    /// <summary>面板取消固定时调用；只清理由面板建立的外部保持，不影响 AI 自己的一次性表情计时。</summary>
    public void ReleaseExternalHold()
    {
        if (externalHold == false) return;
        externalHold = false;
        heldExpression = null;
        lastPlayExpressionTime = null;
    }
    DateTime? lastPlayExpressionTime;
    double expressionHoldSeconds = DefaultExpressionSeconds;
    string? heldExpression;

    public ExpressionModule(PetBridge bridge, PetModelMetadata metadata)
    {
        this.bridge = bridge;
        this.metadata = metadata;
        cancellationTokenSource = new CancellationTokenSource();

        // 换模型后表情列表会变，回收循环必须一直跑着（之前按初始列表判断，换到有表情的模型也不回收）
        AutoRevertLoop(cancellationTokenSource.Token);
    }
    public void Dispose()
    {
        cancellationTokenSource?.Cancel();
        cancellationTokenSource?.Dispose();
        motionHoldCts?.Cancel();
        motionHoldCts?.Dispose();
    }

    void StopMotionHold()
    {
        try { motionHoldCts?.Cancel(); motionHoldCts?.Dispose(); } catch { }
        motionHoldCts = null;
    }

    /// <summary>面板点「回到默认姿势」时调用：停掉 AI 用 seconds&lt;=0 设置的无限重播。</summary>
    public void CancelMotionHold() => StopMotionHold();

    /// <summary>保持动作：按动作自身时长反复重发，直到到点或被她换掉。</summary>
    async Task MotionHoldLoop(string group, int index, double periodSeconds, double keepSeconds,
        CancellationToken token, string[] motionParams)
    {
        try
        {
            double elapsed = 0;
            while (token.IsCancellationRequested == false)
            {
                await Task.Delay(TimeSpan.FromSeconds(periodSeconds), token);
                elapsed += periodSeconds;
                if (keepSeconds > 0 && elapsed >= keepSeconds)
                {
                    bridge.SendMessage("motion_stop");       // 保持时间到：停下动作，回到默认姿势
                    return;
                }
                bridge.SendMessage("motion", new {
                    group, index, autoStopSeconds = 0, resetAfterSeconds = 0, @params = motionParams
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { AlifeLog.LogError(e); }
    }

    async void AutoRevertLoop(CancellationToken cancellationToken)
    {
        while (cancellationToken.IsCancellationRequested == false)
        {
            try
            {
                await Task.Delay(500, cancellationToken);
                if (heldExpression != null) continue;      // 一直保持的表情：不收回
                if (lastPlayExpressionTime == null) continue;

                if (DateTime.Now - lastPlayExpressionTime.Value > TimeSpan.FromSeconds(expressionHoldSeconds))
                {
                    bridge.SendMessage("expression_reset");
                    lastPlayExpressionTime = null;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e)
            {
                // 单次异常不能让循环退出（之前空列表 First() 抛异常，循环直接死掉，表情就再也不恢复了）
                AlifeLog.LogError(e);
                await Task.Delay(1000);
            }
        }
    }
}
