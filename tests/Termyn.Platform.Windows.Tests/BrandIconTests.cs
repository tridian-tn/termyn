using System.Buffers.Binary;
using System.Drawing;
using Termyn.Core.Model;
using Termyn.Core.Settings;
using Termyn.Platform.Windows;
using Termyn.TestSupport;

namespace Termyn.Platform.Windows.Tests;

/// <summary>
/// The mark, which is drawn rather than shipped — so it is the code that has to be held to the
/// design, and the committed asset that has to be held to the code.
/// </summary>
public class BrandIconTests
{
    [Fact]
    public void A_written_icon_names_every_size_it_was_asked_for()
    {
        var file = Temp();
        BrandIcon.WriteIcoFile(file);

        var bytes = File.ReadAllBytes(file);
        Assert.Equal(0, BitConverter.ToUInt16(bytes, 0));   // reserved
        Assert.Equal(1, BitConverter.ToUInt16(bytes, 2));   // type: icon
        Assert.Equal(BrandIcon.IconSizes.Count, BitConverter.ToUInt16(bytes, 4));

        for (var i = 0; i < BrandIcon.IconSizes.Count; i++)
        {
            var entry = 6 + (i * 16);
            var size = BrandIcon.IconSizes[i];

            // 256 is written as zero: the field is one byte and can't hold 256 itself.
            var expected = size >= 256 ? 0 : size;
            Assert.Equal(expected, bytes[entry]);
            Assert.Equal(expected, bytes[entry + 1]);

            Assert.Equal(0, bytes[entry + 2]);                        // no palette
            Assert.Equal(1, BitConverter.ToUInt16(bytes, entry + 4)); // planes
            Assert.Equal(32, BitConverter.ToUInt16(bytes, entry + 6));
        }
    }

    [Fact]
    public void Every_frame_offset_points_at_the_frame_it_claims()
    {
        var file = Temp();
        BrandIcon.WriteIcoFile(file);
        var bytes = File.ReadAllBytes(file);

        var count = BitConverter.ToUInt16(bytes, 4);
        var expectedOffset = 6 + (count * 16);
        var total = expectedOffset;

        for (var i = 0; i < count; i++)
        {
            var entry = 6 + (i * 16);
            var length = BitConverter.ToInt32(bytes, entry + 8);
            var offset = BitConverter.ToInt32(bytes, entry + 12);

            Assert.Equal(expectedOffset, offset);
            Assert.True(offset + length <= bytes.Length, "a frame runs off the end of the file");

            // Every frame is a PNG, which the format has allowed since Vista.
            Assert.Equal([0x89, 0x50, 0x4E, 0x47], bytes[offset..(offset + 4)]);

            expectedOffset += length;
            total += length;
        }

        Assert.Equal(total, bytes.Length); // no slack, nothing truncated
    }

    [Fact]
    public void A_written_icon_is_one_the_shell_will_load_at_every_size()
    {
        var file = Temp();
        BrandIcon.WriteIcoFile(file);

        using var whole = new Icon(file);
        Assert.NotNull(whole);

        // Every size below the largest resolves exactly. The 256 frame is deliberately excluded:
        // it's stored PNG-compressed, and the Icon(path, size) lookup hands back the next one down
        // rather than decoding it — Explorer reads it, this API doesn't.
        foreach (var size in BrandIcon.IconSizes.Where(s => s < BrandIcon.MaxSize))
        {
            using var frame = new Icon(file, new Size(size, size));
            Assert.Equal(size, frame.Width);
        }
    }

    [Fact]
    public void The_committed_icon_is_the_one_the_code_draws_now()
    {
        // The executable and the installer both point at this file, and nothing regenerates it — so
        // without this, changing the mark updates the tray and leaves those two on the old one.
        var committed = Path.Combine(Repository.Root(), "assets", "termyn.ico");
        Assert.True(File.Exists(committed), $"missing {committed}");

        var fresh = Temp();
        BrandIcon.WriteIcoFile(fresh);

        // Compared as pictures rather than as bytes. PNG is lossless, so decoding both sides gives
        // the same pixels whatever the encoder did with them — and the encoder is not ours: this
        // machine and the CI runner have different builds of gdiplus, so comparing compressed
        // lengths would fail on CI and tell whoever read it to regenerate a mark that hadn't changed.
        var a = Frames(File.ReadAllBytes(fresh));
        var b = Frames(File.ReadAllBytes(committed));

        Assert.Equal(a.Keys.Order(), b.Keys.Order());

        foreach (var size in a.Keys)
        {
            using var drawn = a[size];
            using var stored = b[size];
            Assert.True(Differences(drawn, stored) == 0, $"the {size}px frame is not what the code draws now");
        }

        static Dictionary<int, Bitmap> Frames(byte[] bytes)
        {
            var frames = new Dictionary<int, Bitmap>();
            var count = BitConverter.ToUInt16(bytes, 4);
            for (var i = 0; i < count; i++)
            {
                var entry = 6 + (i * 16);
                var size = bytes[entry] == 0 ? BrandIcon.MaxSize : bytes[entry];
                var length = BitConverter.ToInt32(bytes, entry + 8);
                var offset = BitConverter.ToInt32(bytes, entry + 12);

                using var png = new MemoryStream(bytes, offset, length);
                frames[size] = new Bitmap(png);
            }
            return frames;
        }
    }

    [Fact]
    public void Every_frame_is_a_picture_of_the_size_the_directory_claims()
    {
        // Including 256, which the shell-loading test has to skip because the Icon API won't decode
        // it — so this is the only thing checking the size of the frame Explorer shows large and the
        // installer stamps on the setup binary.
        var bytes = File.ReadAllBytes(Path.Combine(Repository.Root(), "assets", "termyn.ico"));
        var count = BitConverter.ToUInt16(bytes, 4);

        Assert.Equal(BrandIcon.IconSizes.Count, count);

        for (var i = 0; i < count; i++)
        {
            var entry = 6 + (i * 16);
            var declared = bytes[entry] == 0 ? BrandIcon.MaxSize : bytes[entry];
            var offset = BitConverter.ToInt32(bytes, entry + 12);

            // Straight out of the PNG's own header, big-endian, so this is the picture's opinion of
            // its size rather than the directory's.
            var width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset + 16, 4));
            var height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset + 20, 4));

            Assert.Equal(declared, width);
            Assert.Equal(declared, height);
        }
    }

    [Fact]
    public void An_icon_written_to_a_chosen_set_of_sizes_holds_exactly_those()
    {
        // The sizes parameter is a documented entry point with no other coverage: ignoring the
        // caller's list and writing IconSizes regardless passes every other test here, including
        // both of the rejection ones, which throw before the list is ever used.
        var path = Temp();
        BrandIcon.WriteIcoFile(path, [16, 32]);

        var bytes = File.ReadAllBytes(path);

        Assert.Equal(2, BitConverter.ToUInt16(bytes, 4));
        Assert.Equal(16, bytes[6]);
        Assert.Equal(32, bytes[6 + 16]);
    }

    [Fact]
    public void A_badge_is_visibly_not_the_tick()
    {
        using var tick = BrandIcon.Draw(16);
        using var badged = BrandIcon.Draw(16, 3);

        Assert.True(Differences(tick, badged) > 20, "a badge should not look like the tick");
    }

    [Fact]
    public void A_count_over_ninety_nine_stops_being_a_number()
    {
        using var hundred = BrandIcon.Draw(16, 100);
        using var lots = BrandIcon.Draw(16, 500);
        using var ninetyNine = BrandIcon.Draw(16, 99);

        Assert.Equal(0, Differences(hundred, lots));            // both read "99+"
        Assert.True(Differences(ninetyNine, hundred) > 0);      // 99 is still a number
    }

    [Theory]
    [InlineData(16, 1)]
    [InlineData(16, 7)]
    [InlineData(16, 10)]
    [InlineData(16, 42)]
    [InlineData(20, 10)]
    [InlineData(24, 10)]
    [InlineData(24, 42)]
    [InlineData(32, 1)]
    [InlineData(32, 10)]
    [InlineData(32, 42)]
    [InlineData(32, 88)]
    public void A_count_balances_on_one_line_in_the_middle_of_the_tile(int size, int badge)
    {
        using var bitmap = BrandIcon.Draw(size, badge);
        AssertLevelOnOneLine(bitmap, size);

        // Halfway between centring the box and centring the ink, so neither is out by a pixel. The
        // box alone leaves a "1"'s stem right of centre, its flag widening the box to the left; the
        // ink alone drags "10" against the left edge, the "1" being so light.
        var (left, _, right, _) = InkBounds(bitmap);
        Assert.InRange((size - 2 - right) - (left - 1), -1, 1);

        var middle = (size - 1) / 2.0;
        Assert.InRange(InkCentre(bitmap), middle - 1, middle + 1);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void An_overflowing_count_sits_on_one_line_in_the_middle_of_the_tile(int size)
    {
        // Centred by its box: the "+" is too light to balance against without the count leaning
        // into the tile's edge.
        using var bitmap = BrandIcon.Draw(size, 100);
        AssertLevelOnOneLine(bitmap, size);

        var (left, _, right, _) = InkBounds(bitmap);
        Assert.InRange((size - 2 - right) - (left - 1), -1, 1);
    }

    [Fact]
    public void A_count_is_drawn_bold()
    {
        // A "1" sixteen pixels tall has a stem about 3.3px wide in Segoe UI Bold, and 1.8px in
        // its regular weight, which smears into the tile at the small end.
        using var bitmap = BrandIcon.Draw(32, 1);

        var stem = StemWidth(bitmap);
        Assert.True(stem >= 2.8, $"the stem is {stem:F2}px wide");
    }

    [Fact]
    public void A_count_in_the_fallback_family_is_still_bold()
    {
        // Microsoft Sans Serif's bold stem is about 2.3px at this height, which is barely heavier
        // than its regular; the badge thickens it to make up the difference.
        using var fallback = FontFamily.GenericSansSerif;
        using var bitmap = BrandIcon.Draw(32, 1, fallback);

        var stem = StemWidth(bitmap);
        Assert.True(stem >= 2.8, $"the stem is {stem:F2}px wide");
    }

    [Fact]
    public void A_count_below_one_is_the_plain_mark()
    {
        using var none = BrandIcon.Draw(16);
        using var negative = BrandIcon.Draw(16, -3);

        Assert.Equal(0, Differences(none, negative));
    }

    [Fact]
    public void The_mark_is_the_theme_accent_on_the_theme_background()
    {
        using var bitmap = BrandIcon.Draw(32);

        var background = bitmap.GetPixel(16, 16);
        Assert.Equal(ThemePalette.Dark.Background.ToString(), Rgb(background));

        // The tile is rounded, so the very corner is outside it.
        Assert.Equal(0, bitmap.GetPixel(0, 0).A);

        var accent = ThemePalette.Dark.Accent;
        var found = false;
        for (var x = 0; x < 32 && !found; x++)
            for (var y = 0; y < 32 && !found; y++)
            {
                var p = bitmap.GetPixel(x, y);
                found = p.R == accent.R && p.G == accent.G && p.B == accent.B;
            }

        Assert.True(found, "the mark should be drawn in the theme's accent");
    }

    [Fact]
    public void An_icon_outlives_the_bitmap_it_was_drawn_from()
    {
        // The GetHicon/FromHandle/Clone/DestroyIcon dance exists for exactly this.
        using var icon = BrandIcon.ToIcon(32);

        GC.Collect();
        GC.WaitForPendingFinalizers();

        using var bitmap = icon.ToBitmap();
        Assert.Equal(32, bitmap.Width);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    [InlineData(257)]
    public void A_size_it_cannot_draw_is_refused_plainly(int size)
        => Assert.Throws<ArgumentOutOfRangeException>(() => BrandIcon.Draw(size));

    [Fact]
    public void An_icon_of_no_frames_is_refused_rather_than_written()
    {
        // A count of zero writes a six-byte file that parses and then can't be loaded.
        Assert.Throws<ArgumentException>(() => BrandIcon.WriteIcoFile(Temp(), []));
    }

    [Fact]
    public void A_size_the_format_cannot_describe_is_refused()
    {
        // Above 256 collides with the zero that means 256, so the directory would name a frame that
        // isn't the one stored.
        Assert.Throws<ArgumentOutOfRangeException>(() => BrandIcon.WriteIcoFile(Temp(), [512]));
    }

    [Fact]
    public void The_sizes_offered_cover_what_the_shell_asks_for()
    {
        Assert.Contains(16, BrandIcon.IconSizes);
        Assert.Contains(32, BrandIcon.IconSizes);
        Assert.Contains(48, BrandIcon.IconSizes);
        Assert.Contains(256, BrandIcon.IconSizes);
        Assert.Equal(BrandIcon.IconSizes.OrderBy(s => s), BrandIcon.IconSizes);
        Assert.Equal(BrandIcon.IconSizes.Distinct(), BrandIcon.IconSizes);
    }

    // ---- Helpers -----------------------------------------------------------------------------------

    private static string Rgb(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static int Differences(Bitmap a, Bitmap b)
    {
        var differing = 0;
        for (var x = 0; x < a.Width; x++)
            for (var y = 0; y < a.Height; y++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y))
                    differing++;
        return differing;
    }

    /// <summary>
    /// The smallest box holding every pixel the accent has visibly been drawn into.
    /// </summary>
    /// <remarks>
    /// Faint edge pixels count too. A half-strength cut-off drops the anti-aliased tops of round
    /// digits but not the flat baseline beneath them, and reads a centred count as sitting low.
    /// </remarks>
    /// <param name="bitmap">A badged mark</param>
    /// <returns>The box's edges, inclusive</returns>
    private static (int Left, int Top, int Right, int Bottom) InkBounds(Bitmap bitmap)
    {
        var threshold = ThemePalette.Dark.Background.R + 32;

        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (var x = 0; x < bitmap.Width; x++)
        {
            for (var y = 0; y < bitmap.Height; y++)
            {
                var p = bitmap.GetPixel(x, y);
                if (p.A < 255 || p.R < threshold) continue;

                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        Assert.True(right >= 0, "the badge drew no count");
        return (left, top, right, bottom);
    }

    /// <summary>
    /// Where the count's ink balances side to side, each pixel weighted by how much accent it holds.
    /// </summary>
    /// <param name="bitmap">A badged mark</param>
    /// <returns>The balancing column, in pixels from the left</returns>
    private static double InkCentre(Bitmap bitmap)
    {
        var slate = ThemePalette.Dark.Background.R;

        double weight = 0, moment = 0;
        for (var x = 0; x < bitmap.Width; x++)
        {
            for (var y = 0; y < bitmap.Height; y++)
            {
                var p = bitmap.GetPixel(x, y);
                if (p.A < 255 || p.R <= slate) continue;

                weight += p.R - slate;
                moment += (p.R - slate) * x;
            }
        }

        Assert.True(weight > 0, "the badge drew no count");
        return moment / weight;
    }

    /// <summary>
    /// How wide a "1"'s stem is, in pixels, from how much accent the row 60% of the way down holds.
    /// </summary>
    /// <remarks>Partly covered pixels count for their part, so it's finer than a pixel count.</remarks>
    /// <param name="bitmap">A mark badged with a 1</param>
    /// <returns>The stem's width, in pixels</returns>
    private static double StemWidth(Bitmap bitmap)
    {
        var slate = ThemePalette.Dark.Background.R;
        var accent = ThemePalette.Dark.Accent.R;

        var (_, top, _, bottom) = InkBounds(bitmap);
        var row = top + (int)((bottom - top) * 0.6);

        double width = 0;
        for (var x = 0; x < bitmap.Width; x++)
        {
            var p = bitmap.GetPixel(x, row);
            if (p.A == 255) width += Math.Clamp((p.R - slate) / (double)(accent - slate), 0, 1);
        }

        return width;
    }

    /// <summary>
    /// Checks a badged mark's count is centred top to bottom, and short enough to be one line.
    /// </summary>
    /// <param name="bitmap">A badged mark</param>
    /// <param name="size">The mark's edge length</param>
    private static void AssertLevelOnOneLine(Bitmap bitmap, int size)
    {
        var (_, top, _, bottom) = InkBounds(bitmap);

        // The tile is solid from 1 to size - 2; its outermost pixels are its anti-aliased edge. A
        // pixel either way is the count's own edge landing unevenly on the grid.
        Assert.InRange((size - 2 - bottom) - (top - 1), -1, 1);

        // Wrapped onto two lines, the digits fill the tile top to bottom.
        Assert.True(bottom - top + 1 <= size * 0.6, $"the count is {bottom - top + 1}px tall in a {size}px tile");
    }

    private static string Temp()
    {
        var path = Path.Combine(Path.GetTempPath(), $"termyn-{Guid.NewGuid():N}.ico");
        return path;
    }
}
