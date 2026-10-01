namespace TriAsr.Application;

/// <summary>Storage configuration boundary; implementations own filesystem access.</summary>
public interface IStoragePaths
{
    string Root { get; }
    string Logs { get; }
    string Database { get; }
}