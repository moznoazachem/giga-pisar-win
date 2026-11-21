using GigaPisar.App;
using Xunit;

namespace GigaPisar.Tests;

/// <summary>Spacing: a dictation right after a word gets a space, anywhere else it does not.</summary>
public class SpacingTests
{
    [Theory]
    [InlineData('т', "То ли")]
    [InlineData('.', "То ли")]
    [InlineData('!', "«Гига» работает")]
    [InlineData('5', "рублей")]
    [InlineData(')', "(ещё)")]
    public void AfterAWordOrAClosingMarkASpaceIsNeeded(char before, string text) =>
        Assert.True(Spacing.NeedsSeparator(before, text));

    [Theory]
    [InlineData(' ', "То ли")]
    [InlineData('\n', "То ли")]
    [InlineData('(', "то ли")]
    [InlineData('«', "Гига")]
    [InlineData('т', ", а потом")]
    [InlineData('т', "")]
    public void AfterASpaceOrAnOpeningMarkNothingIsAdded(char before, string text) =>
        Assert.False(Spacing.NeedsSeparator(before, text));

    [Fact]
    public void AtTheStartOfAFieldNothingIsAdded() =>
        Assert.False(Spacing.NeedsSeparator(null, "То ли"));
}
