using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LibGit2Sharp;

namespace MinVer.Lib;

public static class Versioner
{
    public static Version GetVersion(string workDir, string tagPrefix, MajorMinor minMajorMinor, string buildMeta, VersionPart autoIncrement, IEnumerable<string> defaultPreReleaseIdentifiers, bool ignoreHeight, ILogger log)
    {
        log = log ?? throw new ArgumentNullException(nameof(log));

        var defaultPreReleaseIdentifiersList = defaultPreReleaseIdentifiers.ToList();

        var (version, height) = GetVersion(workDir, tagPrefix, defaultPreReleaseIdentifiersList, log);

        _ = height.HasValue && ignoreHeight && log.IsDebugEnabled && log.Debug("Ignoring height.");
        version = !height.HasValue || ignoreHeight ? version : version.WithHeight(height.Value, autoIncrement, defaultPreReleaseIdentifiersList);

        version = version.AddBuildMetadata(buildMeta);

        var calculatedVersion = version.Satisfying(minMajorMinor, defaultPreReleaseIdentifiersList);

        _ = calculatedVersion != version
            ? log.IsInfoEnabled && log.Info($"Bumping version to {calculatedVersion} to satisfy minimum major minor {minMajorMinor}.")
            : log.IsDebugEnabled && log.Debug($"The calculated version {calculatedVersion} satisfies the minimum major minor {minMajorMinor}.");

        _ = log.IsInfoEnabled && log.Info($"Calculated version {calculatedVersion}.");

        return calculatedVersion;
    }

    private static (Version Version, int? Height) GetVersion(string workDir, string tagPrefix, List<string> defaultPreReleaseIdentifiers, ILogger log)
    {
        if (!Git.TryGetRepository(workDir, out var repository))
        {
            var version = new Version(defaultPreReleaseIdentifiers);

            _ = log.IsWarnEnabled && log.Warn(1001, $"'{workDir}' is not a valid Git working directory. Using default version {version}.");

            return (version, default);
        }

        if (repository.Info.IsHeadUnborn)
        {
            var version = new Version(defaultPreReleaseIdentifiers);

            _ = log.IsInfoEnabled && log.Info($"No commits found. Using default version {version}.");

            return (version, default);
        }

        var tags = repository.Tags;

        var orderedCandidates = GetCandidates(repository.Head.Commits.First(), tags, tagPrefix, defaultPreReleaseIdentifiers, log)
            .OrderBy(candidate => candidate.Version)
            .ThenByDescending(candidate => candidate.Index).ToList();

        repository.Dispose();

        var tagWidth = log.IsDebugEnabled ? orderedCandidates.Max(candidate => candidate.Tag.Length) : 0;
        var versionWidth = log.IsDebugEnabled ? orderedCandidates.Max(candidate => candidate.Version.ToString().Length) : 0;
        var heightWidth = log.IsDebugEnabled ? orderedCandidates.Max(candidate => candidate.Height).ToString(CultureInfo.CurrentCulture).Length : 0;

        if (log.IsDebugEnabled)
        {
            foreach (var candidate in orderedCandidates.Take(orderedCandidates.Count - 1))
            {
                _ = log.Debug($"Ignoring {candidate.ToString(tagWidth, versionWidth, heightWidth)}.");
            }
        }

        var selectedCandidate = orderedCandidates.Last();

        _ = string.IsNullOrEmpty(selectedCandidate.Tag) && log.IsInfoEnabled && log.Info($"No commit found with a valid SemVer 2.0 version{(string.IsNullOrEmpty(tagPrefix) ? "" : $" prefixed with '{tagPrefix}'")}. Using default version {selectedCandidate.Version}.");
        _ = log.IsInfoEnabled && log.Info($"Using{(log.IsDebugEnabled && orderedCandidates.Count > 1 ? "    " : " ")}{selectedCandidate.ToString(tagWidth, versionWidth, heightWidth)}.");

        return (selectedCandidate.Version, selectedCandidate.Height);
    }

    private static List<Candidate> GetCandidates(Commit head, TagCollection tags, string tagPrefix, List<string> defaultPreReleaseIdentifiers, ILogger log)
    {
        var tagsAndVersions = new List<(string Name, string Sha, Version Version)>();

        foreach (var tag in tags)
        {
            if (Version.TryParse(tag.FriendlyName, out var version, tagPrefix))
            {
                tagsAndVersions.Add((tag.FriendlyName, tag.PeeledTarget.Sha, version));
            }
            else
            {
                _ = log.IsDebugEnabled && log.Debug($"Ignoring non-version tag {{ Name: {tag.FriendlyName}, Sha: {tag.PeeledTarget.Sha} }}.");
            }
        }

        tagsAndVersions =
        [
            .. tagsAndVersions
                        .OrderBy(tagAndVersion => tagAndVersion.Version)
                        .ThenBy(tagsAndVersion => tagsAndVersion.Name)
,
        ];

        var itemsToCheck = new Stack<(Commit Commit, int Height, Commit? Child)>();
        itemsToCheck.Push((head, 0, null));

        var checkedShas = new HashSet<string>();
        var candidates = new List<Candidate>();

        while (itemsToCheck.TryPop(out var item))
        {
            _ = item.Child != null && log.IsTraceEnabled && log.Trace($"Checking parents of commit {item.Child}...");
            _ = log.IsTraceEnabled && log.Trace($"Checking commit {item.Commit} (height {item.Height})...");

            if (!checkedShas.Add(item.Commit.Sha))
            {
                _ = log.IsTraceEnabled && log.Trace($"Commit {item.Commit} already checked. Abandoning path.");
                continue;
            }

            var commitTagsAndVersions = tagsAndVersions.Where(tagAndVersion => tagAndVersion.Sha == item.Commit.Sha).ToList();

            if (commitTagsAndVersions.Any())
            {
                foreach (var (name, _, version) in commitTagsAndVersions)
                {
                    var candidate = new Candidate(item.Commit, item.Height, name, version, candidates.Count);
                    _ = log.IsTraceEnabled && log.Trace($"Found version tag {candidate}.");
                    candidates.Add(candidate);
                }

                continue;
            }

            _ = log.IsTraceEnabled && log.Trace($"Found no version tags on commit {item.Commit}.");

            if (!item.Commit.Parents.Any())
            {
                candidates.Add(new Candidate(item.Commit, item.Height, "", new Version(defaultPreReleaseIdentifiers), candidates.Count));
                _ = log.IsTraceEnabled && log.Trace($"Found root commit {candidates.Last()}.");
                continue;
            }

            if (log.IsTraceEnabled)
            {
                _ = log.Trace($"Commit {item.Commit} has {item.Commit.Parents.Count()} parent(s):");
                foreach (var parent in item.Commit.Parents)
                {
                    _ = log.Trace($"- {parent}");
                }
            }

            foreach (var parent in item.Commit.Parents.Reverse())
            {
                itemsToCheck.Push((parent, item.Height + 1, item.Commit));
            }
        }

        _ = log.IsDebugEnabled && log.Debug($"{checkedShas.Count:N0} commits checked.");
        return candidates;
    }

    private sealed class Candidate(Commit commit, int height, string tag, Version version, int index)
    {
        public Commit Commit { get; } = commit;

        public int Height { get; } = height;

        public string Tag { get; } = tag;

        public Version Version { get; } = version;

        public int Index { get; } = index;

        public override string ToString() => this.ToString(0, 0, 0);

        public string ToString(int tagWidth, int versionWidth, int heightWidth) =>
            $"{{ {nameof(this.Commit)}: {this.Commit.Sha[..Math.Min(7, this.Commit.Sha.Length)]}, {nameof(this.Tag)}: {$"'{this.Tag}',".PadRight(tagWidth + 3)} {nameof(this.Version)}: {$"{this.Version},".PadRight(versionWidth + 1)} {nameof(this.Height)}: {this.Height.ToString(CultureInfo.CurrentCulture).PadLeft(heightWidth)} }}";
    }
}
