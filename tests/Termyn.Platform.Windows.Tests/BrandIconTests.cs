using System.Drawing;
using Termyn.Core.Model;
using Termyn.Core.Settings;
using Termyn.Platform.Windows;

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
        var committed = Path.Combine(RepoRoot(), "assets", "termyn.ico");
        Assert.True(File.Exists(committed), $"missing {committed}");

        var fresh = Temp();
        BrandIcon.WriteIcoFile(fresh);

        Assert.Equal(Directory(File.ReadAllBytes(fresh)), Directory(File.ReadAllBytes(committed)));

        // The frame count and sizes, which is what survives a different PNG encoder.
        static List<(int Size, int Length)> Directory(byte[] bytes)
        {
            var count = BitConverter.ToUInt16(bytes, 4);
            var entries = new List<(int, int)>(count);
            for (var i = 0; i < count; i++)
            {
                var entry = 6 + (i * 16);
                entries.Add((bytes[entry] == 0 ? 256 : bytes[entry], BitConverter.ToInt32(bytes, entry + 8)));
            }
            return entries;
        }
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

    private static string Temp()
    {
        var path = Path.Combine(Path.GetTempPath(), $"termyn-{Guid.NewGuid():N}.ico");
        return path;
    }

    /// <summary>
    /// Walks up to the directory holding the solution, so committed assets can be read. Keyed on the
    /// solution file rather than Directory.Build.props, which also sits in tests/.
    /// </summary>
    internal static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Termyn.slnx")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
