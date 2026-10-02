using System.Reflection;

namespace TriAsr.App;

/// <summary>Display metadata comes from the same version used by the installer.</summary>
public static class AppInfo
{
    public static string Name => Edition.ProductName;
    public static string Version => typeof(AppInfo).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";
    public static string VersionLabel => $"v{Version}";
    public static string WindowTitle => $"{Name} · v{Version}";
    /// <summary>The privacy policy in the interface language (the English one where it has not been translated).</summary>
    public static string PrivacyPolicy => Loc.PrivacyPolicy(Loc.Instance.Language);
}
