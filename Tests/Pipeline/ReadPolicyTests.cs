using Iris.Core.Detection;
using Iris.Core.Pipeline;
using Xunit;

namespace Iris.Tests.Pipeline;

public class ReadPolicyTests
{
    private const string Name = "SOMEPLAYER";
    private const double Tick = 83.3;

    private readonly FakeJudge _judge = new();
    private readonly SessionNames _names = new();
    private readonly FakeResolver _resolver = new();
    private readonly PlateState _plate = new(1, 0.0);
    private readonly ReadPolicy _policy;
    private long _frame;

    public ReadPolicyTests() => _policy = new ReadPolicy(PipelineSettings.Default, _resolver, _judge, new WhiteDuplicateRule(3), _names);

    private PolicyEvent Read(double ms, string name = Name, double width = 80.0, long? frame = null)
    {
        var observation = new ReadObservation(ms, frame ?? ++_frame, ObservationClass.Name, name, Array.Empty<O0Glyph>(), width, PlateColor.White, null, "", 14.0);
        return _policy.Apply(_plate, observation, 1).Event;
    }

    [Fact]
    public void AnUnknownNameStillNeedsThreeReads()
    {
        Assert.Equal(PolicyEvent.None, Read(0.0));
        Assert.Equal(PolicyEvent.None, Read(Tick));
        Assert.Equal(PolicyEvent.Confirmed, Read(2 * Tick));
    }

    [Fact]
    public void ANameVerifiedThisSessionConfirmsOnTwoReads()
    {
        _names.AddVerified(Name);

        Assert.Equal(PolicyEvent.None, Read(0.0));
        Assert.Equal(PolicyEvent.Confirmed, Read(Tick));
        Assert.Equal(PlateStatus.Confirmed, _plate.Status);
        Assert.Equal(Name, _plate.Name);
    }

    [Fact]
    public void AKnownPlayerFromTheCacheOrListsConfirmsOnTwoReads()
    {
        _judge.Known.Add(Name);

        Read(0.0);

        Assert.Equal(PolicyEvent.Confirmed, Read(Tick));
    }

    [Fact]
    public void OneReadIsNeverEnoughEvenForAKnownPlayer()
    {
        _names.AddVerified(Name);

        Assert.Equal(PolicyEvent.None, Read(0.0));
    }

    [Fact]
    public void TwoKnownReadsTooCloseTogetherDoNotConfirm()
    {
        _names.AddVerified(Name);

        Read(0.0);

        Assert.Equal(PolicyEvent.None, Read(PipelineSettings.Default.KnownConfirmSpanMs - 1.0));
    }

    [Fact]
    public void TwoKnownReadsOfTheSameFrameDoNotConfirm()
    {
        _names.AddVerified(Name);

        Read(0.0, frame: 7);

        Assert.Equal(PolicyEvent.None, Read(Tick, frame: 7));
    }

    [Fact]
    public void TwoDifferentNamesDoNotConfirm()
    {
        _names.AddVerified(Name);
        _names.AddVerified("OTHERPLAYER");

        Read(0.0, "OTHERPLAYER");

        Assert.Equal(PolicyEvent.None, Read(Tick));
    }

    [Fact]
    public void AKnownNameWhoseWidthJumpsDoesNotConfirm()
    {
        _names.AddVerified(Name);

        Read(0.0, width: 40.0);

        Assert.Equal(PolicyEvent.None, Read(Tick, width: 80.0));
    }

    [Fact]
    public void AKnownNameWithUndecidedOAndZeroStillWaits()
    {
        _judge.Known.Add(Name);
        _resolver.Decided = false;

        Read(0.0);

        Assert.Equal(PolicyEvent.None, Read(Tick));
    }

    [Fact]
    public void TheJudgeIsNotAskedUntilTwoReadsAgree()
    {
        Read(0.0);

        Assert.Equal(0, _judge.KnownQuestions);
    }

    private sealed class FakeJudge : IReadJudge
    {
        public HashSet<string> Known { get; } = new();

        public int KnownQuestions { get; private set; }

        public NameClass Classify(string name) => NameClass.Valid;

        public bool IsListed(string name) => false;

        public bool IsKnownPlayer(string name)
        {
            KnownQuestions++;
            return Known.Contains(name);
        }
    }

    [Fact]
    public void AnExactReadOfAVerifiedNameDoesNotWaitForOAndZeroEvidence()
    {
        _names.AddVerified(Name);
        _resolver.Decided = false;

        Assert.Equal(PolicyEvent.None, Read(0.0));
        Assert.Equal(PolicyEvent.Confirmed, Read(Tick));
    }

    [Fact]
    public void WithBothLookAlikeSpellingsVerifiedTheEvidenceStillDecides()
    {
        _names.AddVerified("PILOT0");
        _names.AddVerified("PILOTO");
        _resolver.Decided = false;

        Read(0.0, "PILOT0");

        Assert.Equal(PolicyEvent.None, Read(Tick, "PILOT0"));
    }

    [Fact]
    public void AnUnverifiedNameStillWaitsForOAndZeroEvidence()
    {
        _resolver.Decided = false;

        Read(0.0);
        Read(Tick);

        Assert.Equal(PolicyEvent.None, Read(2 * Tick));
    }

    private sealed class FakeResolver : INameResolver
    {
        public bool Decided { get; set; } = true;

        public ResolvedName Resolve(PlateState plate, string name, IReadOnlyList<O0Glyph> glyphs) => new(name, Decided);
    }
}
