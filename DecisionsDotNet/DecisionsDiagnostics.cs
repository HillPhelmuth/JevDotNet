namespace DecisionsDotNet;

/// <summary>Diagnostic IDs for parts of the SDK marked experimental. They may change in a minor release.</summary>
internal static class DecisionsDiagnostics
{
    /// <summary>The parts of the semantic conditional layer still settling: decision sets, collection verbs, the client pipeline, and the add-on packages' helpers.</summary>
    internal const string Conditions = "DECISIONS002";

    internal const string UrlFormat = "https://github.com/HillPhelmuth/JevDotNet/blob/main/CHANGELOG.md#experimental-apis";
}
