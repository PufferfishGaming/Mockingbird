using System.Reflection;

namespace TriAsr.App;

/// <summary>Display metadata comes from the same version used by the installer.</summary>
public static class AppInfo
{
    public const string Name = "Mockingbird Studio";
    public static string Version => typeof(AppInfo).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";
    public static string VersionLabel => $"v{Version}";
    public static string WindowTitle => $"{Name} · v{Version}";
    public static string PrivacyPolicy
    {
        get
        {
            using var stream = typeof(AppInfo).Assembly.GetManifestResourceStream("Mockingbird.Privacy.md")
                ?? throw new InvalidOperationException("The privacy policy resource is missing.");
            using var reader = new System.IO.StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}

