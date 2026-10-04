using System;
using System.IO;
using System.Linq;
using STlauncher.Core.Skins;
using Xunit;

namespace STlauncher.Core.Tests;

public class SkinTests
{
    private const uint Red = 0xFFFF0000;
    private const uint Blue = 0xFF0000FF;

    private static string TempFolder()
        => Path.Combine(Path.GetTempPath(), "stlauncher-tests", Guid.NewGuid().ToString("N"), "skins");

    /// <summary>Every pixel its own colour, so a wrong copy cannot pass by coincidence.</summary>
    private static uint[] Numbered(int height, uint alpha = 0xFF000000)
        => Enumerable.Range(0, 64 * height).Select(i => alpha | (uint)(i + 1)).ToArray();

    // ===================== Layout =====================

    [Theory]
    [InlineData(SkinModel.Classic)]
    [InlineData(SkinModel.Slim)]
    public void Faces_StayInsideTheTextureAndNeverOverlap(SkinModel model)
    {
        var owner = new int[64 * 64];
        var faces = SkinLayout.Faces(model);

        Assert.Equal(72, faces.Count);

        for (var i = 0; i < faces.Count; i++)
        {
            var rect = faces[i].Rect;
            Assert.True(rect.X >= 0 && rect.Y >= 0 && rect.Right <= 64 && rect.Bottom <= 64, faces[i].ToString());

            for (var y = rect.Y; y < rect.Bottom; y++)
            {
                for (var x = rect.X; x < rect.Right; x++)
                {
                    Assert.True(owner[y * 64 + x] == 0, $"{faces[i]} overlaps another face at {x},{y}");
                    owner[y * 64 + x] = i + 1;
                }
            }
        }
    }

    [Theory]
    [InlineData(SkinModel.Classic)]
    [InlineData(SkinModel.Slim)]
    public void Mirror_IsItsOwnInverseAndSwapsTheLimbs(SkinModel model)
    {
        foreach (var face in SkinLayout.Faces(model))
        {
            for (var y = face.Rect.Y; y < face.Rect.Bottom; y++)
            {
                for (var x = face.Rect.X; x < face.Rect.Right; x++)
                {
                    var twin = SkinLayout.Mirror(x, y, model);
                    Assert.NotNull(twin);
                    Assert.Equal((x, y), SkinLayout.Mirror(twin!.Value.X, twin.Value.Y, model));
                }
            }
        }

        // The outer top corner of the right arm's front is the outer top corner of the left arm's front.
        var rightFront = SkinLayout.Face(SkinPart.RightArm, SkinLayer.Base, SkinSide.Front, model).Rect;
        var leftFront = SkinLayout.Face(SkinPart.LeftArm, SkinLayer.Base, SkinSide.Front, model).Rect;
        Assert.Equal((leftFront.Right - 1, leftFront.Y), SkinLayout.Mirror(rightFront.X, rightFront.Y, model));

        // The unused corner above the head's right side belongs to nothing.
        Assert.Null(SkinLayout.Mirror(0, 0, model));
    }

    // ===================== 64x32 =====================

    /// <summary>The game's own list of copies for an old skin, written out independently of the layout.</summary>
    private static void CopyRect(uint[] image, int x, int y, int dx, int dy, int width, int height)
    {
        for (var row = 0; row < height; row++)
        {
            for (var column = 0; column < width; column++)
            {
                image[(y + dy + row) * 64 + x + dx + width - 1 - column] = image[(y + row) * 64 + x + column];
            }
        }
    }

    [Fact]
    public void Legacy_ConvertsTheWayTheGameDoes()
    {
        var legacy = Numbered(32);

        // Holes in the hat area, as a real old skin has: the "no hat meant" rule must not fire.
        for (var i = 32; i < 64; i++)
        {
            legacy[i] = 0;
        }

        var expected = new uint[64 * 64];
        legacy.CopyTo(expected, 0);

        CopyRect(expected, 4, 16, 16, 32, 4, 4);
        CopyRect(expected, 8, 16, 16, 32, 4, 4);
        CopyRect(expected, 0, 20, 24, 32, 4, 12);
        CopyRect(expected, 4, 20, 16, 32, 4, 12);
        CopyRect(expected, 8, 20, 8, 32, 4, 12);
        CopyRect(expected, 12, 20, 16, 32, 4, 12);
        CopyRect(expected, 44, 16, -8, 32, 4, 4);
        CopyRect(expected, 48, 16, -8, 32, 4, 4);
        CopyRect(expected, 40, 20, 0, 32, 4, 12);
        CopyRect(expected, 44, 20, -8, 32, 4, 12);
        CopyRect(expected, 48, 20, -16, 32, 4, 12);
        CopyRect(expected, 52, 20, -8, 32, 4, 12);

        // The new limbs' block is forced opaque, unused corners included.
        for (var y = 48; y < 64; y++)
        {
            for (var x = 16; x < 48; x++)
            {
                expected[y * 64 + x] |= 0xFF000000;
            }
        }

        var converted = SkinImage.FromPixels(legacy, 64, 32);

        Assert.NotNull(converted);
        Assert.Equal(expected, converted!.Pixels.ToArray());
    }

    [Fact]
    public void Legacy_AFullyOpaqueHatAreaIsNoHat()
    {
        var converted = SkinLegacy.Convert(Numbered(32));

        Assert.Equal(0u, converted[40, 8] >> 24);     // the hat's front: cleared
        Assert.Equal(0xFFu, converted[8, 8] >> 24);   // the face: opaque
        Assert.Equal(0xFFu, converted[44, 20] >> 24); // the right arm, inside the cleared block's lower rows: opaque again
    }

    [Fact]
    public void FromPixels_RefusesOtherSizes()
    {
        Assert.Null(SkinImage.FromPixels(new uint[64 * 48], 64, 48));
        Assert.Null(SkinImage.FromPixels(new uint[128 * 128], 128, 128));
        Assert.NotNull(SkinImage.FromPixels(new uint[64 * 64], 64, 64));
    }

    [Fact]
    public void LooksSlim_NeedsAllFourEmptyPatches()
    {
        var image = SkinImage.FromPixels(Numbered(64), 64, 64)!;
        Assert.False(image.LooksSlim());

        foreach (var (x, y, w, h) in new[] { (50, 16, 2, 4), (54, 20, 2, 12), (42, 48, 2, 4), (46, 52, 2, 12) })
        {
            for (var row = y; row < y + h; row++)
            {
                for (var column = x; column < x + w; column++)
                {
                    image[column, row] = 0;
                }
            }
        }

        Assert.True(image.LooksSlim());

        image[46, 60] = Red;
        Assert.False(image.LooksSlim());
    }

    // ===================== File check =====================

    private static byte[] PngHeader(int width, int height)
    {
        var bytes = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(bytes, 0);
        bytes[16] = (byte)(width >> 24);
        bytes[17] = (byte)(width >> 16);
        bytes[18] = (byte)(width >> 8);
        bytes[19] = (byte)width;
        bytes[20] = (byte)(height >> 24);
        bytes[21] = (byte)(height >> 16);
        bytes[22] = (byte)(height >> 8);
        bytes[23] = (byte)height;
        return bytes;
    }

    [Theory]
    [InlineData(64, 64, SkinFileKind.Modern)]
    [InlineData(64, 32, SkinFileKind.Legacy)]
    [InlineData(128, 128, SkinFileKind.WrongSize)]
    [InlineData(64, 48, SkinFileKind.WrongSize)]
    [InlineData(1920, 1080, SkinFileKind.WrongSize)]
    public void Inspect_ReadsTheSizeFromTheHeader(int width, int height, SkinFileKind expected)
    {
        Assert.Equal(expected, SkinFile.Inspect(PngHeader(width, height), out var w, out var h));
        Assert.Equal((width, height), (w, h));
    }

    [Fact]
    public void Inspect_RefusesWhatIsNotAPng()
    {
        Assert.Equal(SkinFileKind.NotPng, SkinFile.Inspect(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, out _, out _));
        Assert.Equal(SkinFileKind.NotPng, SkinFile.Inspect("not an image at all, just some text"u8, out _, out _));
        Assert.Equal(SkinFileKind.NotPng, SkinFile.Inspect(ReadOnlySpan<byte>.Empty, out _, out _));
    }

    // ===================== Tools and history =====================

    [Fact]
    public void Stroke_IsOneStepOfHistory()
    {
        var document = new SkinDocument(new SkinImage(), SkinModel.Classic);

        document.BeginStroke();
        document.Apply(SkinTool.Pencil, 8, 8, Red);
        document.Apply(SkinTool.Pencil, 9, 8, Red);
        document.Apply(SkinTool.Pencil, 10, 8, Red);
        document.EndStroke();

        Assert.True(document.IsDirty);
        Assert.Equal(Red, document.Image[9, 8]);

        Assert.True(document.Undo());
        Assert.Equal(0u, document.Image[8, 8]);
        Assert.Equal(0u, document.Image[10, 8]);
        Assert.False(document.CanUndo);
        Assert.False(document.IsDirty);

        Assert.True(document.Redo());
        Assert.Equal(Red, document.Image[10, 8]);
        Assert.False(document.CanRedo);
    }

    [Fact]
    public void Dirty_FollowsTheSavedStateThroughUndoAndRedo()
    {
        var document = new SkinDocument(new SkinImage(), SkinModel.Classic);

        document.Apply(SkinTool.Pencil, 8, 8, Red);
        document.MarkSaved();
        Assert.False(document.IsDirty);

        document.Apply(SkinTool.Pencil, 9, 8, Blue);
        Assert.True(document.IsDirty);

        document.Undo();
        Assert.False(document.IsDirty);

        document.Undo();
        Assert.True(document.IsDirty);

        // A new change after an undo throws the saved state out of reach.
        document.Apply(SkinTool.Eraser, 20, 20, 0);
        document.Apply(SkinTool.Pencil, 20, 20, Blue);
        Assert.True(document.IsDirty);

        document.Model = SkinModel.Slim;
        document.MarkSaved();
        Assert.False(document.IsDirty);
        document.Model = SkinModel.Classic;
        Assert.True(document.IsDirty);
    }

    [Fact]
    public void AStrokeThatChangesNothing_LeavesNoStep()
    {
        var document = new SkinDocument(new SkinImage(), SkinModel.Classic);

        document.BeginStroke();
        document.Apply(SkinTool.Eraser, 8, 8, 0);
        document.Apply(SkinTool.Pencil, 9, 9, Red);
        document.Apply(SkinTool.Eraser, 9, 9, 0);
        document.EndStroke();

        Assert.False(document.CanUndo);
        Assert.False(document.IsDirty);
    }

    [Fact]
    public void Fill_StopsAtTheEdgeOfTheFace()
    {
        var document = new SkinDocument(new SkinImage(), SkinModel.Classic);
        var face = SkinLayout.Face(SkinPart.Head, SkinLayer.Base, SkinSide.Front, SkinModel.Classic).Rect;

        // A wall down the middle of the face: the fill takes the left half only.
        for (var y = face.Y; y < face.Bottom; y++)
        {
            document.Apply(SkinTool.Pencil, face.X + 4, y, Blue);
        }

        document.Apply(SkinTool.Fill, face.X, face.Y, Red);

        Assert.Equal(Red, document.Image[face.X + 3, face.Bottom - 1]);
        Assert.Equal(Blue, document.Image[face.X + 4, face.Y]);
        Assert.Equal(0u, document.Image[face.X + 5, face.Y]);

        // The pixel just left of the face is the head's right side: untouched.
        Assert.Equal(0u, document.Image[face.X - 1, face.Y]);
        Assert.Equal(0u, document.Image[face.X, face.Y - 1]);

        // In an unused corner there is nothing to fill.
        document.Apply(SkinTool.Fill, 0, 0, Red);
        Assert.Equal(0u, document.Image[0, 0]);

        document.Undo();
        Assert.Equal(0u, document.Image[face.X, face.Y]);
        Assert.Equal(Blue, document.Image[face.X + 4, face.Y]);
    }

    [Fact]
    public void Mirror_DrawsOnTheOtherLimbToo()
    {
        var document = new SkinDocument(new SkinImage(), SkinModel.Slim) { Mirror = true };
        var right = SkinLayout.Face(SkinPart.RightLeg, SkinLayer.Overlay, SkinSide.Front, SkinModel.Slim).Rect;
        var left = SkinLayout.Face(SkinPart.LeftLeg, SkinLayer.Overlay, SkinSide.Front, SkinModel.Slim).Rect;

        document.Apply(SkinTool.Pencil, right.X, right.Y + 3, Red);

        Assert.Equal(Red, document.Image[right.X, right.Y + 3]);
        Assert.Equal(Red, document.Image[left.Right - 1, left.Y + 3]);

        document.Undo();
        Assert.Equal(0u, document.Image[left.Right - 1, left.Y + 3]);
    }

    [Fact]
    public void Picker_ReadsWithoutChanging()
    {
        var image = new SkinImage();
        image[12, 12] = Blue;
        var document = new SkinDocument(image, SkinModel.Classic);

        Assert.Equal(Blue, document.Apply(SkinTool.Picker, 12, 12, Red));
        Assert.Null(document.Apply(SkinTool.Picker, 64, 0, Red));
        Assert.False(document.CanUndo);
    }

    // ===================== Colour =====================

    [Theory]
    [InlineData("#A3243F", 0xFFA3243Fu)]
    [InlineData("a3243f", 0xFFA3243Fu)]
    [InlineData(" #FA0 ", 0xFFFFAA00u)]
    public void Hex_Parses(string text, uint expected)
    {
        Assert.True(SkinColour.TryParseHex(text, out var colour));
        Assert.Equal(expected, colour);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    [InlineData("red")]
    public void Hex_RefusesNonsense(string text) => Assert.False(SkinColour.TryParseHex(text, out _));

    [Theory]
    [InlineData(0xFFFF0000u)]
    [InlineData(0xFF00FF00u)]
    [InlineData(0xFF0000FFu)]
    [InlineData(0xFFA3243Fu)]
    [InlineData(0xFF808080u)]
    [InlineData(0xFF000000u)]
    [InlineData(0xFFFFFFFFu)]
    public void Hsv_RoundTrips(uint colour)
    {
        var (h, s, v) = SkinColour.ToHsv(colour);

        Assert.Equal(colour, SkinColour.FromHsv(h, s, v));
        Assert.Equal(colour, SkinColour.TryParseHex(SkinColour.ToHex(colour), out var parsed) ? parsed : 0);
    }

    // ===================== Library =====================

    [Fact]
    public void Library_KeepsSkinsAcrossInstances()
    {
        var folder = TempFolder();
        var library = new SkinLibrary(folder);
        var png = new byte[] { 1, 2, 3, 4 };

        var knight = library.Add("Knight", SkinModel.Slim, png, DateTimeOffset.Now.AddMinutes(-5));
        var second = library.Add("Knight", SkinModel.Classic, png);
        library.SetWorn(knight.Id);

        var reopened = new SkinLibrary(folder);
        var list = reopened.List();

        Assert.Equal(new[] { "Knight 2", "Knight" }, list.Select(e => e.Name));   // the newest first
        Assert.Equal(SkinModel.Slim, list[1].SkinModel);
        Assert.Equal(png, File.ReadAllBytes(reopened.PathOf(knight.Id)));
        Assert.Equal(knight.Id, reopened.Worn?.Id);
        Assert.NotEqual(knight.Id, second.Id);
    }

    [Fact]
    public void Library_RenameDuplicateUpdateDelete()
    {
        var library = new SkinLibrary(TempFolder());
        var first = library.Add("Mage", SkinModel.Classic, new byte[] { 1 });
        var other = library.Add("Rogue", SkinModel.Classic, new byte[] { 2 });

        // A name already taken gets a number; renaming to its own name does not.
        Assert.Equal("rogue 2", library.Rename(first.Id, "rogue")!.Name);
        Assert.Equal("Rogue", library.Rename(other.Id, "Rogue")!.Name);
        Assert.Null(library.Rename(first.Id, "   "));

        var copy = library.Duplicate(other.Id, "Rogue")!;
        Assert.Equal("Rogue 3", copy.Name);
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(library.PathOf(copy.Id)));

        Assert.True(library.Update(copy.Id, new byte[] { 9, 9 }, SkinModel.Slim));
        Assert.Equal(SkinModel.Slim, library.Find(copy.Id)!.SkinModel);
        Assert.Equal(new byte[] { 9, 9 }, File.ReadAllBytes(library.PathOf(copy.Id)));
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(library.PathOf(other.Id)));

        library.SetWorn(other.Id);
        Assert.True(library.Delete(other.Id));
        Assert.False(File.Exists(library.PathOf(other.Id)));
        Assert.Null(library.Worn);
        Assert.Equal(2, library.List().Count);
        Assert.False(library.Delete("missing"));
    }

    [Fact]
    public void Library_SurvivesABrokenIndexAndIgnoresForeignIds()
    {
        var folder = TempFolder();
        Directory.CreateDirectory(folder);

        File.WriteAllText(Path.Combine(folder, "index.json"), "{ this is not json");
        Assert.Empty(new SkinLibrary(folder).List());

        // An id that points out of the folder is not one of ours and is never followed.
        File.WriteAllText(Path.Combine(folder, "index.json"),
            "{\"worn\":\"..\\\\outside\",\"skins\":[{\"id\":\"..\\\\outside\",\"name\":\"x\"},{\"id\":null,\"name\":\"y\"}]}");
        File.WriteAllBytes(Path.Combine(folder, "..", "outside.png"), new byte[] { 1 });

        var library = new SkinLibrary(folder);
        Assert.Empty(library.List());
        Assert.Null(library.Worn);
        Assert.False(library.Delete("..\\outside"));
        Assert.True(File.Exists(Path.Combine(folder, "..", "outside.png")));

        // An entry whose file has gone is not shown.
        var entry = library.Add("Ghost", SkinModel.Classic, new byte[] { 1 });
        File.Delete(library.PathOf(entry.Id));
        Assert.Empty(library.List());
    }
}
