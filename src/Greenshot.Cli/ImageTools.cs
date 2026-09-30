/*
 * Greenshot - a free and open source screenshot tool
 * Copyright (C) 2007-2026 Thomas Braun, Jens Klingen, Robin Krom
 *
 * For more information see: https://getgreenshot.org/
 * The Greenshot project is hosted on GitHub https://github.com/greenshot/greenshot
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 1 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;

namespace Greenshot.Cli;

internal enum StackLayout
{
    Horizontal, Vertical, Grid
}

/// <summary>
/// Image operations that are not editor elements: preview copies, coordinate grid, combining images, finding differences
/// </summary>
internal static class ImageTools
{
    /// <summary>
    /// Smaller copy for looking at, never bigger than the original. Give the width or the longest side.
    /// </summary>
    public static Image Preview(Image image, int? width, int? maxSide)
    {
        int newWidth = image.Width;
        int newHeight = image.Height;
        if (width.HasValue)
        {
            newWidth = width.Value;
            newHeight = (int) Math.Round((double) image.Height * width.Value / image.Width);
        }
        else
        {
            int limit = maxSide ?? 1200;
            int longest = Math.Max(image.Width, image.Height);
            if (longest > limit)
            {
                newWidth = (int) Math.Round((double) image.Width * limit / longest);
                newHeight = (int) Math.Round((double) image.Height * limit / longest);
            }
        }
        if (newWidth >= image.Width)
        {
            return new Bitmap(image);
        }
        return Scale(image, Math.Max(1, newWidth), Math.Max(1, newHeight));
    }

    public static Bitmap Scale(Image image, int width, int height)
    {
        var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(result);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.DrawImage(image, new Rectangle(0, 0, width, height), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel);
        return result;
    }

    /// <summary>
    /// A copy with a line every step pixels and the coordinates written next to them, to read positions for annotations
    /// </summary>
    public static Image DrawGrid(Image image, int step)
    {
        var result = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(result);
        graphics.DrawImage(image, 0, 0, image.Width, image.Height);
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using var minorPen = new Pen(Color.FromArgb(110, 255, 0, 255));
        using var majorPen = new Pen(Color.FromArgb(190, 255, 0, 255));
        using var font = new Font("Consolas", 9f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(Color.White);
        using var backBrush = new SolidBrush(Color.FromArgb(200, 120, 0, 120));

        void Label(string text, float x, float y)
        {
            var size = graphics.MeasureString(text, font);
            graphics.FillRectangle(backBrush, x, y, size.Width, size.Height);
            graphics.DrawString(text, font, textBrush, x, y);
        }

        bool intersections = step >= 100 && (long) (image.Width / step) * (image.Height / step) <= 200;
        for (int x = 0; x < image.Width; x += step)
        {
            graphics.DrawLine(x % (step * 5) == 0 ? majorPen : minorPen, x, 0, x, image.Height);
        }
        for (int y = 0; y < image.Height; y += step)
        {
            graphics.DrawLine(y % (step * 5) == 0 ? majorPen : minorPen, 0, y, image.Width, y);
        }
        for (int x = 0; x < image.Width; x += step)
        {
            Label(x.ToString(), x + 1, 1);
        }
        for (int y = step; y < image.Height; y += step)
        {
            Label(y.ToString(), 1, y + 1);
        }
        if (intersections)
        {
            for (int y = step; y < image.Height; y += step)
            {
                for (int x = step; x < image.Width; x += step)
                {
                    Label($"{x},{y}", x + 2, y + 2);
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Put images next to each other. With a tile width every image is scaled to it, keeping its proportions.
    /// Returns the image and where every tile went.
    /// </summary>
    public static Bitmap Combine(IReadOnlyList<Image> images, StackLayout layout, int columns, int gap, Color gapColor, int tileWidth, out List<Rectangle> tiles)
    {
        var scaled = new List<Image>();
        try
        {
            foreach (var image in images)
            {
                scaled.Add(tileWidth > 0 && tileWidth != image.Width
                    ? Scale(image, tileWidth, Math.Max(1, (int) Math.Round((double) image.Height * tileWidth / image.Width)))
                    : image);
            }

            if (layout == StackLayout.Horizontal) columns = scaled.Count;
            else if (layout == StackLayout.Vertical) columns = 1;
            columns = Math.Max(1, Math.Min(columns, scaled.Count));
            int rows = (scaled.Count + columns - 1) / columns;

            // every column is as wide as its widest tile, every row as high as its highest one
            var columnWidths = new int[columns];
            var rowHeights = new int[rows];
            for (int i = 0; i < scaled.Count; i++)
            {
                columnWidths[i % columns] = Math.Max(columnWidths[i % columns], scaled[i].Width);
                rowHeights[i / columns] = Math.Max(rowHeights[i / columns], scaled[i].Height);
            }

            long width = columnWidths.Sum() + (long) gap * (columns - 1);
            long height = rowHeights.Sum() + (long) gap * (rows - 1);
            if (width * height > 250_000_000L || width > 32000 || height > 32000)
            {
                throw new CliException($"the combined image would be {width}x{height}, use --width to make the tiles smaller");
            }

            var result = new Bitmap((int) width, (int) height, PixelFormat.Format32bppArgb);
            tiles = new List<Rectangle>();
            using var graphics = Graphics.FromImage(result);
            graphics.Clear(gapColor);
            int y = 0;
            for (int row = 0; row < rows; row++)
            {
                int x = 0;
                for (int column = 0; column < columns; column++)
                {
                    int index = row * columns + column;
                    if (index < scaled.Count)
                    {
                        graphics.DrawImage(scaled[index], x, y, scaled[index].Width, scaled[index].Height);
                        tiles.Add(new Rectangle(x, y, scaled[index].Width, scaled[index].Height));
                    }
                    x += columnWidths[column] + gap;
                }
                y += rowHeights[row] + gap;
            }
            return result;
        }
        finally
        {
            foreach (var image in scaled.Where(s => !images.Contains(s)))
            {
                image.Dispose();
            }
        }
    }

    /// <summary>
    /// The areas where two images of the same size differ: pixels that differ by more than the threshold (0-255, largest
    /// channel difference) are collected in 8x8 blocks, blocks closer than merge pixels are grouped, groups smaller than
    /// minArea square pixels are ignored.
    /// </summary>
    public static List<Rectangle> Differences(Bitmap before, Bitmap after, int threshold, int minArea, int merge, out long changedPixels)
    {
        if (before.Size != after.Size)
        {
            throw new CliException($"the images have different sizes ({before.Width}x{before.Height} and {after.Width}x{after.Height}), diff needs the same size");
        }

        const int block = 8;
        int width = before.Width;
        int height = before.Height;
        int blocksX = (width + block - 1) / block;
        int blocksY = (height + block - 1) / block;
        var minX = new int[blocksX * blocksY];
        var minY = new int[blocksX * blocksY];
        var maxX = new int[blocksX * blocksY];
        var maxY = new int[blocksX * blocksY];
        for (int i = 0; i < minX.Length; i++)
        {
            minX[i] = int.MaxValue;
            minY[i] = int.MaxValue;
            maxX[i] = -1;
            maxY[i] = -1;
        }

        changedPixels = 0;
        var rect = new Rectangle(0, 0, width, height);
        var dataBefore = before.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var dataAfter = after.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rowBefore = new byte[width * 4];
            var rowAfter = new byte[width * 4];
            for (int y = 0; y < height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(dataBefore.Scan0 + y * dataBefore.Stride, rowBefore, 0, rowBefore.Length);
                System.Runtime.InteropServices.Marshal.Copy(dataAfter.Scan0 + y * dataAfter.Stride, rowAfter, 0, rowAfter.Length);
                for (int x = 0; x < width; x++)
                {
                    int i = x * 4;
                    int difference = Math.Max(Math.Abs(rowBefore[i] - rowAfter[i]), Math.Max(Math.Abs(rowBefore[i + 1] - rowAfter[i + 1]), Math.Abs(rowBefore[i + 2] - rowAfter[i + 2])));
                    if (difference > threshold)
                    {
                        changedPixels++;
                        int b = (y / block) * blocksX + x / block;
                        if (x < minX[b]) minX[b] = x;
                        if (y < minY[b]) minY[b] = y;
                        if (x > maxX[b]) maxX[b] = x;
                        if (y > maxY[b]) maxY[b] = y;
                    }
                }
            }
        }
        finally
        {
            before.UnlockBits(dataBefore);
            after.UnlockBits(dataAfter);
        }

        // group the blocks that are close to each other
        int radius = Math.Max(1, (merge + block - 1) / block);
        var visited = new bool[blocksX * blocksY];
        var result = new List<Rectangle>();
        var stack = new Stack<int>();
        for (int start = 0; start < visited.Length; start++)
        {
            if (visited[start] || maxX[start] < 0)
            {
                continue;
            }

            int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
            visited[start] = true;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int current = stack.Pop();
                left = Math.Min(left, minX[current]);
                top = Math.Min(top, minY[current]);
                right = Math.Max(right, maxX[current]);
                bottom = Math.Max(bottom, maxY[current]);

                int cx = current % blocksX;
                int cy = current / blocksX;
                for (int ny = Math.Max(0, cy - radius); ny <= Math.Min(blocksY - 1, cy + radius); ny++)
                {
                    for (int nx = Math.Max(0, cx - radius); nx <= Math.Min(blocksX - 1, cx + radius); nx++)
                    {
                        int neighbor = ny * blocksX + nx;
                        if (!visited[neighbor] && maxX[neighbor] >= 0)
                        {
                            visited[neighbor] = true;
                            stack.Push(neighbor);
                        }
                    }
                }
            }

            var found = Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
            if ((long) found.Width * found.Height >= minArea)
            {
                result.Add(found);
            }
        }

        return result.OrderBy(r => r.Y).ThenBy(r => r.X).ToList();
    }
}
