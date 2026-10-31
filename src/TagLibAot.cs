using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace LiteSonic;

/// <summary>
/// TagLib# istanzia i lettori dei formati via reflection (Activator.CreateInstance).
/// Con trimming / Native AOT i costruttori verrebbero rimossi: li preserviamo esplicitamente.
/// </summary>
static class TagLibAot
{
    const DynamicallyAccessedMemberTypes Ctors =
        DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors;

    [ModuleInitializer]
    [DynamicDependency(Ctors, typeof(TagLib.Mpeg.AudioFile))]
    [DynamicDependency(Ctors, typeof(TagLib.Flac.File))]
    [DynamicDependency(Ctors, typeof(TagLib.Ogg.File))]
    [DynamicDependency(Ctors, typeof(TagLib.Mpeg4.File))]
    [DynamicDependency(Ctors, typeof(TagLib.Aac.File))]
    [DynamicDependency(Ctors, typeof(TagLib.Riff.File))]
    [DynamicDependency(Ctors, typeof(TagLib.Asf.File))]
    [DynamicDependency(Ctors, typeof(TagLib.Ape.File))]
    [DynamicDependency(Ctors, typeof(TagLib.WavPack.File))]
    [DynamicDependency(Ctors, typeof(TagLib.Aiff.File))]
    [DynamicDependency(Ctors, typeof(TagLib.Dsf.File))]
    [DynamicDependency(Ctors, typeof(TagLib.MusePack.File))]
    internal static void Preserve() { }
}
