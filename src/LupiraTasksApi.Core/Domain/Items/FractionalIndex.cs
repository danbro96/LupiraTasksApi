namespace LupiraTasksApi.Domain.Items;

/// <summary>
/// Order-key arithmetic for <see cref="ItemState.SortOrder"/>: a wire-compatible port of the
/// <c>fractional-indexing</c> npm package the app clients use, so a key minted server-side (MCP) interleaves with
/// keys minted on a phone and both sort identically under ordinal comparison.
///
/// A key is an integer part — a head char encoding its digit count (<c>a</c>..<c>z</c> = 1..26 digits for
/// non-negative, <c>Z</c>..<c>A</c> for negative) followed by base-62 digits — plus an optional fractional tail. A
/// key can always be produced strictly between two distinct keys, so an insert or reorder never renumbers
/// siblings, and appending stays short: 500 appends from empty reach 3 characters.
/// </summary>
public static class FractionalIndex
{
    private const string Digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private const string SmallestInteger = "A00000000000000000000000000";

    /// <summary>The key for the first item in an empty group.</summary>
    public const string First = "a0";

    /// <summary>A key strictly after <paramref name="a"/>, or <see cref="First"/> when it is <c>null</c>.</summary>
    public static string KeyAfter(string? a) => KeyBetween(a, null);

    /// <summary>
    /// A key strictly between <paramref name="a"/> and <paramref name="b"/>; <c>null</c> means
    /// "unbounded" on that side. Throws <see cref="ArgumentException"/> for a malformed key or when
    /// <paramref name="a"/> is not ordinally less than <paramref name="b"/>.
    /// </summary>
    public static string KeyBetween(string? a, string? b)
    {
        if (a is not null) ValidateOrderKey(a);
        if (b is not null) ValidateOrderKey(b);
        if (a is not null && b is not null && string.CompareOrdinal(a, b) >= 0)
            throw new ArgumentException($"order keys out of order: '{a}' >= '{b}'.", nameof(a));

        if (a is null)
        {
            if (b is null) return First;
            var integerB = IntegerPart(b);
            if (integerB == SmallestInteger) return integerB + Midpoint(string.Empty, b[integerB.Length..]);
            if (string.CompareOrdinal(integerB, b) < 0) return integerB;
            return DecrementInteger(integerB)
                ?? throw new ArgumentException($"no key exists below '{b}'.", nameof(b));
        }

        if (b is null)
        {
            var integerA = IntegerPart(a);
            return IncrementInteger(integerA) ?? integerA + Midpoint(a[integerA.Length..], null);
        }

        var ia = IntegerPart(a);
        var ib = IntegerPart(b);
        if (ia == ib) return ia + Midpoint(a[ia.Length..], b[ib.Length..]);

        var incremented = IncrementInteger(ia)
            ?? throw new ArgumentException($"no key exists above '{a}'.", nameof(a));
        return string.CompareOrdinal(incremented, b) < 0 ? incremented : ia + Midpoint(a[ia.Length..], null);
    }

    /// <summary>
    /// True when <paramref name="key"/> is a well-formed order key. Lets callers skip keys minted by
    /// another scheme — the DAV seam's <c>'~'+guid</c> — instead of throwing on them.
    /// </summary>
    public static bool IsValid(string? key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        try
        {
            ValidateOrderKey(key);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The shortest fractional tail strictly between two tails (<paramref name="b"/> null = unbounded).</summary>
    private static string Midpoint(string a, string? b)
    {
        var zero = Digits[0];
        if (b is not null && string.CompareOrdinal(a, b) >= 0)
            throw new ArgumentException($"order keys out of order: '{a}' >= '{b}'.", nameof(a));
        if ((a.Length > 0 && a[^1] == zero) || (b is { Length: > 0 } && b[^1] == zero))
            throw new ArgumentException("order key has a trailing zero.", nameof(a));

        if (b is not null)
        {
            // Carry the shared prefix over verbatim and recurse on the first differing digit.
            var shared = 0;
            while (shared < b.Length && (shared < a.Length ? a[shared] : zero) == b[shared]) shared++;
            if (shared > 0) return b[..shared] + Midpoint(Rest(a, shared), b[shared..]);
        }

        var digitA = a.Length > 0 ? Digits.IndexOf(a[0]) : 0;
        var digitB = b is not null ? Digits.IndexOf(b[0]) : Digits.Length;
        if (digitB - digitA > 1)
            return Digits[(int) Math.Round(0.5 * (digitA + digitB), MidpointRounding.AwayFromZero)].ToString();
        if (b is { Length: > 1 }) return b[..1];
        // Digits are consecutive with no room between them: keep a's digit and go one place deeper.
        return Digits[digitA] + Midpoint(Rest(a, 1), null);
    }

    private static string Rest(string s, int start) => start >= s.Length ? string.Empty : s[start..];

    private static int IntegerLength(char head) => head switch
    {
        >= 'a' and <= 'z' => head - 'a' + 2,
        >= 'A' and <= 'Z' => 'Z' - head + 2,
        _ => throw new ArgumentException($"invalid order key head: '{head}'.", nameof(head)),
    };

    private static string IntegerPart(string key)
    {
        if (key.Length == 0) throw new ArgumentException("order key is empty.", nameof(key));
        var length = IntegerLength(key[0]);
        if (length > key.Length) throw new ArgumentException($"invalid order key: '{key}'.", nameof(key));
        return key[..length];
    }

    private static void ValidateOrderKey(string key)
    {
        if (key == SmallestInteger) throw new ArgumentException($"invalid order key: '{key}'.", nameof(key));
        var fraction = key[IntegerPart(key).Length..];
        if (fraction.Length > 0 && fraction[^1] == Digits[0])
            throw new ArgumentException($"invalid order key: '{key}'.", nameof(key));
    }

    private static void ValidateInteger(string integer)
    {
        if (integer.Length != IntegerLength(integer[0]))
            throw new ArgumentException($"invalid integer part of an order key: '{integer}'.", nameof(integer));
    }

    /// <summary><c>null</c> when the integer part is already the largest representable one.</summary>
    private static string? IncrementInteger(string integer)
    {
        ValidateInteger(integer);
        var head = integer[0];
        var digits = integer[1..].ToCharArray();

        var carry = true;
        for (var i = digits.Length - 1; carry && i >= 0; i--)
        {
            var next = Digits.IndexOf(digits[i]) + 1;
            if (next == Digits.Length) digits[i] = Digits[0];
            else (digits[i], carry) = (Digits[next], false);
        }

        if (!carry) return head + new string(digits);

        if (head == 'Z') return $"a{Digits[0]}";
        if (head == 'z') return null;
        // Carrying past the head widens (lowercase) or narrows (uppercase) the integer part by a digit.
        var wider = (char) (head + 1);
        var rest = new string(digits);
        return wider + (wider > 'a' ? rest + Digits[0] : rest[..^1]);
    }

    /// <summary><c>null</c> when the integer part is already the smallest representable one.</summary>
    private static string? DecrementInteger(string integer)
    {
        ValidateInteger(integer);
        var head = integer[0];
        var digits = integer[1..].ToCharArray();

        var borrow = true;
        for (var i = digits.Length - 1; borrow && i >= 0; i--)
        {
            var next = Digits.IndexOf(digits[i]) - 1;
            if (next == -1) digits[i] = Digits[^1];
            else (digits[i], borrow) = (Digits[next], false);
        }

        if (!borrow) return head + new string(digits);

        if (head == 'a') return $"Z{Digits[^1]}";
        if (head == 'A') return null;
        var narrower = (char) (head - 1);
        var rest = new string(digits);
        return narrower + (narrower < 'Z' ? rest + Digits[^1] : rest[..^1]);
    }
}
