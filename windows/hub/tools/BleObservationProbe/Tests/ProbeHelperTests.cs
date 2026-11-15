using BleObservationProbe.Analysis;

namespace BleObservationProbe.Tests;

public class ProbeArgumentsTests
{
    private const string Flag = ProbeArguments.AuthorizeFlag;

    [Fact]
    public void IsAuthorized_WithoutFlag_IsFalse()
        => Assert.False(ProbeArguments.IsAuthorized(["--seconds", "60"]));

    [Theory]
    [InlineData]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("/?")]
    public void Plain_scope_requests_are_recognised(params string[] args)
        => Assert.True(ProbeArguments.IsScopeRequest(args));

    [Theory]
    [InlineData("--seconds", "60")]
    [InlineData("--i-authorise-a-passive-scan", "--seconds", "60")]
    [InlineData("--I-AUTHORIZE-A-PASSIVE-SCAN", "--seconds", "60")]
    [InlineData("--help", "--seconds", "60")]
    public void Other_arguments_without_the_exact_flag_are_not_a_scope_request(params string[] args)
    {
        Assert.False(ProbeArguments.IsAuthorized(args));
        Assert.False(ProbeArguments.IsScopeRequest(args));
    }

    [Fact]
    public void Parse_Valid_ReturnsOptions()
    {
        var (o, e) = ProbeArguments.Parse([Flag, "--seconds", "60", "--out", @"C:\x"]);
        Assert.Null(e);
        Assert.Equal(new ProbeOptions(60, @"C:\x"), o);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("121")]
    [InlineData("-5")]
    [InlineData("abc")]
    public void Parse_SecondsOutOfRange_Refused(string value)
        => Assert.NotNull(ProbeArguments.Parse([Flag, "--seconds", value]).Error);

    [Fact]
    public void Parse_MissingSeconds_Refused()
        => Assert.NotNull(ProbeArguments.Parse([Flag]).Error);

    [Fact]
    public void Parse_UnknownOrDuplicateOrMissingValue_Refused()
    {
        Assert.NotNull(ProbeArguments.Parse([Flag, "--seconds", "5", "--connect"]).Error);
        Assert.NotNull(ProbeArguments.Parse([Flag, "--seconds", "5", "--seconds", "6"]).Error);
        Assert.NotNull(ProbeArguments.Parse([Flag, "--seconds", "5", "--out"]).Error);
        Assert.NotNull(ProbeArguments.Parse([Flag, "--seconds"]).Error);
    }
}

public sealed class OutputFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bleprobe-tests-" + Guid.NewGuid().ToString("N"));

    public OutputFolderTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void DefaultFor_UsesUtcTimestamp()
    {
        var t = new DateTimeOffset(2026, 10, 15, 9, 8, 7, TimeSpan.FromHours(2));
        Assert.Equal(Path.Combine(@"D:\r", "20261015-070807"), OutputFolder.DefaultFor(t, @"D:\r"));
    }

    [Fact]
    public void Resolve_InsideRepository_Refused()
    {
        var (path, error) = OutputFolder.Resolve(Path.Combine(_root, "repo", "results"), DateTimeOffset.UtcNow, [Path.Combine(_root, "repo")]);
        Assert.Null(path);
        Assert.Contains("inside the repository", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_SiblingWithSharedPrefix_NotTreatedAsInside()
        => Assert.False(OutputFolder.IsSameOrInside(Path.Combine(_root, "repo2"), Path.Combine(_root, "repo")));

    [Fact]
    public void Resolve_NonEmptyFolder_Refused_EmptyAccepted()
    {
        var full = Path.Combine(_root, "out");
        Directory.CreateDirectory(full);
        Assert.Equal(full, OutputFolder.Resolve(full, DateTimeOffset.UtcNow, []).Path);
        File.WriteAllText(Path.Combine(full, "x.txt"), "x");
        Assert.Contains("not empty", OutputFolder.Resolve(full, DateTimeOffset.UtcNow, []).Error, StringComparison.Ordinal);
    }

    [Fact]
    public void FindRepositoryRoots_FindsGitFolderAbove()
    {
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var deep = Directory.CreateDirectory(Path.Combine(repo, "a", "b")).FullName;
        Assert.Contains(repo, OutputFolder.FindRepositoryRoots(deep), StringComparer.OrdinalIgnoreCase);
    }
}

public class AddressTaggerTests
{
    [Fact]
    public void Tag_StableWithinKey_DiffersAcrossKeys_DoesNotContainAddress()
    {
        var key1 = Enumerable.Repeat((byte)1, 32).ToArray();
        var key2 = Enumerable.Repeat((byte)2, 32).ToArray();
        const ulong address = 0xA1B2C3D4E5F6;
        var t1 = new AddressTagger(key1).Tag(address);
        Assert.Equal(t1, new AddressTagger(key1).Tag(address));
        Assert.NotEqual(t1, new AddressTagger(key2).Tag(address));
        Assert.NotEqual(t1, new AddressTagger(key1).Tag(address + 1));
        Assert.Matches("^addr#[0-9a-f]{8}$", t1);
        Assert.DoesNotContain("e5f6", t1, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Constructor_ShortKey_Throws()
        => Assert.Throws<ArgumentException>(() => new AddressTagger(new byte[8]));
}

public class AppleTlvTests
{
    [Fact]
    public void Walk_TwoEntries_ThenTruncated()
    {
        byte[] data = [0x10, 0x02, 0xAA, 0xBB, 0x0C, 0x01, 0xCC, 0x09, 0x05, 0x01];
        var e = AppleTlv.Walk(data);
        Assert.Equal([(byte)0x10, (byte)0x0C, (byte)0x09], e.Select(x => x.Type));
        Assert.False(e[0].Truncated);
        Assert.True(e[2].Truncated);
    }

    [Fact]
    public void ExplainParseFailure_Reasons()
    {
        byte[] nearby = [0x10, 0x02, 0xAA, 0xBB];
        Assert.StartsWith("not a proximity message (types: 0x10)", AppleTlv.ExplainParseFailure(nearby, AppleTlv.Walk(nearby)), StringComparison.Ordinal);
        byte[] pairing = [0x07, 0x05, 0x00, 0x24, 0x20, 0x01, 0x02];
        Assert.StartsWith("pairing-mode", AppleTlv.ExplainParseFailure(pairing, AppleTlv.Walk(pairing)), StringComparison.Ordinal);
        byte[] truncated = [0x07, 0x19, 0x01, 0x24, 0x20, 0x55];
        Assert.StartsWith("truncated", AppleTlv.ExplainParseFailure(truncated, AppleTlv.Walk(truncated)), StringComparison.Ordinal);
    }
}

public class RunSummaryTests
{
    /// <summary>07 19 | paired 01 | model 24 20 | status | pods | flags+case | lid | colour | conn | 16-byte tail.</summary>
    private static byte[] Proximity(byte status, byte pods, byte caseByte, byte lid)
        => [0x07, 0x19, 0x01, 0x24, 0x20, status, pods, caseByte, lid, 0x00, 0x04, .. Enumerable.Repeat((byte)0xEE, 16)];

    private static SightingObservation Obs(long seq, double t, string tag, byte[] data, bool name = false)
        => SightingObservation.Create(seq, DateTimeOffset.UnixEpoch.AddSeconds(t), t, tag, -60, 0x004C, data, name);

    [Fact]
    public void Sighting_ParsesProximity_AndRecordsNoName()
    {
        var o = Obs(1, 1.5, "addr#00000001", Proximity(0x55, 0x88, 0x98, 0x01), name: true);
        Assert.NotNull(o.Proximity);
        Assert.Null(o.ParseFailure);
        string json = o.ToJson().ToJsonString();
        Assert.Contains("\"namePresent\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"modelId\":\"0x2420\"", json, StringComparison.Ordinal);
        Assert.Contains("\"identity\":\"unconfirmed\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_GroupsByModel_TracksTagsOverTime_AndHistograms()
    {
        var b = new RunSummaryBuilder(bucketSeconds: 5);
        b.Add(Obs(1, 1, "addr#aaaaaaaa", Proximity(0x55, 0x88, 0x98, 0x09)));
        b.Add(Obs(2, 2, "addr#bbbbbbbb", Proximity(0x75, 0x88, 0x98, 0x09)));
        b.Add(Obs(3, 7, "addr#cccccccc", Proximity(0x55, 0x87, 0x18, 0x01)));
        b.Add(Obs(4, 8, "addr#dddddddd", [0x10, 0x02, 0xAA, 0xBB]));
        b.Add(Obs(5, 9, "addr#dddddddd", [0x10, 0x02, 0xAA, 0xBB, 0x0C, 0x01, 0xCC]));

        Assert.Equal(5, b.Total);
        Assert.Equal(3, b.ProximityCount);
        Assert.Equal(4, b.DistinctTags);

        var run = new RunInfo
        {
            ProbeVersion = "1.0.0", Arguments = ["--seconds", "60"], Seconds = 60, OutputFolder = @"D:\out",
            StartedUtc = DateTimeOffset.UnixEpoch, StoppedUtc = DateTimeOffset.UnixEpoch.AddSeconds(10),
            Ended = "timer", ExitCode = 0,
        };
        var json = b.BuildJson(run);
        var model = json["models"]!.AsArray().Single()!;
        Assert.Equal("0x2420", (string?)model["modelId"]);
        Assert.Equal(3, (int)model["distinctAddressTags"]!);

        var buckets = json["distinctTagsOverTime"]!.AsArray();
        Assert.Equal(2, (int)buckets[0]!["distinctTags"]!);
        Assert.Equal(2, (int)buckets[1]!["distinctTags"]!);
        Assert.Equal(1, (int)buckets[1]!["distinctProximityTags"]!);
        Assert.Equal(2, (int)buckets[1]!["newTags"]!);

        var hist = json["histograms"]!;
        Assert.Equal(2, (long)hist["statusByte"]!["0x55"]!);
        Assert.Equal(1, (long)hist["statusByte"]!["0x75"]!);
        Assert.Equal(3, (long)hist["statusBitSet"]!["0x01"]!);
        Assert.Equal(1, (long)hist["statusBitSet"]!["0x20"]!);
        Assert.Equal(3, (long)hist["podsHighNibble"]!["8"]!);
        Assert.Equal(2, (long)hist["podsLowNibble"]!["8"]!);
        Assert.Equal(1, (long)hist["podsLowNibble"]!["7"]!);
        Assert.Equal(3, (long)hist["caseNibble"]!["8"]!);

        var other = json["nonProximityMessageTypes"]!;
        Assert.Equal(2, (long)other["0x10"]!);
        Assert.Equal(1, (long)other["0x0C"]!);

        string text = b.BuildText(run);
        Assert.Contains("UNCONFIRMED", text, StringComparison.Ordinal);
        Assert.Contains("UNCONFIRMED", (string?)json["identityNotice"], StringComparison.Ordinal);
        Assert.Contains("0x10=2", text, StringComparison.Ordinal);
    }
}
