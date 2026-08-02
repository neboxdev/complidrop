using CompliDrop.Api.Tests.TestHelpers;
using FluentAssertions;

namespace CompliDrop.Api.Tests;

/// <summary>
/// Hermetic self-tests for <see cref="SourceScan"/>, the plumbing every source-scanning gate now shares.
/// An enforcement gate is only as good as its stripper and its locator: a stripper that dropped everything
/// would make every <c>NotContain</c> assertion pass on any source at all, and a locator that silently
/// found the wrong tree would scan nothing. These used to live inside one gate and cover only it.
/// </summary>
public class SourceScanTests
{
    [Fact]
    public void The_comment_stripper_removes_prose_without_touching_code()
    {
        SourceScan.StripLineComments("    // SetProperty(d => d.ExtractionTrust, x)\n    var a = 1;")
            .Should().NotContain("ExtractionTrust").And.Contain("var a = 1;");
        SourceScan.StripLineComments("    /// <summary>SaveChangesAsync()</summary>\n    var b = 2;")
            .Should().NotContain("SaveChangesAsync").And.Contain("var b = 2;");
        SourceScan.StripLineComments("    .SetProperty(d => d.ExtractionTrust, x) // why\n")
            .Should().Contain("SetProperty(d => d.ExtractionTrust",
                "a trailing comment must not smuggle the statement past a gate");
    }

    [Fact]
    public void The_locator_finds_the_real_production_tree_and_fails_closed_otherwise()
    {
        var root = SourceScan.ProductionRoot();
        root.Name.Should().Be("CompliDrop.Api");
        File.Exists(SourceScan.ProductionFile("Endpoints", "DocumentEndpoints.cs")).Should().BeTrue();

        var missing = () => SourceScan.ProductionFile("Endpoints", "NoSuchEndpoints.cs");
        missing.Should().Throw<FileNotFoundException>(
            "a renamed or moved file must fail the gate loudly, not leave it reading nothing");
    }
}
