using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Plugins.RouterTasks;

// 仅提取已知 acw_sc__v2 验证格式的数据，不执行页面 JavaScript。
internal static class UsageLogWaf
{
    private const int MaxHtmlBytes = 256 * 1024;
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789+/";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly Regex Scripts = Pattern(@"<script\b([^>]*)>([\s\S]*?)</script\s*>", RegexOptions.IgnoreCase);
    private static readonly Regex External = Pattern(@"\bsrc\s*=", RegexOptions.IgnoreCase);
    private static readonly Regex Seed = Pattern("\\b(?:var|let|const)\\s+arg1\\s*=\\s*(['\"])([^'\"]*)\\1");
    private static readonly Regex Table = Pattern(@"\b(?:var|let|const)\s+N\s*=\s*\[([^\]]*)\]");
    private static readonly Regex Permutation = Pattern(@"\b(?:var|let|const)\s+m\s*=\s*\[([^\]]*)\]");
    private static readonly Regex Hex40 = Pattern(@"\A[0-9a-f]{40}\z", RegexOptions.IgnoreCase);
    private static readonly Regex QuotedToken = Pattern("\\A(['\"])([a-zA-Z0-9+/=]*)\\1\\z");
    private static readonly Regex EncodedToken = Pattern(@"\A[a-zA-Z0-9+/]{1,2048}={0,2}\z");
    private static readonly Regex Position = Pattern(@"\A(?:0x[0-9a-f]{1,2}|[1-9][0-9]?)\z", RegexOptions.IgnoreCase);
    private static readonly Regex ChallengeMarker = Pattern(
        @"\bacw_sc__v2\b|\barg1\b|aliyun_waf|cf-chl-|challenge-platform|turnstile|captcha|访问验证|验证码|请按住滑块", RegexOptions.IgnoreCase);

    private static Regex Pattern(string pattern, RegexOptions options = RegexOptions.None)
        => new(pattern, options | RegexOptions.CultureInvariant, RegexTimeout);

    internal static bool IsChallenge(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return false;
        // 合法日志 JSON 的 content 可能包含 captcha 等文本，不能误作验证页面。
        var trimmed = html.AsSpan();
        while (!trimmed.IsEmpty && (char.IsWhiteSpace(trimmed[0]) || trimmed[0] == '\uFEFF')) trimmed = trimmed[1..];
        if (trimmed.IsEmpty || trimmed[0] != '<') return false;
        try { return ChallengeMarker.IsMatch(html); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    internal static string? Solve(string html)
    {
        if (html is null || Encoding.UTF8.GetByteCount(html) > MaxHtmlBytes) return null;
        try
        {
            var matches = Scripts.Matches(html);
            if (matches.Count is 0 or > 32) return null;
            var source = string.Join('\n', matches.Cast<Match>()
                .Where(match => !External.IsMatch(match.Groups[1].Value)).Select(match => match.Groups[2].Value));
            if (!source.Contains("acw_sc__v2", StringComparison.Ordinal)) return null;
            var seed = Unique(source, Seed, 2);
            var table = Unique(source, Table, 1);
            var permutation = Unique(source, Permutation, 1);
            if (seed is null || !Hex40.IsMatch(seed) || table is null || permutation is null) return null;

            var tokens = Entries(table);
            if (tokens.Length is 0 or > 2048) return null;
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in tokens)
            {
                var match = QuotedToken.Match(token);
                if (!match.Success || match.Groups[2].Length > 2048) return null;
                var key = Decode(match.Groups[2].Value);
                if (Hex40.IsMatch(key) && !key.Equals(seed, StringComparison.OrdinalIgnoreCase)) keys.Add(key);
            }
            if (keys.Count != 1) return null;

            var entries = Entries(permutation);
            if (entries.Length != 40) return null;
            var mapping = new int[40];
            for (var i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                if (!Position.IsMatch(entry)) return null;
                mapping[i] = entry.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? int.Parse(entry.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                    : int.Parse(entry, CultureInfo.InvariantCulture);
            }
            if (mapping.Any(value => value is < 1 or > 40) || mapping.Distinct().Count() != 40) return null;
            var shuffled = new string(mapping.Select(position => seed[position - 1]).ToArray());
            var mask = keys.Single();
            var cookie = new StringBuilder("acw_sc__v2=");
            for (var i = 0; i < 40; i += 2)
            {
                var value = int.Parse(shuffled.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                    ^ int.Parse(mask.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                cookie.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            }
            return cookie.ToString();
        }
        catch (RegexMatchTimeoutException) { return null; }
    }

    private static string? Unique(string source, Regex regex, int group)
    {
        var matches = regex.Matches(source);
        return matches.Count == 1 ? matches[0].Groups[group].Value : null;
    }

    private static string[] Entries(string source)
    {
        var value = source.Trim();
        if (value.EndsWith(',')) value = value[..^1].TrimEnd();
        return value.Split(',').Select(item => item.Trim()).ToArray();
    }

    private static string Decode(string encoded)
    {
        if (!EncodedToken.IsMatch(encoded)) return "";
        var data = encoded.TrimEnd('=');
        if (data.Length % 4 == 1) return "";
        var bits = 0;
        var buffer = 0;
        var decoded = new StringBuilder();
        foreach (var character in data)
        {
            buffer = buffer * 64 + Alphabet.IndexOf(character);
            bits += 6;
            if (bits < 8) continue;
            bits -= 8;
            decoded.Append((char)(buffer >> bits));
            buffer &= (1 << bits) - 1;
        }
        return buffer == 0 ? decoded.ToString() : "";
    }
}
