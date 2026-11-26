using System.Text;

namespace Sample.Core;

/// <summary>URL slugs: lower-case ASCII letters and digits joined by single hyphens.</summary>
public static class Slug
{
    public static string From(string title, int maxLength = 60)
    {
        var sb = new StringBuilder();
        foreach (var c in title.ToLowerInvariant())
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                sb.Append(c);
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }

        var slug = sb.ToString().Trim('-');
        return slug.Length <= maxLength ? slug : slug[..maxLength].TrimEnd('-');
    }
}
