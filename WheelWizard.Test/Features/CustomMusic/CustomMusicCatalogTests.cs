using System.Buffers.Binary;
using System.Text;
using WheelWizard.CustomMusic.Catalog;

namespace WheelWizard.Test.Features.CustomMusic;

public class CustomMusicCatalogTests
{
    [Fact]
    public void GameCatalog_ListsEveryCourseArenaMenuAndJingleOnce()
    {
        var slots = GameMusicCatalog.Build();

        Assert.Equal(slots.Count, slots.Select(slot => slot.Key).Distinct().Count());
        // 32 courses where Luigi Circuit and Mario Circuit share music, plus Galaxy Colosseum.
        Assert.Equal(32, slots.Count(slot => slot.Category == MusicCategory.Course));
        Assert.Equal(10, slots.Count(slot => slot.Category == MusicCategory.Arena));
        Assert.All(slots.Where(slot => slot.Category is MusicCategory.Course or MusicCategory.Arena && slot.CourseId != GameMusicCatalog.GalaxyColosseumCourseId), slot => Assert.True(slot.HasFinalLap));
        Assert.Equal(slots.OrderBy(slot => slot.Order).Select(slot => slot.Key), slots.Select(slot => slot.Key));
    }

    [Fact]
    public void GameCatalog_UsesTheGamesExactFileNames()
    {
        var slots = GameMusicCatalog.Build().ToDictionary(slot => slot.Key);

        var luigi = slots["game:n_circuit32"];
        Assert.Equal("Luigi Circuit / Mario Circuit", luigi.Name);
        Assert.Equal(["n_Circuit32_n.brstm"], luigi.FileNames);
        Assert.Equal(["n_Circuit32_f.brstm"], luigi.FinalLapFileNames);
        Assert.Equal(["n_Kinoko_F.brstm"], slots["game:n_kinoko"].FinalLapFileNames);

        var waiting = slots["game:o_wi-fi_waiting32"];
        Assert.Equal(MusicCategory.Menu, waiting.Category);
        Assert.Equal(["o_Wi-Fi_waiting32.brstm"], waiting.FileNames);
        Assert.False(waiting.HasFinalLap);

        Assert.False(slots["game:o_start32_fan"].Loops);
        Assert.Equal(["wifi_lobby_bg.brstm"], slots["menu:wifi_lobby_bg"].FileNames);
    }

    [Theory]
    [InlineData(0x08, "game:n_circuit32")]
    [InlineData(0x00, "game:n_circuit32")]
    [InlineData(0x02, "game:n_kinoko")]
    [InlineData(GameMusicCatalog.GalaxyColosseumCourseId, "game:n_bossmi_32")]
    [InlineData(0x7F, null)]
    public void GameCatalog_FindsTheMusicOfACourseId(int courseId, string? key) => Assert.Equal(key, GameMusicCatalog.KeyForCourseId(courseId));

    [Theory]
    [InlineData("n_Circuit32_n", "n_Circuit32")]
    [InlineData("STRM_N_KOOPA_F", "STRM_N_KOOPA")]
    [InlineData("title_bg", "title_bg")]
    public void GameCatalog_StripsLapSuffixes(string name, string expected) => Assert.Equal(expected, GameMusicCatalog.StripLapSuffix(name));

    [Fact]
    public void PulsarConfigReader_ReadsCupsTracksVariantsAndFileNames()
    {
        var config = new PulsarConfigBuilder()
            .Cup("SNES Cup")
            .Track(0x08, 0x18, "SNES Mario Circuit 1", version: "v2")
            .Track(0x01, 0x08, "SNES Donut Plains 1")
            .Track(0x02, 0x1A, "SNES Ghost Valley 1", variants: [(0x02, 0x1A, "SNES Ghost Valley 1 (Night)")])
            .Track(0x04, 0x0F, "SNES Bowser Castle 1")
            .Cup("N64 Cup") // an odd cup count is stored rounded up to an even number
            .Track(0x00, 0x1C, "N64 Luigi Raceway")
            .Track(0x05, 0x05, "N64 Moo Moo Farm")
            .Track(0x06, 0x06, "N64 Koopa Troopa Beach")
            .Track(0x07, 0x07, "N64 Kalimari Desert")
            .Cup("GBA Cup")
            .Track(0x08, 0x08, "GBA Peach Circuit")
            .Track(0x09, 0x09, "GBA Riverside Park")
            .Track(0x0A, 0x0A, "GBA Cheese Land")
            .Track(0x0B, 0x0B, "GBA Sky Garden")
            .File(0, "rMC1")
            .File(2, "rGV1")
            .File((1 << 12) + 2, "rGV1Night")
            .File(4, "nLR")
            .Build();

        var result = PulsarConfigReader.Read(config);

        Assert.Equal(["SNES Cup", "N64 Cup", "GBA Cup"], result.CupNames);
        Assert.Equal(12, result.Tracks.Count);

        var mc1 = result.Tracks[0];
        Assert.Equal("SNES Mario Circuit 1", mc1.Name); // the coloured " v2" suffix is not part of the name
        Assert.Equal("rMC1", mc1.FileName);
        Assert.Equal(0x18, mc1.MusicSlot);
        Assert.Null(result.Tracks[1].FileName);

        var ghostValley = result.Tracks[2];
        Assert.Equal("SNES Ghost Valley 1 (all versions)", ghostValley.Name);
        var night = Assert.Single(ghostValley.Variants);
        Assert.Equal("SNES Ghost Valley 1 (Night)", night.Name);
        Assert.Equal("rGV1Night", night.FileName);
        Assert.Equal(0x1A, night.MusicSlot);

        Assert.Equal(1, result.Tracks[4].CupIndex);
        Assert.Equal("GBA Sky Garden", result.Tracks[11].Name);
        Assert.Empty(result.Tracks[11].Variants);
    }

    [Fact]
    public void PulsarConfigReader_RejectsOtherFiles() => Assert.Throws<InvalidDataException>(() => PulsarConfigReader.Read(Encoding.ASCII.GetBytes("RSTM and some more bytes here....")));

    [Fact]
    public void PulsarConfigReader_FileSectionKeepsOnlyTheFileName()
    {
        var files = PulsarConfigReader.ReadFileSection(Encoding.UTF8.GetBytes("FILE\n1A=uMKS|expert1|expert2\n1001A=uMKSv2|a\nbroken line\n0?cupicon\n"));

        Assert.Equal("uMKS", files[0x1A]);
        Assert.Equal("uMKSv2", files[0x1001A]);
        Assert.Equal(2, files.Count);
    }

    /// <summary>Writes a minimal Pulsar config (PULS header, CUPS, BMG names and FILE block) like Pulsar's creator tool.</summary>
    private sealed class PulsarConfigBuilder
    {
        private readonly List<string> _cups = [];
        private readonly List<(byte Slot, byte Music, string Name, string? Version, (byte Slot, byte Music, string Name)[] Variants)> _tracks = [];
        private readonly Dictionary<int, string> _files = [];

        public PulsarConfigBuilder Cup(string name)
        {
            _cups.Add(name);
            return this;
        }

        public PulsarConfigBuilder Track(byte slot, byte music, string name, string? version = null, (byte, byte, string)[]? variants = null)
        {
            _tracks.Add((slot, music, name, version, variants ?? []));
            return this;
        }

        public PulsarConfigBuilder File(int id, string fileName)
        {
            _files[id] = fileName;
            return this;
        }

        public byte[] Build()
        {
            var storedCups = _cups.Count + _cups.Count % 2;
            var trackTable = new List<byte>();
            for (var i = 0; i < storedCups * 4; i++)
            {
                var track = _tracks[i % _tracks.Count];
                trackTable.AddRange([track.Slot, track.Music, .. U16(i < _tracks.Count ? track.Variants.Length : 0), .. U32(0x1234)]);
            }

            var variants = _tracks.SelectMany(track => track.Variants).SelectMany(v => new[] { v.Slot, v.Music }).ToArray();
            byte[] cupsBody = [.. U16(_cups.Count), 2, 0, .. new byte[8], .. S32(variants.Length / 2), .. trackTable, .. variants, .. new byte[storedCups * 8]];
            byte[] cups = [.. "CUPS"u8, .. U32(3), .. U32(12 + cupsBody.Length), .. cupsBody];

            var messages = new List<(uint Id, byte[] Text)>();
            for (var i = 0; i < _cups.Count; i++)
                messages.Add((0x10000u + (uint)i, Utf16(_cups[i])));
            for (var i = 0; i < _tracks.Count; i++)
            {
                var track = _tracks[i];
                messages.Add((0x20000u + (uint)i, track.Version == null ? Utf16(track.Name) : [.. Utf16(track.Name), .. ColourEscape, .. Utf16(" " + track.Version), .. ColourEscape]));
                if (track.Variants.Length == 0)
                    continue;
                messages.Add((0x20000u + (8u << 12) + (uint)i, Utf16(track.Name + " (all versions)")));
                for (var v = 0; v < track.Variants.Length; v++)
                    messages.Add((0x20000u + ((uint)(v + 1) << 12) + (uint)i, Utf16(track.Variants[v].Name)));
            }

            var fileText = "FILE\n" + string.Concat(_files.Select(file => $"{file.Key:X}={file.Value}|e1|e2|e3|e4\n")) + "0?cupicon\n";

            const int headerLength = 0x24;
            byte[] info = [.. "INFO"u8, .. U32(1), .. U32(12 + 0x30), .. new byte[0x30]];
            var cupsOffset = headerLength + info.Length;
            var bmgOffset = cupsOffset + cups.Length;
            var header = new byte[headerLength];
            "PULS"u8.CopyTo(header);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), 3);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8), headerLength);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0x0C), cupsOffset);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0x10), bmgOffset);
            return [.. header, .. info, .. cups, .. Bmg(messages), .. Encoding.UTF8.GetBytes(fileText)];
        }

        private static readonly byte[] ColourEscape = [0, 0x1A, 8, 0, 0, 3, 0, 0];

        private static byte[] Bmg(List<(uint Id, byte[] Text)> messages)
        {
            var text = new List<byte> { 0, 0 };
            var offsets = new List<int>();
            foreach (var message in messages)
            {
                offsets.Add(text.Count);
                text.AddRange([.. message.Text, 0, 0]);
            }

            var inf = Block("INF1", [.. U16(messages.Count), .. U16(8), .. U32(0), .. offsets.SelectMany(offset => U32(offset).Concat(U32(0)))]);
            var dat = Block("DAT1", [.. text]);
            var mid = Block("MID1", [.. U16(messages.Count), 0x10, 0, .. U32(0), .. messages.SelectMany(message => U32((int)message.Id))]);
            byte[] body = [.. inf, .. dat, .. mid];
            byte[] header = [.. "MESGbmg1"u8, .. U32(0x20 + body.Length), .. U32(3), 2, .. new byte[15]];
            return [.. header, .. body];
        }

        private static byte[] Block(string magic, byte[] body)
        {
            var length = (8 + body.Length + 0x1F) & ~0x1F;
            var block = new byte[length];
            Encoding.ASCII.GetBytes(magic, block.AsSpan(0, 4));
            BinaryPrimitives.WriteInt32BigEndian(block.AsSpan(4), length);
            body.CopyTo(block, 8);
            return block;
        }

        private static byte[] Utf16(string text) => Encoding.BigEndianUnicode.GetBytes(text);

        private static byte[] U16(int value) => [(byte)(value >> 8), (byte)value];

        private static byte[] U32(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

        private static byte[] S32(int value) => U32(value);
    }
}
