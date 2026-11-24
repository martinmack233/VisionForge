using System;
using System.IO;
using System.Reflection;
public static class Program
{
    public static int Main(string[] args)
    {
        Console.WriteLine("Runtime="+Environment.Version+"; x64="+Environment.Is64BitProcess);
        try
        {
            var assembly=Assembly.LoadFrom(Path.GetFullPath(args[0]));
            Console.WriteLine("Loaded="+assembly.FullName);
            var api=assembly.GetType("BoqApi_CSharp.BoqApi", true);
            Console.WriteLine("PublicApi="+api.FullName);
            var init=api.GetMethod("Init", BindingFlags.Public|BindingFlags.Static);
            Console.WriteLine("Init="+init.Invoke(null,null));
            try
            {
                var discover=api.GetMethod("DiscoverCamera", BindingFlags.Public|BindingFlags.Static);
                object[] parameters={0};
                var devices=(Array)discover.Invoke(null,parameters);
                Console.WriteLine("DiscoverCount="+parameters[0]+"; ArrayLength="+(devices==null ? -1 : devices.Length));
                if (devices!=null) foreach(var device in devices)
                    foreach(var field in device.GetType().GetFields()) if(field.Name=="Serial" || field.Name=="ModelName" || field.Name=="FactoryName" || field.Name=="UserName") Console.WriteLine(field.Name+"="+field.GetValue(device));
            }
            finally { Console.WriteLine("Deinit="+api.GetMethod("Deinit",BindingFlags.Public|BindingFlags.Static).Invoke(null,null)); }
            return 0;
        }
        catch(Exception ex)
        {
            Console.WriteLine(ex.ToString());
            return 2;
        }
    }
}

