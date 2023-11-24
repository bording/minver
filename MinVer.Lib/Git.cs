using System.Diagnostics.CodeAnalysis;
using System.IO;
using LibGit2Sharp;

namespace MinVer.Lib;

internal static class Git
{
    public static bool TryGetRepository(string directory, [NotNullWhen(returnValue: true)] out Repository? repository)
    {
        var valid = false;
        repository = null;

        while (!valid && directory is not null)
        {
            var repoPath = Path.Combine(directory, ".git");
            valid = Repository.IsValid(repoPath);

            if (valid)
            {
                repository = new Repository(repoPath);
                return true;
            }
            else
            {
                directory = Path.GetDirectoryName(directory);
            }
        }

        return false;
    }
}
