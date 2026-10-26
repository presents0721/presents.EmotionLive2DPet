using System;

namespace presents.EmotionLive2DPet;

/// <summary>
/// 当前生效的模型元数据。换模型时只替换 <see cref="Metadata"/>，
/// 各模块持有同一个上下文对象即可立刻读到新模型（不必重建整个服务容器）。
/// </summary>
public sealed class PetModelContext
{
    public PetModelMetadata Metadata { get; set; } = null!;
}

/// <summary>
/// 当前配置对象。设置页改动后由主模块替换为最新实例，
/// 模块每次使用时读取 <see cref="Current"/>，避免抓到旧配置。
/// </summary>
public sealed class PetConfigContext(EmotionLive2DPetConfig current)
{
    public EmotionLive2DPetConfig Current { get; set; } = current;
}

/// <summary>设置页「模型」列表里的一项。</summary>
/// <param name="Name">展示名（模型文件夹名）。</param>
/// <param name="ModelJsonPath">模型描述文件 *.model3.json 的绝对路径。</param>
/// <param name="Directory">模型所在目录的绝对路径。</param>
/// <param name="Identifier">写回 <see cref="EmotionLive2DPetConfig.ModelName"/> 的值（内置模型为目录名，外部模型为绝对路径）。</param>
/// <param name="Location">给用户看的短路径提示（内置模型显示相对路径，外部模型显示所在目录）。</param>
/// <param name="IsBundled">是否位于插件的默认模型目录下。</param>
public sealed record PetModelEntry(
    string Name,
    string ModelJsonPath,
    string Directory,
    string Identifier,
    string Location,
    bool IsBundled)
{
    public override string ToString() => Name;
}
