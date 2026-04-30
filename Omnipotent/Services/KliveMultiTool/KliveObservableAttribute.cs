namespace Omnipotent.Services.KliveMultiTool
{
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class KliveObservableAttribute : Attribute
    {
        public string? Label { get; }

        public KliveObservableAttribute(string? label = null)
        {
            Label = label;
        }
    }
}
