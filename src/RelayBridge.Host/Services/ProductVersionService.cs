// SPDX-License-Identifier: MPL-2.0

using System.Reflection;
using RelayBridge.Core.Release;

namespace RelayBridge.Host.Services;

public sealed class ProductVersionService
{
    public ProductVersionService()
        : this(typeof(ProductVersionService).Assembly)
    {
    }

    internal ProductVersionService(Assembly assembly)
    {
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!ProductSemanticVersion.TryParse(informationalVersion, out var currentVersion))
        {
            throw new InvalidOperationException("The compiled RelayBridge product version is invalid.");
        }

        CurrentVersion = currentVersion;
    }

    public ProductSemanticVersion CurrentVersion { get; }

    public ReleaseChannel CurrentChannel => CurrentVersion.DefaultChannel;
}
