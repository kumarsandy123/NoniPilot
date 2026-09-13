using NoniPilot.Agent.Tools;

namespace NoniPilot.Tests.Unit;

public class FuzzyNameMatcherTests
{
    private sealed record NamedItem(string Name);

    [Fact]
    public void FindBestMatch_ExactMatch_ReturnsIt()
    {
        var items = new[] { new NamedItem("AMBERG IT"), new NamedItem("Downloads") };

        var result = FuzzyNameMatcher.FindBestMatch(items, "AMBERG IT", i => i.Name);

        Assert.Equal("AMBERG IT", result?.Name);
    }

    [Theory]
    [InlineData("amberg it")]
    [InlineData("amber gaiety")]
    [InlineData("ambar ai ti")]
    [InlineData("Amberg-IT")]
    public void FindBestMatch_GarbledVoiceTranscription_StillFindsRealFolder(string garbledHint)
    {
        var items = new[] { new NamedItem("AMBERG IT"), new NamedItem("Downloads"), new NamedItem("Screenshots") };

        var result = FuzzyNameMatcher.FindBestMatch(items, garbledHint, i => i.Name);

        Assert.Equal("AMBERG IT", result?.Name);
    }

    [Fact]
    public void FindBestMatch_NoPlausibleCandidate_ReturnsNull()
    {
        var items = new[] { new NamedItem("Downloads"), new NamedItem("Screenshots") };

        var result = FuzzyNameMatcher.FindBestMatch(items, "completely unrelated xyz", i => i.Name);

        Assert.Null(result);
    }

    [Fact]
    public void FindBestMatch_EmptyHint_ReturnsNull()
    {
        var items = new[] { new NamedItem("AMBERG IT") };

        var result = FuzzyNameMatcher.FindBestMatch(items, "", i => i.Name);

        Assert.Null(result);
    }

    [Fact]
    public void FindBestMatch_EmptyItemList_ReturnsNull()
    {
        var result = FuzzyNameMatcher.FindBestMatch(Array.Empty<NamedItem>(), "AMBERG IT", i => i.Name);

        Assert.Null(result);
    }

    [Fact]
    public void FindBestMatch_DifferentScript_DoesNotFalselyMatch()
    {
        // A raw Devanagari hint has zero character overlap with a Latin-script folder name -
        // this documents the known limitation the system prompt now works around by asking
        // the model to transliterate before calling the tool, rather than silently guessing.
        var items = new[] { new NamedItem("AMBERG IT") };

        var result = FuzzyNameMatcher.FindBestMatch(items, "एमबर्ग आईटी", i => i.Name);

        Assert.Null(result);
    }
}
