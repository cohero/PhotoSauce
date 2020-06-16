﻿using System;
using System.Runtime.InteropServices;

using Blake2Fast;

namespace PhotoSauce.MagicScaler
{
	internal static class CacheHash
	{
		public const int DigestLength = 5;

		private static ReadOnlySpan<byte> base32Table => new[] {
			(byte)'A', (byte)'B', (byte)'C', (byte)'D', (byte)'E', (byte)'F', (byte)'G', (byte)'H',
			(byte)'I', (byte)'J', (byte)'K', (byte)'L', (byte)'M', (byte)'N', (byte)'O', (byte)'P',
			(byte)'Q', (byte)'R', (byte)'S', (byte)'T', (byte)'U', (byte)'V', (byte)'W', (byte)'X',
			(byte)'Y', (byte)'Z', (byte)'2', (byte)'3', (byte)'4', (byte)'5', (byte)'6', (byte)'7'
		};

		// first 40 bits from the crypto hash, base32 encoded
		// https://tools.ietf.org/html/rfc4648#section-6
#if !BUILTIN_SPAN
		unsafe
#endif
		public static string Encode(ReadOnlySpan<byte> bhash)
		{
			if (DigestLength > (uint)bhash.Length)
				throw new ArgumentException($"Hash must be at least {DigestLength} bytes");

			var b32 = base32Table;
#if BUILTIN_SPAN
			Span<char> hash = stackalloc char[8];
#else
			char* hash = stackalloc char[8];
#endif

			hash[0] = (char)b32[  bhash[0]         >> 3];
			hash[1] = (char)b32[((bhash[0] & 0x07) << 2) | (bhash[1] >> 6)];
			hash[2] = (char)b32[( bhash[1] & 0x3e) >> 1];
			hash[3] = (char)b32[((bhash[1] & 0x01) << 4) | (bhash[2] >> 4)];
			hash[4] = (char)b32[((bhash[2] & 0x0f) << 1) | (bhash[3] >> 7)];
			hash[5] = (char)b32[( bhash[3] & 0x7c) >> 2];
			hash[6] = (char)b32[((bhash[3] & 0x03) << 3) | (bhash[4] >> 5)];
			hash[7] = (char)b32[  bhash[4] & 0x1f];

			return new string(hash);
		}

		public static string Create(string data)
		{
			var hash = (Span<byte>)stackalloc byte[DigestLength];
			Blake2b.ComputeAndWriteHash(DigestLength, MemoryMarshal.AsBytes(data.AsSpan()), hash);

			return Encode(hash);
		}
	}
}
