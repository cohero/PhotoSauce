﻿using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Diagnostics.CodeAnalysis;

#if NETFRAMEWORK
using System.Linq;
using System.Configuration;
using System.Collections.Specialized;
#endif

using Blake2Fast;

namespace PhotoSauce.MagicScaler
{
	internal static class MiscExtensions
	{
		public static Orientation Clamp(this Orientation o) => o < Orientation.Normal? Orientation.Normal : o > Orientation.Rotate270 ? Orientation.Rotate270 : o;

		public static GifDisposalMethod Clamp(this GifDisposalMethod m) => m < GifDisposalMethod.Preserve || m > GifDisposalMethod.RestorePrevious ? GifDisposalMethod.Preserve : m;

		public static bool SwapsDimensions(this Orientation o) => o > Orientation.FlipVertical;

		public static bool RequiresCache(this Orientation o) => o > Orientation.FlipHorizontal;

		public static bool FlipsX(this Orientation o) => o == Orientation.FlipHorizontal || o == Orientation.Rotate180 || o == Orientation.Rotate270 || o == Orientation.Transverse;

		public static bool FlipsY(this Orientation o) => o == Orientation.FlipVertical || o == Orientation.Rotate180 || o == Orientation.Rotate90 || o == Orientation.Transverse;

		public static Orientation Invert(this Orientation o) => o == Orientation.Rotate270 ? Orientation.Rotate90 : o == Orientation.Rotate90 ? Orientation.Rotate270 : o;

		public static bool IsSubsampledX(this ChromaSubsampleMode o) => o == ChromaSubsampleMode.Subsample420 || o == ChromaSubsampleMode.Subsample422;

		public static bool IsSubsampledY(this ChromaSubsampleMode o) => o == ChromaSubsampleMode.Subsample420 || o == ChromaSubsampleMode.Subsample440;

		public static bool InsensitiveEquals(this string s1, string s2) => string.Equals(s1, s2, StringComparison.OrdinalIgnoreCase);

		public static string GetFileExtension(this FileFormat fmt, string? preferredExtension = null)
		{
			if (fmt == FileFormat.Png8)
				fmt = FileFormat.Png;

			string ext = fmt.ToString().ToLowerInvariant();
			if (!string.IsNullOrEmpty(preferredExtension))
			{
				if (preferredExtension[0] == '.')
					preferredExtension = preferredExtension.Substring(1);

				if (preferredExtension.InsensitiveEquals(ext) || (preferredExtension.InsensitiveEquals("jpg") && fmt == FileFormat.Jpeg) || (preferredExtension.InsensitiveEquals("tif") && fmt == FileFormat.Tiff))
					return preferredExtension;
			}

			return ext;
		}

		public static void TryReturn<T>(this ArrayPool<T> pool, T[]? buff)
		{
			if (buff is not null)
				pool.Return(buff);
		}

		public static Guid FinalizeToGuid<T>(this T hasher) where T : IBlake2Incremental
		{
			var hash = (Span<byte>)stackalloc byte[hasher.DigestLength];
			hasher.Finish(hash);

			return MemoryMarshal.Read<Guid>(hash);
		}

		[return: MaybeNull]
		public static TValue GetValueOrDefault<TKey, TValue>(this IDictionary<TKey, TValue> dic, TKey key, TValue defaultValue = default) where TKey : notnull =>
			dic.TryGetValue(key, out var value) ? value : defaultValue;

		[return: MaybeNull]
		public static TValue GetValueOrDefault<TKey, TValue>(this IReadOnlyDictionary<TKey, TValue> dic, TKey key, TValue defaultValue = default) where TKey : notnull =>
			dic.TryGetValue(key, out var value) ? value : defaultValue;

#if NETFRAMEWORK
		public static IDictionary<string, string> ToDictionary(this NameValueCollection nv) =>
			nv.AllKeys.Where(k => !string.IsNullOrEmpty(k)).ToDictionary(k => k, k => nv.GetValues(k).LastOrDefault(), StringComparer.OrdinalIgnoreCase);

		public static IDictionary<string, string> ToDictionary(this KeyValueConfigurationCollection kv) =>
			kv.AllKeys.Where(k => !string.IsNullOrEmpty(k)).ToDictionary(k => k, k => kv[k].Value, StringComparer.OrdinalIgnoreCase);
#endif

		public static IDictionary<TKey, TValue> Coalesce<TKey, TValue>(this IDictionary<TKey, TValue> dic1, IDictionary<TKey, TValue> dic2) where TKey : notnull
		{
			foreach (var kv in dic2)
				dic1[kv.Key] = kv.Value;

			return dic1;
		}
	}
}
