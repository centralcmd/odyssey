using System.Text;
using Odyssey.Core.Imaging;
using Odyssey.Dtos.Recipes;

namespace Odyssey.Core.Recipes.Import;

/// <summary>
/// The per-source preparation both the create path and the worker run (issue #602 §6): images through the one still
/// image pipeline — its stripped bytes are what is stored and what is sent — and text files as strict UTF-8.
/// </summary>
public static class RecipeImportSourcePreparation
{
    public static readonly StillImagePolicy ImagePolicy = new(
        ["image/jpeg", "image/png", "image/webp"], "PNG, JPEG or WebP", RecipeImportLimits.MaxImageDimension, "a recipe photo");

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Validates and strips an image. Throws <see cref="DomainValidationException"/> on an unacceptable image and
    /// <see cref="ImageTooLargeAfterStripException"/> when even the stripped bytes exceed the per-image cap.
    /// </summary>
    public static ValidatedImage StripImage(byte[] raw, string contentType)
    {
        var image = StillImageValidator.Validate(raw, contentType, RecipeImportLimits.MaxExistingImageRawBytes, ImagePolicy);
        if (image.Bytes.Length > RecipeImportLimits.MaxImageBytes)
        {
            throw new ImageTooLargeAfterStripException();
        }

        return image;
    }

    /// <summary>The text of a text file, or <c>null</c> when it is not strict UTF-8 or holds a NUL.</summary>
    public static string? DecodeText(byte[] raw)
    {
        try
        {
            var text = StrictUtf8.GetString(raw);
            if (text.Length > 0 && text[0] == '﻿')
            {
                text = text[1..];
            }

            return text.Contains('\0') ? null : text;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>The encoded request size of a set of sources (A1): base64 for files, UTF-8 bytes for text, plus the prompt.</summary>
    public static long EncodedTotal(IEnumerable<(bool IsFile, long Bytes)> sources) =>
        RecipeImportLimits.PromptAllowanceBytes + sources.Sum(s => RecipeImportLimits.EncodedBytes(s.Bytes, s.IsFile));
}

/// <summary>A still image that is still over the per-image cap after its metadata was stripped.</summary>
public sealed class ImageTooLargeAfterStripException() : Exception("The image is too large to send, even without its metadata.");
