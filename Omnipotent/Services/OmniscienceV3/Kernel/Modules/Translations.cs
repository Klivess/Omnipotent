namespace Omnipotent.Services.OmniscienceV3.Kernel.Modules
{
    /// <summary>A stored translation of one version of a message. The original is never replaced by it.</summary>
    public sealed record Translation(string MessageId, int Version, string SourceLanguage, string Target, string Text);

    /// <summary>
    /// What other modules may ask the translation layer (N19): translations that already exist, for the current version of
    /// each message. Asking never calls a model, so a digest or an extraction can use it freely.
    /// </summary>
    public interface ITranslations
    {
        IReadOnlyDictionary<string, Translation> Cached(IReadOnlyCollection<string> messageIds);
    }
}
