using System.Collections;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Plugins.Samples;
using VisionPlatform.Runtime.Execution;

namespace VisionPlatform.Editor.Core.Examples;

public sealed record ExampleVerification(bool Passed,IReadOnlyList<string> Details)
{
    public string Summary => Passed ? "通过 · 本次实际运行与预期一致" : "未通过 · "+string.Join("；",Details);
}
public static class ExampleVerifier
{
    public static ExampleVerification Verify(ExampleManifest manifest,RunResult run,string? caseId=null,bool communication=false)
    {
        var variant=caseId is null?null:manifest.Cases.Single(c=>c.Id==caseId);
        var checks=communication?manifest.CommunicationExpected:variant?.Checks??manifest.Expected;
        var result=Verify(checks,run.Nodes.ToDictionary(n=>n.Key,n=>n.Value.Outputs),run.Nodes.ToDictionary(n=>n.Key,n=>n.Value.Status.ToString()));
        if(run.Success!=(variant?.ExpectedRunSuccess??true))return new(false,result.Details.Append("流程执行状态不符合预期："+run.Status).ToArray());
        return result;
    }
    public static ExampleVerification Verify(IReadOnlyList<ExampleCheck> checks,IReadOnlyDictionary<string,IReadOnlyDictionary<string,object>> outputs,IReadOnlyDictionary<string,string> statuses)
    {
        var messages=new List<string>();
        if(checks.Count==0)return new(false,["示例没有定义可验证的预期结果。"]);
        foreach(var check in checks)
        {
            var name=check.NodeId+"."+check.Port+(check.Property.Length>0?"."+check.Property:"");
            try
            {
                object? value=check.Port=="$status"?statuses[check.NodeId]:outputs[check.NodeId][check.Port];
                foreach(var part in check.Property.Split('.',StringSplitOptions.RemoveEmptyEntries))
                    value=value switch
                    {
                        IReadOnlyDictionary<string,double> values=>values[part],
                        IReadOnlyDictionary<string,object> values=>values[part],
                        IDictionary values=>values[part],
                        ImageFrame image when part.StartsWith("Pixel[",StringComparison.Ordinal)&&part.EndsWith(']')=>Pixel(image,part),
                        null=>throw new InvalidDataException("属性为空"),
                        _=>value.GetType().GetProperty(part)?.GetValue(value)??throw new InvalidDataException("属性不存在："+part)
                    };
                bool equal;
                if(check.Expected.ValueKind==JsonValueKind.Number)
                {
                    var actual=Convert.ToDouble(value,CultureInfo.InvariantCulture);var expected=check.Expected.GetDouble();
                    equal=double.IsFinite(actual)&&double.IsFinite(expected)&&double.IsFinite(check.Tolerance)&&check.Tolerance>=0&&Math.Abs(actual-expected)<=check.Tolerance;
                }
                else if(check.Expected.ValueKind is JsonValueKind.True or JsonValueKind.False)equal=value is bool b&&b==check.Expected.GetBoolean();
                else equal=string.Equals(Convert.ToString(value,CultureInfo.InvariantCulture),check.Expected.GetString(),StringComparison.Ordinal);
                if(!equal)messages.Add(name+" 实际="+Convert.ToString(value,CultureInfo.InvariantCulture)+"，预期="+check.Expected.GetRawText());
            }
            catch(Exception ex){messages.Add(name+"："+ex.Message);}
        }
        return new(messages.Count==0,messages.AsReadOnly());
    }
    private static byte Pixel(ImageFrame image,string property)
    {
        var coordinates=property[6..^1].Split(',');
        if(coordinates.Length is <2 or >3)throw new InvalidDataException("像素检查需要Pixel[x,y]或Pixel[x,y,channel]。");
        var x=int.Parse(coordinates[0],CultureInfo.InvariantCulture);var y=int.Parse(coordinates[1],CultureInfo.InvariantCulture);
        var channel=coordinates.Length==3?int.Parse(coordinates[2],CultureInfo.InvariantCulture):0;
        var channels=image.PixelFormat is PixelFormat.RGB8 or PixelFormat.BGR8?3:1;
        if(x<0||x>=image.Width||y<0||y>=image.Height||channel<0||channel>=channels)throw new InvalidDataException("像素检查坐标越界。");
        using var lease=image.Lease();return lease.GetRowSpan(y)[x*channels+channel];
    }
}

/// <summary>Explicit one-test loopback scope. Loading/opening examples never calls this method.</summary>
public static class LocalTcpExample
{
    public static async Task<string> RunAsync(Func<int,CancellationToken,Task> execute,CancellationToken ct=default)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start(1);
        Task<string>? received=null;
        try
        {
            var port=((IPEndPoint)listener.LocalEndpoint).Port;
            received=Receive();await execute(port,timeout.Token).ConfigureAwait(false);
            return await received.ConfigureAwait(false);
            async Task<string> Receive()
            {
                using var client=await listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(false);
                using var stream=client.GetStream();var data=new List<byte>();var buffer=new byte[1];
                while(data.Count<16384)
                {
                    if(await stream.ReadAsync(buffer,timeout.Token).ConfigureAwait(false)==0)throw new IOException("本地接收端在完整消息前断开。");
                    if(buffer[0]==(byte)'\n')return Encoding.UTF8.GetString(data.ToArray());data.Add(buffer[0]);
                }
                throw new InvalidDataException("本地示例消息超过16KiB。");
            }
        }
        finally
        {
            timeout.Cancel();listener.Stop();
            if(received is not null)try{await received.ConfigureAwait(false);}catch(Exception){}
        }
    }
}
