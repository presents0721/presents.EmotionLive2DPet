/**
 * 具身表演层：把 C# 下发的情绪事件 / VAD / 语音包络 / 原生动作接到 Soullink 引擎，
 * 再把引擎每帧产出的 Live2D 参数写进模型，实现连续情绪、FACS 表情、身体小动作与口型。
 *
 * 依赖：
 *  - window.Soullink（lib/soullink/soullink.js）
 *  - window.SoullinkProfile.buildProfile（profile.js）
 *  - pet.js 提供的 messageBus / model / window.isOverModelAt
 */
(function () {
    'use strict';

    const S = window.Soullink;
    if (!S) {
        console.error('[Embodiment] Soullink 引擎未加载，具身表演不可用');
        return;
    }

    const MotionPriority = (PIXI.live2d && PIXI.live2d.MotionPriority) || {
        NONE: 0, IDLE: 1, NORMAL: 2, FORCE: 3,
    };

    const clamp = (value, min, max) => Math.min(max, Math.max(min, value));

    // ---- 语音包络分析器：优先真实包络，其次说话状态噪声 ----
    const envelopeAnalyzer = {
        levels: null,
        windowMs: 60,
        startMs: 0,
        active: false,
        level: 0,

        getLevel() {
            if (!this.active) return 0;

            if (this.levels && this.levels.length > 0) {
                const index = Math.floor((Date.now() - this.startMs) / this.windowMs);
                const i = clamp(index, 0, this.levels.length - 1);
                const current = this.levels[i] ?? 0;
                const prev = this.levels[Math.max(0, i - 1)] ?? current;
                const next = this.levels[Math.min(this.levels.length - 1, i + 1)] ?? current;
                this.level = Math.max(current, (prev + next) * 0.5);
                return clamp(this.level, 0, 1);
            }

            // 没有真实包络时用平滑噪声模拟说话时的开口幅度
            const t = performance.now() / 1000;
            const noise = 0.24
                + 0.16 * Math.sin(t * 15.7) * Math.sin(t * 5.3)
                + Math.random() * 0.10;
            this.level = clamp(noise, 0.05, 0.85);
            return this.level;
        },

        getPeak() {
            return clamp(this.level * 1.35, 0, 1);
        },

        isAvailable() {
            return this.active;
        },

        reset() {
            this.level = 0;
        },
    };

    let runtime = null;
    let runtimeUnavailable = false;
    let settings = null;
    let classifier = null;

    let hookedModel = null;
    let lastUpdateTime = null;

    let idleEnabled = true;
    let gazeEnabled = true;
    let lipSyncEnabled = true;
    let parameterGain = 1;
    let bodyMotionGain = 1;
    // 真实语音是否仍在播放：字幕先隐藏时不能因此丢掉语音包络
    let voicePlayback = false;

    const gaze = {x: 0, y: 0};
    const pointer = {x: 0, y: 0, inside: false, width: 0, height: 0};

    let lastNativeToken = -1;
    let lastReport = {emotion: null, intensity: 0, at: 0};
    let lastFacsDiagAt = 0;
    const paramIndexCache = new Map();

    // 原生动作/表情播放期间，暂停写入它们驱动的参数，避免情绪层逐帧覆盖原生动画
    let motionSuppressParamIds = new Set();
    let motionSuppressUntil = 0;
    let expressionSuppressParamIds = new Set();
    let expressionSuppressUntil = 0;

    // 具身表演总开关：关闭后情绪/FACS/视线/原生动作全部停手，只保留用户手动固定的表情、动作与服饰
    let autoPerformance = true;
    // 关闭瞬间先让情绪淡回中性再停手，免得最后那一下皱眉/张嘴僵在脸上
    let autoReleaseUntil = 0;

    // 原生表情（哭哭/脸红/星星眼…）播放后要按时收回，否则会一直挂在脸上不再变化
    const NATIVE_EXPRESSION_SECONDS = 6;
    let nativeExpressionUntil = 0;
    // 用户在外观面板里固定的表情：自动情绪不许拿别的表情把它顶掉
    let pinnedExpressionName = null;
    // 上一次自动触发的原生表情。情绪稳定时不能每来一条消息就重播同一张脸，
    // 只有情绪换挡、强度明显上升、或者过了较长冷却才允许再演一次。
    let lastNativeTrigger = { name: null, emotion: null, intensity: 0, at: -999 };
    const NATIVE_SWITCH_COOLDOWN = 1.5;      // 情绪换挡时的防闪烁间隔
    const NATIVE_REPEAT_COOLDOWN = 4.5;      // 同一情绪、强度明显上升时的最短间隔
    const NATIVE_STABLE_COOLDOWN = 25;       // 情绪没变化、强度也没升时的间隔（只做低频点缀）
    const NATIVE_INTENSITY_STEP = 0.25;      // 认为「强度明显上升」的幅度

    // 引擎分类器偏事件型语句，这里补一层常用口语/寒暄的兜底，避免 AI 的温和回复全都落到 neutral
    const SUPPLEMENT_RULES = [
        [/(太好了|太棒|好棒|开心|高兴|幸福|喜欢|爱你|可爱|谢谢|感谢|哈哈|嘿嘿|真好|么么|贴贴|抱抱)/, 'happy', 0.8],
        [/(难过|伤心|抱歉|对不起|遗憾|可惜|失落|委屈|呜呜)/, 'sad', 0.7],
        [/(生气|讨厌|气死|烦死|离谱|过分|哼)/, 'anger', 0.65],
        [/(惊讶|竟然|居然|天哪|震惊|没想到|哇)/, 'surprised', 0.75],
        [/(困惑|疑惑|奇怪|不明白|搞不懂|怎么回事)/, 'confused', 0.6],
        [/(害羞|脸红|不好意思|羞|别夸)/, 'shy', 0.7],
        [/(晚安|休息|放松|睡|累|困)/, 'tired', 0.55],
        [/(兴奋|激动|加油|期待|冲呀|冲鸭)/, 'excited', 0.8],
        [/(担心|小心|注意安全|别怕|没事|慢慢来)/, 'concerned', 0.6],
    ];

    function supplementIntent(intent, text) {
        if (intent.emotion && intent.emotion !== 'neutral') return intent;
        for (const [pattern, emotion, intensity] of SUPPLEMENT_RULES) {
            if (!pattern.test(text)) continue;
            return {
                emotion,
                intensity,
                contextTags: intent.contextTags || ['supplement'],
                sourceMessage: text,
            };
        }
        return intent;
    }

    // ---- 运行时构建 ----

    function applySettings() {
        if (!settings) return;
        const config = settings.config || {};

        idleEnabled = config.idleEnabled !== false;
        gazeEnabled = config.gazeFollow !== false;
        lipSyncEnabled = config.lipSyncEnabled !== false;
        parameterGain = clamp(Number(config.expressionIntensity) || 1, 0.2, 2);
        bodyMotionGain = clamp(Number(config.bodyMotionGain) || 1, 0.2, 2);

        if (runtime) {
            runtime.setIdleEnabled(idleEnabled);
            runtime.setLipSyncEnabled(lipSyncEnabled);
            runtime.setParameterGain(parameterGain);
            runtime.setBodyMotionGain(bodyMotionGain);
        }
    }

    // 把通道覆盖率报告交给 C#，供「表情校准」面板显示映射来源与未映射通道
    function publishCalibration(profile) {
        try {
            postMessage({type: 'calibration_report', report: (profile && profile.calibration) || null});
        } catch (e) {}
    }

    // 校准面板打开时如果还没拿到报告（模块晚于首次报告加载），主动补发一次
    messageBus.on('calibration_report_request', function() {
        try {
            if (runtime && runtime.profile) publishCalibration(runtime.profile);
        } catch (e) {}
    });

    function ensureRuntime() {
        if (!settings) return null;

        if (!runtime) {
            let profile;
            try {
                profile = window.SoullinkProfile.buildProfile(settings, S);
            } catch (e) {
                console.error('[Embodiment] 生成模型表演档案失败', e);
                return null;
            }

            // 没有 cdi3/参数表的老模型无法映射表情维度，直接跳过而不是每帧报错
            if (!profile.parameterMap || Object.keys(profile.parameterMap).length === 0) {
                console.warn('[Embodiment] 模型缺少可映射参数，已跳过具身表演');
                runtimeUnavailable = true;
                return null;
            }

            try {
                runtime = new S.SoullinkRuntime({
                    profile,
                    emotionPersonality: {
                        reactivity: 1.0,
                        targetApproachRate: 0.95,
                        decayRate: 0.12,
                        emotionHoldSeconds: 2.8,
                    },
                    motionStyle: Object.assign({}, S.motionStylePresets.natural, {seed: 20261021}),
                    audioLevelAnalyzer: envelopeAnalyzer,
                });
                classifier = new S.MessageReactionClassifier();
                // 用 warn 是为了让这行诊断日志被 pet.js 转发到插件日志里（console.log 不转发）
                console.warn('[Embodiment] 表演档案就绪',
                    'mapped=' + (profile.debug && profile.debug.mappedTargetCount) + '/' + (profile.debug && profile.debug.parameterCount),
                    'calibration=' + ((profile.calibration && (profile.calibration.mappedKeyCount + '/' + profile.calibration.totalKeyCount)) || '-'),
                    'unmappedKeys=', (profile.calibration && profile.calibration.unmappedKeys) || [],
                    'expressions=' + Object.keys(profile.expressionMap || {}).length,
                    'nativeExpressions=' + ((profile.nativeAnimations && profile.nativeAnimations.expressions) || []).length,
                    'expressionMap=', profile.expressionMap || {},
                    'expressionParams=', profile.expressionParams || {},
                    'privateEmotions=', Object.keys(profile.privateEmotionMap || {}),
                    'facsChannels=', Object.keys(profile.parameterMap || {}).join(','),
                    'capabilities=', profile.capabilities);
            } catch (e) {
                console.error('[Embodiment] 创建情绪运行时失败', e);
                runtime = null;
                runtimeUnavailable = true;
                return null;
            }

            publishCalibration(profile);
            applySettings();
        }

        return runtime;
    }

    // ---- 模型挂钩 ----

    function hookModel(target) {
        if (!target || hookedModel === target) return;
        hookedModel = target;
        runtimeUnavailable = false;
        paramIndexCache.clear();
        lastUpdateTime = null;
        lastNativeToken = -1;
        nativeExpressionUntil = 0;
        pinnedExpressionName = null;   // 换模型后 C# 会重新下发固定表情（如果还有效）
        lastNativeTrigger = { name: null, emotion: null, intensity: 0, at: -999 };
        lastAutoMotionAt = -999;
        lastAutoMotionEmotion = null;
        activeMotionParamIds = [];
        expressionSuppressUntil = 0;
        expressionSuppressParamIds = new Set();
        motionSuppressUntil = 0;
        motionSuppressParamIds = new Set();
        featureOverlay = null;
        faceOverlay = null;

        const internal = target.internalModel;
        if (!internal || typeof internal.on !== 'function') {
            console.error('[Embodiment] 模型缺少 internalModel，无法挂载表演层');
            return;
        }

        internal.on('beforeModelUpdate', function () {
            applyFrame(target);
        });

        // 新模型立即给一个中性状态，避免首帧保持模型默认表情
        // 先用 moc3 的真实参数区间补齐参数表（cdi3 常常没有 Min/Max），再建/重建档案
        if (harvestParameterRanges(target) && runtime) rebuildProfile();
        ensureRuntime();
    }

    function getParameterIndex(coreModel, id) {
        if (paramIndexCache.has(id)) return paramIndexCache.get(id);
        let index = -1;
        try {
            index = coreModel.getParameterIndex(id);
        } catch {
            index = -1;
        }
        paramIndexCache.set(id, index);
        return index;
    }

    // 很多第三方 cdi3 不带 Min/Max，档案只能按 -1..1 猜，头身动作会被压得几乎看不见。
    // moc3 本身带着真实区间，这里在挂载模型时补进参数表，再重建档案。
    function harvestParameterRanges(target) {
        if (!settings || !Array.isArray(settings.parameters)) return false;
        const coreModel = target && target.internalModel && target.internalModel.coreModel;
        if (!coreModel || typeof coreModel.getParameterCount !== 'function') return false;

        // pixi-live2d-display 的 coreModel 只有 getParameterIndex(id)，没有 getParameterId(index)，
        // 所以按参数表里的 id 反查下标再取区间；不依赖具体库版本的内部字段。
        let total = 0;
        try { total = Number(coreModel.getParameterCount()) || 0; } catch { total = 0; }
        let changed = 0;
        try {
            for (const parameter of settings.parameters) {
                let index = -1;
                try { index = coreModel.getParameterIndex(parameter.id); } catch { index = -1; }
                if (!(index >= 0) || (total && index >= total)) continue;
                const min = Number(coreModel.getParameterMinimumValue(index));
                const max = Number(coreModel.getParameterMaximumValue(index));
                const def = Number(coreModel.getParameterDefaultValue(index));
                if (!Number.isFinite(min) || !Number.isFinite(max) || !(max > min)) continue;
                if (parameter.min === min && parameter.max === max && parameter.default === def) continue;
                parameter.min = min;
                parameter.max = max;
                if (Number.isFinite(def)) parameter.default = def;
                changed++;
            }
        } catch (e) {
            console.warn('[Embodiment] 读取模型参数区间失败', e);
            return false;
        }
        if (changed) console.warn('[Embodiment] 补全模型参数区间', changed, '个');
        return changed > 0;
    }

    function rebuildProfile() {
        if (!runtime || !settings) return;
        try {
            const rebuilt = window.SoullinkProfile.buildProfile(settings, S);
            runtime.setProfile(rebuilt);
            publishCalibration(rebuilt);
        } catch (e) {
            console.error('[Embodiment] 重建表演档案失败', e);
        }
    }

    function ruleTargets(rule) {
        if (!rule) return [];
        if (rule.target) return [rule.target];
        return rule.targets || [];
    }

    function applyFrame(target) {
        if (target !== hookedModel) return;      // 旧模型卸载后残留的帧回调，直接丢弃
        if (runtimeUnavailable) return;

        const now = performance.now() / 1000;
        // 总开关关闭后情绪层停手，但用户/AI 显式触发的手势与面部叠加仍要能演
        const autoOff = !autoPerformance && now >= autoReleaseUntil;
        if (autoOff && !gestureOverlay && !faceOverlay) return;
        if (!runtime) {
            ensureRuntime();
            if (!runtime) return;
        }

        const delta = lastUpdateTime == null ? 1 / 60 : clamp(now - lastUpdateTime, 0.001, 0.1);
        lastUpdateTime = now;

        if (nativeExpressionUntil > 0 && now > nativeExpressionUntil) resetNativeExpression();

        let snapshot;
        if (autoOff) {
            snapshot = {live2dParams: {}, profile: runtime.profile};
        } else {
            updateGaze(delta);
            try {
                snapshot = runtime.update(now, delta);
            } catch (e) {
                console.error('[Embodiment] 情绪引擎更新失败，已停用', e);
                runtime = null;
                return;
            }
        }

        const coreModel = target.internalModel.coreModel;
        if (!coreModel) return;

        const suppressed = new Set(
            (snapshot.nativeAnimation && snapshot.nativeAnimation.suppressParamIds) || []);
        if (now < motionSuppressUntil) {
            for (const id of motionSuppressParamIds) suppressed.add(id);
        }
        if (now < expressionSuppressUntil) {
            for (const id of expressionSuppressParamIds) suppressed.add(id);
        }
        const params = snapshot.live2dParams || {};

        for (const id in params) {
            if (suppressed.has(id)) continue;
            const value = params[id];
            if (!Number.isFinite(value)) continue;
            if (getParameterIndex(coreModel, id) < 0) continue;
            coreModel.setParameterValueById(id, value);
        }

        // 原生表情文件多为单参数开关，这里按情绪把开关参数补足强度（黑脸/哭哭/流泪）
        if (!autoOff) {
            applyFeatureOverlay(coreModel, now);
            applyGaze(coreModel, snapshot.profile);
        }
        // 手势在情绪层之后写：头的倾斜、点头、手臂这类「整段动作」优先于逐帧微动
        applyGestureOverlay(coreModel, now, suppressed);
        // AI 指定的人设表情放最后：显式指定的通道优先于情绪层与视线跟随
        applyFaceOverlay(coreModel, now, suppressed);
        if (!autoOff) {
            playNativeAnimation(target, snapshot.nativeAnimation);
            if (now - lastFacsDiagAt > 5) logFacsDiagnostics(now, coreModel, snapshot);
            reportState(now, snapshot);
        }
    }

    /** 低频打印 FACS 合成值与实际写入参数，用来确认表情层真的在改脸而不是只有口型。 */
    function logFacsDiagnostics(now, coreModel, snapshot) {
        lastFacsDiagAt = now;
        const facs = snapshot.facs || {};
        const facsText = ['mouthSmile', 'mouthFrown', 'eyeSmile', 'browInnerUp', 'browDown', 'eyeOpen', 'blush', 'tear']
            .filter((key) => Number.isFinite(facs[key]))
            .map((key) => `${key}=${Number(facs[key]).toFixed(2)}`)
            .join(' ');
        // 头/身通道的目标参数随模型而异，这里按当前档案解析出来一起打印，
        // 手势或情绪动作有没有真正落地，看这几个数值就知道。
        const map = (snapshot.profile && snapshot.profile.parameterMap) || {};
        const poseIds = ['headX', 'headY', 'bodyX', 'bodyY']
            .flatMap((key) => ruleTargets(map[key]))
            .slice(0, 4);
        const paramIds = ['ParamEyeLSmile', 'ParamMouthForm', 'ParamEyeLOpen', 'ParamBrowLY', 'ParamBrowLForm', 'ParamCheek', 'ParamMouthOpenY', ...poseIds];
        const paramText = paramIds
            .filter((id) => getParameterIndex(coreModel, id) >= 0)
            .map((id) => `${id}=${Number(coreModel.getParameterValueById(id) || 0).toFixed(2)}`)
            .join(' ');
        const emotion = (snapshot.vad && snapshot.vad.dominantEmotion) || 'neutral';
        console.warn('[Embodiment] FACS', `state=${snapshot.state || '-'}`, `emotion=${emotion}`, facsText, '|', paramText);
    }

    function updateGaze(delta) {
        let targetX = 0;
        let targetY = 0;
        if (gazeEnabled && pointer.inside && pointer.width > 0 && pointer.height > 0) {
            targetX = clamp((pointer.x / pointer.width) * 2 - 1, -1, 1);
            targetY = clamp((pointer.y / pointer.height) * 2 - 1, -1, 1);
        }

        const k = 1 - Math.exp(-delta / 0.18);
        gaze.x += (targetX - gaze.x) * k;
        gaze.y += (targetY - gaze.y) * k;
    }

    function applyGaze(coreModel, profile) {
        if (!gazeEnabled || !profile || !profile.parameterMap) return;

        const map = profile.parameterMap;
        const add = (rule, value) => {
            if (!Number.isFinite(value) || Math.abs(value) < 0.0001) return;
            for (const id of ruleTargets(rule)) {
                if (getParameterIndex(coreModel, id) < 0) continue;
                try {
                    coreModel.addParameterValueById(id, value);
                } catch {
                }
            }
        };

        // 眼珠与头部轻微跟随，幅度刻意保守，避免破坏引擎自身的视线表演
        add(map.gazeX, gaze.x * 0.55);
        add(map.gazeY, -gaze.y * 0.45);
        add(map.headX, gaze.x * 6);
        add(map.headY, -gaze.y * 4);
    }

    function playNativeAnimation(target, directive) {
        if (!directive) {
            if (lastNativeToken !== -1) resetNativeExpression();
            lastNativeToken = -1;
            return;
        }
        if (directive.token === lastNativeToken) return;
        lastNativeToken = directive.token;

        if (directive.expression) {
            playNativeExpression(directive.expression);
        }

        if (directive.motion) {
            // 空闲层动作幅度小、时长长，冻结表情会显得僵硬；只对主动触发的动作避让
            if (directive.motion.priority !== 'idle') {
                beginMotionSuppression(directive.motion.group, directive.motion.index);
            }
            const priority = directive.motion.priority === 'force'
                ? MotionPriority.FORCE
                : (directive.motion.priority === 'idle' ? MotionPriority.IDLE : MotionPriority.NORMAL);
            try {
                const result = target.motion(directive.motion.group, directive.motion.index, priority);
                if (result && typeof result.catch === 'function') result.catch(() => {});
            } catch (e) {
                console.warn('[Embodiment] 播放原生动作失败', directive.motion, e);
            }
        }
    }

    function beginExpressionSuppression(name, seconds) {
        const entry = (settings && settings.expressions || [])
            .find((item) => String(item.name) === String(name));
        const params = (entry && entry.params) || [];
        if (params.length === 0) return;

        expressionSuppressParamIds = new Set(params);
        expressionSuppressUntil = performance.now() / 1000 + (Number(seconds) || 2.5);
    }

    /** 把表情队列整段停掉：表情管理器默认让旧表情淡出 1 秒，新旧两张脸会叠在一起。 */
    function stopNativeExpressions() {
        if (!hookedModel) return;
        const internal = hookedModel.internalModel;
        const manager = internal && internal.motionManager ? internal.motionManager.expressionManager : null;
        if (manager && typeof manager.stopAllExpressions === 'function') {
            try { manager.stopAllExpressions(); } catch { }
        }
    }

    /** 播一个原生表情，并登记一个收回时间（否则表情会一直停在脸上）。 */
    function playNativeExpression(name) {
        if (!hookedModel || !name) return;
        // 固定的表情优先：自动情绪/文本推断不能覆盖用户的选择；
        // 同一个固定表情也不用重播，更不能登记 6 秒回收，否则固定表情会被自动收掉。
        if (pinnedExpressionName) return;
        // 原生表情整张接管：清掉按参数名合成的残留，并停掉上一张表情，避免圈圈眼叠爱心眼
        featureOverlay = null;
        stopNativeExpressions();
        beginExpressionSuppression(name, NATIVE_EXPRESSION_SECONDS);
        nativeExpressionUntil = performance.now() / 1000 + NATIVE_EXPRESSION_SECONDS;
        try {
            const result = hookedModel.expression(String(name));
            if (result && typeof result.catch === 'function') result.catch(() => {});
        } catch (e) {
            console.warn('[Embodiment] 播放原生表情失败', name, e);
        }
    }

    /** 情绪键 → 模型自带表情；情绪层常给出 soft-happy/curious 这类细分名，先归一化。 */
    const NATIVE_EMOTION_ALIASES = {
        'soft-happy': 'happy', 'soft-positive': 'happy', 'soft-calm': 'calm',
        'soft-uneasy': 'concerned', 'soft-curious': 'confused', 'soft-low': 'sad',
        'soft-shy': 'shy', 'soft-steady': 'calm',
        curious: 'confused', uneasy: 'concerned', anger: 'angry', anxiety: 'concerned',
        joy: 'happy', joyful: 'happy', sadness: 'sad', fear: 'concerned', tired: 'calm',
        affectionate: 'happy', embarrassed: 'shy', startled: 'surprised', nervous: 'concerned',
    };

    function nativeEmotionKey(emotion) {
        const base = String(emotion || '').toLowerCase();
        return NATIVE_EMOTION_ALIASES[base] || base.replace(/^soft-/, '');
    }

    function nativeExpressionFor(emotion) {
        if (!runtime || !runtime.profile) return null;
        const map = runtime.profile.expressionMap;
        return (map && map[nativeEmotionKey(emotion)]) || null;
    }

    /**
     * 自动情绪触发的原生表情要防重：同一条表情还在脸上、情绪没换、强度也没明显上升时不重播。
     * explicit=true 用于 AI/用户点名的表情，永远立即播。
     */
    function maybePlayNativeExpression(name, emotion, intensity, now, explicit) {
        if (!name || pinnedExpressionName) return false;
        if (!explicit) {
            const emotionKey = nativeEmotionKey(emotion);
            // 同一个表情还在显示中：让它自然演完，不要一条消息重播一次
            if (nativeExpressionUntil > now && lastNativeTrigger.name === String(name)) return false;
            const sameEmotion = emotionKey === lastNativeTrigger.emotion;
            const risen = intensity >= lastNativeTrigger.intensity + NATIVE_INTENSITY_STEP;
            // 情绪真的换挡了，允许很快换一张脸；同一情绪则按强度是否上升分两档冷却
            const minGap = sameEmotion
                ? (risen ? NATIVE_REPEAT_COOLDOWN : NATIVE_STABLE_COOLDOWN)
                : NATIVE_SWITCH_COOLDOWN;
            if (now - lastNativeTrigger.at < minGap) return false;
        }
        playNativeExpression(name);
        lastNativeTrigger = {
            name: String(name),
            emotion: nativeEmotionKey(emotion),
            intensity: Number(intensity) || 0,
            at: now,
        };
        return true;
    }

    // ---- 情绪特征叠加 ----
    // 模型自带的「黑脸/哭哭/流泪」都是单参数开关（exp3 里只有一个 Param，Blend=Add），
    // 单靠表情文件时有时无、强度也不足，生气和难过看起来都像在笑。
    // 这里按情绪把这类开关参数直接按值写进去，持续整个反应窗口，保证视觉上明显不同。
    // 情绪 → 档案里的私有特征参数键（由 profile.js 从参数名语义自动识别，换模型不用改这里）
    const PRIVATE_FEATURE_KEYS = {
        angry: ['angerEffect', 'shadowEffect'],
        sad: ['tearEffect'],
        confused: ['confusionEffect'],
        surprised: ['surpriseEffect'],
        happy: ['positiveEye', 'starEffect'],
        excited: ['positiveEye', 'starEffect'],
    };
    const FEATURE_ATTACK_SECONDS = 0.12;
    const FEATURE_RELEASE_SECONDS = 0.6;

    let featureOverlay = null;   // {params, startedAt, holdSeconds, strength}

    // 查找顺序：模型自带表情实际控制的参数 → 按参数名识别出的私有特征参数。
    // 两者都拿不到时返回空，此时只靠 FACS 逐帧合成（对没有表情资产的模型同样有效）。
    function featureParamsFor(emotion) {
        if (!runtime || !runtime.profile) return [];
        const profile = runtime.profile;
        const key = nativeEmotionKey(emotion);
        const direct = profile.expressionParams && profile.expressionParams[key];
        if (direct && direct.length) return direct;
        const featureKeys = PRIVATE_FEATURE_KEYS[key] || [];
        const result = [];
        for (const featureKey of featureKeys) {
            const entry = profile.privateEmotionMap && profile.privateEmotionMap[featureKey];
            for (const id of (entry && entry.targets) || []) {
                if (!result.includes(id)) result.push(id);
            }
        }
        return result;
    }

    function startFeatureOverlay(emotion, intensity) {
        const params = featureParamsFor(emotion);
        if (!params.length) return;
        const strength = clamp(Number(intensity) || 0.8, 0, 1);
        featureOverlay = {
            params,
            startedAt: performance.now() / 1000,
            holdSeconds: 2.2 + strength * 1.2,
            strength: 0.85 + strength * 0.15,
        };
        console.warn('[Embodiment] 特征表情叠加', nativeEmotionKey(emotion), '→', params.join(','));
    }

    function applyFeatureOverlay(coreModel, now) {
        if (!featureOverlay) return;
        const elapsed = now - featureOverlay.startedAt;
        if (elapsed < 0) return;
        const hold = featureOverlay.holdSeconds;
        let amount;
        if (elapsed < FEATURE_ATTACK_SECONDS) amount = elapsed / FEATURE_ATTACK_SECONDS;
        else if (elapsed < hold) amount = 1;
        else amount = 1 - (elapsed - hold) / FEATURE_RELEASE_SECONDS;
        if (amount <= 0) {
            featureOverlay = null;
            return;
        }
        const value = clamp(amount, 0, 1) * featureOverlay.strength;
        for (const id of featureOverlay.params) {
            if (getParameterIndex(coreModel, id) < 0) continue;
            coreModel.setParameterValueById(id, value);
        }
    }

    // ---- AI 直接驱动的面部动作单元（FACS）叠加层 ----
    // AI 填一组 AU 值（0~1，视线/头部为 -1~1），这里按档案里的规则写进模型参数，
    // 所以「傲娇」「病娇」这类模型没有的表情，也能用眉+眼+嘴+视线+歪头的组合表演出来。
    const FACE_ATTACK_SECONDS = 0.12;
    const FACE_RELEASE_SECONDS = 0.6;
    let faceOverlay = null;   // {rules, startedAt, holdSeconds}

    function startFaceOverlay(msg, now) {
        if (!runtime || !runtime.profile) return;
        const aus = msg && msg.aus ? msg.aus : {};
        const map = runtime.profile.parameterMap || {};
        const rules = [];
        for (const key of Object.keys(aus)) {
            const rule = map[key];
            const value = Number(aus[key]);
            if (!rule || !Number.isFinite(value)) continue;
            rules.push({key, rule, value});
        }
        if (!rules.length) {
            console.warn('[Embodiment] PetFace 没有可映射的面部参数，已忽略');
            return;
        }

        const seconds = Number(msg.seconds);
        faceOverlay = {
            rules,
            startedAt: now,
            holdSeconds: Number.isFinite(seconds) && seconds > 0 ? seconds : Infinity,
        };
        console.warn('[Embodiment] AI 面部叠加', rules.map((item) =>
            `${item.key}=${item.value}`).join(' '), '保持', faceOverlay.holdSeconds);
    }

    function applyFaceOverlay(coreModel, now, suppressed) {
        if (!faceOverlay) return;
        const elapsed = now - faceOverlay.startedAt;
        if (elapsed < 0) return;

        const hold = faceOverlay.holdSeconds;
        let amount;
        if (elapsed < FACE_ATTACK_SECONDS) amount = elapsed / FACE_ATTACK_SECONDS;
        else if (elapsed < hold) amount = 1;
        else amount = 1 - (elapsed - hold) / FACE_RELEASE_SECONDS;
        if (amount <= 0) {
            faceOverlay = null;
            return;
        }
        amount = clamp(amount, 0, 1);

        const sets = [];
        const adds = [];
        for (const item of faceOverlay.rules) {
            const rule = item.rule;
            const mapped = S.transformRuleValue(item.value * amount, rule);
            if (!Number.isFinite(mapped)) continue;
            const targets = ruleTargets(rule).filter((id) => !suppressed.has(id));
            if (!targets.length) continue;
            if (rule.mode === 'add' || rule.mode === 'subtract') adds.push({targets, mapped, rule});
            else sets.push({targets, mapped, rule});
        }

        // 与引擎一致：先写 set，再叠加 add/subtract，同一目标的后写 set 会覆盖前面的
        for (const item of sets) {
            for (const id of item.targets) {
                if (getParameterIndex(coreModel, id) < 0) continue;
                coreModel.setParameterValueById(id, clampRuleValue(item.mapped, item.rule));
            }
        }
        for (const item of adds) {
            for (const id of item.targets) {
                if (getParameterIndex(coreModel, id) < 0) continue;
                try { coreModel.addParameterValueById(id, item.mapped); } catch { }
            }
        }
    }

    function clampRuleValue(value, rule) {
        const min = rule && Number.isFinite(rule.min) ? rule.min : -1000;
        const max = rule && Number.isFinite(rule.max) ? rule.max : 1000;
        return clamp(value, min, max);
    }

    // ---- 手势库：语义关键帧（头/身/视线/五官 + 可选手臂肩部） ----
    // 关键帧值都在 -1~1（0 = 中性），真正写参数时交给表演档案的 outputRange 校准，
    // 所以同一套手势在不同模型上都会落到该模型自己的参数区间。
    const GESTURES = {
        nod: {
            seconds: 1.2,
            frames: [
                [0, {}],
                [0.22, {headY: 0.6, bodyY: 0.12, eyeSmile: 0.22}],
                [0.46, {headY: -0.16, bodyY: -0.03}],
                [0.7, {headY: 0.12, bodyY: 0.03}],
                [1, {}],
            ],
        },
        shakeHead: {
            seconds: 1.5,
            frames: [
                [0, {}],
                [0.2, {headX: 0.55, gazeX: 0.45, headZ: -0.06}],
                [0.42, {headX: -0.55, gazeX: -0.45, headZ: 0.06}],
                [0.64, {headX: 0.42, gazeX: 0.3}],
                [0.84, {headX: -0.18, gazeX: -0.12}],
                [1, {}],
            ],
        },
        bow: {
            seconds: 2.2,
            frames: [
                [0, {}],
                [0.26, {headY: 0.85, bodyY: 0.35, gazeY: 0.4, eyeOpen: 0.7}],
                [0.62, {headY: 0.8, bodyY: 0.32, gazeY: 0.36, eyeOpen: 0.7}],
                [0.88, {headY: 0.12, bodyY: 0.05}],
                [1, {}],
            ],
        },
        leanIn: {
            seconds: 1.8,
            frames: [
                [0, {}],
                [0.3, {bodyY: 0.5, headY: 0.3, gazeY: -0.25, browInnerUp: 0.22}],
                [0.7, {bodyY: 0.44, headY: 0.26, gazeY: -0.2, browInnerUp: 0.18}],
                [1, {}],
            ],
        },
        leanBack: {
            seconds: 1.6,
            frames: [
                [0, {}],
                [0.28, {bodyY: -0.42, headY: -0.32, eyeOpen: 1.05, browOuterUp: 0.45}],
                [0.62, {bodyY: -0.36, headY: -0.26, eyeOpen: 1, browOuterUp: 0.32}],
                [1, {}],
            ],
        },
        lookAround: {
            seconds: 2,
            frames: [
                [0, {}],
                [0.22, {headX: 0.5, gazeX: 0.7, headZ: -0.05}],
                [0.5, {headX: 0.44, gazeX: 0.6, headZ: -0.04}],
                [0.76, {headX: -0.5, gazeX: -0.7, headZ: 0.05}],
                [1, {}],
            ],
        },
        shrug: {
            seconds: 1.6,
            arms: true,
            frames: [
                [0, {}],
                [0.26, {
                    armLShoulder: 0.55, armRShoulder: 0.55, bodyY: -0.14, headZ: 0.12,
                    browInnerUp: 0.3, mouthFrown: 0.12,
                }],
                [0.66, {armLShoulder: 0.5, armRShoulder: 0.5, bodyY: -0.1, headZ: 0.1}],
                [1, {}],
            ],
        },
        wave: {
            seconds: 1.8,
            arms: true,
            frames: [
                [0, {}],
                [0.16, {armRShoulder: 0.85, headZ: -0.18, eyeSmile: 0.5, mouthSmile: 0.35}],
                [0.32, {armRShoulder: 0.62, headZ: -0.14, eyeSmile: 0.5, mouthSmile: 0.35}],
                [0.48, {armRShoulder: 0.9, headZ: -0.2, eyeSmile: 0.55, mouthSmile: 0.4}],
                [0.64, {armRShoulder: 0.64, headZ: -0.14, eyeSmile: 0.5, mouthSmile: 0.35}],
                [0.8, {armRShoulder: 0.86, headZ: -0.18, eyeSmile: 0.5, mouthSmile: 0.35}],
                [1, {}],
            ],
        },
        handsUp: {
            seconds: 1.9,
            arms: true,
            frames: [
                [0, {}],
                [0.22, {
                    armLShoulder: 0.9, armRShoulder: 0.9, headY: -0.35, browOuterUp: 0.5,
                    eyeSmile: 0.45,
                }],
                [0.58, {armLShoulder: 0.86, armRShoulder: 0.86, headY: -0.3, browOuterUp: 0.4}],
                [0.84, {armLShoulder: 0.2, armRShoulder: 0.2, headY: -0.05}],
                [1, {}],
            ],
        },
        heart: {
            seconds: 2.4,
            arms: true,
            frames: [
                [0, {}],
                [0.3, {
                    armLShoulder: 0.62, armRShoulder: 0.62, headZ: 0.3, headY: 0.08,
                    blush: 0.8, mouthSmile: 0.7, eyeSmile: 0.65,
                }],
                [0.72, {
                    armLShoulder: 0.6, armRShoulder: 0.6, headZ: 0.28, headY: 0.08,
                    blush: 0.75, mouthSmile: 0.65, eyeSmile: 0.6,
                }],
                [1, {}],
            ],
        },
        point: {
            seconds: 1.6,
            arms: true,
            frames: [
                [0, {}],
                [0.2, {armRShoulder: 0.72, headZ: -0.12, browOuterUp: 0.25, gazeX: 0.2}],
                [0.7, {armRShoulder: 0.68, headZ: -0.1, browOuterUp: 0.2, gazeX: 0.18}],
                [1, {}],
            ],
        },
    };

    const GESTURE_ALIASES = {
        '挥手': 'wave', '招手': 'wave', '打招呼': 'wave',
        '点头': 'nod', '摇头': 'shakeHead', '鞠躬': 'bow', '弯腰': 'bow',
        '前倾': 'leanIn', '凑近': 'leanIn', '后仰': 'leanBack', '后退': 'leanBack',
        '张望': 'lookAround', '环顾': 'lookAround', '耸肩': 'shrug',
        '举手': 'handsUp', '举双手': 'handsUp', '欢呼': 'handsUp',
        '比心': 'heart', '爱心': 'heart', '指向': 'point', '指': 'point',
    };

    // 模型没有 ParamArm* 时，合成手臂手势几乎看不出来；有同名/近义的原生动作就优先用原生动作。
    // 只取「打开/出来」类，跳过「关闭/切回默认」，否则会先把姿势收回去。
    const GESTURE_MOTION_KEYWORDS = {
        wave: ['挥手', '招手', '打招呼', '举手'],
        handsUp: ['举手', '双手', '欢呼'],
        point: ['竖指', '手指', '食指', '指向'],
        heart: ['比心', '爱心', '心心'],
        nod: ['点头'],
        shakeHead: ['摇头'],
        bow: ['鞠躬', '弯腰'],
        leanIn: ['前倾', '凑近'],
        leanBack: ['后仰', '后退'],
        lookAround: ['张望', '环顾'],
        shrug: ['耸肩'],
    };

    function nativeMotionForGesture(key) {
        if (!settings || !Array.isArray(settings.motions)) return null;
        const words = GESTURE_MOTION_KEYWORDS[key] || [];
        if (!words.length) return null;
        for (const item of settings.motions) {
            const name = `${item.name || ''} ${item.label || ''}`;
            if (!name.trim()) continue;
            if (/(idle|scene|loop|待机|场景)/i.test(name)) continue;
            if (/(关闭|切回|切默认|回默认|收起|收回|close|reset)/i.test(name)) continue;
            if (words.some((word) => name.includes(word))) return item;
        }
        return null;
    }

    let gestureOverlay = null; // {name, startedAt, duration, frames, intensity}

    /** 关键帧线性插值：缺省的通道按 0（中性）处理。 */
    function sampleFrames(frames, t) {
        if (!frames.length) return {};
        if (t <= frames[0][0]) return frames[0][1];
        for (let i = 1; i < frames.length; i++) {
            const [t1, pose1] = frames[i];
            if (t > t1) continue;
            const [t0, pose0] = frames[i - 1];
            const k = t1 === t0 ? 1 : clamp((t - t0) / (t1 - t0), 0, 1);
            const keys = new Set([...Object.keys(pose0), ...Object.keys(pose1)]);
            const out = {};
            for (const key of keys)
                out[key] = (pose0[key] ?? 0) * (1 - k) + (pose1[key] ?? 0) * k;
            return out;
        }
        return frames[frames.length - 1][1];
    }

    /** 触发一个手势；名称支持英文名与中文别名。返回是否真的触发了（模型没有对应通道时仍会演头/身层）。 */
    function startGesture(name, intensity, seconds) {
        if (!runtime || !runtime.profile) return false;
        const raw = String(name || '').trim();
        const lowered = raw.toLowerCase();
        // 手势表里是 lookAround/shakeHead 这类驼峰名，不能只做 toLowerCase，否则自动编排会报「未知手势」
        const key = GESTURE_ALIASES[raw]
            || Object.keys(GESTURES).find((gestureName) => gestureName.toLowerCase() === lowered)
            || lowered;
        const def = GESTURES[key];
        if (!def) {
            console.warn('[Embodiment] 未知手势:', raw);
            return false;
        }

        // 先找模型自带的手势动作：手臂参数缺失时它比合成关键帧可靠得多
        const native = nativeMotionForGesture(key);
        if (native) {
            console.warn('[Embodiment] 手势', key, '→ 原生动作', native.name || native.label);
            // AI 指定的时长优先；没指定就按动作自身时长演一遍后回默认
            playMotion(native.group, native.index, 'force', Number(seconds) > 0 ? Number(seconds) : 0);
            return true;
        }

        const now = performance.now() / 1000;
        const total = Number(seconds) > 0 ? Number(seconds) : def.seconds;
        gestureOverlay = {
            name: key,
            startedAt: now,
            duration: total,
            frames: def.frames,
            intensity: clamp(Number(intensity) || 0.75, 0.2, 1),
        };
        console.warn('[Embodiment] 手势', key, '强度', gestureOverlay.intensity,
            '时长', total, '手臂可用=', !!(runtime.profile && runtime.profile.armCapable));
        return true;
    }

    /** 把手势关键帧写进参数；手臂通道不存在时自动跳过，只演头/身层。 */
    function applyGestureOverlay(coreModel, now, suppressed) {
        if (!gestureOverlay) return;
        const elapsed = now - gestureOverlay.startedAt;
        if (elapsed < 0) return;
        if (elapsed >= gestureOverlay.duration) {
            gestureOverlay = null;
            return;
        }
        const pose = sampleFrames(gestureOverlay.frames, elapsed / gestureOverlay.duration);
        const map = (runtime && runtime.profile && runtime.profile.parameterMap) || {};

        const sets = [];
        const adds = [];
        for (const key in pose) {
            const rule = map[key];
            if (!rule) continue;      // 模型没有这个通道（例如没有手臂参数）
            const mapped = S.transformRuleValue(pose[key] * gestureOverlay.intensity, rule);
            if (!Number.isFinite(mapped)) continue;
            const targets = ruleTargets(rule).filter((id) => !suppressed.has(id));
            if (!targets.length) continue;
            if (rule.mode === 'add' || rule.mode === 'subtract') adds.push({targets, mapped, rule});
            else sets.push({targets, mapped, rule});
        }

        for (const item of sets) {
            for (const id of item.targets) {
                if (getParameterIndex(coreModel, id) < 0) continue;
                coreModel.setParameterValueById(id, clampRuleValue(item.mapped, item.rule));
            }
        }
        for (const item of adds) {
            for (const id of item.targets) {
                if (getParameterIndex(coreModel, id) < 0) continue;
                try { coreModel.addParameterValueById(id, item.mapped); } catch { }
            }
        }
    }

    /** 把表情重置回空的默认表情，参数重新交给情绪层驱动。 */
    function resetNativeExpression() {
        nativeExpressionUntil = 0;
        expressionSuppressUntil = 0;
        expressionSuppressParamIds = new Set();
        if (!hookedModel) return;
        stopNativeExpressions();
        const internal = hookedModel.internalModel;
        const manager = internal && internal.motionManager ? internal.motionManager.expressionManager : null;
        if (manager && typeof manager.resetExpression === 'function') {
            try { manager.resetExpression(); }
            catch (e) { console.warn('[Embodiment] 收回表情失败', e); }
        }
    }

    function beginMotionSuppression(group, index) {
        const entry = (settings && settings.motions || []).find((item) =>
            String(item.group) === String(group) && Number(item.index) === Number(index));
        const params = (entry && entry.params) || [];
        if (params.length === 0) return;

        const duration = Number(entry.duration) || 2.5;
        motionSuppressParamIds = new Set(params);
        motionSuppressUntil = performance.now() / 1000 + clamp(duration, 0.6, 12);
    }

    function reportState(now, snapshot) {
        if (!snapshot.vad) return;

        const emotion = snapshot.vad.dominantEmotion || 'neutral';
        const intensity = Number(snapshot.vad.intensity) || 0;
        const changed = emotion !== lastReport.emotion || Math.abs(intensity - lastReport.intensity) > 0.05;

        if (!changed || now - lastReport.at < 1) return;
        lastReport = {emotion, intensity, at: now};
        postMessage({type: 'state', emotion, intensity});
    }

    // ---- 自动动作编排：情绪 → 手势 / 原生动作 / 窗口位移 ----
    // 三条通道各有独立冷却，避免每句台词都手舞足蹈；情绪不够强时只做面部。
    const AUTO_GESTURES_BY_EMOTION = {
        happy: ['nod'],
        excited: ['nod', 'handsUp'],
        sad: ['bow'],
        angry: ['shakeHead'],
        surprised: ['leanBack'],
        confused: ['lookAround'],
        concerned: ['leanIn'],
        shy: ['bow', 'leanIn'],
        proud: ['leanBack'],
        relaxed: ['leanIn'],
        bored: ['lookAround'],
        calm: ['leanIn'],
    };
    const AUTO_EFFECTS_BY_EMOTION = {
        excited: ['hop'],
        surprised: ['jump'],
        angry: ['shake'],
        happy: ['hop', 'bounce'],
    };
    let lastAutoGestureAt = -999;
    let lastAutoMotionAt = -999;
    let lastAutoEffectAt = -999;
    // 原生动作比合成手势「重」，情绪没换挡时更不该反复触发
    let lastAutoMotionEmotion = null;
    let lastAutoMotionIntensity = 0;

    function maybeAutoExpress(emotion, intensity, now) {
        if (!autoPerformance || !runtime || !hookedModel) return;
        const profile = runtime.profile;
        if (!profile) return;
        const key = nativeEmotionKey(emotion);

        // 1) 模型自带的原生动作优先：表现力比程序化合成强，也不容易每次都是同一套手势。
        // 但情绪稳定时不能每 8 秒就换一个姿势：换情绪/强度明显上升才用短冷却，否则走长冷却。
        if (!gestureOverlay && now >= motionSuppressUntil && intensity >= 0.5) {
            const emotionChanged = key !== lastAutoMotionEmotion
                || intensity >= lastAutoMotionIntensity + 0.2;
            const minGap = emotionChanged ? 8 : 30;
            if (now - lastAutoMotionAt >= minGap) {
                const map = profile.emotionMotionMap;
                const matched = (map && map[key]) || [];
                const pool = matched.length
                    ? matched
                    : (map && map.any && Math.random() < 0.4 ? map.any : null);
                if (pool && pool.length) {
                    const entry = pool[Math.floor(Math.random() * pool.length)];
                    lastAutoMotionAt = now;
                    lastAutoMotionEmotion = key;
                    lastAutoMotionIntensity = intensity;
                    // 这一轮已经演了原生动作，不再叠加合成手势（否则两套动作会互相顶）
                    lastAutoGestureAt = now;
                    console.warn('[Embodiment] 情绪动作', key, '→', entry.group, entry.index, entry.name || entry.label);
                    playMotion(entry.group, entry.index, 'normal');
                }
            }
        }

        // 2) 模型没有对应原生动作时，才用合成的头/身（以及有手臂时的整段手势）兜底
        if (!gestureOverlay && now >= motionSuppressUntil && intensity >= 0.55 &&
            now - lastAutoGestureAt >= 9) {
            const pool = (AUTO_GESTURES_BY_EMOTION[key] || []).filter((name) => {
                const def = GESTURES[name];
                return def && (!def.arms || profile.armCapable);
            });
            if (pool.length) {
                lastAutoGestureAt = now;
                startGesture(pool[Math.floor(Math.random() * pool.length)], 0.5 + intensity * 0.35, 0);
            }
        }

        // 3) 窗口级演出：位移/跳动与模型参数无关，任何模型都能用
        if (intensity >= 0.7 && now - lastAutoEffectAt >= 22) {
            const pool = AUTO_EFFECTS_BY_EMOTION[key];
            if (pool && pool.length) {
                lastAutoEffectAt = now;
                messageBus._dispatch({
                    type: 'pet_window_effect',
                    name: pool[Math.floor(Math.random() * pool.length)],
                    intensity: clamp(intensity, 0.25, 0.8),
                });
            }
        }
    }

    // ---- 消息处理 ----

    messageBus.on('pet:model-ready', function (msg) {
        const target = msg.model || (typeof model !== 'undefined' ? model : null);
        if (target) {
            hookModel(target);
        }
    });

    messageBus.on('pet:model-unloading', function () {
        hookedModel = null;
        paramIndexCache.clear();
        lastUpdateTime = null;
        gestureOverlay = null;
        pinnedExpressionName = null;
        lastNativeTrigger = { name: null, emotion: null, intensity: 0, at: -999 };
        lastAutoMotionAt = -999;
        lastAutoMotionEmotion = null;
        activeMotionParamIds = [];
    });

    messageBus.on('embodied_settings', function (msg) {
        const next = {
            modelName: msg.modelName,
            displayName: msg.displayName,
            parameters: msg.parameters || [],
            groups: msg.groups || [],
            expressions: msg.expressions || [],
            motions: msg.motions || [],
            lipSyncIds: msg.lipSyncIds || [],
            calibrationGains: msg.calibrationGains || (settings && settings.calibrationGains) || {},
            config: msg.config || {},
        };

        const hadRuntime = !!runtime;
        settings = next;
        runtimeUnavailable = false;
        // C# 下发的参数表通常没有 min/max，先向当前模型补全真实区间再建档案
        harvestParameterRanges(hookedModel);

        if (hadRuntime && runtime) {
            // 重新生成档案，参数表可能因换模型而变化
            try {
                const rebuilt = window.SoullinkProfile.buildProfile(settings, S);
                runtime.setProfile(rebuilt);
                publishCalibration(rebuilt);
            } catch (e) {
                console.error('[Embodiment] 更新模型表演档案失败', e);
                runtime = null;
            }
        }

        applySettings();
        gestureOverlay = null;
        ensureRuntime();
    });

    // 校准面板改了倍率：直接用缓存的参数表重建档案，不必等 C# 重新下发整套设置
    messageBus.on('calibration_gains', function (msg) {
        if (!settings) return;
        settings = Object.assign({}, settings, {calibrationGains: (msg && msg.gains) || {}});
        if (!runtime) return;
        try {
            const rebuilt = window.SoullinkProfile.buildProfile(settings, S);
            runtime.setProfile(rebuilt);
            publishCalibration(rebuilt);
        } catch (e) {
            console.error('[Embodiment] 应用校准倍率失败', e);
        }
    });

    messageBus.on('emotion_text', function (msg) {
        if (!runtime || !classifier) return;
        const text = String(msg.text || '').trim();
        if (!text) return;

        const gain = clamp(Number(msg.gain) || 1, 0.1, 1.5);
        const now = performance.now() / 1000;

        try {
            const intent = supplementIntent(classifier.classify(text), text);
            intent.intensity = clamp((Number(intent.intensity) || 0.5) * gain, 0.05, 1);
            runtime.triggerIntent(intent, now);

            // 模型自带表情（星星眼/哭哭/脸红/圈圈眼…）优先：整张表情文件比按参数名逐个合成准确，
            // 只有模型确实没有对应表情时，才用私有特征参数补一层。
            const nativeName = nativeExpressionFor(intent.emotion);
            if (nativeName) {
                if (maybePlayNativeExpression(nativeName, intent.emotion, intent.intensity, now, false))
                    console.warn('[Embodiment] 叠加原生表情', intent.emotion, '→', nativeName);
            } else if (intent.intensity >= 0.5) {
                startFeatureOverlay(intent.emotion, intent.intensity);
            }

            // 情绪够强时编排出整段动作：手势 / 模型自带动作 / 窗口位移
            maybeAutoExpress(intent.emotion, intent.intensity, now);

            // 用户发话后进入「等待回复」的小动作，避免站桩
            if (msg.source === 'user' && !envelopeAnalyzer.active) {
                runtime.startVoiceWaitingMotion(now, undefined, {
                    emotion: intent.emotion,
                    intensity: intent.intensity,
                });
            }
        } catch (e) {
            console.warn('[Embodiment] 文本情绪推断失败', e);
        }
    });

    messageBus.on('pet_perform', function (msg) {
        if (!ensureRuntime()) return;
        const now = performance.now() / 1000;
        const emotion = String(msg.emotion || 'happy');
        const intensity = clamp(Number(msg.intensity) || 0.7, 0.05, 1);

        const nativeName = msg.expression || nativeExpressionFor(emotion);
        if (nativeName) maybePlayNativeExpression(nativeName, emotion, intensity, now, !!msg.expression);
        if (msg.motion && hookedModel) {
            playNamedMotion(msg.motion);
        }

        try {
            runtime.triggerIntent({
                emotion,
                intensity,
                contextTags: ['manual'],
                sourceMessage: '',
            }, now);
            // 已经播了原生表情就不再按参数名合成同一张脸，避免把表情文件里的正确取值覆盖成 1
            if (!nativeName) startFeatureOverlay(emotion, intensity);
        } catch (e) {
            console.warn('[Embodiment] 指定情绪表演失败', e);
        }
        if (msg.gesture) startGesture(msg.gesture, Math.max(intensity, 0.7), 0);
        if (msg.effect) messageBus._dispatch({type: 'pet_window_effect', name: String(msg.effect), intensity});
        lastReport = {emotion, intensity, at: now - 1};
    });

    // AI 直接给面部参数（FACS AU），可指定基础情绪、原生表情与保持时间
    messageBus.on('pet_face', function (msg) {
        if (!ensureRuntime()) return;
        const now = performance.now() / 1000;
        const emotion = String(msg.emotion || '').trim();
        const intensity = clamp(Number(msg.intensity) || 0.7, 0.05, 1);

        if (emotion) {
            try {
                runtime.triggerIntent({
                    emotion,
                    intensity,
                    contextTags: ['pet_face'],
                    sourceMessage: '',
                }, now);
            } catch (e) {
                console.warn('[Embodiment] PetFace 基础情绪失败', e);
            }
            const nativeName = msg.expression || nativeExpressionFor(emotion);
            if (nativeName) maybePlayNativeExpression(nativeName, emotion, intensity, now, !!msg.expression);
        } else if (msg.expression) {
            maybePlayNativeExpression(String(msg.expression), null, intensity, now, true);
        }

        startFaceOverlay(msg, now);
        if (emotion) lastReport = {emotion, intensity, at: now - 1};
    });

    messageBus.on('pet_vad', function (msg) {
        if (!ensureRuntime()) return;
        runtime.applyVADTarget({
            valence: clamp(Number(msg.valence) || 0, -1, 1),
            arousal: clamp(Number(msg.arousal) || 0, -1, 1),
            dominance: clamp(Number(msg.dominance) || 0, -1, 1),
        }, clamp(Number(msg.amount) || 1, 0, 1));
    });

    messageBus.on('speech_state', function (msg) {
        if (!runtime) return;
        const speaking = !!msg.speaking;
        if (speaking) {
            envelopeAnalyzer.active = true;
        } else if (!voicePlayback) {
            envelopeAnalyzer.active = false;
            envelopeAnalyzer.levels = null;
        }
        try {
            runtime.setVoicePlaybackActive(speaking || voicePlayback);
        } catch (e) {
            console.warn('[Embodiment] 切换说话状态失败', e);
        }
    });

    messageBus.on('voice_envelope', function (msg) {
        const levels = Array.isArray(msg.envelope) ? msg.envelope : null;
        if (!levels || levels.length === 0) return;

        voicePlayback = true;
        envelopeAnalyzer.levels = levels;
        envelopeAnalyzer.windowMs = Number(msg.windowMs) || 60;
        envelopeAnalyzer.startMs = Number(msg.startUnixMs) || Date.now();
        envelopeAnalyzer.active = true;
        envelopeAnalyzer.level = 0;
        if (runtime) runtime.setVoicePlaybackActive(true);
    });

    messageBus.on('voice_stop', function () {
        voicePlayback = false;
        envelopeAnalyzer.active = false;
        envelopeAnalyzer.levels = null;
        envelopeAnalyzer.reset();
        if (runtime) runtime.setVoicePlaybackActive(false);
    });

    messageBus.on('native_expression', function (msg) {
        if (!msg.name) return;
        maybePlayNativeExpression(String(msg.name), null, 1, performance.now() / 1000, true);
    });

    // 外观与表演面板的总开关：关掉后本层的情绪/FACS/视线/原生动作全部停止写参数
    messageBus.on('auto_performance', function (msg) {
        const enabled = !(msg && msg.enabled === false);
        if (enabled === autoPerformance) return;
        autoPerformance = enabled;

        if (!enabled && runtime) {
            // 先给 1 秒把当前情绪淡回中性，再彻底停手；用户固定的表情/动作由各自通道保持
            autoReleaseUntil = performance.now() / 1000 + 1.2;
            try {
                runtime.applyVADTarget({valence: 0, arousal: 0, dominance: 0}, 0.9);
                runtime.setIdleEnabled(false);
            } catch (e) {
                console.warn('[Embodiment] 停止自动表演失败', e);
            }
            faceOverlay = null;
            gestureOverlay = null;
        } else if (enabled && runtime) {
            applySettings();
        }
    });

    // 用户手动固定的表情（星星眼/爱心眼/脸红…）：播放后不自动收回，并长期避让情绪层
    messageBus.on('expression_hold', function (msg) {
        if (!hookedModel || !msg || !msg.id) return;
        const sameExpression = pinnedExpressionName === String(msg.id);
        pinnedExpressionName = String(msg.id);
        // 换表情时先停掉旧的；重复下发同一个固定表情则不要重启，避免每次面板操作都闪一下
        if (sameExpression === false) {
            featureOverlay = null;
            stopNativeExpressions();
        }
        try {
            const result = hookedModel.expression(String(msg.id));
            if (result && typeof result.catch === 'function') result.catch(() => {});
        } catch (e) {
            console.warn('[Embodiment] 固定表情失败', msg.id, e);
        }
        beginExpressionSuppression(String(msg.id), 1e9);
        nativeExpressionUntil = 0;
    });

    // C# 侧决定收回表情（或用户手动重置）时走这里，确保避让窗口一起清掉
    messageBus.on('expression_reset', function () {
        pinnedExpressionName = null;
        nativeExpressionUntil = 0;
        expressionSuppressUntil = 0;
        expressionSuppressParamIds = new Set();
    });

    messageBus.on('native_motion', function (msg) {
        if (!hookedModel || !msg.group) return;
        playMotion(String(msg.group), Number(msg.index) || 0, msg.priority, msg.autoStopSeconds, msg.params);
    });

    // AI 指定的整段手势：挥手 / 点头 / 鞠躬 / 比心 / 举双手 / 耸肩 …
    messageBus.on('pet_gesture', function (msg) {
        if (!ensureRuntime()) return;
        startGesture(msg && msg.name, msg && msg.intensity, msg && msg.seconds);
    });

    // 增强版 ExpressionModule 也会直接发 expression / motion：
    // 这两条消息由它自己播放，具身层不重复播放，但必须知道这些参数已被原生动画占用并让路。
    messageBus.on('expression', function (msg) {
        if (!hookedModel || !msg || !msg.id) return;
        // 普通表情保持约 6 秒；hold=true 表示用户要求一直保持，避让窗口直接拉长
        beginExpressionSuppression(String(msg.id), msg.hold ? 1e9 : 6);
    });

    messageBus.on('motion', function (msg) {
        if (!hookedModel || !msg || !msg.group) return;
        beginMotionSuppression(String(msg.group), Number(msg.index) || 0);
    });

    messageBus.on('pointer', function (msg) {
        pointer.x = Number(msg.x) || 0;
        pointer.y = Number(msg.y) || 0;
        pointer.inside = !!msg.inside;
        pointer.width = Number(msg.width) || 0;
        pointer.height = Number(msg.height) || 0;
    });

    /** 停掉所有动作并回默认姿势；动作文件大多声明 Loop=true，不主动停就会一直挂着。 */
    function stopNativeMotions() {
        if (!hookedModel) return;
        const manager = hookedModel.internalModel && hookedModel.internalModel.motionManager;
        if (manager && typeof manager.stopAllMotions === 'function') {
            try { manager.stopAllMotions(); } catch { }
        }
    }

    // 动作参数不会自己回默认：停掉动作后要把它们写回默认值，否则手/道具会停在最后一帧
    let activeMotionParamIds = [];
    function rememberMotionParams(ids) {
        if (!Array.isArray(ids)) return;
        for (const id of ids) {
            if (id && activeMotionParamIds.includes(id) === false) activeMotionParamIds.push(id);
        }
    }
    function resetMotionParams() {
        const ids = activeMotionParamIds;
        activeMotionParamIds = [];
        if (!ids.length || !hookedModel) return;
        const core = hookedModel.internalModel && hookedModel.internalModel.coreModel;
        if (!core) return;
        for (const id of ids) {
            let index = -1;
            try { index = core.getParameterIndex(id); } catch { index = -1; }
            if (!(index >= 0)) continue;
            let def = 0;
            try {
                if (typeof core.getParameterDefaultValue === 'function') {
                    const value = Number(core.getParameterDefaultValue(index));
                    if (Number.isFinite(value)) def = value;
                }
            } catch { }
            try { core.setParameterValueById(id, def); } catch { }
        }
    }
    function stopAndResetMotions() {
        stopNativeMotions();
        resetMotionParams();
    }

    function playMotion(group, index, priority, autoStopSeconds, paramIds) {
        if (!hookedModel) return;
        if (priority !== 'idle') {
            beginMotionSuppression(group, index);
        }
        const value = priority === 'force'
            ? MotionPriority.FORCE
            : (priority === 'idle' ? MotionPriority.IDLE : MotionPriority.NORMAL);
        // 用全局序号作废旧动作的自动停止计时，避免把后面新播的动作也停掉
        const seq = window.__petMotionSeq = (window.__petMotionSeq || 0) + 1;
        const entry = (settings && settings.motions || []).find((item) =>
            String(item.group) === String(group) && Number(item.index) === Number(index));
        if (priority !== 'idle') {
            rememberMotionParams(Array.isArray(paramIds) && paramIds.length
                ? paramIds
                : ((entry && entry.params) || []));
        }
        try {
            const result = hookedModel.motion(group, index, value);
            if (result && typeof result.catch === 'function') result.catch(() => {});
        } catch (e) {
            console.warn('[Embodiment] 播放原生动作失败', group, index, e);
        }

        if (priority === 'idle') return;
        let after = Number(autoStopSeconds);
        let resetAfter = after;
        const duration = Number(entry && entry.duration) || 0;
        const loops = !!(entry && entry.loop);
        // 循环动作先播一遍就停住、保持最后一帧，到请求时长再复位；否则会反复「举起→放下」
        if (after > 0 && loops && duration > 0.5 && after > duration) {
            resetAfter = Math.min(after, 30);
            after = Math.max(0.4, duration - 0.1);
        }
        if (!(after > 0) && duration > 0.5) {
            after = Math.min(duration + 0.3, 20);
            resetAfter = after;
        }
        if (!(after > 0)) return;
        resetAfter = resetAfter > 0 ? Math.min(resetAfter, 30) : after;
        setTimeout(function() {
            if (window.__petMotionSeq !== seq) return;
            stopNativeMotions();
            if (resetAfter <= after) resetMotionParams();
        }, after * 1000);
        if (resetAfter > after) {
            setTimeout(function() {
                if (window.__petMotionSeq !== seq) return;
                resetMotionParams();
            }, resetAfter * 1000);
        }
    }

    function playNamedMotion(name) {
        if (!settings || !Array.isArray(settings.motions)) {
            return playMotion(name, 0, 'force');
        }
        const found = settings.motions.find((item) =>
            String(item.name || '') === name || String(item.group || '') === name);
        if (found) playMotion(String(found.group), Number(found.index) || 0, 'force');
    }

    // ---- 兜底：pet.js 的 model 变量在极早期可能尚未就绪 ----
    function watchModel() {
        requestAnimationFrame(watchModel);
        if (hookedModel) return;
        if (typeof model !== 'undefined' && model) {
            hookModel(model);
        }
    }
    watchModel();

    console.log('[Embodiment] 具身表演层已加载');
})();
