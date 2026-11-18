namespace Omnipotent.Services.CS2ArbitrageBot.Engine
{
    /// <summary>How an attempt to list a unit on the Steam Market ended.</summary>
    public enum SteamSellOutcome
    {
        /// <summary>Listed (into the buy order) and confirmed.</summary>
        Listed,
        /// <summary>No working Steam login: only Klives can approve one, so this is an outage, not a failed sale.</summary>
        LoginUnavailable,
        /// <summary>Steam's inventory could not be read (rate limit, server error): try again later.</summary>
        InventoryUnavailable,
        /// <summary>The unit is in the inventory but still under a trade/market hold.</summary>
        NotMarketableYet,
        /// <summary>The inventory was read and the unit is not in it (sold or moved by hand?).</summary>
        NotInInventory,
        /// <summary>Steam refused the listing.</summary>
        Refused,
        /// <summary>Listed, but the mobile confirmation was not completed.</summary>
        NotConfirmed,
    }

    public readonly record struct SteamSellResult(SteamSellOutcome Outcome, string Detail)
    {
        public bool Listed => Outcome == SteamSellOutcome.Listed;

        /// <summary>
        /// Whether this counts towards the position's failed-sale limit. Outages (no login, Steam's inventory down, an
        /// unlock that has not landed yet) say nothing about the position: counting them is how a dead Steam login
        /// turned every waiting item into a "needs a manual look" alert.
        /// </summary>
        public bool CountsAsFailedAttempt => Outcome is SteamSellOutcome.NotInInventory or SteamSellOutcome.Refused or SteamSellOutcome.NotConfirmed;

        public static SteamSellResult Ok() => new(SteamSellOutcome.Listed, "listed");
    }
}
