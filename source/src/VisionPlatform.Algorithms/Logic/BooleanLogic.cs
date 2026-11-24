using System.ComponentModel;

namespace VisionPlatform.Algorithms.Logic;

public sealed record LogicBinaryOptions
{
    [DisplayName("输入 A（无端口输入时）")]
    public bool ValueA { get; init; }
    [DisplayName("输入 B（无端口输入时）")]
    public bool ValueB { get; init; }
}

public sealed record LogicNotOptions
{
    [DisplayName("输入值（无端口输入时）")]
    public bool Value { get; init; }
}

/// <summary>Pure operations on two already-materialized Boolean inputs. Local &&/|| short-circuit
/// does not change upstream DAG scheduling. Gate side-effecting branches with IfElse controls.</summary>
public static class BooleanLogic
{
    public static bool And(bool valueA, bool valueB) => valueA && valueB;
    public static bool Or(bool valueA, bool valueB) => valueA || valueB;
    public static bool Not(bool value) => !value;
}
