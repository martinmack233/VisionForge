using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Runtime.Execution;
using Xunit;

namespace VisionPlatform.Editor.Core.Tests;

public sealed class ArchitectureTests
{
    [Theory]
    [InlineData("contracts")]
    [InlineData("runtime")]
    [InlineData("editor-core")]
    public void HeadlessModulesDoNotReferenceDesktopFrameworks(string module)
    {
        var assembly = module == "contracts" ? typeof(IExecutableNode).Assembly
            : module == "runtime" ? typeof(RuntimeExecutor).Assembly : typeof(IEditorSession).Assembly;
        var references = assembly.GetReferencedAssemblies().Select(r => r.Name!).ToArray();
        Assert.DoesNotContain(references, n => n is "Nodify" or "PresentationCore" or "PresentationFramework" or "WindowsBase");
    }
}
