namespace VisionPlatform.Contracts.Collections;
/// <summary>A typed scalar association within one execution, not a tracked physical target.</summary>
public sealed record ScalarItem(ItemOrigin Source,CollectionValue Value):VisionItem(Source)
{
    public override VisionItemKind Kind=>VisionItemKind.Scalar;
    public override IReadOnlyDictionary<string,CollectionValue> Fields=>Data((Value.Number is not null?"Number":Value.Boolean is not null?"Boolean":"Text",Value),("Index",Origin.SourceIndex));
}
