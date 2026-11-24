using System.Collections.ObjectModel;

namespace VisionPlatform.Contracts.Numerics;
/// <summary>Immutable row-major finite Float64 mathematical matrix, NOT an image or pixel format.</summary>
public sealed class NumericMatrix
{
    public const int MaximumElements=1_000_000;
    private readonly double[] _values;
    public int Rows{get;}public int Columns{get;}
    public IReadOnlyList<double> Values{get;}
    public NumericMatrix(int rows,int columns,IEnumerable<double> values)
    {
        if(rows<1||columns<1||(long)rows*columns>MaximumElements)throw new ArgumentException("矩阵必须非空，最多1000000个元素。");
        _values=values.Take(checked(rows*columns+1)).ToArray();if(_values.Length!=rows*columns||_values.Any(v=>!double.IsFinite(v)))throw new ArgumentException("矩阵元素数量不符或包含非有限数值。");
        Rows=rows;Columns=columns;Values=new ReadOnlyCollection<double>(_values);
    }
    public double this[int row,int column]=>row>=0&&row<Rows&&column>=0&&column<Columns?_values[row*Columns+column]:throw new ArgumentOutOfRangeException(nameof(row),"矩阵索引从0开始，不能越界。");
    public double[] ToArray()=>(double[])_values.Clone();
    public override string ToString()=>$"Float64 {Rows}×{Columns}（只读，行主序）";
}
