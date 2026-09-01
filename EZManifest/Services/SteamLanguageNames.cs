using System.Text.RegularExpressions;

namespace EZManifest.Services;

internal static class SteamLanguageNames
{
    private static readonly Regex LocaleSuffixRegex = new(
        @"-\s*([a-z]{2})(?:_([A-Za-z]{2}))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly (string Code, string Token)[] Tokens =
    [
        ("brazilian", "portuguese - brazil"),
        ("brazilian", "portuguese brazil"),
        ("latam", "spanish - latin america"),
        ("latam", "spanish latin america"),
        ("schinese", "simplified chinese"),
        ("tchinese", "traditional chinese"),
        ("latam", "latin america"),
        ("brazilian", "pt-br"),
        ("brazilian", "pt_br"),
        ("schinese", "zh-cn"),
        ("tchinese", "zh-tw"),
        ("tchinese", "zh-hk"),
        ("schinese", "schinese"),
        ("tchinese", "tchinese"),
        ("koreana", "koreana"),
        ("brazilian", "brazilian"),
        ("portuguese", "portuguese"),
        ("indonesian", "indonesian"),
        ("hungarian", "hungarian"),
        ("bulgarian", "bulgarian"),
        ("ukrainian", "ukrainian"),
        ("vietnamese", "vietnamese"),
        ("norwegian", "norwegian"),
        ("romanian", "romanian"),
        ("japanese", "japanese"),
        ("schinese", "chinese"),
        ("korean", "korean"),
        ("koreana", "korean"),
        ("english", "english"),
        ("spanish", "spanish"),
        ("finnish", "finnish"),
        ("swedish", "swedish"),
        ("turkish", "turkish"),
        ("italian", "italian"),
        ("russian", "russian"),
        ("german", "german"),
        ("french", "french"),
        ("danish", "danish"),
        ("polish", "polish"),
        ("arabic", "arabic"),
        ("dutch", "dutch"),
        ("greek", "greek"),
        ("czech", "czech"),
        ("thai", "thai"),
        ("latam", "latam")
    ];

    public static string? InferFromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        string trimmed = name.Trim();
        Match suffix = LocaleSuffixRegex.Match(trimmed);
        if (suffix.Success)
            return MapIso(suffix.Groups[1].Value, suffix.Groups[2].Value);

        string lower = trimmed.ToLowerInvariant();
        foreach (var (code, token) in Tokens)
        {
            if (lower == token || lower == $"{token} depot")
                return NormalizeCode(code);
        }

        foreach (var (code, token) in Tokens)
        {
            if (lower.EndsWith($" - {token}", StringComparison.Ordinal)
                || lower.EndsWith($" - {token} depot", StringComparison.Ordinal)
                || lower.EndsWith($" {token}", StringComparison.Ordinal)
                || lower.EndsWith($" {token} depot", StringComparison.Ordinal)
                || lower.EndsWith($" ({token})", StringComparison.Ordinal)
                || lower.EndsWith($"_{token}", StringComparison.Ordinal))
            {
                return NormalizeCode(code);
            }
        }

        return null;
    }

    private static string NormalizeCode(string code) =>
        code == "korean" ? "koreana" : code;

    private static string? MapIso(string language, string region)
    {
        language = language.ToLowerInvariant();
        region = region.ToLowerInvariant();
        return (language, region) switch
        {
            ("pt", "br") => "brazilian",
            ("es", "mx") or ("es", "419") => "latam",
            ("zh", "cn") or ("zh", "hans") => "schinese",
            ("zh", "tw") or ("zh", "hk") or ("zh", "hant") => "tchinese",
            ("ko", _) => "koreana",
            ("en", _) => "english",
            _ => language.Length == 2 ? language : null
        };
    }
}
