using TidyUp.Core.Lists;
using TidyUp.Core.Model;
using TidyUp.Core.Planning;
using static TidyUp.Core.Tests.TestData;

namespace TidyUp.Core.Tests;

/// <summary>Each test here is a way the released build could destroy something the player meant to keep.</summary>
public class SafetyRegressionTests
{
    [Fact]
    public void Adding_market_prices_keeps_every_protection_the_scan_found()
    {
        // A copy made by hand once dropped the curated never-touch ids on every real scan.
        var scanned = new ItemContext
        {
            CharacterId = 7, CharacterName = "Someone",
            GearsetItemIds = new HashSet<uint> { 1 }, PlateItemIds = new HashSet<uint> { 2 }, PlatesLoaded = true,
            SeasonalItemIds = new HashSet<uint> { 3 }, ProtectedItemIds = new HashSet<uint> { 4 },
            RetiredCurrencyGearIds = new HashSet<uint> { 5 }, MaxGearsetItemLevel = 690,
        };

        var enriched = scanned.WithMarket(new Dictionary<uint, MarketPrice> { [9] = new(9, 100, 200, DateTimeOffset.UnixEpoch) },
            attempted: true, new Dictionary<uint, bool> { [9] = true });

        foreach (var property in typeof(ItemContext).GetProperties())
        {
            if (property.Name is nameof(ItemContext.MarketPrices) or nameof(ItemContext.MarketLookupAttempted) or nameof(ItemContext.Registered)) continue;
            Assert.Equal(property.GetValue(scanned), property.GetValue(enriched));
        }
        Assert.Contains(4u, enriched.ProtectedItemIds);
        Assert.True(enriched.MarketLookupAttempted);
    }

    [Fact]
    public void Items_listed_only_for_hand_picking_are_not_suggested_and_start_unticked()
    {
        var inputs = new PlannerInputs
        {
            IncludeUnproposed = true,
            Context = Context(),
            Profile = new Profile(),
            InfoLookup = Lookup,
            ProtectList = new ItemList(),
            AlwaysDiscardList = new ItemList(),
            SessionSkips = new HashSet<string>(),
            IsAvailable = (k, _) => k.IsAlwaysLoaded(),
            RetainerNames = new Dictionary<ulong, string>(),
        };
        var items = new[]
        {
            ScannedItem.Simple(Inv(0), 1, 14),   // vendor-only junk: a rule suggests it
            ScannedItem.Simple(Inv(1), 15, 3),   // marketable widget: no rule, listed only
        };

        var plan = new RunPlanner().Build(items, inputs);

        var junk = Assert.Single(plan.AllRows, r => r.Item.ItemId == 1);
        var listed = Assert.Single(plan.AllRows, r => r.Item.ItemId == 15);
        Assert.True(junk.IsSuggested);
        Assert.False(listed.IsSuggested);
        Assert.False(listed.Checked);
        Assert.True(listed.IsExecutable);   // it can still be picked by hand
    }
}
