﻿using System;
using System.Numerics;
using System.Runtime.CompilerServices;

#if HWINTRINSICS
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
#endif

using static PhotoSauce.MagicScaler.MathUtil;

using VectorF = System.Numerics.Vector<float>;

namespace PhotoSauce.MagicScaler
{
	internal static class Rec601Luma
	{
		public const double R = 0.299;
		public const double B = 0.114;
		public const double G = 1 - R - B;
		public static readonly Vector3 Coefficients = new Vector3((float)B, (float)G, (float)R);

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static byte FromBgr(byte b, byte g, byte r)
		{
			const uint rY = (ushort)(R * UQ15One + 0.5);
			const uint gY = (ushort)(G * UQ15One + 0.5);
			const uint bY = (ushort)(B * UQ15One + 0.5);

			return UnFix15ToByte(r * rY + g * gY + b * bY);
		}
	}

	internal static class Rec709Luma
	{
		public const double R = 0.2126;
		public const double B = 0.0722;
		public const double G = 1 - R - B;
		public static readonly Vector3 Coefficients = new Vector3((float)B, (float)G, (float)R);

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static ushort FromBgr(ushort b, ushort g, ushort r)
		{
			const uint rY = (ushort)(R * UQ15One + 0.5);
			const uint gY = (ushort)(G * UQ15One + 0.5);
			const uint bY = (ushort)(B * UQ15One + 0.5);

			return UnFixToUQ15One(r * rY + g * gY + b * bY);
		}
	}

	internal static class GreyConverter
	{
		unsafe public static void ConvertLine(PixelFormat inFormat, byte* ipstart, byte* opstart, int cbIn, int cbOut)
		{
			if (inFormat == PixelFormat.Grey32BppLinearFloat || inFormat == PixelFormat.Y32BppLinearFloat)
				greyLinearToGreyFloat(ipstart, opstart, cbIn);
			else if (inFormat == PixelFormat.Grey16BppLinearUQ15 || inFormat == PixelFormat.Y16BppLinearUQ15)
				greyLinearToGreyUQ15(ipstart, opstart, cbIn);
			else if (inFormat == PixelFormat.Bgr24Bpp)
				bgrToGreyByte(ipstart, opstart, cbIn);
			else if (inFormat == PixelFormat.Bgr48BppLinearUQ15)
				bgrToGreyUQ15(ipstart, opstart, cbIn);
			else if (inFormat == PixelFormat.Bgrx32Bpp || inFormat == PixelFormat.Bgra32Bpp || inFormat == PixelFormat.Pbgra32Bpp)
				bgrxToGreyByte(ipstart, opstart, cbIn);
			else if (inFormat == PixelFormat.Pbgra64BppLinearUQ15)
				bgrxToGreyUQ15(ipstart, opstart, cbIn);
			else if (inFormat == PixelFormat.Bgr96BppFloat)
				bgrToGreyFloat(ipstart, opstart, cbIn, false);
			else if (inFormat == PixelFormat.Bgrx128BppFloat || inFormat == PixelFormat.Pbgra128BppFloat)
				bgrxToGreyFloat(ipstart, opstart, cbIn, false);
			else if (inFormat == PixelFormat.Bgr96BppLinearFloat)
			{
				bgrToGreyFloat(ipstart, opstart, cbIn, true);
				greyLinearToGreyFloat(opstart, opstart, cbOut);
			}
			else if (inFormat == PixelFormat.Bgrx128BppLinearFloat || inFormat == PixelFormat.Pbgra128BppLinearFloat)
			{
				bgrxToGreyFloat(ipstart, opstart, cbIn, true);
				greyLinearToGreyFloat(opstart, opstart, cbOut);
			}
			else
				throw new NotSupportedException("Unsupported pixel format");
		}

		unsafe private static void bgrToGreyByte(byte* ipstart, byte* opstart, int cb)
		{
			byte* ip = ipstart, ipe = ipstart + cb - 3, op = opstart;

			while (ip <= ipe)
			{
				byte y = Rec601Luma.FromBgr(ip[0], ip[1], ip[2]);
				op[0] = y;

				ip += 3;
				op++;
			}
		}

		unsafe private static void bgrToGreyUQ15(byte* ipstart, byte* opstart, int cb)
		{
			fixed (byte* gtstart = &LookupTables.SrgbGammaUQ15[0])
			{
				ushort* ip = (ushort*)ipstart, ipe = (ushort*)(ipstart + cb) - 3, op = (ushort*)opstart;
				byte* gt = gtstart;

				while (ip <= ipe)
				{
					uint y = Rec709Luma.FromBgr(ip[0], ip[1], ip[2]);
					op[0] = gt[y];

					ip += 3;
					op++;
				}
			}
		}

		unsafe private static void bgrToGreyFloat(byte* ipstart, byte* opstart, int cb, bool linear)
		{
			float* ip = (float*)ipstart, ipe = (float*)(ipstart + cb) - 3, op = (float*)opstart;
			var clum = linear ? Rec709Luma.Coefficients : Rec601Luma.Coefficients;
			float cbl = clum.X, cgl = clum.Y, crl = clum.Z;

			while (ip <= ipe)
			{
				float c0 = ip[0] * cbl, c1 = ip[1] * cgl, c2 = ip[2] * crl;
				op[0] = c0 + c1 + c2;

				ip += 3;
				op++;
			}
		}

		unsafe private static void bgrxToGreyByte(byte* ipstart, byte* opstart, int cb)
		{
			byte* ip = ipstart, ipe = ipstart + cb - 4, op = opstart;

			while (ip <= ipe)
			{
				byte y = Rec601Luma.FromBgr(ip[0], ip[1], ip[2]);
				op[0] = y;

				ip += 4;
				op++;
			}
		}

		unsafe private static void bgrxToGreyUQ15(byte* ipstart, byte* opstart, int cb)
		{
			fixed (byte* gtstart = &LookupTables.SrgbGammaUQ15[0])
			{
				ushort* ip = (ushort*)ipstart, ipe = (ushort*)(ipstart + cb) - 4, op = (ushort*)opstart;
				byte* gt = gtstart;

				while (ip <= ipe)
				{
					uint y = Rec709Luma.FromBgr(ip[0], ip[1], ip[2]);
					op[0] = gt[y];

					ip += 4;
					op++;
				}
			}
		}

		unsafe private static void bgrxToGreyFloat(byte* ipstart, byte* opstart, int cb, bool linear)
		{
			float* ip = (float*)ipstart, ipe = (float*)(ipstart + cb) - 4, op = (float*)opstart;
			var clum = new Vector4(linear ? Rec709Luma.Coefficients : Rec601Luma.Coefficients, 0f);

			while (ip <= ipe)
			{
				*op++ = Vector4.Dot(Unsafe.ReadUnaligned<Vector4>(ip), clum);
				ip += 4;
			}
		}

		unsafe private static void greyLinearToGreyUQ15(byte* ipstart, byte* opstart, int cb)
		{
			fixed (byte* gtstart = &LookupTables.SrgbGammaUQ15[0])
			{
				ushort* ip = (ushort*)ipstart, ipe = (ushort*)(ipstart + cb), op = (ushort*)opstart;
				byte* gt = gtstart;

				while (ip < ipe)
					*op++ = gt[(uint)ClampToUQ15One((uint)*ip++)];
			}
		}

#if HWINTRINSICS
		[MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
		unsafe private static void greyLinearToGreyFloat(byte* ipstart, byte* opstart, int cb)
		{
			float* ip = (float*)ipstart, ipe = (float*)(ipstart + cb), op = (float*)opstart;

#if HWINTRINSICS
			if (Avx.IsSupported)
			{
				var vzero = Vector256<float>.Zero;

				ipe -= Vector256<float>.Count;
				while (ip <= ipe)
				{
					var v = Avx.Max(vzero, Avx.LoadVector256(ip));
					ip += Vector256<float>.Count;

					v = Avx.Sqrt(v);

					Avx.Store(op, v);
					op += Vector256<float>.Count;
				}
				ipe += Vector256<float>.Count;
			}
			else
#endif
			{
				var vzero = VectorF.Zero;

				ipe -= VectorF.Count;
				while (ip <= ipe)
				{
					var v = Unsafe.ReadUnaligned<VectorF>(ip);
					ip += VectorF.Count;

					v = Vector.SquareRoot(Vector.Max(v, vzero));

					Unsafe.WriteUnaligned(op, v);
					op += VectorF.Count;
				}
				ipe += VectorF.Count;
			}

			float fmin = Vector4.Zero.X;
			while (ip < ipe)
				*op++ = MaxF(*ip++, fmin).Sqrt();
		}
	}
}
