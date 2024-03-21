using System.ComponentModel;
using GitVersion.Common;
using GitVersion.Configuration;
using GitVersion.Core;
using GitVersion.Extensions;
using GitVersion.Git;
using GitVersion.VersionCalculation.TrunkBased;
using GitVersion.VersionCalculation.TrunkBased.NonTrunk;
using GitVersion.VersionCalculation.TrunkBased.Trunk;

namespace GitVersion.VersionCalculation;

internal sealed class TrunkBasedVersionStrategy(
    Lazy<GitVersionContext> contextLazy,
    IRepositoryStore repositoryStore,
    ITaggedSemanticVersionRepository taggedSemanticVersionRepository,
    IIncrementStrategyFinder incrementStrategyFinder)
    : IVersionStrategy
{
    private volatile int iterationCounter;
    private readonly Lazy<GitVersionContext> contextLazy = contextLazy.NotNull();
    private readonly ITaggedSemanticVersionRepository taggedSemanticVersionRepository = taggedSemanticVersionRepository.NotNull();
    private readonly IRepositoryStore repositoryStore = repositoryStore.NotNull();
    private readonly IIncrementStrategyFinder incrementStrategyFinder = incrementStrategyFinder.NotNull();

    private GitVersionContext Context => contextLazy.Value;

    private static readonly IReadOnlyCollection<ITrunkBasedContextPreEnricher> TrunkContextPreEnricherCollection =
    [
        new EnrichSemanticVersion(),
        new EnrichIncrement()
    ];
    private static readonly IReadOnlyCollection<ITrunkBasedContextPostEnricher> TrunkContextPostEnricherCollection =
    [
        new RemoveSemanticVersion(),
        new RemoveIncrement()
    ];
    private static readonly IReadOnlyCollection<ITrunkBasedIncrementer> TrunkIncrementerCollection =
    [
        // Trunk
        new CommitOnTrunk(),

        new CommitOnTrunkWithPreReleaseTag(),
        new LastCommitOnTrunkWithPreReleaseTag(),

        new CommitOnTrunkWithStableTag(),
        new LastCommitOnTrunkWithStableTag(),

        new MergeCommitOnTrunk(),
        new LastMergeCommitOnTrunk(),

        new CommitOnTrunkBranchedToTrunk(),
        new CommitOnTrunkBranchedToNonTrunk(),

        // NonTrunk
        new CommitOnNonTrunk(),
        new CommitOnNonTrunkWithPreReleaseTag(),
        new LastCommitOnNonTrunkWithPreReleaseTag(),

        new CommitOnNonTrunkWithStableTag(),
        new LastCommitOnNonTrunkWithStableTag(),

        new MergeCommitOnNonTrunk(),
        new LastMergeCommitOnNonTrunk(),

        new CommitOnNonTrunkBranchedToTrunk(),
        new CommitOnNonTrunkBranchedToNonTrunk()
    ];

    public IEnumerable<BaseVersion> GetBaseVersions(EffectiveBranchConfiguration configuration)
    {
        configuration.NotNull();

        if (!Context.Configuration.VersionStrategy.HasFlag(VersionStrategies.TrunkBased))
            yield break;

        var iteration = CreateIteration(branchName: Context.CurrentBranch.Name, configuration: configuration.Value);

        var commitsInReverseOrder = configuration.Value.Ignore.Filter(Context.CurrentBranchCommits);

        var taggedSemanticVersions = taggedSemanticVersionRepository.GetAllTaggedSemanticVersions(
            Context.Configuration, configuration.Value, Context.CurrentBranch, null, Context.CurrentCommit.When
        );

        var targetLabel = configuration.Value.GetBranchSpecificLabel(Context.CurrentBranch.Name, null);
        IterateOverCommitsRecursive(
            commitsInReverseOrder: commitsInReverseOrder,
            iteration: iteration,
            targetLabel: targetLabel,
            taggedSemanticVersions: taggedSemanticVersions
        );

        yield return DetermineBaseVersion(iteration, targetLabel);
    }

    private TrunkBasedIteration CreateIteration(
        ReferenceName branchName, EffectiveConfiguration configuration, TrunkBasedIteration? parent = null)
    {
        var iterationCount = Interlocked.Increment(ref iterationCounter);
        return new(
            id: $"#{iterationCount}",
            branchName: branchName,
            configuration: configuration,
            parent: parent
        );
    }

    private bool IterateOverCommitsRecursive(
        IEnumerable<ICommit> commitsInReverseOrder, TrunkBasedIteration iteration, string? targetLabel,
        ILookup<ICommit, SemanticVersionWithTag> taggedSemanticVersions, HashSet<ICommit>? traversedCommits = null)
    {
        traversedCommits ??= [];

        Lazy<IReadOnlyDictionary<ICommit, EffectiveBranchConfiguration>> commitsWasBranchedFromLazy = new(
            () => GetCommitsWasBranchedFrom(branchName: iteration.BranchName)
        );

        var configuration = iteration.Configuration;
        var branchName = iteration.BranchName;

        foreach (var item in commitsInReverseOrder)
        {
            if (!traversedCommits.Add(item)) continue;

            if (commitsWasBranchedFromLazy.Value.TryGetValue(item, out var effectiveConfigurationWasBranchedFrom)
                && (!configuration.IsMainBranch || effectiveConfigurationWasBranchedFrom.Value.IsMainBranch))
            {
                configuration = effectiveConfigurationWasBranchedFrom.Value;
                branchName = effectiveConfigurationWasBranchedFrom.Branch.Name;
                taggedSemanticVersions = taggedSemanticVersionRepository.GetAllTaggedSemanticVersions(
                    Context.Configuration, effectiveConfigurationWasBranchedFrom.Value, effectiveConfigurationWasBranchedFrom.Branch, null, Context.CurrentCommit.When
                );
            }

            var incrementForcedByCommit = GetIncrementForcedByCommit(item, configuration);
            var commit = iteration.CreateCommit(item, branchName, configuration, incrementForcedByCommit);

            var semanticVersions = taggedSemanticVersions[item].ToArray();
            commit.AddSemanticVersions(semanticVersions.Select(element => element.Value));

            var label = targetLabel ?? configuration.GetBranchSpecificLabel(branchName, null);
            foreach (var semanticVersion in semanticVersions)
            {
                if (semanticVersion.Value.IsMatchForBranchSpecificLabel(label)) return true;
            }

            if (item.IsMergeCommit())
            {
                Lazy<IReadOnlyCollection<ICommit>> mergedCommitsInReverseOrderLazy = new(
                    () => incrementStrategyFinder.GetMergedCommits(item, 1, configuration.Ignore).Reverse().ToList()
                );

                if (configuration.TrackMergeMessage
                    && MergeMessage.TryParse(item, Context.Configuration, out var mergeMessage))
                {
                    if (mergeMessage.Version is not null)
                    {
                        commit.AddSemanticVersions(mergeMessage.Version);
                        return true;
                    }

                    if (mergeMessage.MergedBranch is not null)
                    {
                        var childConfiguration = Context.Configuration.GetEffectiveConfiguration(
                            mergeMessage.MergedBranch
                        );

                        if (childConfiguration.IsMainBranch)
                        {
                            if (configuration.IsMainBranch) throw new NotImplementedException();
                            mergedCommitsInReverseOrderLazy = new(
                                () => incrementStrategyFinder.GetMergedCommits(item, 0, configuration.Ignore).Reverse().ToList()
                            );
                            childConfiguration = configuration;
                        }

                        var childIteration = CreateIteration(
                            branchName: mergeMessage.MergedBranch,
                            configuration: childConfiguration,
                            parent: iteration
                        );

                        var done = IterateOverCommitsRecursive(
                            commitsInReverseOrder: mergedCommitsInReverseOrderLazy.Value,
                            iteration: childIteration,
                            targetLabel: targetLabel,
                            traversedCommits: traversedCommits,
                            taggedSemanticVersions: taggedSemanticVersions
                        );

                        commit.AddChildIteration(childIteration);
                        if (done) return true;
                    }

                    traversedCommits.AddRange(mergedCommitsInReverseOrderLazy.Value);
                }
            }
        }
        return false;
    }

    private VersionField GetIncrementForcedByCommit(ICommit commit, EffectiveConfiguration configuration)
    {
        commit.NotNull();
        configuration.NotNull();

        return configuration.CommitMessageIncrementing switch
        {
            CommitMessageIncrementMode.Enabled => incrementStrategyFinder.GetIncrementForcedByCommit(commit, configuration),
            CommitMessageIncrementMode.Disabled => VersionField.None,
            CommitMessageIncrementMode.MergeMessageOnly => commit.IsMergeCommit()
                ? incrementStrategyFinder.GetIncrementForcedByCommit(commit, configuration) : VersionField.None,
            _ => throw new InvalidEnumArgumentException(
                nameof(configuration.CommitMessageIncrementing), (int)configuration.CommitMessageIncrementing, typeof(CommitMessageIncrementMode)
            )
        };
    }

    private IReadOnlyDictionary<ICommit, EffectiveBranchConfiguration> GetCommitsWasBranchedFrom(ReferenceName branchName)
    {
        Dictionary<ICommit, EffectiveBranchConfiguration> result = [];

        var branch = repositoryStore.FindBranch(branchName);
        if (branch is null) return result;

        var branchCommits = repositoryStore.FindCommitBranchesWasBranchedFrom(
            branch, Context.Configuration
        ).ToList();

        var branchCommitDictionary = branchCommits.ToDictionary(
            element => element.Branch, element => element.Commit
        );
        foreach (var item in branchCommitDictionary.Keys)
        {
            var branchConfiguration = Context.Configuration.GetBranchConfiguration(item);
            if (branchConfiguration.Increment == IncrementStrategy.Inherit) continue;

            if (result.ContainsKey(branchCommitDictionary[item]))
            {
                if ((branchConfiguration.IsMainBranch ?? Context.Configuration.IsMainBranch) == true
                    && !result[branchCommitDictionary[item]].Value.IsMainBranch)
                {
                    result[branchCommitDictionary[item]]
                        = new(new(Context.Configuration, branchConfiguration), item);
                }
            }
            else
            {
                result.Add(
                    key: branchCommitDictionary[item],
                    value: new(new(Context.Configuration, branchConfiguration), item)
                );
            }
        }
        return result;
    }

    private static BaseVersion DetermineBaseVersion(TrunkBasedIteration iteration, string? targetLabel)
        => DetermineBaseVersionRecursive(iteration, targetLabel);

    internal static BaseVersion DetermineBaseVersionRecursive(TrunkBasedIteration iteration, string? targetLabel)
    {
        iteration.NotNull();

        var incrementSteps = GetIncrements(iteration, targetLabel).ToArray();

        BaseVersion? result = null;
        for (var i = 0; i < incrementSteps.Length; i++)
        {
            if (incrementSteps[i] is BaseVersionOperand baseVersionOperand)
            {
                result = new BaseVersion(baseVersionOperand);
            }
            else if (incrementSteps[i] is BaseVersionOperator baseVersionOperator)
            {
                result ??= new BaseVersion();
                result = result.Apply(baseVersionOperator);
            }
        }
        return result ?? throw new InvalidOperationException();
    }

    private static IEnumerable<IBaseVersionIncrement> GetIncrements(TrunkBasedIteration iteration, string? targetLabel)
    {
        TrunkBasedContext context = new()
        {
            TargetLabel = targetLabel,
        };

        foreach (var commit in iteration.Commits)
        {
            foreach (var item in TrunkContextPreEnricherCollection)
            {
                item.Enrich(iteration, commit, context);
            }

            foreach (var incrementer in TrunkIncrementerCollection
                .Where(element => element.MatchPrecondition(iteration, commit, context)))
            {
                foreach (var item in incrementer.GetIncrements(iteration, commit, context))
                {
                    yield return item;
                }
            }

            foreach (var item in TrunkContextPostEnricherCollection)
            {
                item.Enrich(iteration, commit, context);
            }
        }
    }
}
