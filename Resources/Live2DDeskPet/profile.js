/**
 * 模型无关的 Soullink 表演档案构建器。
 *
 * 参考 soullink-emotion-sdk 的 profile 自动生成逻辑（MIT License）：
 * - 映射优先级与 SDK 一致：模型自带标准参数组 → Cubism 标准参数 ID → cdi3 参数名关键词；
 * - AU 语义通道的默认系数直接沿用 SDK（eyeSquint 0.22、browDown 0.85、browOuterUp 0.9）；
 * - 用 cdi3 里每个参数的真实 min/max/default 生成 outputRange，让同一语义值在不同模型上
 *   都落到该参数自己的有效区间，不再依赖全局「拍脑袋」放大倍数；
 * - 输出覆盖率和每键映射来源（standard-group / standard-id / name-match / derived），
 *   便于和 SDK 的 CalibrationPanel 一样核对映射是否正确。
 */

const STANDARD_PARAM_TABLE = {
  eyeOpen: {
    group: 'EyeBlink',
    pair: { left: ['ParamEyeLOpen'], right: ['ParamEyeROpen'] },
    mode: 'set', scale: 1, min: 0, max: 1.2,
  },
  eyeSmile: {
    pair: { left: ['ParamEyeLSmile'], right: ['ParamEyeRSmile'] },
    mode: 'set', scale: 1, min: 0, max: 1,
  },
  gazeX: { ids: ['ParamEyeBallX'], mode: 'set', scale: 1, min: -1, max: 1 },
  gazeY: { ids: ['ParamEyeBallY'], mode: 'set', scale: 1, min: -1, max: 1 },
  headX: { ids: ['ParamAngleX'], mode: 'set', scale: 30, min: -30, max: 30 },
  headY: { ids: ['ParamAngleY'], mode: 'set', scale: 30, min: -30, max: 30 },
  headZ: { ids: ['ParamAngleZ'], mode: 'set', scale: 30, min: -30, max: 30 },
  bodyX: { ids: ['ParamBodyAngleX'], mode: 'set', scale: 12, min: -12, max: 12 },
  bodyY: { ids: ['ParamBodyAngleY'], mode: 'set', scale: 12, min: -12, max: 12 },
  bodyZ: { ids: ['ParamBodyAngleZ'], mode: 'set', scale: 12, min: -12, max: 12 },
  mouthSmile: { ids: ['ParamMouthForm'], mode: 'set', scale: 1, min: -1, max: 1 },
  mouthOpen: {
    group: 'LipSync',
    ids: ['ParamMouthOpenY'],
    mode: 'set', scale: 1, min: 0, max: 1,
  },
  browInnerUp: {
    pair: { left: ['ParamBrowLY'], right: ['ParamBrowRY'] },
    mode: 'set', scale: 1, min: -1, max: 1,
  },
  browOuterUp: {
    pair: { left: ['ParamBrowLAngle'], right: ['ParamBrowRAngle'] },
    mode: 'set', scale: 0.9, min: -1, max: 1,
  },
  browDown: {
    pair: { left: ['ParamBrowLForm'], right: ['ParamBrowRForm'] },
    mode: 'set', scale: -0.85, min: -1, max: 1,
  },
  blush: { ids: ['ParamCheek'], mode: 'set', scale: 1, min: 0, max: 1 },
  breath: { ids: ['ParamBreath'], mode: 'set', scale: 1, min: 0, max: 1 },
};

/** 与 Soullink 引擎 defaultFACSState 一致的语义通道清单，用于覆盖率诊断。 */
const FACS_KEYS = [
  'browInnerUp', 'browOuterUp', 'browDown', 'eyeOpen', 'eyeSmile', 'eyeSquint',
  'eyeBlinkL', 'eyeBlinkR', 'mouthSmile', 'mouthFrown', 'mouthOpen', 'mouthPucker',
  'gazeX', 'gazeY', 'headX', 'headY', 'headZ', 'bodyX', 'bodyY', 'bodyZ',
  'blush', 'tear', 'sweat', 'breath',
];

function normalizeText(value) {
  return String(value ?? '')
    .normalize('NFKC')
    .toLowerCase()
    .replace(/[\s_\-./\\]+/g, '');
}

function unique(values) {
  return [...new Set(values)];
}

function matchesSemanticNeedle(parameter, needle) {
  const normalizedNeedle = normalizeText(needle);
  if (!normalizedNeedle) return false;
  const fields = [parameter.id, parameter.name, parameter.groupName].filter(Boolean);
  // 中文/日文没有词边界，直接做包含匹配；ASCII 走分词，避免 CatEar 被 "tear" 命中
  if (/[^\u0000-\u007f]/.test(normalizedNeedle))
    return fields.some((field) => normalizeText(field).includes(normalizedNeedle));
  const needleTokens = semanticTokens(needle);
  return fields.some((field) => {
    const tokens = semanticTokens(field);
    return needleTokens.length > 0 && needleTokens.every((needleToken) => tokens.some((token) => token.startsWith(needleToken)));
  });
}

function semanticTokens(input) {
  return String(input ?? '')
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2')
    .toLowerCase()
    .split(/[^a-z0-9]+/)
    .filter(Boolean);
}

function matchesSelectorNeedle(parameter, needle) {
  const normalizedNeedle = normalizeText(needle);
  if (!normalizedNeedle) return false;
  const normalizedId = normalizeText(parameter.id);
  const normalizedName = normalizeText(parameter.name);
  if (normalizedId.includes(normalizedNeedle) || normalizedName.includes(normalizedNeedle)) return true;
  // “左/右”这类方向词用更宽松的边界匹配，避免 left 命中 leftarm 之类
  if (normalizedNeedle === 'left' || normalizedNeedle === 'l') return /(^|[^a-z])l(eft)?($|[^a-z])/.test(parameter.id.toLowerCase());
  if (normalizedNeedle === 'right' || normalizedNeedle === 'r') return /(^|[^a-z])r(ight)?($|[^a-z])/.test(parameter.id.toLowerCase());
  return false;
}

class ParameterSelector {
  constructor(parameters) {
    this.parameters = parameters;
    this.byId = new Map(parameters.map((parameter) => [parameter.id, parameter]));
  }

  preferred(ids) {
    return ids.find((id) => this.byId.has(id));
  }

  bestMatch(needleGroups) {
    let best;
    for (const parameter of this.parameters) {
      const groupScores = needleGroups.map((needles) =>
        needles.filter((needle) => matchesSelectorNeedle(parameter, needle)).length);
      if (groupScores.some((score) => score === 0)) continue;
      const score = groupScores.reduce((sum, value) => sum + value, 0);
      if (!best || score > best.score) best = { id: parameter.id, score };
    }
    return best?.id;
  }

  one(needles, preferredIds) {
    return this.preferred(preferredIds) ?? this.bestMatch([needles]);
  }

  pair(sharedNeedles, leftIds, rightIds) {
    const left = this.preferred(leftIds) ?? this.bestMatch([sharedNeedles, ['left', '左', ' l']]);
    const right = this.preferred(rightIds) ?? this.bestMatch([sharedNeedles, ['right', '右', ' r']]);
    return [left, right].filter(Boolean);
  }

  eyeOpenPair(groups) {
    const blinkGroup = groups.find((group) => group.Target === 'Parameter' && group.Name === 'EyeBlink');
    const ids = (blinkGroup?.Ids ?? []).filter((id) => this.byId.has(id));
    if (ids.length >= 2) return ids.slice(0, 2);
    // 老工程模型常用 PARAM_EYE_L_OPEN / 左眼　開閉 这类命名，needle 要覆盖拆分形式
    return this.pair(
      ['eyeopen', 'eyelopen', 'eyeropen', '开闭', '開閉', 'まばたき', 'blink'],
      ['ParamEyeLOpen'], ['ParamEyeROpen']);
  }

  many(needles, exclusions = []) {
    const normalizedExclusions = exclusions.map(normalizeText).filter(Boolean);
    const result = [];
    for (const parameter of this.parameters) {
      const haystack = normalizeText(`${parameter.id} ${parameter.name} ${parameter.groupName}`);
      if (normalizedExclusions.some((needle) => haystack.includes(needle))) continue;
      if (needles.some((needle) => matchesSemanticNeedle(parameter, needle))) result.push(parameter.id);
    }
    return unique(result).slice(0, 4);
  }
}

function resolveStandard(key, parameters, groups) {
  const spec = STANDARD_PARAM_TABLE[key];
  if (!spec) return undefined;
  const ids = new Set(parameters.map((parameter) => parameter.id));
  if (spec.group) {
    const group = groups.find((candidate) => candidate.Target === 'Parameter' && candidate.Name === spec.group);
    const groupIds = (group?.Ids ?? []).filter((id) => ids.has(id));
    if (groupIds.length > 0) return { ids: groupIds, source: 'standard-group' };
  }
  const candidateIds = spec.pair ? [...spec.pair.left, ...spec.pair.right] : spec.ids ?? [];
  if (candidateIds.length > 0 && candidateIds.every((id) => ids.has(id)))
    return { ids: [...candidateIds], source: 'standard-id' };
  return undefined;
}

function buildPrivateEmotionMap(parameters, excludedIds) {
  const definitions = [
    {
      key: 'positiveEye', category: 'positiveEye',
      needles: ['爱心眼', '星星眼', 'heart eye', 'love eye', 'star eye', 'sparkle eye'],
      priority: 90, confidence: 0.94, exclusiveGroup: 'face-effect',
    },
    {
      key: 'confusionEffect', category: 'privateEffect',
      needles: ['困惑', '疑问', 'confused', 'confusion', 'question mark'],
      emotions: ['confused'], priority: 95, confidence: 0.96, exclusiveGroup: 'face-effect',
    },
    {
      key: 'angerEffect', category: 'anger',
      needles: ['生气', '愤怒', '怒', 'angry', 'anger', 'mad'],
      priority: 90, confidence: 0.95, exclusiveGroup: 'face-effect',
    },
    {
      key: 'shadowEffect', category: 'shadow',
      needles: ['脸黑', '黑脸', '阴影', 'shadow', 'dark face'],
      priority: 80, confidence: 0.94, exclusiveGroup: 'face-effect',
    },
    {
      key: 'surpriseEffect', category: 'surprise',
      needles: ['惊讶', '震惊', 'surprise', 'shock'],
      priority: 80, confidence: 0.92, exclusiveGroup: 'face-effect',
    },
    {
      key: 'starEffect', category: 'privateEffect',
      needles: ['星星', 'star', 'sparkle'],
      emotions: ['excited', 'happy', 'surprised'],
      priority: 70, confidence: 0.82, exclusiveGroup: 'face-effect',
    },
  ];

  const result = {};
  const claimed = new Set();
  for (const definition of definitions) {
    const targets = parameters
      .filter((parameter) => !excludedIds.has(parameter.id) && !claimed.has(parameter.id))
      .filter((parameter) => definition.needles.some((needle) => matchesSemanticNeedle(parameter, needle)))
      .map((parameter) => parameter.id)
      .slice(0, 4);
    if (!targets.length) continue;
    targets.forEach((target) => claimed.add(target));
    result[definition.key] = {
      targets,
      category: definition.category,
      ...(definition.emotions ? { emotions: definition.emotions } : {}),
      exclusiveGroup: definition.exclusiveGroup,
      priority: definition.priority,
      source: 'heuristic',
      confidence: definition.confidence,
    };
  }
  return result;
}

function idleConfigFor(map) {
  const idleConfig = {};
  if (map.gazeX) idleConfig.gazeX = [-0.12, 0.12];
  if (map.gazeY) idleConfig.gazeY = [-0.06, 0.08];
  if (map.headX) idleConfig.headX = [-0.08, 0.08];
  if (map.headY) idleConfig.headY = [-0.04, 0.04];
  if (map.headZ) idleConfig.headZ = [-0.05, 0.05];
  if (map.bodyX) idleConfig.bodyX = [-0.045, 0.045];
  if (map.bodyY) idleConfig.bodyY = [-0.014, 0.014];
  if (map.bodyZ) idleConfig.bodyZ = [-0.055, 0.055];
  if (map.mouthSmile) idleConfig.mouthSmile = [0.02, 0.1];
  if (map.browInnerUp) idleConfig.browInnerUp = [0, 0.06];
  if (map.eyeOpen) idleConfig.eyeOpen = [0.9, 1];
  return idleConfig;
}

function buildExpressionMap(expressions) {
  if (!expressions?.length) return undefined;
  // 表情名没有统一规范：同一个情绪在不同模型里可能叫「黑脸 / 怒り顔 / angry_face / Angry」。
  // 这里维护一套中英日关键词本体，命中即映射；没命中的表情保持原样，不参与情绪映射。
  // 顺序有意义：越具体、越容易误判的放前面（例如「脸红」不能先被 shy 之外的规则抢走）。
  const emotionHeuristics = [
    [['blush', '脸红', '害羞', '害臊', '羞', '照れ', '赤面', 'embarrassed', 'shy'], 'shy'],
    [['angry', 'anger', 'mad', 'rage', '怒', '生气', '愤怒', '黑脸', '怒り', 'おこ', 'ムカ'], 'angry'],
    [['tears', '泪', '哭', '泣', '流泪', 'tear', 'cry', 'sad', '悲', '难过', '伤心', '泣き'], 'sad'],
    // 星星眼/心心眼这类 VTS 常用表情在中文包里很常见，归到 happy 最容易被触发
    [['loveeyes', 'loveeye', 'love', 'heart', '爱心', '心心', '星星', 'star', 'sparkle', 'kira', '目星'], 'happy'],
    [['excited', '兴奋', 'やる気', 'waku', 'excite'], 'excited'],
    [['confused', 'confusion', 'question', '困惑', '疑问', '疑问符', '幽灵', 'ghost', '晕', 'dizzy', 'はてな', '？', '?'], 'confused'],
    [['smile', 'happy', '开心', '微笑', '笑顔', 'にこ', '笑'], 'happy'],
    [['surprised', 'surprise', 'shock', '惊', '震惊', 'びっくり', '驚き'], 'surprised'],
    [['worried', 'worry', 'anxious', '担心', '不安', '心配'], 'concerned'],
    [['sleepy', 'tired', 'sleep', '困', '睡', '累', '疲', '眠'], 'tired'],
    [['disgust', 'annoyed', '嫌弃', '无语', '白眼', '嫌'], 'disgust'],
  ];
  const sorted = [...expressions].sort((a, b) => String(a.file).localeCompare(String(b.file)));
  const result = {};
  const claimed = new Set();
  for (const { name } of sorted) {
    const normalized = normalizeText(name);
    for (const [needles, emotion] of emotionHeuristics) {
      if (claimed.has(emotion)) continue;
      if (needles.some((needle) => normalized.includes(normalizeText(needle)))) {
        result[emotion] = name;
        claimed.add(emotion);
        break;
      }
    }
  }
  return Object.keys(result).length ? result : undefined;
}

function sanitizeId(value) {
  return String(value ?? 'model').replace(/[^a-zA-Z0-9_\-]+/g, '_').slice(0, 48) || 'model';
}

/**
 * @param {{modelName:string, displayName?:string, modelUrl?:string, parameters:Array, groups:Array, expressions:Array, motions:Array, lipSyncIds?:string[]}} settings
 * @param {object} engine Soullink 引擎模块（window.Soullink）
 */
function buildProfile(settings, engine) {
  const parameters = (settings.parameters ?? [])
    .filter((parameter) => parameter && parameter.id)
    .map((parameter) => ({
      id: String(parameter.id),
      name: parameter.name ?? '',
      groupId: parameter.groupId ?? '',
      groupName: parameter.groupName ?? '',
      // cdi3 自带的参数区间：校准的输入，缺失时退回 Cubism 常规区间
      min: Number.isFinite(parameter.min) ? parameter.min : -1,
      max: Number.isFinite(parameter.max) ? parameter.max : 1,
      default: Number.isFinite(parameter.default) ? parameter.default : 0,
    }));
  const groups = (settings.groups ?? []).map((group) => ({
    Target: group.target ?? group.Target,
    Name: group.name ?? group.Name,
    Ids: group.ids ?? group.Ids ?? [],
  }));
  const expressions = (settings.expressions ?? []).filter((item) => item?.name);
  const motions = (settings.motions ?? []).filter((item) => item?.group);

  const selector = new ParameterSelector(parameters);
  const paramIds = new Set(parameters.map((parameter) => parameter.id));
  const byId = new Map(parameters.map((parameter) => [parameter.id, parameter]));
  const map = {};
  const add = (key, rule) => { if (rule) map[key] = rule; };
  /** 每个 FACS 键最终的解析来源，对齐 SDK 的 CalibrationPanel 覆盖率信息。 */
  const sources = {};

  // 用户在校准面板里手动调的通道倍率（按模型持久化）；缺省 1 = 完全按 cdi3 区间自动校准
  const gainOverrides = settings.calibrationGains ?? {};
  const gainOf = (key) => {
    const value = Number(gainOverrides[key]);
    // 0 表示关闭该通道（手臂手势这类不确定的通道需要能彻底停掉）
    return Number.isFinite(value) && value >= 0 ? Math.min(3, value) : 1;
  };
  /** 绝对值通道（eyeOpen/mouthOpen/breath）语义即参数值，倍率无意义；眨 eye 为派生通道。 */
  const NOT_ADJUSTABLE = new Set(['eyeOpen', 'mouthOpen', 'breath', 'eyeBlinkL', 'eyeBlinkR']);

  /** 按关键词语义在全部参数里收集目标（不限 4 个，不依赖某个模型的 Param 编号）。 */
  const collectTargets = (keywords, exclusions = []) => {
    const normalizedExclusions = exclusions.map(normalizeText).filter(Boolean);
    const ids = [];
    for (const parameter of parameters) {
      const haystack = normalizeText(`${parameter.id} ${parameter.name} ${parameter.groupName}`);
      if (normalizedExclusions.some((needle) => haystack.includes(needle))) continue;
      if (keywords.some((needle) => matchesSemanticNeedle(parameter, needle))) ids.push(parameter.id);
    }
    return unique(ids);
  };

  /** cdi3 记录的参数有效区间：不同模型同一语义参数可能差一个量级，校准就落在这里。 */
  const toIdList = (ids) => unique((Array.isArray(ids) ? ids : [ids]).filter(Boolean));

  const rangeOf = (ids) => {
    let min = Infinity;
    let max = -Infinity;
    let defaultValue;
    let count = 0;
    for (const id of toIdList(ids)) {
      const parameter = byId.get(id);
      if (!parameter) continue;
      const pMin = Number.isFinite(parameter.min) ? parameter.min : -1;
      const pMax = Number.isFinite(parameter.max) ? parameter.max : 1;
      const pDef = Number.isFinite(parameter.default) ? parameter.default : 0;
      min = Math.min(min, pMin);
      max = Math.max(max, pMax);
      if (defaultValue === undefined) defaultValue = pDef;
      count++;
    }
    if (!count) return undefined;
    return { min, max, default: defaultValue, span: Math.max(max - defaultValue, defaultValue - min, 0.1) };
  };

  /**
   * 正向通道（0 → 参数默认值，1 → 参数上限）。
   * outputRange 直接把语义值换成该参数自己的数值区间，代替原来全局 1.6/1.8 之类的放大倍数。
   */
  const positiveRule = (ids, gain = 1) => {
    const list = toIdList(ids);
    const range = rangeOf(list);
    if (!range) return undefined;
    return {
      targets: list, mode: 'set', scale: 1,
      min: range.min, max: range.max,
      outputRange: [range.default, range.default + (range.max - range.default) * gain],
    };
  };

  /** 负向通道（0 → 默认值，1 → 参数下限），用于皱眉、眉毛下压这类独占目标的键。 */
  const negativeRule = (ids, gain = 1) => {
    const list = toIdList(ids);
    const range = rangeOf(list);
    if (!range) return undefined;
    return {
      targets: list, mode: 'set', scale: 1,
      min: range.min, max: range.max,
      outputRange: [range.default, range.default + (range.min - range.default) * gain],
    };
  };

  /** 与正向通道共用目标时叠加负向幅度（mouthFrown / eyeSquint），避免 set 互相覆盖。 */
  const subtractRule = (ids, gain = 1) => {
    const list = toIdList(ids);
    const range = rangeOf(list);
    if (!range) return undefined;
    return {
      targets: list, mode: 'subtract', scale: 1,
      min: range.min, max: range.max,
      outputRange: [range.default, range.default + (range.default - range.min) * gain],
    };
  };

  /** 角度/视线类双极通道：语义 -1..1 映射到参数对称区间。 */
  const bipolarRule = (id, gain = 1) => {
    if (!id) return undefined;
    const range = rangeOf([id]);
    if (!range) return undefined;
    const bound = Math.min(Math.max(range.max - range.default, 0.1), Math.max(range.default - range.min, 0.1));
    return {
      target: id, mode: 'set', scale: 1,
      min: range.min, max: range.max,
      // 语义是 -1..1（0 = 中性）；inputRange 负责把 -1..1 归一化成 0..1，
      // 再由 outputRange 落到该参数自己的区间，否则 0 会被映射到区间下限。
      inputRange: [-1, 1],
      outputRange: [range.default - bound * gain, range.default + bound * gain],
    };
  };

  /** 绝对值通道（eyeOpen / mouthOpen / breath）：语义值本身就是参数值，只按模型区间钳制。 */
  const absoluteRule = (ids) => {
    const list = toIdList(ids);
    const range = rangeOf(list);
    if (!range) return undefined;
    return { targets: list, mode: 'set', scale: 1, min: range.min, max: range.max };
  };

  /** 负向叠加：眉毛下压与 browInnerUp 共用同一组参数时不能 set，否则会把内眉上抬清成 0。 */
  const negativeAddRule = (ids, gain = 1) => {
    const list = toIdList(ids);
    const range = rangeOf(list);
    if (!range) return undefined;
    const span = Math.max(range.default - range.min, range.max - range.default, 0.1);
    return { targets: list, mode: 'add', scale: -span * gain, min: range.min, max: range.max };
  };

  /** 同一参数被多个语义键写入时用 add，把幅度换算成参数单位。 */
  const addRule = (ids, gain) => {
    const list = toIdList(ids);
    const range = rangeOf(list);
    if (!range) return undefined;
    return {
      targets: list, mode: 'add',
      scale: Math.max(0.05, (range.max - range.default) * gain),
      min: range.min, max: range.max,
    };
  };

  /** SDK 的三层解析：模型标准分组 → Cubism 标准 ID → cdi3 名称关键词。 */
  const pickStandard = (key, nameMatchIds = []) => {
    const standard = resolveStandard(key, parameters, groups);
    if (standard?.ids?.length) {
      sources[key] = standard.source;
      return standard.ids;
    }
    const list = unique(nameMatchIds.filter(Boolean));
    if (list.length) sources[key] = 'name-match';
    return list;
  };

  const blinkGroup = groups.find((group) => group.Target === 'Parameter' && group.Name === 'EyeBlink');
  const blinkGroupIds = (blinkGroup?.Ids ?? []).filter((id) => paramIds.has(id));
  const eyeOpenIds = selector.eyeOpenPair(groups);

  if (eyeOpenIds.length) {
    sources.eyeOpen = blinkGroupIds.length >= 2 ? 'standard-group' : 'name-match';
    add('eyeOpen', absoluteRule(eyeOpenIds));
    const eyeRange = rangeOf(eyeOpenIds);
    if (eyeOpenIds[0]) {
      add('eyeBlinkL', { target: eyeOpenIds[0], mode: 'subtract', scale: 1, min: eyeRange.min, max: eyeRange.max });
      sources.eyeBlinkL = 'derived';
    }
    if (eyeOpenIds[1]) {
      add('eyeBlinkR', { target: eyeOpenIds[1], mode: 'subtract', scale: 1, min: eyeRange.min, max: eyeRange.max });
      sources.eyeBlinkR = 'derived';
    }
    // 眯眼与 eyeOpen 共用目标，必须叠加（subtract）；系数沿用 SDK 的 0.22
    add('eyeSquint', subtractRule(eyeOpenIds, 0.22 * gainOf('eyeSquint')));
    sources.eyeSquint = 'derived';
  }

  const eyeSmile = pickStandard('eyeSmile',
    selector.pair(['eyesmile', 'eye smile', '微笑'], ['ParamEyeLSmile'], ['ParamEyeRSmile']));
  add('eyeSmile', positiveRule(eyeSmile, gainOf('eyeSmile')));

  const gazeX = pickStandard('gazeX', [selector.one(['eyeballx', 'eye x', '眼球x', '眼珠x'], ['ParamEyeBallX'])])[0];
  const gazeY = pickStandard('gazeY', [selector.one(['eyebally', 'eye y', '眼球y', '眼珠y'], ['ParamEyeBallY'])])[0];
  add('gazeX', bipolarRule(gazeX, gainOf('gazeX')));
  add('gazeY', bipolarRule(gazeY, gainOf('gazeY')));

  const headX = pickStandard('headX', [selector.one(['anglex', '角度x'], ['ParamAngleX'])])[0];
  const headY = pickStandard('headY', [selector.one(['angley', '角度y'], ['ParamAngleY'])])[0];
  const headZ = pickStandard('headZ', [selector.one(['anglez', '角度z'], ['ParamAngleZ'])])[0];
  add('headX', bipolarRule(headX, gainOf('headX')));
  add('headY', bipolarRule(headY, gainOf('headY')));
  add('headZ', bipolarRule(headZ, gainOf('headZ')));

  const bodyX = pickStandard('bodyX', [selector.one(['bodyanglex', '身体旋转x', '身体x'], ['ParamBodyAngleX'])])[0];
  const bodyY = pickStandard('bodyY', [selector.one(['bodyangley', '身体旋转y', '身体y'], ['ParamBodyAngleY'])])[0];
  const bodyZ = pickStandard('bodyZ', [selector.one(['bodyanglez', '身体旋转z', '身体z'], ['ParamBodyAngleZ'])])[0];
  add('bodyX', bipolarRule(bodyX, gainOf('bodyX')));
  add('bodyY', bipolarRule(bodyY, gainOf('bodyY')));
  add('bodyZ', bipolarRule(bodyZ, gainOf('bodyZ')));

  // ---- 手臂（可选通道）：只有模型真的暴露手臂参数时才映射 ----
  // Cubism 官方示例常用 ParamArmLA01/RA01（肩）、ParamArmLA02（肘）；VTuber 导出常见 ParamArmLA/ParamArmRA。
  // 缺失时手势层自动降级成头/身动作，写不存在的参数在 Cubism 里是空操作。
  const pickArmParam = (preferredIds, pattern) =>
    selector.preferred(preferredIds) ?? (parameters.find((item) => pattern.test(item.id))?.id);
  const armLShoulder = pickArmParam(['ParamArmLA01', 'ParamArmLA', 'ParamArmL'], /^ParamArmL(?:A?0?1)?$/i);
  const armRShoulder = pickArmParam(['ParamArmRA01', 'ParamArmRA', 'ParamArmR'], /^ParamArmR(?:A?0?1)?$/i);
  add('armLShoulder', bipolarRule(armLShoulder, gainOf('armLShoulder')));
  add('armRShoulder', bipolarRule(armRShoulder, gainOf('armRShoulder')));
  if (armLShoulder) sources.armLShoulder = 'standard-id';
  if (armRShoulder) sources.armRShoulder = 'standard-id';

  const mouthForm = pickStandard('mouthSmile',
    [selector.one(['mouthform', '嘴变形', '嘴　变形'], ['ParamMouthForm'])])[0];
  if (mouthForm) {
    add('mouthSmile', positiveRule(mouthForm, gainOf('mouthSmile')));
    // 嘴角下撇与微笑共用嘴形参数：用 SDK 的 subtract 叠加，不用两套 set 互相覆盖
    add('mouthFrown', subtractRule(mouthForm, gainOf('mouthFrown')));
    sources.mouthFrown = 'derived';
  } else {
    // 少数模型没有嘴形参数，而是把嘴角上抬/下撇拆成两个独立参数（如 上がり口 / 下がり口）
    const mouthUp = collectTargets(['mouth up', '上がり口', '嘴角上', '口角上']);
    const mouthDown = collectTargets(['mouth down', '下がり口', '嘴角下', '口角下']);
    if (mouthUp.length) { add('mouthSmile', positiveRule(mouthUp, gainOf('mouthSmile'))); sources.mouthSmile = 'name-match'; }
    if (mouthDown.length) { add('mouthFrown', positiveRule(mouthDown, gainOf('mouthFrown'))); sources.mouthFrown = 'name-match'; }
  }

  // 优先使用模型声明的 LipSync 参数组；否则回退标准口型参数
  const declaredLipSync = (settings.lipSyncIds ?? []).filter((id) => paramIds.has(id));
  if (declaredLipSync.length) sources.mouthOpen = 'lipsync-group';
  const mouthOpenIds = declaredLipSync.length
    ? declaredLipSync
    : pickStandard('mouthOpen', [selector.one(['mouthopeny', '嘴张开', '张开和闭合'], ['ParamMouthOpenY'])]);
  add('mouthOpen', absoluteRule(mouthOpenIds));

  const mouthPucker = selector.one(['mouthpucker', '鼓嘴', '嘟嘴'], []);
  add('mouthPucker', positiveRule([mouthPucker], gainOf('mouthPucker')));
  if (mouthPucker) sources.mouthPucker = 'name-match';

  const browY = pickStandard('browInnerUp',
    selector.pair(['brow', '眉', '上下'], ['ParamBrowLY'], ['ParamBrowRY']));
  const browAngle = pickStandard('browOuterUp',
    selector.pair(['brow', '眉', 'angle', '角度'], ['ParamBrowLAngle'], ['ParamBrowRAngle']));
  const browForm = pickStandard('browDown',
    selector.pair(['brow', '眉', 'form', '变形', '変形'], ['ParamBrowLForm'], ['ParamBrowRForm']));

  // 很多模型没有独立「眉角」参数，回退时 browOuterUp 会和 browInnerUp 撞到同一组参数。
  // 重叠目标必须用 add 叠加，否则难过时「内眉上抬」会被后写的 set 清成 0。
  const browOuterOnly = browAngle.filter((id) => !browY.includes(id));
  const browOuterShared = browAngle.filter((id) => browY.includes(id));
  add('browInnerUp', positiveRule(browY, gainOf('browInnerUp')));
  if (browOuterOnly.length) {
    add('browOuterUp', positiveRule(browOuterOnly, 0.9 * gainOf('browOuterUp')));
  } else {
    add('browOuterUp', addRule(browOuterShared, 0.9 * gainOf('browOuterUp')));
  }
  // 眉毛下压采用 SDK 的 0.85 系数；若与内眉/眉角共用参数则改为负向叠加，避免覆盖
  const browDownShared = browForm.some((id) => browY.includes(id) || browAngle.includes(id));
  const browDownGain = 0.85 * gainOf('browDown');
  add('browDown', browDownShared ? negativeAddRule(browForm, browDownGain) : negativeRule(browForm, browDownGain));

  const standardBlush = resolveStandard('blush', parameters, groups);
  if (standardBlush?.ids?.length) sources.blush = standardBlush.source;
  const blushName = selector.many(['blush', '脸红', '脸颊泛红', '腮红'], ['脸黑']);
  if (!standardBlush && blushName.length) sources.blush = 'name-match';
  add('blush', positiveRule([...(standardBlush?.ids ?? []), ...blushName], gainOf('blush')));

  // 泪目/汗水按参数名语义收集；排除物理演算驱动的「流泪物理」类参数，逐帧写死会让泪水抖动
  const tearIds = collectTargets(['tear', '泪', '眼泪', '泣'], ['汗', '流泪物理', 'tearphysics']);
  if (tearIds.length) sources.tear = 'name-match';
  add('tear', positiveRule(tearIds, gainOf('tear')));
  const sweatIds = collectTargets(['sweat', '汗'], ['泪']);
  if (sweatIds.length) sources.sweat = 'name-match';
  add('sweat', positiveRule(sweatIds, gainOf('sweat')));

  const breath = pickStandard('breath', [selector.one(['breath', '呼吸'], ['ParamBreath'])])[0];
  add('breath', absoluteRule([breath].filter(Boolean)));

  const mappedIds = new Set();
  for (const rule of Object.values(map)) {
    if (rule?.target) mappedIds.add(rule.target);
    for (const id of rule?.targets ?? []) mappedIds.add(id);
  }

  // 覆盖率诊断：对齐 SDK CalibrationPanel 的 FACS Key Coverage，
  // 直接告诉使用者「哪些语义通道真的映射到了这个模型」以及每条的来源与数值区间。
  const calibrationPerKey = {};
  const unmappedKeys = [];
  const heuristicKeys = [];
  for (const key of FACS_KEYS) {
    const rule = map[key];
    const targets = rule ? (rule.target ? [rule.target] : [...(rule.targets ?? [])]) : [];
    const range = targets.length ? rangeOf(targets) : undefined;
    const mapped = Boolean(rule) && Boolean(range);
    const source = mapped ? (sources[key] ?? 'heuristic') : 'unmapped';
    if (!mapped) unmappedKeys.push(key);
    else if (source === 'name-match' || source === 'heuristic') heuristicKeys.push(key);
    calibrationPerKey[key] = {
      mapped,
      source,
      targets,
      adjustable: !NOT_ADJUSTABLE.has(key) && mapped,
      gain: gainOf(key),
      ...(range ? { range: [range.min, range.max], default: range.default } : {}),
      ...(rule?.mode ? { mode: rule.mode } : {}),
      ...(rule?.outputRange ? { outputRange: rule.outputRange } : {}),
    };
  }
  const mappedKeyCount = FACS_KEYS.length - unmappedKeys.length;
  const calibration = {
    mappedKeyCount,
    totalKeyCount: FACS_KEYS.length,
    coverage: Number((mappedKeyCount / FACS_KEYS.length).toFixed(3)),
    mappedTargetCount: mappedIds.size,
    parameterCount: parameters.length,
    unmappedKeys,
    heuristicKeys,
    perKey: calibrationPerKey,
  };

  const profile = {
    schemaVersion: engine.CURRENT_SCHEMA_VERSION,
    modelId: sanitizeId(settings.modelName),
    displayName: settings.displayName || settings.modelName,
    version: '1.0.0',
    modelPath: settings.modelUrl || settings.modelName,
    capabilities: {},
    parameterMap: map,
    calibration,
    idleConfig: idleConfigFor(map),
    reactionBias: {
      shy: { blushMultiplier: 1.1, gazeAwayMultiplier: 1.05 },
      happy: { mouthSmileMultiplier: 1, eyeSmileMultiplier: 1 },
    },
    neutralParams: engine.deriveNeutralParams({ parameterMap: map }),
    parameterSmoothing: engine.deriveParameterSmoothing({ parameterMap: map }),
  };

  const privateEmotionMap = buildPrivateEmotionMap(parameters, mappedIds);
  if (Object.keys(privateEmotionMap).length) profile.privateEmotionMap = privateEmotionMap;

  if (expressions.length || motions.length) {
    profile.nativeAnimations = {
      expressions: expressions.map((item) => ({
        name: item.name,
        file: item.file || `${item.name}.exp3.json`,
        ...(item.params && item.params.length ? {params: [...item.params]} : {}),
      })),
      motions: motions.map((item) => ({
        group: item.group,
        index: Number(item.index) || 0,
        file: item.file || `${item.group}_${item.index}.motion3.json`,
      })),
    };
  }

  const expressionMap = buildExpressionMap(expressions);
  if (expressionMap) {
    profile.expressionMap = expressionMap;
    // 把「情绪 → 该原生表情实际控制的参数」一并记下来，
    // 表演层可以直接按值写这些开关参数，不需要知道模型具体用了哪个 Param 编号。
    const byName = new Map(expressions.map((item) => [item.name, item]));
    const paramsByEmotion = {};
    for (const [emotion, name] of Object.entries(expressionMap)) {
      const ids = byName.get(name)?.params ?? [];
      if (ids.length) paramsByEmotion[emotion] = [...ids];
    }
    if (Object.keys(paramsByEmotion).length) profile.expressionParams = paramsByEmotion;
  }

  // ---- 情绪 → 模型原生动作的自动绑定 ----
  // 动作名/文件名里带语义关键词的按情绪归类；没有关键词的动作进 any 池，
  // 只在情绪很强时低频抽取，避免把「待机」「场景」这类长动作当表情动作播。
  const MOTION_EMOTION_KEYWORDS = {
    happy: ['smile', 'happy', 'joy', 'laugh', 'fun', '笑', '开心', '嬉', '喜'],
    excited: ['excited', 'cheer', 'jump', 'yay', '兴奋', '欢呼', '嗨'],
    surprised: ['surprise', 'shock', 'wow', '惊', '意'],
    sad: ['sad', 'cry', 'tear', '悲', '哭', '难过', '泣'],
    angry: ['angry', 'anger', 'mad', '怒', '生气'],
    shy: ['shy', 'blush', '照れ', '羞', '恥'],
    confused: ['confus', 'question', '疑问', '困惑', '?'],
    proud: ['proud', '得意', '自信'],
    relaxed: ['relax', 'calm', 'breath', '放松', '呼吸'],
    bored: ['bored', 'sleepy', '困', '无聊'],
  };
  const motionEntries = motions.map((item) => ({
    group: item.group,
    index: Number(item.index) || 0,
    name: item.name || '',
    file: item.file || '',
    duration: Number(item.duration) || 0,
  }));
  const emotionMotionMap = {};
  const motionPool = [];
  for (const entry of motionEntries) {
    const haystack = normalizeText(`${entry.name} ${entry.group} ${entry.file}`);
    // 待机/场景动画不是「表情动作」，自动触发会显得莫名其妙
    if (/(idle|scene|loop|待机|场景)/.test(haystack)) continue;
    motionPool.push(entry);
    for (const [emotion, words] of Object.entries(MOTION_EMOTION_KEYWORDS)) {
      if (!words.some((word) => haystack.includes(normalizeText(word)))) continue;
      (emotionMotionMap[emotion] ??= []).push(entry);
    }
  }
  if (motionPool.length) emotionMotionMap.any = motionPool;

  profile.capabilities = engine.detectCapabilities(profile);
  // 注意不能写 profile.motionMap：那是 Soullink 自带的「情绪→单个动作绑定」字段，
  // 前端编排层用的是自己的候选池（带冷却），字段名必须区分开。
  if (Object.keys(emotionMotionMap).length) profile.emotionMotionMap = emotionMotionMap;
  // 手臂是否可用：前端手势层据此决定叠加手臂关键帧还是只演头/身
  profile.armCapable = Boolean(armLShoulder || armRShoulder);

  // 保留未映射参数信息，便于调试面板展示
  profile.debug = {
    eyeOpenSource: sources.eyeOpen ?? 'none',
    mappedTargetCount: mappedIds.size,
    parameterCount: parameters.length,
    facsKeyCount: calibration.totalKeyCount,
    facsMappedKeyCount: calibration.mappedKeyCount,
    unmappedKeys: calibration.unmappedKeys,
  };

  return profile;
}

window.SoullinkProfile = { buildProfile };
