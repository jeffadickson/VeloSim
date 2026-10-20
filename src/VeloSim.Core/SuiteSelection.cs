using System.Buffers.Binary;
using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;

namespace VeloSim;

public sealed record SuiteSelection(int OriginalCaseCount, IReadOnlyList<string> Include, IReadOnlyList<string> Exclude, int ShardIndex, int ShardCount);
public static class SuiteSelector
{
    public static int AssignShard(string name, int shardCount)
    {
        if (shardCount is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(shardCount));
        return (int)(BinaryPrimitives.ReadUInt32BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(name.ToLowerInvariant()))) % (uint)shardCount);
    }
    public static RegressionSuiteManifest Select(RegressionSuiteManifest suite, IReadOnlyList<string>? include = null, IReadOnlyList<string>? exclude = null, int shardIndex = 0, int shardCount = 1)
    {
        include ??= ["*"]; exclude ??= [];
        if (include.Count == 0 || include.Concat(exclude).Any(p => string.IsNullOrWhiteSpace(p) || p.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '*' or '?')))) throw new ArgumentException("Invalid suite filter.");
        if (shardCount is < 1 or > 256 || shardIndex < 0 || shardIndex >= shardCount) throw new ArgumentException("Invalid shard index/count.");
        var filtered = suite.Cases.Where(c => include.Any(p => FileSystemName.MatchesSimpleExpression(p, c.Name, true)) && !exclude.Any(p => FileSystemName.MatchesSimpleExpression(p, c.Name, true))).ToArray();
        if (filtered.Length == 0) throw new ArgumentException("Suite filters selected no cases.");
        var cases = filtered.Where(c => AssignShard(c.Name, shardCount) == shardIndex).ToArray();
        if (cases.Length == 0) throw new ArgumentException("No cases assigned to requested shard.");
        return suite with { Cases = Array.AsReadOnly(cases), Selection = new(suite.Cases.Count, include.ToArray(), exclude.ToArray(), shardIndex, shardCount) };
    }
}
