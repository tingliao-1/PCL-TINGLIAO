// Copyright (c) 2026 Xocio. Licensed under the GNU General Public License v3.0.
// Based on Campfire Link implementation.

using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace PCLCS.Link
{
    /// <summary>
    /// Invite code of a room, written as <c>U/XXXX-XXXX-XXXX-XXXX</c>.
    /// </summary>
    public sealed class RoomCode
    {
        private const string Alphabet = "0123456789ABCDEFGHJKLMNPQRSTUVWXYZ";
        private const string NetworkPrefix = "scaffolding-mc-";
        private const int DigitCount = 16;
        private const int Checksum = 7;
        private const int TextLength = 21;

        private static readonly BigInteger Range = BigInteger.Pow(Alphabet.Length, DigitCount);

        private RoomCode(BigInteger value)
        {
            var digits = new char[DigitCount];
            for (int i = 0; i < DigitCount; i++)
            {
                digits[i] = Alphabet[(int)(value % Alphabet.Length)];
                value /= Alphabet.Length;
            }

            var code = new StringBuilder("U/", TextLength);
            for (int i = 0; i < DigitCount; i++)
            {
                if (i > 0 && i % 4 == 0)
                    code.Append('-');
                code.Append(digits[i]);
            }

            Code = code.ToString();
            NetworkName = NetworkPrefix + Group(digits, 0) + "-" + Group(digits, 4);
            NetworkSecret = Group(digits, 8) + "-" + Group(digits, 12);
        }

        public string Code { get; }

        public string NetworkName { get; }

        public string NetworkSecret { get; }

        public override string ToString() => Code;

        public static RoomCode Create()
        {
            var bytes = new byte[17];
            using (var random = RandomNumberGenerator.Create())
                random.GetBytes(bytes, 0, 16);

            var value = new BigInteger(bytes) % Range;
            return new RoomCode(value - value % Checksum);
        }

        public static RoomCode? Find(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return null;

            string upper = text!.ToUpperInvariant();
            for (int start = 0; start + TextLength <= upper.Length; start++)
            {
                if (upper[start] == 'U' && upper[start + 1] == '/' && TryDecode(upper, start + 2, out var value))
                    return new RoomCode(value);
            }
            return null;
        }

        private static bool TryDecode(string text, int offset, out BigInteger value)
        {
            value = BigInteger.Zero;

            for (int i = TextLength - 3; i >= 0; i--)
            {
                char c = text[offset + i];
                if (i == 4 || i == 9 || i == 14)
                {
                    if (c != '-')
                        return false;
                    continue;
                }

                int digit = ValueOf(c);
                if (digit < 0)
                    return false;
                value = value * Alphabet.Length + digit;
            }

            return value % Checksum == 0;
        }

        private static int ValueOf(char c)
        {
            if (c >= '0' && c <= '9')
                return c - '0';
            if (c >= 'A' && c <= 'Z')
            {
                if (c == 'I') return 1;
                if (c == 'O') return 0;
                int digit = c - 'A' + 10;
                if (c > 'I') digit--;
                if (c > 'O') digit--;
                return digit;
            }
            return -1;
        }

        private static string Group(char[] digits, int start)
        {
            return new string(digits, start, 4);
        }
    }
}
