// SPDX-License-Identifier: MPL-2.0

using System.Reflection;
using Xunit;

namespace RelayBridge.Tests;

public sealed class CoreArchitectureTests
{
    [Fact]
    public void Core_does_not_reference_outer_application_layers()
    {
        var coreAssembly = Assembly.Load("RelayBridge.Core");
        var references = coreAssembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("RelayBridge.Infrastructure", references);
        Assert.DoesNotContain("RelayBridge.Host", references);
        Assert.DoesNotContain("Microsoft.AspNetCore", references);
        Assert.DoesNotContain("Microsoft.EntityFrameworkCore", references);
    }
}
