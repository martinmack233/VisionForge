using VisionPlatform.Contracts.Nodes;
namespace VisionPlatform.Communication;
/// <summary>New unified creators cannot silently reuse another node's named resource. Explicit output references grant subsequent use.</summary>
public sealed class ResourceOwnership
{
    private readonly Dictionary<string,string> _owners=new(StringComparer.Ordinal);private readonly object _gate=new();
    public static void Claim(IExecutionServices? services,string key,string owner)
    {
        if(services is null)throw new InvalidOperationException("通信资源需要Runtime会话。");var ledger=services.GetOrCreate("vision.communication.owners.v1",()=>new ResourceOwnership());
        lock(ledger._gate){if(ledger._owners.TryGetValue(key,out var existing)&&existing!=owner)throw new InvalidOperationException("同名通信资源由另一个节点创建；请使用它的显式输出引用，或为当前节点设置不同名称。");if(!ledger._owners.ContainsKey(key)&&ledger._owners.Count>=128)throw new InvalidOperationException("会话通信创建者记录128项已满；请关闭工程会话后重开。");ledger._owners[key]=owner;}
    }
}
