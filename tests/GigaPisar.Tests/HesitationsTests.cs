using GigaPisar.App;
using Xunit;

namespace GigaPisar.Tests;

/// <summary>Hesitations: "э-э" and "м-м" leave the take, real words stay.</summary>
public class HesitationsTests
{
    [Theory]
    [InlineData("Вот. А-а просто сразу, чтобы они мгновенно появлялись", "Вот. Просто сразу, чтобы они мгновенно появлялись")]
    [InlineData("А-а, понял тебя.", "Понял тебя.")]
    [InlineData("Ты где? А-а, я уже тут.", "Ты где? Я уже тут.")]
    [InlineData("Эм, ну ладно.", "Ну ладно.")]
    [InlineData("Э-э... давай завтра", "Давай завтра")]
    [InlineData("Первая\nМ-м, вторая", "Первая\nВторая")]
    public void SoundAtTheStartOfASentenceGoesWithItsPunctuation(string said, string expected) =>
        Assert.Equal(expected, Hesitations.Removed(said));

    [Theory]
    [InlineData("Есть сообщения, э-э, которые мы не сокращаем.", "Есть сообщения, которые мы не сокращаем.")]
    [InlineData("Я эээ думаю ммм завтра", "Я думаю завтра")]
    [InlineData("Давай завтра, э-э.", "Давай завтра.")]
    [InlineData("Давай завтра, э-э-э", "Давай завтра")]
    [InlineData("Купи хлеб, э-э, м-м, молоко", "Купи хлеб, молоко")]
    public void SoundInsideASentenceLeavesOneComma(string said, string expected) =>
        Assert.Equal(expected, Hesitations.Removed(said));

    [Theory]
    [InlineData("Э-э-э.")]
    [InlineData("М-м, э-э...")]
    public void NothingButSoundsIsAnEmptyTake(string said) =>
        Assert.Equal("", Hesitations.Removed(said));

    [Theory]
    [InlineData("А я думаю, что да.")]
    [InlineData("Длина 5 мм.")]
    [InlineData("Это эхо, поэтому мама ушла.")]
    [InlineData("Скажи «э-э» вслух.")]
    [InlineData("Привет, как дела?")]
    public void WordsAndUnitsStayAsTheyAre(string said) =>
        Assert.Equal(said, Hesitations.Removed(said));
}
