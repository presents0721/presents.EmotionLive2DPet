using System.ComponentModel;

namespace presents.EmotionLive2DPet;

/// <summary>具身桌宠配置。</summary>
public class EmotionLive2DPetConfig
{
    [Description("当前使用的 Live2D 模型。可在插件设置页的模型列表里点选；留空时自动使用找到的第一个模型。"
        + "模型可放在 Storage/Live2DDeskPet/Models 下，也可通过「浏览模型文件夹…」指向任意目录。")]
    public string ModelName { get; set; } = "";

    [Browsable(false)]
    [Description("额外的模型搜索目录（每行或用分号分隔）。设置页点「浏览模型文件夹…」时会自动加入，"
        + "切换模型时会递归查找目录下所有 *.model3.json。")]
    public string ExtraModelPaths { get; set; } = "";

    [Description("把模型目录里没在 model3.json 登记的表情/动作文件也纳入可用列表（很多模型包不登记，不开就用不了它们的表情）")]
    public bool MergeUnregisteredAssets { get; set; } = true;

    [Description("自动隐藏水印：模型自带「水印」表情时，按其参数每帧固定住，水印不再出现")]
    public bool HideWatermark { get; set; } = true;

    [Description("手动指定水印参数固定值，形如 Param191=1;Param71=1；留空则用模型自带水印表情里的值")]
    public string WatermarkParamOverride { get; set; } = "";

    [Description("表情强度倍率（0.2 ~ 2.0）。越大，FACS 表情越明显；默认 1.8 已放大嘴形、眼笑与眉毛幅度")]
    public double ExpressionIntensity { get; set; } = 1.8;

    [Description("身体动作幅度倍率（0.2 ~ 2.0）。影响头部摆动、身体倾斜和呼吸")]
    public double BodyMotionGain { get; set; } = 1.2;

    [Description("根据 AI 回复与你的输入自动推断情绪并表演（本地文本情绪识别，无需额外模型）")]
    public bool AutoEmotionFromChat { get; set; } = true;

    [Description("AI 回复时在桌宠底部显示半透明字幕条")]
    public bool ShowSubtitle { get; set; } = true;

    [Description("空闲时自动呼吸、眨眼、视线游移与低频小动作")]
    public bool IdleMotionEnabled { get; set; } = true;

    [Description("视线与头部轻微跟随鼠标（移动窗口或鼠标在桌宠附近时生效）")]
    public bool GazeFollowEnabled { get; set; } = true;

    [Description("使用 Alife 语音插件合成语音时，用真实音量包络驱动口型")]
    public bool SpeechLipSyncEnabled { get; set; } = true;

    [Description("重启后沿用上次的情绪状态（会自然衰减）")]
    public bool PersistEmotion { get; set; } = true;
}
