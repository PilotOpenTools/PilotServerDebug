using Debug;
using Xunit;

namespace Debug.Tests;

public class WriteGuardTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("test")]
    [InlineData("stage")]
    [InlineData("prod")]
    public void WithoutApplyNothingIsAsked(string? contour)
    {
        Assert.Equal(WriteDecision.ViewOnly, WriteGuard.Decide(contour, apply: false, yes: true, yesProd: true));
        Assert.Equal(WriteDecision.ViewOnly, WriteGuard.Decide(contour, apply: false, yes: false, yesProd: false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("test")]
    [InlineData("stage")]
    [InlineData("unknown")]
    public void NonProdKeepsOldBehaviour(string? contour)
    {
        Assert.Equal(WriteDecision.AskYesNo, WriteGuard.Decide(contour, apply: true, yes: false, yesProd: false));
        Assert.Equal(WriteDecision.Proceed, WriteGuard.Decide(contour, apply: true, yes: true, yesProd: false));
    }

    [Theory]
    [InlineData("prod")]
    [InlineData("PROD")]
    [InlineData(" Prod ")]
    public void ProdAsksForWordAndIgnoresYes(string contour)
    {
        Assert.Equal(WriteDecision.AskProdWord, WriteGuard.Decide(contour, apply: true, yes: false, yesProd: false));
        Assert.Equal(WriteDecision.AskProdWord, WriteGuard.Decide(contour, apply: true, yes: true, yesProd: false));
    }

    [Fact]
    public void YesProdSkipsQuestionOnlyForProd()
    {
        Assert.Equal(WriteDecision.Proceed, WriteGuard.Decide("prod", apply: true, yes: false, yesProd: true));
        Assert.Equal(WriteDecision.AskYesNo, WriteGuard.Decide("test", apply: true, yes: false, yesProd: true));
    }

    [Theory]
    [InlineData("test", "http://a:5545/db", "http://b:5545/db", "prod")]
    [InlineData("stage", "http://a:5545/db", null, "stage")]
    [InlineData("test", "http://a:5545/db", "HTTP://A:5545/db/", "test")]
    [InlineData(null, "http://a:5545/db", "http://b:5545/db", null)]
    [InlineData("test", null, "http://b:5545/db", "prod")]
    public void ServerOverrideDistrustsContour(string? contour, string? caseServer, string? overrideServer, string? expected) =>
        Assert.Equal(expected, WriteGuard.EffectiveContour(contour, caseServer, overrideServer));

    [Fact]
    public void ServerOverrideOfTestCaseAsksProdWord() =>
        Assert.Equal(WriteDecision.AskProdWord,
            WriteGuard.Decide(WriteGuard.EffectiveContour("test", "http://a/db", "http://prod/db"), apply: true, yes: true, yesProd: false));

    [Theory]
    [InlineData("prod", true)]
    [InlineData(" prod ", true)]
    [InlineData("y", false)]
    [InlineData("yes", false)]
    [InlineData("PROD", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyTheExactWordConfirms(string? answer, bool expected) =>
        Assert.Equal(expected, WriteGuard.IsProdWord(answer));
}
