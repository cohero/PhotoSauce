﻿using System;
using System.Buffers;
using System.Threading;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

using Blake2Fast;
using PhotoSauce.MagicScaler.Interpolators;

namespace PhotoSauce.MagicScaler
{
	internal sealed class KernelMap<T> : IMultiDisposable where T : unmanaged
	{
		private static class Cache
		{
			private static readonly SimpleLruCache<Guid, KernelMap<T>> lruCache = new SimpleLruCache<Guid, KernelMap<T>>();

			public static KernelMap<T> GetOrAdd(int isize, int osize, InterpolationSettings interpolator, int ichannels, double offset)
			{
				if (!(interpolator.WeightingFunction is IUniquelyIdentifiable iwf))
					return KernelMap<T>.create(isize, osize, interpolator, ichannels, offset);

				var hasher = Blake2b.CreateIncrementalHasher(Unsafe.SizeOf<Guid>());
				hasher.Update(isize);
				hasher.Update(osize);
				hasher.Update(ichannels);
				hasher.Update(offset);
				hasher.Update(iwf.UniqueID);
				hasher.Update(interpolator.Blur);

				var hash = (Span<byte>)stackalloc byte[Unsafe.SizeOf<Guid>()];
				hasher.Finish(hash);
				var key = MemoryMarshal.Read<Guid>(hash);

				if (lruCache.TryGet(key, out var map))
					return map;

				return lruCache.GetOrAdd(key, KernelMap<T>.create(isize, osize, interpolator, ichannels, offset));
			}
		}

		private const int maxStackAlloc = 128;

		private static readonly GaussianInterpolator blur_0_50 = new GaussianInterpolator(0.50);
		private static readonly GaussianInterpolator blur_0_60 = new GaussianInterpolator(0.60);
		private static readonly GaussianInterpolator blur_0_75 = new GaussianInterpolator(0.75);
		private static readonly GaussianInterpolator blur_1_00 = new GaussianInterpolator(1.00);
		private static readonly GaussianInterpolator blur_1_50 = new GaussianInterpolator(1.50);

		private static int getPadding(int isize, int ksize, int channels)
		{
			if (typeof(T) != typeof(float))
				return 0;

			int inc = channels == 3 ? 4 : (ksize >= 8 ? HWIntrinsics.VectorCount<T>() : 4) / channels;
			int	pad = MathUtil.DivCeiling(ksize, inc) * inc - ksize;
			int thresh = channels == 4 ? 1 : HWIntrinsics.IsSupported || channels == 1 ? 2 : 3;

			return ksize < thresh || ksize + pad > isize ? 0 : pad;
		}

		private static void fillWeights(Span<float> kernel, IInterpolator interpolator, double start, double center, double scale)
		{
			double sum = 0d;
			for (int i = 0; i < kernel.Length; i++)
			{
				double weight = interpolator.GetValue(Math.Abs((start - center + i) * scale));
				sum += weight;
				kernel[i] = (float)weight;
			}

			float kscale = (float)(1 / sum);
			for (int i = 0; i < kernel.Length; i++)
				kernel[i] *= kscale;
		}

		private static void convertWeights(ReadOnlySpan<float> kernel, Span<T> mbuff, int channels)
		{
			if (typeof(T) != typeof(int) && typeof(T) != typeof(float))
				throw new NotSupportedException(nameof(T) + " must be int or float");

			if (mbuff.Length == 0 || mbuff.Length < kernel.Length * channels)
				throw new ArgumentException("Buffer too small", nameof(mbuff));

			if (typeof(T) == typeof(float))
			{
				var conv = MemoryMarshal.Cast<float, T>(kernel);

				if (channels == 1)
					conv.CopyTo(mbuff);
				else
					for (int i = 0; i < conv.Length; i++)
						mbuff.Slice(i * channels, channels).Fill(conv[i]);
			}
			else
			{
				ref T sstart = ref mbuff[0];
				for (int i = 0; i < kernel.Length; i++)
					Unsafe.Add(ref sstart, i) = (T)(object)MathUtil.Fix15(kernel[i]);
			}
		}

		private static int clamp(int start, int ipix, Span<float> kernel)
		{
			if (start + kernel.Length > ipix)
			{
				int ns = ipix - kernel.Length, last = kernel.Length - 1, offs = start - ns;

				float a = 0f;
				for (int i = 0; i <= offs; i++)
					a += kernel[last - i];

				start = ns;
				kernel[last] = a;

				for (int i = last - 1; i >= 0; i--)
					kernel[i] = i >= offs ? kernel[i - offs] : default;
			}

			if (start < 0)
			{
				int offs = -start;

				float a = 0f;
				for (int i = 0; i <= offs; i++)
					a += kernel[i];

				start = 0;
				kernel[0] = a;

				for (int i = 1; i < kernel.Length; i++)
					kernel[i] = i + offs < kernel.Length ? kernel[i + offs] : default;
			}

			return start;
		}

		unsafe private static KernelMap<T> create(int isize, int osize, InterpolationSettings interpolator, int ichannels, double offset)
		{
			double support = interpolator.WeightingFunction.Support;
			double cscale = Math.Min((double)osize / isize, 1d) / interpolator.Blur;
			support /= cscale;

			// prevent artifacts from heavily negative-weighted edges when kernel is larger than input
			if (isize < 3 && support * 2 > isize)
				support = isize / 2d;

			int cycle = osize / (int)MathUtil.GCD((uint)isize, (uint)osize);
			int channels = typeof(T) == typeof(float) ? ichannels : 1;
			int ksize = (int)Math.Ceiling(support * 2d);
			int kpad = getPadding(isize, ksize, channels);
			int klen = ksize + kpad;

			int cacheLen = isize == osize ? klen : 0;
			int buffLen = klen * channels + cacheLen;
			byte[]? karray = null;
			var kbuff = buffLen <= maxStackAlloc ?
				stackalloc float[buffLen] :
				MemoryMarshal.Cast<byte, float>(karray = ArrayPool<byte>.Shared.Rent(buffLen * sizeof(float))).Slice(0, buffLen);

			var kcache = kbuff.Slice(buffLen - cacheLen, cacheLen);
			var kernel = kbuff.Slice(buffLen - klen - cacheLen, klen);
			var mbuff = MemoryMarshal.Cast<float, T>(kbuff.Slice(0, Math.Min(klen, isize) * channels));
			var mbytes = MemoryMarshal.AsBytes(mbuff);

			if (cacheLen > 0)
				fillWeights(kcache, interpolator.WeightingFunction, (int)support - ksize + 1, 0, 1);

			var map = new KernelMap<T>(osize, Math.Min(klen, isize), channels);
			fixed (byte* mstart = map.Map)
			{
				int* mpi = (int*)mstart;
				T* mpw = (T*)(mstart + (typeof(T) == typeof(float) ? map.indexLen : sizeof(int)));

				double sinc = (double)isize / osize;
				double spoint = ((double)isize - osize) / (osize * 2d) + offset;

				for (int i = 0; i < osize; i++)
				{
					int start = (int)(spoint + support) - ksize + 1;

					if (cacheLen > 0)
						kcache.CopyTo(kernel);
					else
						fillWeights(kernel.Slice(0, ksize), interpolator.WeightingFunction, start, spoint, cscale);

					spoint += sinc;

					if (kpad > 0)
						kernel.Slice(ksize).Clear();

					var kclamp = kernel;
					if (start < 0 || start + kernel.Length > isize)
					{
						start = clamp(start, isize, kernel);
						kclamp = kernel.Slice(0, Math.Min(kernel.Length, isize));
					}

					*mpi++ = start;

					convertWeights(kclamp, mbuff, channels);
					if (typeof(T) == typeof(float) && i > cycle)
					{
						int coffs = *(mpi - cycle * 2);
						if (new ReadOnlySpan<byte>(mstart + coffs, mbytes.Length).SequenceEqual(mbytes))
						{
							*mpi++ = coffs;
							continue;
						}
					}

					Unsafe.CopyBlockUnaligned(ref Unsafe.AsRef<byte>(mpw), ref mbytes[0], (uint)mbytes.Length);

					if (typeof(T) == typeof(float))
					{
						*mpi++ = (int)((byte*)mpw - mstart);
						mpw += mbuff.Length;
					}
					else
					{
						mpi += mbuff.Length;
						mpw += mbuff.Length + 1;
					}
				}
			}

			ArrayPool<byte>.Shared.TryReturn(karray);

			return map;
		}

		public static KernelMap<T> CreateResample(int isize, int osize, InterpolationSettings interpolator, int ichannels, bool subsampleOffset)
		{
			double offset = interpolator.WeightingFunction.Support < 0.1 ? 0.5 : subsampleOffset ? 0.25 : 0.0;

			return Cache.GetOrAdd(isize, osize, interpolator, ichannels, offset);
		}

		public static KernelMap<T> CreateBlur(int size, double radius, int ichannels)
		{
			var interpolator = radius switch {
				0.50 => blur_0_50,
				0.60 => blur_0_60,
				0.75 => blur_0_75,
				1.00 => blur_1_00,
				1.50 => blur_1_50,
				_    => new GaussianInterpolator(radius)
			};

			return Cache.GetOrAdd(size, size, new InterpolationSettings(interpolator, 1.0), ichannels, 0.0);
		}

		private ArraySegment<byte> map;
		private volatile int refCount = 1;

		private int indexLen => typeof(T) == typeof(float) ? MathUtil.PowerOfTwoCeiling(Pixels * sizeof(int) * 2, HWIntrinsics.VectorCount<byte>()) : Pixels * sizeof(int);

		public int Pixels { get; }
		public int Samples { get; }
		public int Channels { get; }

		public ReadOnlySpan<byte> Map {
			get {
				if (refCount <= 0 || map.Array is null)
					throw new ObjectDisposedException(nameof(KernelMap<T>));

				return new ReadOnlySpan<byte>(map.Array, map.Offset, map.Count);
			}
		}

		private KernelMap(int pixels, int samples, int channels)
		{
			Pixels = pixels;
			Samples = samples;
			Channels = channels;

			int len = indexLen + pixels * samples * channels * Unsafe.SizeOf<T>();
			map = BufferPool.Rent(len);
		}

		public bool TryAddRef() => Interlocked.Increment(ref refCount) > 0;

		public void Dispose()
		{
			if (Interlocked.Decrement(ref refCount) == 0 && Interlocked.CompareExchange(ref refCount, int.MinValue, 0) == 0)
			{
				BufferPool.Return(map);
				map = default;
			}
		}
	}
}