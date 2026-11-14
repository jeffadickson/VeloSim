using NUnit.Framework;

namespace VeloSim.Tests;

public sealed class TestRepositoryTests
{
    [Test]
    public void RelocatedBinariesUseSourceFixturesInsteadOfTheirArtifactDirectory()
    {
        Assert.That(File.Exists(Path.Combine(TestRepository.Root, "examples", "counter.normalized.json")), Is.True);
    }

    [TestCase("relative")]
    [TestCase("Z:/missing-velosim-checkout")]
    public void InvalidExplicitCheckoutDoesNotFallBackSilently(string path) =>
        Assert.Throws<IOException>(() => TestRepository.Resolve(path, TestRepository.Root));
}
