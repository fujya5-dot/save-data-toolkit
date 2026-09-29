using System.Numerics;
using System.Security.Cryptography;

namespace YuniRetroToolkit.GameDefinitions;

public static class Ed25519SignatureVerifier
{
    public const int PublicKeySize = 32;
    public const int SignatureSize = 64;
    private static readonly BigInteger P = (BigInteger.One << 255) - 19;
    private static readonly BigInteger L = (BigInteger.One << 252) + BigInteger.Parse("27742317777372353535851937790883648493");
    private static readonly BigInteger D = Mod(-121665 * Inverse(121666));
    private static readonly BigInteger I = BigInteger.ModPow(2, (P - 1) / 4, P);
    private static readonly Point Identity = new(0, 1, 1, 0);
    private static readonly Point BasePoint = Decode(Convert.FromHexString("5866666666666666666666666666666666666666666666666666666666666666"));

    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != PublicKeySize || signature.Length != SignatureSize) return false;
        try
        {
            var a = Decode(publicKey);
            var r = Decode(signature[..32]);
            if (IsIdentity(a) || !IsIdentity(Multiply(a, L))) return false;
            var s = FromLittleEndian(signature[32..]);
            if (s >= L) return false;
            var challengeInput = new byte[64 + message.Length];
            signature[..32].CopyTo(challengeInput);
            publicKey.CopyTo(challengeInput.AsSpan(32));
            message.CopyTo(challengeInput.AsSpan(64));
            var challenge = FromLittleEndian(SHA512.HashData(challengeInput)) % L;
            var left = Multiply(BasePoint, s);
            var right = Add(r, Multiply(a, challenge));
            return Equal(left, right);
        }
        catch (InvalidDataException) { return false; }
        catch (ArithmeticException) { return false; }
    }

    public static bool IsValidPublicKey(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != PublicKeySize) return false;
        try
        {
            var point = Decode(publicKey);
            return !IsIdentity(point) && IsIdentity(Multiply(point, L));
        }
        catch (InvalidDataException) { return false; }
        catch (ArithmeticException) { return false; }
    }

    private static Point Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length != 32) throw new InvalidDataException("Invalid Ed25519 point length.");
        Span<byte> yBytes = stackalloc byte[32];
        encoded.CopyTo(yBytes);
        var sign = (yBytes[31] >> 7) & 1;
        yBytes[31] &= 0x7f;
        var y = FromLittleEndian(yBytes);
        if (y >= P) throw new InvalidDataException("Non-canonical Ed25519 point.");
        var y2 = Mod(y * y);
        var x2 = Mod((y2 - 1) * Inverse(D * y2 + 1));
        var x = BigInteger.ModPow(x2, (P + 3) / 8, P);
        if (Mod(x * x - x2) != 0) x = Mod(x * I);
        if (Mod(x * x - x2) != 0) throw new InvalidDataException("Invalid Ed25519 point.");
        if ((x & 1) != sign) x = P - x;
        if (x == 0 && sign != 0) throw new InvalidDataException("Non-canonical Ed25519 sign bit.");
        return new(x, y, 1, Mod(x * y));
    }

    private static Point Add(Point first, Point second)
    {
        var a = Mod((first.Y - first.X) * (second.Y - second.X));
        var b = Mod((first.Y + first.X) * (second.Y + second.X));
        var c = Mod(2 * D * first.T * second.T);
        var d = Mod(2 * first.Z * second.Z);
        var e = Mod(b - a);
        var f = Mod(d - c);
        var g = Mod(d + c);
        var h = Mod(b + a);
        return new(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }

    private static Point Double(Point point)
    {
        var a = Mod(point.X * point.X);
        var b = Mod(point.Y * point.Y);
        var c = Mod(2 * point.Z * point.Z);
        var d = Mod(-a);
        var e = Mod((point.X + point.Y) * (point.X + point.Y) - a - b);
        var g = Mod(d + b);
        var f = Mod(g - c);
        var h = Mod(d - b);
        return new(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }

    private static Point Multiply(Point point, BigInteger scalar)
    {
        var result = Identity;
        var addend = point;
        while (scalar > 0)
        {
            if (!scalar.IsEven) result = Add(result, addend);
            addend = Double(addend);
            scalar >>= 1;
        }
        return result;
    }

    private static BigInteger Inverse(BigInteger value) => BigInteger.ModPow(Mod(value), P - 2, P);
    private static BigInteger Mod(BigInteger value) => (value % P + P) % P;
    private static BigInteger FromLittleEndian(ReadOnlySpan<byte> value) => new(value, isUnsigned: true, isBigEndian: false);
    private static bool Equal(Point first, Point second)
        => Mod(first.X * second.Z - second.X * first.Z) == 0 && Mod(first.Y * second.Z - second.Y * first.Z) == 0;
    private static bool IsIdentity(Point point) => Mod(point.X) == 0 && Mod(point.Y - point.Z) == 0;
    private readonly record struct Point(BigInteger X, BigInteger Y, BigInteger Z, BigInteger T);
}
