using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Plugins.RouterTasks;

namespace RouterTasks.Tests;

[TestClass]
public sealed class UsageLogWafTests
{
    private const string SyntheticSeed = "0123456789abcdef0123456789abcdef01234567";
    private const string SyntheticMask = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly int[] Identity = Enumerable.Range(1, 40).ToArray();

    private static string Encode(string text) => new(Convert.ToBase64String(Encoding.ASCII.GetBytes(text))
        .Select(character => char.IsAsciiLetterLower(character) ? char.ToUpperInvariant(character)
            : char.IsAsciiLetterUpper(character) ? char.ToLowerInvariant(character) : character).ToArray());

    private static string Challenge(string seed = SyntheticSeed, string[]? keys = null, int[]? mapping = null)
        => $"<html><script>var arg1='{seed}';var N=[{string.Join(',', new[] { "noise" }.Concat(keys ?? [SyntheticMask]).Select(value => $"'{Encode(value)}'"))}];var m=[{string.Join(',', (mapping ?? Identity).Select(value => "0x" + value.ToString("x")))}];document.cookie='acw_sc__v2=';</script></html>";

    [TestMethod]
    public void SyntheticIdentityAndReversePermutationsProduceExpectedCookies()
    {
        Assert.AreEqual("acw_sc__v2=ab89efcd23016745ab89efcd23016745ab89efcd", UsageLogWaf.Solve(Challenge()));
        Assert.AreEqual("acw_sc__v2=dcfe98ba54761032dcfe98ba54761032dcfe98ba",
            UsageLogWaf.Solve(Challenge(mapping: Identity.Reverse().ToArray())));
        Assert.AreEqual("acw_sc__v2=" + SyntheticMask, UsageLogWaf.Solve(Challenge(seed: new string('0', 40))));
    }

    [TestMethod]
    public void SupportsWhitespaceQuotesDecimalPositionsAndTrailingCommas()
    {
        var html = $"<script>\nconst arg1 = \"{SyntheticSeed}\";\nlet N = [ \"{Encode("noise")}\", \"{Encode(SyntheticMask)}\", ];\nvar m = [ {string.Join(", ", Identity)}, ];\n'acw_sc__v2';</script>";
        Assert.AreEqual(UsageLogWaf.Solve(Challenge()), UsageLogWaf.Solve(html));
    }

    [TestMethod]
    public void ParsesDataWithoutEvaluatingInlineScriptsAndIgnoresExternalScripts()
    {
        var html = Challenge().Replace("</script>", "; throw Error('REMOTE'); fetch('https://untrusted.invalid');</script>", StringComparison.Ordinal);
        Assert.AreEqual(UsageLogWaf.Solve(Challenge()), UsageLogWaf.Solve(html));
        Assert.IsNull(UsageLogWaf.Solve(html.Replace("<script>", "<script src='https://untrusted.invalid/code.js'>", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void RejectsAmbiguousSeedTablePermutationAndMask()
    {
        var html = Challenge();
        var values = new[]
        {
            html + html,
            html.Replace("var N=[", "var N=[];var N=[", StringComparison.Ordinal),
            html.Replace("var m=[", "var m=[];var m=[", StringComparison.Ordinal),
            Challenge(keys: []), Challenge(keys: [SyntheticSeed]), Challenge(keys: ["invalid"]),
            Challenge(keys: [SyntheticMask, new string('b', 40)])
        };
        foreach (var value in values) Assert.IsNull(UsageLogWaf.Solve(value));
        Assert.AreEqual(UsageLogWaf.Solve(html), UsageLogWaf.Solve(Challenge(keys: [SyntheticMask, SyntheticMask.ToUpperInvariant()])));
    }

    [TestMethod]
    public void RejectsInvalidSeedsPositionsAndEncodedExpressions()
    {
        var html = Challenge();
        var values = new[]
        {
            Challenge(seed: new string('f', 39)), Challenge(seed: new string('G', 40)),
            Challenge(mapping: Identity.Skip(1).ToArray()), Challenge(mapping: Enumerable.Repeat(1, 40).ToArray()),
            Challenge(mapping: [0, .. Identity.Skip(1)]), Challenge(mapping: [41, .. Identity.Skip(1)]),
            html.Replace("0x1,", "1+0,", StringComparison.Ordinal),
            html.Replace("0x1,", "1\u0660,", StringComparison.Ordinal),
            html.Replace($"'{Encode(SyntheticMask)}'", "fetch('https://untrusted.invalid')", StringComparison.Ordinal),
            html.Replace("var N=[", "var N=['" + new string('a', 2049) + "',", StringComparison.Ordinal)
        };
        foreach (var value in values) Assert.IsNull(UsageLogWaf.Solve(value));
    }

    [TestMethod]
    public void BoundsUtf8BytesAndInlineScriptCount()
    {
        Assert.IsNull(UsageLogWaf.Solve(Challenge() + new string('x', 256 * 1024)));
        Assert.IsNull(UsageLogWaf.Solve(Challenge() + new string('汉', 90 * 1024)));
        Assert.IsNull(UsageLogWaf.Solve(Challenge() + string.Concat(Enumerable.Repeat("<script>noise</script>", 32))));
        Assert.IsNotNull(UsageLogWaf.Solve(Challenge() + string.Concat(Enumerable.Repeat("<script>noise</script>", 31))));
    }

    [TestMethod]
    public void ChallengeDetectionRequiresHtmlAndNeverMisclassifiesLogJson()
    {
        Assert.IsTrue(UsageLogWaf.IsChallenge(Challenge()));
        foreach (var marker in new[] { "cf-chl-", "challenge-platform", "turnstile", "captcha", "访问验证", "验证码", "请按住滑块", "aliyun_waf" })
        {
            Assert.IsTrue(UsageLogWaf.IsChallenge("\uFEFF \n<html>" + marker + "</html>"));
            Assert.IsFalse(UsageLogWaf.IsChallenge("{\"success\":true,\"data\":[{\"content\":\"" + marker + "\"}]}"));
        }
        Assert.IsFalse(UsageLogWaf.IsChallenge("<html>regular site page</html>"));
        Assert.IsFalse(UsageLogWaf.IsChallenge("captcha"));
        Assert.IsNull(UsageLogWaf.Solve("<html>captcha</html>"));
        Assert.IsNull(UsageLogWaf.Solve(""));
    }
}
