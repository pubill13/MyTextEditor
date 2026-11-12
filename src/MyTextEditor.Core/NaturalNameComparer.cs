namespace MyTextEditor.Core;

public sealed class NaturalNameComparer : IComparer<string>
{
    public static NaturalNameComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null) return x is null ? y is null ? 0 : -1 : 1;
        var a = 0;
        var b = 0;
        while (a < x.Length && b < y.Length)
        {
            if (char.IsAsciiDigit(x[a]) && char.IsAsciiDigit(y[b]))
            {
                var startA = a;
                var startB = b;
                while (a < x.Length && char.IsAsciiDigit(x[a])) a++;
                while (b < y.Length && char.IsAsciiDigit(y[b])) b++;
                var digitsA = startA;
                var digitsB = startB;
                while (digitsA < a && x[digitsA] == '0') digitsA++;
                while (digitsB < b && y[digitsB] == '0') digitsB++;
                var result = (a - digitsA).CompareTo(b - digitsB);
                if (result == 0) result = x.AsSpan(digitsA, a - digitsA).SequenceCompareTo(y.AsSpan(digitsB, b - digitsB));
                if (result != 0) return result;
                continue;
            }
            var c = char.ToUpperInvariant(x[a++]).CompareTo(char.ToUpperInvariant(y[b++]));
            if (c != 0) return c;
        }
        var remaining = (x.Length - a).CompareTo(y.Length - b);
        return remaining != 0 ? remaining : StringComparer.OrdinalIgnoreCase.Compare(x, y);
    }
}
