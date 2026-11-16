using System.Text;
using System.Text.RegularExpressions;

namespace pxdNotepad.Editor;

public enum LinkKind { Email, Phone, Web }

/// <summary>A phone number, email address or web link found in a note's text.</summary>
public sealed record FoundLink(int Start, int Length, LinkKind Kind, string Text)
{
    /// <summary>What opens it: mailto:, tel: (digits, + and ;ext=) or the web address.</summary>
    public string Target => Kind switch
    {
        LinkKind.Email => "mailto:" + Text,
        LinkKind.Phone => "tel:" + LinkDetector.Dialable(Text),
        _ => Text.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + Text : Text,
    };
}

/// <summary>Finds phone numbers, email addresses and web links in plain text (nothing is stored: it's found as shown).</summary>
public static partial class LinkDetector
{
    // Web: http(s):// or www. up to whitespace. Email: the usual shape. Phone: optional +country, (area) or area,
    // 3 + 4 digits with spaces, dots or dashes between, optional extension; not inside a longer run of digits/letters.
    [GeneratedRegex(@"(?<web>\b(?:https?://|www\.)[^\s<>""]+)|(?<email>\b[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}\b)|(?<phone>(?<![\w+])(?:\+\d{1,3}[\s.-]?)?(?:\(\d{3}\)\s?|\d{3}[\s.-]?)\d{3}[\s.-]?\d{4}(?:\s*(?:x|ext\.?|extension)\s*\d{1,6})?(?![\w]))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static List<FoundLink> Find(string text)
    {
        var list = new List<FoundLink>();
        if (text.Length < 7) return list;
        foreach (Match m in Pattern().Matches(text))
        {
            if (m.Groups["web"].Success)
            {
                // A sentence's full stop or a closing bracket isn't part of the link.
                var v = m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '\'', '"');
                if (v.Length > 4) list.Add(new FoundLink(m.Index, v.Length, LinkKind.Web, v));
            }
            else if (m.Groups["email"].Success)
                list.Add(new FoundLink(m.Index, m.Length, LinkKind.Email, m.Value));
            else
                list.Add(new FoundLink(m.Index, m.Length, LinkKind.Phone, m.Value.Trim()));
        }
        return list;
    }

    /// <summary>"(555) 123-4567 x204" → "5551234567;ext=204"; "+1 555.123.4567" → "+15551234567".</summary>
    public static string Dialable(string phone)
    {
        var ext = "";
        var m = Regex.Match(phone, @"(?:x|ext\.?|extension)\s*(\d{1,6})\s*$", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            ext = ";ext=" + m.Groups[1].Value;
            phone = phone[..m.Index];
        }
        var sb = new StringBuilder();
        foreach (var ch in phone)
            if (char.IsDigit(ch) || (ch == '+' && sb.Length == 0)) sb.Append(ch);
        return sb + ext;
    }
}
