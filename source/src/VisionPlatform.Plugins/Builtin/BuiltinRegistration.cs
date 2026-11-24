using VisionPlatform.Runtime.Graph;

namespace VisionPlatform.Plugins.Builtin;

public static class BuiltinRegistration
{
    public static NodeRegistry CreateRegistry()
    {
        var registry = new NodeRegistry();
        registry.Discover(typeof(BuiltinRegistration).Assembly);
        return registry;
    }
}
