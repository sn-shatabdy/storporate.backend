using Storporate.Modules.InstitutionalClubNetwork.SponsorshipMatching;

namespace Storporate.Tests.Unit.InstitutionalClubNetwork;

/// <summary>STOR-71 pure matcher and plain-word search rules.</summary>
public class SponsorshipMatcherTests
{
    private static MatchGoal Goal(
        string[]? fields = null,
        int[]? years = null,
        string[]? universities = null,
        string[]? cities = null,
        string[]? kinds = null) => new(
            "Campus push",
            "Acme Ltd",
            ["Brand awareness"],
            kinds ?? [],
            fields ?? [],
            years ?? [],
            cities ?? [],
            universities ?? []);

    private static MatchClub Club(
        string[]? fields = null,
        int[]? years = null,
        string university = "Some University",
        string? city = null,
        (string Title, string? Description)[]? events = null) => new(
            "Tech Club",
            university,
            city,
            fields ?? [],
            years ?? [],
            (events ?? []).Select(e => new MatchEvent(e.Title, e.Description)).ToList());

    [Fact]
    public void FieldOfStudy_OverlapsCaseInsensitivelyAfterTrim()
    {
        var overlap = SponsorshipMatcher.ComputeOverlap(
            Goal(fields: ["Computer Science", "Law"]),
            Club(fields: ["  computer science ", "Arts"]));
        Assert.Equal(new[] { "Computer Science" }, overlap.Fields);
        Assert.Equal(1, overlap.DimensionCount);
    }

    [Fact]
    public void StudyYear_OverlapsOnSharedYears()
    {
        var overlap = SponsorshipMatcher.ComputeOverlap(Goal(years: [1, 2, 3]), Club(years: [3, 4, 2]));
        Assert.Equal(new[] { 2, 3 }, overlap.Years);
    }

    [Fact]
    public void University_OverlapsCaseInsensitively()
    {
        var overlap = SponsorshipMatcher.ComputeOverlap(
            Goal(universities: ["University of Dhaka"]),
            Club(university: " university of dhaka "));
        Assert.Equal(new[] { "University of Dhaka" }, overlap.Universities);
    }

    [Fact]
    public void City_OverlapsOnlyWhenTheClubHasOne()
    {
        Assert.Equal(new[] { "Dhaka" }, SponsorshipMatcher.ComputeOverlap(Goal(cities: ["Dhaka"]), Club(city: "dhaka")).Cities);
        Assert.Empty(SponsorshipMatcher.ComputeOverlap(Goal(cities: ["Dhaka"]), Club(city: null)).Cities);
    }

    [Theory]
    [InlineData("Hackathon", "Annual Hackathon 2026", null, true)]
    [InlineData("Hackathon", "Night of code", "A 24 hour hackathons for beginners", true)]
    [InlineData("Career fair", "Job Fair", null, true)]
    [InlineData("Workshop", "Robotics bootcamp", null, true)]
    [InlineData("Sports event", "Inter-department Football", null, true)]
    [InlineData("Cultural event", "Spring Music Night", null, true)]
    [InlineData("Seminar", "Guest lecture series", null, true)]
    [InlineData("Conference", "Weekly meetup", "Board games", false)]
    [InlineData("Hackathon", "Whacking day", null, false)]
    public void EventKind_IsMatchedAgainstEventTitleAndDescription(string kind, string title, string? description, bool expected)
    {
        var overlap = SponsorshipMatcher.ComputeOverlap(Goal(kinds: [kind]), Club(events: [(title, description)]));
        Assert.Equal(expected, overlap.HasEventKinds);
    }

    [Fact]
    public void Band_NoOverlap_IsNone()
    {
        var overlap = SponsorshipMatcher.ComputeOverlap(Goal(fields: ["Law"]), Club(fields: ["Arts"]));
        Assert.Null(SponsorshipMatcher.BandOf(overlap));
        Assert.Empty(SponsorshipMatcher.ReasonsFor(overlap, MatchPerspective.Company));
    }

    [Fact]
    public void Band_Strong_NeedsEventKindPlusFieldOrYearPlusThreeDimensions()
    {
        var goal = Goal(fields: ["CS"], years: [2], universities: ["DU"], kinds: ["Hackathon"]);
        var hackathon = ("Hackathon", (string?)null);

        // event + field + year
        Assert.Equal(FitBand.Strong, Band(goal, Club(fields: ["CS"], years: [2], events: [hackathon])));
        // event + field + university
        Assert.Equal(FitBand.Strong, Band(goal, Club(fields: ["CS"], university: "DU", events: [hackathon])));
        // event + year + university
        Assert.Equal(FitBand.Strong, Band(goal, Club(years: [2], university: "DU", events: [hackathon])));
        // only two dimensions: not Strong
        Assert.Equal(FitBand.Good, Band(goal, Club(fields: ["CS"], events: [hackathon])));
        // no field or year, so event + university + city is not Strong
        var cityGoal = Goal(universities: ["DU"], cities: ["Dhaka"], kinds: ["Hackathon"]);
        Assert.Equal(FitBand.Good, Band(cityGoal, Club(university: "DU", city: "Dhaka", events: [hackathon])));
        // field + year + university without an event kind is not Strong
        Assert.Equal(FitBand.Good, Band(goal, Club(fields: ["CS"], years: [2], university: "DU")));
    }

    [Fact]
    public void Band_Good_NeedsTwoDimensionsIncludingFieldOrEventKind()
    {
        var goal = Goal(fields: ["CS"], years: [2], universities: ["DU"], kinds: ["Hackathon"]);
        Assert.Equal(FitBand.Good, Band(goal, Club(fields: ["CS"], years: [2])));
        Assert.Equal(FitBand.Good, Band(goal, Club(university: "DU", events: [("Hackathon", null)])));
    }

    [Fact]
    public void Band_Partial_IsOneDimensionOrTwoWithoutFieldOrEventKind()
    {
        var goal = Goal(fields: ["CS"], years: [2], universities: ["DU"], cities: ["Dhaka"], kinds: ["Hackathon"]);
        Assert.Equal(FitBand.Partial, Band(goal, Club(fields: ["CS"])));
        Assert.Equal(FitBand.Partial, Band(goal, Club(years: [2])));
        Assert.Equal(FitBand.Partial, Band(goal, Club(events: [("Hackathon", null)])));
        Assert.Equal(FitBand.Partial, Band(goal, Club(years: [2], university: "DU")));
        Assert.Equal(FitBand.Partial, Band(goal, Club(years: [2], university: "DU", city: "Dhaka")));
    }

    [Fact]
    public void Points_OrderStrongerOverlapsAbovePlainOnes()
    {
        var goal = Goal(fields: ["CS"], years: [2]);
        var both = SponsorshipMatcher.PointsOf(SponsorshipMatcher.ComputeOverlap(goal, Club(fields: ["CS"], years: [2])));
        var one = SponsorshipMatcher.PointsOf(SponsorshipMatcher.ComputeOverlap(goal, Club(years: [2])));
        Assert.True(both > one);
    }

    [Fact]
    public void Reasons_CompanyView_ReadAsPlainSentences()
    {
        var goal = Goal(fields: ["Computer Science"], years: [2, 3], universities: ["University of Dhaka"], kinds: ["Hackathon"]);
        var club = Club(fields: ["Computer Science"], years: [2, 3], university: "University of Dhaka", events: [("Hackathon", null)]);
        var reasons = SponsorshipMatcher.Assess(goal, club, MatchPerspective.Company).Reasons;

        Assert.Equal(
            new[]
            {
                "Their audience includes Computer Science, which you want to reach.",
                "They run hackathons, an event kind you would back.",
                "They study at University of Dhaka, one of your target universities.",
                "They reach Year 2 and Year 3 students.",
            },
            reasons);
    }

    [Fact]
    public void Reasons_ClubView_ReadAsPlainSentences()
    {
        var goal = Goal(fields: ["Computer Science"], kinds: ["Hackathon", "Workshop"]);
        var club = Club(fields: ["Computer Science"], events: [("Hackathon", null), ("Workshop", null)]);
        var reasons = SponsorshipMatcher.Assess(goal, club, MatchPerspective.Club).Reasons;

        Assert.Equal(
            new[]
            {
                "They want to reach Computer Science students, and your audience includes them.",
                "They back hackathons and workshops, and you run them.",
            },
            reasons);
        Assert.Contains("They back hackathons, and you run one.", SponsorshipMatcher.Assess(
            Goal(kinds: ["Hackathon"]), Club(events: [("Hackathon", null)]), MatchPerspective.Club).Reasons);
    }

    [Fact]
    public void Reasons_AreCappedAtFour_AndFollowTheWordingRules()
    {
        var goal = Goal(
            fields: ["Computer Science", "Engineering"],
            years: [1, 2],
            universities: ["University of Dhaka"],
            cities: ["Dhaka"],
            kinds: ["Hackathon", "Workshop"]);
        var club = Club(
            fields: ["Computer Science", "Engineering"],
            years: [1, 2],
            university: "University of Dhaka",
            city: "Dhaka",
            events: [("Hackathon", null), ("Workshop", null)]);

        foreach (var perspective in Enum.GetValues<MatchPerspective>())
        {
            var reasons = SponsorshipMatcher.Assess(goal, club, perspective).Reasons;
            Assert.Equal(SponsorshipMatcher.MaxReasons, reasons.Count);
            foreach (var reason in reasons)
            {
                Assert.EndsWith(".", reason);
                Assert.DoesNotContain('—', reason);
                Assert.DoesNotContain('–', reason);
                Assert.DoesNotContain('%', reason);
                Assert.DoesNotContain("score", reason, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("evidence", reason, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("proof", reason, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Query_DropsStopWordsShortTokensAndDuplicates()
    {
        var parsed = SponsorshipMatchQuery.Parse("  We need a HACKATHON for the students, hackathon! x ");
        Assert.Equal(new[] { "we", "hackathon" }, parsed.Tokens.Select(t => t.Original));
        Assert.True(SponsorshipMatchQuery.Parse("the and for").IsEmpty);
        Assert.True(SponsorshipMatchQuery.Parse("   ").IsEmpty);
        Assert.True(SponsorshipMatchQuery.Parse(null).IsEmpty);
    }

    [Fact]
    public void Query_ExpandsSynonyms()
    {
        var coding = SponsorshipMatchQuery.Parse("coding").Tokens.Single();
        Assert.Contains("hackathon", coding.Terms);
        Assert.Contains("computer science", coding.Terms);
        Assert.Contains("career fair", SponsorshipMatchQuery.Parse("hiring").Tokens.Single().Terms);
        Assert.Contains("csr education", SponsorshipMatchQuery.Parse("charity").Tokens.Single().Terms);
        Assert.Contains("sports event", SponsorshipMatchQuery.Parse("sports").Tokens.Single().Terms);
        Assert.Contains("cultural event", SponsorshipMatchQuery.Parse("music").Tokens.Single().Terms);
        Assert.Contains("business", SponsorshipMatchQuery.Parse("startup").Tokens.Single().Terms);
    }

    [Theory]
    [InlineData("year 2 hackathon", 2)]
    [InlineData("2nd year hackathon", 2)]
    [InlineData("hackathon year3", 3)]
    public void Query_HonoursYearMentions_AndDoesNotKeepThemAsWords(string query, int year)
    {
        var parsed = SponsorshipMatchQuery.Parse(query);
        Assert.Equal(new[] { year }, parsed.Years);
        Assert.Equal(new[] { "hackathon" }, parsed.Tokens.Select(t => t.Original));
    }

    [Fact]
    public void Query_Match_UsesWholeWordsAndSynonyms_AndRequiresMentionedYear()
    {
        var parsed = SponsorshipMatchQuery.Parse("coding year 2");
        var hit = SponsorshipMatchQuery.Match(parsed, "Robotics Club Annual Hackathons", [1, 2]);
        Assert.NotNull(hit);
        Assert.Equal(new[] { "coding" }, hit!.MatchedWords);
        Assert.Equal(new[] { 2 }, hit.MatchedYears);

        Assert.Null(SponsorshipMatchQuery.Match(parsed, "Robotics Club Annual Hackathons", [1, 3]));
        Assert.Null(SponsorshipMatchQuery.Match(SponsorshipMatchQuery.Parse("art"), "Smart Systems Club", []));
        Assert.NotNull(SponsorshipMatchQuery.Match(SponsorshipMatchQuery.Parse("hackathon"), "Hackathons", []));
    }

    [Fact]
    public void Query_Reasons_ListMatchedWords_MaxThree_NoDashesOrBannedWords()
    {
        var parsed = SponsorshipMatchQuery.Parse("alpha beta gamma delta year 2");
        var match = SponsorshipMatchQuery.Match(parsed, "alpha beta gamma delta", [2])!;
        var reasons = SponsorshipMatchQuery.ReasonsFor(match);
        Assert.Equal("Matches your words: alpha, beta, gamma.", reasons[0]);
        Assert.Equal("Matches the study year you asked for: Year 2.", reasons[1]);
        Assert.All(reasons, r =>
        {
            Assert.DoesNotContain('—', r);
            Assert.DoesNotContain("score", r, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static FitBand? Band(MatchGoal goal, MatchClub club) =>
        SponsorshipMatcher.BandOf(SponsorshipMatcher.ComputeOverlap(goal, club));
}
