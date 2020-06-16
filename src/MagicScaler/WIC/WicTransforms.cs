﻿using System;
using System.Buffers;
using System.Diagnostics;

using PhotoSauce.Interop.Wic;
using PhotoSauce.MagicScaler.Transforms;

namespace PhotoSauce.MagicScaler
{
	internal static class WicTransforms
	{
		public static readonly Guid[] PlanarPixelFormats = new[] { Consts.GUID_WICPixelFormat8bppY, Consts.GUID_WICPixelFormat8bppCb, Consts.GUID_WICPixelFormat8bppCr };

		public static void AddColorProfileReader(PipelineContext ctx)
		{
			if (!(ctx.ImageFrame is WicImageFrame wicFrame) || ctx.Settings.ColorProfileMode == ColorProfileMode.Ignore)
				return;

			ctx.WicContext.SourceColorContext = WicColorProfile.GetSourceProfile(wicFrame.ColorProfileSource, ctx.Settings.ColorProfileMode).WicColorContext;
			ctx.WicContext.DestColorContext = WicColorProfile.GetDestProfile(wicFrame.ColorProfileSource, ctx.Settings.ColorProfileMode).WicColorContext;
		}

		public static void AddColorspaceConverter(PipelineContext ctx)
		{
			if (ctx.WicContext.SourceColorContext is null || ctx.WicContext.DestColorContext is null || ctx.WicContext.SourceColorContext == ctx.WicContext.DestColorContext)
				return;

			var trans = ctx.WicContext.AddRef(Wic.Factory.CreateColorTransformer());
			if (trans.TryInitialize(ctx.Source.AsIWICBitmapSource(), ctx.WicContext.SourceColorContext, ctx.WicContext.DestColorContext, ctx.Source.Format.FormatGuid))
				ctx.Source = trans.AsPixelSource(nameof(IWICColorTransform));
		}

		public static void AddPixelFormatConverter(PipelineContext ctx, bool allowPbgra = true)
		{
			var curFormat = ctx.Source.Format;
			if (curFormat.ColorRepresentation == PixelColorRepresentation.Cmyk)
			{
				var rgbColorContext = ctx.Settings.ColorProfileMode == ColorProfileMode.ConvertToSrgb ? WicColorProfile.Srgb.Value : WicColorProfile.AdobeRgb.Value;
				ctx.WicContext.SourceColorContext ??= WicColorProfile.Cmyk.Value.WicColorContext;

				// TODO WIC doesn't support proper CMYKA conversion with color profile
				if (curFormat.AlphaRepresentation == PixelAlphaRepresentation.None)
				{
					// WIC doesn't support 16bpc CMYK conversion with color profile
					if (curFormat.BitsPerPixel == 64)
						ctx.Source = ctx.AddDispose(new ConversionTransform(ctx.Source, null, null, PixelFormat.Cmyk32Bpp));

					var trans = ctx.WicContext.AddRef(Wic.Factory.CreateColorTransformer());
					if (trans.TryInitialize(ctx.Source.AsIWICBitmapSource(), ctx.WicContext.SourceColorContext, rgbColorContext.WicColorContext, Consts.GUID_WICPixelFormat24bppBGR))
					{
						ctx.Source = trans.AsPixelSource(nameof(IWICColorTransform));
						curFormat = ctx.Source.Format;
					}
				}

				ctx.WicContext.DestColorContext = ctx.WicContext.SourceColorContext = rgbColorContext.WicColorContext;
				ctx.DestColorProfile = ctx.SourceColorProfile = rgbColorContext.ParsedProfile;
			}

			if (curFormat == PixelFormat.Y8Bpp || curFormat == PixelFormat.Cb8Bpp || curFormat == PixelFormat.Cr8Bpp)
				return;

			var newFormat = PixelFormat.Bgr24Bpp;
			if (allowPbgra && curFormat.AlphaRepresentation == PixelAlphaRepresentation.Associated && ctx.Settings.BlendingMode != GammaMode.Linear && ctx.Settings.MatteColor.IsEmpty)
				newFormat = PixelFormat.Pbgra32Bpp;
			else if (curFormat.AlphaRepresentation != PixelAlphaRepresentation.None)
				newFormat = PixelFormat.Bgra32Bpp;
			else if (curFormat.ColorRepresentation == PixelColorRepresentation.Grey)
				newFormat = PixelFormat.Grey8Bpp;

			if (curFormat == newFormat)
				return;

			var conv = ctx.WicContext.AddRef(Wic.Factory.CreateFormatConverter());
			if (!conv.CanConvert(curFormat.FormatGuid, newFormat.FormatGuid))
				throw new NotSupportedException("Can't convert to destination pixel format");

			conv.Initialize(ctx.Source.AsIWICBitmapSource(), newFormat.FormatGuid, WICBitmapDitherType.WICBitmapDitherTypeNone, null, 0.0, WICBitmapPaletteType.WICBitmapPaletteTypeCustom);
			ctx.Source = conv.AsPixelSource($"{nameof(IWICFormatConverter)}: {curFormat.Name}->{newFormat.Name}");
		}

		public static void AddIndexedColorConverter(PipelineContext ctx)
		{
			var curFormat = ctx.Source.Format;
			var newFormat = PixelFormat.Indexed8Bpp;

			if (!ctx.Settings.IndexedColor || curFormat.NumericRepresentation == PixelNumericRepresentation.Indexed || curFormat.ColorRepresentation == PixelColorRepresentation.Grey)
				return;

			var conv = ctx.WicContext.AddRef(Wic.Factory.CreateFormatConverter());
			if (!conv.CanConvert(curFormat.FormatGuid, newFormat.FormatGuid))
				throw new NotSupportedException("Can't convert to destination pixel format");

			var bmp = ctx.WicContext.AddRef(Wic.Factory.CreateBitmapFromSource(ctx.Source.AsIWICBitmapSource(), WICBitmapCreateCacheOption.WICBitmapCacheOnDemand));
			ctx.Source = bmp.AsPixelSource(nameof(IWICBitmap));

			var pal = ctx.WicContext.AddRef(Wic.Factory.CreatePalette());
			pal.InitializeFromBitmap(ctx.Source.AsIWICBitmapSource(), 256u, curFormat.AlphaRepresentation != PixelAlphaRepresentation.None);
			ctx.WicContext.DestPalette = pal;

			conv.Initialize(ctx.Source.AsIWICBitmapSource(), newFormat.FormatGuid, WICBitmapDitherType.WICBitmapDitherTypeErrorDiffusion, pal, 33.33, WICBitmapPaletteType.WICBitmapPaletteTypeCustom);
			ctx.Source = conv.AsPixelSource($"{nameof(IWICFormatConverter)}: {curFormat.Name}->{newFormat.Name}", false);
		}

		public static void AddExifFlipRotator(PipelineContext ctx)
		{
			if (ctx.Orientation == Orientation.Normal)
				return;

			var rotator = ctx.WicContext.AddRef(Wic.Factory.CreateBitmapFlipRotator());
			rotator.Initialize(ctx.Source.AsIWICBitmapSource(), ctx.Orientation.ToWicTransformOptions());

			ctx.Source = rotator.AsPixelSource(nameof(IWICBitmapFlipRotator));

			if (ctx.Orientation.RequiresCache())
			{
				var crop = ctx.Settings.Crop;
				var bmp = ctx.WicContext.AddRef(Wic.Factory.CreateBitmapFromSourceRect(ctx.Source.AsIWICBitmapSource(), (uint)crop.X, (uint)crop.Y, (uint)crop.Width, (uint)crop.Height));

				ctx.Source = bmp.AsPixelSource(nameof(IWICBitmap));
				ctx.Settings.Crop = ctx.Source.Area.ToGdiRect();
			}

			ctx.Orientation = Orientation.Normal;
		}

		public static void AddCropper(PipelineContext ctx)
		{
			var crop = PixelArea.FromGdiRect(ctx.Settings.Crop);
			if (crop == ctx.Source.Area)
				return;

			var cropper = ctx.WicContext.AddRef(Wic.Factory.CreateBitmapClipper());
			cropper.Initialize(ctx.Source.AsIWICBitmapSource(), crop.ToWicRect());

			ctx.Source = cropper.AsPixelSource(nameof(IWICBitmapClipper));
			ctx.Settings.Crop = ctx.Source.Area.ToGdiRect();
		}

		public static void AddScaler(PipelineContext ctx)
		{
			bool swap = ctx.Orientation.SwapsDimensions();
			var tsize = ctx.Settings.InnerSize;

			int width = swap ? tsize.Height : tsize.Width, height = swap ? tsize.Width : tsize.Height;
			if (ctx.Source.Width == width && ctx.Source.Height == height)
				return;

			var mode =
				ctx.Settings.Interpolation.WeightingFunction.Support < 0.1 ? WICBitmapInterpolationMode.WICBitmapInterpolationModeNearestNeighbor :
				ctx.Settings.Interpolation.WeightingFunction.Support < 1.0 ? ctx.Settings.ScaleRatio > 1.0 ? WICBitmapInterpolationMode.WICBitmapInterpolationModeFant : WICBitmapInterpolationMode.WICBitmapInterpolationModeNearestNeighbor :
				ctx.Settings.Interpolation.WeightingFunction.Support > 1.0 ? ctx.Settings.ScaleRatio > 1.0 ? WICBitmapInterpolationMode.WICBitmapInterpolationModeHighQualityCubic :WICBitmapInterpolationMode.WICBitmapInterpolationModeCubic :
				ctx.Settings.ScaleRatio > 1.0 ? WICBitmapInterpolationMode.WICBitmapInterpolationModeFant :
				WICBitmapInterpolationMode.WICBitmapInterpolationModeLinear;

			var scaler = ctx.WicContext.AddRef(Wic.Factory.CreateBitmapScaler());
			scaler.Initialize(ctx.Source.AsIWICBitmapSource(), (uint)width, (uint)height, mode);

			ctx.Source = scaler.AsPixelSource(nameof(IWICBitmapScaler));
		}

		public static void AddHybridScaler(PipelineContext ctx, int ratio = default)
		{
			ratio = ratio == default ? ctx.Settings.HybridScaleRatio : ratio;
			if (ratio == 1 || ctx.Settings.Interpolation.WeightingFunction.Support < 0.1)
				return;

			uint width = (uint)MathUtil.DivCeiling(ctx.Source.Width, ratio);
			uint height = (uint)MathUtil.DivCeiling(ctx.Source.Height, ratio);

			if (ctx.Source is WicPixelSource wsrc && wsrc.WicSource is IWICBitmapSourceTransform)
				ctx.Source = wsrc.WicSource.AsPixelSource(nameof(IWICBitmapFrameDecode));

			var scaler = ctx.WicContext.AddRef(Wic.Factory.CreateBitmapScaler());
			scaler.Initialize(ctx.Source.AsIWICBitmapSource(), width, height, WICBitmapInterpolationMode.WICBitmapInterpolationModeFant);

			ctx.Source = scaler.AsPixelSource(nameof(IWICBitmapScaler) + " (hybrid)");
			ctx.Settings.HybridMode = HybridScaleMode.Off;
		}

		public static void AddNativeScaler(PipelineContext ctx)
		{
			int ratio = ctx.Settings.HybridScaleRatio;
			if (ratio == 1 || !(ctx.ImageFrame is WicImageFrame wicFrame) || !wicFrame.SupportsNativeScale || !(ctx.Source is WicPixelSource wsrc) || !(wsrc.WicSource is IWICBitmapSourceTransform trans))
				return;

			uint ow = (uint)ctx.Source.Width, oh = (uint)ctx.Source.Height;
			uint cw = (uint)MathUtil.DivCeiling((int)ow, ratio), ch = (uint)MathUtil.DivCeiling((int)oh, ratio);
			trans.GetClosestSize(ref cw, ref ch);

			if (cw == ow && ch == oh)
				return;

			var orient = ctx.Orientation;
			var scaler = ctx.WicContext.AddRef(Wic.Factory.CreateBitmapScaler());
			scaler.Initialize(ctx.Source.AsIWICBitmapSource(), cw, ch, WICBitmapInterpolationMode.WICBitmapInterpolationModeFant);

			ctx.Source = scaler.AsPixelSource(nameof(IWICBitmapSourceTransform));
			ctx.Settings.Crop = PixelArea.FromGdiRect(ctx.Settings.Crop).DeOrient(orient, (int)ow, (int)oh).ProportionalScale((int)ow, (int)oh, (int)cw, (int)ch).ReOrient(orient, (int)cw, (int)ch).ToGdiRect();
			ctx.Settings.HybridMode = HybridScaleMode.Off;
		}

		public static void AddPlanarCache(PipelineContext ctx)
		{
			if (!(ctx.Source is WicPixelSource wsrc) || !(wsrc.WicSource is IWICPlanarBitmapSourceTransform trans))
				throw new NotSupportedException("Transform chain doesn't support planar mode.  Only JPEG Decoder, Rotator, Scaler, and PixelFormatConverter are allowed");

			int ratio = ctx.Settings.HybridScaleRatio.Clamp(1, 8);
			uint ow = (uint)ctx.Source.Width, oh = (uint)ctx.Source.Height;
			uint cw = (uint)MathUtil.DivCeiling((int)ow, ratio), ch = (uint)MathUtil.DivCeiling((int)oh, ratio);

			var desc = ArrayPool<WICBitmapPlaneDescription>.Shared.Rent(PlanarPixelFormats.Length);
			if (!trans.DoesSupportTransform(ref cw, ref ch, WICBitmapTransformOptions.WICBitmapTransformRotate0, WICPlanarOptions.WICPlanarOptionsDefault, PlanarPixelFormats, desc, (uint)PlanarPixelFormats.Length))
				throw new NotSupportedException("Requested planar transform not supported");

			var crop = PixelArea.FromGdiRect(ctx.Settings.Crop).DeOrient(ctx.Orientation, (int)ow, (int)oh).ProportionalScale((int)ow, (int)oh, (int)cw, (int)ch);
			var cache = ctx.AddDispose(new WicPlanarCache(trans, desc, WICBitmapTransformOptions.WICBitmapTransformRotate0, cw, ch, crop));

			ArrayPool<WICBitmapPlaneDescription>.Shared.Return(desc);

			ctx.PlanarContext = new PipelineContext.PlanarPipelineContext(cache.SourceY, cache.SourceCb, cache.SourceCr);
			ctx.Source = ctx.PlanarContext.SourceY;
			ctx.Settings.Crop = ctx.Source.Area.ReOrient(ctx.Orientation, ctx.Source.Width, ctx.Source.Height).ToGdiRect();
			ctx.Settings.HybridMode = HybridScaleMode.Off;
		}

		public static void AddPlanarConverter(PipelineContext ctx)
		{
			Debug.Assert(ctx.PlanarContext is not null);

			var planes = new[] { ctx.PlanarContext.SourceY.AsIWICBitmapSource(), ctx.PlanarContext.SourceCb.AsIWICBitmapSource(), ctx.PlanarContext.SourceCr.AsIWICBitmapSource() };
			var conv = (IWICPlanarFormatConverter)ctx.WicContext.AddRef(Wic.Factory.CreateFormatConverter());
			conv.Initialize(planes, (uint)planes.Length, Consts.GUID_WICPixelFormat24bppBGR, WICBitmapDitherType.WICBitmapDitherTypeNone, null, 0.0, WICBitmapPaletteType.WICBitmapPaletteTypeCustom);

			ctx.Source = conv.AsPixelSource(nameof(IWICPlanarFormatConverter));
			ctx.PlanarContext = null;
		}
	}
}