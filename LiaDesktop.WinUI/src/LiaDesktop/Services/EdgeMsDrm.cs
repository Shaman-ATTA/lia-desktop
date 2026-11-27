using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace LiaDesktop.Services;

/// <summary>
/// Microsoft Edge TTS Sec-MS-GEC v2 DRM (ported from edge-tts 7.3.1 <c>edge_tts.drm</c>).
/// Token format: <c>"{cs64}-{sha256}"</c> uppercase hex (16 hex - 64 hex).
/// Uses a non-standard SHA-256 digest — do not replace with <see cref="SHA256"/>.
/// </summary>
public static class EdgeMsDrm
{
    public const string TrustedClientToken = EdgeTtsConstants.TrustedClientToken;
    public const string ChromiumFullVersion = EdgeTtsConstants.ChromiumFullVersion;
    public const string SecMsGecVersion = EdgeTtsConstants.SecMsGecVersion;

    const long WinEpoch = 11644473600L;
    const double SToNs = 1e9;

    // FIPS 180-4 initial hash values (used when folding the first block).
    static readonly uint[] Sha256Initial =
    {
        0x6A09E667u, 0xBB67AE85u, 0x3C6EF372u, 0xA54FF53Au,
        0x510E527Fu, 0x9B05688Cu, 0x1F83D9ABu, 0x5BE0CD19u,
    };

    static readonly uint[] Sha256State =
    {
        0xFD9FBEA2u, 0x91243CE7u, 0xBE499094u, 0xCD2E1E0Au,
        0x6C006F49u, 0x04BD35ABu, 0xFEB0A7E4u, 0x03EFFBD2u,
    };

    static readonly uint[] Sha256Schedule =
    {
        0xEB24BF9Eu, 0x1558691Fu, 0xA0C09EE2u, 0x0CFA32BAu,
        0xA622EE8Cu, 0xD1DD9491u, 0xC0DD25A0u, 0xF9E70AB6u,
        0xF0C31573u, 0x7058CE4Eu, 0x7188E606u, 0x00E5AB33u,
        0xD258B060u, 0x72766564u,
    };

    static readonly uint[] Sha256RoundConstants =
    {
        0x428A2F98u, 0x71374491u, 0xB5C0FBCFu, 0xE9B5DBA5u,
        0x3956C25Bu, 0x59F111F1u, 0x923F82A4u, 0xAB1C5ED5u,
        0xD807AA98u, 0x12835B01u, 0x243185BEu, 0x550C7DC3u,
        0x72BE5D74u, 0x80DEB1FEu, 0x9BDC06A7u, 0xC19BF174u,
        0xE49B69C1u, 0xEFBE4786u, 0x0FC19DC6u, 0x240CA1CCu,
        0x2DE92C6Fu, 0x4A7484AAu, 0x5CB0A9DCu, 0x76F988DAu,
        0x983E5152u, 0xA831C66Du, 0xB00327C8u, 0xBF597FC7u,
        0xC6E00BF3u, 0xD5A79147u, 0x06CA6351u, 0x14292967u,
        0x27B70A85u, 0x2E1B2138u, 0x4D2C6DFCu, 0x53380D13u,
        0x650A7354u, 0x766A0ABBu, 0x81C2C92Eu, 0x92722C85u,
        0xA2BFE8A1u, 0xA81A664Bu, 0xC24B8B70u, 0xC76C51A3u,
        0xD192E819u, 0xD6990624u, 0xF40E3585u, 0x106AA070u,
        0x19A4C116u, 0x1E376C08u, 0x2748774Cu, 0x34B0BCB5u,
        0x391C0CB3u, 0x4ED8AA4Au, 0x5B9CCA4Fu, 0x682E6FF3u,
        0x748F82EEu, 0x78A5636Fu, 0x84C87814u, 0x8CC70208u,
        0x90BEFFFAu, 0xA4506CEBu, 0xBEF9A3F7u, 0xC67178F2u,
    };

    const uint Sha256Adjustment = 0xC9DC06A7u;
    const uint Sha256Sigma0Adjustment = 0x059C0B80u;
    const uint Sha256Sigma1Adjustment = 0x000B92C0u;

    // Round constants with index 14 replaced by Sha256Adjustment.
    static readonly uint[] Sha256BlockConstants = BuildBlockConstants();

    static readonly (uint Coefficient, uint Offset)[] Cs64Coefficients =
    {
        (0xE82E91B1u, 0xB32700D0u),
        (0x44DE24C6u, 0x88DFF51Du),
        (0x3BEB65BCu, 0x29514038u),
        (0xC89C5933u, 0xB328CBDFu),
        (0x8D1EC211u, 0xD405B4D5u),
        (0x314EF5D5u, 0x26E55F3Cu),
        (0xE92491B1u, 0x607801C6u),
        (0xF0A5B9D5u, 0x82B2C1FBu),
        (0x06064CF3u, 0xFA51A3DAu),
        (0xDA79FA60u, 0x207CC464u),
        (0xBED20560u, 0x3C3C74F3u),
        (0x89F9C9F8u, 0xD2D14B61u),
        (0x35FA38C6u, 0xBB04A2C1u),
        (0xCB49E3C6u, 0xC5626167u),
    };

    const uint Cs64Factor = 0x82893933u;
    const uint Cs64Adjustment0 = 0xF2F90B1Au;
    const uint Cs64Adjustment1 = 0x0B1A0000u;

    // Cache for token hash keyed by salt bytes (mirrors Python lru_cache).
    static readonly object TokenHashLock = new();
    static byte[]? CachedTokenHashKey;
    static (string Cs64, string Sha256) CachedTokenHash;

    static readonly object Sha256InitLock = new();
    static byte[]? CachedSha256InitKey;
    static uint[]? CachedSha256InitState;

    public static double ClockSkewSeconds { get; set; }

    public static string GenerateSecMsGec() =>
        GenerateSecMsGec(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 + ClockSkewSeconds);

    /// <summary>Generate a v2 Sec-MS-GEC for a fixed Unix timestamp (testing).</summary>
    public static string GenerateSecMsGec(double unixTimestamp)
    {
        var ticks = unixTimestamp;
        ticks += WinEpoch;
        ticks -= ticks % 300;
        ticks *= SToNs / 100.0;
        var strToHash = Encoding.ASCII.GetBytes($"{ticks:0}{TrustedClientToken}");
        var (cs64, sha256) = TokenHash(strToHash);
        return $"{cs64}-{sha256}";
    }

    public static string GenerateMuid()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes);
    }

    /// <summary>
    /// Parse an RFC 2616 Date header and adjust <see cref="ClockSkewSeconds"/>.
    /// Soft no-op when the header is missing or unparseable.
    /// </summary>
    public static void AdjustClockSkewFromServerDate(string? rfc2616Date)
    {
        if (string.IsNullOrWhiteSpace(rfc2616Date))
            return;

        if (!TryParseRfc2616Date(rfc2616Date, out var serverUnix))
            return;

        var clientUnix = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 + ClockSkewSeconds;
        ClockSkewSeconds += serverUnix - clientUnix;
    }

    public static string BuildWssUrl(string connectionId)
    {
        var gec = GenerateSecMsGec();
        return $"{EdgeTtsConstants.WssUrlBase}"
            + $"&ConnectionId={connectionId}"
            + $"&Sec-MS-GEC={gec}"
            + $"&Sec-MS-GEC-Version={SecMsGecVersion}";
    }

    public static IReadOnlyDictionary<string, string> WssHeadersWithoutCookie() =>
        EdgeTtsConstants.WssHeadersWithoutCookie;

    static bool TryParseRfc2616Date(string date, out double unixTimestamp)
    {
        unixTimestamp = 0;
        // RFC 1123 / RFC 2616 HTTP-date
        if (DateTimeOffset.TryParseExact(
                date,
                "r",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dto))
        {
            unixTimestamp = dto.ToUnixTimeMilliseconds() / 1000.0;
            return true;
        }

        if (DateTimeOffset.TryParse(
                date,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out dto))
        {
            unixTimestamp = dto.ToUnixTimeMilliseconds() / 1000.0;
            return true;
        }

        return false;
    }

    static uint[] BuildBlockConstants()
    {
        var c = new uint[64];
        Array.Copy(Sha256RoundConstants, c, 64);
        c[14] = Sha256Adjustment;
        return c;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint Rol16(uint value) => (value << 16) | (value >> 16);

    static List<uint> WordsLe4Even(ReadOnlySpan<byte> data)
    {
        var pad = (4 - (data.Length % 4)) % 4;
        var paddedLen = data.Length + pad;
        if (paddedLen == 0)
            return new List<uint>();

        Span<byte> padded = paddedLen <= 256 ? stackalloc byte[paddedLen] : new byte[paddedLen];
        data.CopyTo(padded);
        for (var i = data.Length; i < paddedLen; i++)
            padded[i] = 0;

        var words = new List<uint>(paddedLen / 4 + 1);
        for (var i = 0; i < paddedLen; i += 4)
            words.Add(BitConverter.ToUInt32(padded.Slice(i, 4))); // little-endian

        if ((words.Count & 1) != 0)
            words.Add(0);
        return words;
    }

    static uint Sigma0(uint value)
    {
        ulong v = value;
        return (uint)((v >> 7) ^ (v << 25) ^ (v >> 18) ^ (v << 14) ^ (v >> 3));
    }

    static uint Sigma1(uint value)
    {
        ulong v = value;
        return (uint)((v >> 17) ^ (v << 15) ^ (v >> 19) ^ (v << 13) ^ (v >> 10));
    }

    static void Sha256Rounds(
        uint[] state,
        IList<uint> words,
        int firstRound,
        out uint a, out uint b, out uint c, out uint d,
        out uint e, out uint f, out uint g, out uint h)
    {
        a = state[0]; b = state[1]; c = state[2]; d = state[3];
        e = state[4]; f = state[5]; g = state[6]; h = state[7];

        var constants = firstRound == 14 ? Sha256BlockConstants : Sha256RoundConstants;
        var aligned = Math.Min(64, ((firstRound + 7) / 8) * 8);

        for (var index = firstRound; index < aligned; index++)
        {
            ulong rotE = (ulong)e * 0x100000001UL;
            ulong rotA = (ulong)a * 0x100000001UL;
            ulong t1 = h
                + ((rotE >> 6) ^ (rotE >> 11) ^ (rotE >> 25))
                + (g ^ (e & (f ^ g)))
                + constants[index]
                + words[index];
            ulong t2 = ((rotA >> 2) ^ (rotA >> 13) ^ (rotA >> 22))
                + ((a & b) | (c & (a | b)));
            var newA = (uint)(t1 + t2);
            var newE = (uint)(d + t1);
            h = g; g = f; f = e; e = newE;
            d = c; c = b; b = a; a = newA;
        }

        for (var index = aligned; index < 64; index += 8)
        {
            ulong rotE = (ulong)e * 0x100000001UL;
            ulong rotA = (ulong)a * 0x100000001UL;
            ulong t1 = h
                + ((rotE >> 6) ^ (rotE >> 11) ^ (rotE >> 25))
                + (g ^ (e & (f ^ g)))
                + constants[index]
                + words[index];
            d = (uint)(d + t1);
            h = (uint)(t1
                + ((rotA >> 2) ^ (rotA >> 13) ^ (rotA >> 22))
                + ((a & b) | (c & (a | b))));

            rotE = (ulong)d * 0x100000001UL;
            rotA = (ulong)h * 0x100000001UL;
            t1 = g
                + ((rotE >> 6) ^ (rotE >> 11) ^ (rotE >> 25))
                + (f ^ (d & (e ^ f)))
                + constants[index + 1]
                + words[index + 1];
            c = (uint)(c + t1);
            g = (uint)(t1
                + ((rotA >> 2) ^ (rotA >> 13) ^ (rotA >> 22))
                + ((h & a) | (b & (h | a))));

            rotE = (ulong)c * 0x100000001UL;
            rotA = (ulong)g * 0x100000001UL;
            t1 = f
                + ((rotE >> 6) ^ (rotE >> 11) ^ (rotE >> 25))
                + (e ^ (c & (d ^ e)))
                + constants[index + 2]
                + words[index + 2];
            b = (uint)(b + t1);
            f = (uint)(t1
                + ((rotA >> 2) ^ (rotA >> 13) ^ (rotA >> 22))
                + ((g & h) | (a & (g | h))));

            rotE = (ulong)b * 0x100000001UL;
            rotA = (ulong)f * 0x100000001UL;
            t1 = e
                + ((rotE >> 6) ^ (rotE >> 11) ^ (rotE >> 25))
                + (d ^ (b & (c ^ d)))
                + constants[index + 3]
                + words[index + 3];
            a = (uint)(a + t1);
            e = (uint)(t1
                + ((rotA >> 2) ^ (rotA >> 13) ^ (rotA >> 22))
                + ((f & g) | (h & (f | g))));

            rotE = (ulong)a * 0x100000001UL;
            rotA = (ulong)e * 0x100000001UL;
            t1 = d
                + ((rotE >> 6) ^ (rotE >> 11) ^ (rotE >> 25))
                + (c ^ (a & (b ^ c)))
                + constants[index + 4]
                + words[index + 4];
            h = (uint)(h + t1);
            d = (uint)(t1
                + ((rotA >> 2) ^ (rotA >> 13) ^ (rotA >> 22))
                + ((e & f) | (g & (e | f))));

            rotE = (ulong)h * 0x100000001UL;
            rotA = (ulong)d * 0x100000001UL;
            t1 = c
                + ((rotE >> 6) ^ (rotE >> 11) ^ (rotE >> 25))
                + (b ^ (h & (a ^ b)))
                + constants[index + 5]
                + words[index + 5];
            g = (uint)(g + t1);
            c = (uint)(t1
                + ((rotA >> 2) ^ (rotA >> 13) ^ (rotA >> 22))
                + ((d & e) | (f & (d | e))));

            rotE = (ulong)g * 0x100000001UL;
            rotA = (ulong)c * 0x100000001UL;
            t1 = b
                + ((rotE >> 6) ^ (rotE >> 11) ^ (rotE >> 25))
                + (a ^ (g & (h ^ a)))
                + constants[index + 6]
                + words[index + 6];
            f = (uint)(f + t1);
            b = (uint)(t1
                + ((rotA >> 2) ^ (rotA >> 13) ^ (rotA >> 22))
                + ((c & d) | (e & (c | d))));

            rotE = (ulong)f * 0x100000001UL;
            rotA = (ulong)b * 0x100000001UL;
            t1 = a
                + ((rotE >> 6) ^ (rotE >> 11) ^ (rotE >> 25))
                + (h ^ (f & (g ^ h)))
                + constants[index + 7]
                + words[index + 7];
            e = (uint)(e + t1);
            a = (uint)(t1
                + ((rotA >> 2) ^ (rotA >> 13) ^ (rotA >> 22))
                + ((b & c) | (d & (b | c))));
        }
    }

    static void ExpandSha256Schedule(List<uint> words, int firstWord = 16)
    {
        for (var index = firstWord; index < 64; index++)
        {
            ulong x = words[index - 2];
            ulong y = words[index - 15];
            // Rotations retain high bits until the final sum is masked (as in Python).
            ulong sigma1 = (x >> 17) ^ (x << 15) ^ (x >> 19) ^ (x << 13) ^ (x >> 10);
            ulong sigma0 = (y >> 7) ^ (y << 25) ^ (y >> 18) ^ (y << 14) ^ (y >> 3);
            words.Add((uint)(sigma1 + words[index - 7] + sigma0 + words[index - 16]));
        }
    }

    static uint[] Sha256Init(ReadOnlySpan<byte> data)
    {
        lock (Sha256InitLock)
        {
            if (CachedSha256InitState is not null
                && CachedSha256InitKey is not null
                && CachedSha256InitKey.AsSpan().SequenceEqual(data))
            {
                return (uint[])CachedSha256InitState.Clone();
            }
        }

        var words = new List<uint>(64);
        for (var i = 0; i < 14; i++)
            words.Add(0);

        // words[14] = first 3 bytes big-endian; words[15] = next 4 bytes big-endian
        words.Add((uint)((data[0] << 16) | (data[1] << 8) | data[2]));
        words.Add((uint)((data[3] << 24) | (data[4] << 16) | (data[5] << 8) | data[6]));

        for (var index = 16; index < 30; index++)
        {
            ulong value = Sha256Schedule[index - 16];
            if (index - 2 >= 14)
            {
                value += index == 16
                    ? Sigma1(words[index - 2]) ^ Sha256Sigma1Adjustment
                    : Sigma1(words[index - 2]);
            }
            if (index - 7 >= 14)
                value += words[index - 7];
            if (index - 15 >= 14)
            {
                value += index == 29
                    ? Sigma0(words[index - 15]) ^ Sha256Sigma0Adjustment
                    : Sigma0(words[index - 15]);
            }
            words.Add((uint)value);
        }

        words.Add((uint)(
            Sigma1(words[28])
            + words[23]
            + Sigma0(words[15])
            + words[14]
            + Sha256Adjustment
            - Sha256RoundConstants[14]));

        ExpandSha256Schedule(words, 31);

        Sha256Rounds(Sha256State, words, 14,
            out var wa, out var wb, out var wc, out var wd,
            out var we, out var wf, out var wg, out var wh);

        var working = new[] { wa, wb, wc, wd, we, wf, wg, wh };
        var result = new uint[8];
        for (var i = 0; i < 8; i++)
            result[i] = Sha256Initial[i] + working[i];

        lock (Sha256InitLock)
        {
            CachedSha256InitKey = data.ToArray();
            CachedSha256InitState = (uint[])result.Clone();
        }

        return result;
    }

    static byte[] Sha256Digest(ReadOnlySpan<byte> data)
    {
        if (data.Length < 7)
            throw new ArgumentException("Token salt must contain at least seven bytes", nameof(data));

        var length = 57 + data.Length;
        var after = data[7..];
        // padded = data[7:] + 0x80 + zeros to (55 - length) % 64, then 8-byte BE bit length
        var zeroPad = (55 - length) % 64;
        if (zeroPad < 0)
            zeroPad += 64;
        var paddedLen = after.Length + 1 + zeroPad + 8;
        var padded = new byte[paddedLen];
        after.CopyTo(padded);
        padded[after.Length] = 0x80;
        // zeros already
        var bitLen = (ulong)length * 8;
        for (var i = 0; i < 8; i++)
            padded[paddedLen - 1 - i] = (byte)(bitLen >> (8 * i));

        var state = Sha256Init(data[..7]);
        for (var offset = 0; offset < padded.Length; offset += 64)
        {
            var words = new List<uint>(64);
            for (var i = 0; i < 16; i++)
            {
                var o = offset + i * 4;
                words.Add((uint)((padded[o] << 24) | (padded[o + 1] << 16) | (padded[o + 2] << 8) | padded[o + 3]));
            }
            ExpandSha256Schedule(words);
            Sha256Rounds(state, words, 0,
                out var wa, out var wb, out var wc, out var wd,
                out var we, out var wf, out var wg, out var wh);
            state[0] += wa; state[1] += wb; state[2] += wc; state[3] += wd;
            state[4] += we; state[5] += wf; state[6] += wg; state[7] += wh;
        }

        var digest = new byte[32];
        for (var i = 0; i < 8; i++)
        {
            digest[i * 4] = (byte)(state[i] >> 24);
            digest[i * 4 + 1] = (byte)(state[i] >> 16);
            digest[i * 4 + 2] = (byte)(state[i] >> 8);
            digest[i * 4 + 3] = (byte)state[i];
        }
        return digest;
    }

    static (uint, uint) Cs64Digest(ReadOnlySpan<byte> data, ReadOnlySpan<byte> digest)
    {
        var keyA = BitConverter.ToUInt32(digest) | 1u;
        var keyB = BitConverter.ToUInt32(digest.Slice(4)) | 1u;
        var factors = new[]
        {
            keyA * Cs64Factor,
            keyB * Cs64Factor,
        };

        var terms = new List<uint>(Cs64Coefficients.Length + 16);
        var offsets = new List<uint>(Cs64Coefficients.Length + 16);
        for (var index = 0; index < Cs64Coefficients.Length; index++)
        {
            var (coefficient, offset) = Cs64Coefficients[index];
            terms.Add(coefficient * factors[index % 2]);
            offsets.Add(offset);
        }

        // b"\x00" + data
        var withNull = new byte[1 + data.Length];
        data.CopyTo(withNull.AsSpan(1));
        var dataWords = WordsLe4Even(withNull);
        for (var index = 0; index < dataWords.Count; index++)
        {
            var word = dataWords[index];
            ulong term = (ulong)word * (index % 2 == 0 ? keyA : keyB);
            ulong offset = (ulong)Rol16(word) * (index % 2 == 0 ? 0xEF0569FBu : 0xC31713DBu);
            if (index == 0)
            {
                term += (ulong)Cs64Adjustment0 * factors[0];
                offset += Cs64Adjustment1;
            }
            terms.Add((uint)term);
            offsets.Add((uint)offset);
        }

        uint swapAcc = 0, swapLast = 0, state = 0, acc = 0;
        for (var index = 0; index < terms.Count; index += 2)
        {
            var x = terms[index];
            var y = terms[index + 1];

            var t = Rol16(offsets[index] + x);
            var q = t * 0x76F50F34u;
            swapAcc += Rol16(q) * 0xC3EFEA97u + q;

            t = Rol16(offsets[index + 1] + y);
            q = t * 0x1881CE3Cu;
            swapLast = Rol16(q) * 0x35BD1EC9u + q;
            swapAcc += swapLast;

            state = state * keyA + x;
            state = Rol16(state) * 0xCF98B111u;
            state = Rol16(state) * 0x87085B9Fu;
            state = Rol16(state) * 0x12CEB96Du;
            state = Rol16(state) * 0x257E1D83u;
            acc += state;

            t = state * keyB + y;
            t = Rol16(t) * 0xA27416F5u;
            t = Rol16(t) * 0xD38396FFu;
            state = Rol16(Rol16(t) * 0x7C932B89u) * 0xBFA49F69u;
            acc += state;
        }

        return (swapLast ^ state, swapAcc ^ acc);
    }

    static (string Cs64, string Sha256) TokenHash(ReadOnlySpan<byte> data)
    {
        lock (TokenHashLock)
        {
            if (CachedTokenHashKey is not null
                && CachedTokenHashKey.AsSpan().SequenceEqual(data)
                && CachedTokenHash.Cs64 is not null)
            {
                return CachedTokenHash;
            }
        }

        var digest = Sha256Digest(data);
        var (w0, w1) = Cs64Digest(data, digest);
        Span<byte> cs64Bytes = stackalloc byte[8];
        BitConverter.TryWriteBytes(cs64Bytes, w0);
        BitConverter.TryWriteBytes(cs64Bytes.Slice(4), w1);
        var cs64 = Convert.ToHexString(cs64Bytes);
        var sha256 = Convert.ToHexString(digest);

        lock (TokenHashLock)
        {
            CachedTokenHashKey = data.ToArray();
            CachedTokenHash = (cs64, sha256);
        }

        return (cs64, sha256);
    }
}
