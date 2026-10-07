using System.Reflection;

namespace KeyNexus.Core;

public static class AppVersion
{
    public static string Current
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "1.0.0";
        }
    }
}
