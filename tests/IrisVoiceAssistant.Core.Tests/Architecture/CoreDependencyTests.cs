using IrisVoiceAssistant.Core.Audio;

namespace IrisVoiceAssistant.Core.Tests.Architecture;

/// <summary>
/// Guards AGENTS.md §3: the Core assembly must stay independent of platform, infrastructure and AI runtimes.
/// </summary>
public sealed class CoreDependencyTests
{
    [Fact]
    public void Core_references_only_the_base_class_library()
    {
        var references = typeof(AudioStreamKind).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToArray();

        var unexpected = references
            .Where(name => !name.StartsWith("System", StringComparison.Ordinal)
                        && name != "netstandard"
                        && name != "mscorlib")
            .ToArray();

        Assert.True(unexpected.Length == 0, $"Core has unexpected references: {string.Join(", ", unexpected)}");
    }
}
