using System.Text.Json;
namespace VisionPlatform.Contracts.Nodes;
/// <summary>Pure parameter metadata. Repeated conditions are AND; allowed values in one condition are OR.</summary>
[AttributeUsage(AttributeTargets.Property,AllowMultiple=true)]
public sealed class AppliesWhenAttribute(string path,params string[] values):Attribute
{public string Path{get;}=path;public IReadOnlyList<string> Values{get;}=Array.AsReadOnly(values);}
/// <summary>Additive conditional input validation; descriptors retain optional ports for old graph consumers.</summary>
public interface IParameterDependentInputs
{IReadOnlyList<string> RequiredInputs(JsonElement parameters);}
