namespace TriAsr.Application;

public sealed record CanaryRequest(string RuntimeDirectory, string Model, string Audio, string Language, string Backend, int Threads, string Output);
