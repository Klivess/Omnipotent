using System.Text.RegularExpressions;

namespace Omnipotent.Services.CS2ArbitrageBot.Engine
{
    /// <summary>Market segments whose prices behave differently (drift, volatility, how buyers choose).</summary>
    public enum ItemCategory
    {
        Skin,
        Sticker,
        Container,
        Charm,
        Patch,
        Other,
    }

    public static class ItemCategories
    {
        private static readonly Regex Wear = new(@"\((Factory New|Minimal Wear|Field-Tested|Well-Worn|Battle-Scarred)\)$", RegexOptions.Compiled);
        private static readonly Regex ContainerName = new(@"(Case|Capsule|Package|Box|Terminal)$", RegexOptions.Compiled);

        public static ItemCategory Of(string marketHashName)
        {
            if (string.IsNullOrEmpty(marketHashName)) return ItemCategory.Other;
            if (Wear.IsMatch(marketHashName) || marketHashName.StartsWith("★", StringComparison.Ordinal)) return ItemCategory.Skin;
            if (marketHashName.StartsWith("Sticker |", StringComparison.Ordinal)) return ItemCategory.Sticker;
            if (marketHashName.StartsWith("Charm |", StringComparison.Ordinal) || marketHashName.StartsWith("Souvenir Charm |", StringComparison.Ordinal)) return ItemCategory.Charm;
            if (marketHashName.StartsWith("Patch |", StringComparison.Ordinal)) return ItemCategory.Patch;
            if (!marketHashName.Contains('|') && ContainerName.IsMatch(marketHashName)) return ItemCategory.Container;
            return ItemCategory.Other;
        }

        /// <summary>
        /// No float or wear, so every unit is the same and buyers take the cheapest listing. Skins are not:
        /// a buyer may pass over a cheaper listing for a better float, so the queue is softer.
        /// </summary>
        public static bool IsInterchangeable(string marketHashName) => Of(marketHashName) != ItemCategory.Skin;
    }
}
