﻿using System;
using System.IO;
using System.Collections.Generic;

using PhotoSauce.Interop.Wic;

namespace PhotoSauce.MagicScaler
{
	/// <summary>Represents basic information about an image container.</summary>
	public sealed class ImageFileInfo
	{
		/// <summary>Represents basic information about an image frame within a container.</summary>
		public readonly struct FrameInfo
		{
			/// <summary>The width of the image frame in pixels.</summary>
			public int Width { get; }
			/// <summary>The height of the image frame in pixels.</summary>
			public int Height { get; }
			/// <summary>True if the image frame contains transparency data, otherwise false.</summary>
			public bool HasAlpha { get; }
			/// <summary>
			/// The stored <a href="https://magnushoff.com/articles/jpeg-orientation/">Exif orientation</a> for the image frame.
			/// The <see cref="Width" /> and <see cref="Height" /> values reflect the corrected orientation, not the stored orientation.
			/// </summary>
			public Orientation ExifOrientation { get; }

			/// <summary>Constructs a new <see cref="FrameInfo" /> instance with the supplied values.</summary>
			/// <param name="width">The width of the image frame in pixels.</param>
			/// <param name="height">The height of the image frame in pixels.</param>
			/// <param name="hasAlpha">True if the image frame contains transparency data, otherwise false.</param>
			/// <param name="orientation">The Exif orientation associated with the image frame.</param>
			public FrameInfo(int width, int height, bool hasAlpha, Orientation orientation)
			{
				Width = width;
				Height = height;
				HasAlpha = hasAlpha;
				ExifOrientation = orientation;
			}
		}

		/// <summary>The size of the image container in bytes.</summary>
		public long FileSize { get; }
		/// <summary>The last modified date of the image container, if applicable.</summary>
		public DateTime FileDate { get; }
		/// <summary>The format of the image container (e.g. JPEG, PNG).</summary>
		public FileFormat ContainerType { get; private set; }
		/// <summary>One or more <see cref="FrameInfo" /> instances describing each image frame in the container.</summary>
		public IReadOnlyList<FrameInfo> Frames { get; private set; }

		/// <summary>Constructs a new <see cref="ImageFileInfo" /> instance with a single frame of the specified <paramref name="width" /> and <paramref name="height" />.</summary>
		/// <param name="width">The width of the image frame in pixels.</param>
		/// <param name="height">The height of the image frame in pixels.</param>
		public ImageFileInfo(int width, int height)
		{
			Frames = new[] { new FrameInfo(width, height, false, Orientation.Normal) };
			ContainerType = FileFormat.Unknown;
		}

		/// <summary>Constructs a new <see cref="ImageFileInfo" /> instance the specified values.</summary>
		/// <param name="containerType">The <see cref="FileFormat" /> of the image container.</param>
		/// <param name="frames">A list containing one <see cref="FrameInfo" /> per image frame in the container.</param>
		/// <param name="fileSize">The size in bytes of the image file.</param>
		/// <param name="fileDate">The last modified date of the image file.</param>
		public ImageFileInfo(FileFormat containerType, IReadOnlyList<FrameInfo> frames, long fileSize, DateTime fileDate)
		{
			ContainerType = containerType;
			Frames = frames;
			FileSize = fileSize;
			FileDate = fileDate;
		}

		/// <summary>Constructs a new <see cref="ImageFileInfo" /> instance by reading the metadata from an image file header.</summary>
		/// <param name="imgPath">The path to the image file.</param>
		public static ImageFileInfo Load(string imgPath)
		{
			var fi = new FileInfo(imgPath);
			if (!fi.Exists)
				throw new FileNotFoundException("File not found", imgPath);

			using var ctx = new PipelineContext(new ProcessImageSettings());
			ctx.ImageContainer = WicImageDecoder.Load(imgPath, ctx);

			return fromWicImage(ctx, fi.Length, fi.LastWriteTimeUtc);
		}

		/// <inheritdoc cref="Load(ReadOnlySpan{byte}, DateTime)" />
		public static ImageFileInfo Load(ReadOnlySpan<byte> imgBuffer) => Load(imgBuffer, DateTime.MinValue);

		/// <summary>Constructs a new <see cref="ImageFileInfo" /> instance by reading the metadata from an image file contained in a <see cref="ReadOnlySpan{T}" />.</summary>
		/// <param name="imgBuffer">The buffer containing the image data.</param>
		/// <param name="lastModified">The last modified date of the image container.</param>
		unsafe public static ImageFileInfo Load(ReadOnlySpan<byte> imgBuffer, DateTime lastModified)
		{
			if (imgBuffer == default) throw new ArgumentNullException(nameof(imgBuffer));

			fixed (byte* pbBuffer = imgBuffer)
			{
				using var ctx = new PipelineContext(new ProcessImageSettings());
				ctx.ImageContainer = WicImageDecoder.Load(pbBuffer, imgBuffer.Length, ctx);

				return fromWicImage(ctx, imgBuffer.Length, lastModified);
			}
		}

		/// <inheritdoc cref="Load(Stream, DateTime)" />
		public static ImageFileInfo Load(Stream imgStream) => Load(imgStream, DateTime.MinValue);

		/// <summary>Constructs a new <see cref="ImageFileInfo" /> instance by reading the metadata from an image file exposed by a <see cref="Stream" />.</summary>
		/// <param name="imgStream">The stream containing the image data.</param>
		/// <param name="lastModified">The last modified date of the image container.</param>
		public static ImageFileInfo Load(Stream imgStream, DateTime lastModified)
		{
			if (imgStream is null) throw new ArgumentNullException(nameof(imgStream));
			if (!imgStream.CanSeek || !imgStream.CanRead) throw new ArgumentException("Input Stream must allow Seek and Read", nameof(imgStream));
			if (imgStream.Length <= 0 || imgStream.Position >= imgStream.Length) throw new ArgumentException("Input Stream is empty or positioned at its end", nameof(imgStream));

			using var ctx = new PipelineContext(new ProcessImageSettings());
			ctx.ImageContainer = WicImageDecoder.Load(imgStream, ctx);

			return fromWicImage(ctx, imgStream.Length, lastModified);
		}

		private static ImageFileInfo fromWicImage(PipelineContext ctx, long fileSize, DateTime fileDate)
		{
			var cont = (WicImageContainer)ctx.ImageContainer;
			var cfmt = cont.ContainerFormat;
			var frames = cfmt == FileFormat.Gif ? getGifFrameInfo(cont) : getFrameInfo(cont);

			return new ImageFileInfo(cfmt, frames, fileSize, fileDate);
		}

		private static FrameInfo[] getFrameInfo(WicImageContainer cont)
		{
			var frames = new FrameInfo[cont.FrameCount];
			for (int i = 0; i < frames.Length; i++)
			{
				using var frame = (WicImageFrame)cont.GetFrame(i);
				frame.WicSource.GetSize(out uint width, out uint height);

				var pixfmt = PixelFormat.FromGuid(frame.WicSource.GetPixelFormat());
				var orient = frame.ExifOrientation;
				if (orient.SwapsDimensions())
					(width, height) = (height, width);

				frames[i] = new FrameInfo((int)width, (int)height, pixfmt.AlphaRepresentation != PixelAlphaRepresentation.None, orient);
			}

			return frames;
		}

		private static FrameInfo[] getGifFrameInfo(WicImageContainer cont)
		{
			using var cmeta = ComHandle.Wrap(cont.WicDecoder.GetMetadataQueryReader());
			int cwidth = cmeta.ComObject.GetValueOrDefault<ushort>(Wic.Metadata.Gif.LogicalScreenWidth);
			int cheight = cmeta.ComObject.GetValueOrDefault<ushort>(Wic.Metadata.Gif.LogicalScreenHeight);

			bool alpha = cont.FrameCount > 1;
			if (!alpha)
			{
				using var frame = ComHandle.Wrap(cont.WicDecoder.GetFrame(0));
				using var fmeta = ComHandle.Wrap(frame.ComObject.GetMetadataQueryReader());
				alpha = fmeta.ComObject.GetValueOrDefault<bool>(Wic.Metadata.Gif.TransparencyFlag);
			}

			var frames = new FrameInfo[cont.FrameCount];
			for (int i = 0; i < frames.Length; i++)
				frames[i] = new FrameInfo(cwidth, cheight, alpha, Orientation.Normal);

			return frames;
		}
	}
}