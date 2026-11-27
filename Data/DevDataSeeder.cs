using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StudyBuddy.Models;

namespace StudyBuddy.Data;

/// <summary>
/// Opt-in, development-only, idempotent sample data seeder for two isolated demo accounts.
/// Never runs outside Development and never uses hardcoded passwords.
/// </summary>
public sealed class DevDataSeeder
{
    private readonly IHostEnvironment _environment;
    private readonly IOptions<DevSeedOptions> _options;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<DevDataSeeder> _logger;

    public DevDataSeeder(
        IHostEnvironment environment,
        IOptions<DevSeedOptions> options,
        UserManager<ApplicationUser> userManager,
        IDbContextFactory<AppDbContext> dbFactory,
        ILogger<DevDataSeeder> logger)
    {
        _environment = environment;
        _options = options;
        _userManager = userManager;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task SeedIfEnabledAsync(CancellationToken cancellationToken = default)
    {
        if (!_environment.IsDevelopment())
        {
            return;
        }

        var config = _options.Value;
        if (!config.Enabled)
        {
            return;
        }

        if (!HasValidCredentials(config.DemoUserOne) || !HasValidCredentials(config.DemoUserTwo))
        {
            _logger.LogWarning(
                "DevSeed is enabled, but DemoUserOne or DemoUserTwo Email/Password is missing from secure development configuration (User Secrets). Skipping sample data seeding.");
            return;
        }

        var userOne = await EnsureUserAsync(
            config.DemoUserOne,
            fallbackName: "Alex Rivera",
            fallbackTimeZone: "America/New_York");

        var userTwo = await EnsureUserAsync(
            config.DemoUserTwo,
            fallbackName: "Priya Nair",
            fallbackTimeZone: "Europe/London");

        if (userOne is null || userTwo is null)
        {
            return;
        }

        var nowUtc = DateTime.UtcNow;

        await EnsureDeckAsync(userOne.Id, BuildDistributedSystemsDeck(userOne.Id, nowUtc), cancellationToken);
        await EnsureDeckAsync(userTwo.Id, BuildCellularMetabolismDeck(userTwo.Id, nowUtc), cancellationToken);
    }

    private static bool HasValidCredentials(DevSeedAccountOptions account) =>
        !string.IsNullOrWhiteSpace(account.Email) &&
        !string.IsNullOrWhiteSpace(account.Password);

    private async Task<ApplicationUser?> EnsureUserAsync(
        DevSeedAccountOptions account,
        string fallbackName,
        string fallbackTimeZone)
    {
        var email = account.Email!.Trim();
        var existing = await _userManager.FindByEmailAsync(email);
        if (existing is not null)
        {
            return existing;
        }

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            FullName = string.IsNullOrWhiteSpace(account.FullName) ? fallbackName : account.FullName.Trim(),
            StudyTimeZoneId = string.IsNullOrWhiteSpace(account.StudyTimeZoneId) ? fallbackTimeZone : account.StudyTimeZoneId.Trim(),
            CreatedAtUtc = DateTime.UtcNow
        };

        var result = await _userManager.CreateAsync(user, account.Password!);
        if (!result.Succeeded)
        {
            var errors = string.Join("; ", result.Errors.Select(e => e.Description));
            _logger.LogWarning("Failed to create dev seed account {Email}: {Errors}", email, errors);
            return null;
        }

        return user;
    }

    private async Task EnsureDeckAsync(string userId, Deck seedDeck, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var exists = await db.Decks
            .AsNoTracking()
            .AnyAsync(d => d.UserId == userId && d.Title == seedDeck.Title, cancellationToken);

        if (exists)
        {
            return;
        }

        db.Decks.Add(seedDeck);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static Deck BuildDistributedSystemsDeck(string userId, DateTime nowUtc)
    {
        const string notes =
            "In distributed systems, the CAP theorem states that during a network partition a data store must choose between consistency and availability. " +
            "Linearizability guarantees that every read returns the value of the most recent completed write across all nodes. " +
            "Raft achieves consensus by electing a single leader that appends log entries and replicates them to a majority quorum before committing. " +
            "A split-brain condition occurs when network partitions allow two subsets of nodes to believe they hold leadership simultaneously. " +
            "Vector clocks capture causal ordering between events without relying on synchronized physical wall clocks. " +
            "Two-phase commit coordinates atomic transactions across shards, but a coordinator crash after the prepare phase can leave participants blocked. " +
            "Consistent hashing maps both keys and nodes onto a ring so adding or removing a node only remaps a small fraction of keys. " +
            "Idempotent operations allow clients to retry timed-out requests safely without applying duplicate side effects. " +
            "Read-your-writes consistency ensures that a user immediately observes updates they just submitted, even when replicas lag behind. " +
            "Write-ahead logging records state mutations sequentially to durable storage before applying changes to in-memory indexes.";

        var createdUtc = nowUtc.AddDays(-10);
        var deck = new Deck
        {
            UserId = userId,
            Title = "Distributed Systems & Consensus Fundamentals",
            NotesText = notes,
            SourceType = NoteSourceType.PastedText,
            OriginalFileName = null,
            CreatedAtUtc = createdUtc,
            UpdatedAtUtc = nowUtc.AddHours(-6)
        };

        var specs = new[]
        {
            new SeedQuestionSpec(
                "What trade-off does the CAP theorem require during a network partition?",
                new[] { "Latency and throughput", "Consistency and availability", "Durability and isolation", "Sharding and caching" },
                1,
                "When a network partition occurs, a distributed store can either preserve linearizable consistency or remain available to serve requests, not both.",
                "CAP Theorem",
                "In distributed systems, the CAP theorem states that during a network partition a data store must choose between consistency and availability.",
                "demo1-ds-q01",
                SelectedIndex: 1, Sm2Quality: 5, RepetitionCount: 2, IntervalDays: 6, EaseFactor: 2.6, DaysAgoAttempted: 2, DaysUntilNextReview: 4),
            new SeedQuestionSpec(
                "What does linearizability guarantee for read operations?",
                new[] { "Every read returns the value of the most recent completed write across all nodes", "Reads never fail during partitions", "Reads always hit local cache", "Writes are batched asynchronously" },
                0,
                "Linearizability provides a single global real-time order where reads reflect the latest completed write.",
                "Consistency Models",
                "Linearizability guarantees that every read returns the value of the most recent completed write across all nodes.",
                "demo1-ds-q02",
                SelectedIndex: 0, Sm2Quality: 4, RepetitionCount: 2, IntervalDays: 6, EaseFactor: 2.5, DaysAgoAttempted: 3, DaysUntilNextReview: 3),
            new SeedQuestionSpec(
                "How does Raft decide when a replicated log entry is committed?",
                new[] { "When the leader writes it locally", "When every node acknowledges it", "When a single leader replicates it to a majority quorum", "When a client retries twice" },
                2,
                "Raft commits an entry once the elected leader has replicated it to a majority quorum of cluster nodes.",
                "Consensus",
                "Raft achieves consensus by electing a single leader that appends log entries and replicates them to a majority quorum before committing.",
                "demo1-ds-q03",
                SelectedIndex: 1, Sm2Quality: 2, RepetitionCount: 0, IntervalDays: 1, EaseFactor: 2.18, DaysAgoAttempted: 2, DaysUntilNextReview: -1),
            new SeedQuestionSpec(
                "When does a split-brain condition happen in a cluster?",
                new[] { "When disk I/O saturates", "When network partitions allow two subsets of nodes to believe they hold leadership simultaneously", "When TLS certificates expire", "When garbage collection pauses a replica" },
                1,
                "Split-brain happens when partitioned groups independently act as leader, risking conflicting writes.",
                "Fault Tolerance",
                "A split-brain condition occurs when network partitions allow two subsets of nodes to believe they hold leadership simultaneously.",
                "demo1-ds-q04",
                SelectedIndex: 1, Sm2Quality: 5, RepetitionCount: 1, IntervalDays: 1, EaseFactor: 2.6, DaysAgoAttempted: 1, DaysUntilNextReview: 0),
            new SeedQuestionSpec(
                "Why are vector clocks used in distributed systems?",
                new[] { "To synchronize NTP servers", "To compress replication logs", "To capture causal ordering between events without relying on synchronized physical wall clocks", "To encrypt RPC frames" },
                2,
                "Vector clocks track logical causality across nodes even when physical clocks drift.",
                "Logical Time",
                "Vector clocks capture causal ordering between events without relying on synchronized physical wall clocks.",
                "demo1-ds-q05",
                SelectedIndex: 2, Sm2Quality: 4, RepetitionCount: 1, IntervalDays: 1, EaseFactor: 2.5, DaysAgoAttempted: 2, DaysUntilNextReview: -1),
            new SeedQuestionSpec(
                "What is a key blocking hazard of two-phase commit?",
                new[] { "A coordinator crash after the prepare phase can leave participants blocked", "Participants cannot use indexes", "It forbids read transactions", "It requires vector clocks" },
                0,
                "Once participants vote prepared, they must hold locks until the coordinator recovers to deliver the commit or abort decision.",
                "Distributed Transactions",
                "Two-phase commit coordinates atomic transactions across shards, but a coordinator crash after the prepare phase can leave participants blocked.",
                "demo1-ds-q06",
                SelectedIndex: 2, Sm2Quality: 1, RepetitionCount: 0, IntervalDays: 1, EaseFactor: 1.96, DaysAgoAttempted: 1, DaysUntilNextReview: 0),
            new SeedQuestionSpec(
                "What primary benefit does consistent hashing provide when cluster membership changes?",
                new[] { "Eliminates network hops", "Adding or removing a node only remaps a small fraction of keys", "Guarantees linearizable reads", "Prevents split-brain automatically" },
                1,
                "Placing keys and nodes on a hash ring limits key movement to neighbors when a node joins or leaves.",
                "Partitioning",
                "Consistent hashing maps both keys and nodes onto a ring so adding or removing a node only remaps a small fraction of keys.",
                "demo1-ds-q07",
                SelectedIndex: 1, Sm2Quality: 5, RepetitionCount: 3, IntervalDays: 15, EaseFactor: 2.7, DaysAgoAttempted: 4, DaysUntilNextReview: 11),
            new SeedQuestionSpec(
                "Why are idempotent operations important over unreliable networks?",
                new[] { "They eliminate serialization overhead", "They allow clients to retry timed-out requests safely without applying duplicate side effects", "They bypass authentication", "They remove the need for durable storage" },
                1,
                "Idempotency makes retries safe when a client cannot tell whether a timed-out request succeeded on the server.",
                "Reliability",
                "Idempotent operations allow clients to retry timed-out requests safely without applying duplicate side effects.",
                "demo1-ds-q08",
                SelectedIndex: 1, Sm2Quality: 4, RepetitionCount: 2, IntervalDays: 6, EaseFactor: 2.5, DaysAgoAttempted: 1, DaysUntilNextReview: 5),
            new SeedQuestionSpec(
                "What does read-your-writes consistency guarantee for an individual user?",
                new[] { "All users see identical latency", "The user immediately observes updates they just submitted, even when replicas lag behind", "Replicas never experience lag", "Writes are skipped if unchanged" },
                1,
                "Read-your-writes guarantees a user sees their own recent writes immediately, typically by routing their reads to the leader or tracking write versions.",
                "Consistency Models",
                "Read-your-writes consistency ensures that a user immediately observes updates they just submitted, even when replicas lag behind.",
                "demo1-ds-q09",
                SelectedIndex: null, Sm2Quality: null, RepetitionCount: 0, IntervalDays: 0, EaseFactor: 2.5, DaysAgoAttempted: null, DaysUntilNextReview: 0),
            new SeedQuestionSpec(
                "How does write-ahead logging improve durability in storage engines?",
                new[] { "By storing indexes only in RAM", "By recording state mutations sequentially to durable storage before applying changes to in-memory indexes", "By skipping fsync on commits", "By broadcasting writes to clients" },
                1,
                "Appending changes to a durable sequential log first enables crash recovery of in-memory structures.",
                "Storage Engines",
                "Write-ahead logging records state mutations sequentially to durable storage before applying changes to in-memory indexes.",
                "demo1-ds-q10",
                SelectedIndex: null, Sm2Quality: null, RepetitionCount: 0, IntervalDays: 0, EaseFactor: 2.5, DaysAgoAttempted: null, DaysUntilNextReview: 0)
        };

        AddQuestionsToDeck(deck, specs, createdUtc, nowUtc);
        return deck;
    }

    private static Deck BuildCellularMetabolismDeck(string userId, DateTime nowUtc)
    {
        const string notes =
            "Glycolysis occurs in the cytosol and converts one molecule of glucose into two molecules of pyruvate while producing a net gain of two ATP and two NADH. " +
            "Phosphofructokinase-1 is the primary rate-limiting enzyme of glycolysis and is allosterically inhibited by high levels of ATP and citrate. " +
            "Before entering the citric acid cycle, pyruvate is oxidized by the pyruvate dehydrogenase complex into acetyl-CoA inside the mitochondrial matrix. " +
            "The citric acid cycle generates three NADH, one FADH2, and one GTP per turn for each acetyl-CoA molecule oxidized. " +
            "Oxidative phosphorylation couples electron transport across the inner mitochondrial membrane to proton pumping into the intermembrane space. " +
            "ATP synthase uses the resulting electrochemical proton gradient to drive phosphorylation of ADP into ATP. " +
            "Oxygen serves as the terminal electron acceptor at Complex IV of the electron transport chain, forming water. " +
            "Under anaerobic conditions in skeletal muscle, lactate dehydrogenase reduces pyruvate to lactate to regenerate NAD+ for continued glycolysis. " +
            "Beta-oxidation breaks down fatty acyl-CoA chains in the mitochondrial matrix into two-carbon acetyl-CoA units along with NADH and FADH2. " +
            "Gluconeogenesis synthesizes glucose from non-carbohydrate precursors such as lactate, glycerol, and glucogenic amino acids primarily in the liver.";

        var createdUtc = nowUtc.AddDays(-8);
        var deck = new Deck
        {
            UserId = userId,
            Title = "Cellular Metabolism & Bioenergetics",
            NotesText = notes,
            SourceType = NoteSourceType.UploadedFile,
            OriginalFileName = "biochem-metabolism-notes.txt",
            CreatedAtUtc = createdUtc,
            UpdatedAtUtc = nowUtc.AddHours(-3)
        };

        var specs = new[]
        {
            new SeedQuestionSpec(
                "Where does glycolysis take place and what is its net ATP yield per glucose?",
                new[] { "Mitochondrial matrix; 4 ATP", "Cytosol; net gain of two ATP", "Nucleus; net gain of two ATP", "Intermembrane space; 32 ATP" },
                1,
                "Glycolysis runs in the cytosol and nets 2 ATP and 2 NADH per glucose converted to 2 pyruvate.",
                "Glycolysis",
                "Glycolysis occurs in the cytosol and converts one molecule of glucose into two molecules of pyruvate while producing a net gain of two ATP and two NADH.",
                "demo2-bio-q01",
                SelectedIndex: 1, Sm2Quality: 5, RepetitionCount: 2, IntervalDays: 6, EaseFactor: 2.6, DaysAgoAttempted: 2, DaysUntilNextReview: 4),
            new SeedQuestionSpec(
                "Which enzyme is the primary rate-limiting step of glycolysis?",
                new[] { "Hexokinase", "Pyruvate kinase", "Phosphofructokinase-1", "Citrate synthase" },
                2,
                "PFK-1 commits glucose to glycolysis and is inhibited by high ATP and citrate.",
                "Enzyme Regulation",
                "Phosphofructokinase-1 is the primary rate-limiting enzyme of glycolysis and is allosterically inhibited by high levels of ATP and citrate.",
                "demo2-bio-q02",
                SelectedIndex: 2, Sm2Quality: 4, RepetitionCount: 2, IntervalDays: 6, EaseFactor: 2.5, DaysAgoAttempted: 4, DaysUntilNextReview: 2),
            new SeedQuestionSpec(
                "What converts pyruvate into acetyl-CoA before the citric acid cycle?",
                new[] { "Lactate dehydrogenase", "Pyruvate dehydrogenase complex", "ATP synthase", "Aldolase" },
                1,
                "The pyruvate dehydrogenase complex oxidizes pyruvate to acetyl-CoA in the mitochondrial matrix.",
                "Link Reaction",
                "Before entering the citric acid cycle, pyruvate is oxidized by the pyruvate dehydrogenase complex into acetyl-CoA inside the mitochondrial matrix.",
                "demo2-bio-q03",
                SelectedIndex: 0, Sm2Quality: 2, RepetitionCount: 0, IntervalDays: 1, EaseFactor: 2.18, DaysAgoAttempted: 2, DaysUntilNextReview: -1),
            new SeedQuestionSpec(
                "What coenzymes and high-energy nucleotide are produced per turn of the citric acid cycle?",
                new[] { "Three NADH, one FADH2, and one GTP", "Two NADH and two ATP", "One NADH and three FADH2", "Four ATP and zero NADH" },
                0,
                "Each acetyl-CoA oxidized in one turn yields 3 NADH, 1 FADH2, and 1 GTP.",
                "Citric Acid Cycle",
                "The citric acid cycle generates three NADH, one FADH2, and one GTP per turn for each acetyl-CoA molecule oxidized.",
                "demo2-bio-q04",
                SelectedIndex: 0, Sm2Quality: 5, RepetitionCount: 1, IntervalDays: 1, EaseFactor: 2.6, DaysAgoAttempted: 1, DaysUntilNextReview: 0),
            new SeedQuestionSpec(
                "Into which compartment are protons pumped during oxidative phosphorylation?",
                new[] { "Cytosol", "Intermembrane space", "Endoplasmic reticulum", "Lysosomal lumen" },
                1,
                "Electron transport complexes pump protons from the matrix into the intermembrane space.",
                "Oxidative Phosphorylation",
                "Oxidative phosphorylation couples electron transport across the inner mitochondrial membrane to proton pumping into the intermembrane space.",
                "demo2-bio-q05",
                SelectedIndex: 1, Sm2Quality: 4, RepetitionCount: 1, IntervalDays: 1, EaseFactor: 2.5, DaysAgoAttempted: 2, DaysUntilNextReview: -1),
            new SeedQuestionSpec(
                "What directly drives ATP synthesis by ATP synthase?",
                new[] { "Direct substrate-level phosphate transfer from pyruvate", "The electrochemical proton gradient", "Hydrolysis of NADH", "DNA transcription" },
                1,
                "Protons flowing back down the electrochemical gradient power rotary catalysis in ATP synthase.",
                "Chemiosmosis",
                "ATP synthase uses the resulting electrochemical proton gradient to drive phosphorylation of ADP into ATP.",
                "demo2-bio-q06",
                SelectedIndex: 1, Sm2Quality: 5, RepetitionCount: 3, IntervalDays: 15, EaseFactor: 2.7, DaysAgoAttempted: 3, DaysUntilNextReview: 12),
            new SeedQuestionSpec(
                "What role does oxygen play in the electron transport chain?",
                new[] { "It donates electrons to Complex I", "It phosphorylates glucose directly", "It serves as the terminal electron acceptor at Complex IV, forming water", "It transports pyruvate across membranes" },
                2,
                "Oxygen accepts electrons at Complex IV and combines with protons to form water.",
                "Electron Transport Chain",
                "Oxygen serves as the terminal electron acceptor at Complex IV of the electron transport chain, forming water.",
                "demo2-bio-q07",
                SelectedIndex: 2, Sm2Quality: 4, RepetitionCount: 2, IntervalDays: 6, EaseFactor: 2.5, DaysAgoAttempted: 1, DaysUntilNextReview: 5),
            new SeedQuestionSpec(
                "Why does skeletal muscle reduce pyruvate to lactate under anaerobic conditions?",
                new[] { "To consume excess oxygen", "To regenerate NAD+ for continued glycolysis", "To synthesize fatty acids", "To activate Complex IV" },
                1,
                "Converting pyruvate to lactate oxidizes NADH back to NAD+ so glycolysis can keep producing ATP.",
                "Fermentation",
                "Under anaerobic conditions in skeletal muscle, lactate dehydrogenase reduces pyruvate to lactate to regenerate NAD+ for continued glycolysis.",
                "demo2-bio-q08",
                SelectedIndex: 3, Sm2Quality: 1, RepetitionCount: 0, IntervalDays: 1, EaseFactor: 1.96, DaysAgoAttempted: 1, DaysUntilNextReview: 0),
            new SeedQuestionSpec(
                "What two-carbon product is released repeatedly during mitochondrial beta-oxidation?",
                new[] { "Pyruvate", "Acetyl-CoA", "Citrate", "Lactate" },
                1,
                "Beta-oxidation cleaves fatty acyl-CoA chains into two-carbon acetyl-CoA units along with NADH and FADH2.",
                "Lipid Metabolism",
                "Beta-oxidation breaks down fatty acyl-CoA chains in the mitochondrial matrix into two-carbon acetyl-CoA units along with NADH and FADH2.",
                "demo2-bio-q09",
                SelectedIndex: null, Sm2Quality: null, RepetitionCount: 0, IntervalDays: 0, EaseFactor: 2.5, DaysAgoAttempted: null, DaysUntilNextReview: 0),
            new SeedQuestionSpec(
                "Which organ is the primary site of gluconeogenesis from non-carbohydrate precursors?",
                new[] { "Spleen", "Liver", "Pancreas", "Heart" },
                1,
                "The liver primarily performs gluconeogenesis from lactate, glycerol, and glucogenic amino acids.",
                "Gluconeogenesis",
                "Gluconeogenesis synthesizes glucose from non-carbohydrate precursors such as lactate, glycerol, and glucogenic amino acids primarily in the liver.",
                "demo2-bio-q10",
                SelectedIndex: null, Sm2Quality: null, RepetitionCount: 0, IntervalDays: 0, EaseFactor: 2.5, DaysAgoAttempted: null, DaysUntilNextReview: 0)
        };

        AddQuestionsToDeck(deck, specs, createdUtc, nowUtc);
        return deck;
    }

    private static void AddQuestionsToDeck(
        Deck deck,
        SeedQuestionSpec[] specs,
        DateTime createdUtc,
        DateTime nowUtc)
    {
        for (var i = 0; i < specs.Length; i++)
        {
            var spec = specs[i];
            var question = new Question
            {
                QuestionText = spec.QuestionText,
                CorrectIndex = spec.CorrectIndex,
                Explanation = spec.Explanation,
                Topic = spec.Topic,
                SourceSentence = spec.SourceSentence,
                CreatedAtUtc = createdUtc.AddMinutes(i + 1)
            };

            for (var optIndex = 0; optIndex < spec.Options.Length; optIndex++)
            {
                question.Options.Add(new QuestionOption
                {
                    Index = optIndex,
                    Text = spec.Options[optIndex]
                });
            }

            DateTime? lastReviewedUtc = null;
            if (spec.SelectedIndex.HasValue && spec.Sm2Quality.HasValue && spec.DaysAgoAttempted.HasValue)
            {
                var attemptedUtc = nowUtc.AddDays(-spec.DaysAgoAttempted.Value);
                lastReviewedUtc = attemptedUtc;
                question.Attempts.Add(new Attempt
                {
                    SelectedIndex = spec.SelectedIndex.Value,
                    IsCorrect = spec.SelectedIndex.Value == spec.CorrectIndex,
                    Sm2Quality = spec.Sm2Quality.Value,
                    AttemptedAtUtc = attemptedUtc,
                    SubmissionId = spec.SubmissionId
                });
            }

            question.ReviewState = new ReviewState
            {
                RepetitionCount = spec.RepetitionCount,
                IntervalDays = spec.IntervalDays,
                EaseFactor = spec.EaseFactor,
                LastReviewedAtUtc = lastReviewedUtc,
                NextReviewUtc = nowUtc.AddDays(spec.DaysUntilNextReview)
            };

            deck.Questions.Add(question);
        }
    }

    private sealed record SeedQuestionSpec(
        string QuestionText,
        string[] Options,
        int CorrectIndex,
        string Explanation,
        string Topic,
        string SourceSentence,
        string SubmissionId,
        int? SelectedIndex,
        int? Sm2Quality,
        int RepetitionCount,
        int IntervalDays,
        double EaseFactor,
        int? DaysAgoAttempted,
        int DaysUntilNextReview);
}

